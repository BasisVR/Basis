using Basis.Network.Core;
using Basis.Network.Core.Compression;
using Basis.Scripts.Networking.Compression;
using Basis.Scripts.Networking.Transmitters;
using Basis.Scripts.Profiler;
using System.Linq;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using static SerializableBasis;
using static Basis.Network.Core.Compression.BasisAvatarBitPacking;
using Basis.Scripts.Drivers;

namespace Basis.Scripts.Networking.NetworkedAvatar
{
    public static class BasisNetworkAvatarCompressor
    {
        const int UnityMuscleCount = 95;

        static bool sInitialized;

        // persistent native LUTs / buffers
        static NativeArray<int> sOrder;         // slot -> muscle index
        static NativeArray<byte> sBitsPerSlot;  // slot -> bit width
        static NativeArray<int> sBitOffsets;    // slot -> bit offset in packed stream

        static NativeArray<float> sMin;         // index by muscle idx
        static NativeArray<float> sInv;         // 1/range or 0
        static NativeArray<float> sMax;         // min + range

        static NativeArray<float> sMusclesNative;   // input scratch persistent (95 floats)
        static NativeArray<uint> sQuantized;        // per-slot quantized ints
        static NativeArray<byte> sPacked;           // packed bitstream output

        // Clamp debug flags (written in Burst job, logged on main thread)
        // 0 = ok, 1 = hit min, 2 = hit max
        static NativeArray<byte> sClampFlags;

        static int sPackedBits;
        static int sPackedBytes;

        // We lock the wire format to HIGH
        static readonly BitQuality WireQuality = BitQuality.High;

        public static void Compress(BasisNetworkTransmitter transmitter, Animator animator)
        {
            Transform t = animator.transform;
            transmitter.PoseHandler ??= new HumanPoseHandler(animator.avatar, t);

            EnsureInitialized();

            transmitter.PoseHandler.GetHumanPose(ref transmitter.HumanPose);

            CompressAvatarData(transmitter.storedAvatarData, transmitter.HumanPose, animator, t);

            var data = transmitter.SendingOutAvatarData.Count == 0 ? null : transmitter.SendingOutAvatarData.Values.ToArray();
            transmitter.storedAvatarData.LASM.AdditionalAvatarDatas = data;

            transmitter.storedAvatarData.LASM.Serialize(transmitter.AvatarSendWriter, WireQuality);

            BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.LocalAvatarSync, transmitter.AvatarSendWriter.Length);

            BasisNetworkConnection.LocalPlayerPeer.Send(transmitter.AvatarSendWriter, BasisNetworkCommons.PlayerAvatarChannel, DeliveryMethod.Sequenced);

            transmitter.AvatarSendWriter.Reset();
            transmitter.ClearAdditional();
        }

        public static void InitalAvatarData(Animator animator, out BasisStoredAvatarData StoredAvatarData)
        {
            EnsureInitialized();

            Transform t = animator.transform;
            var poseHandler = new HumanPoseHandler(animator.avatar, t);
            var humanPose = new HumanPose();
            poseHandler.GetHumanPose(ref humanPose);

            StoredAvatarData = new BasisStoredAvatarData();
            CompressAvatarData(StoredAvatarData, humanPose, animator, t);
        }
        public static void CompressAvatarData(BasisStoredAvatarData AvatarData, HumanPose pose, Animator animator, Transform ScaleTransform)
        {
            EnsureInitialized();

            // Ensure payload buffer exists and is correct size for HIGH
            int needed = BasisAvatarBitPacking.ConvertToSize(WireQuality);
            AvatarData.LASM.DataQualityLevel = (byte)WireQuality;
            AvatarData.LASM.array ??= new byte[needed];
            if (AvatarData.LASM.array.Length != needed)
            {
                AvatarData.LASM.array = new byte[needed];
            }

            int offset = 0;
            Transform hips = BasisLocalAvatarDriver.Mapping.Hips;
            // Position
            BasisUnityBitPackerExtensionsUnsafe.WritePosition(animator.bodyPosition, ref AvatarData.LASM.array, ref offset);
            JobHandle handle = CompressAvatarMuscles_BitPacked(pose.muscles, ref AvatarData.LASM, ref offset, out int offsetForComplete);

            // Scale
            BasisUnityBitPackerExtensionsUnsafe.CompressScale(ScaleTransform.localScale.y, ref AvatarData.LASM, ref offset);

            // Rotation
            BasisUnityBitPackerExtensionsUnsafe.WriteCompressedQuaternionToBytes(animator.bodyRotation, ref AvatarData.LASM.array, ref offset);

            Complete(handle, ref AvatarData.LASM, offsetForComplete);
        }

