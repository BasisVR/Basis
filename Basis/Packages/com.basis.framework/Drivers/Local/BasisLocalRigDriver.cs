using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.TransformBinders.BoneControl;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Animations.Rigging;
using UnityEngine.Playables;
using static Basis.Scripts.Avatar.BasisAvatarIKStageCalibration;
using static BasisHeightDriver;

namespace Basis.Scripts.Drivers
{
    /// <summary>
    /// Local rig driver that wires up Unity Animation Rigging constraints for a player avatar,
    /// filters tracker noise (One Euro Filter), and manually evaluates the rig graph each frame.
    /// Sets up spine, head, hands, feet, and toes, and toggles layers based on available rigs.
    /// </summary>
    [Serializable]
    public class BasisLocalRigDriver
    {
        /// <summary>
        /// Lower = more smoothing; Higher = more responsive. (0.01f, 10f)
        /// </summary>
        public static float MinCutoff = 5.5f;

        /// <summary>
        /// How much to raise cutoff when motion is fast (reduces lag during quick moves). (0f, 10f)
        /// </summary>
        public static float Beta = 3.25f;

        /// <summary>
        /// Cutoff for derivative smoothing. (0.01f, 10f)
        /// </summary>
        public static float DerivativeCutoff = 3f;

        /// <summary>
        /// Global smoothing strength multiplier (1-100). Divides MinCutoff and Hz values
        /// to amplify filtering. Higher = stronger smoothing but more latency.
        /// </summary>
        public static float SmoothingStrength = 1f;

        public RigBuilder Builder;
        public List<RigTransform> AdditionalTransforms = new List<RigTransform>();
        public PlayableGraph PlayableGraph;
        public Rig MainRig;
        public RigLayer RigLayer;
        public BasisFullBodyIK BasisFullIKConstraint;

        private BasisLocalPlayer localPlayer;
        private BasisTransformMapping basisTransformMapping;

        private static readonly IKOneEuroFilterQuaternion fRotHips = new IKOneEuroFilterQuaternion(MinCutoff, Beta, DerivativeCutoff);
        private static readonly IKOneEuroFilterQuaternion fRotHead = new IKOneEuroFilterQuaternion(MinCutoff, Beta, DerivativeCutoff);
        private static readonly IKOneEuroFilterQuaternion fRotLeftFoot = new IKOneEuroFilterQuaternion(MinCutoff, Beta, DerivativeCutoff);
        private static readonly IKOneEuroFilterQuaternion fRotRightFoot = new IKOneEuroFilterQuaternion(MinCutoff, Beta, DerivativeCutoff);
        private static readonly IKOneEuroFilterQuaternion fRotChest = new IKOneEuroFilterQuaternion(MinCutoff, Beta, DerivativeCutoff);
        private static readonly IKOneEuroFilterQuaternion fRotLeftLowerLeg = new IKOneEuroFilterQuaternion(MinCutoff, Beta, DerivativeCutoff);
        private static readonly IKOneEuroFilterQuaternion fRotRightLowerLeg = new IKOneEuroFilterQuaternion(MinCutoff, Beta, DerivativeCutoff);
        private static readonly IKOneEuroFilterQuaternion fRotLeftHand = new IKOneEuroFilterQuaternion(MinCutoff, Beta, DerivativeCutoff);
        private static readonly IKOneEuroFilterQuaternion fRotRightHand = new IKOneEuroFilterQuaternion(MinCutoff, Beta, DerivativeCutoff);
        private static readonly IKOneEuroFilterQuaternion fRotLeftLowerArm = new IKOneEuroFilterQuaternion(MinCutoff, Beta, DerivativeCutoff);
        private static readonly IKOneEuroFilterQuaternion fRotRightLowerArm = new IKOneEuroFilterQuaternion(MinCutoff, Beta, DerivativeCutoff);
        private static readonly IKOneEuroFilterQuaternion fRotLeftToe = new IKOneEuroFilterQuaternion(MinCutoff, Beta, DerivativeCutoff);
        private static readonly IKOneEuroFilterQuaternion fRotRightToe = new IKOneEuroFilterQuaternion(MinCutoff, Beta, DerivativeCutoff);
        private static readonly IKOneEuroFilterQuaternion fRotLeftShoulder = new IKOneEuroFilterQuaternion(MinCutoff, Beta, DerivativeCutoff);
        private static readonly IKOneEuroFilterQuaternion fRotRightShoulder = new IKOneEuroFilterQuaternion(MinCutoff, Beta, DerivativeCutoff);

        private static readonly OneEuroFilterVector3 fPosHips = new OneEuroFilterVector3(MinCutoff, Beta, DerivativeCutoff);
        private static readonly OneEuroFilterVector3 fPosHead = new OneEuroFilterVector3(MinCutoff, Beta, DerivativeCutoff);
        private static readonly OneEuroFilterVector3 fPosLeftFoot = new OneEuroFilterVector3(MinCutoff, Beta, DerivativeCutoff);
        private static readonly OneEuroFilterVector3 fPosRightFoot = new OneEuroFilterVector3(MinCutoff, Beta, DerivativeCutoff);
        private static readonly OneEuroFilterVector3 fPosChest = new OneEuroFilterVector3(MinCutoff, Beta, DerivativeCutoff);
        private static readonly OneEuroFilterVector3 fPosLeftLowerLeg = new OneEuroFilterVector3(MinCutoff, Beta, DerivativeCutoff);
        private static readonly OneEuroFilterVector3 fPosRightLowerLeg = new OneEuroFilterVector3(MinCutoff, Beta, DerivativeCutoff);
        private static readonly OneEuroFilterVector3 fPosLeftHand = new OneEuroFilterVector3(MinCutoff, Beta, DerivativeCutoff);
        private static readonly OneEuroFilterVector3 fPosRightHand = new OneEuroFilterVector3(MinCutoff, Beta, DerivativeCutoff);
        private static readonly OneEuroFilterVector3 fPosLeftLowerArm = new OneEuroFilterVector3(MinCutoff, Beta, DerivativeCutoff);
        private static readonly OneEuroFilterVector3 fPosRightLowerArm = new OneEuroFilterVector3(MinCutoff, Beta, DerivativeCutoff);
        private static readonly OneEuroFilterVector3 fPosLeftToe = new OneEuroFilterVector3(MinCutoff, Beta, DerivativeCutoff);
        private static readonly OneEuroFilterVector3 fPosRightToe = new OneEuroFilterVector3(MinCutoff, Beta, DerivativeCutoff);

        // Keep this order stable forever.
        // These indices drive your toggle arrays AND which filter instance is used.
        public const int S_Hips = 0;
        public const int S_Head = 1;
        public const int S_LeftFoot = 2;
        public const int S_RightFoot = 3;
        public const int S_Chest = 4;
        public const int S_LeftLowerLeg = 5;
        public const int S_RightLowerLeg = 6;
        public const int S_LeftHand = 7;
        public const int S_RightHand = 8;
        public const int S_LeftLowerArm = 9;
        public const int S_RightLowerArm = 10;
        public const int S_LeftToe = 11;
        public const int S_RightToe = 12;
        public const int S_LeftShoulder = 13;
        public const int S_RightShoulder = 14;

