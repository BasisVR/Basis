using Basis.Scripts.Avatar;
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
using UnityEngine.Animations.Rigging;

namespace Basis.Scripts.Drivers
{
	[Serializable]
	public class BasisLocalAvatarDriver : BasisAvatarDriver
	{
        public const string TPose = "Assets/Animator/Animated TPose.controller";
        public const string Locomotion = "Locomotion";
        public static Vector3 HeadScale = Vector3.one;
		public static Vector3 HeadScaledDown = Vector3.zero;
		public static bool HasTPoseEvent = false;
		public static float MaxExtendedDistance;
		public static BasisLocalAvatarDriver Instance;
		public static bool IsNormalHead;
		public static bool CurrentlyTposing = false;
		public static Action CalibrationComplete;
		public static Action TposeStateChange;
		public static BasisTransformMapping References = new BasisTransformMapping();
		public static RuntimeAnimatorController SavedruntimeAnimatorController;
		public static SkinnedMeshRenderer[] SkinnedMeshRenderer;
		public static bool HasEvents = false;
		public static List<int> ActiveMatrixOverrides = new List<int>();
		public static int SkinnedMeshRendererLength;
        public Dictionary<BasisBoneTrackedRole, Transform> StoredRolesTransforms = new Dictionary<BasisBoneTrackedRole, Transform>();

        [SerializeField]
		public BasisAvatarScaleModifier ScaleAvatarModification = new BasisAvatarScaleModifier();
        public float currentDistance;
        public Vector3 direction;
        public float overshoot;
        public Vector3 correction;
        public Vector3 output;
        public void InitialLocalCalibration(BasisLocalPlayer player)
		{
			player.CurrentHeight.PickRatio(BasisSelectedHeightMode.EyeHeight);
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

			// Initialize the rig driver
			if (player.LocalRigDriver == null)
			{
				player.LocalRigDriver = new BasisLocalRigDriver();
			}
			player.LocalRigDriver.Initialize(player, BasisLocalPlayer.Instance, References);

			player.LocalRigDriver.CleanupBeforeContinue();
			player.LocalRigDriver.AdditionalTransforms.Clear();
			player.LocalRigDriver.Rigs.Clear();
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
			//tpose
			PutAvatarIntoTPose();

			player.LocalRigDriver.Builder = BasisHelpers.GetOrAddComponent<RigBuilder>(AvatarAnimatorParent);
			player.LocalRigDriver.Builder.enabled = false;
			Calibration(player.BasisAvatar);
			BasisLocalPlayer.Instance.LocalBoneDriver.RemoveAllListeners();
			BasisLocalPlayer.Instance.LocalEyeDriver.Initalize(this, player);
			SetMatrixOverride();
			UpdateWhenOffscreen(true);
			if (References.Hashead)
			{
				HeadScale = References.head.localScale;
			}
			else
			{
				HeadScale = Vector3.one;
			}

			player.LocalRigDriver.SetBodySettings(player.LocalBoneDriver);

			CalculateTransformPositions(player, player.LocalBoneDriver);

			ComputeOffsets(player.LocalBoneDriver);

			player.LocalHandDriver.ReInitialize(player.BasisAvatar.Animator);

			CalibrationComplete?.Invoke();
			player.LocalAnimatorDriver.Initialize(player);
			//stop Tpose
			ResetAvatarAnimator();
			BasisAvatarIKStageCalibration.HasFBIKTrackers = false;
			if (BasisLocalPlayer.Instance.LocalBoneDriver.FindBone(out BasisLocalBoneControl Head, BasisBoneTrackedRole.Head))
			{
				Head.HasRigLayer = BasisHasRigLayer.HasRigLayer;
			}
			if (BasisLocalPlayer.Instance.LocalBoneDriver.FindBone(out BasisLocalBoneControl Hips, BasisBoneTrackedRole.Hips))
			{
				Hips.HasRigLayer = BasisHasRigLayer.HasRigLayer;
			}
			if (BasisLocalPlayer.Instance.LocalBoneDriver.FindBone(out BasisLocalBoneControl Spine, BasisBoneTrackedRole.Spine))
			{
				Spine.HasRigLayer = BasisHasRigLayer.HasRigLayer;
			}
			StoredRolesTransforms = BasisAvatarIKStageCalibration.GetAllRolesAsTransform();
			player.AvatarTransform.parent = player.transform;
			player.AvatarTransform.SetLocalPositionAndRotation(-Hips.TposeLocal.position, Quaternion.identity);
			MaxExtendedDistance = Vector3.Distance(BasisLocalBoneDriver.HeadControl.TposeLocal.position, BasisLocalBoneDriver.HipsControl.TposeLocal.position);
			player.LocalRigDriver.BuildBuilder();
			IsNormalHead = true;
		}