        public static JobHandle CompressAvatarMuscles_BitPacked(float[] pose, ref LocalAvatarSyncMessage message, ref int offset, out int SuppliedIndex)
        {
            EnsureMusclesBuffer(UnityMuscleCount);

            // Copy pose.muscles into persistent native buffer (95 floats)
            unsafe
            {
                fixed (float* src = pose)
                {
                    UnsafeUtility.MemCpy(sMusclesNative.GetUnsafePtr(), src, sizeof(float) * UnityMuscleCount);
                }
            }

            unsafe
            {
                // Clear packed buffer before writing bits (important because we OR into bytes)
                UnsafeUtility.MemClear(sPacked.GetUnsafePtr(), sPackedBytes);
            }

            // Job A: quantize each slot in parallel -> sQuantized[slot]
            var qJob = new QuantizeJob
            {
                Muscles = sMusclesNative,
                Min = sMin,
                Inv = sInv,
                Max = sMax,
                Order = sOrder,
                BitsPerSlot = sBitsPerSlot,
                OutQuant = sQuantized,
                ClampFlags = sClampFlags,
            };

            // Job B: pack quantized ints into bitstream (single thread)
            var pJob = new PackJob
            {
                BitsPerSlot = sBitsPerSlot,
                BitOffsets = sBitOffsets,
                Quant = sQuantized,
                Packed = sPacked,
            };

            JobHandle h1 = qJob.Schedule(sOrder.Length, 32);
            JobHandle h2 = pJob.Schedule(h1);

            SuppliedIndex = offset;
            offset += sPackedBytes;

            return h2;
        }
        public static void Complete(JobHandle h2, ref LocalAvatarSyncMessage message, int SuppliedIndex)
        {
            h2.Complete();

            // Log clamp hits (must be main thread; Burst jobs cannot call Debug.Log)
            // If you want to reduce spam, wrap this in DEVELOPMENT_BUILD / UNITY_EDITOR or add throttling.
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            for (int slot = 0; slot < sClampFlags.Length; slot++)
            {
                byte flag = sClampFlags[slot];
                if (flag == 0) continue;

                int muscle = sOrder[slot];

                if (flag == 1)
                {
                    BasisDebug.LogError($"[BasisNetworkAvatarCompressor] Clamp MIN hit. slot={slot} muscleIndex={muscle}");
                }
                else // flag == 2
                {
                    BasisDebug.LogError($"[BasisNetworkAvatarCompressor] Clamp MAX hit. slot={slot} muscleIndex={muscle}");
                }
            }
#endif

            // Copy packed bytes into final message buffer at offset
            unsafe
            {
                void* srcPtr = sPacked.GetUnsafeReadOnlyPtr();
                fixed (byte* dst = message.array)
                {
                    UnsafeUtility.MemCpy(dst + SuppliedIndex, srcPtr, sPackedBytes);
                }
            }
        }
        static void EnsureInitialized()
        {
            if (sInitialized) return;

            // These must already be initialized by BasisOrderedDataSet.Initalize()
            var minT = BasisAvatarBitPacking.MinMuscle;
            var rangeT = BasisAvatarBitPacking.RangeMuscle;

            if (minT == null || rangeT == null || minT.Length != UnityMuscleCount || rangeT.Length != UnityMuscleCount)
            {
                Debug.LogError("[BasisNetworkAvatarCompressor] BasisOrderedDataSet tables invalid. Call BasisOrderedDataSet.Initalize() first.");
                return;
            }

            int slots = BasisAvatarBitPacking.WRITE_ORDER.Length;

            // ALWAYS use HIGH bits table
            byte[] bitsManaged = BasisAvatarBitPacking.GetBitsPerSlot(WireQuality);

            // Compute bit offsets + packed sizes
            sPackedBits = 0;
            var bitOffsManaged = new int[slots];
            for (int i = 0; i < slots; i++)
            {
                bitOffsManaged[i] = sPackedBits;
                sPackedBits += bitsManaged[i];
            }
            sPackedBytes = (sPackedBits + 7) >> 3;

            // Allocate persistent natives
            sOrder = new NativeArray<int>(slots, Allocator.Persistent);
            sBitsPerSlot = new NativeArray<byte>(slots, Allocator.Persistent);
            sBitOffsets = new NativeArray<int>(slots, Allocator.Persistent);

            sMin = new NativeArray<float>(UnityMuscleCount, Allocator.Persistent);
            sInv = new NativeArray<float>(UnityMuscleCount, Allocator.Persistent);
            sMax = new NativeArray<float>(UnityMuscleCount, Allocator.Persistent);

            sPacked = new NativeArray<byte>(sPackedBytes, Allocator.Persistent);
            sQuantized = new NativeArray<uint>(slots, Allocator.Persistent);

            // Clamp debug flags
            sClampFlags = new NativeArray<byte>(slots, Allocator.Persistent);

            // Fill per-slot arrays
            for (int i = 0; i < slots; i++)
            {
                sOrder[i] = BasisAvatarBitPacking.WRITE_ORDER[i];
                sBitsPerSlot[i] = bitsManaged[i];       // <-- HIGH bits
                sBitOffsets[i] = bitOffsManaged[i];
                sClampFlags[i] = 0;
            }

            // Fill per-muscle LUTs
            for (int idx = 0; idx < UnityMuscleCount; idx++)
            {
                float min = minT[idx];
                float r = rangeT[idx];
                sMin[idx] = min;
                sInv[idx] = (r <= 0f) ? 0f : 1f / r;
                sMax[idx] = min + r;
            }

            EnsureMusclesBuffer(UnityMuscleCount);

            sInitialized = true;
        }

