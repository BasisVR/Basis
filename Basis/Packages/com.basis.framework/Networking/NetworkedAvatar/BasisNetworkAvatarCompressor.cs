using Basis.Network.Core;
using Basis.Scripts.Networking.Compression;
using Basis.Scripts.Networking.Transmitters;
using Basis.Scripts.Profiler;
using LiteNetLib;
using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using static Basis.Scripts.Networking.Transmitters.BasisNetworkTransmitter;
using static SerializableBasis;

namespace Basis.Scripts.Networking.NetworkedAvatar
{
    public static class BasisNetworkAvatarCompressor
    {
        // ==============================
        // constants / counts
        // ==============================
        const int UnityMuscleCount = 95;
        const int Skipped = 6;                 // eyes/jaw (15..20)
        const int muscleCount = UnityMuscleCount - Skipped; // 89

        // ==============================
        // init state
        // ==============================
        static bool sInitialized;

        // managed lookup (from BasisMuscleRange)
        static float[] sMinManaged;        // length 95
        static float[] sInvManaged;        // 1/range or 0
        static float[] sMaxManaged;        // min + range

        // persistent native LUTs / buffers
        static NativeArray<int> sOrder;     // slot -> muscle index
        static NativeArray<byte> sIsByte;    // slot -> 1 if 8-bit, else 0
        static NativeArray<int> sOffsets;   // slot -> byte offset in packed
        static NativeArray<float> sMin;       // index by muscle idx
        static NativeArray<float> sInv;       // "
        static NativeArray<float> sMax;       // "
        static NativeArray<byte> sPacked;    // packed output, reused

        static NativeArray<float> sMusclesNative; // input scratch persistent

        static int sPackedSize;

        // write order (slot -> muscle index), matches your sections exactly, skipping 15..20
        // Spine/Chest/Head
        static readonly int[] WRITE_ORDER = new int[]
        {
            // 0..14
            0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,

            // Left Leg (slots 15..22) 21..28
            21,22,23,24,25,26,27,28,

            // Right Leg (slots 23..30) 29..36
            29,30,31,32,33,34,35,36,

            // Left Arm (slots 31..39) 37..45
            37,38,39,40,41,42,43,44,45,

            // Right Arm (slots 40..48) 46..54
            46,47,48,49,50,51,52,53,54,

            // Left Hand Fingers (slots 49..68) 55..74
            55,56,57,58,59,60,61,62,63,64,65,66,67,68,69,70,71,72,73,74,

            // Right Hand Fingers (slots 69..88) 75..94
            75,76,77,78,79,80,81,82,83,84,85,86,87,88,89,90,91,92,93,94,
        };

        // per-slot precision (true = 8-bit; false = 16-bit) mirrors your SetCompressedUshort asByte flags
        static readonly bool[] IS_BYTE = new bool[]
        {
            // Spine/Chest/Head (0..14) -> 6,7,8 true; rest false
            false,false,false,false,false,false, true, true, true, false,false,false,false,false,false,

            // Left Leg (15..22): only 27 true
            false,false,false,false,false,false, true, false,

            // Right Leg (23..30): only 35 true
            false,false,false,false,false,false, true, false,

            // Left Arm (31..39): all false
            false,false,false,false,false,false,false,false,false,

            // Right Arm (40..48): all false
            false,false,false,false,false,false,false,false,false,

            // Left Hand Fingers (49..68)
            false,false, true, true,  // 55,56,57,58
            false, true, true, true,  // 59,60,61,62
            false, true, true, true,  // 63,64,65,66
            false, true, true, true,  // 67,68,69,70
            false, true, true, true,  // 71,72,73,74

            // Right Hand Fingers (69..88)
            false,false, true, true,  // 75,76,77,78
            false, true, true, true,  // 79,80,81,82
            false, true, true, true,  // 83,84,85,86
            false, true, true, true,  // 87,88,89,90
            false, true, true, true,  // 91,92,93,94
        };
        public static void Compress(BasisNetworkTransmitter transmitter, Animator animator)
        {
            transmitter.PoseHandler ??= new HumanPoseHandler(animator.avatar, animator.transform);

            EnsureInitialized(); // our compressor init

            // Get current pose from Animator
            transmitter.PoseHandler.GetHumanPose(ref transmitter.HumanPose);

            CompressAvatarData(transmitter.storedAvatarData, transmitter.HumanPose, animator);

            var data = transmitter.SendingOutAvatarData.Count == 0 ? null : transmitter.SendingOutAvatarData.Values.ToArray();

            transmitter.storedAvatarData.LASM.AdditionalAvatarDatas = data;

            transmitter.storedAvatarData.LASM.Serialize(transmitter.AvatarSendWriter);

            BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.LocalAvatarSync, transmitter.AvatarSendWriter.Length);

