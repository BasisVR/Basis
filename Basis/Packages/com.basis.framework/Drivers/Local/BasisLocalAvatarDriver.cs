using Basis.Scripts.Avatar;
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
using UnityEngine.Animations.Rigging;

namespace Basis.Scripts.Drivers
{
    /// <summary>
    /// Local avatar driver responsible for calibration, T-pose sequencing, animator swapping,
    /// transform initialization, and mesh update settings for a locally controlled avatar.
    /// </summary>
    [Serializable]
    public class BasisLocalAvatarDriver : BasisAvatarDriver
    {

        /// <summary>Addressables key for the default locomotion animator controller.</summary>
        public const string Locomotion = "Locomotion";

        /// <summary>Cached original head scale recorded during initialization.</summary>
        public static Vector3 HeadScale = Vector3.one;

        /// <summary>Scale used to hide the head (scaled to zero).</summary>
        public static Vector3 HeadScaledDown = Vector3.zero;

        /// <summary>Tracks whether the T-pose state-change event was wired.</summary>
        public static bool HasTPoseEvent = false;

        /// <summary>Singleton-like reference to the local avatar driver instance.</summary>
        public static BasisLocalAvatarDriver Instance;

        /// <summary>True when the head currently uses the normal/original scale.</summary>
        public static bool IsNormalHead;

        /// <summary>True while the avatar is being held in T-pose mode.</summary>
        public static bool CurrentlyTposing = false;

        /// <summary>Event raised when calibration has completed.</summary>
        public static Action CalibrationComplete;

        /// <summary>Event raised whenever the T-pose state changes.</summary>
        public static Action TposeStateChange;

        /// <summary>Discovered avatar transform references (head, hands, etc.).</summary>
        public static BasisTransformMapping Mapping = new BasisTransformMapping();

        /// <summary>Saved animator controller used to restore after T-pose.</summary>
        public static RuntimeAnimatorController SavedruntimeAnimatorController;

        /// <summary>All skinned mesh renderers under the avatar animator.</summary>
        public static SkinnedMeshRenderer[] SkinnedMeshRenderer;

        /// <summary>Whether runtime events have been subscribed.</summary>
        public static bool HasEvents = false;

        /// <summary>Cached length of <see cref="SkinnedMeshRenderer"/>.</summary>
        public static int SkinnedMeshRendererLength;

        /// <summary>Stores the transforms for each tracked role at calibration time.</summary>
        public Dictionary<BasisBoneTrackedRole, Transform> StoredRolesTransforms = new Dictionary<BasisBoneTrackedRole, Transform>();

        /// <summary>Runtime scale modification settings for the avatar.</summary>
        [SerializeField]
        public BasisAvatarScaleModifier ScaleAvatarModification = new BasisAvatarScaleModifier();