        static void EnsureMusclesBuffer(int count)
        {
            if (!sMusclesNative.IsCreated || sMusclesNative.Length != count)
            {
                if (sMusclesNative.IsCreated) sMusclesNative.Dispose();
                sMusclesNative = new NativeArray<float>(count, Allocator.Persistent);
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void OnDomainReload()
        {
            Dispose();
        }

        public static void Dispose()
        {
            if (sOrder.IsCreated) sOrder.Dispose();
            if (sBitsPerSlot.IsCreated) sBitsPerSlot.Dispose();
            if (sBitOffsets.IsCreated) sBitOffsets.Dispose();

            if (sMin.IsCreated) sMin.Dispose();
            if (sInv.IsCreated) sInv.Dispose();
            if (sMax.IsCreated) sMax.Dispose();

            if (sPacked.IsCreated) sPacked.Dispose();
            if (sMusclesNative.IsCreated) sMusclesNative.Dispose();
            if (sQuantized.IsCreated) sQuantized.Dispose();

            if (sClampFlags.IsCreated) sClampFlags.Dispose();

            sInitialized = false;
        }

        [BurstCompile(FloatMode = FloatMode.Default, FloatPrecision = FloatPrecision.Medium)]
        struct QuantizeJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float> Muscles;   // index by Unity muscle index
            [ReadOnly] public NativeArray<float> Min;
            [ReadOnly] public NativeArray<float> Inv;
            [ReadOnly] public NativeArray<float> Max;

            [ReadOnly] public NativeArray<int> Order;        // slot -> muscle idx
            [ReadOnly] public NativeArray<byte> BitsPerSlot; // slot -> bits

            public NativeArray<uint> OutQuant;               // slot -> q
            public NativeArray<byte> ClampFlags;             // slot -> 0/1/2

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static uint QuantN(float x01, int bits)
            {
                uint maxQ = (uint)((1 << bits) - 1);
                return (uint)math.round(x01 * maxQ);
            }

            public void Execute(int slot)
            {
                int idx = Order[slot];
                float v = Muscles[idx];

                float min = Min[idx];
                float inv = Inv[idx];
                float max = Max[idx];

                // Detect saturation BEFORE clamping so we only log real min/max hits.
                byte flag = 0;
                if (v <= min) flag = 1;
                else if (v >= max) flag = 2;
                ClampFlags[slot] = flag;

                float clamped = math.clamp(v, min, max);
                float norm = (inv == 0f) ? 0f : (clamped - min) * inv;

                int bits = BitsPerSlot[slot];
                OutQuant[slot] = QuantN(norm, bits);
            }
        }

        [BurstCompile(FloatMode = FloatMode.Default, FloatPrecision = FloatPrecision.Medium)]
        struct PackJob : IJob
        {
            [ReadOnly] public NativeArray<byte> BitsPerSlot; // slot -> bits
            [ReadOnly] public NativeArray<int> BitOffsets;   // slot -> bit offset
            [ReadOnly] public NativeArray<uint> Quant;       // slot -> q

            public NativeArray<byte> Packed;                 // output bytes (pre-cleared)

            public void Execute()
            {
                int slots = BitsPerSlot.Length;
                for (int slot = 0; slot < slots; slot++)
                {
                    int bitPos = BitOffsets[slot];
                    int bits = BitsPerSlot[slot];
                    uint q = Quant[slot];
                    BitWriter.WriteBits(ref Packed, bitPos, q, bits);
                }
            }
        }

        static class BitWriter
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void WriteBits(ref NativeArray<byte> dst, int bitPos, uint value, int bitCount)
            {
                int bytePos = bitPos >> 3;
                int bitInByte = bitPos & 7;

                uint v = value;
                int bitsLeft = bitCount;

                while (bitsLeft > 0)
                {
                    int room = 8 - bitInByte;
                    int take = bitsLeft < room ? bitsLeft : room;

                    uint mask = (uint)((1 << take) - 1);
                    byte chunk = (byte)(v & mask);

                    byte cur = dst[bytePos];
                    cur = (byte)(cur | (chunk << bitInByte));
                    dst[bytePos] = cur;

                    v >>= take;
                    bitsLeft -= take;
                    bytePos++;
                    bitInByte = 0;
                }
            }
        }
    }
}
