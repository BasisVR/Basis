using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.TransformBinders.BoneControl;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using Basis.IK;
using UnityEngine.Jobs;
using UnityEngine.Playables;
using static Basis.Scripts.Avatar.BasisAvatarIKStageCalibration;
using static BasisHeightDriver;

namespace Basis.Scripts.Drivers
{
    /// <summary>
    /// Local rig driver that wires up Unity Animation Rigging constraints for a player avatar,
    /// filters tracker noise (One Euro Filter), and runs the IK solve against the animated pose
    /// (evaluated by the engine's animation stage, or manually when the legacy switch is off).
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

        [System.NonSerialized] public PlayableGraph PlayableGraph;
        [System.NonSerialized] public readonly BasisPoseSkeleton PoseSkeleton = new BasisPoseSkeleton();
        [System.NonSerialized] public readonly BasisLocomotionPoseSystem LocomotionPose = new BasisLocomotionPoseSystem();
        [System.NonSerialized] public BasisEerieMovement IKJob;
        [System.NonSerialized] public bool IKJobCreated;
        public bool RigLayerActive = true;
        [System.NonSerialized] public bool IKDataReady;

        /// <summary>
        /// The FBIK hand target offsets (landmark frame -> hand bone frame), as plain quaternions.
        ///
        /// MediaPipe needs these to cancel FBIK's offset -- it emits an already-finished BONE rotation, so the
        /// solve's own `target * offset` would apply the palm->bone map a second time. But BasisFullBodyIK derives
        /// from RigConstraint&lt;,,&gt;, so reading `.data` from another package forces com.basis.mediapipe to take a hard
        /// dependency on Unity.Animation.Rigging just to fetch two quaternions. Handing them out from here -- inside
        /// the assembly that already references Rigging -- keeps that dependency where it belongs.
        ///
        /// Identity when there is no constraint yet, which is the correct no-op: an uncalibrated offset must not
        /// rotate anything.
        /// </summary>
        public Quaternion LeftHandIKOffset => IKDataReady ? IKJob.offsetRotationLeftHand : Quaternion.identity;
        public Quaternion RightHandIKOffset => IKDataReady ? IKJob.offsetRotationRightHand : Quaternion.identity;

        private BasisLocalPlayer localPlayer;
        public BasisTransformMapping basisTransformMapping;

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

        // Smoothed butterfly-knee hint (laying-down knee splay from tracked feet; see BasisButterflyKneeCore)
        private static Vector3 smoothedLeftButterflyHint, smoothedRightButterflyHint;
        private static float smoothedLeftButterflyWeight, smoothedRightButterflyWeight;
        private const float ButterflyKneeSmoothRate = 8f;

        // Smoothed knee-forward hint (upright knee azimuth following the tracked foot's toe; see BasisKneeForwardCore)
        private static Vector3 smoothedLeftKneeFwdHint, smoothedRightKneeFwdHint;
        private static float smoothedLeftKneeFwdWeight, smoothedRightKneeFwdWeight;
        private const float KneeForwardSmoothRate = 10f;

        // Per-foot blend weights for transitioning IK in/out (0 = animation, 1 = foot driver)
        private static float footIKBlendWeightLeft = 0f;
        private static float footIKBlendWeightRight = 0f;
        private static float footIKBlendWeight = 0f; // min of left/right, used for hip bob
        private const float FootIKBlendInSpeed = 20f;  // ~50ms to fully engage
        private const float FootIKBlendOutSpeed = 15f; // ~67ms to fully disengage

        // Hysteresis: require stationary for this long before engaging foot IK.
        // Prevents single-frame flicker at jump apex or during speed oscillations.
        private static float stationaryTimer = 0f;
        private const float StationaryDelaySeconds = 0.15f;

        // ── LOCOMOTION FOOT IK (experimental, default OFF -- see the measured caveats below) ──
        // false = shipping behaviour. footIKReady additionally requires isStationaryEnough, so ANY stick
        // deflection off dead-centre (MovementVector.sqrMagnitude > 0.001) drops the blend to 0 and holds it
        // there until 150 ms after the stick returns to rest. SolveLegs then early-returns on enabled*LowerLeg
        // == 0 and the legs are pure FK from the locomotion clip -- no ground contact, no surface adaptation,
        // no heel-strike, and (because they are gated on the same blend weight) no hip bob, no lateral sway
        // and no pelvic axial rotation either. The whole gait model is therefore only ever VISIBLE while
        // standing, turning in place, or walking room-scale.
        //
        // true = the stepper also drives the feet during stick locomotion, which is what FinalIK's VRIK calls
        // Locomotion.Procedural and what it was built for (Lang, "Character Animation in Dead and Buried").
        //
        // ⚠ NOT headset-verified. What IS measured, on the 41-scenario sweep at 0.5x/1x/2x (this is exactly the
        // regime the flag exposes, and the sweep has never modelled the gate, so it has always reported it):
        //   - STEADY-state locomotion is inside the gate at every speed: walk-normal 1.05, walk-fast 1.05,
        //     sprint 1.15 against a 1.18 limit, with a clean alternating cycle and duty factor 0.53.
        //   - TRANSIENTS still exceed it. A hard start from rest peaks ~1.3-1.6x standing reach for ~0.3 s
        //     before settling, worst on small avatars; jumping while running and hard direction reversals are
        //     the other two. Those are the honest reason this defaults off, not the sustained gait.
        // Flip it, walk and sprint around, and watch specifically for the leg visibly stretching in the first
        // moment of a hard start and when jumping mid-run.
        private static readonly bool LocomotionFootIK = false;

        // ── FOOT ROTATION KILL SWITCH ──
        // false => hand SolveLegs the zero-quaternion sentinel, which makes it keep the ANIMATION's foot rotation.
        // That is the long-standing, known-good behaviour: no heel-strike / toe-off / slope adaptation, and a
        // planted foot pivots with the body -- but locomotion is guaranteed intact.
        // true  => drive the foot's rotation from the foot placement driver (SafeFootTargetRotation).
        //
        // ENABLED 2026-07-18. The prerequisites the OFF default was waiting on are now met:
        //  - the project BUILDS (dotnet build "Basis Framework.csproj" clean);
        //  - the math is TESTED (BasisFootFrameTests, 10/10 green: rest reproduces the T-pose rotation so it
        //    cannot come out toes-up, the offset pre-cancel survives the solve's own multiply, swing pitch
        //    plantarflexes at toe-off / dorsiflexes at heel-strike, NaN degrades to the sentinel);
        //  - the footAlign CAPTURE ORDERING is verified correct -- BasisLocalFootDriver.InitializeVariables()
        //    (-> CaptureFootAlignment) runs at BasisLocalAvatarDriver:229, BEFORE ResetAvatarAnimator() at :236,
        //    so it captures the flat T-pose foot (unlike the arm bake, which was the opposite order and wrong).
        // SafeFootTargetRotation still degrades to the sentinel (= this old behaviour) on any NaN/degeneracy, so
        // the floor is exactly what OFF gave. ⚠ VERIFY IN-HEADSET: stand still, arms down -- the feet must sit
        // flat and naturally toed-out, NOT toes-up/tilted; a planted foot must HOLD as you turn, not pivot.
        // Flip back to false if the un-discard misbehaves.
        // static readonly, NOT const: a const would make the ternaries below compile-time-constant and raise
        // CS0429 (unreachable expression code) under warnings-as-errors. The JIT folds this away just the same.
        private static readonly bool FootRotationFromDriver = true;

        // ── ANIMATOR EVALUATION STAGE ──
        // true  => the animator's playable graph stays in GameTime mode and the ENGINE evaluates it in the
        //          PreLateUpdate animation stage, where clip sampling / humanoid retarget / transform writes
        //          (Animators.ProcessAnimationsJob, WriteJob, IKAndTwistBoneJob) run on job-system workers.
        //          A manual PlayableGraph.Evaluate() runs that same pipeline synchronously on the main
        //          thread — profiled at ~0.10 ms of the 0.126 ms IKDestinations block.
        // false => legacy path: Manual time mode + PlayableGraph.Evaluate(deltaTime) in SimulateIKDestinations.
        // Equivalent either way: manual evaluation was only load-bearing while the FBIK lived INSIDE the
        // graph (Animation Rigging, since removed); animator parameters are consumed one evaluate late in
        // BOTH modes (SimulateAnimator runs after the old evaluate point), local bone writes commute with
        // the root moves LateUpdate performs, and the first pose reader (GatherNow) runs after the stage
        // either way. PreLateUpdate sits after all Updates and before all LateUpdates, so no other script
        // phase sees a different pose than before.
        // static readonly, NOT const: same CS0162/CS0429 reasoning as FootRotationFromDriver above.
        private static readonly bool EngineDrivenAnimatorEvaluate = true;

        // Batched filter job state — one slot per S_* index (shoulder slot in position arrays is unused).
        private NativeArray<float3> _posInputs;
        private NativeArray<float3> _posOutputs;
        private NativeArray<quaternion> _rotInputs;
        private NativeArray<quaternion> _rotOutputs;
        private NativeArray<byte> _posModeNative;
        private NativeArray<byte> _rotModeNative;
        private NativeArray<float4> _posTuning;
        private NativeArray<float4> _rotTuning;
        private NativeArray<float3> _fallbackPosStates;
        private NativeArray<quaternion> _fallbackRotStates;
        private NativeArray<BasisEuroVec3State> _euroPosStates;
        private NativeArray<BasisEuroQuatState> _euroRotStates;

        // Post-IK world-pose publish: solved bones read via IJobParallelForTransform, rest via _ikFallbackControls.
        private TransformAccessArray _ikPublishTransforms;
        private BasisLocalBoneControl[] _ikPublishControls;
        private BasisLocalBoneControl[] _ikFallbackControls;
        private NativeArray<float3> _ikPublishPositions;
        private NativeArray<quaternion> _ikPublishRotations;
        public void Initialize(BasisLocalPlayer localPlayer, BasisTransformMapping references)
        {
            this.localPlayer = localPlayer;
            basisTransformMapping = references;
            timeAccumulator = 0f;
        }
        public void BuildBuilder()
        {
            if (localPlayer?.BasisAvatar?.Animator == null || !IKDataReady)
            {
                BasisDebug.LogError("Missing Localplayer || Avatar || Animator || constraint");
                return;
            }

            Animator animator = localPlayer.BasisAvatar.Animator;
            PlayableGraph = animator.playableGraph;
            PlayableGraph.SetTimeUpdateMode(EngineDrivenAnimatorEvaluate ? DirectorUpdateMode.GameTime : DirectorUpdateMode.Manual);

            LocomotionPose.CompleteIfPending();
            PoseSkeleton.Build(animator.transform, CollectIKBones(basisTransformMapping));
            PoseSkeleton.SetTranslationFree(basisTransformMapping.Hips);
            BasisEerieMovementSetup.Create(ref IKJob, PoseSkeleton, basisTransformMapping);
            IKJobCreated = true;

            ResetSmoothingState();
            RefreshBodyFit();
            LocomotionPose.OnRigBuilt();
        }

        public void RefreshBodyFit()
        {
            if (!PoseSkeleton.IsCreated || basisTransformMapping == null)
            {
                return;
            }

            // The locomotion pose job writes the stream on a worker; join it before the fit paths below
            // touch Stream.LocalPosition from the main thread.
            LocomotionPose.CompleteIfPending();

            if (!Basis.BasisUI.BasisSettingsDefaults.FBIKBodyFit.RawValue)
            {
                if (PoseSkeleton.FitActive)
                {
                    PoseSkeleton.ResetFit();
                    PoseSkeleton.WriteFittedLocalPositions();
                }
                AppliedBodyFit = BasisBodyFitResult.Identity;
                BasisBodyFitNetworking.UpdateLocalFit(in AppliedBodyFit);
                BasisLocalPlayer.Instance?.BasisLocalFootDriver?.RefreshBodyFitScale();
                return;
            }

            var measurements = new BasisBodyFitMeasurements
            {
                PlayerEyeHeight = BasisHeightDriver.PlayerEyeHeight,
                PlayerArmSpan = BasisHeightDriver.PlayerArmSpan,
                PlayerHipHeight = BasisHeightDriver.PlayerHipHeight,
                AvatarEyeHeight = BasisHeightDriver.AvatarEyeHeight,
                AvatarArmSpan = BasisHeightDriver.AvatarArmSpan,
                AvatarHipHeight = BasisHeightDriver.AvatarHipHeight,
                AvatarLegSpan = BasisHeightDriver.AvatarLegSpan,
                AvatarSpineSpan = BasisHeightDriver.AvatarSpineSpan,
                AvatarShoulderWidth = BasisHeightDriver.AvatarShoulderWidth,
            };

            BasisBodyFitResult fit = BasisBodyFitCore.Solve(
                measurements,
                Basis.BasisUI.BasisSettingsDefaults.FBIKBodyFitMaxDeviation.RawValue);

            BasisBodyFitApply.Apply(PoseSkeleton, basisTransformMapping, fit);
            AppliedBodyFit = fit;

            // Remotes render the authored avatar unless they are told these scales — the pose channel
            // carries rotations only, never segment lengths. Send-on-change lives in the networking
            // class; this runs on every rig build and settings change, most of which are no-ops.
            BasisBodyFitNetworking.UpdateLocalFit(in fit);

            // Push the new lengths onto the bone transforms right now rather than waiting for the next
            // RunIKSolve scatter. Calibration captures its tracker offsets against live bone positions
            // (see BasisAvatarIKStageCalibration's one-scale-frame note), so a fit that lands a frame
            // later would leave every captured offset describing a body the avatar no longer has.
            PoseSkeleton.WriteFittedLocalPositions();
            BasisLocalPlayer.Instance?.BasisLocalFootDriver?.RefreshBodyFitScale();

            if (fit.HasArmFit)
            {
                BasisDebug.Log($"Body fit: arms scaled {fit.ArmScale:F4}", BasisDebug.LogTag.IK);
            }
            else
            {
                BasisDebug.Log($"Body fit: arms not fitted - {BasisBodyFitCore.Describe(fit.ArmStatus)}", BasisDebug.LogTag.IK);
            }

            if (fit.HasBodyFit)
            {
                BasisDebug.Log($"Body fit: legs scaled {fit.LegScale:F4}, spine scaled {fit.TorsoScale:F4}", BasisDebug.LogTag.IK);
            }
            else
            {
                BasisDebug.Log($"Body fit: legs and spine not fitted - {BasisBodyFitCore.Describe(fit.BodyStatus)}", BasisDebug.LogTag.IK);
            }
        }


