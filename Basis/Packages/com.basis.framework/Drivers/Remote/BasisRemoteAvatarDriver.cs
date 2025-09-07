using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Device_Management;
using Basis.Scripts.TransformBinders.BoneControl;
using GatorDragonGames.JigglePhysics;
using System;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.AddressableAssets;
namespace Basis.Scripts.Drivers
{
    [System.Serializable]
    public class BasisRemoteAvatarDriver : BasisAvatarDriver
    {
        public Action CalibrationComplete;
        [SerializeField]
        public BasisTransformMapping References = new BasisTransformMapping();
        public SkinnedMeshRenderer[] SkinnedMeshRenderer;
        public BasisPlayer Player;
        public bool HasEvents = false;
        public int SkinnedMeshRendererLength;
        public Vector3 AvatarInitalScale = Vector3.one;
        public void RemoteCalibration(BasisRemotePlayer player)
        {
            if (!IsAble(player))
            {
                return;
            }
            else
            {
                //  BasisDebug.Log("RemoteCalibration Underway", BasisDebug.LogTag.Avatar);
            }

            Player = player;


            SkinnedMeshRenderer = Player.BasisAvatar.Animator.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            SkinnedMeshRendererLength = SkinnedMeshRenderer.Length;
            SetupAvatarLayers(Player, BasisLayerMapper.RemoteAvatarLayer);
            PutAvatarIntoTPose();

            AvatarInitalScale = Player.BasisAvatar.transform.localScale;
            BasisTransformMapping.AutoDetectReferences(Player.BasisAvatar.Animator, player.BasisAvatar.transform, ref References);
            References.RecordPoses(Player.BasisAvatar.Animator);
            var JiggleRigs = player.BasisAvatar.GetComponentsInChildren<JiggleRig>();

            foreach (JiggleRig Rig in JiggleRigs)
            {
                Rig.OnInitialize();
            }

            Player.FaceIsVisible = false;
            if (player.BasisAvatar == null)
            {
                BasisDebug.LogError("Missing Avatar On Remote", BasisDebug.LogTag.Avatar);
            }
            if (player.BasisAvatar.FaceVisemeMesh == null)
            {
                BasisDebug.Log("Missing Face for " + Player.DisplayName, BasisDebug.LogTag.Avatar);
            }
            Player.UpdateFaceVisibility(player.BasisAvatar.FaceVisemeMesh.isVisible);
            if (Player.FaceRenderer != null)
            {
                GameObject.Destroy(Player.FaceRenderer);
            }
            Player.FaceRenderer = BasisHelpers.GetOrAddComponent<BasisMeshRendererCheck>(player.BasisAvatar.FaceVisemeMesh.gameObject);
            Player.FaceRenderer.Check += Player.UpdateFaceVisibility;

            if (BasisFacialBlinkDriver.MeetsRequirements(player.BasisAvatar))
            {
                Player.FacialBlinkDriver.Initialize(Player, player.BasisAvatar);
            }
            player.RemoteEyeDriver.Initalize(this, player);
            UpdateWhenOffscreenAndDisableMatrixRecal(false);
            player.BasisAvatar.Animator.logWarnings = false;
            CalculateTransformPositions(player, player.RemoteBoneDriver);
            ComputeOffsets(player.RemoteBoneDriver);
            player.BasisAvatar.Animator.enabled = false;

            SetupAvatarJiggleColliders();
            ResetAvatarAnimator();
        }
        public bool CurrentlyTposing;
        public RuntimeAnimatorController SavedruntimeAnimatorController;
        public void PutAvatarIntoTPose()
        {
            BasisDebug.Log("PutAvatarIntoTPose", BasisDebug.LogTag.Avatar);
            CurrentlyTposing = true;
            if (SavedruntimeAnimatorController == null)
            {
                SavedruntimeAnimatorController = Player.BasisAvatar.Animator.runtimeAnimatorController;
            }
            UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle<RuntimeAnimatorController> op = Addressables.LoadAssetAsync<RuntimeAnimatorController>(TPose);
            RuntimeAnimatorController RAC = op.WaitForCompletion();
            Player.BasisAvatar.Animator.runtimeAnimatorController = RAC;
            ForceUpdateAnimator(Player.BasisAvatar.Animator);
        }
        public const string TPose = "Assets/Animator/Animated TPose.controller";
        public void ForceUpdateAnimator(Animator Anim)
        {
            // Specify the time you want the Animator to update to (in seconds)
            float desiredTime = Time.deltaTime;

            // Call the Update method to force the Animator to update to the desired time
            Anim.Update(desiredTime);
        }