            BasisNetworkConnection.LocalPlayerPeer.Send(
                transmitter.AvatarSendWriter,
                BasisNetworkCommons.PlayerAvatarChannel,
                DeliveryMethod.Sequenced);

            transmitter.AvatarSendWriter.Reset();
            transmitter.ClearAdditional();
        }

        public static void InitalAvatarData(Animator animator, out BasisStoredAvatarData StoredAvatarData)
        {
            EnsureInitialized();

            var poseHandler = new HumanPoseHandler(animator.avatar, animator.transform);
            var humanPose = new HumanPose();
            poseHandler.GetHumanPose(ref humanPose);

            StoredAvatarData = new BasisStoredAvatarData();
            CompressAvatarData(StoredAvatarData, humanPose, animator);
        }

        [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Low)]
        public static void CompressAvatarData(BasisStoredAvatarData AvatarData, HumanPose pose, Animator animator)
        {
            EnsureInitialized();

            int offset = 0;

            // Position
            BasisUnityBitPackerExtensionsUnsafe.WritePosition(
                animator.bodyPosition, ref AvatarData.LASM.array, ref offset);

            // Rotation
            BasisUnityBitPackerExtensionsUnsafe.WriteQuaternionToBytes(
                animator.bodyRotation, ref AvatarData.LASM.array, ref offset, BasisNetworkPlayer.RotationCompression);

            // Muscles (parallel, zero-GC)
            CompressAvatarMuscles_Parallel(ref pose, ref AvatarData.LASM, ref offset);

            // Scale
            CompressScale(animator.transform.localScale.y, ref AvatarData.LASM, ref offset);
        }
        public static byte[] OutGoingBytes;
        // ==============================
        // new hot path: parallel compressor
        // ==============================
        public static void CompressAvatarMuscles_Parallel(ref HumanPose pose, ref LocalAvatarSyncMessage message, ref int offset)
        {
            // copy pose.muscles -> persistent native (no GC)
            EnsureMusclesBuffer(UnityMuscleCount);

            unsafe
            {
                fixed (float* src = pose.muscles)
                {
                    UnsafeUtility.MemCpy(
                        sMusclesNative.GetUnsafePtr(),
                        src,
                        sizeof(float) * UnityMuscleCount);
                }
            }

            // launch job: each slot writes to its own offset in sPacked (no races)
            var job = new QuantizeWriteJob
            {
                Muscles = sMusclesNative,
                Min = sMin,
                Inv = sInv,
                Max = sMax,

                Order = sOrder,
                IsByte = sIsByte,
                Offsets = sOffsets,

                OutBytes = sPacked
            };

            var handle = job.Schedule(sOrder.Length, 32);
            handle.Complete();

            // copy packed into final message buffer (NO managed array hop)
            unsafe
            {
                void* srcPtr = sPacked.GetUnsafeReadOnlyPtr();
                fixed (byte* dst = message.array)
                {
                    UnsafeUtility.MemCpy(dst + offset, srcPtr, sPackedSize);
                }
            }
            offset += sPackedSize;
        }

        // ==============================
        // shared utils (unchanged)
        // ==============================
        public static void CompressScale(float scale, ref LocalAvatarSyncMessage message, ref int offset)
        {
            const float Min = 0.005f;
            const float Max = 150f;
            const float range = Max - Min;

            float clamped = math.clamp(scale, Min, Max);
            float normalized = (clamped - Min) / range;

            ushort compressed = (ushort)(normalized * BasisMuscleRange.UShortRangeDifference);
            BasisUnityBitPackerExtensionsUnsafe.WriteUShort(compressed, ref message.array, ref offset);
        }

        // ==============================
        // initialization / disposal
        // ==============================
        static void EnsureInitialized()
        {
            if (sInitialized) return;

            // 1) load BasisMuscleRange into managed LUTs
            var minT = BasisMuscleRange.MinMuscle;   // length 95
            var rangeT = BasisMuscleRange.RangeMuscle; // length 95

            if (minT == null || rangeT == null || minT.Length != UnityMuscleCount || rangeT.Length != UnityMuscleCount)
            {
                Debug.LogError("[BasisNetworkAvatarCompressor] BasisMuscleRange tables invalid.");
                return;
            }

            sMinManaged = new float[UnityMuscleCount];
            sInvManaged = new float[UnityMuscleCount];
            sMaxManaged = new float[UnityMuscleCount];

            for (int i = 0; i < UnityMuscleCount; i++)
            {
                sMinManaged[i] = minT[i];
                float r = rangeT[i];
                sInvManaged[i] = (r <= 0f) ? 0f : 1f / r;
                sMaxManaged[i] = minT[i] + r;
            }

            // 2) build offsets and packed size from IS_BYTE
            sPackedSize = 0;
            var offs = new int[WRITE_ORDER.Length];
            for (int i = 0; i < WRITE_ORDER.Length; i++)
            {
                offs[i] = sPackedSize;
                sPackedSize += IS_BYTE[i] ? 1 : 2;
            }

            // 3) allocate persistent natives
            sOrder = new NativeArray<int>(WRITE_ORDER.Length, Allocator.Persistent);
            sIsByte = new NativeArray<byte>(WRITE_ORDER.Length, Allocator.Persistent);
            sOffsets = new NativeArray<int>(WRITE_ORDER.Length, Allocator.Persistent);

            sMin = new NativeArray<float>(UnityMuscleCount, Allocator.Persistent);
            sInv = new NativeArray<float>(UnityMuscleCount, Allocator.Persistent);
            sMax = new NativeArray<float>(UnityMuscleCount, Allocator.Persistent);

            sPacked = new NativeArray<byte>(sPackedSize, Allocator.Persistent);

            // 4) fill natives
            for (int i = 0; i < WRITE_ORDER.Length; i++)
            {
                sOrder[i] = WRITE_ORDER[i];
                sIsByte[i] = IS_BYTE[i] ? (byte)1 : (byte)0;
                sOffsets[i] = offs[i];
            }
            for (int i = 0; i < UnityMuscleCount; i++)
            {
                sMin[i] = sMinManaged[i];
                sInv[i] = sInvManaged[i];
                sMax[i] = sMaxManaged[i];
            }

            // 5) create persistent input buffer
            EnsureMusclesBuffer(UnityMuscleCount);

            sInitialized = true;

            // optional: log
            // Debug.Log($"[BasisNetworkAvatarCompressor] Init: slots={WRITE_ORDER.Length}, packed={sPackedSize} bytes");
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
            if (!sInitialized) return;

            if (sOrder.IsCreated) sOrder.Dispose();
            if (sIsByte.IsCreated) sIsByte.Dispose();
            if (sOffsets.IsCreated) sOffsets.Dispose();
            if (sMin.IsCreated) sMin.Dispose();
            if (sInv.IsCreated) sInv.Dispose();
            if (sMax.IsCreated) sMax.Dispose();
            if (sPacked.IsCreated) sPacked.Dispose();
            if (sMusclesNative.IsCreated) sMusclesNative.Dispose();

            sInitialized = false;
        }

        // ==============================
        // job
        // ==============================
        [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Low)]
        struct QuantizeWriteJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float> Muscles;   // index by Unity muscle index
            [ReadOnly] public NativeArray<float> Min;
            [ReadOnly] public NativeArray<float> Inv;
            [ReadOnly] public NativeArray<float> Max;

            [ReadOnly] public NativeArray<int> Order;      // slot -> muscle idx
            [ReadOnly] public NativeArray<byte> IsByte;     // slot -> 1/0
            [ReadOnly] public NativeArray<int> Offsets;    // slot -> byte offset

            [NativeDisableParallelForRestriction]
            public NativeArray<byte> OutBytes;              // shared packed buffer

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static byte Quant8(float x01) => (byte)math.round(x01 * 255f);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static ushort Quant16(float x01) => (ushort)math.round(x01 * 65535f);

            public void Execute(int slot)
            {
                int idx = Order[slot];

                float v = Muscles[idx];
                float min = Min[idx];
                float inv = Inv[idx];
                float max = Max[idx];

                float clamped = math.clamp(v, min, max);
                float norm = (inv == 0f) ? 0f : (clamped - min) * inv;

                int o = Offsets[slot];

                if (IsByte[slot] == 1)
                {
                    OutBytes[o] = Quant8(norm);
                }
                else
                {
                    ushort u = Quant16(norm);
                    OutBytes[o] = (byte)(u & 0xFF);
                    OutBytes[o + 1] = (byte)(u >> 8);
                }
            }
        }
    }
}