		public static void ScaleHeadToNormal()
		{
			if (IsNormalHead || Instance == null || References.Hashead == false) return;

			References.head.localScale = HeadScale;
			IsNormalHead = true;
		}

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
			if (References.Hashead == false)
			{
				return;
			}
			References.head.localScale = HeadScaledDown;
			IsNormalHead = false;
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

			//legs
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

		public void CalculateMaxExtended()
		{
			MaxExtendedDistance = Vector3.Distance(BasisLocalBoneDriver.HeadControl.TposeLocalScaled.position, BasisLocalBoneDriver.HipsControl.TposeLocalScaled.position);
		}
		public float ActiveAvatarEyeHeight()
		{
			if (BasisLocalPlayer.Instance.BasisAvatar != null)
			{
				return BasisLocalPlayer.Instance.BasisAvatar.AvatarEyePosition.x;
			}
			else
			{
				return BasisLocalPlayer.FallbackSize;
			}
		}

		public void TryActiveMatrixOverride(int InstanceID)
		{
			if (ActiveMatrixOverrides.Contains(InstanceID) == false)
			{
				ActiveMatrixOverrides.Add(InstanceID);
				SetAllMatrixRecalculation(true);
			}
		}

		public void RemoveActiveMatrixOverride(int InstanceID)
		{
			if (ActiveMatrixOverrides.Remove(InstanceID))
			{
				if (ActiveMatrixOverrides.Count == 0)
				{
					SetAllMatrixRecalculation(false);
				}
			}
		}

		public void SetMatrixOverride()
		{
#if UNITY_EDITOR
			SetAllMatrixRecalculation(true);
#else
            if (ActiveMatrixOverrides.Count != 0)
            {
                SetAllMatrixRecalculation(true);
            }
            else
            {
                SetAllMatrixRecalculation(false);
           }
#endif
		}

