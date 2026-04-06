using Basis.Network.Core;
using Basis.Network.Core.Compression;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking.Compression;
using Basis.Scripts.Networking.Transmitters;
using Basis.Scripts.Profiler;
using System.Linq;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;
using static Basis.Network.Core.Compression.BasisAvatarBitPacking;

namespace Basis.Scripts.Networking.NetworkedAvatar
{
    /// <summary>
    /// Compresses local avatar bone rotations as T-pose-relative deltas using
    /// "smallest three" quaternion encoding.
    ///
    /// ExtractBoneDeltas uses a TransformAccessArray to read bone local rotations,
    /// then a Burst-compiled IJob computes deltas and compresses the bitstream in
    /// a single pass (BasisBoneDeltaAndCompressJob).
    /// </summary>
    public static class BasisNetworkAvatarCompressor
    {
        static bool sInitialized;

        // Persistent T-pose local rotations captured during calibration (indexed by HumanBodyBones 0..54)
        static quaternion[] sTposeLocalRotations;

        // Scratch buffer for 54 delta quaternions (indexed by slot in BONE_WRITE_ORDER)
        static NativeArray<quaternion> sBoneDeltas;

        // Job system persistent arrays
        static TransformAccessArray sBoneTransformAccess;
        static NativeArray<quaternion> sCurrentLocalRotations;
        static NativeArray<quaternion> sTposeNative;
        static NativeArray<byte> sBpcNative;
        static NativeArray<float> sMaxComponentNative;
        static bool sJobArraysReady;

        // Wire quality is locked to HIGH
        static readonly BitQuality WireQuality = BitQuality.High;

        // Outbound sequence counter
        static byte sLocalSequence;

        /// <summary>
        /// Called during local avatar calibration to capture T-pose bone rotations.
        /// Must be called while the avatar is in T-pose.
        /// </summary>
        public static void CaptureTPose()
        {
            sTposeLocalRotations = new quaternion[55]; // HumanBodyBones 0..54
            for (int Index = 0; Index < 55; Index++)
            {
                if (BasisLocalAvatarDriver.Mapping.TposeLocal.TryGetValue((HumanBodyBones)Index, out var value))
                {
                    sTposeLocalRotations[Index] = value.rotation;
                }
                else
                {
                    sTposeLocalRotations[Index] = quaternion.identity;
                }
            }

            // Rebuild job arrays for the new avatar
            BuildJobArrays();
        }

        public static void Compress(BasisNetworkTransmitter transmitter, Animator animator)
        {
            Transform t = animator.transform;

            EnsureInitialized();
            // Extract deltas via main-thread Transform reads
            ExtractBoneDeltas();

            CompressAvatarData(transmitter.storedAvatarData, t);

            var data = transmitter.SendingOutAvatarData.Count == 0 ? null : transmitter.SendingOutAvatarData.Values.ToArray();
            transmitter.storedAvatarData.LASM.AdditionalAvatarDatas = data;
            transmitter.storedAvatarData.LASM.LinkedAvatarIndex = transmitter.LastLinkedAvatarIndex;

            bool hasAdditional = data != null && data.Length > 0;
            byte channel = BasisNetworkCommons.GetPlayerAvatarChannelForQuality((int)WireQuality, hasAdditional);

            transmitter.AvatarSendWriter.Put(sLocalSequence);
            unchecked { sLocalSequence++; }

            transmitter.storedAvatarData.LASM.SerializeForChannel(transmitter.AvatarSendWriter, WireQuality);

            BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.LocalAvatarSync, transmitter.AvatarSendWriter.Length);

            BasisNetworkConnection.LocalPlayerPeer.Send(transmitter.AvatarSendWriter, channel, DeliveryMethod.Unreliable);

            transmitter.AvatarSendWriter.Reset();
            transmitter.ClearAdditional();
        }

        public static void InitalAvatarData(Animator animator, out BasisStoredAvatarData StoredAvatarData)
        {
            EnsureInitialized();

            ExtractBoneDeltas();

            StoredAvatarData = new BasisStoredAvatarData();
            CompressAvatarData(StoredAvatarData, animator.transform);
        }

        static void CompressAvatarData(BasisStoredAvatarData AvatarData, Transform ScaleTransform)
        {
            int needed = BasisAvatarBitPacking.ConvertToSize(WireQuality);
            AvatarData.LASM.DataQualityLevel = (byte)WireQuality;
            AvatarData.LASM.array ??= new byte[needed];
            if (AvatarData.LASM.array.Length != needed)
            {
                AvatarData.LASM.array = new byte[needed];
            }

            // Clear the array (bone rotation packing ORs into bytes)
            System.Array.Clear(AvatarData.LASM.array, 0, needed);

            int offset = 0;
            if (BasisLocalAvatarDriver.Mapping.HasHips)
            {
                Transform hips = BasisLocalAvatarDriver.Mapping.Hips;

                hips.GetPositionAndRotation(out var hipsPos, out var hipsRot);

                // Position (hips world position)
                BasisUnityBitPackerExtensionsUnsafe.WritePosition(hipsPos, ref AvatarData.LASM.array, ref offset);

                // Bone rotations — use Burst job if arrays are ready, otherwise fallback
                if (sJobArraysReady)
                {
                    CompressBoneRotationsJobified(AvatarData.LASM.array, ref offset);
                }
                else
                {
                    BasisBoneRotationUtils.CompressBoneRotations(sBoneDeltas, WireQuality, AvatarData.LASM.array, ref offset);
                }

                // Scale
                BasisUnityBitPackerExtensionsUnsafe.CompressScale(ScaleTransform.localScale.y, ref AvatarData.LASM, ref offset);

                // Hips world rotation
                BasisUnityBitPackerExtensionsUnsafe.WriteCompressedQuaternionToBytes(hipsRot, ref AvatarData.LASM.array, ref offset);
            }
        }

