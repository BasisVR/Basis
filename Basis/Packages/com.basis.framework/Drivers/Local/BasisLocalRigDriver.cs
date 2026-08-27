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
using UnityEngine;
using Basis.IK;
using UnityEngine.Jobs;
using UnityEngine.Playables;
using static Basis.Scripts.Avatar.BasisAvatarIKStageCalibration;
using static BasisHeightDriver;
using Basis.Scripts.Debugging;
namespace Basis.Scripts.Drivers
{
    [Serializable]
    public partial class BasisLocalRigDriver
    {
        public static float MinCutoff = 5.5f, Beta = 3.25f, DerivativeCutoff = 3f, SmoothingStrength = 1f;
        [System.NonSerialized] public PlayableGraph PlayableGraph;
        [System.NonSerialized] public readonly BasisPoseSkeleton PoseSkeleton = new BasisPoseSkeleton();
        [System.NonSerialized] public readonly BasisLocomotionPoseSystem LocomotionPose = new BasisLocomotionPoseSystem();
        [System.NonSerialized] public BasisEerieMovement IKJob;
        [System.NonSerialized] public bool IKJobCreated;
        public bool RigLayerActive = true;
        [System.NonSerialized] public bool IKDataReady;
        JobHandle ikSolveHandle;
        bool ikSolveScheduled, ikScatterPending, ikPublishPending;
        public Quaternion LeftHandIKOffset => IKDataReady ? IKJob.offsetRotationLeftHand : Quaternion.identity;
        public Quaternion RightHandIKOffset => IKDataReady ? IKJob.offsetRotationRightHand : Quaternion.identity;
        private BasisLocalPlayer localPlayer;
        public BasisTransformMapping basisTransformMapping;
        public const int sHips = 0, sHead = 1, sLeftFoot = 2, sRightFoot = 3, sChest = 4, sLeftLowerLeg = 5, sRightLowerLeg = 6, sLeftHand = 7, sRightHand = 8, sLeftLowerArm = 9, sRightLowerArm = 10, sLeftToe = 11, sRightToe = 12, sLeftShoulder = 13, sRightShoulder = 14, SlotCount = 15;
        static readonly string[] SlotNames =
        {
            "Hips", "Head", "LeftFoot", "RightFoot", "Chest", "LeftLowerLeg", "RightLowerLeg", "LeftHand", "RightHand", "LeftLowerArm", "RightLowerArm", "LeftToe", "RightToe", "LeftShoulder", "RightShoulder",
        };
        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        private void WatchdogCheckFilterSlots(string stage)
        {
            if (!BasisFiniteWatchdog.Enabled || !posInputs.IsCreated)
            {
                return;
            }
            for (int i = 0; i < SlotCount; i++)
            {
                string slot = i < SlotNames.Length ? SlotNames[i] : i.ToString();
                if (BasisFiniteWatchdog.IsNonFinite((Vector3)posInputs[i]))
                {
                    BasisFiniteWatchdog.ReportValue(stage, $"IK raw position input, slot '{slot}' (bone control OutGoingData, playspace local)", posInputs[i].ToString());
                    return;
                }
                if (BasisFiniteWatchdog.IsNonFinite((Quaternion)rotInputs[i]))
                {
                    BasisFiniteWatchdog.ReportValue(stage, $"IK raw rotation input, slot '{slot}' (bone control OutGoingData, playspace local)", rotInputs[i].ToString());
                    return;
                }
                BasisEuroVec3State posState = euroPosStates[i];
                if (BasisFiniteWatchdog.IsNonFinite((Vector3)posState.hatX) || BasisFiniteWatchdog.IsNonFinite((Vector3)posState.hatDx))
                {
                    BasisFiniteWatchdog.ReportValue(stage, $"IK one-euro POSITION state latched, slot '{slot}' — raw input is finite, so this slot was poisoned on an earlier frame and can never recover", $"hatX={posState.hatX} hatDx={posState.hatDx} mode={posModeNative[i]}");
                    return;
                }
                BasisEuroQuatState rotState = euroRotStates[i];
                if (BasisFiniteWatchdog.IsNonFinite((Quaternion)rotState.prev) || BasisFiniteWatchdog.IsNonFinite((Vector3)rotState.logVecState.hatX) || BasisFiniteWatchdog.IsNonFinite((Vector3)rotState.logVecState.hatDx))
                {
                    BasisFiniteWatchdog.ReportValue(stage, $"IK one-euro ROTATION state latched, slot '{slot}'", $"prev={rotState.prev} hatX={rotState.logVecState.hatX} hatDx={rotState.logVecState.hatDx} mode={rotModeNative[i]}");
                    return;
                }
                if (BasisFiniteWatchdog.IsNonFinite((Vector3)fallbackPosStates[i]))
                {
                    BasisFiniteWatchdog.ReportValue(stage, $"IK fallback position state, slot '{slot}'", fallbackPosStates[i].ToString());
                    return;
                }
                if (BasisFiniteWatchdog.IsNonFinite((Vector3)posOutputs[i]))
                {
                    BasisFiniteWatchdog.ReportValue(stage, $"IK filtered position OUTPUT, slot '{slot}' — input and state are finite, so the filter produced it", $"{posOutputs[i]} mode={posModeNative[i]} tuning={posTuning[i]}");
                    return;
                }
                if (BasisFiniteWatchdog.IsNonFinite((Quaternion)rotOutputs[i]))
                {
                    BasisFiniteWatchdog.ReportValue(stage, $"IK filtered rotation OUTPUT, slot '{slot}'", $"{rotOutputs[i]} mode={rotModeNative[i]} tuning={rotTuning[i]}");
                    return;
                }
            }
        }
        System.IntPtr watchdogStreamPtr;
        string watchdogStreamStage;
        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        private unsafe void WatchdogCheckPoseStream(string stage)
        {
            if (!BasisFiniteWatchdog.Enabled || !PoseSkeleton.IsCreated)
            {
                return;
            }
            var stream = PoseSkeleton.Stream;
            Transform[] nodes = PoseSkeleton.Nodes;

            System.IntPtr streamPtr = (System.IntPtr)stream.LocalRotation.GetUnsafeReadOnlyPtr();
            bool bufferReplaced = watchdogStreamPtr != System.IntPtr.Zero && streamPtr != watchdogStreamPtr;
            string previousStage = watchdogStreamStage;
            watchdogStreamPtr = streamPtr;
            watchdogStreamStage = stage;
            System.Text.StringBuilder bad = null;
            int badCount = 0;
            string firstNode = null;
            for (int i = 0; i < stream.Count; i++)
            {
                bool badPosition = BasisFiniteWatchdog.IsNonFinite((Vector3)stream.LocalPosition[i]);
                bool badRotation = BasisFiniteWatchdog.IsNonFinite((Quaternion)stream.LocalRotation[i]);
                if (!badPosition && !badRotation)
                {
                    continue;
                }
                string node = nodes != null && i < nodes.Length && nodes[i] != null ? nodes[i].name : i.ToString();
                badCount++;
                if (bad == null)
                {
                    bad = new System.Text.StringBuilder(512);
                    firstNode = node;
                }

                if (badCount <= 12)
                {
                    bad.Append($"\n    [{i}] '{node}' localPosition={stream.LocalPosition[i]} localRotation={stream.LocalRotation[i]} " + $"translationFree={stream.TranslationFree[i] != 0} bindLength={stream.BindLength[i]} " + $"fitScale={PoseSkeleton.FitScale[i]} writable={System.Array.IndexOf(PoseSkeleton.WriteIndices, i) >= 0}");
                }
            }
            if (bad == null)
            {
                return;
            }
            BasisFiniteWatchdog.ReportValue(stage, $"IK pose stream, first bad node '{firstNode}'", $"{badCount}/{stream.Count} node(s) bad, fitActive={PoseSkeleton.FitActive}, " + $"scatterPending={ikScatterPending}, publishPending={ikPublishPending}, solveScheduled={ikSolveScheduled}" + (bufferReplaced ? $"\n    ** THE STREAM BUFFER WAS REPLACED since '{previousStage}' — the rig was rebuilt mid-frame, so these values are fresh allocation memory, not solve output. **" : $"\n    (same stream buffer as '{previousStage ?? "<first check>"}')") + bad);
        }
        public static bool[] SmoothPos = new bool[SlotCount], SmoothRot = new bool[SlotCount];
        public static bool[] EuroPos = new bool[SlotCount], EuroRot = new bool[SlotCount];
        [Range(0.01f, 60f)] public static float PositionSmoothingHz = 20f;
        [Range(0.01f, 60f)] public static float RotationSmoothingHz = 25f;
        public double timeAccumulator;
        public static Vector3 sPosHips, sPosHead, sPosLeftFoot, sPosRightFoot, sPosChest, sPosLeftLowerLeg;
        public static Vector3 sPosRightLowerLeg, sPosLeftHand, sPosRightHand, sPosLeftLowerArm, sPosRightLowerArm;
        public static Vector3 sPosLeftToe, sPosRightToe;
        public static Quaternion sRotHips, sRotHead, sRotLeftFoot, sRotRightFoot, sRotChest, sRotLeftLowerLeg;
        public static Quaternion sRotRightLowerLeg, sRotLeftHand, sRotRightHand, sRotLeftLowerArm, sRotRightLowerArm;
        public static Quaternion sRotLeftToe, sRotRightToe, sRotLeftShoulder, sRotRightShoulder;
        public static bool hasFallbackState;
        private static Vector3 smoothedLeftButterflyHint, smoothedRightButterflyHint;
        private static float smoothedLeftButterflyWeight, smoothedRightButterflyWeight;
        private const float ButterflyKneeSmoothRate = 8f;
        private static Vector3 smoothedLeftKneeFwdHint, smoothedRightKneeFwdHint;
        private static float smoothedLeftKneeFwdWeight, smoothedRightKneeFwdWeight;
        private const float KneeForwardSmoothRate = 10f;
        private static float footIKBlendWeightLeft = 0f, footIKBlendWeightRight = 0f;
        private static float footIKBlendWeight = 0f;
        private static float stationaryTimer = 0f;
        private NativeArray<float3> posInputs, posOutputs;
        private NativeArray<quaternion> rotInputs, rotOutputs;
        private NativeArray<byte> posModeNative, rotModeNative;
        private NativeArray<float4> posTuning, rotTuning;
        private NativeArray<float3> fallbackPosStates;
        private NativeArray<quaternion> fallbackRotStates;
        private NativeArray<BasisEuroVec3State> euroPosStates;
        private NativeArray<BasisEuroQuatState> euroRotStates;
        private TransformAccessArray ikPublishTransforms;
        private BasisLocalBoneControl[] ikPublishControls, ikFallbackControls;
        private NativeArray<float3> ikPublishPositions;
        private NativeArray<quaternion> ikPublishRotations;
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
            PlayableGraph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);
            LocomotionPose.CompleteIfPending();
            CompleteSolveIfPending();

