// BasisVehicleWheelNetCodec.cs
using System;
using UnityEngine;

namespace Basis.Network.Vehicles
{
    /// <summary>
    /// Adds ABSOLUTE wheel angles after the base 22 bytes, plus:
    /// - tick (ushort) : monotonically increasing sender tick
    /// - engineRevs01 (0..1)
    /// - steerRatio (-1..1) for steering wheel visuals
    /// Wheels are bitpacked, minimal size, no deltas.
    /// </summary>
    public static class BasisVehicleWheelNetCodec
    {
        // ---------------- Bit packers ----------------
        private struct BitWriter
        {
            public byte[] Buffer;
            public int BitPos;

            public BitWriter(byte[] buffer, int bitPos)
            {
                Buffer = buffer;
                BitPos = bitPos;
            }

            public void WriteBits(uint value, int bits)
            {
                int bytePos = BitPos >> 3;
                int bitOffset = BitPos & 7;

                ulong cur = 0;
                for (int i = 0; i < 8 && (bytePos + i) < Buffer.Length; i++)
                    cur |= ((ulong)Buffer[bytePos + i]) << (8 * i);

                ulong mask = (bits == 32) ? 0xFFFFFFFFul : ((1ul << bits) - 1ul);
                cur &= ~(mask << bitOffset);
                cur |= ((ulong)value & mask) << bitOffset;

                for (int i = 0; i < 8 && (bytePos + i) < Buffer.Length; i++)
                    Buffer[bytePos + i] = (byte)((cur >> (8 * i)) & 0xFF);

                BitPos += bits;
            }
        }

        private struct BitReader
        {
            public readonly byte[] Buffer;
            public int BitPos;

            public BitReader(byte[] buffer, int bitPos)
            {
                Buffer = buffer;
                BitPos = bitPos;
            }

            public uint ReadBits(int bits)
            {
                int bytePos = BitPos >> 3;
                int bitOffset = BitPos & 7;

                ulong cur = 0;
                for (int i = 0; i < 8 && (bytePos + i) < Buffer.Length; i++)
                    cur |= ((ulong)Buffer[bytePos + i]) << (8 * i);

                ulong mask = (bits == 32) ? 0xFFFFFFFFul : ((1ul << bits) - 1ul);
                uint v = (uint)((cur >> bitOffset) & mask);

                BitPos += bits;
                return v;
            }
        }

        // ---------------- Size math ----------------
        public static int ExtraBytes(int wheelCount, int steerCount, int spinBits, int steerBits, int engineBits, int steerRatioBits)
        {
            int spinTotalBits = Mathf.Max(0, wheelCount) * Mathf.Clamp(spinBits, 1, 16);
            int steerTotalBits = Mathf.Max(0, steerCount) * Mathf.Clamp(steerBits, 1, 16);

            int extraBits =
                Mathf.Clamp(engineBits, 1, 16) +
                Mathf.Clamp(steerRatioBits, 1, 16);

            int bitBytes = ((spinTotalBits + steerTotalBits + extraBits) + 7) >> 3;
            return bitBytes;
        }

        // ---------------- Quantization ----------------
        private static uint QuantizeAngle360(float deg, int bits)
        {
            deg %= 360f;
            if (deg < 0f) deg += 360f;

            uint max = (uint)((1 << bits) - 1);
            return (uint)Mathf.RoundToInt(deg * max / 360f) & max;
        }

        private static float DequantizeAngle360(uint q, int bits)
        {
            uint max = (uint)((1 << bits) - 1);
            return (q * 360f) / max;
        }

        private static uint QuantizeSignedRange(float v, float min, float max, int bits)
        {
            v = Mathf.Clamp(v, min, max);
            uint steps = (uint)((1 << bits) - 1);
            float t = (v - min) / (max - min);
            return (uint)Mathf.RoundToInt(t * steps);
        }

        private static float DequantizeSignedRange(uint q, float min, float max, int bits)
        {
            uint steps = (uint)((1 << bits) - 1);
            float t = q / (float)steps;
            return Mathf.Lerp(min, max, t);
        }