        public const int SlotCount = 15;

        // Smoothing enable toggles (position + rotation)
        public static bool[] SmoothPos = new bool[SlotCount];
        public static bool[] SmoothRot = new bool[SlotCount];

        // One Euro enable toggles (position + rotation)
        public static bool[] EuroPos = new bool[SlotCount];
        public static bool[] EuroRot = new bool[SlotCount];

        // Fallback smoothing when smoothing is ON but Euro is OFF
        [Range(0.01f, 60f)] public static float PositionSmoothingHz = 20f;
        [Range(0.01f, 60f)] public static float RotationSmoothingHz = 25f;

        public double timeAccumulator;

        public static Vector3 sPosHips, sPosHead, sPosLeftFoot, sPosRightFoot, sPosChest, sPosLeftLowerLeg, sPosRightLowerLeg;
        public static Vector3 sPosLeftHand, sPosRightHand, sPosLeftLowerArm, sPosRightLowerArm, sPosLeftToe, sPosRightToe;

        public static Quaternion sRotHips, sRotHead, sRotLeftFoot, sRotRightFoot, sRotChest, sRotLeftLowerLeg, sRotRightLowerLeg;
        public static Quaternion sRotLeftHand, sRotRightHand, sRotLeftLowerArm, sRotRightLowerArm, sRotLeftToe, sRotRightToe;
        public static Quaternion sRotLeftShoulder, sRotRightShoulder;

        public static bool hasFallbackState;

        // Smoothed knee hint rotations for foot-driver path (prevents upper leg snapping)
        private static Quaternion smoothedLeftKneeRot = Quaternion.identity;
        private static Quaternion smoothedRightKneeRot = Quaternion.identity;

        // Per-foot blend weights for transitioning IK in/out (0 = animation, 1 = foot driver)
        private static float footIKBlendWeightLeft = 0f;
        private static float footIKBlendWeightRight = 0f;
        private static float footIKBlendWeight = 0f; // max of left/right, used for hip bob
        private const float FootIKBlendInSpeed = 20f;  // ~50ms to fully engage
        private const float FootIKBlendOutSpeed = 15f; // ~67ms to fully disengage

        // Hysteresis: require stationary for this long before engaging foot IK.
        // Prevents single-frame flicker at jump apex or during speed oscillations.
        private static float stationaryTimer = 0f;
        private const float StationaryDelaySeconds = 0.15f;

        // Batched filter job state — one slot per S_* index (shoulder slot in position arrays is unused).
        private NativeArray<float3> _posInputs;
        private NativeArray<float3> _posOutputs;
        private NativeArray<quaternion> _rotInputs;
        private NativeArray<quaternion> _rotOutputs;
        private NativeArray<byte> _posModeNative;
        private NativeArray<byte> _rotModeNative;
        private NativeArray<float3> _fallbackPosStates;
        private NativeArray<quaternion> _fallbackRotStates;
        private NativeArray<BasisEuroVec3State> _euroPosStates;
        private NativeArray<BasisEuroQuatState> _euroRotStates;
        public void Initialize(BasisLocalPlayer localPlayer, BasisTransformMapping references)
        {
            this.localPlayer = localPlayer;
            basisTransformMapping = references;
            timeAccumulator = 0f;
        }
        public void BuildBuilder()
        {
            if (localPlayer?.BasisAvatar?.Animator == null || Builder == null)
            {
                BasisDebug.LogError("Missing Localplayer || Avatar || Animator || builder");
                return;
            }

            PlayableGraph = localPlayer.BasisAvatar.Animator.playableGraph;
            PlayableGraph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            Builder.Build(PlayableGraph);

            ResetSmoothingState();
        }

        public void SetBodySettings()
        {
            var rigGO = CreateOrGetRig("Main IK", true, out MainRig, out RigLayer);
            Spine(rigGO);
            BasisLocalBoneControl.HasEvents = true;
        }

        public void CleanupBeforeContinue()
        {
            DisposeFilterArrays();

            if (MainRig == null)
            {
                return;
            }

            GameObject.Destroy(MainRig.gameObject);
            MainRig = null;
            RigLayer = default;
        }

        private void EnsureFilterArrays()
        {
            if (_posInputs.IsCreated) return;
            _posInputs = new NativeArray<float3>(SlotCount, Allocator.Persistent);
            _posOutputs = new NativeArray<float3>(SlotCount, Allocator.Persistent);
            _rotInputs = new NativeArray<quaternion>(SlotCount, Allocator.Persistent);
            _rotOutputs = new NativeArray<quaternion>(SlotCount, Allocator.Persistent);
            _posModeNative = new NativeArray<byte>(SlotCount, Allocator.Persistent);
            _rotModeNative = new NativeArray<byte>(SlotCount, Allocator.Persistent);
            _fallbackPosStates = new NativeArray<float3>(SlotCount, Allocator.Persistent);
            _fallbackRotStates = new NativeArray<quaternion>(SlotCount, Allocator.Persistent);
            _euroPosStates = new NativeArray<BasisEuroVec3State>(SlotCount, Allocator.Persistent);
            _euroRotStates = new NativeArray<BasisEuroQuatState>(SlotCount, Allocator.Persistent);

            // quaternion default-constructs to all-zeros which isn't a valid rotation; seed to identity.
            for (int i = 0; i < SlotCount; i++)
            {
                _rotInputs[i] = quaternion.identity;
                _rotOutputs[i] = quaternion.identity;
                _fallbackRotStates[i] = quaternion.identity;
            }
        }

