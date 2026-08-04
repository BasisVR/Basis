using Basis.Scripts.Networking.Compression;
using Unity.Mathematics;

namespace Basis.Scripts.Networking.Sync
{
    /// <summary>
    /// Wire (de)serialization for a <see cref="BasisSyncSchema"/>. Supports full keyframes (every
    /// field) and dirty-mask deltas (changed fields only). The header and dirty mask stay byte-aligned;
    /// field payloads are packed into a contiguous bitstream so continuous components can use any bit
    /// width (Raw 32-bit float, Half 16-bit, or N-bit Ranged within [min,max]) and bools cost one bit.
    /// Rotations use Basis smallest-three quaternion packing; discrete ints use zig-zag varints.
    /// </summary>
    public static class BasisSyncCodec
    {
        public const byte FlagKeyframe = 1 << 0;
        public const int HeaderSize = 4; // seq(1) + flags(1) + intervalMs(2)
        public const int ChecksumSize = 2; // optional Fletcher-16 integrity trailer (see BasisSyncedObject.UseChecksum)

        public static int MaxSerializedSize(BasisSyncSchema schema)
        {
            int bits = 0;
            for (int i = 0; i < schema.FieldCount; i++) bits += MaxFieldBits(schema, schema.GetField(i));
            int payloadBytes = (bits + 7) >> 3;
            // +1 slack for the partial-byte tail; +ChecksumSize so the buffer always fits the optional trailer.
            return HeaderSize + schema.DirtyMaskBytes + payloadBytes + 1 + ChecksumSize;
        }

        /// <summary>
        /// Actual on-wire byte breakdown for a payload of <paramref name="payloadBits"/> bits across
        /// <paramref name="fieldCount"/> fields. A keyframe = header + payload (+ optional checksum); a delta
        /// adds the dirty mask. The bit-packed payload rounds up to whole bytes exactly once.
        /// </summary>
        public static void WireBytes(int payloadBits, int fieldCount, bool checksum, out int payloadBytes, out int maskBytes, out int keyframeBytes, out int deltaBytes)
        {
            payloadBytes = (payloadBits + 7) >> 3;
            maskBytes = (fieldCount + 7) >> 3;
            int extra = checksum ? ChecksumSize : 0;
            keyframeBytes = HeaderSize + payloadBytes + extra;
            deltaBytes = HeaderSize + maskBytes + payloadBytes + extra;
        }

        private static int MaxFieldBits(BasisSyncSchema schema, in BasisSyncField f)
        {
            switch (f.Pool)
            {
                case BasisSyncPool.Continuous:
                {
                    int bits = 0;
                    for (int c = 0; c < f.ContComponents; c++) bits += ContBits(schema, f.Offset + c);
                    return bits;
                }
                case BasisSyncPool.Rotation:
                    return 2 + 3 * (1 + RotMagBits(f));
                default:
                    switch (f.Type)
                    {
                        case BasisSyncFieldType.Bool: return 1;
                        case BasisSyncFieldType.Byte: return 8;
                        case BasisSyncFieldType.UShort: return 16;
                        default: return 40; // Int/UInt zig-zag varint, up to 5 bytes
                    }
            }
        }

        private static int ContBits(BasisSyncSchema schema, int ci)
        {
            switch (schema.QuantMode(ci))
            {
                case BasisQuantMode.Half: return 16;
                case BasisQuantMode.Ranged: return ClampBits(schema.QuantBits(ci));
                default: return 32;
            }
        }

        private static int ClampBits(int b) => b < 1 ? 1 : (b > 31 ? 31 : b);

        private static int RotMagBits(in BasisSyncField f) => math.clamp(f.RotBits <= 0 ? 9 : f.RotBits, 2, 18);

        public static int Serialize(BasisSyncSchema schema, BasisSyncValues values, bool keyframe, byte[] dirtyMask, byte seq, ushort intervalMs, byte[] outBuf)
            => Serialize(schema, values, keyframe, dirtyMask, seq, intervalMs, outBuf, false);

