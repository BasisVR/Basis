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
    /// Burst-compiled job that computes T-pose-relative bone deltas and compresses them
    /// into a packed byte buffer using smallest-three quaternion encoding.
    ///
    /// Replaces the main-thread ExtractBoneDeltas() + CompressBoneRotations() calls
    /// with a single Burst-optimized pass. The job reads current bone local rotations
    /// (written by a prior TransformAccessArray read), computes deltas against cached
    /// T-pose rotations, and writes the compressed bitstream.
    ///
    /// This runs as an IJob (not parallel) because the bit-packed output is sequential.
    /// Burst still provides significant wins via SIMD quaternion math and branch elimination.
    /// </summary>
    [BurstCompile]
    public struct BasisBoneDeltaAndCompressJob : IJob
    {
        /// <summary>Current local rotations read from transforms. Length = SyncBoneCount.</summary>
        [ReadOnly] public NativeArray<quaternion> CurrentLocalRotations;

        /// <summary>T-pose local rotations per bone slot. Length = SyncBoneCount.</summary>
        [ReadOnly] public NativeArray<quaternion> TposeLocalRotations;

        /// <summary>Bits-per-component per slot. Length = SyncBoneCount.</summary>
        [ReadOnly] public NativeArray<byte> BitsPerComponent;

        /// <summary>Max quaternion component range per slot. Length = SyncBoneCount.</summary>
        [ReadOnly] public NativeArray<float> MaxComponent;

        /// <summary>Output byte buffer (the full packet array). Must be pre-cleared in the rotation region.</summary>
        [NativeDisableContainerSafetyRestriction]
        public NativeArray<byte> OutputBuffer;

        /// <summary>Byte offset where the rotation bitstream starts (after position bytes).</summary>
        public int RotationByteOffset;

        /// <summary>Number of bones to process.</summary>
        public int BoneCount;

        /// <summary>Computed bone deltas, written for other consumers (e.g., interpolation).</summary>
        public NativeArray<quaternion> BoneDeltas;

        public void Execute()
        {
            int bitPos = RotationByteOffset << 3;

            for (int slot = 0; slot < BoneCount; slot++)
            {
                // Delta = inverse(tpose) * current
                quaternion tpose = TposeLocalRotations[slot];
                quaternion current = CurrentLocalRotations[slot];
                quaternion delta = math.mul(math.inverse(tpose), current);

                // Normalize
                float4 dv = delta.value;
                float lenSq = math.lengthsq(dv);
                delta = lenSq > 1e-8f ? new quaternion(dv * math.rsqrt(lenSq)) : quaternion.identity;

                BoneDeltas[slot] = delta;

                // Encode smallest-three
                int bpc = BitsPerComponent[slot];
                int totalBits = 2 + 3 * bpc;
                float maxRange = MaxComponent[slot];

                ulong packed = EncodeSmallestThree(delta.value.x, delta.value.y, delta.value.z, delta.value.w, bpc, maxRange);
                WriteBits(bitPos, packed, totalBits);
                bitPos += totalBits;
            }
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

            return (ulong)maxIdx | ((ulong)qa << 2) | ((ulong)qb << (2 + bpc)) | ((ulong)qc << (2 + 2 * bpc));
        }

        private static uint Clamp(uint v, uint min, uint max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        private unsafe void WriteBits(int bitPos, ulong value, int bitCount)
        {
            byte* dst = (byte*)OutputBuffer.GetUnsafePtr();
            int bytePos = bitPos >> 3;
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
