using Unity.Scripting.LifecycleManagement;
using Basis.Scripts.Avatar;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Player;
using Basis.Scripts.TransformBinders.BoneControl;
using GatorDragonGames.JigglePhysics;
using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.AddressableAssets;
namespace Basis.Scripts.Drivers
{
    [AutoStaticsCleanup]
    [Serializable]
    public partial class BasisLocalAvatarDriver : BasisAvatarDriver
    {

        public const string Locomotion = "Locomotion";

        public static readonly Dictionary<BasisBoneTrackedRole, BasisCalibratedCoords> TposeBoneSnapshot = new Dictionary<BasisBoneTrackedRole, BasisCalibratedCoords>();
        public static bool HasTposeBoneSnapshot;

        public static Vector3 HeadScale = Vector3.one;

        public static Vector3 HeadScaledDown = Vector3.zero;

        public static HeadChopEntry[] HeadChopEntries = Array.Empty<HeadChopEntry>();

        public static int HeadChopEntriesLength;

        public struct HeadChopEntry
        {
            public Transform Target;
            public Vector3 NormalScale;
            public Vector3 HiddenScale;
        }

        public static BasisLocalAvatarDriver Instance;

        public static bool IsNormalHead;

        public static bool CurrentlyTposing = false;

        public static Action CalibrationComplete;

        public static Action TposeStateChange;

        public static BasisTransformMapping Mapping = new BasisTransformMapping();

        public static RuntimeAnimatorController SavedruntimeAnimatorController;

        public static SkinnedMeshRenderer[] SkinnedMeshRenderer;

        public static bool HasEvents = false;

        public static int SkinnedMeshRendererLength;

        public static JiggleRig[] JiggleRigs = Array.Empty<JiggleRig>();

        [System.NonSerialized] public Dictionary<BasisBoneTrackedRole, Transform> StoredRolesTransforms = new Dictionary<BasisBoneTrackedRole, Transform>();

        [SerializeField]
        public BasisAvatarScaleModifier ScaleAvatarModification = new BasisAvatarScaleModifier();