        /// <summary>
        /// As Serialize, optionally appending a 2-byte Fletcher-16 integrity trailer. The receiver checks it
        /// (<see cref="VerifyChecksum"/>) and drops the packet on mismatch, so a corrupted unreliable packet can
        /// neither apply a garbage value nor poison the sequence high-water-mark.
        /// </summary>
        public static int Serialize(BasisSyncSchema schema, BasisSyncValues values, bool keyframe, byte[] dirtyMask, byte seq, ushort intervalMs, byte[] outBuf, bool appendChecksum)
        {
            int o = 0;
            outBuf[o++] = seq;
            outBuf[o++] = keyframe ? FlagKeyframe : (byte)0;
            outBuf[o++] = (byte)intervalMs;
            outBuf[o++] = (byte)(intervalMs >> 8);

            if (!keyframe)
            {
                for (int i = 0; i < schema.DirtyMaskBytes; i++) outBuf[o++] = dirtyMask[i];
            }

            var w = new BitWriter(outBuf, o);
            int n = schema.FieldCount;
            for (int fi = 0; fi < n; fi++)
            {
                if (!keyframe && (dirtyMask[fi >> 3] & (1 << (fi & 7))) == 0) continue;
                EncodeField(schema, schema.GetField(fi), values, ref w);
            }

            int len = w.ByteLength;
            if (appendChecksum && len + ChecksumSize <= outBuf.Length)
            {
                ushort ck = Fletcher16(outBuf, len);
                outBuf[len] = (byte)ck;
                outBuf[len + 1] = (byte)(ck >> 8);
                len += ChecksumSize;
            }
            return len;
        }

        /// <summary>True if the trailing 2-byte Fletcher-16 matches the packet body. Call before trusting a packet's sequence/values.</summary>
        public static bool VerifyChecksum(byte[] buf, int length)
        {
            if (buf == null || length < HeaderSize + ChecksumSize || length > buf.Length) return false;
            int payloadLen = length - ChecksumSize;
            ushort expected = Fletcher16(buf, payloadLen);
            ushort actual = (ushort)(buf[payloadLen] | (buf[payloadLen + 1] << 8));
            return expected == actual;
        }

        private static ushort Fletcher16(byte[] data, int len)
        {
            int s1 = 0, s2 = 0;
            for (int i = 0; i < len; i++)
            {
                s1 = (s1 + data[i]) % 255;
                s2 = (s2 + s1) % 255;
            }
            return (ushort)((s2 << 8) | s1);
        }

        public static bool Deserialize(BasisSyncSchema schema, byte[] buf, int length, BasisSyncValues baseline, out byte seq, out bool keyframe, out ushort intervalMs)
        {
            seq = 0;
            keyframe = false;
            intervalMs = 0;
            if (buf == null || length < HeaderSize || length > buf.Length) return false;

            int o = 0;
            seq = buf[o++];
            byte flags = buf[o++];
            keyframe = (flags & FlagKeyframe) != 0;
            intervalMs = (ushort)(buf[o] | (buf[o + 1] << 8));
            o += 2;
            int fieldCount = schema.FieldCount;

            if (keyframe)
            {
                // Preflight the packet so rejected/truncated data never partially mutates the baseline.
                var verify = new BitReader(buf, o, length);
                for (int fi = 0; fi < fieldCount; fi++)
                {
                    if (!SkipField(schema, schema.GetField(fi), ref verify)) return false;
                }

                var r = new BitReader(buf, o, length);
                for (int fi = 0; fi < fieldCount; fi++)
                {
                    if (!DecodeField(schema, schema.GetField(fi), baseline, ref r)) return false;
                }
            }
            else
            {
                int maskStart = o;
                o += schema.DirtyMaskBytes;
                if (o > length) return false;

                // Preflight the packet so rejected/truncated data never partially mutates the baseline.
                var verify = new BitReader(buf, o, length);
                for (int fi = 0; fi < fieldCount; fi++)
                {
                    if ((buf[maskStart + (fi >> 3)] & (1 << (fi & 7))) != 0)
                    {
                        if (!SkipField(schema, schema.GetField(fi), ref verify)) return false;
                    }
                }

                var r = new BitReader(buf, o, length);
                for (int fi = 0; fi < fieldCount; fi++)
                {
                    if ((buf[maskStart + (fi >> 3)] & (1 << (fi & 7))) != 0)
                    {
                        if (!DecodeField(schema, schema.GetField(fi), baseline, ref r)) return false;
                    }
                }
            }
            return true;
        }