            ikScatterPending = false;
            ikPublishPending = false;
            PoseSkeleton.Build(animator.transform, CollectIKBones(basisTransformMapping));
            PoseSkeleton.SetTranslationFree(basisTransformMapping.Hips);
            leftHipJointRestLocal = basisTransformMapping.Hips != null && basisTransformMapping.LeftUpperLeg != null ? basisTransformMapping.Hips.InverseTransformPoint(basisTransformMapping.LeftUpperLeg.position) : Vector3.zero;
            rightHipJointRestLocal = basisTransformMapping.Hips != null && basisTransformMapping.RightUpperLeg != null ? basisTransformMapping.Hips.InverseTransformPoint(basisTransformMapping.RightUpperLeg.position) : Vector3.zero;
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

            LocomotionPose.CompleteIfPending();
            CompleteSolveIfPending();
            if (!Basis.BasisUI.BasisSettingsDefaults.FBIKBodyFit.RawValue)
            {
                if (PoseSkeleton.FitActive)
                {
                    PoseSkeleton.ResetFit();
                    PoseSkeleton.WriteFittedLocalPositions();
                }
                IKJob.RescaleTposeFit(1f, 1f);
                AppliedBodyFit = BasisBodyFitResult.Identity;
                BasisBodyFitNetworking.UpdateLocalFit(in AppliedBodyFit);
                BasisLocalPlayer.Instance?.BasisLocalFootDriver?.RefreshBodyFitScale();
                return;
            }
            var measurements = new BasisBodyFitMeasurements
            {
                PlayerEyeHeight = BasisHeightDriver.PlayerEyeHeight, PlayerArmSpan = BasisHeightDriver.PlayerArmSpan, PlayerHipHeight = BasisHeightDriver.PlayerHipHeight, AvatarEyeHeight = BasisHeightDriver.AvatarEyeHeight, AvatarArmSpan = BasisHeightDriver.AvatarArmSpan, AvatarHipHeight = BasisHeightDriver.AvatarHipHeight, AvatarLegSpan = BasisHeightDriver.AvatarLegSpan, AvatarSpineSpan = BasisHeightDriver.AvatarSpineSpan, AvatarShoulderWidth = BasisHeightDriver.AvatarShoulderWidth,

                UniformScale = BasisHeightDriver.AppliedUniformScale,
            };
            BasisBodyFitResult fit = BasisBodyFitCore.Solve( measurements, Basis.BasisUI.BasisSettingsDefaults.FBIKBodyFitMaxDeviation.RawValue);
            BasisBodyFitApply.Apply(PoseSkeleton, basisTransformMapping, fit);
            IKJob.RescaleTposeFit(fit.HasArmFit ? fit.ArmScale : 1f, fit.HasBodyFit ? fit.TorsoScale : 1f);
            AppliedBodyFit = fit;

            BasisBodyFitNetworking.UpdateLocalFit(in fit);

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

            HasRecalibratedRotationOffsets = false;
            Spine();
            BasisLocalBoneControl.HasEvents = true;

