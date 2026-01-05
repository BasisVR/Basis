using System.Runtime.CompilerServices;
using Unity.Mathematics;
using static BasisNetworkPrimitiveCompression;
namespace Basis.Scripts.Networking.Compression
{
    public static class BasisUnityBitPackerExtensionsUnsafe
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteUShort(ushort value, ref byte[] bytes, ref int offset)
        {
            bytes[offset++] = (byte)value;
            bytes[offset++] = (byte)(value >> 8);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort ReadUShort(ref byte[] bytes, ref int offset)
        {
            ushort result = (ushort)(bytes[offset] | (bytes[offset + 1] << 8));
            offset += 2;
            return result;
        }
        const int BITS = 22;
        const uint MAX = (1u << BITS) - 1;
        const float INV_SQRT2 = 0.7071067811865475244f; // 1/sqrt(2)

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static uint Quantize(float v)
        {
            // map [-INV_SQRT2, +INV_SQRT2] -> [0, MAX]
            float t = (v + INV_SQRT2) / (2f * INV_SQRT2);
            t = math.clamp(t, 0f, 1f);
            return (uint)math.round(t * MAX);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float Dequantize(uint q)
        {
            float t = (float)q / MAX;
            return t * (2f * INV_SQRT2) - INV_SQRT2;
        }

        public static void WriteQuaternionToBytes(quaternion q, ref byte[] bytes, ref int offset)
        {
            float4 v = q.value;
            if (!math.all(math.isfinite(v)))
                v = new float4(0, 0, 0, 1);

            // index of largest abs component
            float4 av = math.abs(v);
            int largest = 0;
            float m = av.x;
            if (av.y > m) { m = av.y; largest = 1; }
            if (av.z > m) { m = av.z; largest = 2; }
            if (av.w > m) { largest = 3; }

            // force largest positive
            if (v[largest] < 0f) v = -v;

            float a, b, c;
            switch (largest)
            {
                case 0: a = v.y; b = v.z; c = v.w; break;
                case 1: a = v.x; b = v.z; c = v.w; break;
                case 2: a = v.x; b = v.y; c = v.w; break;
                default: a = v.x; b = v.y; c = v.z; break;
            }

            ulong qa = Quantize(a);
            ulong qb = Quantize(b);
            ulong qc = Quantize(c);

            // Layout in 128 bits:
            // low  = [largest:2][qa:22][qb:22][qc_low:18]  -> 64 bits
            // high = [qc_high:4]                           -> remaining bits (only 4 used)
            // Total used = 2 + 22 + 22 + 22 = 68 bits
            ulong low =
                ((ulong)largest) |
                (qa << 2) |
                (qb << (2 + BITS)) |
                ((qc & ((1UL << 18) - 1UL)) << (2 + 2 * BITS));

            ulong high = qc >> 18; // top 4 bits (since 22-18 = 4)

            WriteULongLE(low, ref bytes, ref offset);
            WriteULongLE(high, ref bytes, ref offset);
        }

        public static quaternion ReadQuaternionFromBytes(ref byte[] bytes, ref int offset)
        {
            ulong low = ReadULongLE(ref bytes, ref offset);
            ulong high = ReadULongLE(ref bytes, ref offset);

            int largest = (int)(low & 0x3UL);

            ulong qa = (low >> 2) & MAX;
            ulong qb = (low >> (2 + BITS)) & MAX;

            // qc is split: 18 bits in low, 4 bits in high
            ulong qcLow = (low >> (2 + 2 * BITS)) & ((1UL << 18) - 1UL);
            ulong qc = qcLow | (high << 18);

            float a = Dequantize((uint)qa);
            float b = Dequantize((uint)qb);
            float c = Dequantize((uint)qc);

            float sum = a * a + b * b + c * c;
            sum = math.min(sum, 1f);
            float missing = math.sqrt(1f - sum);

            float4 v;
            switch (largest)
            {
                case 0: v = new float4(missing, a, b, c); break;
                case 1: v = new float4(a, missing, b, c); break;
                case 2: v = new float4(a, b, missing, c); break;
                default: v = new float4(a, b, c, missing); break;
            }

            v = math.normalize(v);
            return new quaternion(v);
        }
        public unsafe static void WriteQuaternionToBytes(quaternion q, ref byte[] bytes, ref int offset, BasisRangedUshortFloatData compressor)
        {
            fixed (byte* ptr = &bytes[offset])
            {
                *((float*)ptr) = float.IsNaN(q.value.x) ? 0f : q.value.x;
                *((float*)(ptr + 4)) = float.IsNaN(q.value.y) ? 0f : q.value.y;
                *((float*)(ptr + 8)) = float.IsNaN(q.value.z) ? 0f : q.value.z;
            }
            offset += 12;

            float w = float.IsNaN(q.value.w) ? 1f : q.value.w;
            ushort compressedW = compressor.Compress(w);
            WriteUShort(compressedW, ref bytes, ref offset);
        }

        public unsafe static quaternion ReadQuaternionFromBytes(ref byte[] bytes, BasisRangedUshortFloatData compressor, ref int offset)
        {
            float x, y, z;
            fixed (byte* ptr = &bytes[offset])
            {
                x = *((float*)ptr);
                y = *((float*)(ptr + 4));
                z = *((float*)(ptr + 8));
            }
            offset += 12;

            ushort compressedW = ReadUShort(ref bytes, ref offset);
            float w = compressor.Decompress(compressedW);

            // Sanitize potential NaNs
            if (float.IsNaN(x)) x = 0f;
            if (float.IsNaN(y)) y = 0f;
            if (float.IsNaN(z)) z = 0f;
            if (float.IsNaN(w)) w = 1f;

            return new quaternion(x, y, z, w);
        }
        static void WriteULongLE(ulong v, ref byte[] bytes, ref int offset)
        {
            // little-endian
            bytes[offset++] = (byte)(v);
            bytes[offset++] = (byte)(v >> 8);
            bytes[offset++] = (byte)(v >> 16);
            bytes[offset++] = (byte)(v >> 24);
            bytes[offset++] = (byte)(v >> 32);
            bytes[offset++] = (byte)(v >> 40);
            bytes[offset++] = (byte)(v >> 48);
            bytes[offset++] = (byte)(v >> 56);
        }

        static ulong ReadULongLE(ref byte[] bytes, ref int offset)
        {
            ulong v =
                (ulong)bytes[offset] |
                ((ulong)bytes[offset + 1] << 8) |
                ((ulong)bytes[offset + 2] << 16) |
                ((ulong)bytes[offset + 3] << 24) |
                ((ulong)bytes[offset + 4] << 32) |
                ((ulong)bytes[offset + 5] << 40) |
                ((ulong)bytes[offset + 6] << 48) |
                ((ulong)bytes[offset + 7] << 56);

            offset += 8;
            return v;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WritePosition(UnityEngine.Vector3 position, ref byte[] buffer, ref int offset)
        {
            unsafe
            {
                fixed (byte* dst = &buffer[offset])
                {
                    float* fDst = (float*)dst;
                    fDst[0] = position.x;
                    fDst[1] = position.y;
                    fDst[2] = position.z;
                }
            }

            offset += 12;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static UnityEngine.Vector3 ReadPosition(ref byte[] buffer, ref int offset)
        {
            UnityEngine.Vector3 result;
            unsafe
            {
                fixed (byte* src = &buffer[offset])
                {
                    float* fSrc = (float*)src;
                    result.x = float.IsNaN(fSrc[0]) ? 0f : fSrc[0];
                    result.y = float.IsNaN(fSrc[1]) ? 0f : fSrc[1];
                    result.z = float.IsNaN(fSrc[2]) ? 0f : fSrc[2];
                }
            }

            offset += 12;
            return result;
        }
    }
}