        public void InitialLocalCalibration(BasisLocalPlayer player, List<BasisHeadChop.HeadChopTarget> harvestedHeadChop)
        {
            Instance = this;
            BasisDebug.Log("InitialLocalCalibration");
            BasisCalibrationDebugRecorder.Begin(SafeAvatarLabel(player));
            RecordCalibrationMeta(player);
            RecordCalibrationStage("Spawn", player);
            TposeStateChange -= player.LocalRigDriver.OnTPose;
            TposeStateChange += player.LocalRigDriver.OnTPose;
            if (IsAble())
            {
                // BasisDebug.Log("LocalCalibration Underway");
            }
            else
            {
                BasisDebug.LogError("Unable to Calibrate Local Avatar Missing Core Requirement (Animator,LocalPlayer Or Driver)");
                return;
            }

            player.LocalRigDriver.Initialize(player, Mapping);

            BasisAvatarIKStageCalibration.BasisBendNormalStore.Clear();
            BasisAvatarIKStageCalibration.BasisLimbRollStore.Clear();

            player.LocalRigDriver.CleanupBeforeContinue();
            GameObject AvatarAnimatorParent = player.BasisAvatar.Animator.gameObject;
            ScaleAvatarModification.ReInitialize(player.BasisAvatar.Animator, player.AvatarTransform);

            player.BasisAvatar.Animator.updateMode = AnimatorUpdateMode.Normal;
            player.BasisAvatar.Animator.logWarnings = false;

            if (player.BasisAvatar.Animator.runtimeAnimatorController == null)
            {
                UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle<RuntimeAnimatorController> op = Addressables.LoadAssetAsync<RuntimeAnimatorController>(Locomotion);
                RuntimeAnimatorController RAC = op.WaitForCompletion();
                player.BasisAvatar.Animator.runtimeAnimatorController = RAC;
                BasisLocomotionPoseSystem.NotifyStockControllerAssigned(RAC);
            }
            player.BasisAvatar.Animator.applyRootMotion = false;
            player.BasisAvatar.HumanScale = player.BasisAvatar.Animator.humanScale;

            // The previous avatar's raw-joint T-pose snapshot is stale for this avatar. Clear it BEFORE
            // the T-pose height capture below, or CalculateAvatarArmSpan serves the OLD avatar's arm
            // span (its live-bone fallback never fires while a snapshot exists) — which made every
            // avatar swap mis-scale in ArmSpan/Auto mode until the user recalibrated. The fresh
            // snapshot is captured further down, before SetBodySettings consumes it.
            TposeBoneSnapshot.Clear();
            HasTposeBoneSnapshot = false;

            // Enter T-Pose for calibration
            PutAvatarIntoTPose();

            // Initialize any physics/jiggle rigs before building the rig. JiggleRigs is filtered
            // out of the content-harvest snapshot by BasisAvatarFactory at load — no walk here.
            // The set is include-inactive, so gate on activity the way the old scan did.
            int length = JiggleRigs.Length;
            for (int Index = 0; Index < length; Index++)
            {
                JiggleRig Rig = JiggleRigs[Index];
                if (Rig == null || !Rig.gameObject.activeInHierarchy)
                {
                    continue;
                }
                Rig.HasAnimatedParameters = false;
                Rig.OnInitialize();
            }

            // Register authored motion (drives non-humanoid transforms IK doesn't touch); rest captured at the current TPose.
            var authoredMotions = player.BasisAvatar.AuthoredMotions;
            if (authoredMotions != null)
            {
                for (int i = 0; i < authoredMotions.Length; i++)
                {
                    BasisAuthoredMotionSystem.Register(authoredMotions[i]);
                }
            }


            Calibration(player);

            RecordCalibrationStage("TPose", player);

            // Capture T-pose bone rotations for network compression (while still in T-pose)
            Networking.NetworkedAvatar.BasisNetworkAvatarCompressor.CaptureTPose();

            player.LocalBoneDriver.RemoveAllListeners();
            BasisLocalEyeDriverData.Liveliness = player.BasisAvatar.EyeLiveliness;
            BasisLocalEyeDriverData.Attentiveness = player.BasisAvatar.EyeAttentiveness;
            BasisLocalEyeDriverData.SetMaxLookAngle(player.BasisAvatar.EyeMaxLookAngleEnabled, player.BasisAvatar.EyeMaxLookAngle);
            BasisLocalEyeDriverData.PersonalityDirty = true;
            BasisDebug.Log($"Eye Personality - Liveliness: {BasisLocalEyeDriverData.Liveliness:F1} | Attentiveness: {BasisLocalEyeDriverData.Attentiveness:F1} | Max Look Angle: {(BasisLocalEyeDriverData.MaxLookAngleEnabled ? $"{BasisLocalEyeDriverData.MaxLookAngleDeg:F0} deg" : "default")}", BasisDebug.LogTag.Avatar);
            BasisLocalEyeDriver.Initialize();
            LocalRenderMeshSettings(
                BasisLayerMapper.LocalAvatarLayer,
                SkinnedMeshRendererLength,
                SkinnedMeshRenderer,
                player.BasisAvatar.FaceVisemeMesh,
                Mapping.Hashead ? Mapping.head : null);

            if (Mapping.Hashead)
            {
                HeadScale = Mapping.head.localScale;
            }
            else
            {
                HeadScale = Vector3.one;
            }

            CollectHeadChopEntries(harvestedHeadChop);

            // Capture the raw-joint T-pose snapshot while the avatar is still physically T-posed and
            // Mapping is populated — BEFORE SetBodySettings, whose rig build re-derives the FBT rotation
            // offsets (ApplyCalibrationToCurrentAvatar) from this snapshot; capturing later would hand
            // that rebuild the previous avatar's binds. Everything downstream (arm span, offset capture
            // references, offset reprojection) derives from this data instead of live bone reads.
            CaptureTposeBoneSnapshot();
            // PutAvatarIntoTPose measured the avatar before this snapshot existed, so the segment
            // calculator necessarily returned no leg/spine/shoulder data on a fresh avatar. Re-run
            // the snapshot-backed measurements now, before BuildBuilder/RefreshBodyFit consumes them.
            BasisLocalHeightCalculator.CalculateAvatarArmSpan();
            BasisLocalHeightCalculator.CalculateAvatarBodySegments();
            BasisLocalHeightCalculator.ValidateEyeToArmSizesAvatar();

            player.AvatarTransform.rotation = player.transform.rotation;
            CalculateTransformPositions(player, player.LocalBoneDriver);
            ComputeOffsets(player.LocalBoneDriver);
            player.LocalBoneDriver.SimulateAndApplyWithoutLerp(player);
            player.LocalRigDriver.SetBodySettings();

            player.BasisLocalFootDriver.InitializeVariables();

            player.LocalHandDriver.ReInitialize(player.BasisAvatar.Animator);
            player.LocalAnimatorDriver.Initialize(player);


            // Exit T-Pose and restore animator
            ResetAvatarAnimator();

            if (player.LocalBoneDriver.FindBone(out BasisLocalBoneControl Head, BasisBoneTrackedRole.Head))
            {
                Head.HasRigLayer = BasisHasRigLayer.HasRigLayer;
            }
            if (player.LocalBoneDriver.FindBone(out BasisLocalBoneControl Hips, BasisBoneTrackedRole.Hips))
            {
                Hips.HasRigLayer = BasisHasRigLayer.HasRigLayer;
            }
            if (player.LocalBoneDriver.FindBone(out BasisLocalBoneControl Spine, BasisBoneTrackedRole.Spine))
            {
                Spine.HasRigLayer = BasisHasRigLayer.HasRigLayer;
            }
            StoredRolesTransforms = BasisAvatarIKStageCalibration.GetAllRolesAsTransform();
            player.AvatarTransform.parent = player.transform;
            player.AvatarTransform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
            // Root is now normalized to identity; offsets captured above were taken before this.
            RecordCalibrationStage("PostZero", player);
            player.LocalRigDriver.BuildBuilder();

            IsNormalHead = true;
            RemoveJiggleRigColliders();
            if (player.IsConsideredFallBackAvatar == false)
            {
                AddJiggleRigColliders(Mapping);
            }
            // Avatar swap reuses the last genuine standing eye height (no live re-poll) so fit is stance-independent.
            BasisHeightDriver.CapturePlayerHeight(recaptureEyeHeight: false);
            BasisHeightDriver.ApplyScaleAndHeight();

            RecordCalibrationStage("Final", player);
            BasisCalibrationDebugRecorder.Flush();
            // Sample the first frames of the live head solve so the observed result can be compared
            // against the predicted target * offset. Writes a separate runtime_* CSV.
            BasisCalibrationDebugRecorder.RuntimeBegin(SafeAvatarLabel(player));
            RecordCalibrationMeta(player);
        }
        public static void ScaleHeadToNormal()
        {
            if (IsNormalHead || Instance == null || Mapping.Hashead == false) return;

            Mapping.head.localScale = HeadScale;
            for (int Index = 0; Index < HeadChopEntriesLength; Index++)
            {
                ref HeadChopEntry Entry = ref HeadChopEntries[Index];
                if (Entry.Target != null)
                {
                    Entry.Target.localScale = Entry.NormalScale;
                }
            }
            IsNormalHead = true;
        }