        public static BasisBodyFitResult AppliedBodyFit = BasisBodyFitResult.Identity;

        public void SetBodySettings()
        {
            // Drop the prior recalibration first: a never-calibrated avatar then uses its own uncalibrated
            // (animator-relative) setup capture from CreateBasisFullBodyRIG.
            HasRecalibratedRotationOffsets = false;
            Spine();
            BasisLocalBoneControl.HasEvents = true;
            // Keep FBT rotation calibration across avatar swaps: re-derive this avatar's per-effector offsets
            // from the stored calibration reference. No-op until the user has calibrated.
            ApplyCalibrationToCurrentAvatar();

            BuildIKPublishArrays();
        }

        public void CleanupBeforeContinue()
        {
            BasisLocalPlayer.OnPlayersHeightChangedNextFrame -= OnPlayersHeightChangedNextFrame;
            LocomotionPose.Dispose();
            DisposeFilterArrays();
            DisposeIKPublishArrays();

            if (IKJobCreated)
            {
                IKJob.Destroy();
                IKJob = default;
                IKJobCreated = false;
            }
            PoseSkeleton.Dispose();
            IKDataReady = false;
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
            _posTuning = new NativeArray<float4>(SlotCount, Allocator.Persistent);
            _rotTuning = new NativeArray<float4>(SlotCount, Allocator.Persistent);
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
            if (_posTuning.IsCreated) _posTuning.Dispose();
            if (_rotTuning.IsCreated) _rotTuning.Dispose();
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

        private static readonly float4[] _groupPosTuning = new float4[BasisSmoothingProfiles.GroupCount];
        private static readonly float4[] _groupRotTuning = new float4[BasisSmoothingProfiles.GroupCount];
        private static readonly bool[] _groupOff = new bool[BasisSmoothingProfiles.GroupCount];
        private static readonly BasisTrackingHardware[] _groupHardware = new BasisTrackingHardware[BasisSmoothingProfiles.GroupCount];

        /// <summary>
        /// Notes the noisiest tracking technology feeding each body group, so the Auto preset can filter a
        /// group for the hardware it actually has. Recomputed rather than cached: devices connect, get
        /// re-roled by calibration, and on Quest a hand swaps between controller and camera tracking mid
        /// session, so a cached map would go stale silently. It is a dozen devices and only runs when
        /// something is set to Auto.
        /// </summary>
        private static void ResolveGroupHardware()
        {
            for (int Index = 0; Index < _groupHardware.Length; Index++)
            {
                _groupHardware[Index] = BasisTrackingHardware.Unknown;
            }

            BasisDeviceManagement manager = BasisDeviceManagement.Instance;
            if (manager == null)
            {
                return;
            }

            var devices = manager.AllInputDevices;
            for (int Index = 0; Index < devices.Count; Index++)
            {
                BasisInput device = devices[Index];
                // Linked halves are averaged into a virtual midpoint that carries their hardware already;
                // counting them too would say nothing new.
                if (device == null || device.IsLinked)
                {
                    continue;
                }

                if (!device.TryGetRole(out BasisBoneTrackedRole role) ||
                    !BasisSmoothingProfiles.TryGetGroupForRole(role, out int group))
                {
                    continue;
                }

                if ((byte)device.TrackingHardware > (byte)_groupHardware[group])
                {
                    _groupHardware[group] = device.TrackingHardware;
                }
            }
        }

        private static bool AnyGroupIsAuto(Basis.BasisUI.BasisSettingsDefaults.SmoothingGroupBindings[] groups)
        {
            for (int Index = 0; Index < BasisSmoothingProfiles.GroupCount; Index++)
            {
                if (BasisSmoothingProfiles.IsAuto(groups[Index].Preset.RawValue))
                {
                    return true;
                }
            }

            return false;
        }

        private static void ResolveSmoothingGroups(float deltaTime)
        {
            var groups = Basis.BasisUI.BasisSettingsDefaults.FBIKSmoothingGroups;
            if (AnyGroupIsAuto(groups))
            {
                ResolveGroupHardware();
            }

            for (int Index = 0; Index < BasisSmoothingProfiles.GroupCount; Index++)
            {
                var bindings = groups[Index];
                string preset = bindings.Preset.RawValue;
                // Auto resolves to a real preset up front, so everything below is unchanged by it.
                if (BasisSmoothingProfiles.IsAuto(preset))
                {
                    preset = BasisSmoothingProfiles.PresetForHardware(_groupHardware[Index]);
                }

                _groupOff[Index] = BasisSmoothingProfiles.IsOff(preset);

                BasisSmoothingProfile profile;
                float strength;
                if (BasisSmoothingProfiles.IsCustom(preset))
                {
                    profile = new BasisSmoothingProfile(
                        bindings.MinCutoff.RawValue,
                        bindings.Beta.RawValue,
                        DerivativeCutoff,
                        bindings.PositionHz.RawValue,
                        bindings.RotationHz.RawValue);
                    strength = Mathf.Max(1f, bindings.Strength.RawValue);
                }
                else
                {
                    if (!BasisSmoothingProfiles.TryGetPreset(preset, out profile))
                    {
                        profile = new BasisSmoothingProfile(MinCutoff, Beta, DerivativeCutoff, PositionSmoothingHz, RotationSmoothingHz);
                    }
                    strength = Mathf.Max(1f, SmoothingStrength);
                }

                float minCutoff = profile.MinCutoff / strength;
                float dCutoff = profile.DerivativeCutoff / strength;
                _groupPosTuning[Index] = new float4(minCutoff, profile.Beta, dCutoff, ExpAlpha(profile.PositionHz / strength, deltaTime));
                _groupRotTuning[Index] = new float4(minCutoff, profile.Beta, dCutoff, ExpAlpha(profile.RotationHz / strength, deltaTime));
            }
        }
        public void OnTPose() => OnTPose(BasisLocalAvatarDriver.CurrentlyTposing);

        public void OnTPose(bool currentlyTposing)
        {
            if (currentlyTposing)
            {
                RigLayerActive = false;
                return;
            }

            RigLayerActive = true;
            RestoreAllTrackers();

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

            // Per-avatar smoothing state: a new avatar must not inherit the previous one's
            // mid-flight foot-IK blend, butterfly hint/weight, or stationary hysteresis.
            smoothedLeftButterflyHint = smoothedRightButterflyHint = Vector3.zero;
            smoothedLeftButterflyWeight = smoothedRightButterflyWeight = 0f;
            smoothedLeftKneeFwdHint = smoothedRightKneeFwdHint = Vector3.zero;
            smoothedLeftKneeFwdWeight = smoothedRightKneeFwdWeight = 0f;
            footIKBlendWeightLeft = footIKBlendWeightRight = footIKBlendWeight = 0f;
            stationaryTimer = 0f;
        }
        /// <summary>
        /// Called at the top of BasisLocalPlayer.Simulate so the locomotion pose job (when active)
        /// overlaps movement, bone sim, and the IK input prep on worker threads.
        /// </summary>
        public void ScheduleLocomotionPose(BasisLocalPlayer player, float deltaTime)
        {
            Animator animator = player?.BasisAvatar != null ? player.BasisAvatar.Animator : null;
            BasisLocoParams frameParams = player.LocalAnimatorDriver.GetLocoParams();
            LocomotionPose.Schedule(this, animator, in frameParams, deltaTime);
        }

        static readonly ProfilerMarker sMarkerIKDestPrep = new ProfilerMarker("BasisDriver.LocalPlayer.IKDest.Prep");
        static readonly ProfilerMarker sMarkerIKDestFootSchedule = new ProfilerMarker("BasisDriver.LocalPlayer.IKDest.FootSchedule");
        static readonly ProfilerMarker sMarkerIKDestGatherTargets = new ProfilerMarker("BasisDriver.LocalPlayer.IKDest.GatherTargets");
        static readonly ProfilerMarker sMarkerIKDestFilters = new ProfilerMarker("BasisDriver.LocalPlayer.IKDest.Filters");
        static readonly ProfilerMarker sMarkerIKDestFootJoin = new ProfilerMarker("BasisDriver.LocalPlayer.IKDest.FootJoin");
        static readonly ProfilerMarker sMarkerIKDestBuildTargets = new ProfilerMarker("BasisDriver.LocalPlayer.IKDest.BuildIKTargets");
        static readonly ProfilerMarker sMarkerIKDestAnimatorEval = new ProfilerMarker("BasisDriver.LocalPlayer.IKDest.AnimatorEvaluate");
        static readonly ProfilerMarker sMarkerIKDestLocoJoin = new ProfilerMarker("BasisDriver.LocalPlayer.IKDest.LocoPoseJoin");
        static readonly ProfilerMarker sMarkerIKDestPoseGather = new ProfilerMarker("BasisDriver.LocalPlayer.IKDest.PoseGather");
        static readonly ProfilerMarker sMarkerIKDestApplyFit = new ProfilerMarker("BasisDriver.LocalPlayer.IKDest.ApplyFit");
        static readonly ProfilerMarker sMarkerIKDestSolve = new ProfilerMarker("BasisDriver.LocalPlayer.IKDest.Solve");
        static readonly ProfilerMarker sMarkerIKDestPoseScatter = new ProfilerMarker("BasisDriver.LocalPlayer.IKDest.PoseScatter");
        static readonly ProfilerMarker sMarkerIKDestPublish = new ProfilerMarker("BasisDriver.LocalPlayer.IKDest.PublishWorldData");

        public void SimulateIKDestinations(float deltaTime)
        {
            if (!IKDataReady || !IKJobCreated)
            {
                return;
            }

            if (!PlayableGraph.IsValid())
            {
                return;
            }

            timeAccumulator += Mathf.Max(deltaTime, 1e-6f);

            sMarkerIKDestPrep.Begin();
            EnsureFilterArrays();

            // Filter tuning is per smoothing group; resolve the 7 groups once, then scatter to the 15 slots.
            ResolveSmoothingGroups(deltaTime);
            sMarkerIKDestPrep.End();
            float safeDt = Mathf.Max(deltaTime, 1e-6f);

            // ── 1. Schedule foot sim FIRST ──
            // It is one long single-threaded Burst job, and everything from the bone gather through the filter
            // scheduling below reads none of its state, so it all overlaps. Scheduled after the filter jobs it
            // had ~20 lines to finish in and CompleteSimulate was mostly a stall.
            sMarkerIKDestFootSchedule.Begin();
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
            bool footIKReady = footDriverReady && (LocomotionFootIK || isStationaryEnough) && footIKSetting;
            bool leftWantIK = footIKReady && !leftHasTracker;
            bool rightWantIK = footIKReady && !rightHasTracker;
            bool leftOrRightDrive = !leftHasTracker || !rightHasTracker;

            bool footSimScheduled = false;
            if (footDriverReady && leftOrRightDrive)
            {
                footDriver.ScheduleSimulate(deltaTime);
                footSimScheduled = true;
            }
            sMarkerIKDestFootSchedule.End();

            // ── 2. Gather raw inputs from bone controls (main thread only) ──
            sMarkerIKDestGatherTargets.Begin();
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
                float4* posTunePtr = (float4*)_posTuning.GetUnsafePtr();
                float4* rotTunePtr = (float4*)_rotTuning.GetUnsafePtr();
                BasisEuroVec3State* euroPosPtr = (BasisEuroVec3State*)_euroPosStates.GetUnsafePtr();
                BasisEuroQuatState* euroRotPtr = (BasisEuroQuatState*)_euroRotStates.GetUnsafePtr();
                float3* fallbackPosPtr = (float3*)_fallbackPosStates.GetUnsafePtr();
                quaternion* fallbackRotPtr = (quaternion*)_fallbackRotStates.GetUnsafePtr();

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

                // ── 3. Compute filter modes from toggles, and scatter each slot's group tuning ──
                for (int i = 0; i < SlotCount; i++)
                {
                    byte group = BasisSmoothingProfiles.SlotGroup[i];
                    posTunePtr[i] = _groupPosTuning[group];
                    rotTunePtr[i] = _groupRotTuning[group];

                    byte newPosMode = (byte)BasisFilterMode.Passthrough;
                    byte newRotMode = (byte)BasisFilterMode.Passthrough;
                    if (!_groupOff[group])
                    {
                        newPosMode = PickMode(SmoothPos[i], EuroPos[i]);
                        newRotMode = PickMode(SmoothRot[i], EuroRot[i]);
                    }

                    // Changing a preset live re-modes the slot. Its filter state is then whatever the previous
                    // mode left behind, which would glide the bone in from a stale pose; reseed from the live
                    // input so a settings change is silent.
                    if (newPosMode != posModePtr[i])
                    {
                        euroPosPtr[i] = default;
                        fallbackPosPtr[i] = posPtr[i];
                    }
                    if (newRotMode != rotModePtr[i])
                    {
                        euroRotPtr[i] = default;
                        fallbackRotPtr[i] = rotPtr[i];
                    }

                    posModePtr[i] = newPosMode;
                    rotModePtr[i] = newRotMode;
                }
                // Shoulders have no position target — always passthrough to skip wasted work.
                posModePtr[S_LeftShoulder] = (byte)BasisFilterMode.Passthrough;
                posModePtr[S_RightShoulder] = (byte)BasisFilterMode.Passthrough;
            }

            // ── 4. On first use, seed fallback states from live inputs so we don't lerp from zero ──
            if (!hasFallbackState)
            {
                hasFallbackState = true;
                _fallbackPosStates.CopyFrom(_posInputs);
                _fallbackRotStates.CopyFrom(_rotInputs);
            }
            sMarkerIKDestGatherTargets.End();

            // ── 5. Schedule batched filter jobs ──
            sMarkerIKDestFilters.Begin();
            var posJob = new BasisBatchPositionFilterJob
            {
                mode = _posModeNative,
                rawInputs = _posInputs,
                tuning = _posTuning,
                euroStates = _euroPosStates,
                fallbackStates = _fallbackPosStates,
                outputs = _posOutputs,
                dt = safeDt,
            };
            var rotJob = new BasisBatchRotationFilterJob
            {
                mode = _rotModeNative,
                rawInputs = _rotInputs,
                tuning = _rotTuning,
                euroStates = _euroRotStates,
                fallbackStates = _fallbackRotStates,
                outputs = _rotOutputs,
                dt = safeDt,
            };
            // Run inline: 15 slots of Burst filter math is microseconds, below the cost of
            // dispatching two parallel-for jobs (batch 4 → up to eight slices) and fencing
            // on them a few lines later. The foot sim keeps its worker-side window — it was
            // scheduled earlier and still completes below.
            posJob.Run(SlotCount);
            rotJob.Run(SlotCount);
            sMarkerIKDestFilters.End();

            // ── 6. Main-thread bookkeeping runs parallel with the foot job ──
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
            footIKBlendWeight = Mathf.Min(footIKBlendWeightLeft, footIKBlendWeightRight);

            bool notifyReengage = footDriverReady &&
                ((leftPrevBlend < 0.001f && footIKBlendWeightLeft >= 0.001f)
                 || (rightPrevBlend < 0.001f && footIKBlendWeightRight >= 0.001f));

            bool leftLLHasTracker = fbtEnabled && BasisLocalBoneDriver.LeftLowerLegControl.HasTracked == BasisHasTracked.HasTracker;
            bool rightLLHasTracker = fbtEnabled && BasisLocalBoneDriver.RightLowerLegControl.HasTracked == BasisHasTracked.HasTracker;
            bool hipsHaveTracker = fbtEnabled && BasisLocalBoneDriver.HipsControl.HasTracked == BasisHasTracked.HasTracker;
            bool trackerBendNormal = Basis.BasisUI.BasisSettingsDefaults.FBIKTrackerBendNormal.RawValue;

            // ── 7. Wait for the foot job ──
            sMarkerIKDestFootJoin.Begin();
            if (footSimScheduled) footDriver.CompleteSimulate();

            // NotifyReEngaging reads live bone control data (not foot sim output), but kept after
            // completion so all foot state is coherent when the next sim starts.
            if (notifyReengage) footDriver.NotifyReEngaging();

            // Surface probes cast from the feet's FINAL positions this frame and are consumed at the top of
            // next frame's ScheduleSimulate, so the rays run on a worker across the frame boundary and cost
            // the main thread nothing. Must stay after NotifyReEngaging, which rewrites currentPos.
            if (footSimScheduled) footDriver.ScheduleSurfaceProbes();
            sMarkerIKDestFootJoin.End();

            // ── 8. Scatter filter outputs into BasisFullIKConstraintJob ──
            sMarkerIKDestBuildTargets.Begin();
            ref BasisEerieMovement data = ref IKJob;

            // Pull out pointers once; avoids per-slot safety-handle checks on each indexer read.
            Vector3 hipsPos;
            Quaternion hipsRot;
            Vector3 chestPos;
            Quaternion chestRot;
            Vector3 llaPos, rlaPos;
            Quaternion llaRot, rlaRot;
            Vector3 playerUpScaled = BasisLocalPlayer.localToWorldMatrix.MultiplyVector(Vector3.up);
            float playerUpScale = playerUpScaled.magnitude;
            Vector3 playerUpDir = playerUpScale > 1e-6f ? playerUpScaled / playerUpScale : Vector3.up;
            unsafe
            {
                float3* pOut = (float3*)_posOutputs.GetUnsafeReadOnlyPtr();
                quaternion* rOut = (quaternion*)_rotOutputs.GetUnsafeReadOnlyPtr();

                hipsPos = pOut[S_Hips];
                hipsRot = rOut[S_Hips];
                hipsPos -= playerUpDir * localPlayer.LocalCharacterDriver.landingCrouchEffect;
                data.targetPositionHips = hipsPos;
                data.targetRotationHips = hipsRot;
                data.hasHipsTracker = hipsHaveTracker;
                // Per frame, not just on OnHasRigChanged: the weight moves continuously while a source fades.
                data.enabledLeftHand = HandRigWeight(BasisLocalBoneDriver.LeftHandControl);
                data.enabledRightHand = HandRigWeight(BasisLocalBoneDriver.RightHandControl);

                data.targetPositionHead = pOut[S_Head];
                data.targetRotationHead = rOut[S_Head];

                // True crouch depth for the sit-back (BasisCrouchOffsetCore): rest head height minus the
                // head target's height, in playspace-local space so walking, teleports and slopes cancel.
                // It cannot be derived inside the job -- the lock-mode stage restores the head-hips
                // separation to rest length before the crouch stage reads it. Seats force it to zero: a
                // chair-sitter's head is low with the hips forward onto the seat, the opposite of a squat.
                float restHeadLocalY = BasisLocalBoneDriver.HeadControl.TposeLocalScaled.position.y;
                data.standingHeadHeight = Mathf.Max(0f, restHeadLocalY * playerUpScale);
                if (localPlayer.LocalSeatDriver.IsSeated || playerUpScale <= 1e-6f)
                {
                    data.crouchDepth = 0f;
                }
                else
                {
                    float headLocalY = BasisLocalPlayer.localToWorldMatrix.inverse.MultiplyPoint3x4((Vector3)pOut[S_Head]).y;
                    data.crouchDepth = Mathf.Max(0f, (restHeadLocalY - headLocalY) * playerUpScale);
                }

                // ── LEFT FOOT ──
                data.footIsTrackerLeftLeg = leftHasTracker;
                if (leftHasTracker)
                {
                    data.targetPositionLeftLowerLeg = pOut[S_LeftFoot];
                    data.targetRotationLeftLowerLeg = rOut[S_LeftFoot];
                    // Re-assert full weight every frame: HasTracked can flip (occlusion, dropout)
                    // without firing OnHasRigChanged, and the foot-sim branch below writes fractional
                    // blend weights that would otherwise stick when the tracker returns.
                    data.enabledLeftLowerLeg = 1f;
                }
                else if (footIKBlendWeightLeft > 0.001f && footDriverReady)
                {
                    data.targetPositionLeftLowerLeg = footDriver.LeftFootPosition;
                    // Foot rotation is LIVE again. It used to be discarded via the zero-quaternion sentinel
                    // (-> SolveLegs kept the animation rotation) because feeding it produced a toes-up foot -- the
                    // driver was handing over a frame built from the BODY's axes, which are not the foot bone's.
                    // FootRotation() now re-seats that frame through the bone's calibrated rest orientation
                    // (footAlign), so a standing foot reproduces its rest rotation exactly. With it live we finally
                    // get: a planted foot HELD in the world (it no longer pivots as the body turns), heel-strike /
                    // toe-off through the swing, and slope adaptation.
                    // PRE-CANCEL THE CALIBRATION OFFSET. SolveLegs hands targetOffsetLeftFoot to SolveTwoBone, which
                    // applies it to the target as `target * offset` -- because the TRACKER path feeds a tracker
                    // rotation, and the offset is what maps the tracker's frame onto the bone's frame. The foot
                    // driver has no tracker: it already emits the finished BONE rotation, so that offset is pure
                    // surplus and lands the foot at footRot*offset. It is CALIBRATED PER AVATAR, which is exactly
                    // why the error is a different wrong angle on every rig instead of a constant one.
                    //
                    // Multiplying by its inverse here makes the solve's own `target * offset` collapse back to the
                    // rotation we meant: (footRot * offset^-1) * offset == footRot.
                    //
                    // This is the "toes-up" that got foot rotation switched off in the first place -- the sentinel
                    // on the zero quaternion existed to dodge this exact multiply, not to dodge a bad frame.
                    data.targetRotationLeftLowerLeg = FootRotationFromDriver
                        ? SafeFootTargetRotation(footDriver.LeftFootRotation, data.offsetRotationLeftFoot)
                        : PreserveTipSentinel;
                    data.enabledLeftLowerLeg = footIKBlendWeightLeft;
                }
                else
                {
                    data.enabledLeftLowerLeg = 0f;
                }

                // ── RIGHT FOOT ──
                data.footIsTrackerRightLeg = rightHasTracker;
                if (rightHasTracker)
                {
                    data.targetPositionRightLowerLeg = pOut[S_RightFoot];
                    data.targetRotationRightLowerLeg = rOut[S_RightFoot];
                    data.enabledRightLowerLeg = 1f;
                }
                else if (footIKBlendWeightRight > 0.001f && footDriverReady)
                {
                    data.targetPositionRightLowerLeg = footDriver.RightFootPosition;
                    data.targetRotationRightLowerLeg = FootRotationFromDriver
                        ? SafeFootTargetRotation(footDriver.RightFootRotation, data.offsetRotationRightFoot)
                        : PreserveTipSentinel;
                    data.enabledRightLowerLeg = footIKBlendWeightRight;
                }
                else
                {
                    data.enabledRightLowerLeg = 0f;
                }

                if (BasisFootRotationDebug.Enabled)
                {
                    if (basisTransformMapping.leftFoot != null)
                        BasisFootRotationDebug.Record("L", Time.time, footIKBlendWeightLeft,
                            !leftHasTracker && footIKBlendWeightLeft > 0.001f && footDriverReady,
                            basisTransformMapping.leftFoot.rotation, data.targetRotationLeftLowerLeg, data.offsetRotationLeftFoot,
                            BasisLocalBoneDriver.LeftFootControl.OutGoingData.rotation,
                            BasisLocalBoneDriver.LeftFootControl.OutgoingWorldData.rotation,
                            (Quaternion)rOut[S_LeftFoot], footDriverReady ? footDriver.LeftFootRotation : Quaternion.identity);
                    if (basisTransformMapping.rightFoot != null)
                        BasisFootRotationDebug.Record("R", Time.time, footIKBlendWeightRight,
                            !rightHasTracker && footIKBlendWeightRight > 0.001f && footDriverReady,
                            basisTransformMapping.rightFoot.rotation, data.targetRotationRightLowerLeg, data.offsetRotationRightFoot,
                            BasisLocalBoneDriver.RightFootControl.OutGoingData.rotation,
                            BasisLocalBoneDriver.RightFootControl.OutgoingWorldData.rotation,
                            (Quaternion)rOut[S_RightFoot], footDriverReady ? footDriver.RightFootRotation : Quaternion.identity);
                }

                // ── HIP BOB + LATERAL SWAY + PELVIS ROTATION ──
                // All three are gated on !hipsHaveTracker: with a hip tracker the pelvis is the user's own, and
                // synthesising gait motion on top of it would fight their real body. (This is gait-driven pelvis
                // motion in the ABSENCE of a tracker -- it is not, and must not become, tracker tilt stabilisation.)
                if (footIKBlendWeight > 0.001f && footDriverReady && !hipsHaveTracker)
                {
                    data.targetPositionHips += playerUpDir * (footDriver.ComputeHipBob() * footIKBlendWeight);
                    data.targetPositionHips += footDriver.ComputeHipSway() * footIKBlendWeight;

                    // Axial rotation + frontal list, blended in by weight so it fades with the rest of foot IK.
                    Quaternion pelvis = Quaternion.Slerp(Quaternion.identity, footDriver.ComputePelvisDelta(), footIKBlendWeight);
                    data.targetRotationHips = pelvis * data.targetRotationHips;
                }

                // ── CHEST (head hint) ──
                chestPos = pOut[S_Chest];
                chestRot = rOut[S_Chest];
                // The chest IK target needs the ACTUAL chest, before the head-hint bias below (which shoves it
                // ~8cm 'up in chest frame' to steer the head solve). Pinning the chest to the biased value
                // leaned the whole torso.
                data.targetPositionChestRaw = chestPos;
                chestPos = ApplyHintBias(BasisBoneTrackedRole.Chest, chestPos, chestRot);
                data.targetPositionChest = chestPos;
                data.targetRotationChest = chestRot;

                // ── KNEE POLE (tracked feet, no knee tracker): foot-forward azimuth + butterfly splay ──
                bool butterflyEnabled = Basis.BasisUI.BasisSettingsDefaults.FBIKButterflyKnees.RawValue;
                float butterflyMaxOpenDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKButterflyKneeMaxOpenDeg.RawValue;
                float butterflySupineFloor = 1f; // merged toggle: butterfly knees works both supine and upright when enabled
                bool kneeFollowsFoot = Basis.BasisUI.BasisSettingsDefaults.FBIKKneeFollowsFoot.RawValue;
                float kneeFootCoupling = Basis.BasisUI.BasisSettingsDefaults.FBIKKneeFootFollowUpright.RawValue;
                float kneeFwdSmoothRate = KneeForwardSmoothRate;
                Vector3 hipsForwardDir = hipsRot * Vector3.forward;
                bool leftFootTracked = fbtEnabled && BasisLocalBoneDriver.LeftFootControl.HasTracked == BasisHasTracked.HasTracker;
                bool rightFootTracked = fbtEnabled && BasisLocalBoneDriver.RightFootControl.HasTracked == BasisHasTracked.HasTracker;

                // ── LEFT LOWER LEG ──
                if (leftLLHasTracker)
                {
                    Vector3 lllPos = pOut[S_LeftLowerLeg];
                    Quaternion lllRot = rOut[S_LeftLowerLeg];
                    lllPos = ApplyHintBias(BasisBoneTrackedRole.LeftLowerLeg, lllPos, lllRot);
                    data.hintPositionLeftLowerLeg = lllPos;
                    data.hintWeightLeftLowerLeg = 1f;
                }
                else if (footIKBlendWeightLeft > 0.001f && footDriverReady)
                {
                    data.hintPositionLeftLowerLeg = footDriver.LeftKneeHint;
                    data.hintWeightLeftLowerLeg = footIKBlendWeightLeft;
                }
                else if (leftFootTracked)
                {
                    Vector3 lBendDir = hipsForwardDir;
                    Vector3 lKneeFwdHint = default;
                    float lKneeFwdWeight = 0f;
                    bool lHaveKneeFwd = kneeFollowsFoot && TryComputeKneeForward(
                        hipsRot, kneeFootCoupling, kneeFwdSmoothRate, playerUpDir, deltaTime,
                        basisTransformMapping.LeftUpperLeg, basisTransformMapping.LeftLowerLeg, data.targetPositionLeftLowerLeg, data.targetRotationLeftLowerLeg,
                        ref smoothedLeftKneeFwdHint, ref smoothedLeftKneeFwdWeight,
                        out lKneeFwdHint, out lKneeFwdWeight, out lBendDir);

                    if (butterflyEnabled && TryComputeButterflyKnee(
                        true, hipsRot, playerUpDir, butterflyMaxOpenDeg, butterflySupineFloor, deltaTime, lBendDir,
                        basisTransformMapping.LeftUpperLeg, basisTransformMapping.LeftLowerLeg, data.targetPositionLeftLowerLeg, data.targetRotationLeftLowerLeg,
                        ref smoothedLeftButterflyHint, ref smoothedLeftButterflyWeight,
                        out Vector3 lButterflyHint, out float lButterflyWeight))
                    {
                        data.hintPositionLeftLowerLeg = lButterflyHint;
                        data.hintWeightLeftLowerLeg = lButterflyWeight;
                    }
                    else if (lHaveKneeFwd && lKneeFwdWeight > 0.001f)
                    {
                        data.hintPositionLeftLowerLeg = lKneeFwdHint;
                        data.hintWeightLeftLowerLeg = lKneeFwdWeight;
                    }
                    else
                    {
                        data.hintWeightLeftLowerLeg = 0f;
                    }
                }
                else
                {
                    data.hintWeightLeftLowerLeg = 0f;
                }

                // ── RIGHT LOWER LEG ──
                if (rightLLHasTracker)
                {
                    Vector3 rllPos = pOut[S_RightLowerLeg];
                    Quaternion rllRot = rOut[S_RightLowerLeg];
                    rllPos = ApplyHintBias(BasisBoneTrackedRole.RightLowerLeg, rllPos, rllRot);
                    data.hintPositionRightLowerLeg = rllPos;
                    data.hintWeightRightLowerLeg = 1f;
                }
                else if (footIKBlendWeightRight > 0.001f && footDriverReady)
                {
                    data.hintPositionRightLowerLeg = footDriver.RightKneeHint;
                    data.hintWeightRightLowerLeg = footIKBlendWeightRight;
                }
                else if (rightFootTracked)
                {
                    Vector3 rBendDir = hipsForwardDir;
                    Vector3 rKneeFwdHint = default;
                    float rKneeFwdWeight = 0f;
                    bool rHaveKneeFwd = kneeFollowsFoot && TryComputeKneeForward(
                        hipsRot, kneeFootCoupling, kneeFwdSmoothRate, playerUpDir, deltaTime,
                        basisTransformMapping.RightUpperLeg, basisTransformMapping.RightLowerLeg, data.targetPositionRightLowerLeg, data.targetRotationRightLowerLeg,
                        ref smoothedRightKneeFwdHint, ref smoothedRightKneeFwdWeight,
                        out rKneeFwdHint, out rKneeFwdWeight, out rBendDir);

                    if (butterflyEnabled && TryComputeButterflyKnee(
                        false, hipsRot, playerUpDir, butterflyMaxOpenDeg, butterflySupineFloor, deltaTime, rBendDir,
                        basisTransformMapping.RightUpperLeg, basisTransformMapping.RightLowerLeg, data.targetPositionRightLowerLeg, data.targetRotationRightLowerLeg,
                        ref smoothedRightButterflyHint, ref smoothedRightButterflyWeight,
                        out Vector3 rButterflyHint, out float rButterflyWeight))
                    {
                        data.hintPositionRightLowerLeg = rButterflyHint;
                        data.hintWeightRightLowerLeg = rButterflyWeight;
                    }
                    else if (rHaveKneeFwd && rKneeFwdWeight > 0.001f)
                    {
                        data.hintPositionRightLowerLeg = rKneeFwdHint;
                        data.hintWeightRightLowerLeg = rKneeFwdWeight;
                    }
                    else
                    {
                        data.hintWeightRightLowerLeg = 0f;
                    }
                }
                else
                {
                    data.hintWeightRightLowerLeg = 0f;
                }

                // Tell the leg solve which knee poles are physical trackers (jittery, and pole-amplified by
                // the solve) so it applies the responsive output-swivel smoothing on that path. Computed hints
                // (foot driver / butterfly) are already smooth and stay untouched.
                data.hintIsTrackerLeftLowerLeg = leftLLHasTracker;
                data.hintIsTrackerRightLowerLeg = rightLLHasTracker;

                if (BasisLegCrouchDebug.Enabled)
                {
                    if (basisTransformMapping.LeftUpperLeg != null && basisTransformMapping.LeftLowerLeg != null && basisTransformMapping.leftFoot != null)
                    {
                        Vector3 hipL = basisTransformMapping.LeftUpperLeg.position, kneeL = basisTransformMapping.LeftLowerLeg.position;
                        float legLenL = Vector3.Distance(hipL, kneeL) + Vector3.Distance(kneeL, basisTransformMapping.leftFoot.position);
                        BasisLegCrouchDebug.Record("L", Time.time, !leftHasTracker && footIKBlendWeightLeft > 0.001f && footDriverReady,
                            legLenL, hipL, data.targetPositionLeftLowerLeg, data.hintPositionLeftLowerLeg, kneeL);
                    }
                    if (basisTransformMapping.RightUpperLeg != null && basisTransformMapping.RightLowerLeg != null && basisTransformMapping.rightFoot != null)
                    {
                        Vector3 hipR = basisTransformMapping.RightUpperLeg.position, kneeR = basisTransformMapping.RightLowerLeg.position;
                        float legLenR = Vector3.Distance(hipR, kneeR) + Vector3.Distance(kneeR, basisTransformMapping.rightFoot.position);
                        BasisLegCrouchDebug.Record("R", Time.time, !rightHasTracker && footIKBlendWeightRight > 0.001f && footDriverReady,
                            legLenR, hipR, data.targetPositionRightLowerLeg, data.hintPositionRightLowerLeg, kneeR);
                    }
                }

                // ── HANDS ──
                data.targetPositionLeftHand = pOut[S_LeftHand];
                data.targetRotationLeftHand = rOut[S_LeftHand];
                data.targetPositionRightHand = pOut[S_RightHand];
                data.targetRotationRightHand = rOut[S_RightHand];

                // ── LOWER ARMS (elbow hints) ──
                // NOTE: no ApplyHintBias here -- a tracker-local lower-arm offset swings with forearm pronation
                // (the forearm rolls about its own axis) and keys off a solver-overwritten bone, which pops the
                // elbow. The knees keep their bias only because the knee is a hinge. Elbow-tracker conditioning
                // is handled solver-side (BasisArmSolveCore HintIsTracker), not by a tracker-local offset.
                // The ROTATION is mapped through the calibration reference, exactly as the lower legs are: the
                // solve compares it against the solved forearm, and an elbow strap's clock angle is arbitrary.
                // No reference (never calibrated) leaves the zero quaternion, which the solve reads as off.
                llaPos = pOut[S_LeftLowerArm];
                llaRot = rOut[S_LeftLowerArm];
                data.hintPositionLeftHand = llaPos;
                data.hintRotationLeftHand = BasisLimbRollStore.TryGet(BasisBoneTrackedRole.LeftLowerArm, out var leftArmToBone)
                    ? llaRot * leftArmToBone
                    : default;

                rlaPos = pOut[S_RightLowerArm];
                rlaRot = rOut[S_RightLowerArm];
                data.hintPositionRightHand = rlaPos;
                data.hintRotationRightHand = BasisLimbRollStore.TryGet(BasisBoneTrackedRole.RightLowerArm, out var rightArmToBone)
                    ? rlaRot * rightArmToBone
                    : default;

                // ── TOES ──
                data.leftDrivenTargetRot = rOut[S_LeftToe];
                data.rightDrivenTargetRot = rOut[S_RightToe];

                // ── SHOULDERS (rotation only) ──
                data.targetRotationLeftShoulder = rOut[S_LeftShoulder];
                data.targetRotationRightShoulder = rOut[S_RightShoulder];
            }

            // ── PROCEDURAL TOE ARTICULATION ──
            // Surface-probe toe bend, scaled by the same blend weight as the rest of foot IK so it fades in and
            // out with it rather than snapping. The FBIK job only consults these when the toe TRACKER is absent,
            // so a real tracked toe still wins; zeroing here when the driver is not engaged keeps the toe under
            // pure animation control on every other path.
            if (footIKBlendWeightLeft > 0.001f && footDriverReady)
            {
                data.leftToeBendDeg = footDriver.LeftToeBendDegrees * footIKBlendWeightLeft;
                data.leftToeBendAxis = footDriver.LeftToeBendAxis;
            }
            else
            {
                data.leftToeBendDeg = 0f;
                data.leftToeBendAxis = Vector3.zero;
            }

            if (footIKBlendWeightRight > 0.001f && footDriverReady)
            {
                data.rightToeBendDeg = footDriver.RightToeBendDegrees * footIKBlendWeightRight;
                data.rightToeBendAxis = footDriver.RightToeBendAxis;
            }
            else
            {
                data.rightToeBendDeg = 0f;
                data.rightToeBendAxis = Vector3.zero;
            }

            // ── SHIN ROLL (tracker-implied lower-leg BONE rotation) ──
            // A calf strap's clock angle is arbitrary and the lower-leg role gets no Recalibrated* rotation
            // offset, so the raw tracker rotation is mapped through the calibration reference before the solve
            // may compare it against the shin. No reference (never calibrated) leaves the zero quaternion,
            // which BasisLegSolveCore reads as "feature off".
            data.hintRotationLeftLowerLeg = (leftLLHasTracker && BasisLimbRollStore.TryGet(BasisBoneTrackedRole.LeftLowerLeg, out var leftToBone))
                ? BasisLocalBoneDriver.LeftLowerLegControl.OutgoingWorldData.rotation * leftToBone
                : default;
            data.hintRotationRightLowerLeg = (rightLLHasTracker && BasisLimbRollStore.TryGet(BasisBoneTrackedRole.RightLowerLeg, out var rightToBone))
                ? BasisLocalBoneDriver.RightLowerLegControl.OutgoingWorldData.rotation * rightToBone
                : default;

            // ── DERIVED BEND PREFS ──
            Vector3 hipsRight = hipsRot * Vector3.right;
            // The knee half-space guard's ANTERIOR reference. Always body-frame, never the tracker-derived
            // normal below: the guard measures "is the knee in front of the leg", and if that reference rides
            // the shin tracker then tibial rotation alone drags a legal knee into the guard's compression band.
            data.kneeAnteriorRef = hipsRight;
            if (trackerBendNormal)
            {
                data.kneeBendPrefLeft = (leftLLHasTracker && BasisBendNormalStore.TryGet(BasisBoneTrackedRole.LeftLowerLeg, out var leftAxis))
                    ? BasisTrackerBendNormalCore.ResolveWorldNormal(BasisLocalBoneDriver.LeftLowerLegControl.OutgoingWorldData.rotation, leftAxis, hipsRight)
                    : hipsRight;
                data.kneeBendPrefRight = (rightLLHasTracker && BasisBendNormalStore.TryGet(BasisBoneTrackedRole.RightLowerLeg, out var rightAxis))
                    ? BasisTrackerBendNormalCore.ResolveWorldNormal(BasisLocalBoneDriver.RightLowerLegControl.OutgoingWorldData.rotation, rightAxis, hipsRight)
                    : hipsRight;
            }
            else
            {
                data.kneeBendPrefLeft = hipsRight;
                data.kneeBendPrefRight = hipsRight;
            }
            // Pull the latest tunable settings into data every frame so slider changes flow into
            // the IK job. Without this the job runs on the boot-time snapshot from Spine().
            ApplyTuningSettings(ref data);
            sMarkerIKDestBuildTargets.End();

            if (!EngineDrivenAnimatorEvaluate && !LocomotionPose.EngineAnimatorSuppressed)
            {
                sMarkerIKDestAnimatorEval.Begin();
                PlayableGraph.Evaluate(deltaTime);
                sMarkerIKDestAnimatorEval.End();
            }
            sMarkerIKDestLocoJoin.Begin();
            bool streamPrefilled = LocomotionPose.TryComplete(PoseSkeleton);
            sMarkerIKDestLocoJoin.End();
            RunIKSolve(deltaTime, streamPrefilled);

            // Publish each bone control's post-IK world pose (the rendered bone) into IKWorldData so consumers can
            // follow the solved bone instead of the pre-IK target. Bones with no solved transform fall back to
            // OutgoingWorldData.
            sMarkerIKDestPublish.Begin();
            PublishIKWorldData();
            sMarkerIKDestPublish.End();

            // Developer diagnostics: after the graph solves, sample the live head/hips/feet solve
            // (target fed to IK, calibrated offset, predicted product, observed bone pose) plus the
            // live avatar roots, so the runtime flip can be observed rather than only predicted.
            if (BasisCalibrationDebugRecorder.RuntimeActive)
            {
                BasisCalibrationDebugRecorder.RuntimeBone("head", BasisLocalBoneDriver.HeadControl.OutgoingWorldData.rotation, data.offsetRotationHead, BasisLocalAvatarDriver.Mapping.head);
                BasisCalibrationDebugRecorder.RuntimeBone("hips", BasisLocalBoneDriver.HipsControl.OutgoingWorldData.rotation, data.offsetRotationHips, BasisLocalAvatarDriver.Mapping.Hips);
                BasisCalibrationDebugRecorder.RuntimeBone("leftFoot", BasisLocalBoneDriver.LeftFootControl.OutgoingWorldData.rotation, data.offsetRotationLeftFoot, BasisLocalAvatarDriver.Mapping.leftFoot);
                BasisCalibrationDebugRecorder.RuntimeBone("rightFoot", BasisLocalBoneDriver.RightFootControl.OutgoingWorldData.rotation, data.offsetRotationRightFoot, BasisLocalAvatarDriver.Mapping.rightFoot);
                Transform animRoot = localPlayer?.BasisAvatar?.Animator != null ? localPlayer.BasisAvatar.Animator.transform : null;
                BasisCalibrationDebugRecorder.RuntimeEndFrame(localPlayer != null ? localPlayer.transform : null, animRoot);
            }

            // Arm-IK jitter capture: log the solved shoulder/elbow/hand + the IK inputs (hand target, elbow
            // hint) each frame so a held-still capture shows which one actually moves. No-op unless armed.
            if (BasisArmIKRuntimeRecorder.Active)
            {
                var armMap = BasisLocalAvatarDriver.Mapping;
                BasisArmIKRuntimeRecorder.Sample(
                    armMap.leftUpperArm, armMap.leftLowerArm, armMap.leftHand,
                    armMap.RightUpperArm, armMap.RightLowerArm, armMap.rightHand,
                    data.targetPositionLeftHand, data.targetPositionRightHand,
                    data.hintPositionLeftHand, data.hintPositionRightHand,
                    data.hintWeightLeftHand, data.hintWeightRightHand);
            }
        }
        public static Vector3 ApplyHintBias(BasisBoneTrackedRole hintRole, Vector3 rawPos, Quaternion rawRot)
        {
            if (BasisHintBiasStore.TryGet(hintRole, out var localOffset))
            {
                return rawPos + rawRot * localOffset;
            }

            return rawPos;
        }