        private static void EncodeField(BasisSyncSchema schema, in BasisSyncField f, BasisSyncValues v, ref BitWriter w)
        {
            switch (f.Pool)
            {
                case BasisSyncPool.Continuous:
                    for (int c = 0; c < f.ContComponents; c++)
                    {
                        int ci = f.Offset + c;
                        EncodeCont(ref w, v.Cont[ci], schema.QuantMode(ci), schema.QuantMin(ci), schema.QuantMax(ci), schema.QuantBits(ci));
                    }
                    break;
                case BasisSyncPool.Rotation:
                {
                    int magBits = RotMagBits(f);
                    quaternion q = v.Rot[f.Offset];
                    UnityEngine.Quaternion uq = new UnityEngine.Quaternion(q.value.x, q.value.y, q.value.z, q.value.w);
                    w.WriteBits(BasisCompression.QuaternionCompressor.CompressSmallestThree(uq, magBits), 2 + 3 * (1 + magBits));
                    break;
                }
                default:
                    switch (f.Type)
                    {
                        case BasisSyncFieldType.Bool: w.WriteBits(v.Disc[f.Offset] != 0 ? 1u : 0u, 1); break;
                        case BasisSyncFieldType.Byte: w.WriteBits((uint)(v.Disc[f.Offset] & 0xFF), 8); break;
                        case BasisSyncFieldType.UShort: w.WriteBits((uint)(v.Disc[f.Offset] & 0xFFFF), 16); break;
                        case BasisSyncFieldType.UInt: WriteVarUInt(ref w, (uint)v.Disc[f.Offset]); break;
                        default: WriteVarInt(ref w, v.Disc[f.Offset]); break;
                    }
                    break;
            }
        }

        private static bool DecodeField(BasisSyncSchema schema, in BasisSyncField f, BasisSyncValues v, ref BitReader r)
        {
            switch (f.Pool)
            {
                case BasisSyncPool.Continuous:
                    for (int c = 0; c < f.ContComponents; c++)
                    {
                        int ci = f.Offset + c;
                        if (!TryDecodeCont(ref r, schema.QuantMode(ci), schema.QuantMin(ci), schema.QuantMax(ci), schema.QuantBits(ci), out float value)) return false;
                        v.Cont[ci] = value;
                    }
                    break;
                case BasisSyncPool.Rotation:
                {
                    int magBits = RotMagBits(f);
                    if (!r.TryReadBitsLong(2 + 3 * (1 + magBits), out ulong packed)) return false;
                    UnityEngine.Quaternion uq = BasisCompression.QuaternionCompressor.DecompressSmallestThree(packed, magBits);
                    v.Rot[f.Offset] = new quaternion(uq.x, uq.y, uq.z, uq.w);
                    break;
                }
                default:
                    switch (f.Type)
                    {
                        case BasisSyncFieldType.Bool:
                        {
                            if (!r.TryReadBits(1, out uint value)) return false;
                            v.Disc[f.Offset] = value != 0 ? 1 : 0;
                            break;
                        }
                        case BasisSyncFieldType.Byte:
                        {
                            if (!r.TryReadBits(8, out uint value)) return false;
                            v.Disc[f.Offset] = (int)value;
                            break;
                        }
                        case BasisSyncFieldType.UShort:
                        {
                            if (!r.TryReadBits(16, out uint value)) return false;
                            v.Disc[f.Offset] = (int)value;
                            break;
                        }
                        case BasisSyncFieldType.UInt:
                        {
                            if (!TryReadVarUInt(ref r, out uint value)) return false;
                            v.Disc[f.Offset] = (int)value;
                            break;
                        }
                        default:
                        {
                            if (!TryReadVarInt(ref r, out int value)) return false;
                            v.Disc[f.Offset] = value;
                            break;
                        }
                    }
                    break;
            }
            return true;
        }