        public static void ScaleHeadToZero()
        {
            if (IsNormalHead == false)
            {
                return;
            }
            if (Instance == null)
            {
                return;
            }
            if (Mapping.Hashead == false)
            {
                return;
            }
            Mapping.head.localScale = HeadScaledDown;
            for (int Index = 0; Index < HeadChopEntriesLength; Index++)
            {
                ref HeadChopEntry Entry = ref HeadChopEntries[Index];
                if (Entry.Target != null)
                {
                    Entry.Target.localScale = Entry.HiddenScale;
                }
            }
            IsNormalHead = false;
        }

        public static void CollectHeadChopEntries(List<BasisHeadChop.HeadChopTarget> harvestedHeadChop)
        {
            if (harvestedHeadChop == null || harvestedHeadChop.Count == 0)
            {
                HeadChopEntries = Array.Empty<HeadChopEntry>();
                HeadChopEntriesLength = 0;
                return;
            }
            int TargetsCount = harvestedHeadChop.Count;
            List<HeadChopEntry> Collected = new List<HeadChopEntry>(TargetsCount);
            HashSet<Transform> Seen = new HashSet<Transform>();
            for (int Index = 0; Index < TargetsCount; Index++)
            {
                BasisHeadChop.HeadChopTarget Entry = harvestedHeadChop[Index];
                Transform Target = Entry.Target;
                if (Target == null) continue;
                if (Mapping.Hashead && Target == Mapping.head) continue;
                if (Seen.Add(Target) == false) continue;
                Vector3 Normal = Target.localScale;
                float ScaleFactor = Mathf.Clamp01(Entry.Scale);
                Collected.Add(new HeadChopEntry
                {
                    Target = Target,
                    NormalScale = Normal,
                    HiddenScale = Normal * ScaleFactor,
                });
            }
            HeadChopEntries = Collected.ToArray();
            HeadChopEntriesLength = HeadChopEntries.Length;
        }