        private void PublishIKWorldData()
        {
            if (!_ikPublishTransforms.isCreated || _ikPublishControls == null
                || _ikPublishTransforms.length != _ikPublishControls.Length)
            {
                PublishIKWorldDataMainThread();
                return;
            }

            if (_ikPublishControls.Length > 0)
            {
                // Read-only inline run: Schedule().Complete() on one line paid a dispatch
                // and a fence for ~17 transforms with zero overlap.
                new BasisReadBoneWorldPoseJob
                {
                    Positions = _ikPublishPositions,
                    Rotations = _ikPublishRotations,
                }.RunReadOnly(_ikPublishTransforms);

                for (int i = 0; i < _ikPublishControls.Length; i++)
                {
                    _ikPublishControls[i].SetIKWorldData(_ikPublishPositions[i], _ikPublishRotations[i]);
                }
            }

            var fallback = _ikFallbackControls;
            for (int i = 0; i < fallback.Length; i++)
            {
                var world = fallback[i].OutgoingWorldData;
                fallback[i].SetIKWorldData(world.position, world.rotation);
            }
        }

        private void BuildIKPublishArrays()
        {
            DisposeIKPublishArrays();

            var m = BasisLocalAvatarDriver.Mapping;
            if (m == null) return;

            (BasisLocalBoneControl control, Transform bone, bool has)[] entries =
            {
                (BasisLocalBoneDriver.HeadControl, m.head, m.Hashead),
                (BasisLocalBoneDriver.NeckControl, m.neck, m.Hasneck),
                (BasisLocalBoneDriver.ChestControl, m.chest, m.Haschest),
                (BasisLocalBoneDriver.SpineControl, m.spine, m.Hasspine),
                (BasisLocalBoneDriver.HipsControl, m.Hips, m.HasHips),

                (BasisLocalBoneDriver.LeftShoulderControl, m.leftShoulder, m.HasleftShoulder),
                (BasisLocalBoneDriver.LeftLowerArmControl, m.leftLowerArm, m.HasleftLowerArm),
                (BasisLocalBoneDriver.LeftHandControl, m.leftHand, m.HasleftHand),
                (BasisLocalBoneDriver.RightShoulderControl, m.RightShoulder, m.HasRightShoulder),
                (BasisLocalBoneDriver.RightLowerArmControl, m.RightLowerArm, m.HasRightLowerArm),
                (BasisLocalBoneDriver.RightHandControl, m.rightHand, m.HasrightHand),

                (BasisLocalBoneDriver.LeftUpperLegControl, m.LeftUpperLeg, m.HasLeftUpperLeg),
                (BasisLocalBoneDriver.LeftLowerLegControl, m.LeftLowerLeg, m.HasLeftLowerLeg),
                (BasisLocalBoneDriver.LeftFootControl, m.leftFoot, m.HasleftFoot),
                (BasisLocalBoneDriver.LeftToeControl, m.leftToe, m.HasleftToes),
                (BasisLocalBoneDriver.RightUpperLegControl, m.RightUpperLeg, m.HasRightUpperLeg),
                (BasisLocalBoneDriver.RightLowerLegControl, m.RightLowerLeg, m.HasRightLowerLeg),
                (BasisLocalBoneDriver.RightFootControl, m.rightFoot, m.HasrightFoot),
                (BasisLocalBoneDriver.RightToeControl, m.rightToe, m.HasrightToes),

                (BasisLocalBoneDriver.EyeControl, null, false),
                (BasisLocalBoneDriver.MouthControl, null, false),
            };

            var solvedTransforms = new List<Transform>(entries.Length);
            var solvedControls = new List<BasisLocalBoneControl>(entries.Length);
            var fallbackControls = new List<BasisLocalBoneControl>(4);

            foreach (var e in entries)
            {
                if (e.control == null) continue;
                if (e.has && e.bone != null)
                {
                    solvedTransforms.Add(e.bone);
                    solvedControls.Add(e.control);
                }
                else
                {
                    fallbackControls.Add(e.control);
                }
            }

            _ikPublishControls = solvedControls.ToArray();
            _ikFallbackControls = fallbackControls.ToArray();
            _ikPublishTransforms = new TransformAccessArray(solvedTransforms.ToArray());
            _ikPublishPositions = new NativeArray<float3>(_ikPublishControls.Length, Allocator.Persistent);
            _ikPublishRotations = new NativeArray<quaternion>(_ikPublishControls.Length, Allocator.Persistent);
        }

