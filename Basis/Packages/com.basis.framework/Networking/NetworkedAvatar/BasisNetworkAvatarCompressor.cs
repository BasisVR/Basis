using Basis.Network.Core;
using Basis.Network.Core.Compression;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking.Compression;
using Basis.Scripts.Networking.Transmitters;
using Basis.Scripts.Profiler;
using Basis.Scripts.TransformBinders.BoneControl;
using Unity.Collections;
using static SerializableBasis;
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

        // Precomputed inverses of T-pose rotations — avoids math.inverse() per bone per frame
        static quaternion[] sInverseTposeLocalRotations;

        // Cached TPose local position for the hips bone — used to compute the
        // hips local-position delta we ship in the avatar packet tail. Captured
        // once per CaptureTPose so we don't hit the TposeLocal dictionary every frame.
        static Unity.Mathematics.float3 sTposeHipsLocalPos;

        // Cached inverse of the TPose hips local rotation — used to compute the
        // hips local-rotation delta (inverseTpose × currentLocal) per frame.
        // Hips isn't in the bone packet, so this is what carries hips orientation
        // independent of the root rotation.
        static quaternion sInverseTposeHipsLocalRot;

        // Scratch buffer for 54 delta quaternions (indexed by slot in BONE_WRITE_ORDER)
        static NativeArray<quaternion> sBoneDeltas;

        // Job system persistent arrays
        static TransformAccessArray sBoneTransformAccess;
        static NativeArray<int> sSlotRemap;
        static NativeArray<quaternion> sCurrentLocalRotations;
        static NativeArray<quaternion> sTposeNative;
        static NativeArray<byte> sBpcNative;
        static NativeArray<float> sMaxComponentNative;
        static bool sJobArraysReady;

        // Persistent NativeArray for jobified compression output — avoids per-frame TempJob allocation.
        static NativeArray<byte> sJobOutputBuffer;

        // Wire quality is locked to HIGH
        static readonly BitQuality WireQuality = BitQuality.High;

        // Outbound sequence counter
        static byte sLocalSequence;

        // Idle suppression: the last payload we actually emitted. A freshly-encoded frame that is
        // byte-identical to this (and carries no additional data) reconstructs to the same pose on
        // every receiver, so it is dropped instead of sent. Lossless — see BasisAvatarIdleSuppression.
        // Reset on avatar change so a new rig always sends its first frame.
        static byte[] sLastSentPayload;
        static int sLastSentLinkedIndex = -1;
        static double sLastSentTime;
        static bool sHasLastSent;
        public static double IdleHeartbeatSeconds = BasisAvatarIdleSuppression.DefaultHeartbeatSeconds;

        // Deadband suppression: RAW (pre-quantization) values of the last frame actually sent,
        // flattened into one float[]. VR sensor noise crosses quantization steps every frame, so
        // byte-identical suppression alone never fires in VR; a frame whose every field moved less
        // than the sub-visibility thresholds in BasisAvatarDeadband is dropped instead. Comparing
        // against the last SENT values bounds the standing error to the threshold (drift sends).
        public static bool DeadbandSuppression = true;
        const int RawBoneOffset = 0;                                   // 51 quats
        const int RawPosOffset = RawBoneOffset + 51 * 4;               // hips world pos
        const int RawBodyRotOffset = RawPosOffset + 3;                 // hips world rot
        const int RawHipsDeltaOffset = RawBodyRotOffset + 4;           // hips local delta
        const int RawHipsRotOffset = RawHipsDeltaOffset + 3;           // hips local rot delta
        const int RawEffPosOffset = RawHipsRotOffset + 4;              // 4 effector positions
        const int RawEffRotOffset = RawEffPosOffset + 12;              // 4 effector rotations
        const int RawScaleOffset = RawEffRotOffset + 16;               // scale
        const int RawMaskOffset = RawScaleOffset + 1;                  // effector mask (exact)
        const int RawFloatCount = RawMaskOffset + 1;
        static readonly float[] sRawCurrent = new float[RawFloatCount];
        static float[] sRawLastSent;
        static bool sRawCaptured;
        static bool sHasRawLastSent;
        static int sLastP2PSessionCount;
        static readonly double sBoneMinAbsDot = BasisAvatarDeadband.MinAbsDotForAngleDegrees(BasisAvatarDeadband.BoneAngleDegrees);
        static readonly double sRootMinAbsDot = BasisAvatarDeadband.MinAbsDotForAngleDegrees(BasisAvatarDeadband.RootAngleDegrees);

        // Uplink delta state (v42): the last full keyframe this client sent — server and P2P peers
        // all rebaseline from it, deltas in between reference its sequence as baseSeq.
        public const double UplinkKeyframeIntervalSeconds = 0.5;
        static byte[] sUplinkBaseline;
        static byte sUplinkBaselineSeq;
        static bool sHasUplinkBaseline;
        static double sUplinkLastKeyframeTime;
        static byte[] sUplinkDeltaScratch;
        static volatile bool sUplinkForceKeyframe;

        /// <summary>
        /// Force the next avatar send to be a full keyframe. Called from the DeltaAvatarChannel
        /// control path when the server or a P2P peer reports a missing uplink baseline (any
        /// receive thread — the flag is volatile and consumed on the main thread).
        /// </summary>
        public static void ForceUplinkKeyframe() => sUplinkForceKeyframe = true;

        // Cached array for additional avatar data — avoids .ToArray() allocation per frame.
        static AdditionalAvatarData[] sCachedAdditionalData;

        /// <summary>
        /// Called during local avatar calibration to capture T-pose bone rotations.
        /// Must be called while the avatar is in T-pose.
        /// </summary>
        public static void CaptureTPose()
        {
            // Force the next frame to send; the new rig's rest pose differs from the old baseline.
            sHasLastSent = false;
            sHasRawLastSent = false;
            sRawCaptured = false;
            sHasUplinkBaseline = false;
            sUplinkForceKeyframe = true;
            sTposeLocalRotations = new quaternion[55]; // HumanBodyBones 0..54
            sInverseTposeLocalRotations = new quaternion[55];
            for (int Index = 0; Index < 55; Index++)
            {
                if (BasisLocalAvatarDriver.Mapping.TposeLocal.TryGetValue((HumanBodyBones)Index, out var value))
                {
                    sTposeLocalRotations[Index] = value.rotation;
                    sInverseTposeLocalRotations[Index] = math.inverse(value.rotation);
                }
                else
                {
                    sTposeLocalRotations[Index] = quaternion.identity;
                    sInverseTposeLocalRotations[Index] = quaternion.identity;
                }
            }

            if (BasisLocalAvatarDriver.Mapping.TposeLocal.TryGetValue(HumanBodyBones.Hips, out var hipsTpose))
            {
                sTposeHipsLocalPos = hipsTpose.position;
                sInverseTposeHipsLocalRot = math.inverse((quaternion)hipsTpose.rotation);
            }
            else
            {
                sTposeHipsLocalPos = Unity.Mathematics.float3.zero;
                sInverseTposeHipsLocalRot = quaternion.identity;
            }

            // Rebuild job arrays for the new avatar
            BuildJobArrays();
        }

        public static void Compress(BasisNetworkTransmitter transmitter, Animator animator, double timeAsDouble)
        {
            Transform t = animator.transform;

            EnsureInitialized();
            // Read bone local rotations via job (or fallback to main thread)
            ReadBoneTransforms();

            CompressAvatarData(transmitter.storedAvatarData, t);

            int additionalCount = transmitter.SendingOutAvatarData.Count;
            AdditionalAvatarData[] data;
            if (additionalCount == 0)
            {
                data = null;
            }
            else
            {
                if (sCachedAdditionalData == null || sCachedAdditionalData.Length != additionalCount)
                    sCachedAdditionalData = new AdditionalAvatarData[additionalCount];
                transmitter.SendingOutAvatarData.Values.CopyTo(sCachedAdditionalData, 0);
                data = sCachedAdditionalData;
            }
            transmitter.storedAvatarData.LASM.AdditionalAvatarDatas = data;
            transmitter.storedAvatarData.LASM.LinkedAvatarIndex = transmitter.LastLinkedAvatarIndex;

            // Must mirror SerializeAdditionalOnly's guard exactly: with >255 entries the serializer
            // writes nothing, so claiming an additional section via the channel/header would desync
            // the receiver's parse. (256 is reachable — SendingOutAvatarData is byte-keyed.)
            bool hasAdditional = data != null && data.Length > 0 && data.Length <= 255;
            byte channel = BasisNetworkCommons.GetPlayerAvatarChannelForQuality((int)WireQuality, hasAdditional);

            // Drop redundant, additional-free frames (a still avatar) until the heartbeat is due:
            // byte-identical frames are pure wire redundancy, and frames whose RAW values all sit
            // inside the sub-quantization deadband are sensor noise — the same shimmer receivers
            // low-pass away. The receiver holds the last snapshot until real motion resumes.
            bool linkedChanged = transmitter.LastLinkedAvatarIndex != sLastSentLinkedIndex;
            // A P2P session appearing (or dropping) forces a send — and a full keyframe — so the
            // new peer gets a starting pose/baseline immediately instead of waiting out the idle
            // heartbeat or the keyframe cadence.
            int p2pSessions = Basis.Scripts.Networking.BasisP2PManager.GetConnectedSessionCount();
            bool p2pTopologyChanged = p2pSessions != sLastP2PSessionCount;
            sLastP2PSessionCount = p2pSessions;
            if (p2pTopologyChanged) sUplinkForceKeyframe = true;

            bool mustSend = p2pTopologyChanged || BasisAvatarIdleSuppression.ShouldSend(
                    transmitter.storedAvatarData.LASM.array,
                    sLastSentPayload,
                    sHasLastSent,
                    hasAdditional,
                    linkedChanged,
                    timeAsDouble,
                    sLastSentTime,
                    IdleHeartbeatSeconds);
            if (mustSend && !p2pTopologyChanged && DeadbandSuppression
                && !hasAdditional && !linkedChanged && sHasLastSent
                && sRawCaptured && sHasRawLastSent
                && timeAsDouble - sLastSentTime < IdleHeartbeatSeconds
                && RawPoseWithinDeadband())
            {
                mustSend = false;
            }
            if (!mustSend)
            {
                transmitter.AvatarSendWriter.Reset();
                transmitter.ClearAdditional();
                return;
            }
            RecordLastSent(transmitter.storedAvatarData.LASM.array, transmitter.LastLinkedAvatarIndex, timeAsDouble);

            byte seq = sLocalSequence;
            unchecked { sLocalSequence++; }

            // Uplink delta decision (v42): a full keyframe every UplinkKeyframeIntervalSeconds (and
            // whenever forced/promoted), deltas against it in between. The same frame feeds the
            // server and every P2P peer, so one baseline serves all receivers; anyone who misses a
            // keyframe requests one via the DeltaAvatarChannel control frame.
            byte[] payload = transmitter.storedAvatarData.LASM.array;
            int payloadLen = payload.Length;
            bool uplinkDeltasAllowed = BasisNetworkManagement.ServerMetaDataMessage.UplinkDeltaEnabled;

            bool keyframe = !uplinkDeltasAllowed
                || sUplinkForceKeyframe
                || !sHasUplinkBaseline
                || sUplinkBaseline == null
                || sUplinkBaseline.Length != payloadLen
                || timeAsDouble - sUplinkLastKeyframeTime >= UplinkKeyframeIntervalSeconds;

            int deltaLen = -1;
            if (!keyframe)
            {
                int cap = BasisAvatarDeltaCompression.MaxDeltaSize(WireQuality);
                if (sUplinkDeltaScratch == null || sUplinkDeltaScratch.Length < cap)
                    sUplinkDeltaScratch = new byte[cap];
                deltaLen = BasisAvatarDeltaCompression.BuildDelta(sUplinkBaseline, payload, WireQuality, sUplinkDeltaScratch, 0);
                // Promotion: a delta that isn't smaller than the keyframe is pointless overhead.
                if (deltaLen < 0 || deltaLen >= payloadLen) keyframe = true;
            }

            if (hasAdditional)
            {
                System.Threading.Interlocked.Increment(ref BasisAdditionalDataDiagnostics.SenderFramesWithAdditional);
                if (keyframe) System.Threading.Interlocked.Increment(ref BasisAdditionalDataDiagnostics.SenderFramesKeyframe);
                else System.Threading.Interlocked.Increment(ref BasisAdditionalDataDiagnostics.SenderFramesDelta);
            }

            byte sendChannel;
            if (keyframe)
            {
                transmitter.AvatarSendWriter.Put(seq);
                transmitter.storedAvatarData.LASM.SerializeForChannel(transmitter.AvatarSendWriter, WireQuality);
                sendChannel = channel;
                if (uplinkDeltasAllowed)
                {
                    if (sUplinkBaseline == null || sUplinkBaseline.Length != payloadLen)
                        sUplinkBaseline = new byte[payloadLen];
                    System.Array.Copy(payload, sUplinkBaseline, payloadLen);
                    sUplinkBaselineSeq = seq;
                    sHasUplinkBaseline = true;
                    sUplinkLastKeyframeTime = timeAsDouble;
                    sUplinkForceKeyframe = false;
                }
            }
            else
            {
                transmitter.AvatarSendWriter.Put(BasisNetworkCommons.BuildDeltaHeader((int)WireQuality, hasAdditional, false));
                transmitter.AvatarSendWriter.Put(seq);
                transmitter.AvatarSendWriter.Put(sUplinkBaselineSeq);
                transmitter.AvatarSendWriter.Put(sUplinkDeltaScratch, 0, deltaLen);
                if (hasAdditional) transmitter.storedAvatarData.LASM.SerializeAdditionalOnly(transmitter.AvatarSendWriter);
                sendChannel = BasisNetworkCommons.DeltaAvatarChannel;
            }

            BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.LocalAvatarSync, transmitter.AvatarSendWriter.Length);
            Basis.Scripts.Networking.BasisP2PManager.BroadcastAvatarViaP2P(transmitter.AvatarSendWriter, sendChannel);
            // Server-send pacing: when a P2P session is active, Compress fires at the
            // fast P2P cadence (e.g. every 17ms) and we throttle the server fan-out to
            // meta.SyncInterval. Without P2P, Simulate already paces this call to
            // meta.SyncInterval (see UpdateSendInterval), so an additional gate just
            // ends up dropping packets when frame-rate jitter pushes two consecutive
            // Simulate fires <100ms apart — the gate skips the second one and the
            // receiver sees a sequence gap (avatar fast-forwards mid-window).
            bool p2pActive = Basis.Scripts.Networking.BasisP2PManager.HasAnyConnectedSession();
            bool shouldSendServer;
            if (p2pActive)
            {
                // Keyframes always reach the server — its uplink baseline must track ours even
                // when the fast P2P cadence is throttling ordinary frames down to SyncInterval.
                shouldSendServer = keyframe || timeAsDouble >= sNextServerSendTime;
            }
            else
            {
                shouldSendServer = true;
            }

            if (shouldSendServer)
            {
                BasisNetworkConnection.LocalPlayerPeer.Send(transmitter.AvatarSendWriter, sendChannel, DeliveryMethod.Unreliable);
                BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.OutboundAvatarServer, transmitter.AvatarSendWriter.Length);
                if (p2pActive)
                {
                    var meta = BasisNetworkManagement.ServerMetaDataMessage;
                    double serverIntervalSec = meta.SyncInterval > 0
                        ? meta.SyncInterval / 1000.0
                        : 0.050;
                    sNextServerSendTime = timeAsDouble + serverIntervalSec;
                }
            }

            transmitter.AvatarSendWriter.Reset();
            transmitter.ClearAdditional();
        }

        static void RecordLastSent(byte[] payload, int linkedIndex, double timeAsDouble)
        {
            int plen = payload.Length;
            if (sLastSentPayload == null || sLastSentPayload.Length != plen)
                sLastSentPayload = new byte[plen];
            System.Array.Copy(payload, sLastSentPayload, plen);
            sLastSentLinkedIndex = linkedIndex;
            sLastSentTime = timeAsDouble;
            sHasLastSent = true;

            if (sRawCaptured)
            {
                sRawLastSent ??= new float[RawFloatCount];
                System.Array.Copy(sRawCurrent, sRawLastSent, RawFloatCount);
                sHasRawLastSent = true;
            }
            else
            {
                sHasRawLastSent = false;
            }
        }

        private static double sNextServerSendTime;

        public static void InitialAvatarData(Animator animator, out BasisStoredAvatarData StoredAvatarData)
        {
            EnsureInitialized();

            ReadBoneTransforms();

            StoredAvatarData = new BasisStoredAvatarData();
            CompressAvatarData(StoredAvatarData, animator.transform);
        }

        // End-effector capture scratch (reused each frame; rot pre-seeded to identity so the codec
        // never encodes a zero quaternion for unanchored slots).
        static readonly Unity.Mathematics.float3[] sEffectorPos = new Unity.Mathematics.float3[BasisAvatarEndEffectors.EffectorCount];
        static readonly quaternion[] sEffectorRot = { quaternion.identity, quaternion.identity, quaternion.identity, quaternion.identity };

        /// <summary>
        /// Fills the effector scratch arrays with hips-local target offset + tip world rotation
        /// for each world-anchored effector and returns the mask (bit i set = effector i anchored).
        /// An effector is anchored only when the sender's own copy is genuinely world-stable (a held
        /// controller/tracker or a planted foot) — otherwise FK is left alone so emotes/poses survive.
        /// Returns 0 (inert) until the per-effector rig seams are wired; see CaptureEndEffectors below.
        /// </summary>
        static byte CaptureEndEffectors(Unity.Mathematics.float3 hipsWorldPos, quaternion hipsWorldRot, float scale)
        {
            byte mask = 0;
            if (TryCaptureEffector(0, BasisLocalBoneDriver.LeftHandControl, HumanBodyBones.LeftHand, hipsWorldPos, hipsWorldRot)) mask |= 1 << 0;
            if (TryCaptureEffector(1, BasisLocalBoneDriver.RightHandControl, HumanBodyBones.RightHand, hipsWorldPos, hipsWorldRot)) mask |= 1 << 1;
            if (TryCaptureEffector(2, BasisLocalBoneDriver.LeftFootControl, HumanBodyBones.LeftFoot, hipsWorldPos, hipsWorldRot)) mask |= 1 << 2;
            if (TryCaptureEffector(3, BasisLocalBoneDriver.RightFootControl, HumanBodyBones.RightFoot, hipsWorldPos, hipsWorldRot)) mask |= 1 << 3;
            return mask;
        }

        static bool TryCaptureEffector(int idx, BasisLocalBoneControl tipControl, HumanBodyBones tipBone,
            Unity.Mathematics.float3 hipsWorldPos, quaternion hipsWorldRot)
        {
            if (tipControl == null || tipControl.HasTracked != BasisHasTracked.HasTracker) return false;
            if (!BasisLocalAvatarDriver.Mapping.GetTransform(tipBone, out var tip)) return false;

            tip.GetPositionAndRotation(out var tipWorldPos, out var tipWorldRot);
            Unity.Mathematics.float3 tipPos = tipWorldPos;

            sEffectorPos[idx] = math.mul(math.conjugate(hipsWorldRot), tipPos - hipsWorldPos);
            sEffectorRot[idx] = tipWorldRot;
            return true;
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
                // Send the HIPS world pose in the high-precision Position/Rotation
                // slots — hips is what's visually anchored and what the server's
                // reduction system uses for distance/quality decisions. Root is
                // derived on the receiver from this pose plus the hips local
                // delta channels (see BulkCopyHipsAndDeriveJob in the receiver).
                BasisLocalAvatarDriver.Mapping.Hips.GetPositionAndRotation(out var hipsWorldPos, out var hipsWorldRot);

                // Position (hips world position)
                BasisUnityBitPackerExtensionsUnsafe.WritePosition(hipsWorldPos, ref AvatarData.LASM.array, ref offset);

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
                BasisUnityBitPackerExtensionsUnsafe.WriteCompressedQuaternionToBytes(hipsWorldRot, ref AvatarData.LASM.array, ref offset);

                // Hips local-position delta (vs TPose). Lets remotes track seated
                // mode and other locally-driven hips overrides on top of the root sync.
                Unity.Mathematics.float3 hipsLocalNow = BasisLocalAvatarDriver.Mapping.Hips.localPosition;
                var hipsDelta = hipsLocalNow - sTposeHipsLocalPos;
                Compression.float3 Insert = new Compression.float3
                {
                    x = hipsDelta.x,
                    y = hipsDelta.y,
                    z = hipsDelta.z
                };
                BasisUnityBitPackerExtensionsUnsafe.CompressHipsDelta(Insert, ref AvatarData.LASM.array, ref offset);

                // Hips local-rotation delta (vs TPose). Carries hips orientation
                // since Hips is excluded from the bone packet (BONE_WRITE_ORDER).
                // delta = inverse(tposeLocalRot) × currentLocalRot — receiver
                // recovers currentLocal as tposeLocalRot × delta.
                quaternion hipsLocalRotNow = BasisLocalAvatarDriver.Mapping.Hips.localRotation;
                quaternion hipsRotDelta = math.mul(sInverseTposeHipsLocalRot, hipsLocalRotNow);
                BasisUnityBitPackerExtensionsUnsafe.WriteCompressedQuaternionToBytes(hipsRotDelta, ref AvatarData.LASM.array, ref offset);

                // End-effector anchoring block (High only). CaptureEndEffectors fills the scratch
                // arrays + returns the anchored mask from the local rig; the receiver two-bone-IKs the
                // anchored limbs to these targets. Mask 0 (nothing world-stable) makes it inert.
                byte effectorMask = CaptureEndEffectors(hipsWorldPos, hipsWorldRot, ScaleTransform.localScale.y);
                BasisAvatarEndEffectors.Encode(AvatarData.LASM.array, offset, effectorMask, sEffectorPos, sEffectorRot);
                offset += BasisAvatarEndEffectors.BlockBytes;

                CaptureRawPose(hipsWorldPos, hipsWorldRot, hipsDelta, hipsRotDelta, ScaleTransform.localScale.y, effectorMask);
            }
            else
            {
                sRawCaptured = false;
            }
        }

        /// <summary>
        /// Snapshots this frame's RAW (pre-quantization) pose values for the deadband comparison.
        /// Unanchored effector slots keep their stale scratch values on both sides of the compare,
        /// so they can never block suppression; a mask change always forces a send.
        /// </summary>
        static void CaptureRawPose(Unity.Mathematics.float3 hipsWorldPos, quaternion hipsWorldRot,
            Unity.Mathematics.float3 hipsDelta, quaternion hipsRotDelta, float scale, byte effectorMask)
        {
            if (!sCurrentLocalRotations.IsCreated)
            {
                sRawCaptured = false;
                return;
            }
            float[] raw = sRawCurrent;
            int boneCount = BasisBoneRotationCompression.SyncBoneCount;
            for (int slot = 0; slot < boneCount; slot++)
            {
                float4 v = sCurrentLocalRotations[slot].value;
                int o = RawBoneOffset + slot * 4;
                raw[o] = v.x; raw[o + 1] = v.y; raw[o + 2] = v.z; raw[o + 3] = v.w;
            }
            raw[RawPosOffset] = hipsWorldPos.x; raw[RawPosOffset + 1] = hipsWorldPos.y; raw[RawPosOffset + 2] = hipsWorldPos.z;
            float4 br = hipsWorldRot.value;
            raw[RawBodyRotOffset] = br.x; raw[RawBodyRotOffset + 1] = br.y; raw[RawBodyRotOffset + 2] = br.z; raw[RawBodyRotOffset + 3] = br.w;
            raw[RawHipsDeltaOffset] = hipsDelta.x; raw[RawHipsDeltaOffset + 1] = hipsDelta.y; raw[RawHipsDeltaOffset + 2] = hipsDelta.z;
            float4 hr = hipsRotDelta.value;
            raw[RawHipsRotOffset] = hr.x; raw[RawHipsRotOffset + 1] = hr.y; raw[RawHipsRotOffset + 2] = hr.z; raw[RawHipsRotOffset + 3] = hr.w;
            for (int e = 0; e < BasisAvatarEndEffectors.EffectorCount; e++)
            {
                int po = RawEffPosOffset + e * 3;
                raw[po] = sEffectorPos[e].x; raw[po + 1] = sEffectorPos[e].y; raw[po + 2] = sEffectorPos[e].z;
                float4 er = sEffectorRot[e].value;
                int ro = RawEffRotOffset + e * 4;
                raw[ro] = er.x; raw[ro + 1] = er.y; raw[ro + 2] = er.z; raw[ro + 3] = er.w;
            }
            raw[RawScaleOffset] = scale;
            raw[RawMaskOffset] = effectorMask;
            sRawCaptured = true;
        }

        /// <summary>
        /// True when every raw field of this frame is within its sub-visibility deadband of the
        /// last frame actually sent (see BasisAvatarDeadband for the thresholds).
        /// </summary>
        static bool RawPoseWithinDeadband()
        {
            float[] cur = sRawCurrent;
            float[] last = sRawLastSent;
            if (cur[RawMaskOffset] != last[RawMaskOffset]) return false;
            System.ReadOnlySpan<float> c = cur, l = last;
            if (!BasisAvatarDeadband.ValuesWithin(c.Slice(RawScaleOffset, 1), l.Slice(RawScaleOffset, 1), BasisAvatarDeadband.ScaleUnits)) return false;
            if (!BasisAvatarDeadband.ValuesWithin(c.Slice(RawPosOffset, 3), l.Slice(RawPosOffset, 3), BasisAvatarDeadband.PositionMeters)) return false;
            if (!BasisAvatarDeadband.ValuesWithin(c.Slice(RawHipsDeltaOffset, 3), l.Slice(RawHipsDeltaOffset, 3), BasisAvatarDeadband.HipsDeltaMeters)) return false;
            if (!BasisAvatarDeadband.QuatsWithin(c.Slice(RawBodyRotOffset, 4), l.Slice(RawBodyRotOffset, 4), sRootMinAbsDot)) return false;
            if (!BasisAvatarDeadband.QuatsWithin(c.Slice(RawHipsRotOffset, 4), l.Slice(RawHipsRotOffset, 4), sRootMinAbsDot)) return false;
            if (!BasisAvatarDeadband.QuatsWithin(c.Slice(RawBoneOffset, 51 * 4), l.Slice(RawBoneOffset, 51 * 4), sBoneMinAbsDot)) return false;
            if (!BasisAvatarDeadband.ValuesWithin(c.Slice(RawEffPosOffset, 12), l.Slice(RawEffPosOffset, 12), BasisAvatarDeadband.EffectorPositionMeters)) return false;
            if (!BasisAvatarDeadband.QuatsWithin(c.Slice(RawEffRotOffset, 16), l.Slice(RawEffRotOffset, 16), sBoneMinAbsDot)) return false;
            return true;
        }

        /// <summary>
        /// Runs the delta + compression as a Burst IJob. Copies the managed byte[] to a NativeArray,
        /// runs the job, then copies back. The Burst compilation of the encode loop is the win here.
        /// </summary>
        static unsafe void CompressBoneRotationsJobified(byte[] dst, ref int byteOffset)
        {
            int boneCount = BasisBoneRotationCompression.SyncBoneCount;
            int rotBytes = BasisBoneRotationCompression.RotationBytes(WireQuality);

            // Reuse persistent NativeArray to avoid per-frame TempJob allocation + deallocation.
            if (!sJobOutputBuffer.IsCreated || sJobOutputBuffer.Length < dst.Length)
            {
                if (sJobOutputBuffer.IsCreated) sJobOutputBuffer.Dispose();
                sJobOutputBuffer = new NativeArray<byte>(dst.Length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }
            NativeArray<byte>.Copy(dst, sJobOutputBuffer, dst.Length);

            var job = new BasisBoneDeltaAndCompressJob
            {
                CurrentLocalRotations = sCurrentLocalRotations,
                TposeLocalRotations = sTposeNative,
                BitsPerComponent = sBpcNative,
                MaxComponent = sMaxComponentNative,
                OutputBuffer = sJobOutputBuffer,
                RotationByteOffset = byteOffset,
                BoneCount = boneCount,
                BoneDeltas = sBoneDeltas,
            };

            job.Run(); // Burst-compiled, runs immediately on this thread

            // Copy result back to managed array
            NativeArray<byte>.Copy(sJobOutputBuffer, 0, dst, 0, dst.Length);

            byteOffset += rotBytes;
        }

        /// <summary>
        /// Reads bone local rotations into sCurrentLocalRotations.
        /// Uses IJobParallelForTransform when job arrays are ready, otherwise falls back
        /// to main-thread reads. Delta computation is handled by BasisBoneDeltaAndCompressJob.
        /// </summary>
        static void ReadBoneTransforms()
        {
            if (sJobArraysReady && sBoneTransformAccess.isCreated)
            {
                // Batch-read bone local rotations. RunReadOnly executes the Burst job on this
                // thread — the old Schedule().Complete() dispatched to a worker and blocked on
                // it in the same statement, paying the fence for zero overlap on ~51 bones.
                var readJob = new ReadBoneLocalRotationsJob
                {
                    SlotRemap = sSlotRemap,
                    CurrentLocalRotations = sCurrentLocalRotations,
                };
                readJob.RunReadOnly(sBoneTransformAccess);
            }
            else
            {
                // Fallback: main-thread reads (before job arrays are built)
                ExtractBoneDeltas();
            }
        }

        /// <summary>
        /// Fallback: reads bone local rotations and computes deltas on the main thread.
        /// Only used before job arrays are built. Also writes sBoneDeltas for the
        /// non-jobified compression path (BasisBoneRotationUtils.CompressBoneRotations).
        /// </summary>
        static void ExtractBoneDeltas()
        {
            if (sInverseTposeLocalRotations == null)
            {
                BasisDebug.LogError($"Missing {nameof(sTposeLocalRotations)}");
                return;
            }

            int boneCount = BasisBoneRotationCompression.SyncBoneCount;

            for (int slot = 0; slot < boneCount; slot++)
            {
                int boneEnum = BasisBoneRotationCompression.BONE_WRITE_ORDER[slot];

                if (BasisLocalAvatarDriver.Mapping.GetTransform((HumanBodyBones)boneEnum, out var bone))
                {
                    quaternion current = bone.localRotation;
                    sCurrentLocalRotations[slot] = current;

                    quaternion inverseTpose = sInverseTposeLocalRotations[boneEnum];
                    sBoneDeltas[slot] = math.mul(inverseTpose, current);
                }
                else
                {
                    sCurrentLocalRotations[slot] = sTposeLocalRotations[boneEnum];
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

            // Build TransformAccessArray for jobified bone reads.
            // Only valid transforms are added; missing bones get identity pre-filled.
            var validTransforms = new System.Collections.Generic.List<Transform>(boneCount);
            var validSlots = new System.Collections.Generic.List<int>(boneCount);
            for (int slot = 0; slot < boneCount; slot++)
            {
                int boneEnum = BasisBoneRotationCompression.BONE_WRITE_ORDER[slot];
                if (BasisLocalAvatarDriver.Mapping.GetTransform((HumanBodyBones)boneEnum, out var bone))
                {
                    validTransforms.Add(bone);
                    validSlots.Add(slot);
                }
                else
                {
                    // Pre-fill missing slots with T-pose so delta = identity
                    sCurrentLocalRotations[slot] = sTposeNative[slot];
                }
            }
            sBoneTransformAccess = new TransformAccessArray(validTransforms.ToArray());
            sSlotRemap = new NativeArray<int>(validSlots.Count, Allocator.Persistent);
            for (int i = 0; i < validSlots.Count; i++)
                sSlotRemap[i] = validSlots[i];

            sJobArraysReady = true;
        }

        static void DisposeJobArrays()
        {
            sJobArraysReady = false;
            if (sBoneTransformAccess.isCreated) sBoneTransformAccess.Dispose();
            if (sSlotRemap.IsCreated) sSlotRemap.Dispose();
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
            if (sJobOutputBuffer.IsCreated) sJobOutputBuffer.Dispose();
            sTposeLocalRotations = null;
            sLastSentPayload = null;
            sHasLastSent = false;
            sRawLastSent = null;
            sHasRawLastSent = false;
            sRawCaptured = false;
            sUplinkBaseline = null;
            sUplinkDeltaScratch = null;
            sHasUplinkBaseline = false;
            sUplinkForceKeyframe = false;
            sInitialized = false;
        }
    }
}