        public void ComputeOffsets(BasisLocalBoneDriver BaseBoneDriver)
        {
            SetAndCreateLock(BaseBoneDriver, BasisBoneTrackedRole.CenterEye, BasisBoneTrackedRole.Head);
            SetAndCreateLock(BaseBoneDriver, BasisBoneTrackedRole.Head, BasisBoneTrackedRole.Neck);
            SetAndCreateLock(BaseBoneDriver, BasisBoneTrackedRole.Head, BasisBoneTrackedRole.Mouth);

            SetAndCreateLock(BaseBoneDriver, BasisBoneTrackedRole.Neck, BasisBoneTrackedRole.Chest);

            SetAndCreateLock(BaseBoneDriver, BasisBoneTrackedRole.Chest, BasisBoneTrackedRole.Spine);
            SetAndCreateLock(BaseBoneDriver, BasisBoneTrackedRole.Spine, BasisBoneTrackedRole.Hips);

            SetAndCreateLock(BaseBoneDriver, BasisBoneTrackedRole.Chest, BasisBoneTrackedRole.LeftShoulder);
            SetAndCreateLock(BaseBoneDriver, BasisBoneTrackedRole.Chest, BasisBoneTrackedRole.RightShoulder);

            SetAndCreateLock(BaseBoneDriver, BasisBoneTrackedRole.LeftShoulder, BasisBoneTrackedRole.LeftUpperArm);
            SetAndCreateLock(BaseBoneDriver, BasisBoneTrackedRole.RightShoulder, BasisBoneTrackedRole.RightUpperArm);

            SetAndCreateLock(BaseBoneDriver, BasisBoneTrackedRole.LeftUpperArm, BasisBoneTrackedRole.LeftLowerArm);
            SetAndCreateLock(BaseBoneDriver, BasisBoneTrackedRole.RightUpperArm, BasisBoneTrackedRole.RightLowerArm);

            SetAndCreateLock(BaseBoneDriver, BasisBoneTrackedRole.LeftLowerArm, BasisBoneTrackedRole.LeftHand);
            SetAndCreateLock(BaseBoneDriver, BasisBoneTrackedRole.RightLowerArm, BasisBoneTrackedRole.RightHand);

            // legs
            SetAndCreateLock(BaseBoneDriver, BasisBoneTrackedRole.Hips, BasisBoneTrackedRole.LeftUpperLeg);
            SetAndCreateLock(BaseBoneDriver, BasisBoneTrackedRole.Hips, BasisBoneTrackedRole.RightUpperLeg);

            SetAndCreateLock(BaseBoneDriver, BasisBoneTrackedRole.LeftUpperLeg, BasisBoneTrackedRole.LeftLowerLeg);
            SetAndCreateLock(BaseBoneDriver, BasisBoneTrackedRole.RightUpperLeg, BasisBoneTrackedRole.RightLowerLeg);

            SetAndCreateLock(BaseBoneDriver, BasisBoneTrackedRole.LeftLowerLeg, BasisBoneTrackedRole.LeftFoot);
            SetAndCreateLock(BaseBoneDriver, BasisBoneTrackedRole.RightLowerLeg, BasisBoneTrackedRole.RightFoot);

            SetAndCreateLock(BaseBoneDriver, BasisBoneTrackedRole.LeftFoot, BasisBoneTrackedRole.LeftToes);
            SetAndCreateLock(BaseBoneDriver, BasisBoneTrackedRole.RightFoot, BasisBoneTrackedRole.RightToes);
        }