        private void DisposeIKPublishArrays()
        {
            if (_ikPublishTransforms.isCreated) _ikPublishTransforms.Dispose();
            if (_ikPublishPositions.IsCreated) _ikPublishPositions.Dispose();
            if (_ikPublishRotations.IsCreated) _ikPublishRotations.Dispose();
            _ikPublishControls = null;
            _ikFallbackControls = null;
        }

        // Publishes every bone control's post-IK world pose (the rendered bone) into IKWorldData. Uses the solved
        // animator transform when the avatar has that bone; otherwise falls back to the pre-IK OutgoingWorldData
        // (center-eye, mouth, or any bone the avatar lacks).
        private static void PublishIKWorldDataMainThread()
        {
            var m = BasisLocalAvatarDriver.Mapping;
            if (m == null) return;

            PublishBoneIK(BasisLocalBoneDriver.HeadControl, m.head, m.Hashead);
            PublishBoneIK(BasisLocalBoneDriver.NeckControl, m.neck, m.Hasneck);
            PublishBoneIK(BasisLocalBoneDriver.ChestControl, m.chest, m.Haschest);
            PublishBoneIK(BasisLocalBoneDriver.SpineControl, m.spine, m.Hasspine);
            PublishBoneIK(BasisLocalBoneDriver.HipsControl, m.Hips, m.HasHips);

            PublishBoneIK(BasisLocalBoneDriver.LeftShoulderControl, m.leftShoulder, m.HasleftShoulder);
            PublishBoneIK(BasisLocalBoneDriver.LeftLowerArmControl, m.leftLowerArm, m.HasleftLowerArm);
            PublishBoneIK(BasisLocalBoneDriver.LeftHandControl, m.leftHand, m.HasleftHand);
            PublishBoneIK(BasisLocalBoneDriver.RightShoulderControl, m.RightShoulder, m.HasRightShoulder);
            PublishBoneIK(BasisLocalBoneDriver.RightLowerArmControl, m.RightLowerArm, m.HasRightLowerArm);
            PublishBoneIK(BasisLocalBoneDriver.RightHandControl, m.rightHand, m.HasrightHand);

            PublishBoneIK(BasisLocalBoneDriver.LeftUpperLegControl, m.LeftUpperLeg, m.HasLeftUpperLeg);
            PublishBoneIK(BasisLocalBoneDriver.LeftLowerLegControl, m.LeftLowerLeg, m.HasLeftLowerLeg);
            PublishBoneIK(BasisLocalBoneDriver.LeftFootControl, m.leftFoot, m.HasleftFoot);
            PublishBoneIK(BasisLocalBoneDriver.LeftToeControl, m.leftToe, m.HasleftToes);
            PublishBoneIK(BasisLocalBoneDriver.RightUpperLegControl, m.RightUpperLeg, m.HasRightUpperLeg);
            PublishBoneIK(BasisLocalBoneDriver.RightLowerLegControl, m.RightLowerLeg, m.HasRightLowerLeg);
            PublishBoneIK(BasisLocalBoneDriver.RightFootControl, m.rightFoot, m.HasrightFoot);
            PublishBoneIK(BasisLocalBoneDriver.RightToeControl, m.rightToe, m.HasrightToes);

            // No humanoid transform for these — publish the pre-IK world pose so IKWorldData is still valid.
            PublishBoneIK(BasisLocalBoneDriver.EyeControl, null, false);
            PublishBoneIK(BasisLocalBoneDriver.MouthControl, null, false);
        }