        /// <summary>
        /// Performs initial local calibration: sets up rig driver, puts avatar into T-pose,
        /// builds rigs, computes offsets, initializes drivers, and restores the animator.
        /// </summary>
        /// <param name="player">The local player instance.</param>
        public void InitialLocalCalibration(BasisLocalPlayer player)
        {
            Instance = this;
            BasisDebug.Log("InitialLocalCalibration");
            if (HasTPoseEvent == false)
            {
                TposeStateChange += player.LocalRigDriver.OnTPose;
                HasTPoseEvent = true;
            }
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

            player.LocalRigDriver.CleanupBeforeContinue();
            player.LocalRigDriver.AdditionalTransforms.Clear();
            GameObject AvatarAnimatorParent = player.BasisAvatar.Animator.gameObject;
            ScaleAvatarModification.ReInitalize(player.BasisAvatar.Animator);

            player.BasisAvatar.Animator.updateMode = AnimatorUpdateMode.Normal;
            player.BasisAvatar.Animator.logWarnings = false;

            if (player.BasisAvatar.Animator.runtimeAnimatorController == null)
            {
                UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle<RuntimeAnimatorController> op = Addressables.LoadAssetAsync<RuntimeAnimatorController>(Locomotion);
                RuntimeAnimatorController RAC = op.WaitForCompletion();
                player.BasisAvatar.Animator.runtimeAnimatorController = RAC;
            }
            player.BasisAvatar.Animator.applyRootMotion = false;
            player.BasisAvatar.HumanScale = player.BasisAvatar.Animator.humanScale;

            // Enter T-Pose for calibration
            PutAvatarIntoTPose();

            // Initialize any physics/jiggle rigs before building the rig
            var JiggleRigs = player.BasisAvatar.GetComponentsInChildren<JiggleRig>();
            int length = JiggleRigs.Length;
            for (int Index = 0; Index < length; Index++)
            {
                JiggleRig Rig = JiggleRigs[Index];
                JiggleRigData Data = Rig.GetJiggleRigData();
                Rig.HasAnimatedParameters = false;
                Rig.OnInitialize();
            }

            player.LocalRigDriver.Builder = BasisHelpers.GetOrAddComponent<RigBuilder>(AvatarAnimatorParent);
            player.LocalRigDriver.Builder.enabled = false;

            Calibration(player);

            player.LocalBoneDriver.RemoveAllListeners();
            BasisLocalEyeDriver.Initalize();
            LocalRenderMeshSettings(BasisLayerMapper.LocalAvatarLayer, SkinnedMeshRendererLength, SkinnedMeshRenderer, player.BasisAvatar.FaceVisemeMesh);

            if (Mapping.Hashead)
            {
                HeadScale = Mapping.head.localScale;
            }
            else
            {
                HeadScale = Vector3.one;
            }

            player.LocalRigDriver.SetBodySettings();


            CalculateTransformPositions(player, player.LocalBoneDriver);

            ComputeOffsets(player.LocalBoneDriver);

            // player.BasisLocalFootDriver.InitializeVariables();

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
            player.LocalRigDriver.BuildBuilder();

            IsNormalHead = true;
            RemoveJiggleRigColliders();
            if (player.IsConsideredFallBackAvatar == false)
            {
                AddJiggleRigColliders(Mapping);
            }
            BasisHeightDriver.ApplyScaleAndHeight();

        }
        /// <summary>
        /// Restores the head scale to its cached normal value if currently hidden/zeroed.
        /// </summary>
        public static void ScaleHeadToNormal()
        {
            if (IsNormalHead || Instance == null || Mapping.Hashead == false) return;

            Mapping.head.localScale = HeadScale;
            IsNormalHead = true;
        }

        /// <summary>
        /// Scales the head to zero, effectively hiding it (e.g., for first-person rigs).
        /// </summary>
        public static void ScaleheadToZero()
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
            IsNormalHead = false;
        }

        /// <summary>
        /// Establishes hierarchical locks/constraints between tracked roles to compute offsets.
        /// </summary>
        /// <param name="BaseBoneDriver">The bone driver providing role lookups and lock creation.</param>
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

        /// <summary>
        /// Checks whether basic dependencies for calibration are present (local player, avatar, animator).
        /// </summary>
        /// <returns>True if calibration can proceed; otherwise false.</returns>
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

        /// <summary>
        /// Returns the active avatar eye height; falls back to a constant when no avatar is available.
        /// </summary>
        /// <returns>Eye height value.</returns>
        public float ActiveAvatarEyeHeight()
        {
            if (BasisLocalPlayer.Instance.BasisAvatar != null)
            {
                return BasisLocalPlayer.Instance.BasisAvatar.AvatarEyePosition.x;
            }
            else
            {
                return BasisHeightDriver.FallbackHeightInMeters;
            }
        }