        public bool IsAble()
        {
            if (IsNull(BasisLocalPlayer.Instance))
            {
                return false;
            }
            if (IsNull(BasisLocalPlayer.Instance.BasisAvatar))
            {
                return false;
            }
            if (IsNull(BasisLocalPlayer.Instance.BasisAvatar.Animator))
            {
                return false;
            }
            return true;
        }

        public float ActiveAvatarEyeHeight()
        {
            var localPlayer = BasisLocalPlayer.Instance;
            if (localPlayer?.BasisAvatar != null)
            {
                return localPlayer.BasisAvatar.AvatarEyePosition.x;
            }
            else
            {
                return BasisHeightDriver.FallbackHeightInMeters;
            }
        }

        public void Calibration(BasisLocalPlayer LocalPlayer)
        {
            var Avatar = LocalPlayer.BasisAvatar;
            FindSkinnedMeshRenders(LocalPlayer);
            // AvatarTransform is the shared local/network coordinate root. The Animator may be nested
            // below an import-scale node, but mapped positions must remain in the same rendered-metre
            // frame used by avatar eye height, tracker offsets, and remote replication.
            BasisTransformMapping.AutoDetectReferences(LocalPlayer.BasisAvatar.Animator, Avatar.transform, ref Mapping, humanoidBones: Avatar.TransformStorage?.HumanoidBones);
            BasisAvatarModelCache.RecordPosesCached(Mapping, LocalPlayer.BasisAvatar.Animator);
            LocalPlayer.FaceIsVisible = false;

            if (Avatar == null)
            {
                BasisDebug.LogError("Missing Avatar");
            }
            if (LocalPlayer.FaceRenderer != null)
            {
                // Mute before the deferred destroy: the outgoing avatar's renderer fires a
                // final OnBecameInvisible during its end-of-frame teardown, and that late
                // notification would stomp the visibility state just set up for the
                // incoming avatar.
                LocalPlayer.FaceRenderer.Check = null;
                GameObject.Destroy(LocalPlayer.FaceRenderer);
                LocalPlayer.FaceRenderer = null;
            }

            if (Avatar.FaceVisemeMesh != null)
            {
                LocalPlayer.UpdateFaceVisibility(Avatar.FaceVisemeMesh.isVisible);
                LocalPlayer.FaceRenderer = BasisHelpers.GetOrAddComponent<BasisMeshRendererCheck>(Avatar.FaceVisemeMesh.gameObject);
                LocalPlayer.FaceRenderer.Check += LocalPlayer.UpdateFaceVisibility;
            }
            else
            {
                BasisDebug.Log("Missing Face for " + LocalPlayer.DisplayName, BasisDebug.LogTag.Avatar);
                LocalPlayer.UpdateFaceVisibility(false);
            }

            if (BasisLocalFacialBlinkDriver.MeetsRequirements(Avatar))
            {
                LocalPlayer.FacialBlinkDriver.Initialize(LocalPlayer, Avatar);
            }
        }

        public void PutAvatarIntoTPose()
        {
            BasisDebug.Log("PutAvatarIntoTPose", BasisDebug.LogTag.Avatar);
            CurrentlyTposing = true;
            if (SavedruntimeAnimatorController == null)
            {
                SavedruntimeAnimatorController = BasisLocalPlayer.Instance.BasisAvatar.Animator.runtimeAnimatorController;
            }
            BasisLocalPlayer.Instance.BasisAvatar.Animator.runtimeAnimatorController = BasisPlayerFactory.TposeController;
            ForceUpdateAnimator(BasisLocalPlayer.Instance.BasisAvatar.Animator);
            TposeStateChange?.Invoke();

            BasisLocalPlayer.Instance.LocalRigDriver.DisableAllTrackers();
            //anytime a avatar goes into a tpose we can grab the avatar height information
            BasisHeightDriver.CaptureAvatarHeightDuringTpose();
        }

