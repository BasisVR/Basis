using System;
using System.Runtime.CompilerServices;
using Unity.Mathematics;
using UnityEngine.UIElements;
using static BasisNetworkPrimitiveCompression;
using static SerializableBasis;
namespace Basis.Scripts.Networking.Compression
{
    public static class BasisUnityBitPackerExtensionsUnsafe
    {
        public const int FloatSize = sizeof(float);
        public const int UShortSize = sizeof(ushort);
        public const int Vector3Size = 3 * FloatSize;
        public const int QuaternionSize = 3 * FloatSize + UShortSize;
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
