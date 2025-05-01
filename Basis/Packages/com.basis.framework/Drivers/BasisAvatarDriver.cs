
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Device_Management;
using Basis.Scripts.TransformBinders.BoneControl;
using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace Basis.Scripts.Drivers
{
    [Serializable]
    public abstract class BasisAvatarDriver
    {
        private static readonly Dictionary<HumanBodyBones, BasisBoneTrackedRole> BoneToRoleMap = new()
        {
            { HumanBodyBones.Head, BasisBoneTrackedRole.Head },
            { HumanBodyBones.Neck, BasisBoneTrackedRole.Neck },
            { HumanBodyBones.Chest, BasisBoneTrackedRole.Chest },
            { HumanBodyBones.Hips, BasisBoneTrackedRole.Hips },
            { HumanBodyBones.Spine, BasisBoneTrackedRole.Spine },
            { HumanBodyBones.LeftUpperLeg, BasisBoneTrackedRole.LeftUpperLeg },
            { HumanBodyBones.RightUpperLeg, BasisBoneTrackedRole.RightUpperLeg },
            { HumanBodyBones.LeftLowerLeg, BasisBoneTrackedRole.LeftLowerLeg },
            { HumanBodyBones.RightLowerLeg, BasisBoneTrackedRole.RightLowerLeg },
            { HumanBodyBones.LeftFoot, BasisBoneTrackedRole.LeftFoot },
            { HumanBodyBones.RightFoot, BasisBoneTrackedRole.RightFoot },
            { HumanBodyBones.LeftShoulder, BasisBoneTrackedRole.LeftShoulder },
            { HumanBodyBones.RightShoulder, BasisBoneTrackedRole.RightShoulder },
            { HumanBodyBones.LeftUpperArm, BasisBoneTrackedRole.LeftUpperArm },
            { HumanBodyBones.RightUpperArm, BasisBoneTrackedRole.RightUpperArm },
            { HumanBodyBones.LeftLowerArm, BasisBoneTrackedRole.LeftLowerArm },
            { HumanBodyBones.RightLowerArm, BasisBoneTrackedRole.RightLowerArm },
            { HumanBodyBones.LeftHand, BasisBoneTrackedRole.LeftHand },
            { HumanBodyBones.RightHand, BasisBoneTrackedRole.RightHand },
            { HumanBodyBones.LeftToes, BasisBoneTrackedRole.LeftToes },
            { HumanBodyBones.RightToes, BasisBoneTrackedRole.RightToes },
            { HumanBodyBones.Jaw, BasisBoneTrackedRole.Mouth }
        };

        private static readonly Dictionary<BasisBoneTrackedRole, HumanBodyBones> RoleToBoneMap = new();

        static BasisAvatarDriver()
        {
            foreach (var pair in BoneToRoleMap)
                RoleToBoneMap[pair.Value] = pair.Key;
        }

        public static bool TryConvertToBoneTrackingRole(HumanBodyBones body, out BasisBoneTrackedRole result) =>
            BoneToRoleMap.TryGetValue(body, out result);

        public static bool TryConvertToHumanoidRole(BasisBoneTrackedRole role, out HumanBodyBones result) =>
            RoleToBoneMap.TryGetValue(role, out result);

        public static bool IsApartOfSpineVertical(BasisBoneTrackedRole role) =>
            role is BasisBoneTrackedRole.Hips or BasisBoneTrackedRole.Chest or BasisBoneTrackedRole.Spine
              or BasisBoneTrackedRole.CenterEye or BasisBoneTrackedRole.Mouth or BasisBoneTrackedRole.Head;

        public float ActiveAvatarEyeHeight() =>
            BasisLocalPlayer.Instance.BasisAvatar?.AvatarEyePosition.x ?? BasisLocalPlayer.FallbackSize;

        public Action CalibrationComplete;
        public Action TposeStateChange;
        public BasisTransformMapping References = new();
        public RuntimeAnimatorController SavedruntimeAnimatorController;
        public SkinnedMeshRenderer[] SkinnedMeshRenderer;
        public BasisPlayer Player;
        public bool CurrentlyTposing = false;
        public bool HasEvents = false;
        public List<int> ActiveMatrixOverrides = new();
        public int SkinnedMeshRendererLength;

        private static readonly string TPose = "Assets/Animator/Animated TPose.controller";

        public void TryActiveMatrixOverride(int id)
        {
            if (!ActiveMatrixOverrides.Contains(id))
            {
                ActiveMatrixOverrides.Add(id);
                SetAllMatrixRecalculation(true);
            }
        }

        public void RemoveActiveMatrixOverride(int id)
        {
            if (ActiveMatrixOverrides.Remove(id) && ActiveMatrixOverrides.Count == 0)
                SetAllMatrixRecalculation(false);
        }

        public void SetMatrixOverride()
        {
#if UNITY_EDITOR
            SetAllMatrixRecalculation(true);
#else
            SetAllMatrixRecalculation(ActiveMatrixOverrides.Count != 0);
#endif
        }

        public void Calibration(BasisAvatar avatar)
        {
            FindSkinnedMeshRenders();
            BasisTransformMapping.AutoDetectReferences(Player.BasisAvatar.Animator, avatar.transform, ref References);
            Player.FaceIsVisible = false;

            if (avatar == null)
            {
                BasisDebug.LogError("Missing Avatar");
                return;
            }

            if (avatar.FaceVisemeMesh == null)
                BasisDebug.Log($"Missing Face for {Player.DisplayName}", BasisDebug.LogTag.Avatar);

            Player.UpdateFaceVisibility(avatar.FaceVisemeMesh?.isVisible ?? false);

            if (Player.FaceRenderer != null)
                GameObject.Destroy(Player.FaceRenderer);

            Player.FaceRenderer = BasisHelpers.GetOrAddComponent<BasisMeshRendererCheck>(avatar.FaceVisemeMesh.gameObject);
            Player.FaceRenderer.Check += Player.UpdateFaceVisibility;

            if (BasisFacialBlinkDriver.MeetsRequirements(avatar))
                Player.FacialBlinkDriver.Initialize(Player, avatar);
        }

        public void PutAvatarIntoTPose()
        {
            CurrentlyTposing = true;

            if (SavedruntimeAnimatorController == null)
                SavedruntimeAnimatorController = Player.BasisAvatar.Animator.runtimeAnimatorController;

            var op = Addressables.LoadAssetAsync<RuntimeAnimatorController>(TPose);
            var controller = op.WaitForCompletion();
            Player.BasisAvatar.Animator.runtimeAnimatorController = controller;
            ForceUpdateAnimator(Player.BasisAvatar.Animator);

            BasisDeviceManagement.UnassignFBTrackers();
            TposeStateChange?.Invoke();
        }

        public void ResetAvatarAnimator()
        {
            Player.BasisAvatar.Animator.runtimeAnimatorController = SavedruntimeAnimatorController;
            SavedruntimeAnimatorController = null;
            CurrentlyTposing = false;
            TposeStateChange?.Invoke();
        }

        public void FindSkinnedMeshRenders()
        {
            SkinnedMeshRenderer = Player.BasisAvatar.Animator.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            SkinnedMeshRendererLength = SkinnedMeshRenderer.Length;
        }

        public void SetAllMatrixRecalculation(bool state)
        {
            for (int i = 0; i < SkinnedMeshRendererLength; i++)
                SkinnedMeshRenderer[i].forceMatrixRecalculationPerRender = state;
        }

        public void updateWhenOffscreen(bool state)
        {
            for (int i = 0; i < SkinnedMeshRendererLength; i++)
                SkinnedMeshRenderer[i].updateWhenOffscreen = state;
        }
        public void CalculateTransformPositions(BasisPlayer player, BasisBaseBoneDriver driver)
        {
            for (int i = 0; i < driver.ControlsLength; i++)
            {
                var role = driver.trackedRoles[i];
                var control = driver.Controls[i];

                if (role == BasisBoneTrackedRole.CenterEye)
                {
                    GetWorldSpaceRotAndPos(() => player.BasisAvatar.AvatarEyePosition, out float3 eyePos);
                    SetInitialData(player.BasisAvatar.Animator, control, role, eyePos);
                }
                else if (role == BasisBoneTrackedRole.Mouth)
                {
                    GetWorldSpaceRotAndPos(() => player.BasisAvatar.AvatarMouthPosition, out float3 mouthPos);
                    SetInitialData(player.BasisAvatar.Animator, control, role, mouthPos);
                }
                else if (BasisDeviceManagement.FBBD.FindBone(out var fallback, role) &&
                         TryConvertToHumanoidRole(role, out var humanoid))
                {
                    GetBoneRotAndPos(player.transform, player.BasisAvatar.Animator, humanoid,
                        fallback.PositionPercentage, out var rot, out float3 tPose, out _);
                    SetInitialData(player.BasisAvatar.Animator, control, role, tPose);
                }
                else
                {
                    BasisDebug.LogError($"Unable to resolve fallback or humanoid bone for {role}");
                }
            }
        }

        public void GetBoneRotAndPos(Transform driver, Animator anim, HumanBodyBones bone, Vector3 heightPct,
                                     out quaternion rotation, out float3 position, out bool usedFallback)
        {
            usedFallback = false;

            if (anim.avatar?.isHuman == true)
            {
                var boneTransform = anim.GetBoneTransform(bone);
                if (boneTransform != null)
                {
                    boneTransform.GetPositionAndRotation(out var pos, out var rot);
                    rotation = rot;
                    position = pos;
                    return;
                }
                usedFallback = true;
            }
            else
            {
                usedFallback = true;
            }

            rotation = driver.rotation;
            position = anim.transform.position;
            position += CalculateFallbackOffset(bone, ActiveAvatarEyeHeight(), heightPct);
        }

        public float3 CalculateFallbackOffset(HumanBodyBones bone, float height, float3 heightPct)
        {
            var scaled = height * heightPct;
            return bone == HumanBodyBones.Hips ? math.mul(scaled, -Vector3.up) : math.mul(scaled, Vector3.up);
        }

        public void GetWorldSpaceRotAndPos(Func<Vector2> selector, out float3 position)
        {
            var basePos = Player.BasisAvatar.Animator.transform.position;
            var localVec3 = BasisHelpers.AvatarPositionConversion(selector());
            position = BasisHelpers.ConvertFromLocalSpace(localVec3, basePos);
        }

        public void ForceUpdateAnimator(Animator animator) =>
            animator.Update(Time.deltaTime);

        public bool IsNull(UnityEngine.Object obj)
        {
            if (obj == null)
            {
                BasisDebug.LogError("Missing Object during calibration");
                return true;
            }
            return false;
        }

        public void SetInitialData(Animator animator, BasisBoneControl bone, BasisBoneTrackedRole role, Vector3 tPoseWorld)
        {
            bone.OutGoingData.position = BasisLocalBoneDriver.ConvertToAvatarSpaceInitial(animator, tPoseWorld);
            bone.TposeLocal.position = bone.OutGoingData.position;
            bone.TposeLocal.rotation = bone.OutGoingData.rotation;

            if (IsApartOfSpineVertical(role))
                bone.TposeLocal.position = new Vector3(0, bone.TposeLocal.position.y, bone.TposeLocal.position.z);

            if (role == BasisBoneTrackedRole.Hips)
                bone.TposeLocal.rotation = quaternion.identity;
        }

        public void SetAndCreateLock(BasisBaseBoneDriver driver, BasisBoneTrackedRole lockTo, BasisBoneTrackedRole assignTo,
                                     float posLerp, float rotLerp, bool create = true)
        {
            if (!create) return;

            if (!driver.FindBone(out var assign, assignTo))
                BasisDebug.LogError($"Can't find bone {assignTo}");

            if (!driver.FindBone(out var target, lockTo))
                BasisDebug.LogError($"Can't find lock target {lockTo}");

            driver.CreateRotationalLock(assign, target, posLerp, rotLerp);
        }

        public Bounds GetBounds(Transform animatorParent)
        {
            var renderers = animatorParent.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
                return new Bounds(Vector3.zero, new Vector3(0.3f, BasisLocalPlayer.FallbackSize, 0.3f));

            var bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                bounds.Encapsulate(renderers[i].bounds);
            return bounds;
        }
    }
}
