using Basis.Network.Core.Compression;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine.Jobs;
using static Basis.Network.Core.Compression.BasisAvatarBitPacking;

namespace Basis.Scripts.Networking.NetworkedAvatar
{
    /// <summary>
    /// Burst-compiled job that converts bone local rotations into the rig-neutral generic
    /// rotation space and compresses them into a packed byte buffer using smallest-three
    /// quaternion encoding.
    ///
    /// Replaces the main-thread ExtractBoneDeltas() + CompressBoneRotations() calls
    /// with a single Burst-optimized pass. The job reads current bone local rotations
    /// (written by a prior TransformAccessArray read), maps them into generic space with the
    /// folded operators cached at calibration, and writes the compressed bitstream.
    ///
    /// The mapped value is <c>g = pre * currentLocal * post</c> — see
    /// <see cref="Basis.Network.Core.Compression.BasisGenericBoneRotation"/> for what g means and
    /// why the operators fold this way. Conjugation preserves rotation angle, so g occupies
    /// exactly the same magnitude range the old T-pose-relative local delta did and the
    /// smallest-three budget below is unchanged.
    ///
    /// This runs as an IJob (not parallel) because the bit-packed output is sequential.
    /// Burst still provides significant wins via SIMD quaternion math and branch elimination.
    /// </summary>
    [BurstCompile]
    public struct BasisBoneDeltaAndCompressJob : IJob
    {
        /// <summary>Current local rotations read from transforms. Length = SyncBoneCount.</summary>
        [ReadOnly] public NativeArray<quaternion> CurrentLocalRotations;

        /// <summary>
        /// Left factor of the generic-space encode, per bone slot: <c>restFrame * conj(tposeLocal)</c>.
        /// Length = SyncBoneCount. Folded at capture rather than rebuilt per bone per tick — the
        /// rest pose is fixed for the life of the avatar, so deriving it inside the encode loop
        /// would recompute a constant every send.
        /// </summary>
        [ReadOnly] public NativeArray<quaternion> EncodePre;

        /// <summary>Right factor of the generic-space encode, per bone slot: <c>conj(restFrame)</c>.</summary>
        [ReadOnly] public NativeArray<quaternion> EncodePost;

        /// <summary>Bits-per-component per slot. Length = SyncBoneCount.</summary>
        [ReadOnly] public NativeArray<byte> BitsPerComponent;

        /// <summary>Max quaternion component range per slot. Length = SyncBoneCount.</summary>
        [ReadOnly] public NativeArray<float> MaxComponent;

        /// <summary>Degrees of freedom per wire slot (v52): 3 = smallest-three, 2 = hinge+twist
        /// angles, 1 = single angle. Mirrors BasisBoneRotationCompression.BONE_DOF.</summary>
        [ReadOnly] public NativeArray<byte> BoneDof;

        /// <summary>Hinge axis code (0=X 1=Y 2=Z) per restricted slot.</summary>
        [ReadOnly] public NativeArray<byte> BoneAxisA;

        /// <summary>Twist axis code per 2-DOF slot.</summary>
        [ReadOnly] public NativeArray<byte> BoneAxisB;

        /// <summary>Half-range (radians) of the hinge angle per restricted slot.</summary>
        [ReadOnly] public NativeArray<float> BoneRangeA;

        /// <summary>Half-range (radians) of the twist angle per 2-DOF slot.</summary>
        [ReadOnly] public NativeArray<float> BoneRangeB;

        public int HingeBitCount;
        public int TwistBitCount;
        public int SingleAxisBitCount;

        /// <summary>Output byte buffer (the full packet array). Must be pre-cleared in the rotation region.</summary>
        [NativeDisableContainerSafetyRestriction]
        public NativeArray<byte> OutputBuffer;

        /// <summary>Byte offset where the rotation bitstream starts (after position bytes).</summary>
        public int RotationByteOffset;

        /// <summary>Explicit bone slots to encode (wire slots 0..BoneCount-1).</summary>
        public int BoneCount;

        /// <summary>Ten curl/splay pairs appended after the explicit slots. See BasisBoneRotationCompression.</summary>
        [ReadOnly] public NativeArray<float2> FingerPercentages;
        public int CurlBits;
        public int SplayBits;

        /// <summary>Computed generic-space rotations, written for other consumers (e.g., interpolation).</summary>
        public NativeArray<quaternion> BoneDeltas;