        private static bool SkipField(BasisSyncSchema schema, in BasisSyncField f, ref BitReader r)
        {
            switch (f.Pool)
            {
                case BasisSyncPool.Continuous:
                    for (int c = 0; c < f.ContComponents; c++)
                    {
                        if (!r.SkipBits(ContBits(schema, f.Offset + c))) return false;
                    }
                    return true;
                case BasisSyncPool.Rotation:
                    return r.SkipBits(2 + 3 * (1 + RotMagBits(f)));
                default:
                    switch (f.Type)
                    {
                        case BasisSyncFieldType.Bool: return r.SkipBits(1);
                        case BasisSyncFieldType.Byte: return r.SkipBits(8);
                        case BasisSyncFieldType.UShort: return r.SkipBits(16);
                        case BasisSyncFieldType.UInt:
                        default:
                            uint ignored;
                            return TryReadVarUInt(ref r, out ignored);
                    }
            }
        }

        private static void EncodeCont(ref BitWriter w, float value, BasisQuantMode mode, float min, float max, int bits)
        {
            switch (mode)
            {
                case BasisQuantMode.Half:
                    w.WriteBits(math.f32tof16(value), 16);
                    break;
                case BasisQuantMode.Ranged:
                {
                    int b = ClampBits(bits);
                    uint maxQ = (1u << b) - 1u;
                    float span = max - min;
                    float t = span > 0f ? (value - min) / span : 0f;
                    t = t < 0f ? 0f : (t > 1f ? 1f : t);
                    w.WriteBits((uint)(t * maxQ + 0.5f), b);
                    break;
                }
                default:
                    w.WriteBits(FloatToBits(value), 32);
                    break;
            }
        }

        /// <summary>Half-extent accepted for a decoded continuous value.</summary>
        private const float MaxDecodedMagnitude = 1e6f;

        private static bool TryDecodeCont(ref BitReader r, BasisQuantMode mode, float min, float max, int bits, out float value)
        {
            value = 0f;
            switch (mode)
            {
                case BasisQuantMode.Half:
                {
                    if (!r.TryReadBits(16, out uint raw)) return false;
                    value = math.f16tof32(raw);
                    return IsUsable(value);
                }
                case BasisQuantMode.Ranged:
                {
                    int b = ClampBits(bits);
                    uint maxQ = (1u << b) - 1u;
                    if (!r.TryReadBits(b, out uint q)) return false;
                    value = min + (q / (float)maxQ) * (max - min);
                    return true;
                }
                default:
                {
                    if (!r.TryReadBits(32, out uint raw)) return false;
                    value = BitsToFloat(raw);
                    return IsUsable(value);
                }
            }
        }

        /// <summary>
        /// The Half and raw-float modes reinterpret wire bits directly, so they can carry NaN or
        /// Infinity into Transform.SetPositionAndRotation, which never recovers.
        /// </summary>
        private static bool IsUsable(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value) &&
            value > -MaxDecodedMagnitude && value < MaxDecodedMagnitude;

        private static void WriteVarInt(ref BitWriter w, int value) => WriteVarUInt(ref w, (uint)((value << 1) ^ (value >> 31)));

        private static bool TryReadVarInt(ref BitReader r, out int value)
        {
            value = 0;
            if (!TryReadVarUInt(ref r, out uint zig)) return false;
            value = (int)(zig >> 1) ^ -(int)(zig & 1);
            return true;
        }