        private void DisposeFilterArrays()
        {
            if (_posInputs.IsCreated) _posInputs.Dispose();
            if (_posOutputs.IsCreated) _posOutputs.Dispose();
            if (_rotInputs.IsCreated) _rotInputs.Dispose();
            if (_rotOutputs.IsCreated) _rotOutputs.Dispose();
            if (_posModeNative.IsCreated) _posModeNative.Dispose();
            if (_rotModeNative.IsCreated) _rotModeNative.Dispose();
            if (_fallbackPosStates.IsCreated) _fallbackPosStates.Dispose();
            if (_fallbackRotStates.IsCreated) _fallbackRotStates.Dispose();
            if (_euroPosStates.IsCreated) _euroPosStates.Dispose();
            if (_euroRotStates.IsCreated) _euroRotStates.Dispose();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte PickMode(bool smoothEnabled, bool euroEnabled)
        {
            if (!smoothEnabled) return (byte)BasisFilterMode.Passthrough;
            return euroEnabled ? (byte)BasisFilterMode.Euro : (byte)BasisFilterMode.Fallback;
        }
        public void OnTPose() => OnTPose(BasisLocalAvatarDriver.CurrentlyTposing);

        public void OnTPose(bool currentlyTposing)
        {
            if (Builder == null)
            {
                BasisDebug.LogWarning($"{nameof(BasisLocalRigDriver)}: Trying to T-pose while Builder is null!");
                return;
            }

            // While in T-pose, disable all rig layers
            if (currentlyTposing)
            {
                foreach (var layer in Builder.layers)
                {
                    if (layer != null)
                    {
                        layer.active = false;
                    }
                }

                return;
            }

            // Notify controls when exiting T-pose
            var driver = BasisLocalPlayer.Instance?.LocalBoneDriver;
            if (driver?.Controls == null)
            {
                return;
            }

            foreach (var control in driver.Controls)
            {
                control?.OnHasRigChanged?.Invoke(control.HasRigLayer == BasisHasRigLayer.HasRigLayer);
            }
        }
        public void ResetSmoothingState()
        {
            timeAccumulator = 0;
            hasFallbackState = false;

            // Reset batched filter state — identity rotations to avoid lerping from zero quats.
            if (_euroPosStates.IsCreated)
            {
                for (int i = 0; i < SlotCount; i++) _euroPosStates[i] = default;
            }
            if (_euroRotStates.IsCreated)
            {
                for (int i = 0; i < SlotCount; i++) _euroRotStates[i] = default;
            }
            if (_fallbackRotStates.IsCreated)
            {
                for (int i = 0; i < SlotCount; i++) _fallbackRotStates[i] = quaternion.identity;
            }
            if (_fallbackPosStates.IsCreated)
            {
                for (int i = 0; i < SlotCount; i++) _fallbackPosStates[i] = float3.zero;
            }

            // Legacy managed Euro filters — still reset in case any live call path uses them.
            fRotHips.Reset();
            fRotHead.Reset();
            fRotLeftFoot.Reset();
            fRotRightFoot.Reset();
            fRotChest.Reset();
            fRotLeftLowerLeg.Reset();
            fRotRightLowerLeg.Reset();
            fRotLeftHand.Reset();
            fRotRightHand.Reset();
            fRotLeftLowerArm.Reset();
            fRotRightLowerArm.Reset();
            fRotLeftToe.Reset();
            fRotRightToe.Reset();
            fRotLeftShoulder.Reset();
            fRotRightShoulder.Reset();
        }
        public void SimulateIKDestinations(float deltaTime)
        {
            if (BasisFullIKConstraint == null || Builder == null)
            {
                return;
            }

            if (!PlayableGraph.IsValid())
            {
                return;
            }

            timeAccumulator += Mathf.Max(deltaTime, 1e-6f);

            EnsureFilterArrays();

            // Fallback smoothing alphas are identical for every slot this frame;
            // compute once instead of running Mathf.Exp per call.
            float smoothingStrength = Mathf.Max(1f, SmoothingStrength);
            float fallbackPosAlpha = ExpAlpha(PositionSmoothingHz / smoothingStrength, deltaTime);
            float fallbackRotAlpha = ExpAlpha(RotationSmoothingHz / smoothingStrength, deltaTime);
            float effectiveMinCutoff = MinCutoff / smoothingStrength;
            float effectiveDCutoff = DerivativeCutoff / smoothingStrength;
            float safeDt = Mathf.Max(deltaTime, 1e-6f);

            // ── 1. Gather raw inputs from bone controls (main thread only) ──
            var hipsData = BasisLocalBoneDriver.HipsControl.OutgoingWorldData;
            var headData = BasisLocalBoneDriver.HeadControl.OutgoingWorldData;
            var leftFootData = BasisLocalBoneDriver.LeftFootControl.OutgoingWorldData;
            var rightFootData = BasisLocalBoneDriver.RightFootControl.OutgoingWorldData;
            var chestData = BasisLocalBoneDriver.ChestControl.OutgoingWorldData;
            var leftLLData = BasisLocalBoneDriver.LeftLowerLegControl.OutgoingWorldData;
            var rightLLData = BasisLocalBoneDriver.RightLowerLegControl.OutgoingWorldData;
            var leftHandData = BasisLocalBoneDriver.LeftHandControl.OutgoingWorldData;
            var rightHandData = BasisLocalBoneDriver.RightHandControl.OutgoingWorldData;
            var leftLAData = BasisLocalBoneDriver.LeftLowerArmControl.OutgoingWorldData;
            var rightLAData = BasisLocalBoneDriver.RightLowerArmControl.OutgoingWorldData;
            var leftToeData = BasisLocalBoneDriver.LeftToeControl.OutgoingWorldData;
            var rightToeData = BasisLocalBoneDriver.RightToeControl.OutgoingWorldData;
            Quaternion leftShoulderRot = BasisLocalBoneDriver.LeftShoulderControl.OutgoingWorldData.rotation;
            Quaternion rightShoulderRot = BasisLocalBoneDriver.RightShoulderControl.OutgoingWorldData.rotation;

            // NativeArray indexer does a safety-handle check on every call. For ~60 sequential
            // writes per frame we cache the pointers once and stream values through UnsafeUtility.
            unsafe
            {
                float3* posPtr = (float3*)_posInputs.GetUnsafePtr();
                quaternion* rotPtr = (quaternion*)_rotInputs.GetUnsafePtr();
                byte* posModePtr = (byte*)_posModeNative.GetUnsafePtr();
                byte* rotModePtr = (byte*)_rotModeNative.GetUnsafePtr();

                posPtr[S_Hips] = hipsData.position;                 rotPtr[S_Hips] = hipsData.rotation;
                posPtr[S_Head] = headData.position;                 rotPtr[S_Head] = headData.rotation;
                posPtr[S_LeftFoot] = leftFootData.position;         rotPtr[S_LeftFoot] = leftFootData.rotation;
                posPtr[S_RightFoot] = rightFootData.position;       rotPtr[S_RightFoot] = rightFootData.rotation;
                posPtr[S_Chest] = chestData.position;               rotPtr[S_Chest] = chestData.rotation;
                posPtr[S_LeftLowerLeg] = leftLLData.position;       rotPtr[S_LeftLowerLeg] = leftLLData.rotation;
                posPtr[S_RightLowerLeg] = rightLLData.position;     rotPtr[S_RightLowerLeg] = rightLLData.rotation;
                posPtr[S_LeftHand] = leftHandData.position;         rotPtr[S_LeftHand] = leftHandData.rotation;
                posPtr[S_RightHand] = rightHandData.position;       rotPtr[S_RightHand] = rightHandData.rotation;
                posPtr[S_LeftLowerArm] = leftLAData.position;       rotPtr[S_LeftLowerArm] = leftLAData.rotation;
                posPtr[S_RightLowerArm] = rightLAData.position;     rotPtr[S_RightLowerArm] = rightLAData.rotation;
                posPtr[S_LeftToe] = leftToeData.position;           rotPtr[S_LeftToe] = leftToeData.rotation;
                posPtr[S_RightToe] = rightToeData.position;         rotPtr[S_RightToe] = rightToeData.rotation;
                posPtr[S_LeftShoulder] = float3.zero;                rotPtr[S_LeftShoulder] = leftShoulderRot;
                posPtr[S_RightShoulder] = float3.zero;               rotPtr[S_RightShoulder] = rightShoulderRot;

                // ── 2. Compute filter modes from toggles ──
                for (int i = 0; i < SlotCount; i++)
                {
                    posModePtr[i] = PickMode(SmoothPos[i], EuroPos[i]);
                    rotModePtr[i] = PickMode(SmoothRot[i], EuroRot[i]);
                }
                // Shoulders have no position target — always passthrough to skip wasted work.
                posModePtr[S_LeftShoulder] = (byte)BasisFilterMode.Passthrough;
                posModePtr[S_RightShoulder] = (byte)BasisFilterMode.Passthrough;
            }

            // ── 3. On first use, seed fallback states from live inputs so we don't lerp from zero ──
            if (!hasFallbackState)
            {
                hasFallbackState = true;
                _fallbackPosStates.CopyFrom(_posInputs);
                _fallbackRotStates.CopyFrom(_rotInputs);
            }

            // ── 4. Schedule batched filter jobs ──
            var posJob = new BasisBatchPositionFilterJob
            {
                mode = _posModeNative,
                rawInputs = _posInputs,
                euroStates = _euroPosStates,
                fallbackStates = _fallbackPosStates,
                outputs = _posOutputs,
                dt = safeDt,
                minCutoff = effectiveMinCutoff,
                beta = Beta,
                dCutoff = effectiveDCutoff,
                fallbackAlpha = fallbackPosAlpha,
            };
            var rotJob = new BasisBatchRotationFilterJob
            {
                mode = _rotModeNative,
                rawInputs = _rotInputs,
                euroStates = _euroRotStates,
                fallbackStates = _fallbackRotStates,
                outputs = _rotOutputs,
                dt = safeDt,
                minCutoff = effectiveMinCutoff,
                beta = Beta,
                dCutoff = effectiveDCutoff,
                fallbackAlpha = fallbackRotAlpha,
            };
            JobHandle posHandle = posJob.Schedule(SlotCount, 4);
            JobHandle rotHandle = rotJob.Schedule(SlotCount, 4);

            // ── 5. Schedule foot sim in parallel with filters ──
            bool fbtEnabled = Basis.BasisUI.BasisSettingsDefaults.EnableFBT.RawValue;
            bool leftHasTracker = fbtEnabled && (BasisLocalBoneDriver.LeftFootControl.HasTracked == BasisHasTracked.HasTracker
                || BasisLocalBoneDriver.LeftUpperLegControl.HasTracked == BasisHasTracked.HasTracker);
            bool rightHasTracker = fbtEnabled && (BasisLocalBoneDriver.RightFootControl.HasTracked == BasisHasTracked.HasTracker
                || BasisLocalBoneDriver.RightUpperLegControl.HasTracked == BasisHasTracked.HasTracker);

            bool locomotionAnimActive = localPlayer.LocalCharacterDriver.MovementVector.sqrMagnitude > 0.001f;
            if (locomotionAnimActive) stationaryTimer = 0f;
            else stationaryTimer += deltaTime;

            BasisLocalFootDriver footDriver = localPlayer.BasisLocalFootDriver;
            bool footDriverReady = footDriver.IsInitialized;
            bool isStationaryEnough = stationaryTimer >= StationaryDelaySeconds;
            bool footIKSetting = Basis.BasisUI.BasisSettingsDefaults.FootIKEnabled.RawValue;
            bool footIKReady = footDriverReady && isStationaryEnough && footIKSetting;
            bool leftWantIK = footIKReady && !leftHasTracker;
            bool rightWantIK = footIKReady && !rightHasTracker;
            bool leftOrRightDrive = !leftHasTracker || !rightHasTracker;

            bool footSimScheduled = false;
            if (footDriverReady && leftOrRightDrive)
            {
                footDriver.ScheduleSimulate(deltaTime);
                footSimScheduled = true;
            }

            // ── 6. Main-thread bookkeeping runs parallel with filter + foot jobs ──
            float leftBlendTarget = leftWantIK ? 1f : 0f;
            float rightBlendTarget = rightWantIK ? 1f : 0f;
            if (leftHasTracker) footIKBlendWeightLeft = 0f;
            if (rightHasTracker) footIKBlendWeightRight = 0f;

            float leftPrevBlend = footIKBlendWeightLeft;
            float rightPrevBlend = footIKBlendWeightRight;
            footIKBlendWeightLeft = Mathf.MoveTowards(footIKBlendWeightLeft, leftBlendTarget,
                (leftWantIK ? FootIKBlendInSpeed : FootIKBlendOutSpeed) * deltaTime);
            footIKBlendWeightRight = Mathf.MoveTowards(footIKBlendWeightRight, rightBlendTarget,
                (rightWantIK ? FootIKBlendInSpeed : FootIKBlendOutSpeed) * deltaTime);
            footIKBlendWeight = Mathf.Max(footIKBlendWeightLeft, footIKBlendWeightRight);

            bool notifyReengage = footDriverReady &&
                ((leftPrevBlend < 0.001f && footIKBlendWeightLeft >= 0.001f)
                 || (rightPrevBlend < 0.001f && footIKBlendWeightRight >= 0.001f));

            bool leftLLHasTracker = fbtEnabled && BasisLocalBoneDriver.LeftLowerLegControl.HasTracked == BasisHasTracked.HasTracker;
            bool rightLLHasTracker = fbtEnabled && BasisLocalBoneDriver.RightLowerLegControl.HasTracked == BasisHasTracked.HasTracker;
            bool hipsHaveTracker = fbtEnabled && BasisLocalBoneDriver.HipsControl.HasTracked == BasisHasTracked.HasTracker;

            // ── 7. Wait for jobs ──
            JobHandle.CombineDependencies(posHandle, rotHandle).Complete();
            if (footSimScheduled) footDriver.CompleteSimulate();

            // NotifyReEngaging reads live bone control data (not foot sim output), but kept after
            // completion so all foot state is coherent when the next sim starts.
            if (notifyReengage) footDriver.NotifyReEngaging();

            // ── 8. Scatter filter outputs into BasisFullBodyData ──
            BasisFullBodyData data = BasisFullIKConstraint.data;

            // Pull out pointers once; avoids per-slot safety-handle checks on each indexer read.
            Vector3 hipsPos;
            Quaternion hipsRot;
            Vector3 chestPos;
            Quaternion chestRot;
            Vector3 llaPos, rlaPos;
            Quaternion llaRot, rlaRot;
            unsafe
            {
                float3* pOut = (float3*)_posOutputs.GetUnsafeReadOnlyPtr();
                quaternion* rOut = (quaternion*)_rotOutputs.GetUnsafeReadOnlyPtr();

                hipsPos = pOut[S_Hips];
                hipsRot = rOut[S_Hips];
                hipsPos.y -= localPlayer.LocalCharacterDriver.landingCrouchEffect;
                data.PositionHips = hipsPos;
                data.RotationHips = hipsRot;

                data.PositionHead = pOut[S_Head];
                data.RotationHead = rOut[S_Head];

                // ── LEFT FOOT ──
                if (leftHasTracker)
                {
                    data.LeftFootPosition = pOut[S_LeftFoot];
                    data.LeftFootRotation = rOut[S_LeftFoot];
                }
                else if (footIKBlendWeightLeft > 0.001f && footDriverReady)
                {
                    data.LeftFootPosition = footDriver.LeftFootPosition;
                    data.LeftFootRotation = footDriver.LeftFootRotation;
                    data.EnableLeftLeg = footIKBlendWeightLeft;
                }
                else
                {
                    data.EnableLeftLeg = 0f;
                }

                // ── RIGHT FOOT ──
                if (rightHasTracker)
                {
                    data.RightFootPosition = pOut[S_RightFoot];
                    data.RightFootRotation = rOut[S_RightFoot];
                }
                else if (footIKBlendWeightRight > 0.001f && footDriverReady)
                {
                    data.RightFootPosition = footDriver.RightFootPosition;
                    data.RightFootRotation = footDriver.RightFootRotation;
                    data.EnableRightLeg = footIKBlendWeightRight;
                }
                else
                {
                    data.EnableRightLeg = 0f;
                }

                // ── HIP BOB ──
                if (footIKBlendWeight > 0.001f && footDriverReady && !hipsHaveTracker)
                {
                    data.PositionHips = new Vector3(data.PositionHips.x,
                        data.PositionHips.y + footDriver.ComputeHipBob() * footIKBlendWeight,
                        data.PositionHips.z);
                }

                // ── CHEST (head hint) ──
                chestPos = pOut[S_Chest];
                chestRot = rOut[S_Chest];
                chestPos = ApplyHintBias(BasisBoneTrackedRole.Chest, chestPos, chestRot);
                data.ChestPosition = chestPos;
                data.ChestRotation = chestRot;

                // ── LEFT LOWER LEG ──
                if (leftLLHasTracker)
                {
                    Vector3 lllPos = pOut[S_LeftLowerLeg];
                    Quaternion lllRot = rOut[S_LeftLowerLeg];
                    lllPos = ApplyHintBias(BasisBoneTrackedRole.LeftLowerLeg, lllPos, lllRot);
                    data.PositionLeftLowerLeg = lllPos;
                    data.RotationLeftLowerLeg = lllRot;
                    data.EnableLeftLowerLeg = 1f;
                }
                else if (footIKBlendWeightLeft > 0.001f && footDriverReady)
                {
                    Quaternion targetRotL = ComputeKneeHintRotation(data.PositionHips, data.LeftFootPosition, footDriver.LeftKneeHint);
                    float kneeRotAlpha = 1f - Mathf.Exp(-8f * deltaTime);
                    smoothedLeftKneeRot = Quaternion.Slerp(smoothedLeftKneeRot, targetRotL, kneeRotAlpha);
                    data.PositionLeftLowerLeg = footDriver.LeftKneeHint;
                    data.RotationLeftLowerLeg = smoothedLeftKneeRot;
                    data.EnableLeftLowerLeg = footIKBlendWeightLeft;
                }
                else
                {
                    data.EnableLeftLowerLeg = 0f;
                }

                // ── RIGHT LOWER LEG ──
                if (rightLLHasTracker)
                {
                    Vector3 rllPos = pOut[S_RightLowerLeg];
                    Quaternion rllRot = rOut[S_RightLowerLeg];
                    rllPos = ApplyHintBias(BasisBoneTrackedRole.RightLowerLeg, rllPos, rllRot);
                    data.PositionRightLowerLeg = rllPos;
                    data.RotationRightLowerLeg = rllRot;
                    data.EnableRightLowerLeg = 1f;
                }
                else if (footIKBlendWeightRight > 0.001f && footDriverReady)
                {
                    Quaternion targetRotR = ComputeKneeHintRotation(data.PositionHips, data.RightFootPosition, footDriver.RightKneeHint);
                    float kneeRotAlpha = 1f - Mathf.Exp(-8f * deltaTime);
                    smoothedRightKneeRot = Quaternion.Slerp(smoothedRightKneeRot, targetRotR, kneeRotAlpha);
                    data.PositionRightLowerLeg = footDriver.RightKneeHint;
                    data.RotationRightLowerLeg = smoothedRightKneeRot;
                    data.EnableRightLowerLeg = footIKBlendWeightRight;
                }
                else
                {
                    data.EnableRightLowerLeg = 0f;
                }

                // ── HANDS ──
                data.PositionLeftHand = pOut[S_LeftHand];
                data.RotationLeftHand = rOut[S_LeftHand];
                data.PositionRightHand = pOut[S_RightHand];
                data.RotationRightHand = rOut[S_RightHand];

                // ── LOWER ARMS (elbow hints) ──
                llaPos = pOut[S_LeftLowerArm];
                llaRot = rOut[S_LeftLowerArm];
                llaPos = ApplyHintBias(BasisBoneTrackedRole.LeftLowerArm, llaPos, llaRot);
                data.LeftLowerArmPosition = llaPos;
                data.LeftLowerArmRotation = llaRot;

                rlaPos = pOut[S_RightLowerArm];
                rlaRot = rOut[S_RightLowerArm];
                rlaPos = ApplyHintBias(BasisBoneTrackedRole.RightLowerArm, rlaPos, rlaRot);
                data.RightLowerArmPosition = rlaPos;
                data.RightLowerArmRotation = rlaRot;

                // ── TOES ──
                data.OutGoingLeftToePosition = pOut[S_LeftToe];
                data.OutGoingLeftToeRotation = rOut[S_LeftToe];
                data.OutGoingRightToePosition = pOut[S_RightToe];
                data.OutGoingRightToeRotation = rOut[S_RightToe];

                // ── SHOULDERS (rotation only) ──
                data.LeftShoulderRotation = rOut[S_LeftShoulder];
                data.RightShoulderRotation = rOut[S_RightShoulder];
            }

            // ── DERIVED BEND PREFS ──
            Vector3 fwdC = chestRot * Vector3.forward;
            Vector3 outC = chestRot * Vector3.right;
            Vector3 upC = chestRot * Vector3.up;
            data.ElbowBendPrefLeft = (fwdC * elbowBendPrefLeftWeights.x
                + outC * elbowBendPrefLeftWeights.y
                + upC * elbowBendPrefLeftWeights.z).normalized;
            data.ElbowBendPrefRight = (fwdC * elbowBendPrefRightWeights.x
                + outC * elbowBendPrefRightWeights.y
                + upC * elbowBendPrefRightWeights.z).normalized;

            Vector3 fwd = hipsRot * Vector3.forward;
            Vector3 outR = hipsRot * Vector3.right;
            Vector3 up = hipsRot * Vector3.up;
            Vector3 hipsRight = hipsRot * Vector3.right;
            data.KneeBendPrefLeft = hipsRight;
            data.KneeBendPrefRight = hipsRight;
            data.SpineBendNormal = (fwd * spineBendNormalWeights.x
                + outR * spineBendNormalWeights.y
                + up * spineBendNormalWeights.z).normalized;

            BasisFullIKConstraint.data = data;
            Builder.SyncLayers();
            PlayableGraph.Evaluate(deltaTime);
        }
        [SerializeField] private Vector3 elbowBendPrefLeftWeights = new Vector3(0, 1, 0);
        [SerializeField] private Vector3 elbowBendPrefRightWeights = new Vector3(0, 1, 0);
        [SerializeField] private Vector3 spineBendNormalWeights = new Vector3(1f, 0f, 0f);
        public static Vector3 ApplyHintBias(BasisBoneTrackedRole hintRole, Vector3 rawPos, Quaternion rawRot)
        {
            if (BasisHintBiasStore.TryGet(hintRole, out var localOffset))
            {
                return rawPos + rawRot * localOffset;
            }

            return rawPos;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float ExpAlpha(float hz, float dt)
        {
            return 1f - Mathf.Exp(-2f * Mathf.PI * Mathf.Max(0.0001f, hz) * Mathf.Max(0.000001f, dt));
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector3 FallbackPos(ref Vector3 state, Vector3 raw, float alpha)
        {
            state = Vector3.LerpUnclamped(state, raw, alpha);
            return state;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Quaternion FallbackRot(ref Quaternion state, Quaternion raw, float alpha)
        {
            state = Quaternion.Slerp(state, raw, alpha);
            return state;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void UpdateEuroSettings()
        {
            float strength = Mathf.Max(1f, SmoothingStrength);
            float effectiveMinCutoff = MinCutoff / strength;
            float effectiveDCutoff = DerivativeCutoff / strength;

            // Position filters
            fPosHips.minCutoff = effectiveMinCutoff; fPosHips.beta = Beta; fPosHips.dCutoff = effectiveDCutoff;
            fPosHead.minCutoff = effectiveMinCutoff; fPosHead.beta = Beta; fPosHead.dCutoff = effectiveDCutoff;
            fPosLeftFoot.minCutoff = effectiveMinCutoff; fPosLeftFoot.beta = Beta; fPosLeftFoot.dCutoff = effectiveDCutoff;
            fPosRightFoot.minCutoff = effectiveMinCutoff; fPosRightFoot.beta = Beta; fPosRightFoot.dCutoff = effectiveDCutoff;
            fPosChest.minCutoff = effectiveMinCutoff; fPosChest.beta = Beta; fPosChest.dCutoff = effectiveDCutoff;
            fPosLeftLowerLeg.minCutoff = effectiveMinCutoff; fPosLeftLowerLeg.beta = Beta; fPosLeftLowerLeg.dCutoff = effectiveDCutoff;
            fPosRightLowerLeg.minCutoff = effectiveMinCutoff; fPosRightLowerLeg.beta = Beta; fPosRightLowerLeg.dCutoff = effectiveDCutoff;
            fPosLeftHand.minCutoff = effectiveMinCutoff; fPosLeftHand.beta = Beta; fPosLeftHand.dCutoff = effectiveDCutoff;
            fPosRightHand.minCutoff = effectiveMinCutoff; fPosRightHand.beta = Beta; fPosRightHand.dCutoff = effectiveDCutoff;
            fPosLeftLowerArm.minCutoff = effectiveMinCutoff; fPosLeftLowerArm.beta = Beta; fPosLeftLowerArm.dCutoff = effectiveDCutoff;
            fPosRightLowerArm.minCutoff = effectiveMinCutoff; fPosRightLowerArm.beta = Beta; fPosRightLowerArm.dCutoff = effectiveDCutoff;
            fPosLeftToe.minCutoff = effectiveMinCutoff; fPosLeftToe.beta = Beta; fPosLeftToe.dCutoff = effectiveDCutoff;
            fPosRightToe.minCutoff = effectiveMinCutoff; fPosRightToe.beta = Beta; fPosRightToe.dCutoff = effectiveDCutoff;

            // Rotation filters
            fRotHips.minCutoff = effectiveMinCutoff; fRotHips.beta = Beta; fRotHips.dCutoff = effectiveDCutoff;
            fRotHead.minCutoff = effectiveMinCutoff; fRotHead.beta = Beta; fRotHead.dCutoff = effectiveDCutoff;
            fRotLeftFoot.minCutoff = effectiveMinCutoff; fRotLeftFoot.beta = Beta; fRotLeftFoot.dCutoff = effectiveDCutoff;
            fRotRightFoot.minCutoff = effectiveMinCutoff; fRotRightFoot.beta = Beta; fRotRightFoot.dCutoff = effectiveDCutoff;
            fRotChest.minCutoff = effectiveMinCutoff; fRotChest.beta = Beta; fRotChest.dCutoff = effectiveDCutoff;
            fRotLeftLowerLeg.minCutoff = effectiveMinCutoff; fRotLeftLowerLeg.beta = Beta; fRotLeftLowerLeg.dCutoff = effectiveDCutoff;
            fRotRightLowerLeg.minCutoff = effectiveMinCutoff; fRotRightLowerLeg.beta = Beta; fRotRightLowerLeg.dCutoff = effectiveDCutoff;
            fRotLeftHand.minCutoff = effectiveMinCutoff; fRotLeftHand.beta = Beta; fRotLeftHand.dCutoff = effectiveDCutoff;
            fRotRightHand.minCutoff = effectiveMinCutoff; fRotRightHand.beta = Beta; fRotRightHand.dCutoff = effectiveDCutoff;
            fRotLeftLowerArm.minCutoff = effectiveMinCutoff; fRotLeftLowerArm.beta = Beta; fRotLeftLowerArm.dCutoff = effectiveDCutoff;
            fRotRightLowerArm.minCutoff = effectiveMinCutoff; fRotRightLowerArm.beta = Beta; fRotRightLowerArm.dCutoff = effectiveDCutoff;
            fRotLeftToe.minCutoff = effectiveMinCutoff; fRotLeftToe.beta = Beta; fRotLeftToe.dCutoff = effectiveDCutoff;
            fRotRightToe.minCutoff = effectiveMinCutoff; fRotRightToe.beta = Beta; fRotRightToe.dCutoff = effectiveDCutoff;
            fRotLeftShoulder.minCutoff = effectiveMinCutoff; fRotLeftShoulder.beta = Beta; fRotLeftShoulder.dCutoff = effectiveDCutoff;
            fRotRightShoulder.minCutoff = effectiveMinCutoff; fRotRightShoulder.beta = Beta; fRotRightShoulder.dCutoff = effectiveDCutoff;
        }
        private void OnPlayersHeightChangedNextFrame(HeightModeChange HeightModeChange)
        {
            var Data = BasisFullIKConstraint.data;
            SetHandCollisionScale(ref Data, BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale);
            BasisFullIKConstraint.data = Data;
        }
        public static void SetHandCollisionScale(ref BasisFullBodyData BodyData, float Scale)
        {
            //1.6m is the default values for below..
            BodyData.HandSkin = 0.03f * Scale;
            BodyData.HandRadius = 0.01f * Scale;
            BodyData.ChestRadius = 0.07f * Scale;
            BodyData.CollisionSkin = 0.05f * Scale;

            var hips = BasisLocalBoneDriver.HipsControl.TposeLocalScaled;
            var spine = BasisLocalBoneDriver.SpineControl.TposeLocalScaled;
            var chest = BasisLocalBoneDriver.ChestControl.TposeLocalScaled;

            var neck = BasisLocalBoneDriver.NeckControl.TposeLocalScaled;
            var head = BasisLocalBoneDriver.HeadControl.TposeLocalScaled;


            float minHeadSpineHeight = 0f;
            minHeadSpineHeight += Vector3.Distance(hips.position, spine.position);
            minHeadSpineHeight += Vector3.Distance(spine.position, chest.position);
            minHeadSpineHeight += Vector3.Distance(chest.position, neck.position);
            minHeadSpineHeight += Vector3.Distance(neck.position, head.position);

            BodyData.minHeadSpineHeight = minHeadSpineHeight;
        }
        public void Spine(GameObject mainRig)
        {
            if (localPlayer == null || mainRig == null)
            {
                return;
            }

            BasisAnimationRiggingHelper.CreateBasisFullBodyRIG(localPlayer,  mainRig, basisTransformMapping, out BasisFullIKConstraint);

            BasisLocalPlayer.OnPlayersHeightChangedNextFrame += OnPlayersHeightChangedNextFrame;
            OnPlayersHeightChangedNextFrame( HeightModeChange.OnTpose);

            var data = BasisFullIKConstraint.data;

            // Legs enabled by presence
            BasisLocalBoneDriver.LeftFootControl.OnHasRigChanged += (hasRig) =>
            {
                var d = BasisFullIKConstraint.data;
                d.EnableLeftLeg = HasRigLayerFloat(BasisLocalBoneDriver.LeftFootControl);
                BasisFullIKConstraint.data = d;
            };
            data.EnableLeftLeg = HasRigLayerFloat(BasisLocalBoneDriver.LeftFootControl);

            BasisLocalBoneDriver.RightFootControl.OnHasRigChanged += (hasRig) =>
            {
                var d = BasisFullIKConstraint.data;
                d.EnableRightLeg = HasRigLayerFloat(BasisLocalBoneDriver.RightFootControl);
                BasisFullIKConstraint.data = d;
            };
            data.EnableRightLeg = HasRigLayerFloat(BasisLocalBoneDriver.RightFootControl);

            BasisLocalBoneDriver.LeftLowerLegControl.OnHasRigChanged += (hasRig) =>
            {
                var d = BasisFullIKConstraint.data;
                d.EnableLeftLowerLeg = HasRigLayerFloat(BasisLocalBoneDriver.LeftLowerLegControl);
                BasisFullIKConstraint.data = d;
            };
            data.EnableLeftLowerLeg = HasRigLayerFloat(BasisLocalBoneDriver.LeftLowerLegControl);

            BasisLocalBoneDriver.RightLowerLegControl.OnHasRigChanged += (hasRig) =>
            {
                var d = BasisFullIKConstraint.data;
                d.EnableRightLowerLeg = HasRigLayerFloat(BasisLocalBoneDriver.RightLowerLegControl);
                BasisFullIKConstraint.data = d;
            };
            data.EnableRightLowerLeg = HasRigLayerFloat(BasisLocalBoneDriver.RightLowerLegControl);

            // Toes
            BasisLocalBoneDriver.LeftToeControl.OnHasRigChanged += (hasRig) =>
            {
                var d = BasisFullIKConstraint.data;
                d.LeftToeEnabled = HasRigLayer(BasisLocalBoneDriver.LeftToeControl);
                BasisFullIKConstraint.data = d;
            };
            data.LeftToeEnabled = HasRigLayer(BasisLocalBoneDriver.LeftToeControl);

            BasisLocalBoneDriver.RightToeControl.OnHasRigChanged += (hasRig) =>
            {
                var d = BasisFullIKConstraint.data;
                d.RightToeEnabled = HasRigLayer(BasisLocalBoneDriver.RightToeControl);
                BasisFullIKConstraint.data = d;
            };
            data.RightToeEnabled = HasRigLayer(BasisLocalBoneDriver.RightToeControl);

            // Hands
            BasisLocalBoneDriver.LeftHandControl.OnHasRigChanged += (hasRig) =>
            {
                var d = BasisFullIKConstraint.data;
                d.EnabledLeftHand = HasRigLayer(BasisLocalBoneDriver.LeftHandControl);
                BasisFullIKConstraint.data = d;
            };
            data.EnabledLeftHand = HasRigLayer(BasisLocalBoneDriver.LeftHandControl);

            BasisLocalBoneDriver.RightHandControl.OnHasRigChanged += (hasRig) =>
            {
                var d = BasisFullIKConstraint.data;
                d.EnabledRightHand = HasRigLayer(BasisLocalBoneDriver.RightHandControl);
                BasisFullIKConstraint.data = d;
            };
            data.EnabledRightHand = HasRigLayer(BasisLocalBoneDriver.RightHandControl);

            // Lower arms (hand hints)
            BasisLocalBoneDriver.LeftLowerArmControl.OnHasRigChanged += (hasRig) =>
            {
                var d = BasisFullIKConstraint.data;
                d.HintWeightLeftHand = HasRigLayer(BasisLocalBoneDriver.LeftLowerArmControl);
                BasisFullIKConstraint.data = d;
            };
            data.HintWeightLeftHand = HasRigLayer(BasisLocalBoneDriver.LeftLowerArmControl);

            BasisLocalBoneDriver.RightLowerArmControl.OnHasRigChanged += (hasRig) =>
            {
                var d = BasisFullIKConstraint.data;
                d.HintWeightRightHand = HasRigLayer(BasisLocalBoneDriver.RightLowerArmControl);
                BasisFullIKConstraint.data = d;
            };
            data.HintWeightRightHand = HasRigLayer(BasisLocalBoneDriver.RightLowerArmControl);

            // Chest (head hint)
            BasisLocalBoneDriver.ChestControl.OnHasRigChanged += (hasRig) =>
            {
                var d = BasisFullIKConstraint.data;
                d.WeightChest = HasRigLayer(BasisLocalBoneDriver.ChestControl);
                BasisFullIKConstraint.data = d;
            };
            data.WeightChest = HasRigLayer(BasisLocalBoneDriver.ChestControl);

            // Chest (head hint)
            BasisLocalBoneDriver.LeftShoulderControl.OnHasRigChanged += (hasRig) =>
            {
                var d = BasisFullIKConstraint.data;
                d.EnabledLeftShoulder = HasRigLayer(BasisLocalBoneDriver.LeftShoulderControl);
                BasisFullIKConstraint.data = d;
            };
            data.EnabledLeftShoulder = HasRigLayer(BasisLocalBoneDriver.LeftShoulderControl);

            // Chest (head hint)
            BasisLocalBoneDriver.RightShoulderControl.OnHasRigChanged += (hasRig) =>
            {
                var d = BasisFullIKConstraint.data;
                d.EnabledRightShoulder = HasRigLayer(BasisLocalBoneDriver.RightShoulderControl);
                BasisFullIKConstraint.data = d;
            };
            data.EnabledRightShoulder = HasRigLayer(BasisLocalBoneDriver.RightShoulderControl);

            // Initialize offsets and weights per humanoid bone
            int totalBones = BasisFullBodyData.Count;
            for (int slot = 0; slot < totalBones; slot++)
            {
                var bone = (HumanBodyBones)slot;
                var t = ResolveHumanoidBoneTransform(bone);
                if (t == null)
                {
                    continue;
                }

                data.SetWeight(slot, false);
                data.SetOffsetRotation(slot, t.rotation);
                data.SetTargetRotation(slot, t.rotation);
            }
            data.MaxBendDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKMaxBendDeg.RawValue;
            data.MinFactor = 1f;
            data.MaxFactor = 1f;
            data.StruggleStart = Basis.BasisUI.BasisSettingsDefaults.FBIKStruggleStart.RawValue;
            data.StruggleEnd = Basis.BasisUI.BasisSettingsDefaults.FBIKStruggleEnd.RawValue;
            data.MaxChestDelta = Basis.BasisUI.BasisSettingsDefaults.FBIKMaxChestDelta.RawValue;
            data.MaxHipDelta = Basis.BasisUI.BasisSettingsDefaults.FBIKMaxHipDelta.RawValue;

            BasisFullIKConstraint.data = data;
        }
        public void DisableAllTrackers()
        {
            if (BasisFullIKConstraint != null)
            {
                var data = BasisFullIKConstraint.data;
                data.EnableLeftLeg = 0f;
                data.EnableRightLeg = 0f;
                data.EnableLeftLowerLeg = 0f;
                data.EnableRightLowerLeg = 0f;
                data.LeftToeEnabled = false;
                data.RightToeEnabled = false;
                // data.EnabledLeftHand = false;
                // data.EnabledRightHand = false;
                data.HintWeightLeftHand = false;
                data.HintWeightRightHand = false;
                data.WeightChest = false;
                data.EnabledLeftShoulder = false;
                data.EnabledRightShoulder = false;
                BasisFullIKConstraint.data = data;
            }
        }
        private static bool HasRigLayer(BasisLocalBoneControl control)
        {
            return control.HasRigLayer == BasisHasRigLayer.HasRigLayer;
        }

        private static float HasRigLayerFloat(BasisLocalBoneControl control)
        {
            return control.HasRigLayer == BasisHasRigLayer.HasRigLayer ? 1f : 0f;
        }

        /// <summary>
        /// Compute a knee bend normal from the hip→foot→kneeHint triangle.
        /// The normal of this triangle defines the plane the knee should bend in.
        /// Falls back to the provided default if the triangle is degenerate.
        /// </summary>
        private static Vector3 ComputeKneeBendNormal(Vector3 hip, Vector3 foot, Vector3 kneeHint, Vector3 fallback)
        {
            Vector3 hipToFoot = foot - hip;
            Vector3 hipToKnee = kneeHint - hip;
            Vector3 normal = Vector3.Cross(hipToFoot, hipToKnee);
            return normal.sqrMagnitude > 1e-8f ? normal.normalized : fallback;
        }

        /// <summary>
        /// Compute a smooth rotation for the knee hint from the hip-knee-foot triangle.
        /// Forward = knee→foot direction, Up = derived from the bend plane.
        /// This prevents snapping that occurs with Quaternion.identity.
        /// </summary>
        private static Quaternion ComputeKneeHintRotation(Vector3 hip, Vector3 foot, Vector3 kneeHint)
        {
            Vector3 kneeToFoot = foot - kneeHint;
            Vector3 kneeToHip = hip - kneeHint;

            if (kneeToFoot.sqrMagnitude < 1e-8f || kneeToHip.sqrMagnitude < 1e-8f)
                return Quaternion.identity;

            // Forward: along the shin (knee toward foot)
            Vector3 fwd = kneeToFoot.normalized;

            // Up: perpendicular to the bend plane, pointing away from the bend
            Vector3 bendNormal = Vector3.Cross(kneeToHip, kneeToFoot);
            Vector3 up = Vector3.Cross(fwd, bendNormal);

            if (up.sqrMagnitude < 1e-8f)
                up = Vector3.up;
            else
                up.Normalize();

            return Quaternion.LookRotation(fwd, up);
        }

        public GameObject CreateOrGetRig(string role, bool enabled, out Rig rig, out RigLayer rigLayer)
        {
            rig = null;
            rigLayer = default;

            if (localPlayer?.BasisAvatar?.Animator == null)
            {
                return null;
            }

            if (Builder != null)
            {
                foreach (var layer in Builder.layers)
                {
                    if (layer?.rig != null && layer.rig.name == $"Rig {role}")
                    {
                        rig = layer.rig;
                        rigLayer = layer;
                        return layer.rig.gameObject;
                    }
                }
            }

            var anim = localPlayer.BasisAvatar.Animator;
            GameObject rigGO = BasisAnimationRiggingHelper.CreateAndSetParent(anim.transform, $"Rig {role}");

            rig = BasisHelpers.GetOrAddComponent<Rig>(rigGO);
            rigLayer = new RigLayer(rig, enabled);

            if (Builder == null)
            {
                Builder = BasisHelpers.GetOrAddComponent<RigBuilder>(anim.gameObject);
            }

            Builder.layers.Add(rigLayer);

            return rigGO;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetOverrideUsage(HumanBodyBones bone, bool enabled)
        {
            var data = BasisFullIKConstraint.data;
            data.SetWeight((int)bone, enabled);
            BasisFullIKConstraint.data = data;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetOverrideData(HumanBodyBones bone, in Vector3 position, in Quaternion rotation)
        {
            var data = BasisFullIKConstraint.data;
            data.SetTargetPosition((int)bone, position);
            data.SetTargetRotation((int)bone, rotation);
            BasisFullIKConstraint.data = data;
        }
        private Transform ResolveHumanoidBoneTransform(HumanBodyBones bone)
        {
            // Prefer references map if available
            if (BasisLocalAvatarDriver.Mapping != null && BasisLocalAvatarDriver.Mapping.GetTransform(bone, out Transform refT))
            {
                return refT;
            }
            // Fallback to Animator
            var animator = localPlayer?.BasisAvatar?.Animator;
            return animator != null ? animator.GetBoneTransform(bone) : null;
        }
    }
}