        public void Execute()
        {
            int bitPos = RotationByteOffset << 3;

            for (int slot = 0; slot < BoneCount; slot++)
            {
                // generic = (restFrame * conj(tpose)) * current * conj(restFrame)
                quaternion current = CurrentLocalRotations[slot];
                quaternion delta = math.mul(math.mul(EncodePre[slot], current), EncodePost[slot]);

                // Normalize
                float4 dv = delta.value;
                float lenSq = math.lengthsq(dv);
                delta = lenSq > 1e-8f ? new quaternion(dv * math.rsqrt(lenSq)) : quaternion.identity;

                BoneDeltas[slot] = delta;

                int dof = BoneDof[slot];
                int totalBits;
                ulong packed;
                if (dof == 3)
                {
                    int bpc = BitsPerComponent[slot];
                    totalBits = 2 + 3 * bpc;
                    packed = EncodeSmallestThree(delta.value.x, delta.value.y, delta.value.z, delta.value.w, bpc, MaxComponent[slot]);
                }
                else if (dof == 2)
                {
                    totalBits = HingeBitCount + TwistBitCount;
                    ExtractHingeTwist(delta.value, BoneAxisA[slot], BoneAxisB[slot], out float angleA, out float angleB);
                    ulong ea = EncodeSignedUnit(angleA / BoneRangeA[slot], HingeBitCount);
                    ulong eb = EncodeSignedUnit(angleB / BoneRangeB[slot], TwistBitCount);
                    packed = ea | (eb << HingeBitCount);
                }
                else
                {
                    totalBits = SingleAxisBitCount;
                    float angle = ExtractSingleAxis(delta.value, BoneAxisA[slot]);
                    packed = EncodeSignedUnit(angle / BoneRangeA[slot], SingleAxisBitCount);
                }

                WriteBits(bitPos, packed, totalBits);
                bitPos += totalBits;
            }

            int fingerWidth = CurlBits + SplayBits;
            for (int finger = 0; finger < FingerPercentages.Length; finger++)
            {
                float2 pct = FingerPercentages[finger];
                ulong curl = EncodeSignedUnit(pct.x, CurlBits);
                ulong splay = EncodeSignedUnit(pct.y, SplayBits);
                WriteBits(bitPos, curl | (splay << CurlBits), fingerWidth);
                bitPos += fingerWidth;
            }
        }

        /// <summary>
        /// Burst-compatible mirror of BasisBoneRotationCompression.EncodeSignedUnit. Clamps rather
        /// than wraps, and maps a non-finite input to the midpoint so an overshooting gain or a
        /// dropped tracking frame cannot encode as a full-scale curl.
        /// </summary>
        private static ulong EncodeSignedUnit(float value, int bits)
        {
            uint maxQ = (uint)((1 << bits) - 1);
            if (math.isnan(value)) return (maxQ + 1) >> 1;
            float clamped = math.clamp(value, -1f, 1f);
            return Clamp((uint)math.round((clamped * 0.5f + 0.5f) * maxQ), 0, maxQ);
        }

        static float GetComponent(float4 v, int axis) => axis == 0 ? v.x : (axis == 1 ? v.y : v.z);

        /// <summary>
        /// Burst-compatible mirror of BasisBoneRotationCompression.ExtractHingeTwist: factorizes
        /// q as R_axisA(angleA) * R_axisB(angleB), projecting off-axis content away.
        /// </summary>
        static void ExtractHingeTwist(float4 q, int axisA, int axisB, out float angleA, out float angleB)
        {
            if (q.w < 0f) q = -q;

            float pb = GetComponent(q, axisB);
            float len = math.sqrt(pb * pb + q.w * q.w);
            float tb, tw;
            if (len > 1e-6f)
            {
                angleB = 2f * math.atan2(pb, q.w);
                float inv = 1f / len;
                tb = pb * inv; tw = q.w * inv;
            }
            else
            {
                angleB = 0f; tb = 0f; tw = 1f;
            }

            // swing = q * conj(twist)
            float cx = axisB == 0 ? -tb : 0f;
            float cy = axisB == 1 ? -tb : 0f;
            float cz = axisB == 2 ? -tb : 0f;
            float sw = q.w * tw - q.x * cx - q.y * cy - q.z * cz;
            float sx = q.w * cx + q.x * tw + q.y * cz - q.z * cy;
            float sy = q.w * cy - q.x * cz + q.y * tw + q.z * cx;
            float sz = q.w * cz + q.x * cy - q.y * cx + q.z * tw;

            if (sw < 0f) { sx = -sx; sy = -sy; sz = -sz; sw = -sw; }
            angleA = 2f * math.atan2(GetComponent(new float4(sx, sy, sz, sw), axisA), sw);
        }