        private static void PublishBoneIK(BasisLocalBoneControl control, Transform bone, bool has)
        {
            if (control == null) return;
            if (has && bone != null)
            {
                bone.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
                control.SetIKWorldData(position, rotation);
            }
            else
            {
                var world = control.OutgoingWorldData;
                control.SetIKWorldData(world.position, world.rotation);
            }
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float ExpAlpha(float hz, float dt)
        {
            return 1f - Mathf.Exp(-2f * Mathf.PI * Mathf.Max(0.0001f, hz) * Mathf.Max(0.000001f, dt));
        }
        private void OnPlayersHeightChangedNextFrame(HeightModeChange HeightModeChange)
        {
            ref BasisEerieMovement Data = ref IKJob;
            SetHandCollisionScale(ref Data, BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale);
        }
        public static void SetHandCollisionScale(ref BasisEerieMovement BodyData, float Scale)
        {
            // Pull the live slider values so a height change keeps tuning consistent with
            // ApplyTuningSettings (which does the same per-frame).
            BodyData.handSkin = Basis.BasisUI.BasisSettingsDefaults.FBIKHandSkin.RawValue * Scale;
            BodyData.handRadius = Basis.BasisUI.BasisSettingsDefaults.FBIKHandRadius.RawValue * Scale;
            BodyData.chestRadius = Basis.BasisUI.BasisSettingsDefaults.FBIKChestRadius.RawValue * Scale;
            BodyData.collisionSkin = Basis.BasisUI.BasisSettingsDefaults.FBIKCollisionSkin.RawValue * Scale;

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

            // minHeadSpineHeight above was the only baked metre scalar this handler refreshed; the arm,
            // clavicle and neck-cue scalars are measured in the same one-shot rig build and were left to go
            // stale on every rescale. Same event, same fix.
            BodyData.RescaleTposeScalars(Scale);
        }
        public void Spine()
        {
            if (localPlayer?.BasisAvatar?.Animator == null)
            {
                return;
            }

            if (IKJobCreated)
            {
                IKJob.Destroy();
                IKJobCreated = false;
            }
            IKJob = default;
            BasisAnimationRiggingHelper.CreateBasisFullBodyRIG(localPlayer, basisTransformMapping, ref IKJob);
            IKDataReady = true;

            BasisLocalPlayer.OnPlayersHeightChangedNextFrame -= OnPlayersHeightChangedNextFrame;
            BasisLocalPlayer.OnPlayersHeightChangedNextFrame += OnPlayersHeightChangedNextFrame;
            OnPlayersHeightChangedNextFrame( HeightModeChange.OnTpose);

            ref BasisEerieMovement data = ref IKJob;

            // Legs enabled by presence
            BasisLocalBoneDriver.LeftFootControl.OnHasRigChanged += (hasRig) =>
            {
                ref BasisEerieMovement d = ref IKJob;
                d.enabledLeftLowerLeg = HasRigLayerFloat(BasisLocalBoneDriver.LeftFootControl);
            };
            data.enabledLeftLowerLeg = HasRigLayerFloat(BasisLocalBoneDriver.LeftFootControl);

            BasisLocalBoneDriver.RightFootControl.OnHasRigChanged += (hasRig) =>
            {
                ref BasisEerieMovement d = ref IKJob;
                d.enabledRightLowerLeg = HasRigLayerFloat(BasisLocalBoneDriver.RightFootControl);
            };
            data.enabledRightLowerLeg = HasRigLayerFloat(BasisLocalBoneDriver.RightFootControl);

            BasisLocalBoneDriver.LeftLowerLegControl.OnHasRigChanged += (hasRig) =>
            {
                ref BasisEerieMovement d = ref IKJob;
                d.hintWeightLeftLowerLeg = HasRigLayerFloat(BasisLocalBoneDriver.LeftLowerLegControl);
            };
            data.hintWeightLeftLowerLeg = HasRigLayerFloat(BasisLocalBoneDriver.LeftLowerLegControl);

            BasisLocalBoneDriver.RightLowerLegControl.OnHasRigChanged += (hasRig) =>
            {
                ref BasisEerieMovement d = ref IKJob;
                d.hintWeightRightLowerLeg = HasRigLayerFloat(BasisLocalBoneDriver.RightLowerLegControl);
            };
            data.hintWeightRightLowerLeg = HasRigLayerFloat(BasisLocalBoneDriver.RightLowerLegControl);

            // Toes
            BasisLocalBoneDriver.LeftToeControl.OnHasRigChanged += (hasRig) =>
            {
                ref BasisEerieMovement d = ref IKJob;
                d.leftToeEnabled = HasRigLayer(BasisLocalBoneDriver.LeftToeControl);
            };
            data.leftToeEnabled = HasRigLayer(BasisLocalBoneDriver.LeftToeControl);

            BasisLocalBoneDriver.RightToeControl.OnHasRigChanged += (hasRig) =>
            {
                ref BasisEerieMovement d = ref IKJob;
                d.rightToeEnabled = HasRigLayer(BasisLocalBoneDriver.RightToeControl);
            };
            data.rightToeEnabled = HasRigLayer(BasisLocalBoneDriver.RightToeControl);

            // Hands
            BasisLocalBoneDriver.LeftHandControl.OnHasRigChanged += (hasRig) =>
            {
                ref BasisEerieMovement d = ref IKJob;
                d.enabledLeftHand = HandRigWeight(BasisLocalBoneDriver.LeftHandControl);
            };
            data.enabledLeftHand = HandRigWeight(BasisLocalBoneDriver.LeftHandControl);

            BasisLocalBoneDriver.RightHandControl.OnHasRigChanged += (hasRig) =>
            {
                ref BasisEerieMovement d = ref IKJob;
                d.enabledRightHand = HandRigWeight(BasisLocalBoneDriver.RightHandControl);
            };
            data.enabledRightHand = HandRigWeight(BasisLocalBoneDriver.RightHandControl);

            // Lower arms (hand hints)
            BasisLocalBoneDriver.LeftLowerArmControl.OnHasRigChanged += (hasRig) =>
            {
                ref BasisEerieMovement d = ref IKJob;
                d.hintWeightLeftHand = HasRigLayer(BasisLocalBoneDriver.LeftLowerArmControl);
            };
            data.hintWeightLeftHand = HasRigLayer(BasisLocalBoneDriver.LeftLowerArmControl);

            BasisLocalBoneDriver.RightLowerArmControl.OnHasRigChanged += (hasRig) =>
            {
                ref BasisEerieMovement d = ref IKJob;
                d.hintWeightRightHand = HasRigLayer(BasisLocalBoneDriver.RightLowerArmControl);
            };
            data.hintWeightRightHand = HasRigLayer(BasisLocalBoneDriver.RightLowerArmControl);

            // Chest (head hint)
            BasisLocalBoneDriver.ChestControl.OnHasRigChanged += (hasRig) =>
            {
                ref BasisEerieMovement d = ref IKJob;
                d.hasChestTracker = HasRigLayer(BasisLocalBoneDriver.ChestControl);
            };
            data.hasChestTracker = HasRigLayer(BasisLocalBoneDriver.ChestControl);

            // Chest (head hint)
            BasisLocalBoneDriver.LeftShoulderControl.OnHasRigChanged += (hasRig) =>
            {
                ref BasisEerieMovement d = ref IKJob;
                d.enabledLeftShoulder = HasRigLayer(BasisLocalBoneDriver.LeftShoulderControl);
            };
            data.enabledLeftShoulder = HasRigLayer(BasisLocalBoneDriver.LeftShoulderControl);

            // Chest (head hint)
            BasisLocalBoneDriver.RightShoulderControl.OnHasRigChanged += (hasRig) =>
            {
                ref BasisEerieMovement d = ref IKJob;
                d.enabledRightShoulder = HasRigLayer(BasisLocalBoneDriver.RightShoulderControl);
            };
            data.enabledRightShoulder = HasRigLayer(BasisLocalBoneDriver.RightShoulderControl);

            // Initialize offsets and weights per override slot. Slots are HumanBodyBones values:
            // 0..20 plus UpperChest (54) — NOT a contiguous 0..Count range, which would touch
            // LeftEye (21, silently ignored) and skip UpperChest entirely.
            for (int i = 0; i < BasisEerieMovement.Count; i++)
            {
                int slot = i <= (int)HumanBodyBones.RightToes ? i : (int)HumanBodyBones.UpperChest;
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
            data.minFactor = 0.95f;
            data.maxFactor = 1.05f;
            ApplyTuningSettings(ref data);

        }

        // Pulls every live-tunable BasisSettingsBinding into the IK data. Called from Spine() at
        // init AND from SimulateIKDestinations every frame so slider changes flow into the
        // animation job. Without the per-frame call, sliders update RawValue but the IK keeps
        // running on the boot-time snapshot.
        // Issue #531: FBT-recalibrated per-effector rotation offsets. CreateBasisFullBodyRIG captures
        // these once at rig build against the pre-calibration frame; a one-shot runtime write to the
        // [SyncSceneToStream] data field does NOT persist (it reverts to the serialized setup value),
        // so FullBodyCalibration stashes the freshly recomputed values here and ApplyTuningSettings
        // re-applies them every frame — the same persistent path the tuning sliders use. Cleared on
        // rig (re)build so a new avatar uses its own setup capture until the user calibrates.
        public static bool HasRecalibratedRotationOffsets;
        public static Quaternion RecalibratedHead, RecalibratedHips, RecalibratedChest;
        public static Quaternion RecalibratedLeftFoot, RecalibratedRightFoot;
        public static Quaternion RecalibratedLeftToe, RecalibratedRightToe;
        public static Quaternion RecalibratedLeftShoulder, RecalibratedRightShoulder;

        private static void ApplyTuningSettings(ref BasisEerieMovement data)
        {
            // The IK job reads PlayerUp for the hip hinge, crouch offset, arm solve and elbow protect.
            // Nothing ever assigned it, so it sat at the SetDefaultValues world up while the foot driver
            // used the real root up -- the two halves of the solve disagreed whenever the root was tilted
            // (play-space flip, seats/vehicles). Identical to Vector3.up for an upright root.
            Vector3 rootUp = BasisLocalPlayer.localToWorldMatrix.MultiplyVector(Vector3.up);
            data.playerUp = rootUp.sqrMagnitude > 1e-8f ? rootUp.normalized : Vector3.up;
            data.maxBendDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKMaxBendDeg.RawValue;
            data.maxChestDeltaDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKMaxChestDelta.RawValue;
            data.spineBendPitch = Basis.BasisUI.BasisSettingsDefaults.FBIKSpineBendPitch.RawValue;
            data.spineBendYaw = Basis.BasisUI.BasisSettingsDefaults.FBIKSpineBendYaw.RawValue;
            data.spineBendRoll = Basis.BasisUI.BasisSettingsDefaults.FBIKSpineBendRoll.RawValue;
            data.upperChestBendPitch = Basis.BasisUI.BasisSettingsDefaults.FBIKUpperChestBendPitch.RawValue;
            data.upperChestBendYaw = Basis.BasisUI.BasisSettingsDefaults.FBIKUpperChestBendYaw.RawValue;
            data.upperChestBendRoll = Basis.BasisUI.BasisSettingsDefaults.FBIKUpperChestBendRoll.RawValue;
            data.hipHingeStartDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKHipHingeStartDeg.RawValue;
            data.hipHingeMaxAddDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKHipHingeMaxAddDeg.RawValue;
            data.chestSpringHz = Basis.BasisUI.BasisSettingsDefaults.FBIKChestSpringHz.RawValue;
            data.chestSpringDamping = Basis.BasisUI.BasisSettingsDefaults.FBIKChestSpringDamping.RawValue;
            data.spineMaxForwardDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKSpineMaxForwardDeg.RawValue;
            data.spineMaxBackwardDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKSpineMaxBackwardDeg.RawValue;
            data.spineMaxLateralDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKSpineMaxLateralDeg.RawValue;
            data.spineSquishBoost = Basis.BasisUI.BasisSettingsDefaults.FBIKSpineSquishBoost.RawValue;
            data.spineGazeFollow = Basis.BasisUI.BasisSettingsDefaults.FBIKSpineGazeFollow.RawValue;
            data.neckGazeFollow = Basis.BasisUI.BasisSettingsDefaults.FBIKNeckGazeFollow.RawValue;
            data.moveBodyBackWhenCrouching = Basis.BasisUI.BasisSettingsDefaults.FBIKMoveBodyBackWhenCrouching.RawValue;
            data.trunkCounterbalance = Basis.BasisUI.BasisSettingsDefaults.FBIKTrunkCounterbalance.RawValue;
            data.swingSmoothRateDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKElbowSwingEnabled.RawValue
                ? Basis.BasisUI.BasisSettingsDefaults.FBIKSwingSmoothRate.RawValue
                : 0f;
            data.spineCCDRelax = Basis.BasisUI.BasisSettingsDefaults.FBIKSpineCCDRelax.RawValue;
            data.spineTwistKeep = Basis.BasisUI.BasisSettingsDefaults.FBIKSpineTwistKeep.RawValue;
            data.spineNeckTwistKeep = Basis.BasisUI.BasisSettingsDefaults.FBIKSpineNeckTwistKeep.RawValue;
            data.neckMaxConeDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKNeckMaxConeDeg.RawValue;
            data.chestArmSwingFactor = Basis.BasisUI.BasisSettingsDefaults.FBIKChestArmSwingFactor.RawValue;
            data.chestArmSwingMaxDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKChestArmSwingMaxDeg.RawValue;
            data.lowerArmTwistFraction = Basis.BasisUI.BasisSettingsDefaults.FBIKLowerArmTwistFraction.RawValue;
            data.upperArmTwistFraction = Basis.BasisUI.BasisSettingsDefaults.FBIKUpperArmTwistFraction.RawValue;
            data.anatDifferentialStiffness = Basis.BasisUI.BasisSettingsDefaults.FBIKAnatDifferentialStiffness.RawValue;
            data.anatShoulderSlide = Basis.BasisUI.BasisSettingsDefaults.FBIKAnatShoulderSlide.RawValue;
            data.anatCervicalLordosis = Basis.BasisUI.BasisSettingsDefaults.FBIKAnatCervicalLordosis.RawValue;
            data.anatPelvicTwistRouting = Basis.BasisUI.BasisSettingsDefaults.FBIKAnatPelvicTwistRouting.RawValue;
            data.spineAnatomicalRom = Basis.BasisUI.BasisSettingsDefaults.FBIKSpineAnatomicalRom.RawValue;
            data.chestIkTarget = Basis.BasisUI.BasisSettingsDefaults.FBIKChestIKTarget.RawValue;
            data.legSwivelSmoothing = Basis.BasisUI.BasisSettingsDefaults.FBIKLegSwivelSmoothing.RawValue;
            data.kneeFootPoleHold = Basis.BasisUI.BasisSettingsDefaults.FBIKKneeFootPoleHold.RawValue;
            data.kneeFootPoleConditioning = Basis.BasisUI.BasisSettingsDefaults.FBIKKneeFootPoleConditioning.RawValue;
            data.lordosisPitchGainDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKLordosisPitchGainDeg.RawValue;
            data.lordosisBaseDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKLordosisBaseDeg.RawValue;
            data.lordosisNeckShare = Basis.BasisUI.BasisSettingsDefaults.FBIKLordosisNeckShare.RawValue;
            data.lordosisMaxHeadPitchDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKLordosisMaxHeadPitchDeg.RawValue;
            data.lordosisExtremeStartDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKLordosisExtremeStartDeg.RawValue;
            data.lordosisExtremeFullDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKLordosisExtremeFullDeg.RawValue;
            data.lordosisExtremeRollForwardMaxDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKLordosisExtremeRollForwardMaxDeg.RawValue;
            data.lordosisExtremeRollBackwardMaxDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKLordosisExtremeRollBackwardMaxDeg.RawValue;
            // Avatar scale, shared by every metre-valued tuning value below.
            float collisionScale = BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale;

            // These six are METRES added straight to the hips/chest world position in ApplyCervicalLordosis,
            // so they must scale with the avatar. Unscaled they were a fixed ~2.5 cm pelvis and ~4 cm chest
            // shove at every size — double the body-relative displacement at 0.5x, and since the chest term
            // is 1.6x the hips term the torso visibly sheared. The Deg values above are angles; leave them.
            data.lordosisExtremeHipsHorizontalMax = Basis.BasisUI.BasisSettingsDefaults.FBIKLordosisExtremeHipsHorizontalMax.RawValue * collisionScale;
            data.lordosisExtremeChestHorizontalMax = Basis.BasisUI.BasisSettingsDefaults.FBIKLordosisExtremeChestHorizontalMax.RawValue * collisionScale;
            data.lordosisExtremeHipsDownMax = Basis.BasisUI.BasisSettingsDefaults.FBIKLordosisExtremeHipsDownMax.RawValue * collisionScale;
            data.lordosisExtremeChestDownMax = Basis.BasisUI.BasisSettingsDefaults.FBIKLordosisExtremeChestDownMax.RawValue * collisionScale;
            data.lordosisExtremeHipsDownLookUp = Basis.BasisUI.BasisSettingsDefaults.FBIKLordosisExtremeHipsDownLookUp.RawValue * collisionScale;
            data.lordosisExtremeChestDownLookUp = Basis.BasisUI.BasisSettingsDefaults.FBIKLordosisExtremeChestDownLookUp.RawValue * collisionScale;

            // Toggles + shoulder-solve params that previously only flowed at init. Without these
            // here, flipping the matching toggle/slider in the IK panel left the animation job
            // running on the boot-time snapshot.
            data.collisionsEnabled = Basis.BasisUI.BasisSettingsDefaults.FBIKCollisionsEnabled.RawValue;
            data.protectElbow = Basis.BasisUI.BasisSettingsDefaults.FBIKProtectElbow.RawValue;
           // data.useNeuralPole = Basis.BasisUI.BasisSettingsDefaults.FBIKNeuralPole.RawValue;
            data.collideTrackedElbow = Basis.BasisUI.BasisSettingsDefaults.FBIKCollideTrackedElbow.RawValue;
          //  data.wristAxialBound = Basis.BasisUI.BasisSettingsDefaults.FBIKWristAxialBound.RawValue;
            data.elbowDragEnabled = Basis.BasisUI.BasisSettingsDefaults.FBIKElbowDrag.RawValue;
            data.elbowDragHz = Basis.BasisUI.BasisSettingsDefaults.FBIKElbowDragHz.RawValue;
            data.shoulderSolveEnabled = Basis.BasisUI.BasisSettingsDefaults.FBIKShoulderSolveEnabled.RawValue;
            data.shoulderShrugEnabled = Basis.BasisUI.BasisSettingsDefaults.FBIKShoulderShrug.RawValue;
           // data.shoulderRetractionEnabled = Basis.BasisUI.BasisSettingsDefaults.FBIKShoulderRetraction.RawValue;
            data.shoulderElevationFactor = Basis.BasisUI.BasisSettingsDefaults.FBIKShoulderElevation.RawValue;
            data.shoulderProtractionFactor = Basis.BasisUI.BasisSettingsDefaults.FBIKShoulderProtraction.RawValue;
            data.shoulderCoupleRatio = Basis.BasisUI.BasisSettingsDefaults.FBIKShoulderCoupleRatio.RawValue;
            data.shoulderMaxDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKShoulderMaxDeg.RawValue;
            data.shoulderSlideStartDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKShoulderSlideStartDeg.RawValue;
            data.shoulderSlideMaxDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKShoulderSlideMaxDeg.RawValue;
            data.shoulderSlideFraction = Basis.BasisUI.BasisSettingsDefaults.FBIKShoulderSlideFraction.RawValue;
            data.thoracicBendStiffen = Basis.BasisUI.BasisSettingsDefaults.FBIKThoracicBendStiffen.RawValue;
            data.spineTautBandFrac = Basis.BasisUI.BasisSettingsDefaults.FBIKSpineTautBandFrac.RawValue;
            data.bendTwistCoupling = Basis.BasisUI.BasisSettingsDefaults.FBIKBendTwistCoupling.RawValue;
            data.neckGazeFollowMaxDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKNeckGazeFollowMaxDeg.RawValue;
            data.trunkCounterbalanceMaxSpineFrac = Basis.BasisUI.BasisSettingsDefaults.FBIKTrunkCounterbalanceMaxFrac.RawValue;
            data.chestIkWeight = Basis.BasisUI.BasisSettingsDefaults.FBIKChestIkWeight.RawValue;
            data.chestIkIterations = Mathf.Max(1, Mathf.RoundToInt(Basis.BasisUI.BasisSettingsDefaults.FBIKChestIkIterations.RawValue));
            data.chestIkHeadRestoreSweeps = Mathf.Max(1, Mathf.RoundToInt(Basis.BasisUI.BasisSettingsDefaults.FBIKChestIkHeadRestoreSweeps.RawValue));
            data.chestPosPullMaxDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKChestPosPullMaxDeg.RawValue;
            data.chestPullMaxDist = Basis.BasisUI.BasisSettingsDefaults.FBIKChestPullMaxDist.RawValue;
            data.chestFollowChestShare = Basis.BasisUI.BasisSettingsDefaults.FBIKChestFollowChestShare.RawValue;
            data.trackedKneeSwivelMinCutoffHz = Basis.BasisUI.BasisSettingsDefaults.FBIKTrackedKneeSwivelMinCutoffHz.RawValue;
            data.trackedKneeSwivelBeta = Basis.BasisUI.BasisSettingsDefaults.FBIKTrackedKneeSwivelBeta.RawValue;
            data.trackedKneeSwivelDerivCutoffHz = Basis.BasisUI.BasisSettingsDefaults.FBIKTrackedKneeSwivelDerivCutoffHz.RawValue;

            // Collision capsule dimensions × avatar scale. Slider defaults now match the
            // hardcoded values previously in SetHandCollisionScale, so this is the canonical path.
            data.handRadius = Basis.BasisUI.BasisSettingsDefaults.FBIKHandRadius.RawValue * collisionScale;
            data.handSkin = Basis.BasisUI.BasisSettingsDefaults.FBIKHandSkin.RawValue * collisionScale;
            data.chestRadius = Basis.BasisUI.BasisSettingsDefaults.FBIKChestRadius.RawValue * collisionScale;
            data.collisionSkin = Basis.BasisUI.BasisSettingsDefaults.FBIKCollisionSkin.RawValue * collisionScale;

            if (HasRecalibratedRotationOffsets)
            {
                data.offsetRotationHead = RecalibratedHead;
                data.offsetRotationHips = RecalibratedHips;
                data.offsetRotationChest = RecalibratedChest;
                data.offsetRotationLeftFoot = RecalibratedLeftFoot;
                data.offsetRotationRightFoot = RecalibratedRightFoot;
                data.offsetRotationLeftToe = RecalibratedLeftToe;
                data.offsetRotationRightToe = RecalibratedRightToe;
                data.offsetRotationLeftShoulder = RecalibratedLeftShoulder;
                data.offsetRotationRightShoulder = RecalibratedRightShoulder;
            }
        }
        public void DisableAllTrackers()
        {
            if (IKDataReady)
            {
                ref BasisEerieMovement data = ref IKJob;
                data.enabledLeftLowerLeg = 0f;
                data.enabledRightLowerLeg = 0f;
                data.hintWeightLeftLowerLeg = 0f;
                data.hintWeightRightLowerLeg = 0f;
                data.leftToeEnabled = false;
                data.rightToeEnabled = false;
                // data.enabledLeftHand = false;
                // data.enabledRightHand = false;
                data.hintWeightLeftHand = false;
                data.hintWeightRightHand = false;
                data.hasChestTracker = false;
                data.hasHipsTracker = false;
                data.enabledLeftShoulder = false;
                data.enabledRightShoulder = false;
            }
        }
        /// <summary>
        /// Re-applies the full-body IK constraint weights from each bone's current rig-layer
        /// state — the inverse of <see cref="DisableAllTrackers"/>. PutAvatarIntoTPose disables
        /// these so the T-pose read isn't dragged by trackers; FullBodyCalibration restores them
        /// as a side effect of (re)assigning roles, but any flow that enters/exits T-pose WITHOUT
        /// a full calibration must call this or the arm hints / chest / shoulders / legs stay
        /// stuck at zero weight (the avatar and controller arms look broken until the next
        /// calibrate). HasHipsTracker is omitted on purpose — the per-frame Simulate recomputes it.
        /// </summary>
        public bool TryGetLegDiagnostics(int slot, out Basis.IK.BasisLegDiagnostics diagnostics)
        {
            if (IKJobCreated && IKJob.legDiagnostics.IsCreated && (uint)slot < (uint)IKJob.legDiagnostics.Length)
            {
                diagnostics = IKJob.legDiagnostics[slot];
                return true;
            }
            diagnostics = default;
            return false;
        }

        public void RestoreAllTrackers()
        {
            if (IKDataReady)
            {
                ref BasisEerieMovement data = ref IKJob;
                data.enabledLeftLowerLeg = HasRigLayerFloat(BasisLocalBoneDriver.LeftFootControl);
                data.enabledRightLowerLeg = HasRigLayerFloat(BasisLocalBoneDriver.RightFootControl);
                data.hintWeightLeftLowerLeg = HasRigLayerFloat(BasisLocalBoneDriver.LeftLowerLegControl);
                data.hintWeightRightLowerLeg = HasRigLayerFloat(BasisLocalBoneDriver.RightLowerLegControl);
                data.leftToeEnabled = HasRigLayer(BasisLocalBoneDriver.LeftToeControl);
                data.rightToeEnabled = HasRigLayer(BasisLocalBoneDriver.RightToeControl);
                data.enabledLeftHand = HandRigWeight(BasisLocalBoneDriver.LeftHandControl);
                data.enabledRightHand = HandRigWeight(BasisLocalBoneDriver.RightHandControl);
                data.hintWeightLeftHand = HasRigLayer(BasisLocalBoneDriver.LeftLowerArmControl);
                data.hintWeightRightHand = HasRigLayer(BasisLocalBoneDriver.RightLowerArmControl);
                data.hasChestTracker = HasRigLayer(BasisLocalBoneDriver.ChestControl);
                data.enabledLeftShoulder = HasRigLayer(BasisLocalBoneDriver.LeftShoulderControl);
                data.enabledRightShoulder = HasRigLayer(BasisLocalBoneDriver.RightShoulderControl);
            }
        }
        /// <summary>
        /// The zero quaternion. SolveLegs reads it as "position-only foot IK": it keeps the foot's pre-solve
        /// (animation) rotation. It is the system's existing, well-defined "I have no usable rotation for you".
        /// </summary>
        public static readonly Quaternion PreserveTipSentinel = new Quaternion(0f, 0f, 0f, 0f);

        /// <summary>
        /// The foot target rotation to hand SolveLegs, with the per-avatar calibration offset pre-cancelled --
        /// or the preserve-tip sentinel if the result is not a usable rotation.
        ///
        /// WHY THIS EXISTS: a NaN here does not degrade the rig, it KILLS it. SolveLegs decides "no rotation
        /// supplied" with `sqrMagnitude(tRot) &lt; 0.5f` -- and NaN &lt; 0.5f is FALSE, so a NaN target does not trip
        /// that guard. It flows into SolveTwoBone, NaNs the leg bone rotations, and from there the rig never
        /// recovers: zeroing EnableLeftLeg only stops us WRITING, it cannot un-poison what is already written.
        /// That is exactly "the legs stop falling back to the animator when I move, and never come back".
        ///
        /// Two ways a NaN gets in, and both are guarded:
        ///   - the OFFSET is degenerate. A serialized Quaternion defaults to (0,0,0,0), not identity, and
        ///     Quaternion.Inverse divides by the squared norm -- inverting it yields NaN. There is a real window
        ///     for this: BasisAnimationRiggingHelper only assigns the offset when the avatar HAS that foot mapped,
        ///     and recalibration/avatar-swap rewrite it live.
        ///   - the foot driver's own rotation is degenerate (a LookRotation on a collapsed frame).
        ///
        /// NOTE THE COMPARISON SHAPE: `!(x > k)`, never `x &lt; k`. NaN compares false to EVERYTHING, so a `&lt;` test
        /// ACCEPTS NaN. That is precisely the bug in SolveLegs' preserveTip check, and the first version of this
        /// guard repeated it. Negating a `>` rejects NaN, zero and denormals in one test.
        ///
        /// Falling back to the SENTINEL rather than identity matters: identity would hand the solve a confidently
        /// WRONG foot rotation, while the sentinel restores exactly the old, known-good behaviour (the animation's
        /// foot rotation). Foot rotation degrades; walking never breaks.
        /// </summary>
        public static Quaternion SafeFootTargetRotation(Quaternion footRot, Quaternion offset)
        {
            float offSqr = offset.x * offset.x + offset.y * offset.y + offset.z * offset.z + offset.w * offset.w;
            if (!(offSqr > 0.5f)) return PreserveTipSentinel;

            Quaternion result = footRot * Quaternion.Inverse(offset);
            float resSqr = result.x * result.x + result.y * result.y + result.z * result.z + result.w * result.w;
            if (!(resSqr > 0.5f)) return PreserveTipSentinel;

            return result;
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
        /// Hand IK weight. Unlike the other limbs this is not a straight on/off: the layer must be there AND the
        /// producer says how far in it is, so a source that comes and goes (webcam tracking) can fade rather than
        /// pop. Clamped, and written so a NaN weight collapses to 0 instead of reaching the Burst job.
        /// </summary>
        private static float HandRigWeight(BasisLocalBoneControl control)
        {
            if (control == null || control.HasRigLayer != BasisHasRigLayer.HasRigLayer) return 0f;
            float w = control.RigLayerWeight;
            return w > 0f ? (w < 1f ? w : 1f) : 0f;
        }

        /// <summary>
        /// Butterfly knees: laying on your back with a foot tracker but no knee tracker, the tracked foot tilts
        /// outward (soles toward each other) and pulls in toward the pelvis, so the knee should fall open
        /// laterally. Computes the outward knee pole via <see cref="BasisButterflyKneeCore"/>, smoothed to avoid
        /// pops. Returns false (and the knee falls back to the default sagittal bend) when the pose isn't a
        /// butterfly. The open angle is clamped to the hip's natural max-open inside the core.
        /// </summary>
        private static bool TryComputeButterflyKnee(
            bool isLeft, Quaternion hipsRot, Vector3 playerUp, float maxOpenDeg, float supineFloor, float dt, Vector3 defaultBendDir,
            Transform upperLeg, Transform lowerLeg, Vector3 footPos, Quaternion footRot,
            ref Vector3 smoothedHint, ref float smoothedWeight,
            out Vector3 hintPos, out float weight)
        {
            hintPos = default;
            weight = 0f;
            if (upperLeg == null || lowerLeg == null)
            {
                smoothedWeight = 0f;
                return false;
            }

            Vector3 hipPos = upperLeg.position;
            Vector3 hipsRight = hipsRot * Vector3.right;
            Vector3 hipsForward = hipsRot * Vector3.forward;

            BasisButterflyKneeInput input;
            input.HipPosition = hipPos;
            input.FootPosition = footPos;
            input.FootInstepDir = footRot * Vector3.up;          // foot "up" = instep normal (the sole faces -this)
            input.OutwardDir = isLeft ? -hipsRight : hipsRight;
            input.DefaultBendDir = defaultBendDir.sqrMagnitude > 1e-6f ? defaultBendDir : hipsForward; // foot-corrected sagittal base (BasisKneeForwardCore); falls back to belly
            input.PlayerUp = playerUp;
            input.TorsoFacingDir = hipsForward;                  // belly . playerUp -> on-your-back factor
            input.UpperLength = Vector3.Distance(hipPos, lowerLeg.position);
            input.LowerLength = Vector3.Distance(lowerLeg.position, footPos);
            input.MaxOpenDeg = maxOpenDeg;
            input.Strength = 1f;
            input.SupineFloor = supineFloor;

            BasisButterflyKneeCore.Solve(input, out BasisButterflyKneeResult result);

            // Smooth the pole + weight so noisy tilt / recline signals can't pop the knee.
            float alpha = 1f - Mathf.Exp(-ButterflyKneeSmoothRate * dt);
            if (smoothedWeight <= 0.0001f && result.HintWeight <= 0.0001f)
            {
                // Fully inactive: track the rest pole so we don't lerp a stale hint in on the next engage.
                smoothedHint = result.KneeHint;
                smoothedWeight = 0f;
                return false;
            }
            smoothedHint = Vector3.Lerp(smoothedHint, result.KneeHint, alpha);
            smoothedWeight = Mathf.Lerp(smoothedWeight, result.HintWeight, alpha);

            if (smoothedWeight <= 0.001f)
            {
                return false;
            }

            hintPos = smoothedHint;
            weight = smoothedWeight;
            return true;
        }

        /// <summary>
        /// Knee-forward azimuth: with a tracked foot but no knee tracker, aim the knee pole along the FOOT's toe
        /// direction instead of straight body-forward, so turning a foot turns the knee. See
        /// <see cref="BasisKneeForwardCore"/> for the standing-vs-supine model. Outputs the sagittal bend direction
        /// (feeds butterfly's default bend) plus a knee hint pole for the non-butterfly path, smoothed to shave
        /// foot-tracker yaw jitter.
        /// </summary>
        private static bool TryComputeKneeForward(
            Quaternion hipsRot, float coupling, float smoothRate, Vector3 playerUp, float dt,
            Transform upperLeg, Transform lowerLeg, Vector3 footPos, Quaternion footRot,
            ref Vector3 smoothedBendDir, ref float smoothedWeight,
            out Vector3 hintPos, out float weight, out Vector3 bendDir)
        {
            hintPos = default;
            weight = 0f;
            bendDir = hipsRot * Vector3.forward;
            if (upperLeg == null || lowerLeg == null)
            {
                smoothedWeight = 0f;
                return false;
            }

            Vector3 hipPos = upperLeg.position;

            BasisKneeForwardInput input;
            input.HipPosition = hipPos;
            input.FootPosition = footPos;
            input.FootForwardDir = footRot * Vector3.forward;    // foot toe direction
            input.BodyForwardDir = hipsRot * Vector3.forward;
            input.PlayerUp = playerUp;
            input.UpperLength = Vector3.Distance(hipPos, lowerLeg.position);
            input.Coupling = coupling;
            input.Strength = 1f;

            BasisKneeForwardCore.Solve(input, out BasisKneeForwardResult result);

            float alpha = 1f - Mathf.Exp(-smoothRate * dt);
            if (smoothedBendDir.sqrMagnitude < 1e-6f)
                smoothedBendDir = result.BendDir;
            else
                smoothedBendDir = Vector3.Slerp(smoothedBendDir.normalized, result.BendDir, alpha);
            smoothedWeight = Mathf.Lerp(smoothedWeight, result.HintWeight, alpha);

            bendDir = smoothedBendDir.sqrMagnitude > 1e-6f ? smoothedBendDir.normalized : result.BendDir;

            Vector3 mid = (hipPos + footPos) * 0.5f;
            float radius = input.UpperLength > 1e-5f ? input.UpperLength : 0.4f;
            hintPos = mid + bendDir * radius;
            weight = smoothedWeight;
            return weight > 0.001f;
        }

        static List<Transform> CollectIKBones(BasisTransformMapping d) => new List<Transform>
        {
            d.Hips, d.spine, d.chest, d.Upperchest, d.neck, d.head,
            d.leftShoulder, d.RightShoulder,
            d.leftUpperArm, d.leftLowerArm, d.leftHand,
            d.RightUpperArm, d.RightLowerArm, d.rightHand,
            d.leftUpperArmTwist, d.leftLowerArmTwist,
            d.RightUpperArmTwist, d.RightLowerArmTwist,
            d.LeftUpperLeg, d.LeftLowerLeg, d.leftFoot,
            d.RightUpperLeg, d.RightLowerLeg, d.rightFoot,
            d.leftToe, d.rightToe,
        };

        void RunIKSolve(float deltaTime, bool streamPrefilled = false)
        {
            if (!RigLayerActive || !IKJobCreated || !PoseSkeleton.IsCreated)
            {
                return;
            }

            if (!streamPrefilled)
            {
                sMarkerIKDestPoseGather.Begin();
                PoseSkeleton.GatherNow();
                sMarkerIKDestPoseGather.End();
            }

            sMarkerIKDestApplyFit.Begin();
            PoseSkeleton.ApplyFit();
            sMarkerIKDestApplyFit.End();

            sMarkerIKDestSolve.Begin();
            IKJob.poseStream = PoseSkeleton.Stream;
            IKJob.poseStream.deltaTime = deltaTime;
            IKJob.Run();
            sMarkerIKDestSolve.End();

            // Leg diagnostics are written INSIDE the job, so read them here and not before Run().
            if (BasisLegSwivelDebug.Enabled)
            {
                if (TryGetLegDiagnostics(0, out Basis.IK.BasisLegDiagnostics dl))
                {
                    BasisLegSwivelDebug.Record("L", Time.time, dl, BendVsAnteriorDeg(IKJob.kneeBendPrefLeft));
                }
                if (TryGetLegDiagnostics(1, out Basis.IK.BasisLegDiagnostics dr))
                {
                    BasisLegSwivelDebug.Record("R", Time.time, dr, BendVsAnteriorDeg(IKJob.kneeBendPrefRight));
                }
            }

            sMarkerIKDestPoseScatter.Begin();
            PoseSkeleton.ScatterNow();
            sMarkerIKDestPoseScatter.End();
        }

        // How far a leg's bend plane has drifted from the body frame. BendNormal rides the lower-leg TRACKER
        // when FBIKTrackerBendNormal is on; kneeAnteriorRef is always hips-right. The anterior guard is measured
        // against the second and the pole eases pull toward the first, so a large angle here is what turns a
        // well-conditioned leg into an ill-conditioned one -- and it is per-leg, which is what makes it the
        // first thing to check when only one knee misbehaves. See BasisLegSwivelDebug.
        float BendVsAnteriorDeg(Vector3 bendNormal)
        {
            Vector3 anterior = IKJob.kneeAnteriorRef;
            if (bendNormal.sqrMagnitude < 1e-8f || anterior.sqrMagnitude < 1e-8f)
            {
                return 0f;
            }

            return Vector3.Angle(bendNormal, anterior);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetOverrideUsage(HumanBodyBones bone, bool enabled)
        {
            ref BasisEerieMovement data = ref IKJob;
            data.SetWeight((int)bone, enabled);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetOverrideData(HumanBodyBones bone, in Vector3 position, in Quaternion rotation)
        {
            ref BasisEerieMovement data = ref IKJob;
            data.SetTargetPosition((int)bone, position);
            data.SetTargetRotation((int)bone, rotation);
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