            ApplyCalibrationToCurrentAvatar();
            BuildIKPublishArrays();
        }
        public void CleanupBeforeContinue()
        {
            CompleteSolveIfPending();
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
            if (posInputs.IsCreated) return;
            posInputs = new NativeArray<float3>(SlotCount, Allocator.Persistent);
            posOutputs = new NativeArray<float3>(SlotCount, Allocator.Persistent);
            rotInputs = new NativeArray<quaternion>(SlotCount, Allocator.Persistent);
            rotOutputs = new NativeArray<quaternion>(SlotCount, Allocator.Persistent);
            posModeNative = new NativeArray<byte>(SlotCount, Allocator.Persistent);
            rotModeNative = new NativeArray<byte>(SlotCount, Allocator.Persistent);
            posTuning = new NativeArray<float4>(SlotCount, Allocator.Persistent);
            rotTuning = new NativeArray<float4>(SlotCount, Allocator.Persistent);
            fallbackPosStates = new NativeArray<float3>(SlotCount, Allocator.Persistent);
            fallbackRotStates = new NativeArray<quaternion>(SlotCount, Allocator.Persistent);
            euroPosStates = new NativeArray<BasisEuroVec3State>(SlotCount, Allocator.Persistent);
            euroRotStates = new NativeArray<BasisEuroQuatState>(SlotCount, Allocator.Persistent);

            for (int i = 0; i < SlotCount; i++)
            {
                rotInputs[i] = quaternion.identity;
                rotOutputs[i] = quaternion.identity;
                fallbackRotStates[i] = quaternion.identity;
            }
        }
        private void DisposeFilterArrays()
        {
            if (posInputs.IsCreated) posInputs.Dispose();
            if (posOutputs.IsCreated) posOutputs.Dispose();
            if (rotInputs.IsCreated) rotInputs.Dispose();
            if (rotOutputs.IsCreated) rotOutputs.Dispose();
            if (posModeNative.IsCreated) posModeNative.Dispose();
            if (rotModeNative.IsCreated) rotModeNative.Dispose();
            if (posTuning.IsCreated) posTuning.Dispose();
            if (rotTuning.IsCreated) rotTuning.Dispose();
            if (fallbackPosStates.IsCreated) fallbackPosStates.Dispose();
            if (fallbackRotStates.IsCreated) fallbackRotStates.Dispose();
            if (euroPosStates.IsCreated) euroPosStates.Dispose();
            if (euroRotStates.IsCreated) euroRotStates.Dispose();
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte PickMode(bool smoothEnabled, bool euroEnabled)
        {
            if (!smoothEnabled) return (byte)BasisFilterMode.Passthrough;
            return euroEnabled ? (byte)BasisFilterMode.Euro : (byte)BasisFilterMode.Fallback;
        }
        private static readonly float4[] groupPosTuning = new float4[BasisSmoothingProfiles.GroupCount], groupRotTuning = new float4[BasisSmoothingProfiles.GroupCount];
        private static readonly bool[] groupOff = new bool[BasisSmoothingProfiles.GroupCount];
        private static readonly BasisTrackingHardware[] groupHardware = new BasisTrackingHardware[BasisSmoothingProfiles.GroupCount];
        private static void ResolveGroupHardware()
        {
            for (int Index = 0; Index < groupHardware.Length; Index++)
            {
                groupHardware[Index] = BasisTrackingHardware.Unknown;
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

                if (device == null || device.IsLinked)
                {
                    continue;
                }
                if (!device.TryGetRole(out BasisBoneTrackedRole role) || !BasisSmoothingProfiles.TryGetGroupForRole(role, out int group))
                {
                    continue;
                }
                if ((byte)device.TrackingHardware > (byte)groupHardware[group])
                {
                    groupHardware[group] = device.TrackingHardware;
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

                if (BasisSmoothingProfiles.IsAuto(preset))
                {
                    preset = BasisSmoothingProfiles.PresetForHardware(groupHardware[Index]);
                }
                groupOff[Index] = BasisSmoothingProfiles.IsOff(preset);
                BasisSmoothingProfile profile;
                float strength;
                if (BasisSmoothingProfiles.IsCustom(preset))
                {
                    profile = new BasisSmoothingProfile( bindings.MinCutoff.RawValue, bindings.Beta.RawValue, DerivativeCutoff, bindings.PositionHz.RawValue, bindings.RotationHz.RawValue);
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
                float minCutoff = profile.MinCutoff / strength, dCutoff = profile.DerivativeCutoff / strength;
                groupPosTuning[Index] = new float4(minCutoff, profile.Beta, dCutoff, ExpAlpha(profile.PositionHz / strength, deltaTime));
                groupRotTuning[Index] = new float4(minCutoff, profile.Beta, dCutoff, ExpAlpha(profile.RotationHz / strength, deltaTime));
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

            if (euroPosStates.IsCreated)
            {
                for (int i = 0; i < SlotCount; i++) euroPosStates[i] = default;
            }
            if (euroRotStates.IsCreated)
            {
                for (int i = 0; i < SlotCount; i++) euroRotStates[i] = default;
            }
            if (fallbackRotStates.IsCreated)
            {
                for (int i = 0; i < SlotCount; i++) fallbackRotStates[i] = quaternion.identity;
            }
            if (fallbackPosStates.IsCreated)
            {
                for (int i = 0; i < SlotCount; i++) fallbackPosStates[i] = float3.zero;
            }

            smoothedLeftButterflyHint = smoothedRightButterflyHint = Vector3.zero;
            smoothedLeftButterflyWeight = smoothedRightButterflyWeight = 0f;
            smoothedLeftKneeFwdHint = smoothedRightKneeFwdHint = Vector3.zero;
            smoothedLeftKneeFwdWeight = smoothedRightKneeFwdWeight = 0f;
            footIKBlendWeightLeft = footIKBlendWeightRight = footIKBlendWeight = 0f;
            stationaryTimer = 0f;
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
            if (!ikPublishTransforms.isCreated || ikPublishControls == null || ikPublishTransforms.length != ikPublishControls.Length)
            {
                PublishIKWorldDataMainThread();
                return;
            }
            if (ikPublishControls.Length > 0)
            {

                new BasisReadBoneWorldPoseJob
                {
                    Positions = ikPublishPositions, Rotations = ikPublishRotations,
                }.RunReadOnly(ikPublishTransforms);
                for (int i = 0; i < ikPublishControls.Length; i++)
                {
                    ikPublishControls[i].SetIKWorldData(ikPublishPositions[i], ikPublishRotations[i]);
                }
            }
            var fallback = ikFallbackControls;
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
                (BasisLocalBoneDriver.HeadControl, m.head, m.Hashead), (BasisLocalBoneDriver.NeckControl, m.neck, m.Hasneck), (BasisLocalBoneDriver.ChestControl, m.chest, m.Haschest), (BasisLocalBoneDriver.SpineControl, m.spine, m.Hasspine), (BasisLocalBoneDriver.HipsControl, m.Hips, m.HasHips), (BasisLocalBoneDriver.LeftShoulderControl, m.leftShoulder, m.HasleftShoulder), (BasisLocalBoneDriver.LeftLowerArmControl, m.leftLowerArm, m.HasleftLowerArm), (BasisLocalBoneDriver.LeftHandControl, m.leftHand, m.HasleftHand), (BasisLocalBoneDriver.RightShoulderControl, m.RightShoulder, m.HasRightShoulder), (BasisLocalBoneDriver.RightLowerArmControl, m.RightLowerArm, m.HasRightLowerArm), (BasisLocalBoneDriver.RightHandControl, m.rightHand, m.HasrightHand), (BasisLocalBoneDriver.LeftUpperLegControl, m.LeftUpperLeg, m.HasLeftUpperLeg), (BasisLocalBoneDriver.LeftLowerLegControl, m.LeftLowerLeg, m.HasLeftLowerLeg), (BasisLocalBoneDriver.LeftFootControl, m.leftFoot, m.HasleftFoot), (BasisLocalBoneDriver.LeftToeControl, m.leftToe, m.HasleftToes), (BasisLocalBoneDriver.RightUpperLegControl, m.RightUpperLeg, m.HasRightUpperLeg), (BasisLocalBoneDriver.RightLowerLegControl, m.RightLowerLeg, m.HasRightLowerLeg), (BasisLocalBoneDriver.RightFootControl, m.rightFoot, m.HasrightFoot), (BasisLocalBoneDriver.RightToeControl, m.rightToe, m.HasrightToes), (BasisLocalBoneDriver.EyeControl, null, false), (BasisLocalBoneDriver.MouthControl, null, false),
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
            ikPublishControls = solvedControls.ToArray();
            ikFallbackControls = fallbackControls.ToArray();
            ikPublishTransforms = new TransformAccessArray(solvedTransforms.ToArray());
            ikPublishPositions = new NativeArray<float3>(ikPublishControls.Length, Allocator.Persistent);
            ikPublishRotations = new NativeArray<quaternion>(ikPublishControls.Length, Allocator.Persistent);
        }
        private void DisposeIKPublishArrays()
        {
            if (ikPublishTransforms.isCreated) ikPublishTransforms.Dispose();
            if (ikPublishPositions.IsCreated) ikPublishPositions.Dispose();
            if (ikPublishRotations.IsCreated) ikPublishRotations.Dispose();
            ikPublishControls = null;
            ikFallbackControls = null;
        }
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

            PublishBoneIK(BasisLocalBoneDriver.EyeControl, null, false);
            PublishBoneIK(BasisLocalBoneDriver.MouthControl, null, false);
        }
        private static void PublishBoneIK(BasisLocalBoneControl control, Transform bone, bool has)
        {
            if (control == null) return;
            if (has && bone != null)
            {
                bone.GetPose(out Vector3 position, out Quaternion rotation);
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

            BodyData.handSkin = Basis.BasisUI.BasisSettingsDefaults.FBIKHandSkin.RawValue * Scale;
            BodyData.handRadius = Basis.BasisUI.BasisSettingsDefaults.FBIKHandRadius.RawValue * Scale;
            BodyData.chestRadius = Basis.BasisUI.BasisSettingsDefaults.FBIKChestRadius.RawValue * Scale;
            BodyData.collisionSkin = Basis.BasisUI.BasisSettingsDefaults.FBIKCollisionSkin.RawValue * Scale;
            BasisCalibratedCoords hips = BasisLocalBoneDriver.HipsControl.TposeLocalScaled, spine = BasisLocalBoneDriver.SpineControl.TposeLocalScaled, chest = BasisLocalBoneDriver.ChestControl.TposeLocalScaled, neck = BasisLocalBoneDriver.NeckControl.TposeLocalScaled, head = BasisLocalBoneDriver.HeadControl.TposeLocalScaled;
            float minHeadSpineHeight = 0f;
            minHeadSpineHeight += Vector3.Distance(hips.position, spine.position);
            minHeadSpineHeight += Vector3.Distance(spine.position, chest.position);
            minHeadSpineHeight += Vector3.Distance(chest.position, neck.position);
            minHeadSpineHeight += Vector3.Distance(neck.position, head.position);
            BodyData.minHeadSpineHeight = minHeadSpineHeight * (BodyData.tposeTorsoFitScale > 0f ? BodyData.tposeTorsoFitScale : 1f);

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
        public static bool HasRecalibratedRotationOffsets;
        public static Quaternion RecalibratedHead, RecalibratedHips, RecalibratedChest, RecalibratedLeftFoot;
        public static Quaternion RecalibratedRightFoot, RecalibratedLeftToe, RecalibratedRightToe;
        public static Quaternion RecalibratedLeftShoulder, RecalibratedRightShoulder;
        private static void ApplyTuningSettings(ref BasisEerieMovement data)
        {
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
            data.chestBendPitch = Basis.BasisUI.BasisSettingsDefaults.FBIKChestBendPitch.RawValue;
            data.chestBendYaw = Basis.BasisUI.BasisSettingsDefaults.FBIKChestBendYaw.RawValue;
            data.chestBendRoll = Basis.BasisUI.BasisSettingsDefaults.FBIKChestBendRoll.RawValue;
            data.neckYawShare = Basis.BasisUI.BasisSettingsDefaults.FBIKNeckYawShare.RawValue;
            data.spineStretchMax = Basis.BasisUI.BasisSettingsDefaults.FBIKSpineStretchMax.RawValue;
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
            data.neckExtensionDamp = Basis.BasisUI.BasisSettingsDefaults.FBIKNeckExtensionDamp.RawValue;
            data.neckFlexionDamp = Basis.BasisUI.BasisSettingsDefaults.FBIKNeckFlexionDamp.RawValue;
            data.moveBodyBackWhenCrouching = Basis.BasisUI.BasisSettingsDefaults.FBIKMoveBodyBackWhenCrouching.RawValue;
            data.trunkCounterbalance = Basis.BasisUI.BasisSettingsDefaults.FBIKTrunkCounterbalance.RawValue;
            data.swingSmoothRateDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKElbowSwingEnabled.RawValue ? Basis.BasisUI.BasisSettingsDefaults.FBIKSwingSmoothRate.RawValue : 0f;
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

            float collisionScale = BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale;

            data.lordosisExtremeHipsHorizontalMax = Basis.BasisUI.BasisSettingsDefaults.FBIKLordosisExtremeHipsHorizontalMax.RawValue * collisionScale;
            data.lordosisExtremeChestHorizontalMax = Basis.BasisUI.BasisSettingsDefaults.FBIKLordosisExtremeChestHorizontalMax.RawValue * collisionScale;
            data.lordosisExtremeHipsHorizontalLookUp = Basis.BasisUI.BasisSettingsDefaults.FBIKLordosisExtremeHipsHorizontalLookUp.RawValue * collisionScale;
            data.lordosisExtremeChestHorizontalLookUp = Basis.BasisUI.BasisSettingsDefaults.FBIKLordosisExtremeChestHorizontalLookUp.RawValue * collisionScale;
            data.lordosisExtremeHipsDownMax = Basis.BasisUI.BasisSettingsDefaults.FBIKLordosisExtremeHipsDownMax.RawValue * collisionScale;
            data.lordosisExtremeChestDownMax = Basis.BasisUI.BasisSettingsDefaults.FBIKLordosisExtremeChestDownMax.RawValue * collisionScale;
            data.lordosisExtremeHipsDownLookUp = Basis.BasisUI.BasisSettingsDefaults.FBIKLordosisExtremeHipsDownLookUp.RawValue * collisionScale;
            data.lordosisExtremeChestDownLookUp = Basis.BasisUI.BasisSettingsDefaults.FBIKLordosisExtremeChestDownLookUp.RawValue * collisionScale;

            data.collisionsEnabled = Basis.BasisUI.BasisSettingsDefaults.FBIKCollisionsEnabled.RawValue;
            data.protectElbow = Basis.BasisUI.BasisSettingsDefaults.FBIKProtectElbow.RawValue;

            data.collideTrackedElbow = Basis.BasisUI.BasisSettingsDefaults.FBIKCollideTrackedElbow.RawValue;

            data.elbowDragEnabled = Basis.BasisUI.BasisSettingsDefaults.FBIKElbowDrag.RawValue;
            data.elbowDragHz = Basis.BasisUI.BasisSettingsDefaults.FBIKElbowDragHz.RawValue;
            data.shoulderSolveEnabled = Basis.BasisUI.BasisSettingsDefaults.FBIKShoulderSolveEnabled.RawValue;
            data.shoulderShrugEnabled = Basis.BasisUI.BasisSettingsDefaults.FBIKShoulderShrug.RawValue;

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
        public void DisableAllTrackers() => trackersDisabled = true;
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
        public void RestoreAllTrackers() => trackersDisabled = false;
        public static bool TryFootTargetRotation(Quaternion footRot, Quaternion offset, out Quaternion target)
        {
            target = Quaternion.identity;
            float offSqr = offset.x * offset.x + offset.y * offset.y + offset.z * offset.z + offset.w * offset.w;
            if (!(offSqr > 0.5f)) return false;
            Quaternion result = footRot * Quaternion.Inverse(offset);
            float resSqr = result.x * result.x + result.y * result.y + result.z * result.z + result.w * result.w;
            if (!(resSqr > 0.5f)) return false;
            target = result;
            return true;
        }
        private static bool HasRigLayer(BasisLocalBoneControl control)
        {
            return control.HasRigLayer == BasisHasRigLayer.HasRigLayer;
        }
        private static float HandRigWeight(BasisLocalBoneControl control)
        {
            if (control == null || control.HasRigLayer != BasisHasRigLayer.HasRigLayer) return 0f;
            float w = control.RigLayerWeight;
            return w > 0f ? (w < 1f ? w : 1f) : 0f;
        }
        private static bool TryComputeButterflyKnee( bool isLeft, Quaternion hipsRot, Vector3 playerUp, float maxOpenDeg, float supineFloor, float dt, Vector3 defaultBendDir, bool haveLeg, Vector3 hipPos, float upperLength, float lowerLength, Vector3 footPos, Quaternion footRot, ref Vector3 smoothedHint, ref float smoothedWeight, out Vector3 hintPos, out float weight)
        {
            hintPos = default;
            weight = 0f;
            if (!haveLeg)
            {
                smoothedWeight = 0f;
                return false;
            }
            Vector3 hipsRight = hipsRot * Vector3.right, hipsForward = hipsRot * Vector3.forward;
            BasisButterflyKneeInput input;
            input.HipPosition = hipPos;
            input.FootPosition = footPos;
            input.FootInstepDir = footRot * Vector3.up;
            input.OutwardDir = isLeft ? -hipsRight : hipsRight;
            input.DefaultBendDir = defaultBendDir.sqrMagnitude > 1e-6f ? defaultBendDir : hipsForward;
            input.PlayerUp = playerUp;
            input.TorsoFacingDir = hipsForward;
            input.UpperLength = upperLength;
            input.LowerLength = lowerLength;
            input.MaxOpenDeg = maxOpenDeg;
            input.Strength = 1f;
            input.SupineFloor = supineFloor;
            BasisButterflyKneeCore.Solve(input, out BasisButterflyKneeResult result);

            float alpha = 1f - Mathf.Exp(-ButterflyKneeSmoothRate * dt);
            if (smoothedWeight <= 0.0001f && result.HintWeight <= 0.0001f)
            {

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
        private static bool TryComputeKneeForward( Quaternion hipsRot, float coupling, float smoothRate, Vector3 playerUp, float dt, bool haveLeg, Vector3 hipPos, float upperLength, Vector3 footPos, Quaternion footRot, ref Vector3 smoothedBendDir, ref float smoothedWeight, out Vector3 hintPos, out float weight, out Vector3 bendDir)
        {
            hintPos = default;
            weight = 0f;
            bendDir = hipsRot * Vector3.forward;
            if (!haveLeg)
            {
                smoothedWeight = 0f;
                return false;
            }
            BasisKneeForwardInput input;
            input.HipPosition = hipPos;
            input.FootPosition = footPos;
            input.FootForwardDir = footRot * Vector3.forward;
            input.BodyForwardDir = hipsRot * Vector3.forward;
            input.PlayerUp = playerUp;
            input.UpperLength = upperLength;
            input.Coupling = coupling;
            input.Strength = 1f;
            BasisKneeForwardCore.Solve(input, out BasisKneeForwardResult result);
            float alpha = 1f - Mathf.Exp(-smoothRate * dt);
            if (smoothedBendDir.sqrMagnitude < 1e-6f) smoothedBendDir = result.BendDir;
            else smoothedBendDir = Vector3.Slerp(smoothedBendDir.normalized, result.BendDir, alpha);
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
            d.Hips, d.spine, d.chest, d.Upperchest, d.neck, d.head, d.leftShoulder, d.RightShoulder, d.leftUpperArm, d.leftLowerArm, d.leftHand, d.RightUpperArm, d.RightLowerArm, d.rightHand, d.leftUpperArmTwist, d.leftLowerArmTwist, d.RightUpperArmTwist, d.RightLowerArmTwist, d.LeftUpperLeg, d.LeftLowerLeg, d.leftFoot, d.RightUpperLeg, d.RightLowerLeg, d.rightFoot, d.leftToe, d.rightToe,
        };
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

            if (BasisLocalAvatarDriver.Mapping != null && BasisLocalAvatarDriver.Mapping.GetTransform(bone, out Transform refT))
            {
                return refT;
            }

            var animator = localPlayer?.BasisAvatar?.Animator;
            return animator != null ? animator.GetBoneTransform(bone) : null;
        }
        bool frameFootSimScheduled, frameFootReengage, trackersDisabled;
        BasisEerieFrameFacts frameFacts;
        Quaternion frameHipsRotation, frameLeftSimFootRotation, frameRightSimFootRotation, frameLeftElbowToBone, frameRightElbowToBone, frameLeftKneeToBone, frameRightKneeToBone;
        Vector3 framePlayerUpDirection, frameLeftKneeAssistHint, frameRightKneeAssistHint, frameLeftKneeBendAxis, frameRightKneeBendAxis;
        Vector3 leftHipJointRestLocal, rightHipJointRestLocal;
        float framePlayerUpScale;
        public void ScheduleLocomotionPose(BasisLocalPlayer player, float deltaTime)
        {
            CompleteSolveIfPending();
            Animator animator = player?.BasisAvatar != null ? player.BasisAvatar.Animator : null;
            BasisLocoParams frameParams = player.LocalAnimatorDriver.GetLocoParams();
            LocomotionPose.Schedule(this, animator, in frameParams, deltaTime);
        }
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
            Step01PrepareFilters(deltaTime);
            Step02FootIK(deltaTime);
            Step03GatherTrackerTargets();
            Step04SmoothTrackerTargets(Mathf.Max(deltaTime, 1e-6f));
            Step05JoinFootSim();
            Step06PushTuning();
            Step07BuildFacts(deltaTime);
            Step08FillTorsoTargets();
            Step09FillFootTargets();
            Step10FillGaitPelvis();
            Step11FillKneeHints();
            Step12FillToeTargets();
            Step13FillKneeBendPreferences();
            Step14FillArmTargets();
            bool basePoseInStream = Step15JoinBasePose();
            Step16ScheduleSolve(deltaTime, basePoseInStream);
            ikPublishPending = true;
        }
        void Step01PrepareFilters(float deltaTime)
        {
            BasisLocalPlayerMarkers.IKDestPrep.Begin();
            EnsureFilterArrays();
            ResolveSmoothingGroups(deltaTime);
            BasisLocalPlayerMarkers.IKDestPrep.End();
        }
        void Step02FootIK(float deltaTime)
        {
            BasisLocalPlayerMarkers.IKDestFootSchedule.Begin();
            BasisLocalFootDriver footDriver = localPlayer.BasisLocalFootDriver;
            ref BasisEerieFrameFacts facts = ref frameFacts;
            facts = default;
            bool fbt = Basis.BasisUI.BasisSettingsDefaults.EnableFBT.RawValue;
            bool leftFoot = fbt && BasisLocalBoneDriver.LeftFootControl.HasTracked == BasisHasTracked.HasTracker, rightFoot = fbt && BasisLocalBoneDriver.RightFootControl.HasTracked == BasisHasTracked.HasTracker;
            facts.deltaTime = deltaTime;
            facts.footSimReady = footDriver.IsInitialized;
            facts.footIKSetting = Basis.BasisUI.BasisSettingsDefaults.FootIKEnabled.RawValue;
            facts.trackerBendNormal = Basis.BasisUI.BasisSettingsDefaults.FBIKTrackerBendNormal.RawValue;
            facts.moving = localPlayer.LocalCharacterDriver.IsLocomoting;
            facts.prone = localPlayer.LocalCharacterDriver.IsProne;
            facts.seated = localPlayer.LocalSeatDriver.IsSeated;
            facts.upright = BasisLocalPlayspaceMover.FlipUpSign > 0f;
            facts.hipsTracked = fbt && BasisLocalBoneDriver.HipsControl.HasTracked == BasisHasTracked.HasTracker;
            facts.chestTracked = HasRigLayer(BasisLocalBoneDriver.ChestControl);
            facts.leftFootTracked = leftFoot;
            facts.rightFootTracked = rightFoot;
            facts.leftLegTracked = leftFoot || (fbt && BasisLocalBoneDriver.LeftUpperLegControl.HasTracked == BasisHasTracked.HasTracker);
            facts.rightLegTracked = rightFoot || (fbt && BasisLocalBoneDriver.RightUpperLegControl.HasTracked == BasisHasTracked.HasTracker);
            facts.leftKneeTracked = fbt && BasisLocalBoneDriver.LeftLowerLegControl.HasTracked == BasisHasTracked.HasTracker;
            facts.rightKneeTracked = fbt && BasisLocalBoneDriver.RightLowerLegControl.HasTracked == BasisHasTracked.HasTracker;
            facts.leftElbowTracked = HasRigLayer(BasisLocalBoneDriver.LeftLowerArmControl);
            facts.rightElbowTracked = HasRigLayer(BasisLocalBoneDriver.RightLowerArmControl);
            facts.leftShoulderTracked = HasRigLayer(BasisLocalBoneDriver.LeftShoulderControl);
            facts.rightShoulderTracked = HasRigLayer(BasisLocalBoneDriver.RightShoulderControl);
            facts.leftToeTracked = HasRigLayer(BasisLocalBoneDriver.LeftToeControl);
            facts.rightToeTracked = HasRigLayer(BasisLocalBoneDriver.RightToeControl);
            facts.leftHandWeight = HandRigWeight(BasisLocalBoneDriver.LeftHandControl);
            facts.rightHandWeight = HandRigWeight(BasisLocalBoneDriver.RightHandControl);
            BasisEeriePlanner.FootIK(ref facts, ref stationaryTimer, ref footIKBlendWeightLeft, ref footIKBlendWeightRight, out frameFootSimScheduled, out frameFootReengage);
            footIKBlendWeight = Mathf.Min(footIKBlendWeightLeft, footIKBlendWeightRight);
            if (frameFootSimScheduled) footDriver.ScheduleSimulate(deltaTime);
            BasisLocalPlayerMarkers.IKDestFootSchedule.End();
        }
        void Step03GatherTrackerTargets()
        {
            BasisLocalPlayerMarkers.IKDestGatherTargets.Begin();
            BasisCalibratedCoords hipsData = BasisLocalBoneDriver.HipsControl.OutGoingData, headData = BasisLocalBoneDriver.HeadControl.OutGoingData, leftFootData = BasisLocalBoneDriver.LeftFootControl.OutGoingData, rightFootData = BasisLocalBoneDriver.RightFootControl.OutGoingData, chestData = BasisLocalBoneDriver.ChestControl.OutGoingData, leftLLData = BasisLocalBoneDriver.LeftLowerLegControl.OutGoingData, rightLLData = BasisLocalBoneDriver.RightLowerLegControl.OutGoingData, leftHandData = BasisLocalBoneDriver.LeftHandControl.OutGoingData, rightHandData = BasisLocalBoneDriver.RightHandControl.OutGoingData, leftLAData = BasisLocalBoneDriver.LeftLowerArmControl.OutGoingData, rightLAData = BasisLocalBoneDriver.RightLowerArmControl.OutGoingData, leftToeData = BasisLocalBoneDriver.LeftToeControl.OutGoingData, rightToeData = BasisLocalBoneDriver.RightToeControl.OutGoingData;
            Quaternion leftShoulderRot = BasisLocalBoneDriver.LeftShoulderControl.OutGoingData.rotation, rightShoulderRot = BasisLocalBoneDriver.RightShoulderControl.OutGoingData.rotation;
            unsafe
            {
                float3* posPtr = (float3*)posInputs.GetUnsafePtr();
                quaternion* rotPtr = (quaternion*)rotInputs.GetUnsafePtr();
                byte* posModePtr = (byte*)posModeNative.GetUnsafePtr();
                byte* rotModePtr = (byte*)rotModeNative.GetUnsafePtr();
                float4* posTunePtr = (float4*)posTuning.GetUnsafePtr();
                float4* rotTunePtr = (float4*)rotTuning.GetUnsafePtr();
                BasisEuroVec3State* euroPosPtr = (BasisEuroVec3State*)euroPosStates.GetUnsafePtr();
                BasisEuroQuatState* euroRotPtr = (BasisEuroQuatState*)euroRotStates.GetUnsafePtr();
                float3* fallbackPosPtr = (float3*)fallbackPosStates.GetUnsafePtr();
                quaternion* fallbackRotPtr = (quaternion*)fallbackRotStates.GetUnsafePtr();
                posPtr[sHips] = hipsData.position;                 rotPtr[sHips] = hipsData.rotation;
                posPtr[sHead] = headData.position;                 rotPtr[sHead] = headData.rotation;
                posPtr[sLeftFoot] = leftFootData.position;         rotPtr[sLeftFoot] = leftFootData.rotation;
                posPtr[sRightFoot] = rightFootData.position;       rotPtr[sRightFoot] = rightFootData.rotation;
                posPtr[sChest] = chestData.position;               rotPtr[sChest] = chestData.rotation;
                posPtr[sLeftLowerLeg] = leftLLData.position;       rotPtr[sLeftLowerLeg] = leftLLData.rotation;
                posPtr[sRightLowerLeg] = rightLLData.position;     rotPtr[sRightLowerLeg] = rightLLData.rotation;
                posPtr[sLeftHand] = leftHandData.position;         rotPtr[sLeftHand] = leftHandData.rotation;
                posPtr[sRightHand] = rightHandData.position;       rotPtr[sRightHand] = rightHandData.rotation;
                posPtr[sLeftLowerArm] = leftLAData.position;       rotPtr[sLeftLowerArm] = leftLAData.rotation;
                posPtr[sRightLowerArm] = rightLAData.position;     rotPtr[sRightLowerArm] = rightLAData.rotation;
                posPtr[sLeftToe] = leftToeData.position;           rotPtr[sLeftToe] = leftToeData.rotation;
                posPtr[sRightToe] = rightToeData.position;         rotPtr[sRightToe] = rightToeData.rotation;
                posPtr[sLeftShoulder] = float3.zero;                rotPtr[sLeftShoulder] = leftShoulderRot;
                posPtr[sRightShoulder] = float3.zero;               rotPtr[sRightShoulder] = rightShoulderRot;
                for (int i = 0; i < SlotCount; i++)
                {
                    byte group = BasisSmoothingProfiles.SlotGroup[i];
                    posTunePtr[i] = groupPosTuning[group];
                    rotTunePtr[i] = groupRotTuning[group];
                    byte newPosMode = (byte)BasisFilterMode.Passthrough;
                    byte newRotMode = (byte)BasisFilterMode.Passthrough;
                    if (!groupOff[group])
                    {
                        newPosMode = PickMode(SmoothPos[i], EuroPos[i]);
                        newRotMode = PickMode(SmoothRot[i], EuroRot[i]);
                    }
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
                posModePtr[sLeftShoulder] = (byte)BasisFilterMode.Passthrough;
                posModePtr[sRightShoulder] = (byte)BasisFilterMode.Passthrough;
            }
            if (!hasFallbackState)
            {
                hasFallbackState = true;
                fallbackPosStates.CopyFrom(posInputs);
                fallbackRotStates.CopyFrom(rotInputs);
            }
            BasisLocalPlayerMarkers.IKDestGatherTargets.End();
        }
        void Step04SmoothTrackerTargets(float safeDt)
        {
            BasisLocalPlayerMarkers.IKDestFilters.Begin();
            Matrix4x4 playspaceMatrix = BasisLocalPlayer.localToWorldMatrix;
            var posJob = new BasisBatchPositionFilterJob
            {
                mode = posModeNative, rawInputs = posInputs, tuning = posTuning, euroStates = euroPosStates, fallbackStates = fallbackPosStates, outputs = posOutputs, dt = safeDt, playspaceToWorld = playspaceMatrix,
            };
            var rotJob = new BasisBatchRotationFilterJob
            {
                mode = rotModeNative, rawInputs = rotInputs, tuning = rotTuning, euroStates = euroRotStates, fallbackStates = fallbackRotStates, outputs = rotOutputs, dt = safeDt, playspaceRotation = playspaceMatrix.rotation,
            };
            posJob.Run(SlotCount);
            rotJob.Run(SlotCount);
            BasisLocalPlayerMarkers.IKDestFilters.End();
            WatchdogCheckFilterSlots("IKDest/PostFilters");
        }
        void Step05JoinFootSim()
        {
            BasisLocalPlayerMarkers.IKDestFootJoin.Begin();
            BasisLocalFootDriver footDriver = localPlayer.BasisLocalFootDriver;
            if (frameFootSimScheduled) footDriver.CompleteSimulate();
            if (frameFootReengage) footDriver.NotifyReEngaging();
            if (frameFootSimScheduled) footDriver.ScheduleSurfaceProbes();
            BasisLocalPlayerMarkers.IKDestFootJoin.End();
        }
        void Step06PushTuning() => ApplyTuningSettings(ref IKJob);
        void Step07BuildFacts(float deltaTime)
        {
            BasisLocalPlayerMarkers.IKDestBuildIKTargets.Begin();
            ref BasisEerieMovement data = ref IKJob;
            ref BasisEerieFrameFacts facts = ref frameFacts;
            BasisLocalFootDriver footDriver = localPlayer.BasisLocalFootDriver;
            Vector3 playerUpScaled = BasisLocalPlayer.localToWorldMatrix.MultiplyVector(Vector3.up);
            framePlayerUpScale = playerUpScaled.magnitude;
            framePlayerUpDirection = framePlayerUpScale > 1e-6f ? playerUpScaled / framePlayerUpScale : Vector3.up;
            if (trackersDisabled)
            {
                facts.hipsTracked = facts.chestTracked = facts.leftLegTracked = facts.rightLegTracked = facts.leftFootTracked = facts.rightFootTracked = facts.leftKneeTracked = facts.rightKneeTracked = false;
                facts.leftElbowTracked = facts.rightElbowTracked = facts.leftShoulderTracked = facts.rightShoulderTracked = facts.leftToeTracked = facts.rightToeTracked = false;
            }
            facts.leftSimFootRotation = BasisEeriePlanner.footRotationFromSim && facts.footSimReady && TryFootTargetRotation(footDriver.LeftFootRotation, data.offsetRotationLeftFoot, out frameLeftSimFootRotation);
            facts.rightSimFootRotation = BasisEeriePlanner.footRotationFromSim && facts.footSimReady && TryFootTargetRotation(footDriver.RightFootRotation, data.offsetRotationRightFoot, out frameRightSimFootRotation);
            facts.leftToeBend = facts.footSimReady && Mathf.Abs(footDriver.LeftToeBendDegrees * footIKBlendWeightLeft) >= 0.01f && footDriver.LeftToeBendAxis.sqrMagnitude >= 1e-6f;
            facts.rightToeBend = facts.footSimReady && Mathf.Abs(footDriver.RightToeBendDegrees * footIKBlendWeightRight) >= 0.01f && footDriver.RightToeBendAxis.sqrMagnitude >= 1e-6f;
            facts.leftElbowRoll = BasisLimbRollStore.TryGet(BasisBoneTrackedRole.LeftLowerArm, out frameLeftElbowToBone);
            facts.rightElbowRoll = BasisLimbRollStore.TryGet(BasisBoneTrackedRole.RightLowerArm, out frameRightElbowToBone);
            facts.leftKneeRoll = BasisLimbRollStore.TryGet(BasisBoneTrackedRole.LeftLowerLeg, out frameLeftKneeToBone);
            facts.rightKneeRoll = BasisLimbRollStore.TryGet(BasisBoneTrackedRole.RightLowerLeg, out frameRightKneeToBone);
            facts.leftKneeBendNormal = BasisBendNormalStore.TryGet(BasisBoneTrackedRole.LeftLowerLeg, out frameLeftKneeBendAxis);
            facts.rightKneeBendNormal = BasisBendNormalStore.TryGet(BasisBoneTrackedRole.RightLowerLeg, out frameRightKneeBendAxis);
            unsafe
            {
                float3* pOut = (float3*)posOutputs.GetUnsafeReadOnlyPtr();
                quaternion* rOut = (quaternion*)rotOutputs.GetUnsafeReadOnlyPtr();
                frameHipsRotation = rOut[sHips];
                facts.leftKneeAssist = BasisEeriePlanner.KneeAssistWanted(in facts, true) ? ComputeKneeAssist(true, pOut[sHips], pOut[sLeftFoot], rOut[sLeftFoot], deltaTime, out frameLeftKneeAssistHint) : 0f;
                facts.rightKneeAssist = BasisEeriePlanner.KneeAssistWanted(in facts, false) ? ComputeKneeAssist(false, pOut[sHips], pOut[sRightFoot], rOut[sRightFoot], deltaTime, out frameRightKneeAssistHint) : 0f;
            }
            BasisEeriePlanner.Frame(ref data, in facts);
            BasisLocalPlayerMarkers.IKDestBuildIKTargets.End();
        }
        void LegPose(bool isLeft, Vector3 hipsPos, Quaternion hipsRot, out bool valid, out Vector3 hipPos, out float upperLength, out float lowerLength)
        {
            BasisLocalBoneControl upper = isLeft ? BasisLocalBoneDriver.LeftUpperLegControl : BasisLocalBoneDriver.RightUpperLegControl, lower = isLeft ? BasisLocalBoneDriver.LeftLowerLegControl : BasisLocalBoneDriver.RightLowerLegControl, foot = isLeft ? BasisLocalBoneDriver.LeftFootControl : BasisLocalBoneDriver.RightFootControl;
            Transform hipsBone = basisTransformMapping != null ? basisTransformMapping.Hips : null;
            valid = hipsBone != null && upper != null && lower != null && foot != null && upper.HasIKWorldData && lower.HasIKWorldData && foot.HasIKWorldData;
            if (!valid)
            {
                hipPos = Vector3.zero;
                upperLength = lowerLength = 0f;
                return;
            }
            Vector3 hipJoint = upper.IKWorldData.position, knee = lower.IKWorldData.position;
            upperLength = Vector3.Distance(hipJoint, knee);
            lowerLength = Vector3.Distance(knee, foot.IKWorldData.position);
            hipPos = hipsPos + hipsRot * IKJob.offsetRotationHips * Vector3.Scale(isLeft ? leftHipJointRestLocal : rightHipJointRestLocal, hipsBone.lossyScale);
        }
        float ComputeKneeAssist(bool isLeft, Vector3 hipsPos, Vector3 footPos, Quaternion footRot, float deltaTime, out Vector3 hint)
        {
            bool butterflyEnabled = Basis.BasisUI.BasisSettingsDefaults.FBIKButterflyKnees.RawValue, kneeFollowsFoot = Basis.BasisUI.BasisSettingsDefaults.FBIKKneeFollowsFoot.RawValue;
            float butterflyMaxOpenDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKButterflyKneeMaxOpenDeg.RawValue, butterflySupineFloor = 1f, kneeFootCoupling = Basis.BasisUI.BasisSettingsDefaults.FBIKKneeFootFollowUpright.RawValue, kneeFwdWeight = 0f;
            Quaternion hipsRot = frameHipsRotation;
            Vector3 playerUpDir = framePlayerUpDirection, bendDir = hipsRot * Vector3.forward, kneeFwdHint = default;
            LegPose(isLeft, hipsPos, hipsRot, out bool haveLeg, out Vector3 hipPos, out float upperLength, out float lowerLength);
            ref Vector3 smoothedKneeFwdHint = ref (isLeft ? ref smoothedLeftKneeFwdHint : ref smoothedRightKneeFwdHint);
            ref float smoothedKneeFwdWeight = ref (isLeft ? ref smoothedLeftKneeFwdWeight : ref smoothedRightKneeFwdWeight);
            ref Vector3 smoothedButterflyHint = ref (isLeft ? ref smoothedLeftButterflyHint : ref smoothedRightButterflyHint);
            ref float smoothedButterflyWeight = ref (isLeft ? ref smoothedLeftButterflyWeight : ref smoothedRightButterflyWeight);
            bool haveKneeFwd = kneeFollowsFoot && TryComputeKneeForward(hipsRot, kneeFootCoupling, KneeForwardSmoothRate, playerUpDir, deltaTime, haveLeg, hipPos, upperLength, footPos, footRot, ref smoothedKneeFwdHint, ref smoothedKneeFwdWeight, out kneeFwdHint, out kneeFwdWeight, out bendDir);
            if (butterflyEnabled && TryComputeButterflyKnee(isLeft, hipsRot, playerUpDir, butterflyMaxOpenDeg, butterflySupineFloor, deltaTime, bendDir, haveLeg, hipPos, upperLength, lowerLength, footPos, footRot, ref smoothedButterflyHint, ref smoothedButterflyWeight, out Vector3 butterflyHint, out float butterflyWeight))
            {
                hint = butterflyHint;
                return butterflyWeight;
            }
            if (haveKneeFwd && kneeFwdWeight > 0.001f)
            {
                hint = kneeFwdHint;
                return kneeFwdWeight;
            }
            hint = default;
            return 0f;
        }
        void Step08FillTorsoTargets()
        {
            BasisLocalPlayerMarkers.IKDestBuildIKTargets.Begin();
            ref BasisEerieMovement data = ref IKJob;
            unsafe
            {
                float3* pOut = (float3*)posOutputs.GetUnsafeReadOnlyPtr();
                quaternion* rOut = (quaternion*)rotOutputs.GetUnsafeReadOnlyPtr();
                Vector3 hipsPos = pOut[sHips];
                hipsPos -= framePlayerUpDirection * localPlayer.LocalCharacterDriver.landingCrouchEffect;
                data.targetPositionHips = hipsPos;
                data.targetRotationHips = frameHipsRotation;
                data.targetPositionHead = pOut[sHead];
                data.targetRotationHead = rOut[sHead];
                float restHeadLocalY = BasisLocalBoneDriver.HeadControl.TposeLocalScaled.position.y;
                data.standingHeadHeight = Mathf.Max(0f, restHeadLocalY * framePlayerUpScale);
                if (framePlayerUpScale <= 1e-6f)
                {
                    data.crouchDepth = 0f;
                }
                else
                {
                    float headLocalY = BasisLocalPlayer.localToWorldMatrix.inverse.MultiplyPoint3x4((Vector3)pOut[sHead]).y;
                    data.crouchDepth = Mathf.Max(0f, (restHeadLocalY - headLocalY) * framePlayerUpScale);
                }
                Vector3 chestPos = pOut[sChest];
                Quaternion chestRot = rOut[sChest];
                data.targetPositionChestRaw = chestPos;
                data.targetPositionChest = ApplyHintBias(BasisBoneTrackedRole.Chest, chestPos, chestRot);
                data.targetRotationChest = chestRot;
            }
            BasisLocalPlayerMarkers.IKDestBuildIKTargets.End();
        }
        void Step09FillFootTargets()
        {
            BasisLocalPlayerMarkers.IKDestBuildIKTargets.Begin();
            ref BasisEerieMovement data = ref IKJob;
            BasisLocalFootDriver footDriver = localPlayer.BasisLocalFootDriver;
            unsafe
            {
                float3* pOut = (float3*)posOutputs.GetUnsafeReadOnlyPtr();
                quaternion* rOut = (quaternion*)rotOutputs.GetUnsafeReadOnlyPtr();
                switch (data.plan.leftLeg.target)
                {
                    case BasisEerieSource.Tracker:
                        data.targetPositionLeftLowerLeg = pOut[sLeftFoot];
                        data.targetRotationLeftLowerLeg = rOut[sLeftFoot];
                        break;
                    case BasisEerieSource.Sim:
                        data.targetPositionLeftLowerLeg = footDriver.LeftFootPosition;
                        if (!data.plan.leftLeg.preserveTip) data.targetRotationLeftLowerLeg = frameLeftSimFootRotation;
                        break;
                }
                switch (data.plan.rightLeg.target)
                {
                    case BasisEerieSource.Tracker:
                        data.targetPositionRightLowerLeg = pOut[sRightFoot];
                        data.targetRotationRightLowerLeg = rOut[sRightFoot];
                        break;
                    case BasisEerieSource.Sim:
                        data.targetPositionRightLowerLeg = footDriver.RightFootPosition;
                        if (!data.plan.rightLeg.preserveTip) data.targetRotationRightLowerLeg = frameRightSimFootRotation;
                        break;
                }
                if (BasisFootRotationDebug.Enabled)
                {
                    if (basisTransformMapping.leftFoot != null) BasisFootRotationDebug.Record("L", Time.time, footIKBlendWeightLeft, data.plan.leftLeg.target == BasisEerieSource.Sim, basisTransformMapping.leftFoot.rotation, data.targetRotationLeftLowerLeg, data.offsetRotationLeftFoot, BasisLocalBoneDriver.LeftFootControl.OutGoingData.rotation, BasisLocalBoneDriver.LeftFootControl.OutgoingWorldData.rotation, (Quaternion)rOut[sLeftFoot], frameFacts.footSimReady ? footDriver.LeftFootRotation : Quaternion.identity);
                    if (basisTransformMapping.rightFoot != null) BasisFootRotationDebug.Record("R", Time.time, footIKBlendWeightRight, data.plan.rightLeg.target == BasisEerieSource.Sim, basisTransformMapping.rightFoot.rotation, data.targetRotationRightLowerLeg, data.offsetRotationRightFoot, BasisLocalBoneDriver.RightFootControl.OutGoingData.rotation, BasisLocalBoneDriver.RightFootControl.OutgoingWorldData.rotation, (Quaternion)rOut[sRightFoot], frameFacts.footSimReady ? footDriver.RightFootRotation : Quaternion.identity);
                }
            }
            BasisLocalPlayerMarkers.IKDestBuildIKTargets.End();
        }
        void Step10FillGaitPelvis()
        {
            if (!IKJob.plan.gaitPelvis)
            {
                return;
            }
            BasisLocalPlayerMarkers.IKDestBuildIKTargets.Begin();
            ref BasisEerieMovement data = ref IKJob;
            BasisLocalFootDriver footDriver = localPlayer.BasisLocalFootDriver;
            data.targetPositionHips += framePlayerUpDirection * (footDriver.ComputeHipBob() * footIKBlendWeight);
            data.targetPositionHips += footDriver.ComputeHipSway() * footIKBlendWeight;
            Quaternion pelvis = Quaternion.Slerp(Quaternion.identity, footDriver.ComputePelvisDelta(), footIKBlendWeight);
            data.targetRotationHips = pelvis * data.targetRotationHips;
            BasisLocalPlayerMarkers.IKDestBuildIKTargets.End();
        }
        void Step11FillKneeHints()
        {
            BasisLocalPlayerMarkers.IKDestBuildIKTargets.Begin();
            ref BasisEerieMovement data = ref IKJob;
            BasisLocalFootDriver footDriver = localPlayer.BasisLocalFootDriver;
            unsafe
            {
                float3* pOut = (float3*)posOutputs.GetUnsafeReadOnlyPtr();
                quaternion* rOut = (quaternion*)rotOutputs.GetUnsafeReadOnlyPtr();
                switch (data.plan.leftLeg.hint)
                {
                    case BasisEerieSource.Tracker: data.hintPositionLeftLowerLeg = ApplyHintBias(BasisBoneTrackedRole.LeftLowerLeg, pOut[sLeftLowerLeg], rOut[sLeftLowerLeg]); break;
                    case BasisEerieSource.Sim: data.hintPositionLeftLowerLeg = footDriver.LeftKneeHint; break;
                    case BasisEerieSource.Assist: data.hintPositionLeftLowerLeg = frameLeftKneeAssistHint; break;
                }
                switch (data.plan.rightLeg.hint)
                {
                    case BasisEerieSource.Tracker: data.hintPositionRightLowerLeg = ApplyHintBias(BasisBoneTrackedRole.RightLowerLeg, pOut[sRightLowerLeg], rOut[sRightLowerLeg]); break;
                    case BasisEerieSource.Sim: data.hintPositionRightLowerLeg = footDriver.RightKneeHint; break;
                    case BasisEerieSource.Assist: data.hintPositionRightLowerLeg = frameRightKneeAssistHint; break;
                }
                if (BasisLegCrouchDebug.Enabled)
                {
                    if (BasisLocalBoneDriver.LeftUpperLegControl.HasIKWorldData && BasisLocalBoneDriver.LeftLowerLegControl.HasIKWorldData && BasisLocalBoneDriver.LeftFootControl.HasIKWorldData)
                    {
                        Vector3 hipL = BasisLocalBoneDriver.LeftUpperLegControl.IKWorldData.position, kneeL = BasisLocalBoneDriver.LeftLowerLegControl.IKWorldData.position;
                        float legLenL = Vector3.Distance(hipL, kneeL) + Vector3.Distance(kneeL, BasisLocalBoneDriver.LeftFootControl.IKWorldData.position);
                        BasisLegCrouchDebug.Record("L", Time.time, data.plan.leftLeg.target == BasisEerieSource.Sim, legLenL, hipL, data.targetPositionLeftLowerLeg, data.hintPositionLeftLowerLeg, kneeL);
                    }
                    if (BasisLocalBoneDriver.RightUpperLegControl.HasIKWorldData && BasisLocalBoneDriver.RightLowerLegControl.HasIKWorldData && BasisLocalBoneDriver.RightFootControl.HasIKWorldData)
                    {
                        Vector3 hipR = BasisLocalBoneDriver.RightUpperLegControl.IKWorldData.position, kneeR = BasisLocalBoneDriver.RightLowerLegControl.IKWorldData.position;
                        float legLenR = Vector3.Distance(hipR, kneeR) + Vector3.Distance(kneeR, BasisLocalBoneDriver.RightFootControl.IKWorldData.position);
                        BasisLegCrouchDebug.Record("R", Time.time, data.plan.rightLeg.target == BasisEerieSource.Sim, legLenR, hipR, data.targetPositionRightLowerLeg, data.hintPositionRightLowerLeg, kneeR);
                    }
                }
            }
            BasisLocalPlayerMarkers.IKDestBuildIKTargets.End();
        }
        void Step12FillToeTargets()
        {
            BasisLocalPlayerMarkers.IKDestBuildIKTargets.Begin();
            ref BasisEerieMovement data = ref IKJob;
            BasisLocalFootDriver footDriver = localPlayer.BasisLocalFootDriver;
            unsafe
            {
                quaternion* rOut = (quaternion*)rotOutputs.GetUnsafeReadOnlyPtr();

                data.leftDrivenTargetRot = rOut[sLeftToe];
                data.rightDrivenTargetRot = rOut[sRightToe];
            }
            if (data.plan.leftLeg.target == BasisEerieSource.Sim)
            {
                data.leftToeBendDeg = footDriver.LeftToeBendDegrees * footIKBlendWeightLeft;
                data.leftToeBendAxis = footDriver.LeftToeBendAxis;
            }
            else
            {
                data.leftToeBendDeg = 0f;
                data.leftToeBendAxis = Vector3.zero;
            }
            if (data.plan.rightLeg.target == BasisEerieSource.Sim)
            {
                data.rightToeBendDeg = footDriver.RightToeBendDegrees * footIKBlendWeightRight;
                data.rightToeBendAxis = footDriver.RightToeBendAxis;
            }
            else
            {
                data.rightToeBendDeg = 0f;
                data.rightToeBendAxis = Vector3.zero;
            }
            BasisLocalPlayerMarkers.IKDestBuildIKTargets.End();
        }
        void Step13FillKneeBendPreferences()
        {
            BasisLocalPlayerMarkers.IKDestBuildIKTargets.Begin();
            ref BasisEerieMovement data = ref IKJob;
            Quaternion leftLowerLeg = BasisLocalBoneDriver.LeftLowerLegControl.OutgoingWorldData.rotation, rightLowerLeg = BasisLocalBoneDriver.RightLowerLegControl.OutgoingWorldData.rotation;
            data.hintRotationLeftLowerLeg = frameFacts.leftKneeRoll ? leftLowerLeg * frameLeftKneeToBone : Quaternion.identity;
            data.hintRotationRightLowerLeg = frameFacts.rightKneeRoll ? rightLowerLeg * frameRightKneeToBone : Quaternion.identity;
            Vector3 hipsRight = frameHipsRotation * Vector3.right;
            data.kneeAnteriorRef = hipsRight;
            data.kneeBendPrefLeft = data.plan.leftLeg.trackerBendNormal ? BasisTrackerBendNormalCore.ResolveWorldNormal(leftLowerLeg, frameLeftKneeBendAxis, hipsRight) : hipsRight;
            data.kneeBendPrefRight = data.plan.rightLeg.trackerBendNormal ? BasisTrackerBendNormalCore.ResolveWorldNormal(rightLowerLeg, frameRightKneeBendAxis, hipsRight) : hipsRight;
            BasisLocalPlayerMarkers.IKDestBuildIKTargets.End();
        }
        void Step14FillArmTargets()
        {
            BasisLocalPlayerMarkers.IKDestBuildIKTargets.Begin();
            ref BasisEerieMovement data = ref IKJob;
            Vector3 llaPos, rlaPos;
            Quaternion llaRot, rlaRot;
            unsafe
            {
                float3* pOut = (float3*)posOutputs.GetUnsafeReadOnlyPtr();
                quaternion* rOut = (quaternion*)rotOutputs.GetUnsafeReadOnlyPtr();

                data.targetPositionLeftHand = pOut[sLeftHand];
                data.targetRotationLeftHand = rOut[sLeftHand];
                data.targetPositionRightHand = pOut[sRightHand];
                data.targetRotationRightHand = rOut[sRightHand];
                llaPos = pOut[sLeftLowerArm];
                llaRot = rOut[sLeftLowerArm];
                data.hintPositionLeftHand = llaPos;
                data.hintRotationLeftHand = frameFacts.leftElbowRoll ? llaRot * frameLeftElbowToBone : Quaternion.identity;
                rlaPos = pOut[sRightLowerArm];
                rlaRot = rOut[sRightLowerArm];
                data.hintPositionRightHand = rlaPos;
                data.hintRotationRightHand = frameFacts.rightElbowRoll ? rlaRot * frameRightElbowToBone : Quaternion.identity;

                data.targetRotationLeftShoulder = rOut[sLeftShoulder];
                data.targetRotationRightShoulder = rOut[sRightShoulder];
            }
            BasisLocalPlayerMarkers.IKDestBuildIKTargets.End();
        }
        bool Step15JoinBasePose()
        {
            BasisLocalPlayerMarkers.IKDestLocoPoseJoin.Begin();
            bool streamPrefilled = LocomotionPose.TryComplete(PoseSkeleton);
            BasisLocalPlayerMarkers.IKDestLocoPoseJoin.End();
            return streamPrefilled;
        }
        void Step16ScheduleSolve(float deltaTime, bool streamPrefilled)
        {
            if (!RigLayerActive || !IKJobCreated || !PoseSkeleton.IsCreated)
            {
                return;
            }
            if (!streamPrefilled)
            {
                BasisLocalPlayerMarkers.IKDestPoseGather.Begin();
                PoseSkeleton.GatherNow();
                BasisLocalPlayerMarkers.IKDestPoseGather.End();
            }
            WatchdogCheckPoseStream(streamPrefilled ? "IKDest/PreFit (stream prefilled by locomotion pose)" : "IKDest/PreFit (stream gathered from bones)");
            BasisLocalPlayerMarkers.IKDestApplyFit.Begin();
            PoseSkeleton.ApplyFit();
            BasisLocalPlayerMarkers.IKDestApplyFit.End();
            WatchdogCheckPoseStream("IKDest/PostApplyFit (body-fit rest positions)");
            BasisLocalPlayerMarkers.IKDestSolve.Begin();
            IKJob.poseStream = PoseSkeleton.Stream;
            IKJob.poseStream.deltaTime = deltaTime;
            BasisIKSolveGizmos.Prepare(ref IKJob);
            ikSolveHandle = IKJob.Schedule();
            ikSolveScheduled = true;
            ikScatterPending = true;
            JobHandle.ScheduleBatchedJobs();
            BasisLocalPlayerMarkers.IKDestSolve.End();
        }
        public void CompleteIKSolve()
        {
            bool solveRan = Step17JoinSolve();
            Step18DrainSolveGizmos(solveRan);
            Step19ScatterPose();
            if (!ikPublishPending)
            {
                return;
            }
            ikPublishPending = false;
            Step20PublishWorldData();
            Step21SampleRecorders();
        }
        bool Step17JoinSolve()
        {
            bool solveRan = ikSolveScheduled;
            if (ikSolveScheduled)
            {
                BasisLocalPlayerMarkers.IKDestSolveJoin.Begin();
                ikSolveHandle.Complete();
                ikSolveScheduled = false;
                BasisLocalPlayerMarkers.IKDestSolveJoin.End();
            }
            return solveRan;
        }
        void Step18DrainSolveGizmos(bool solveRan)
        {
            BasisLocalPlayerMarkers.IKDestSolveGizmos.Begin();
            if (solveRan)
            {
                BasisIKSolveGizmos.Drain(ref IKJob, BasisLocalCameraDriver.Position);
            }
            else
            {
                BasisIKSolveGizmos.Hide();
            }
            BasisLocalPlayerMarkers.IKDestSolveGizmos.End();
        }
        void Step19ScatterPose()
        {
            if (!ikScatterPending)
            {
                return;
            }
            ikScatterPending = false;

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
            WatchdogCheckPoseStream("IKDest/PostSolve (stream, pre-scatter)");
            BasisLocalPlayerMarkers.IKDestPoseScatter.Begin();
            PoseSkeleton.ScatterNow();
            BasisLocalPlayerMarkers.IKDestPoseScatter.End();
            BasisFiniteWatchdog.Checkpoint("IKDest/PostPoseScatter (FBIK solve output)");
        }
        void Step20PublishWorldData()
        {
            BasisLocalPlayerMarkers.IKDestPublishWorldData.Begin();
            PublishIKWorldData();
            BasisLocalPlayerMarkers.IKDestPublishWorldData.End();
        }
        void Step21SampleRecorders()
        {
            ref BasisEerieMovement data = ref IKJob;
            if (BasisCalibrationDebugRecorder.RuntimeActive)
            {
                BasisCalibrationDebugRecorder.RuntimeBone("head", BasisLocalBoneDriver.HeadControl.OutgoingWorldData.rotation, data.offsetRotationHead, BasisLocalAvatarDriver.Mapping.head);
                BasisCalibrationDebugRecorder.RuntimeBone("hips", BasisLocalBoneDriver.HipsControl.OutgoingWorldData.rotation, data.offsetRotationHips, BasisLocalAvatarDriver.Mapping.Hips);
                BasisCalibrationDebugRecorder.RuntimeBone("leftFoot", BasisLocalBoneDriver.LeftFootControl.OutgoingWorldData.rotation, data.offsetRotationLeftFoot, BasisLocalAvatarDriver.Mapping.leftFoot);
                BasisCalibrationDebugRecorder.RuntimeBone("rightFoot", BasisLocalBoneDriver.RightFootControl.OutgoingWorldData.rotation, data.offsetRotationRightFoot, BasisLocalAvatarDriver.Mapping.rightFoot);
                Transform animRoot = localPlayer?.BasisAvatar?.Animator != null ? localPlayer.BasisAvatar.Animator.transform : null;
                BasisCalibrationDebugRecorder.RuntimeEndFrame(localPlayer != null ? localPlayer.transform : null, animRoot);
            }
            if (BasisArmIKRuntimeRecorder.Active)
            {
                var armMap = BasisLocalAvatarDriver.Mapping;
                BasisArmIKRuntimeRecorder.Sample( armMap.leftUpperArm, armMap.leftLowerArm, armMap.leftHand, armMap.RightUpperArm, armMap.RightLowerArm, armMap.rightHand, data.targetPositionLeftHand, data.targetPositionRightHand, data.hintPositionLeftHand, data.hintPositionRightHand, data.plan.leftArm.trackerHint, data.plan.rightArm.trackerHint);
            }
        }
        public void CompleteSolveIfPending()
        {
            if (ikSolveScheduled)
            {
                ikSolveHandle.Complete();
                ikSolveScheduled = false;
            }
        }
        float BendVsAnteriorDeg(Vector3 bendNormal)
        {
            Vector3 anterior = IKJob.kneeAnteriorRef;
            if (bendNormal.sqrMagnitude < 1e-8f || anterior.sqrMagnitude < 1e-8f)
            {
                return 0f;
            }
            return Vector3.Angle(bendNormal, anterior);
        }
    }
}
