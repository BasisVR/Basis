using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.TransformBinders.BoneControl;
using System;
using UnityEngine;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.Common;
using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.Device_Management;
using Unity.Mathematics;
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
        public void RemoteCalibration(BasisRemotePlayer remotePlayer)
        {
            if (!IsAble(remotePlayer))
            {
                return;
            }
            else
            {
                //  BasisDebug.Log("RemoteCalibration Underway", BasisDebug.LogTag.Avatar);
            }
            Calibration(remotePlayer.BasisAvatar);
            remotePlayer.EyeFollow.Initalize(this, remotePlayer);
            UpdateWhenOffscreen(false);
            remotePlayer.BasisAvatar.Animator.logWarnings = false;
            for (int Index = 0; Index < SkinnedMeshRenderer.Length; Index++)
            {
                SkinnedMeshRenderer[Index].forceMatrixRecalculationPerRender = false;
            }
            CalculateTransformPositions(remotePlayer, remotePlayer.RemoteBoneDriver);
            ComputeOffsets(remotePlayer.RemoteBoneDriver);
            remotePlayer.BasisAvatar.Animator.enabled = false;
            CalibrationComplete?.Invoke();
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
        public void Calibration(BasisAvatar Avatar)
        {
            SkinnedMeshRenderer = Player.BasisAvatar.Animator.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            SkinnedMeshRendererLength = SkinnedMeshRenderer.Length;
            BasisTransformMapping.AutoDetectReferences(Player.BasisAvatar.Animator, Avatar.transform, ref References);
            References.RecordPoses(Player.BasisAvatar.Animator);
            Player.FaceIsVisible = false;
            if (Avatar == null)
            {
                BasisDebug.LogError("Missing Avatar");
            }
            if (Avatar.FaceVisemeMesh == null)
            {
                BasisDebug.Log("Missing Face for " + Player.DisplayName, BasisDebug.LogTag.Avatar);
            }
            Player.UpdateFaceVisibility(Avatar.FaceVisemeMesh.isVisible);
            if (Player.FaceRenderer != null)
            {
                GameObject.Destroy(Player.FaceRenderer);
            }
            Player.FaceRenderer = BasisHelpers.GetOrAddComponent<BasisMeshRendererCheck>(Avatar.FaceVisemeMesh.gameObject);
            Player.FaceRenderer.Check += Player.UpdateFaceVisibility;

            if (BasisFacialBlinkDriver.MeetsRequirements(Avatar))
            {
                Player.FacialBlinkDriver.Initialize(Player, Avatar);
            }
        }

        public void CalculateTransformPositions(BasisPlayer BasisPlayer, BasisRemoteBoneDriver driver)
        {
            Transform Transform = BasisPlayer.BasisAvatar.Animator.transform;
            float3 bottom = Transform.position;
            for (int Index = 0; Index < driver.ControlsLength; Index++)
            {
                BasisRemoteBoneControl Control = driver.Controls[Index];
                if (driver.trackedRoles[Index] == BasisBoneTrackedRole.CenterEye)
                {
                    GetWorldSpaceRotAndPos(() => Player.BasisAvatar.AvatarEyePosition, bottom, out float3 TposeWorld);
                    SetInitialData(Transform, Control, driver.trackedRoles[Index], TposeWorld);
                }
                else
                {
                    if (driver.trackedRoles[Index] == BasisBoneTrackedRole.Mouth)
                    {
                        GetWorldSpaceRotAndPos(() => Player.BasisAvatar.AvatarMouthPosition, bottom, out float3 TposeWorld);
                        SetInitialData(Transform, Control, driver.trackedRoles[Index], TposeWorld);
                    }
                    else
                    {
                        if (BasisDeviceManagement.FBBD.FindBone(out BasisFallBone FallBackBone, driver.trackedRoles[Index]))
                        {
                            if (TryConvertToHumanoidRole(driver.trackedRoles[Index], out HumanBodyBones HumanBones))
                            {
                                GetBoneRotAndPos(BasisPlayer.transform, BasisPlayer.BasisAvatar, HumanBones, FallBackBone.PositionPercentage, out quaternion Rotation, out float3 TposeWorld, out bool UsedFallback);
                                SetInitialData(Transform, Control, driver.trackedRoles[Index], TposeWorld);
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
                    }
                }
            }
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
        public void GetWorldSpaceRotAndPos(Func<Vector2> positionSelector, float3 bottom, out float3 position)
        {
            Vector3 convertedToVector3 = BasisHelpers.AvatarPositionConversion(positionSelector());
            position = BasisHelpers.ConvertFromLocalSpace(convertedToVector3, bottom);
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
        public void UpdateWhenOffscreen(bool State)
        {
            for (int Index = 0; Index < SkinnedMeshRendererLength; Index++)
            {
                SkinnedMeshRenderer Render = SkinnedMeshRenderer[Index];
                Render.updateWhenOffscreen = State;
            }
        }
    }
}