        /// <summary>Burst-compatible mirror of BasisBoneRotationCompression.ExtractSingleAxis.</summary>
        static float ExtractSingleAxis(float4 q, int axisA)
        {
            if (q.w < 0f) q = -q;
            return 2f * math.atan2(GetComponent(q, axisA), q.w);
        }

        // Burst-compatible encode (inlined from BasisBoneRotationCompression)
        private ulong EncodeSmallestThree(float qx, float qy, float qz, float qw, int bpc, float maxRange)
        {
            float ax = math.abs(qx), ay = math.abs(qy), az = math.abs(qz), aw = math.abs(qw);

            int maxIdx = 0;
            float maxVal = ax;
            if (ay > maxVal) { maxIdx = 1; maxVal = ay; }
            if (az > maxVal) { maxIdx = 2; maxVal = az; }
            if (aw > maxVal) { maxIdx = 3; }

            // Negate if largest is negative
            float sign = maxIdx switch { 0 => qx, 1 => qy, 2 => qz, _ => qw };
            if (sign < 0f) { qx = -qx; qy = -qy; qz = -qz; qw = -qw; }

            float a, b, c;
            switch (maxIdx)
            {
                case 0: a = qy; b = qz; c = qw; break;
                case 1: a = qx; b = qz; c = qw; break;
                case 2: a = qx; b = qy; c = qw; break;
                default: a = qx; b = qy; c = qz; break;
            }

            float invRange = 1f / maxRange;
            uint maxQ = (uint)((1 << bpc) - 1);
            uint qa = Clamp((uint)math.round((math.clamp(a * invRange, -1f, 1f) * 0.5f + 0.5f) * maxQ), 0, maxQ);
            uint qb = Clamp((uint)math.round((math.clamp(b * invRange, -1f, 1f) * 0.5f + 0.5f) * maxQ), 0, maxQ);
            uint qc = Clamp((uint)math.round((math.clamp(c * invRange, -1f, 1f) * 0.5f + 0.5f) * maxQ), 0, maxQ);

            return (ulong)(uint)maxIdx | ((ulong)qa << 2) | ((ulong)qb << (2 + bpc)) | ((ulong)qc << (2 + 2 * bpc));
        }

        private static uint Clamp(uint v, uint min, uint max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        private unsafe void WriteBits(int bitPos, ulong value, int bitCount)
        {
            int bytePos = bitPos >> 3;
            // OutputBuffer is [NativeDisableContainerSafetyRestriction] and written through a raw
            // pointer, so nothing below this line would catch an overrun — not the container's own
            // bounds check, not the job safety system. The bit budget is supposed to match
            // BasisBoneRotationCompression.RotationBytes exactly, but that agreement is spread over
            // the per-slot DOF table, the quality bit widths and FingerPercentages.Length; any one
            // of them drifting turns a wire-format mismatch into a silent heap write past the
            // packet. Drop the field instead: a short packet is a visible desync, not corruption.
            if (bitPos < 0 || ((bitPos + bitCount + 7) >> 3) > OutputBuffer.Length) return;

            byte* dst = (byte*)OutputBuffer.GetUnsafePtr();
            int bitInByte = bitPos & 7;
            int bitsLeft = bitCount;

            while (bitsLeft > 0)
            {
                int room = 8 - bitInByte;
                int take = bitsLeft < room ? bitsLeft : room;
                ulong maskVal = (1UL << take) - 1UL;
                byte chunk = (byte)(value & maskVal);
                dst[bytePos] = (byte)(dst[bytePos] | (chunk << bitInByte));
                value >>= take;
                bitsLeft -= take;
                bytePos++;
                bitInByte = 0;
            }
        }
    }

    /// <summary>
    /// Reads bone local rotations from transforms via TransformAccessArray,
    /// replacing the main-thread ExtractBoneDeltas loop.
    /// Uses a slot remap to handle missing bones (only valid transforms
    /// are in the TransformAccessArray).
    /// </summary>
    [BurstCompile]
    public struct ReadBoneLocalRotationsJob : IJobParallelForTransform
    {
        /// <summary>Maps TransformAccessArray index → slot in CurrentLocalRotations.</summary>
        [ReadOnly] public NativeArray<int> SlotRemap;

        /// <summary>Output: local rotations written at remapped slot indices.</summary>
        [NativeDisableParallelForRestriction]
        public NativeArray<quaternion> CurrentLocalRotations;

        public void Execute(int index, TransformAccess transform)
        {
            CurrentLocalRotations[SlotRemap[index]] = transform.localRotation;
        }
    }
}
