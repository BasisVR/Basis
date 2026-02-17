using System;
using UnityEngine;
namespace Basis.Network.Vehicles
{
    public static class BasisVehicleNetCodec
    {
        public const int MinPacketSize = 22;
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

        // ---------- Quaternion compression (32-bit smallest-three) ----------
        private const float INV_SQRT2 = 0.7071067811865475f;

        public static uint PackQuaternionSmallestThree32(Quaternion q)
        {
            float[] c = { q.x, q.y, q.z, q.w };

            int largest = 0;
            float largestAbs = Mathf.Abs(c[0]);
            for (int i = 1; i < 4; i++)
            {
                float a = Mathf.Abs(c[i]);
                if (a > largestAbs)
                {
                    largestAbs = a;
                    largest = i;
                }
            }

            if (c[largest] < 0f)
                for (int i = 0; i < 4; i++) c[i] = -c[i];

            int aIdx = (largest + 1) & 3;
            int bIdx = (largest + 2) & 3;
            int cIdx = (largest + 3) & 3;

            uint qa = Quantize10(c[aIdx]);
            uint qb = Quantize10(c[bIdx]);
            uint qc = Quantize10(c[cIdx]);

            return ((uint)largest & 0x3u)
                 | ((qa & 0x3FFu) << 2)
                 | ((qb & 0x3FFu) << 12)
                 | ((qc & 0x3FFu) << 22);
        }

        public static Quaternion UnpackQuaternionSmallestThree32(uint packed)
        {
            int largest = (int)(packed & 0x3u);

            float a = Dequantize10((packed >> 2) & 0x3FFu);
            float b = Dequantize10((packed >> 12) & 0x3FFu);
            float c = Dequantize10((packed >> 22) & 0x3FFu);

            float[] o = new float[4];
            o[(largest + 1) & 3] = a;
            o[(largest + 2) & 3] = b;
            o[(largest + 3) & 3] = c;

            float sum = a * a + b * b + c * c;
            o[largest] = Mathf.Sqrt(Mathf.Max(0f, 1f - sum));

            return NormalizeSafe(new Quaternion(o[0], o[1], o[2], o[3]));
        }

        private static uint Quantize10(float v)
        {
            v = Mathf.Clamp(v, -INV_SQRT2, INV_SQRT2);
            float n = (v + INV_SQRT2) / (2f * INV_SQRT2);
            int q = Mathf.RoundToInt(n * 1023f);
            return (uint)Mathf.Clamp(q, 0, 1023);
        }

        private static float Dequantize10(uint q)
        {
            float n = q / 1023f;
            return n * (2f * INV_SQRT2) - INV_SQRT2;
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

        // ---------- Half floats for scale ----------
        public static ushort FloatToHalf(float f)
        {
            uint x = FloatToU32(f);

            uint sign = (x >> 16) & 0x8000u;
            int exp = (int)((x >> 23) & 0xFFu) - 127 + 15;
            uint mant = x & 0x7FFFFFu;

            if (exp <= 0)
            {
                if (exp < -10) return (ushort)sign;
                mant |= 0x800000u;
                int shift = 14 - exp;
                uint halfMant = mant >> shift;
                if (((mant >> (shift - 1)) & 1u) != 0u) halfMant += 1u;
                return (ushort)(sign | (halfMant & 0x03FFu));
            }

            if (exp >= 31)
            {
                if (mant == 0) return (ushort)(sign | 0x7C00u);
                return (ushort)(sign | 0x7C00u | (mant >> 13));
            }

            uint halfExp = (uint)exp << 10;
            uint halfMant2 = mant >> 13;
            if ((mant & 0x00001000u) != 0u) halfMant2 += 1u;

            return (ushort)(sign | halfExp | (halfMant2 & 0x03FFu));
        }

        public static float HalfToFloat(ushort h)
        {
            uint sign = (uint)(h & 0x8000u) << 16;
            uint exp = (uint)(h & 0x7C00u) >> 10;
            uint mant = (uint)(h & 0x03FFu);

            if (exp == 0)
            {
                if (mant == 0) return U32ToFloat(sign);

                exp = 1;
                while ((mant & 0x0400u) == 0)
                {
                    mant <<= 1;
                    exp--;
                }
                mant &= 0x03FFu;

                uint outExp = (exp - 15 + 127) << 23;
                uint outMant = mant << 13;
                return U32ToFloat(sign | outExp | outMant);
            }

            if (exp == 31)
            {
                uint outExp = 0xFFu << 23;
                uint outMant = mant << 13;
                return U32ToFloat(sign | outExp | outMant);
            }

            uint outExp2 = (exp - 15 + 127) << 23;
            uint outMant2 = mant << 13;
            return U32ToFloat(sign | outExp2 | outMant2);
        }
    }
}