        private static void WriteVarUInt(ref BitWriter w, uint v)
        {
            while (v >= 0x80)
            {
                w.WriteBits((v & 0x7F) | 0x80, 8);
                v >>= 7;
            }
            w.WriteBits(v, 8);
        }

        private static bool TryReadVarUInt(ref BitReader r, out uint value)
        {
            value = 0;
            int shift = 0;
            for (int count = 0; count < 5; count++)
            {
                if (!r.TryReadBits(8, out uint raw)) return false;
                byte b = (byte)raw;
                if (count == 4 && (b & 0xF0) != 0) return false;
                value |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return true;
                shift += 7;
            }
            return false;
        }

        private static unsafe uint FloatToBits(float v) => *(uint*)&v;
        private static unsafe float BitsToFloat(uint u) => *(float*)&u;

        /// <summary>LSB-first bit packer over a byte buffer starting at a byte offset.</summary>
        private struct BitWriter
        {
            private readonly byte[] _buf;
            private int _byte;
            private int _bit;

            public BitWriter(byte[] buf, int startByte)
            {
                _buf = buf;
                _byte = startByte;
                _bit = 0;
                if (_byte < _buf.Length) _buf[_byte] = 0;
            }

            public void WriteBits(uint value, int bits)
            {
                for (int i = 0; i < bits; i++)
                {
                    if (((value >> i) & 1u) != 0 && _byte < _buf.Length) _buf[_byte] |= (byte)(1 << _bit);
                    if (++_bit == 8)
                    {
                        _bit = 0;
                        _byte++;
                        if (_byte < _buf.Length) _buf[_byte] = 0;
                    }
                }
            }

            public void WriteBits(ulong value, int bits)
            {
                if (bits <= 32) { WriteBits((uint)value, bits); return; }
                WriteBits((uint)(value & 0xFFFFFFFFu), 32);
                WriteBits((uint)(value >> 32), bits - 32);
            }

            public int ByteLength => _byte + (_bit > 0 ? 1 : 0);
        }

        /// <summary>LSB-first bit reader with explicit bounds checks for corrupt/truncated packets.</summary>
        private struct BitReader
        {
            private readonly byte[] _buf;
            private readonly int _limit;
            private int _byte;
            private int _bit;

            public BitReader(byte[] buf, int startByte, int limitBytes)
            {
                _buf = buf;
                _byte = startByte;
                _bit = 0;
                _limit = limitBytes < buf.Length ? limitBytes : buf.Length;
            }

            public bool TryReadBits(int bits, out uint value)
            {
                value = 0;
                if (bits > 32 || !CanReadBits(bits)) return false;

                for (int i = 0; i < bits; i++)
                {
                    if (((_buf[_byte] >> _bit) & 1) != 0) value |= 1u << i;
                    //Advance Bit
                    if (++_bit == 8)
                    {
                        _bit = 0;
                        _byte++;
                    }
                }
                return true;
            }

            public bool SkipBits(int bits)
            {
                if (!CanReadBits(bits)) return false;
                int total = _bit + bits;
                _byte += total >> 3;
                _bit = total & 7;
                return true;
            }

            private bool CanReadBits(int bits)
            {
                if (bits < 0 || bits > 64) return false;
                int available = ((_limit - _byte) << 3) - _bit;
                return available >= bits;
            }

            public bool TryReadBitsLong(int bits, out ulong value)
            {
                value = 0;
                if (!CanReadBits(bits)) return false;
                if (bits <= 32)
                {
                    if (!TryReadBits(bits, out uint only)) return false;
                    value = only;
                    return true;
                }
                if (!TryReadBits(32, out uint lo)) return false;
                if (!TryReadBits(bits - 32, out uint hi)) return false;
                value = lo | ((ulong)hi << 32);
                return true;
            }
        }
    }
}
