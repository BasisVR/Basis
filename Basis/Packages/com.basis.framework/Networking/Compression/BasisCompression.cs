using System.Runtime.CompilerServices;
using UnityEngine;

namespace Basis.Scripts.Networking.Compression
{
    /// <summary>Functions to Compress Quaternions and Floats</summary>
    public static class BasisCompression
    {
        /// <summary>
        /// A Smallest Three Quaternion Compressor Implementation
        /// </summary>
        public static class QuaternionCompressor
        {
            private const ushort k_PrecisionMask = (1 << 9) - 1;

            private const float k_SqrtTwoOverTwoEncoding = 0.70710678118654752440084436210485f;
            private const float k_CompressionEncodingMask = (1.0f / k_SqrtTwoOverTwoEncoding) * k_PrecisionMask;
            private const ushort k_ShiftNegativeBit = 9;
            private const float k_DecompressionDecodingMask = (1.0f / k_PrecisionMask) * k_SqrtTwoOverTwoEncoding;
            private const ushort k_NegShortBit = 0x200;

            private const ushort k_True = 1;
            private const ushort k_False = 0;

            /// <summary>
            /// Compresses a Quaternion into a uint
            /// </summary>
            public static uint CompressQuaternion(ref UnityEngine.Quaternion quaternion)
            {
                float x = quaternion.x;
                float y = quaternion.y;
                float z = quaternion.z;
                float w = quaternion.w;

                float ax = Mathf.Abs(x);
                float ay = Mathf.Abs(y);
                float az = Mathf.Abs(z);
                float aw = Mathf.Abs(w);

                float max = Mathf.Max(ax, Mathf.Max(ay, Mathf.Max(az, aw)));

                int indexToSkip = ax == max ? 0 : ay == max ? 1 : az == max ? 2 : 3;
                bool maxSign = GetSign(quaternion, indexToSkip);

                uint compressed = (uint)indexToSkip;
                int index = 0;

                void Encode(float value, int skip)
                {
                    if (index == skip) { index++; return; }

                    bool signBit = (value < 0) != maxSign;
                    ushort encoded = (ushort)Mathf.Round(Mathf.Abs(value) * k_CompressionEncodingMask);
                    compressed = (compressed << 10) | (uint)((signBit ? 1u : 0u) << k_ShiftNegativeBit | encoded);

                    index++;
                }

                Encode(x, indexToSkip);
                Encode(y, indexToSkip);
                Encode(z, indexToSkip);
                Encode(w, indexToSkip);

                return compressed;
            }

            /// <summary>
            /// Decompresses a compressed uint into a Quaternion
            /// </summary>
            public static UnityEngine.Quaternion DecompressQuaternion(uint compressed)
            {
                int indexToSkip = (int)(compressed >> 30);
                float[] result = new float[4];
                float sumSquares = 0f;

                for (int i = 3; i >= 0; i--)
                {
                    if (i == indexToSkip)
                        continue;

                    bool sign = (compressed & k_NegShortBit) != 0;
                    ushort encoded = (ushort)(compressed & k_PrecisionMask);
                    float value = encoded * k_DecompressionDecodingMask;
                    result[i] = sign ? -value : value;

                    sumSquares += result[i] * result[i];
                    compressed >>= 10;
                }

                result[indexToSkip] = Mathf.Sqrt(1.0f - sumSquares);

                return new UnityEngine.Quaternion(result[0], result[1], result[2], result[3]);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static bool GetSign(UnityEngine.Quaternion quat, int index)
            {
                return index switch
                {
                    0 => quat.x < 0,
                    1 => quat.y < 0,
                    2 => quat.z < 0,
                    3 => quat.w < 0,
                    _ => false,
                };
            }
        }
    }
}