        public void ResetAvatarAnimator()
        {
            BasisDebug.Log("ResetAvatarAnimator", BasisDebug.LogTag.Avatar);
            Player.BasisAvatar.Animator.runtimeAnimatorController = SavedruntimeAnimatorController;
            SavedruntimeAnimatorController = null;
            CurrentlyTposing = false;
        }
        public async void SetupAvatarJiggleColliders()
        {
            RemoveJiggleRigColliders();
            BasisPlayerSettingsData BasisPlayerSettingsData = await BasisPlayerSettingsManager.RequestPlayerSettings(Player.UUID);
            if (BasisPlayerSettingsData.AvatarInteraction)
            {
                AddJiggleRigColliders(References);
            }
        }
        public void ComputeOffsets(BasisRemoteBoneDriver BBD)
        {
            SetAndCreateLock(BBD, BasisBoneTrackedRole.Head, BasisBoneTrackedRole.Neck);
            SetAndCreateLock(BBD, BasisBoneTrackedRole.Head, BasisBoneTrackedRole.CenterEye);
            SetAndCreateLock(BBD, BasisBoneTrackedRole.Head, BasisBoneTrackedRole.Mouth);
            SetAndCreateLock(BBD, BasisBoneTrackedRole.Neck, BasisBoneTrackedRole.Chest);
            SetAndCreateLock(BBD, BasisBoneTrackedRole.Chest, BasisBoneTrackedRole.Spine);
            SetAndCreateLock(BBD, BasisBoneTrackedRole.Spine, BasisBoneTrackedRole.Hips);
        }
        public bool IsAble(BasisRemotePlayer remotePlayer)
        {
            if (IsNull(remotePlayer.BasisAvatar))
            {
                return false;
            }
            if (remotePlayer.RemoteBoneDriver == null)
            {
                return false;
            }
            if (IsNull(remotePlayer.BasisAvatar.Animator))
            {
                return false;
            }
            if (IsNull(remotePlayer))
            {
                return false;
            }
            return true;
        }
        public float ActiveAvatarEyeHeight(BasisAvatar BasisAvatar)
        {
            if (BasisAvatar != null)
            {
                return BasisAvatar.AvatarEyePosition.x;
            }
            else
            {
                return BasisLocalPlayer.FallbackSize;
            }
        }
        public void CalculateTransformPositions(BasisPlayer basisPlayer, BasisRemoteBoneDriver driver)
        {
            Transform Transform = basisPlayer.BasisAvatar.Animator.transform;
            Animator animator = basisPlayer.BasisAvatar.Animator;
            Transform rootTransform = animator.transform;
            float3 Position = Transform.position;
            for (int Index = 0; Index < driver.ControlsLength; Index++)
            {
                var control = driver.Controls[Index];
                var role = driver.trackedRoles[Index];

                switch (driver.trackedRoles[Index])
                {
                    case BasisBoneTrackedRole.CenterEye:
                        {
                            GetWorldSpacePos(BasisHelpers.AvatarPositionConversion(basisPlayer.BasisAvatar.AvatarEyePosition), Position, out float3 world);
                            SetInitialData(rootTransform, control, role, world);
                            break;
                        }

                    case BasisBoneTrackedRole.Mouth:
                        {
                            GetWorldSpacePos(BasisHelpers.AvatarPositionConversion(basisPlayer.BasisAvatar.AvatarMouthPosition), Position, out float3 world);
                            SetInitialData(rootTransform, control, role, world);
                            break;
                        }

                    default:
                        {
                            if (BasisDeviceManagement.Instance.FBBD.FindBone(out BasisFallBone fallback, driver.trackedRoles[Index]))
                            {
                                if (TryConvertToHumanoidRole(driver.trackedRoles[Index], out HumanBodyBones human))
                                {
                                    GetBoneRotAndPos(basisPlayer.transform, basisPlayer.BasisAvatar, human, fallback.PositionPercentage, out quaternion _, out float3 world, out bool _);
                                    SetInitialData(rootTransform, control, role, world);
                                }
                                else
                                {
                                    BasisDebug.LogError("cant Convert to humanbodybone " + driver.trackedRoles[Index]);
                                }
                            }
                            else
                            {
                                BasisDebug.LogError("cant find Fallback Bone for " + driver.trackedRoles[Index]);
                            }

                            break;
                        }
                }
            }
        }
        public void GetWorldSpacePos(Vector3 localAvatarSpace, Vector3 AnimatorPosition, out float3 position)
        {
            position = BasisHelpers.ConvertFromLocalSpace(localAvatarSpace, AnimatorPosition);
        }
        public void GetBoneRotAndPos(Transform driver, BasisAvatar BasisAvatar, HumanBodyBones bone, Vector3 heightPercentage, out quaternion Rotation, out float3 Position, out bool UsedFallback)
        {
            if (BasisAvatar.Animator.avatar != null && BasisAvatar.Animator.avatar.isHuman)
            {
                Transform boneTransform = BasisAvatar.Animator.GetBoneTransform(bone);
                if (boneTransform == null)
                {
                    Rotation = driver.rotation;
                    Position = driver.position;
                    Position += CalculateFallbackOffset(bone, ActiveAvatarEyeHeight(BasisAvatar), heightPercentage);
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
                Rotation = driver.rotation;
                Position = driver.position;
                Position = new Vector3(0, Position.y, 0);
                Position += CalculateFallbackOffset(bone, ActiveAvatarEyeHeight(BasisAvatar), heightPercentage);
                Position = new Vector3(0, Position.y, 0);
                UsedFallback = true;
            }
        }
        public float3 CalculateFallbackOffset(HumanBodyBones bone, float fallbackHeight, float3 heightPercentage)
        {
            Vector3 height = fallbackHeight * heightPercentage;
            return bone == HumanBodyBones.Hips ? math.mul(height, -Vector3.up) : math.mul(height, Vector3.up);
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
        public void SetInitialData(Transform Transform, BasisRemoteBoneControl bone, BasisBoneTrackedRole Role, Vector3 WorldTpose)
        {
            bone.OutGoingData.position = BasisLocalBoneDriver.ConvertToAvatarSpaceInitial(Transform, WorldTpose);
            bone.TposeLocal.position = bone.OutGoingData.position;
            bone.TposeLocal.rotation = bone.OutGoingData.rotation;
            if (IsApartOfSpineVertical(Role))
            {
                bone.OutGoingData.position = new Vector3(0, bone.OutGoingData.position.y, bone.OutGoingData.position.z);
                bone.TposeLocal.position = bone.OutGoingData.position;
            }
            if (Role == BasisBoneTrackedRole.Hips)
            {
                bone.TposeLocal.rotation = quaternion.identity;
            }
            bone.TposeLocalScaled.position = bone.TposeLocal.position;
            bone.TposeLocalScaled.rotation = bone.TposeLocal.rotation;
        }
        public void SetAndCreateLock(BasisRemoteBoneDriver BaseBoneDriver, BasisBoneTrackedRole LockToBoneRole, BasisBoneTrackedRole AssignedTo)
        {
            if (BaseBoneDriver.FindBone(out BasisRemoteBoneControl AssignedToAddToBone, AssignedTo) == false)
            {
                BasisDebug.LogError("Cant Find Bone " + AssignedTo);
            }
            if (BaseBoneDriver.FindBone(out BasisRemoteBoneControl LockToBone, LockToBoneRole) == false)
            {
                BasisDebug.LogError("Cant Find Bone " + LockToBoneRole);
            }
            BaseBoneDriver.CreateRotationalLock(AssignedToAddToBone, LockToBone);
        }
        public void UpdateWhenOffscreenAndDisableMatrixRecal(bool State)
        {
            for (int Index = 0; Index < SkinnedMeshRendererLength; Index++)
            {
                SkinnedMeshRenderer Render = SkinnedMeshRenderer[Index];
                Render.updateWhenOffscreen = State;
                Render.forceMatrixRecalculationPerRender = false;
            }
        }
    }
}
