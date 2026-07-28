using Basis.Network.Core.Compression;
using Unity.Mathematics;

namespace Basis.Scripts.Networking.NetworkedAvatar
{
    /// <summary>
    /// Wire codec for the end-effector anchoring block (High quality only, appended after the tail).
    ///
    /// Layout — 35 bytes = 280 bits, LSB-first via <see cref="BasisBoneRotationCompression.WriteBits"/>:
    ///   [mask : 8 bits]  bit0 LHand, bit1 RHand, bit2 LFoot, bit3 RFoot (1 = anchored / valid)
    ///   4 × effector, each 68 bits:
    ///     [tip position : 3 × 12 bits]  hips-local offset in a ±2 m box  (~1 mm)
    ///     [tip rotation : 2 + 3×10 bits] smallest-three world orientation (~0.08°)
    ///
    /// A per-effector 8-bit swivel (elbow/knee pole angle) used to follow the rotation. It was dropped
    /// because the receiver stopped reading it: the pole is taken from the FK joint position instead,
    /// which is stable and has no reference-axis degeneracy (see BasisRemoteNetworkDriver's solve).
    ///
    /// The block is fixed-size regardless of the mask so reads stay fixed-offset; unanchored slots are
    /// simply ignored by the receiver. Positions are hips-local so they inherit the hips' precision and
    /// need no absolute-world range. See <see cref="BasisRemoteLimbIK"/> for how the receiver applies them.
    /// </summary>
    public static class BasisAvatarEndEffectors
    {
        public const int EffectorCount = 4;      // 0=LHand 1=RHand 2=LFoot 3=RFoot
        public const int BlockBytes = 35;
        public const float PosRange = 2.0f;      // ±2 m hips-local box
        const int PosBits = 12, RotBpc = 10;

        static uint QuantPos(float v)
        {
            float n = math.saturate(math.clamp(v / PosRange, -1f, 1f) * 0.5f + 0.5f);
            return (uint)math.round(n * ((1 << PosBits) - 1));
        }
        static float DequantPos(uint q) => ((float)q / ((1 << PosBits) - 1) * 2f - 1f) * PosRange;

        /// <summary>Encodes the block at <paramref name="byteOffset"/>. pos/rot are length-4 (reused by the caller).</summary>
        public static void Encode(byte[] dst, int byteOffset, byte mask, float3[] pos, quaternion[] rot)
        {
            System.Array.Clear(dst, byteOffset, BlockBytes);   // WriteBits ORs into bytes
            int bit = byteOffset * 8;
            BasisBoneRotationCompression.WriteBits(dst, bit, mask, 8); bit += 8;
            for (int i = 0; i < EffectorCount; i++)
            {
                float3 p = pos[i];
                BasisBoneRotationCompression.WriteBits(dst, bit, QuantPos(p.x), PosBits); bit += PosBits;
                BasisBoneRotationCompression.WriteBits(dst, bit, QuantPos(p.y), PosBits); bit += PosBits;
                BasisBoneRotationCompression.WriteBits(dst, bit, QuantPos(p.z), PosBits); bit += PosBits;
                quaternion r = rot[i];
                ulong packed = BasisBoneRotationCompression.EncodeSmallestThree(r.value.x, r.value.y, r.value.z, r.value.w, RotBpc, BasisBoneRotationCompression.InvSqrt2);
                BasisBoneRotationCompression.WriteBits(dst, bit, packed, 2 + 3 * RotBpc); bit += 2 + 3 * RotBpc;
            }
        }

        /// <summary>Decodes the block at <paramref name="byteOffset"/> into the length-4 arrays; returns the mask.</summary>
        public static byte Decode(byte[] src, int byteOffset, float3[] pos, quaternion[] rot)
        {
            int bit = byteOffset * 8;
            byte mask = (byte)BasisBoneRotationCompression.ReadBits(src, ref bit, 8);
            for (int i = 0; i < EffectorCount; i++)
            {
                float x = DequantPos((uint)BasisBoneRotationCompression.ReadBits(src, ref bit, PosBits));
                float y = DequantPos((uint)BasisBoneRotationCompression.ReadBits(src, ref bit, PosBits));
                float z = DequantPos((uint)BasisBoneRotationCompression.ReadBits(src, ref bit, PosBits));
                pos[i] = new float3(x, y, z);
                ulong packed = BasisBoneRotationCompression.ReadBits(src, ref bit, 2 + 3 * RotBpc);
                BasisBoneRotationCompression.DecodeSmallestThree(packed, RotBpc, out float rx, out float ry, out float rz, out float rw, BasisBoneRotationCompression.InvSqrt2);
                rot[i] = math.normalize(new quaternion(rx, ry, rz, rw));
            }
            return mask;
        }
    }
}
