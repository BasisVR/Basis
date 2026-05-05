using Basis.Network.Core.Compression;
using Unity.Collections;
using Unity.Mathematics;

namespace Basis.Scripts.Networking.NetworkedAvatar
{
    /// <summary>
    /// Unity-side wrappers around BasisBoneRotationCompression (which is pure C#).
    /// Bridges quaternion/NativeArray types to the server-compatible encode/decode.
    /// </summary>
    public static class BasisBoneRotationUtils
    {
        /// <summary>
        /// Compresses 54 bone delta rotations into a packed byte buffer.
        /// </summary>
        public static void CompressBoneRotations(
            NativeArray<quaternion> boneDeltas,
            BasisAvatarBitPacking.BitQuality quality,
            byte[] dst,
            ref int byteOffset)
        {
            byte[] bpc = BasisBoneRotationCompression.GetBpcTable(quality);
            int bitPos = byteOffset << 3;

            float[] ranges = BasisBoneRotationCompression.MAX_COMPONENT;

            for (int slot = 0; slot < BasisBoneRotationCompression.SyncBoneCount; slot++)
            {
                int bitsPerComp = bpc[slot];
                int totalBits = 2 + 3 * bitsPerComp;

                quaternion q = boneDeltas[slot];
                ulong packed = BasisBoneRotationCompression.EncodeSmallestThree(
                    q.value.x, q.value.y, q.value.z, q.value.w, bitsPerComp, ranges[slot]);

                BasisBoneRotationCompression.WriteBits(dst, bitPos, packed, totalBits);
                bitPos += totalBits;
            }

            byteOffset = (bitPos + 7) >> 3;
        }

        /// <summary>
        /// Decompresses bone rotations from a packed byte buffer into a NativeArray.
        /// </summary>
        public static void DecompressBoneRotations(
            byte[] src,
            BasisAvatarBitPacking.BitQuality quality,
            ref NativeArray<quaternion> boneDeltas,
            ref int byteOffset)
        {
            byte[] bpc = BasisBoneRotationCompression.GetBpcTable(quality);
            int bitPos = byteOffset << 3;

            float[] ranges = BasisBoneRotationCompression.MAX_COMPONENT;

            for (int slot = 0; slot < BasisBoneRotationCompression.SyncBoneCount; slot++)
            {
                int bitsPerComp = bpc[slot];
                int totalBits = 2 + 3 * bitsPerComp;

                ulong packed = BasisBoneRotationCompression.ReadBits(src, ref bitPos, totalBits);
                BasisBoneRotationCompression.DecodeSmallestThree(packed, bitsPerComp,
                    out float qx, out float qy, out float qz, out float qw, ranges[slot]);

                boneDeltas[slot] = new quaternion(qx, qy, qz, qw);
            }

            byteOffset = (bitPos + 7) >> 3;
        }
    }
}