        /// <summary>
        /// Performs reference detection, layer setup, pose recording, face visibility wiring,
        /// and facial blink driver initialization during calibration.
        /// </summary>
        /// <param name="LocalPlayer">The local player whose avatar is being calibrated.</param>
        public void Calibration(BasisLocalPlayer LocalPlayer)
        {
            var Avatar = LocalPlayer.BasisAvatar;
            FindSkinnedMeshRenders(LocalPlayer);
            BasisTransformMapping.AutoDetectReferences(LocalPlayer.BasisAvatar.Animator, Avatar.transform, ref Mapping);
            Mapping.RecordPoses(LocalPlayer.BasisAvatar.Animator);
            LocalPlayer.FaceIsVisible = false;

            if (Avatar == null)
            {
                BasisDebug.LogError("Missing Avatar");
            }
            if (Avatar.FaceVisemeMesh == null)
            {
                BasisDebug.Log("Missing Face for " + LocalPlayer.DisplayName, BasisDebug.LogTag.Avatar);
            }

            LocalPlayer.UpdateFaceVisibility(Avatar.FaceVisemeMesh.isVisible);

            if (LocalPlayer.FaceRenderer != null)
            {
                GameObject.Destroy(LocalPlayer.FaceRenderer);
            }

            LocalPlayer.FaceRenderer = BasisHelpers.GetOrAddComponent<BasisMeshRendererCheck>(Avatar.FaceVisemeMesh.gameObject);
            LocalPlayer.FaceRenderer.Check += LocalPlayer.UpdateFaceVisibility;

            if (BasisLocalFacialBlinkDriver.MeetsRequirements(Avatar))
            {
                LocalPlayer.FacialBlinkDriver.Initialize(LocalPlayer, Avatar);
            }
        }

        /// <summary>
        /// Swaps the animator to the T-pose controller, forces an update, and raises the state change event.
        /// </summary>
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

        /// <summary>
        /// Restores the original animator controller and leaves T-pose mode, raising the state change event.
        /// </summary>
        public void ResetAvatarAnimator()
        {
            BasisDebug.Log("ResetAvatarAnimator", BasisDebug.LogTag.Avatar);
            BasisLocalPlayer.Instance.BasisAvatar.Animator.runtimeAnimatorController = SavedruntimeAnimatorController;
            SavedruntimeAnimatorController = null;
            CurrentlyTposing = false;
            TposeStateChange?.Invoke();
        }