        public void CaptureTposeBoneSnapshot()
        {
            TposeBoneSnapshot.Clear();
            HasTposeBoneSnapshot = false;
            if (Mapping.HasAnimatorRoot == false || Mapping.AnimatorRoot == null)
            {
                BasisDebug.LogError("CaptureTposeBoneSnapshot: no animator root; snapshot unavailable.", BasisDebug.LogTag.Avatar);
                return;
            }

            Mapping.AnimatorRoot.GetPositionAndRotation(out Vector3 rootPos, out Quaternion rootRot);
            Quaternion invRoot = Quaternion.Inverse(rootRot);
            float scale = ScaleAvatarModification != null ? ScaleAvatarModification.ApplyScale : 1f;
            if (float.IsNaN(scale) || float.IsInfinity(scale) || scale <= 1e-6f)
            {
                scale = 1f;
            }

            Dictionary<BasisBoneTrackedRole, Transform> roles = BasisAvatarIKStageCalibration.GetAllRolesAsTransform();
            foreach (KeyValuePair<BasisBoneTrackedRole, Transform> pair in roles)
            {
                Transform bone = pair.Value;
                if (bone == null)
                {
                    continue;
                }
                bone.GetPositionAndRotation(out Vector3 bonePos, out Quaternion boneRot);
                TposeBoneSnapshot[pair.Key] = new BasisCalibratedCoords(
                    (invRoot * (bonePos - rootPos)) / scale,
                    invRoot * boneRot);
            }
            HasTposeBoneSnapshot = TposeBoneSnapshot.Count > 0;
        }

        public void ResetAvatarAnimator()
        {
            BasisDebug.Log("ResetAvatarAnimator", BasisDebug.LogTag.Avatar);
            BasisLocalPlayer.Instance.BasisAvatar.Animator.runtimeAnimatorController = SavedruntimeAnimatorController;
            SavedruntimeAnimatorController = null;
            CurrentlyTposing = false;
            TposeStateChange?.Invoke();
        }

        public void CalculateTransformPositions(BasisPlayer basisPlayer, BasisLocalBoneDriver driver)
        {
            // Cache hot references
            Animator animator = basisPlayer.BasisAvatar.Animator;
            Transform rootTransform = animator.transform;

            rootTransform.GetPositionAndRotation(out Vector3 RootPosition, out Quaternion RootRotation);
            var fbdb = BasisDeviceManagement.Instance.FBBD;

            for (int Index = 0; Index < driver.ControlsLength; Index++)
            {
                var control = driver.Controls[Index];
                var role = driver.trackedRoles[Index];

                switch (role)
                {
                    case BasisBoneTrackedRole.CenterEye:
                        {
                            // Convert avatar-local eye position to world and apply
                            GetWorldSpacePos(BasisHelpers.AvatarPositionConversion(basisPlayer.BasisAvatar.AvatarEyePosition), RootPosition, RootRotation, out float3 world);
                            SetInitialData(rootTransform, control, role, world, RootRotation);
                            break;
                        }

                    case BasisBoneTrackedRole.Mouth:
                        {
                            // Convert avatar-local mouth position to world and apply
                            GetWorldSpacePos(BasisHelpers.AvatarPositionConversion(basisPlayer.BasisAvatar.AvatarMouthPosition), RootPosition, RootRotation, out float3 world);
                            SetInitialData(rootTransform, control, role, world, RootRotation);
                            break;
                        }

                    default:
                        {
                            // Use fallback DB + humanoid mapping
                            if (fbdb.FindBone(out BasisFallBackBone fallback, role))
                            {
                                if (TryConvertToHumanoidRole(role, out HumanBodyBones human))
                                {
                                    GetBoneRotAndPos(RootRotation, animator, human, fallback.PositionPercentage, out quaternion worldRotation, out float3 world, out bool _);

                                    SetInitialData(rootTransform, control, role, world, worldRotation);
                                }
                                else
                                {
                                    BasisDebug.LogError("can't Convert to humanbodybone " + role);
                                }
                            }
                            else
                            {
                                BasisDebug.LogError("can't find Fallback Bone for " + role);
                            }
                            break;
                        }
                }
            }
        }