        /// <summary>
        /// Runs the delta + compression as a Burst IJob. Copies the managed byte[] to a NativeArray,
        /// runs the job, then copies back. The Burst compilation of the encode loop is the win here.
        /// </summary>
        static unsafe void CompressBoneRotationsJobified(byte[] dst, ref int byteOffset)
        {
            int boneCount = BasisBoneRotationCompression.SyncBoneCount;
            int rotBytes = BasisBoneRotationCompression.RotationBytes(WireQuality);

            // Wrap the managed byte[] in a NativeArray without copy (pinned)
            // We need to copy the relevant region into a temp native array for the job
            var nativeDst = new NativeArray<byte>(dst.Length, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            NativeArray<byte>.Copy(dst, nativeDst);

            var job = new BasisBoneDeltaAndCompressJob
            {
                CurrentLocalRotations = sCurrentLocalRotations,
                TposeLocalRotations = sTposeNative,
                BitsPerComponent = sBpcNative,
                MaxComponent = sMaxComponentNative,
                OutputBuffer = nativeDst,
                RotationByteOffset = byteOffset,
                BoneCount = boneCount,
                BoneDeltas = sBoneDeltas,
            };

            job.Run(); // Burst-compiled, runs immediately on this thread

            // Copy result back to managed array
            NativeArray<byte>.Copy(nativeDst, dst);
            nativeDst.Dispose();

            byteOffset += rotBytes;
        }

        /// <summary>
        /// Extracts bone local rotations from the animator and computes
        /// T-pose-relative delta quaternions for each bone.
        /// </summary>
        static void ExtractBoneDeltas()
        {
            if (sTposeLocalRotations == null)
            {
                BasisDebug.LogError($"Missing {nameof(sTposeLocalRotations)}");
                return;
            }

            int boneCount = BasisBoneRotationCompression.SyncBoneCount;

            // Read current local rotations and compute deltas
            for (int slot = 0; slot < boneCount; slot++)
            {
                int boneEnum = BasisBoneRotationCompression.BONE_WRITE_ORDER[slot];

                if (BasisLocalAvatarDriver.Mapping.GetTransform((HumanBodyBones)boneEnum, out var bone))
                {
                    quaternion current = bone.localRotation;
                    sCurrentLocalRotations[slot] = current;

                    quaternion tpose = sTposeLocalRotations[boneEnum];
                    quaternion delta = math.mul(math.inverse(tpose), current);
                    sBoneDeltas[slot] = delta;
                }
                else
                {
                    sCurrentLocalRotations[slot] = quaternion.identity;
                    sBoneDeltas[slot] = quaternion.identity;
                }
            }
        }

        /// <summary>
        /// Builds persistent NativeArrays for the Burst compression job.
        /// Called once per avatar change (in CaptureTPose).
        /// </summary>
        static void BuildJobArrays()
        {
            DisposeJobArrays();

            int boneCount = BasisBoneRotationCompression.SyncBoneCount;
            byte[] bpcTable = BasisBoneRotationCompression.GetBpcTable(WireQuality);
            float[] maxComp = BasisBoneRotationCompression.MAX_COMPONENT;

            sCurrentLocalRotations = new NativeArray<quaternion>(boneCount, Allocator.Persistent);

            // T-pose rotations in BONE_WRITE_ORDER slot order
            sTposeNative = new NativeArray<quaternion>(boneCount, Allocator.Persistent);
            for (int slot = 0; slot < boneCount; slot++)
            {
                int boneEnum = BasisBoneRotationCompression.BONE_WRITE_ORDER[slot];
                sTposeNative[slot] = sTposeLocalRotations[boneEnum];
            }

            sBpcNative = new NativeArray<byte>(boneCount, Allocator.Persistent);
            NativeArray<byte>.Copy(bpcTable, sBpcNative, boneCount);

            sMaxComponentNative = new NativeArray<float>(boneCount, Allocator.Persistent);
            NativeArray<float>.Copy(maxComp, sMaxComponentNative, boneCount);

            sJobArraysReady = true;
        }

        static void DisposeJobArrays()
        {
            sJobArraysReady = false;
            if (sCurrentLocalRotations.IsCreated) sCurrentLocalRotations.Dispose();
            if (sTposeNative.IsCreated) sTposeNative.Dispose();
            if (sBpcNative.IsCreated) sBpcNative.Dispose();
            if (sMaxComponentNative.IsCreated) sMaxComponentNative.Dispose();
        }

        static void EnsureInitialized()
        {
            if (sInitialized)
            {
                return;
            }

            if (!sBoneDeltas.IsCreated)
            {
                sBoneDeltas = new NativeArray<quaternion>(BasisBoneRotationCompression.SyncBoneCount, Allocator.Persistent);
            }
            // Capture T-pose bone rotations for network compression (while still in T-pose)
            Networking.NetworkedAvatar.BasisNetworkAvatarCompressor.CaptureTPose();

            sInitialized = true;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void OnDomainReload()
        {
            Dispose();
        }

        public static void Dispose()
        {
            DisposeJobArrays();
            if (sBoneDeltas.IsCreated) sBoneDeltas.Dispose();
            sTposeLocalRotations = null;
            sInitialized = false;
        }
    }
}