        private static uint Quantize01(float v, int bits)
        {
            v = Mathf.Clamp01(v);
            uint steps = (uint)((1 << bits) - 1);
            return (uint)Mathf.RoundToInt(v * steps);
        }

        private static float Dequantize01(uint q, int bits)
        {
            uint steps = (uint)((1 << bits) - 1);
            return q / (float)steps;
        }

        private static uint QuantizeSigned01(float v, int bits)
        {
            v = Mathf.Clamp(v, -1f, 1f);
            uint steps = (uint)((1 << bits) - 1);
            float t = (v + 1f) * 0.5f;
            return (uint)Mathf.RoundToInt(t * steps);
        }

        private static float DequantizeSigned01(uint q, int bits)
        {
            uint steps = (uint)((1 << bits) - 1);
            float t = q / (float)steps;
            return t * 2f - 1f;
        }

        // ---------------- Public API ----------------
        public static byte[] WritePacketWithWheels(
            Vector3 pos, Quaternion rot, Vector3 scale,
            float[] wheelSpinDeg, float[] steerDeg,
            float engineRevs01, float steerRatio,
            int spinBits, int steerBits,
            int engineBits, int steerRatioBits,
            float steerMin, float steerMax
        )
        {
            int wheelCount = wheelSpinDeg != null ? wheelSpinDeg.Length : 0;
            int steerCount = steerDeg != null ? steerDeg.Length : 0;

            spinBits = Mathf.Clamp(spinBits, 8, 12);
            steerBits = Mathf.Clamp(steerBits, 7, 11);
            engineBits = Mathf.Clamp(engineBits, 6, 10);
            steerRatioBits = Mathf.Clamp(steerRatioBits, 7, 11);

            int extra = ExtraBytes(wheelCount, steerCount, spinBits, steerBits, engineBits, steerRatioBits);
            byte[] buffer = new byte[BasisVehicleNetCodec.MinPacketSize + extra];

            int o = 0;

            rot = NormalizeSafe(rot);
            uint packedQuat = BasisVehicleNetCodec.PackQuaternionSmallestThree32(rot);

            ushort sx = BasisVehicleNetCodec.FloatToHalf(scale.x);
            ushort sy = BasisVehicleNetCodec.FloatToHalf(scale.y);
            ushort sz = BasisVehicleNetCodec.FloatToHalf(scale.z);

            WriteF32(buffer, ref o, pos.x);
            WriteF32(buffer, ref o, pos.y);
            WriteF32(buffer, ref o, pos.z);
            WriteU32(buffer, ref o, packedQuat);
            WriteU16(buffer, ref o, sx);
            WriteU16(buffer, ref o, sy);
            WriteU16(buffer, ref o, sz);

            int bitBase = o * 8;
            var bw = new BitWriter(buffer, bitBase);

            for (int i = 0; i < wheelCount; i++)
                bw.WriteBits(QuantizeAngle360(wheelSpinDeg[i], spinBits), spinBits);

            for (int i = 0; i < steerCount; i++)
                bw.WriteBits(QuantizeSignedRange(steerDeg[i], steerMin, steerMax, steerBits), steerBits);

            bw.WriteBits(Quantize01(engineRevs01, engineBits), engineBits);
            bw.WriteBits(QuantizeSigned01(steerRatio, steerRatioBits), steerRatioBits);

            return buffer;
        }