        public void GetWorldSpacePos(Vector3 localAvatarSpace, Vector3 AnimatorPosition, Quaternion AnimatorRotation, out float3 position)
        {
            position = BasisHelpers.ConvertFromLocalSpace(localAvatarSpace, AnimatorPosition, AnimatorRotation);
        }

        public void GetBoneRotAndPos(quaternion RootRotation, Animator anim, HumanBodyBones bone, Vector3 heightPercentage, out quaternion Rotation, out float3 Position, out bool UsedFallback)
        {
            if (anim.avatar != null && anim.avatar.isHuman)
            {
                Transform boneTransform = anim.GetBoneTransform(bone);
                if (boneTransform == null)
                {
                    Rotation = RootRotation;
                    Position = anim.transform.position;
                    // Position = new Vector3(0, Position.y, 0);
                    Position += CalculateFallbackOffset(bone, ActiveAvatarEyeHeight(), heightPercentage);
                    //Position = new Vector3(0, Position.y, 0);
                    UsedFallback = true;
                }
                else
                {
                    UsedFallback = false;
                    boneTransform.GetPositionAndRotation(out Vector3 VPosition, out Quaternion QRotation);
                    Position = VPosition;
                    Rotation = QRotation;
                }
            }
            else
            {
                Rotation = RootRotation;
                Position = anim.transform.position;
                Position = new Vector3(0, Position.y, 0);
                Position += CalculateFallbackOffset(bone, ActiveAvatarEyeHeight(), heightPercentage);
                Position = new Vector3(0, Position.y, 0);
                UsedFallback = true;
            }
        }

        public float3 CalculateFallbackOffset(HumanBodyBones bone, float fallbackHeight, float3 heightPercentage)
        {
            Vector3 height = fallbackHeight * heightPercentage;
            return bone == HumanBodyBones.Hips ? math.mul(height, -Vector3.up) : math.mul(height, Vector3.up);
        }

        public void ForceUpdateAnimator(Animator Anim)
        {
            // Specify the time you want the Animator to update to (in seconds)
            float desiredTime = Time.deltaTime;

            // Call the Update method to force the Animator to update to the desired time
            Anim.Update(desiredTime);
        }

        public bool IsNull(UnityEngine.Object obj)
        {
            if (obj == null)
            {
                BasisDebug.LogError("Missing Object during calibration");
                return true;
            }
            else
            {
                return false;
            }
        }

        public void SetInitialData(Transform Transform, BasisLocalBoneControl bone, BasisBoneTrackedRole Role, Vector3 WorldTpose, Quaternion WorldTposeRotation)
        {
            Vector3 outgoingPosition = BasisLocalBoneDriver.ConvertToAvatarSpaceInitial(Transform, WorldTpose);
            Quaternion outgoingRotation = Quaternion.Inverse(Transform.rotation) * WorldTposeRotation;

            if (IsApartOfSpineVertical(Role))
            {
                outgoingPosition.x = 0;
            }

            bone.SetOutgoing(outgoingPosition, outgoingRotation);
            bone.SetTposeLocal(outgoingPosition, outgoingRotation);
            bone.SetTposeScaled(outgoingPosition, outgoingRotation);
        }

        public void SetAndCreateLock(BasisLocalBoneDriver BaseBoneDriver, BasisBoneTrackedRole LockToBoneRole, BasisBoneTrackedRole AssignedTo)
        {
            if (BaseBoneDriver.FindBone(out BasisLocalBoneControl AssignedToAddToBone, AssignedTo) == false)
            {
                BasisDebug.LogError("Can't Find Bone " + AssignedTo);
            }
            if (BaseBoneDriver.FindBone(out BasisLocalBoneControl LockToBone, LockToBoneRole) == false)
            {
                BasisDebug.LogError("Can't Find Bone " + LockToBoneRole);
            }
            BaseBoneDriver.CreateRotationalLock(AssignedToAddToBone, LockToBone);
        }

        private static string SafeAvatarLabel(BasisLocalPlayer player)
        {
            if (player == null)
            {
                return "avatar";
            }
            string name = null;
            BasisAvatar avatar = player.BasisAvatar;
            if (avatar != null)
            {
                name = avatar.name;
            }
            if (string.IsNullOrWhiteSpace(name))
            {
                name = player.DisplayName;
            }
            return string.IsNullOrWhiteSpace(name) ? "avatar" : name;
        }

