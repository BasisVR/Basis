using Basis.Scripts.Networking.Compression;
using System.Runtime.CompilerServices;

namespace Basis.Network.Core.Compression
{
    public static class BasisNetworkCompressionExtensions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WritePosition(Vector3 position, ref byte[] buffer, ref int offset)
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
        public static Vector3 ReadPosition(ref byte[] buffer)
        {
            Vector3 result;
            unsafe
            {
                fixed (byte* src = &buffer[0])
                {
                    float* fSrc = (float*)src;
                    result.x = fSrc[0];
                    result.y = fSrc[1];
                    result.z = fSrc[2];
                }
            }
            return result;
        }
    }
}