		public void Calibration(BasisAvatar Avatar)
		{
			FindSkinnedMeshRenders();
			BasisTransformMapping.AutoDetectReferences(BasisLocalPlayer.Instance.BasisAvatar.Animator, Avatar.transform, ref References);
			References.RecordPoses(BasisLocalPlayer.Instance.BasisAvatar.Animator);
            BasisLocalPlayer.Instance.FaceIsVisible = false;
			if (Avatar == null)
			{
				BasisDebug.LogError("Missing Avatar");
			}
			if (Avatar.FaceVisemeMesh == null)
			{
				BasisDebug.Log("Missing Face for " + BasisLocalPlayer.Instance.DisplayName, BasisDebug.LogTag.Avatar);
			}
            BasisLocalPlayer.Instance.UpdateFaceVisibility(Avatar.FaceVisemeMesh.isVisible);
			if (BasisLocalPlayer.Instance.FaceRenderer != null)
			{
				GameObject.Destroy(BasisLocalPlayer.Instance.FaceRenderer);
			}
            BasisLocalPlayer.Instance.FaceRenderer = BasisHelpers.GetOrAddComponent<BasisMeshRendererCheck>(Avatar.FaceVisemeMesh.gameObject);
            BasisLocalPlayer.Instance.FaceRenderer.Check += BasisLocalPlayer.Instance.UpdateFaceVisibility;

			if (BasisFacialBlinkDriver.MeetsRequirements(Avatar))
			{
                BasisLocalPlayer.Instance.FacialBlinkDriver.Initialize(BasisLocalPlayer.Instance, Avatar);
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
			UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle<RuntimeAnimatorController> op = Addressables.LoadAssetAsync<RuntimeAnimatorController>(TPose);
			RuntimeAnimatorController RAC = op.WaitForCompletion();
            BasisLocalPlayer.Instance.BasisAvatar.Animator.runtimeAnimatorController = RAC;
			ForceUpdateAnimator(BasisLocalPlayer.Instance.BasisAvatar.Animator);
			BasisDeviceManagement.UnassignFBTrackers();
			TposeStateChange?.Invoke();
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
			Vector3 Position = rootTransform.position;
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
                            GetWorldSpacePos(BasisHelpers.AvatarPositionConversion(basisPlayer.BasisAvatar.AvatarEyePosition), Position, out float3 world);
                            SetInitialData(rootTransform, control, role, world);
                            break;
                        }

                    case BasisBoneTrackedRole.Mouth:
                        {
                            // Convert avatar-local mouth position to world and apply
                            GetWorldSpacePos(BasisHelpers.AvatarPositionConversion(basisPlayer.BasisAvatar.AvatarMouthPosition), Position, out float3 world);
                            SetInitialData(rootTransform, control, role, world);
                            break;
                        }

                    default:
                        {
                            // Use fallback DB + humanoid mapping
                            if (fbdb.FindBone(out BasisFallBone fallback, role))
                            {
                                if (TryConvertToHumanoidRole(role, out HumanBodyBones human))
                                {
                                    GetBoneRotAndPos(basisPlayer.transform,animator,human, fallback.PositionPercentage,out quaternion _,out float3 world,out bool _);

                                    SetInitialData(rootTransform, control, role, world);
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
        public void GetWorldSpacePos(Vector3 localAvatarSpace,Vector3 AnimatorPosition, out float3 position)
        {
            position = BasisHelpers.ConvertFromLocalSpace(localAvatarSpace, AnimatorPosition);
        }

        public void GetBoneRotAndPos(Transform driver, Animator anim, HumanBodyBones bone, Vector3 heightPercentage, out quaternion Rotation, out float3 Position, out bool UsedFallback)
		{
			if (anim.avatar != null && anim.avatar.isHuman)
			{
				Transform boneTransform = anim.GetBoneTransform(bone);
				if (boneTransform == null)
				{
					Rotation = driver.rotation;
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
				Rotation = driver.rotation;
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

		public void SetInitialData(Transform Transform, BasisLocalBoneControl bone, BasisBoneTrackedRole Role, Vector3 WorldTpose)
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

		public void FindSkinnedMeshRenders()
		{
			SkinnedMeshRenderer = BasisLocalPlayer.Instance.BasisAvatar.Animator.GetComponentsInChildren<SkinnedMeshRenderer>(true);
			SkinnedMeshRendererLength = SkinnedMeshRenderer.Length;
		}

		public void SetAllMatrixRecalculation(bool State)
		{
			for (int Index = 0; Index < SkinnedMeshRendererLength; Index++)
			{
				SkinnedMeshRenderer Render = SkinnedMeshRenderer[Index];
				if (Render != null)
				{
					Render.forceMatrixRecalculationPerRender = State;
				}
			}
		}

		public void UpdateWhenOffscreen(bool State)
		{
			for (int Index = 0; Index < SkinnedMeshRendererLength; Index++)
			{
				SkinnedMeshRenderer Render = SkinnedMeshRenderer[Index];
				if (Render != null)
				{
					Render.updateWhenOffscreen = State;
				}
			}
		}
        // ─────────────────────────────────────────────────────────────────────────────
        // Root motion placement based on head/hips spread
        // ─────────────────────────────────────────────────────────────────────────────
        public Quaternion MoveAvatar(BasisAvatar basisAvatar)
        {
            if (basisAvatar == null)
                return Quaternion.identity;

            // World positions pulled from outgoing driver data
            Vector3 headPosition = BasisLocalBoneDriver.HeadControl.OutgoingWorldData.position;
            Vector3 hipsPosition = BasisLocalBoneDriver.HipsControl.OutgoingWorldData.position;
            Quaternion parentWorldRotation = BasisLocalBoneDriver.HipsControl.OutgoingWorldData.rotation;

            currentDistance = Vector3.Distance(headPosition, hipsPosition);

            // XZ blend between hips and head, grounded at hips Y for stability
            Vector3 blendedXZ = Vector3.Lerp(hipsPosition, headPosition, 0.5f);
            blendedXZ.y = hipsPosition.y;

            // Compute local-space offset we want to apply under the parent rotation
            if (currentDistance <= MaxExtendedDistance)
            {
                // Within expected range: use T-Pose hips local offset
                output = -BasisLocalBoneDriver.HipsControl.TposeLocalScaled.position;
                direction = Vector3.zero;
                overshoot = 0f;
                correction = Vector3.zero;
            }
            else
            {
                // Extend beyond T-Pose span: push back by overshoot
                direction = (hipsPosition - headPosition).normalized;
                overshoot = currentDistance - MaxExtendedDistance;
                correction = direction * overshoot;

                Vector3 tposeHips = BasisLocalBoneDriver.HipsControl.TposeLocalScaled.position;
                float3 correctedHips = tposeHips + correction;
                output = -correctedHips;
            }

            // Place child at blendedXZ plus rotated local offset
            Vector3 childWorldPosition = blendedXZ + parentWorldRotation * output;

            basisAvatar.transform.SetPositionAndRotation(childWorldPosition, parentWorldRotation);
            return parentWorldRotation;
        }
    }
}