        public static void ReadPacketWithWheels(
            byte[] buffer,
            int wheelCount, int steerCount,
            int spinBits, int steerBits,
            int engineBits, int steerRatioBits,
            float steerMin, float steerMax,
            out Vector3 pos, out Quaternion rot, out Vector3 scale,
            out float[] wheelSpinDeg, out float[] steerDeg,
            out float engineRevs01, out float steerRatio
        )
        {
            spinBits = Mathf.Clamp(spinBits, 8, 12);
            steerBits = Mathf.Clamp(steerBits, 7, 11);
            engineBits = Mathf.Clamp(engineBits, 6, 10);
            steerRatioBits = Mathf.Clamp(steerRatioBits, 7, 11);

            int expectedMin = BasisVehicleNetCodec.MinPacketSize + ExtraBytes(wheelCount, steerCount, spinBits, steerBits, engineBits, steerRatioBits);

            if (buffer == null || buffer.Length < expectedMin)
            {
                pos = default;
                rot = Quaternion.identity;
                scale = Vector3.one;
                wheelSpinDeg = new float[Mathf.Max(0, wheelCount)];
                steerDeg = new float[Mathf.Max(0, steerCount)];
                engineRevs01 = 0f;
                steerRatio = 0f;
                return;
            }

            int o = 0;

            pos = new Vector3(ReadF32(buffer, ref o), ReadF32(buffer, ref o), ReadF32(buffer, ref o));
            rot = BasisVehicleNetCodec.UnpackQuaternionSmallestThree32(ReadU32(buffer, ref o));
            scale = new Vector3(
                BasisVehicleNetCodec.HalfToFloat(ReadU16(buffer, ref o)),
                BasisVehicleNetCodec.HalfToFloat(ReadU16(buffer, ref o)),
                BasisVehicleNetCodec.HalfToFloat(ReadU16(buffer, ref o))
            );

            wheelSpinDeg = new float[Mathf.Max(0, wheelCount)];
            steerDeg = new float[Mathf.Max(0, steerCount)];

            int bitBase = o * 8;
            var br = new BitReader(buffer, bitBase);

            for (int i = 0; i < wheelCount; i++)
                wheelSpinDeg[i] = DequantizeAngle360(br.ReadBits(spinBits), spinBits);

            for (int i = 0; i < steerCount; i++)
                steerDeg[i] = DequantizeSignedRange(br.ReadBits(steerBits), steerMin, steerMax, steerBits);

            engineRevs01 = Dequantize01(br.ReadBits(engineBits), engineBits);
            steerRatio = DequantizeSigned01(br.ReadBits(steerRatioBits), steerRatioBits);
        }

        // ---------------- Local endian float helpers ----------------
        private static uint FloatToU32(float v)
        {
            var b = BitConverter.GetBytes(v);
            return (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));
        }

        private static float U32ToFloat(uint v)
        {
            var b = new byte[4];
            b[0] = (byte)(v & 0xFF);
            b[1] = (byte)((v >> 8) & 0xFF);
            b[2] = (byte)((v >> 16) & 0xFF);
            b[3] = (byte)((v >> 24) & 0xFF);
            return BitConverter.ToSingle(b, 0);
        }

        private static void WriteU16(byte[] b, ref int o, ushort v)
        {
            b[o++] = (byte)(v & 0xFF);
            b[o++] = (byte)(v >> 8);
        }

        private static void WriteU32(byte[] b, ref int o, uint v)
        {
            b[o++] = (byte)(v & 0xFF);
            b[o++] = (byte)((v >> 8) & 0xFF);
            b[o++] = (byte)((v >> 16) & 0xFF);
            b[o++] = (byte)((v >> 24) & 0xFF);
        }

        private static void WriteF32(byte[] b, ref int o, float v)
        {
            WriteU32(b, ref o, FloatToU32(v));
        }

        private static ushort ReadU16(byte[] b, ref int o)
        {
            ushort v = (ushort)(b[o] | (b[o + 1] << 8));
            o += 2;
            return v;
        }

        private static uint ReadU32(byte[] b, ref int o)
        {
            uint v = (uint)(b[o]
                          | (b[o + 1] << 8)
                          | (b[o + 2] << 16)
                          | (b[o + 3] << 24));
            o += 4;
            return v;
        }

        private static float ReadF32(byte[] b, ref int o)
        {
            return U32ToFloat(ReadU32(b, ref o));
        }

        private static Quaternion NormalizeSafe(Quaternion q)
        {
            float m = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (m > 1e-8f)
            {
                float inv = 1f / m;
                q.x *= inv; q.y *= inv; q.z *= inv; q.w *= inv;
                return q;
            }
            return Quaternion.identity;
        }
    }
}