        /// <summary>
        /// Initializes outgoing positions for each bone control based on avatar data, humanoid mapping, or fallback DB.
        /// </summary>
        /// <param name="basisPlayer">The player whose avatar is used for bone mapping.</param>
        /// <param name="driver">The bone driver storing controls and roles.</param>
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
                            GetWorldSpacePos(BasisHelpers.AvatarPositionConversion(basisPlayer.BasisAvatar.AvatarEyePosition), RootPosition, out float3 world);
                            SetInitialData(rootTransform, control, role, world, RootRotation);
                            break;
                        }

                    case BasisBoneTrackedRole.Mouth:
                        {
                            // Convert avatar-local mouth position to world and apply
                            GetWorldSpacePos(BasisHelpers.AvatarPositionConversion(basisPlayer.BasisAvatar.AvatarMouthPosition), RootPosition, out float3 world);
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
                                    BasisDebug.LogError("cant Convert to humanbodybone " + role);
                                }
                            }
                            else
                            {
                                BasisDebug.LogError("cant find Fallback Bone for " + role);
                            }
                            break;
                        }
                }
            }
        }

        /// <summary>
        /// Converts a local avatar-space position to world space based on animator position.
        /// </summary>
        /// <param name="localAvatarSpace">Point in avatar-local coordinates.</param>
        /// <param name="AnimatorPosition">Animator world position used as origin.</param>
        /// <param name="position">Out: computed world position.</param>
        public void GetWorldSpacePos(Vector3 localAvatarSpace, Vector3 AnimatorPosition, out float3 position)
        {
            position = BasisHelpers.ConvertFromLocalSpace(localAvatarSpace, AnimatorPosition);
        }

        /// <summary>
        /// Retrieves rotation and position for a humanoid bone if possible; otherwise computes a fallback
        /// based on eye height and configured height percentage.
        /// </summary>
        /// <param name="driver">Driver transform used for fallback orientation.</param>
        /// <param name="anim">Animator providing humanoid mapping.</param>
        /// <param name="bone">Humanoid bone to query.</param>
        /// <param name="heightPercentage">Relative height used in fallback positioning.</param>
        /// <param name="Rotation">Out: resulting rotation.</param>
        /// <param name="Position">Out: resulting position.</param>
        /// <param name="UsedFallback">Out: true if fallback path was used.</param>
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

        /// <summary>
        /// Calculates a simple vertical offset for fallback positioning based on bone type and avatar height.
        /// </summary>
        /// <param name="bone">Humanoid bone being positioned.</param>
        /// <param name="fallbackHeight">Height scalar (often eye height or similar).</param>
        /// <param name="heightPercentage">Multiplier for the height.</param>
        /// <returns>Offset vector applied to the base position.</returns>
        public float3 CalculateFallbackOffset(HumanBodyBones bone, float fallbackHeight, float3 heightPercentage)
        {
            Vector3 height = fallbackHeight * heightPercentage;
            return bone == HumanBodyBones.Hips ? math.mul(height, -Vector3.up) : math.mul(height, Vector3.up);
        }

        /// <summary>
        /// Forces an immediate animator update by advancing it by <see cref="Time.deltaTime"/>.
        /// </summary>
        /// <param name="Anim">Animator to update.</param>
        public void ForceUpdateAnimator(Animator Anim)
        {
            // Specify the time you want the Animator to update to (in seconds)
            float desiredTime = Time.deltaTime;

            // Call the Update method to force the Animator to update to the desired time
            Anim.Update(desiredTime);
        }

        /// <summary>
        /// Null-check helper that logs an error when the object is missing during calibration.
        /// </summary>
        /// <param name="obj">Object to test.</param>
        /// <returns>True if null; otherwise false.</returns>
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

        /// <summary>
        /// Seeds a bone control’s T-pose and outgoing data based on a world-space T-pose position
        /// and applies special rules for vertical spine alignment and hips rotation.
        /// </summary>
        /// <param name="Transform">Avatar root transform.</param>
        /// <param name="bone">The bone control to initialize.</param>
        /// <param name="Role">The tracked role of the bone.</param>
        /// <param name="WorldTpose">World-space T-pose position to convert to avatar space.</param>
        public void SetInitialData(Transform Transform, BasisLocalBoneControl bone, BasisBoneTrackedRole Role, Vector3 WorldTpose, Quaternion WorldTposeRotation)
        {
            bone.OutGoingData.position = BasisLocalBoneDriver.ConvertToAvatarSpaceInitial(Transform, WorldTpose);
            bone.OutGoingData.rotation = Quaternion.Inverse(Transform.rotation) * WorldTposeRotation;

            if (IsApartOfSpineVertical(Role))
            {
                bone.OutGoingData.position.x = 0;
            }

            bone.TposeLocal.rotation = bone.OutGoingData.rotation;
            bone.TposeLocal.position = bone.OutGoingData.position;

            bone.TposeLocalScaled.position = bone.TposeLocal.position;
            bone.TposeLocalScaled.rotation = bone.TposeLocal.rotation;
        }

        /// <summary>
        /// Creates a lock/constraint between two roles (AssignedTo follows LockToBoneRole) using the base driver.
        /// </summary>
        /// <param name="BaseBoneDriver">The driver containing role lookups.</param>
        /// <param name="LockToBoneRole">The role to lock toward.</param>
        /// <param name="AssignedTo">The role being assigned/linked to the lock target.</param>
        public void SetAndCreateLock(BasisLocalBoneDriver BaseBoneDriver, BasisBoneTrackedRole LockToBoneRole, BasisBoneTrackedRole AssignedTo)
        {
            if (BaseBoneDriver.FindBone(out BasisLocalBoneControl AssignedToAddToBone, AssignedTo) == false)
            {
                BasisDebug.LogError("Cant Find Bone " + AssignedTo);
            }
            if (BaseBoneDriver.FindBone(out BasisLocalBoneControl LockToBone, LockToBoneRole) == false)
            {
                BasisDebug.LogError("Cant Find Bone " + LockToBoneRole);
            }
            BaseBoneDriver.CreateRotationalLock(AssignedToAddToBone, LockToBone);
        }

        /// <summary>
        /// Populates <see cref="SkinnedMeshRenderer"/> and caches its length for fast loops.
        /// </summary>
        /// <param name="LocalPlayer">The local player whose avatar meshes are scanned.</param>
        public void FindSkinnedMeshRenders(BasisLocalPlayer LocalPlayer)
        {
            SkinnedMeshRenderer = LocalPlayer.BasisAvatar.Animator.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            SkinnedMeshRendererLength = SkinnedMeshRenderer.Length;
        }
    }
}