        private static void RecordCalibrationMeta(BasisLocalPlayer player)
        {
            if (BasisCalibrationDebugRecorder.Enabled == false)
            {
                return;
            }
            BasisAvatar avatar = player != null ? player.BasisAvatar : null;
            BasisCalibrationDebugRecorder.Meta("avatarName", avatar != null ? avatar.name : "(none)");
            BasisCalibrationDebugRecorder.Meta("displayName", player != null ? player.DisplayName : "(none)");
            BasisCalibrationDebugRecorder.Meta("isFallbackAvatar", player != null ? player.IsConsideredFallBackAvatar.ToString() : "?");
        }

        private static void RecordCalibrationStage(string stage, BasisLocalPlayer player)
        {
            if (BasisCalibrationDebugRecorder.Enabled == false)
            {
                return;
            }
            Transform animRoot = player?.BasisAvatar?.Animator != null ? player.BasisAvatar.Animator.transform : null;
            BasisCalibrationDebugRecorder.Bone(stage, "AnimatorRoot", animRoot);
            if (player != null)
            {
                BasisCalibrationDebugRecorder.Bone(stage, "PlayerRoot", player.transform);
            }
            BasisCalibrationDebugRecorder.Bone(stage, "Mapping.AnimatorRoot", Mapping.AnimatorRoot);
            BasisCalibrationDebugRecorder.Bone(stage, "Hips", Mapping.Hips);
            BasisCalibrationDebugRecorder.Bone(stage, "spine", Mapping.spine);
            BasisCalibrationDebugRecorder.Bone(stage, "chest", Mapping.chest);
            BasisCalibrationDebugRecorder.Bone(stage, "Upperchest", Mapping.Upperchest);
            BasisCalibrationDebugRecorder.Bone(stage, "neck", Mapping.neck);
            BasisCalibrationDebugRecorder.Bone(stage, "head", Mapping.head);
            BasisCalibrationDebugRecorder.Bone(stage, "leftShoulder", Mapping.leftShoulder);
            BasisCalibrationDebugRecorder.Bone(stage, "leftUpperArm", Mapping.leftUpperArm);
            BasisCalibrationDebugRecorder.Bone(stage, "leftLowerArm", Mapping.leftLowerArm);
            BasisCalibrationDebugRecorder.Bone(stage, "leftHand", Mapping.leftHand);
            BasisCalibrationDebugRecorder.Bone(stage, "RightShoulder", Mapping.RightShoulder);
            BasisCalibrationDebugRecorder.Bone(stage, "RightUpperArm", Mapping.RightUpperArm);
            BasisCalibrationDebugRecorder.Bone(stage, "RightLowerArm", Mapping.RightLowerArm);
            BasisCalibrationDebugRecorder.Bone(stage, "rightHand", Mapping.rightHand);
            BasisCalibrationDebugRecorder.Bone(stage, "LeftUpperLeg", Mapping.LeftUpperLeg);
            BasisCalibrationDebugRecorder.Bone(stage, "LeftLowerLeg", Mapping.LeftLowerLeg);
            BasisCalibrationDebugRecorder.Bone(stage, "leftFoot", Mapping.leftFoot);
            BasisCalibrationDebugRecorder.Bone(stage, "leftToe", Mapping.leftToe);
            BasisCalibrationDebugRecorder.Bone(stage, "RightUpperLeg", Mapping.RightUpperLeg);
            BasisCalibrationDebugRecorder.Bone(stage, "RightLowerLeg", Mapping.RightLowerLeg);
            BasisCalibrationDebugRecorder.Bone(stage, "rightFoot", Mapping.rightFoot);
            BasisCalibrationDebugRecorder.Bone(stage, "rightToe", Mapping.rightToe);
        }

        public void FindSkinnedMeshRenders(BasisLocalPlayer LocalPlayer)
        {
            SkinnedMeshRenderer = LocalPlayer.BasisAvatar.SkinnedMeshRenderers
                ?? LocalPlayer.BasisAvatar.Animator.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            SkinnedMeshRendererLength = SkinnedMeshRenderer.Length;
        }
    }
}
