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
		public static Vector3 HeadScale = Vector3.one;
		public static Vector3 HeadScaledDown = Vector3.zero;

		//public BasisLocalRigDriver RigDriver;

		public bool HasTPoseEvent = false;
		public string Locomotion = "Locomotion";
		public float MaxExtendedDistance;
		public static BasisLocalAvatarDriver Instance;
		public static bool IsNormalHead;
		public Dictionary<BasisBoneTrackedRole, Transform> StoredRolesTransforms;

		public static bool CurrentlyTposing = false;

		[SerializeField]
		public BasisScaleAvatarModification ScaleAvatarModification = new BasisScaleAvatarModification();

		[Serializable]
		public class BasisScaleAvatarModification
		{
			/// <summary>
			/// set during calibration
			/// </summary>
			public Vector3 DuringCalibrationScale = Vector3.one;
			/// <summary>
			/// Set Scale
			/// </summary>
			public float ApplyScale;
			/// <summary>
			/// Final Scale is Set Scale * DuringCalibrationScale
			/// </summary>
			public Vector3 FinalScale = Vector3.one;
			public void ReInitalize(Animator Animator)
			{
				DuringCalibrationScale = Animator.transform.localScale;
				ApplyScale = 1;
				FinalScale = DuringCalibrationScale;
			}
			public void SetAvatarheightOverride(float Scale)
			{
				ApplyScale = Scale;
				// Final scale = Default scale * Override scale (component-wise)
				FinalScale = DuringCalibrationScale * Scale;
				if (BasisLocalPlayer.Instance.BasisAvatar != null)
				{
					BasisLocalPlayer.Instance.BasisAvatar.transform.localScale = FinalScale;
				}
			}
		}

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
			Player = player;
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
			player.LocalRigDriver.Initialize(player, Player, References);

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
			player.BasisAvatarTransform.parent = player.transform;
			player.BasisAvatarTransform.SetLocalPositionAndRotation(-Hips.TposeLocal.position, Quaternion.identity);
			MaxExtendedDistance = Vector3.Distance(BasisLocalBoneDriver.Head.TposeLocal.position, BasisLocalBoneDriver.Hips.TposeLocal.position);
			player.LocalRigDriver.BuildBuilder();
			IsNormalHead = true;
		}

		public static void ScaleHeadToNormal()
		{
			if (IsNormalHead || Instance == null || Instance.References.Hashead == false) return;

			Instance.References.head.localScale = HeadScale;
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
			if (Instance.References.Hashead == false)
			{
				return;
			}
			Instance.References.head.localScale = HeadScaledDown;
			IsNormalHead = false;
		}

		public void ComputeOffsets(BasisLocalBoneDriver BaseBoneDriver)
		{
			SetAndCreateLock(BaseBoneDriver, BasisBoneTrackedRole.CenterEye, BasisBoneTrackedRole.Head, 40, 35, true);
			SetAndCreateLock(BaseBoneDriver, BasisBoneTrackedRole.Head, BasisBoneTrackedRole.Neck, 40, 35, true);
			SetAndCreateLock(BaseBoneDriver, BasisBoneTrackedRole.Head, BasisBoneTrackedRole.Mouth, 40, 30, true);

			SetAndCreateLock(BaseBoneDriver, BasisBoneTrackedRole.Neck, BasisBoneTrackedRole.Chest, 40, 30, true);

			SetAndCreateLock(BaseBoneDriver, BasisBoneTrackedRole.Chest, BasisBoneTrackedRole.Spine, 40, 14, true);
			SetAndCreateLock(BaseBoneDriver, BasisBoneTrackedRole.Spine, BasisBoneTrackedRole.Hips, 40, 14, true);

			SetAndCreateLock(BaseBoneDriver, BasisBoneTrackedRole.Chest, BasisBoneTrackedRole.LeftShoulder, 40, 14, true);
			SetAndCreateLock(BaseBoneDriver, BasisBoneTrackedRole.Chest, BasisBoneTrackedRole.RightShoulder, 40, 14, true);

			SetAndCreateLock(BaseBoneDriver, BasisBoneTrackedRole.LeftShoulder, BasisBoneTrackedRole.LeftUpperArm, 40, 14, true);
			SetAndCreateLock(BaseBoneDriver, BasisBoneTrackedRole.RightShoulder, BasisBoneTrackedRole.RightUpperArm, 40, 14, true);

			SetAndCreateLock(BaseBoneDriver, BasisBoneTrackedRole.LeftUpperArm, BasisBoneTrackedRole.LeftLowerArm, 40, 14, true);
			SetAndCreateLock(BaseBoneDriver, BasisBoneTrackedRole.RightUpperArm, BasisBoneTrackedRole.RightLowerArm, 40, 14, true);

			SetAndCreateLock(BaseBoneDriver, BasisBoneTrackedRole.LeftLowerArm, BasisBoneTrackedRole.LeftHand, 40, 14, true);
			SetAndCreateLock(BaseBoneDriver, BasisBoneTrackedRole.RightLowerArm, BasisBoneTrackedRole.RightHand, 40, 14, true);

			//legs
			SetAndCreateLock(BaseBoneDriver, BasisBoneTrackedRole.Hips, BasisBoneTrackedRole.LeftUpperLeg, 40, 14, true);
			SetAndCreateLock(BaseBoneDriver, BasisBoneTrackedRole.Hips, BasisBoneTrackedRole.RightUpperLeg, 40, 14, true);

			SetAndCreateLock(BaseBoneDriver, BasisBoneTrackedRole.LeftUpperLeg, BasisBoneTrackedRole.LeftLowerLeg, 40, 14, true);
			SetAndCreateLock(BaseBoneDriver, BasisBoneTrackedRole.RightUpperLeg, BasisBoneTrackedRole.RightLowerLeg, 40, 14, true);

			SetAndCreateLock(BaseBoneDriver, BasisBoneTrackedRole.LeftLowerLeg, BasisBoneTrackedRole.LeftFoot, 40, 14, true);
			SetAndCreateLock(BaseBoneDriver, BasisBoneTrackedRole.RightLowerLeg, BasisBoneTrackedRole.RightFoot, 40, 14, true);

			SetAndCreateLock(BaseBoneDriver, BasisBoneTrackedRole.LeftFoot, BasisBoneTrackedRole.LeftToes, 40, 14, true);
			SetAndCreateLock(BaseBoneDriver, BasisBoneTrackedRole.RightFoot, BasisBoneTrackedRole.RightToes, 4, 14, true);
		}

		public bool IsAble()
		{
			if (IsNull(Player))
			{
				return false;
			}
			if (IsNull(Player.BasisAvatar))
			{
				return false;
			}
			if (IsNull(Player.BasisAvatar.Animator))
			{
				return false;
			}
			return true;
		}

		public void CalculateMaxExtended()
		{
			MaxExtendedDistance = Vector3.Distance(BasisLocalBoneDriver.Head.TposeLocalScaled.position, BasisLocalBoneDriver.Hips.TposeLocalScaled.position);
		}

		public Action CalibrationComplete;
		public Action TposeStateChange;
		[SerializeField]
		public BasisTransformMapping References = new BasisTransformMapping();
		public RuntimeAnimatorController SavedruntimeAnimatorController;
		public SkinnedMeshRenderer[] SkinnedMeshRenderer;
		public BasisPlayer Player;
		public bool HasEvents = false;
		public List<int> ActiveMatrixOverrides = new List<int>();
		public int SkinnedMeshRendererLength;

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
			BasisDeviceManagement.UnassignFBTrackers();
			TposeStateChange?.Invoke();
		}

		public void ResetAvatarAnimator()
		{
			BasisDebug.Log("ResetAvatarAnimator", BasisDebug.LogTag.Avatar);
			Player.BasisAvatar.Animator.runtimeAnimatorController = SavedruntimeAnimatorController;
			SavedruntimeAnimatorController = null;
			CurrentlyTposing = false;
			TposeStateChange?.Invoke();
		}

		public void CalculateTransformPositions(BasisPlayer BasisPlayer, BasisLocalBoneDriver driver)
		{
			//  BasisDebug.Log("CalculateTransformPositions", BasisDebug.LogTag.Avatar);
			Transform Transform = BasisPlayer.BasisAvatar.Animator.transform;
			for (int Index = 0; Index < driver.ControlsLength; Index++)
			{
				BasisLocalBoneControl Control = driver.Controls[Index];
				if (driver.trackedRoles[Index] == BasisBoneTrackedRole.CenterEye)
				{
					GetWorldSpaceRotAndPos(() => Player.BasisAvatar.AvatarEyePosition, out float3 TposeWorld);
					SetInitialData(Transform, Control, driver.trackedRoles[Index], TposeWorld);
				}
				else
				{
					if (driver.trackedRoles[Index] == BasisBoneTrackedRole.Mouth)
					{
						GetWorldSpaceRotAndPos(() => Player.BasisAvatar.AvatarMouthPosition, out float3 TposeWorld);
						SetInitialData(Transform, Control, driver.trackedRoles[Index], TposeWorld);
					}
					else
					{
						if (BasisDeviceManagement.FBBD.FindBone(out BasisFallBone FallBackBone, driver.trackedRoles[Index]))
						{
							if (TryConvertToHumanoidRole(driver.trackedRoles[Index], out HumanBodyBones HumanBones))
							{
								GetBoneRotAndPos(BasisPlayer.transform, BasisPlayer.BasisAvatar.Animator, HumanBones, FallBackBone.PositionPercentage, out quaternion Rotation, out float3 TposeWorld, out bool UsedFallback);
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

		public void GetWorldSpaceRotAndPos(Func<Vector2> positionSelector, out float3 position)
		{
			float3 bottom = Player.BasisAvatar.Animator.transform.position;
			Vector3 convertedToVector3 = BasisHelpers.AvatarPositionConversion(positionSelector());
			position = BasisHelpers.ConvertFromLocalSpace(convertedToVector3, bottom);
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

		public void SetAndCreateLock(BasisLocalBoneDriver BaseBoneDriver, BasisBoneTrackedRole LockToBoneRole, BasisBoneTrackedRole AssignedTo, float PositionLerpAmount, float QuaternionLerpAmount, bool CreateLocks = true)
		{
			if (CreateLocks)
			{
				if (BaseBoneDriver.FindBone(out BasisLocalBoneControl AssignedToAddToBone, AssignedTo) == false)
				{
					BasisDebug.LogError("Cant Find Bone " + AssignedTo);
				}
				if (BaseBoneDriver.FindBone(out BasisLocalBoneControl LockToBone, LockToBoneRole) == false)
				{
					BasisDebug.LogError("Cant Find Bone " + LockToBoneRole);
				}
				BaseBoneDriver.CreateRotationalLock(AssignedToAddToBone, LockToBone, PositionLerpAmount, QuaternionLerpAmount);
			}
		}

		public void FindSkinnedMeshRenders()
		{
			SkinnedMeshRenderer = Player.BasisAvatar.Animator.GetComponentsInChildren<SkinnedMeshRenderer>(true);
			SkinnedMeshRendererLength = SkinnedMeshRenderer.Length;
		}

		public void SetAllMatrixRecalculation(bool State)
		{
			for (int Index = 0; Index < SkinnedMeshRendererLength; Index++)
			{
				SkinnedMeshRenderer Render = SkinnedMeshRenderer[Index];
				Render.forceMatrixRecalculationPerRender = State;
			}
		}

		public void UpdateWhenOffscreen(bool State)
		{
			for (int Index = 0; Index < SkinnedMeshRendererLength; Index++)
			{
				SkinnedMeshRenderer Render = SkinnedMeshRenderer[Index];
				Render.updateWhenOffscreen = State;
			}
		}

		// Delegate methods to RigDriver
		public void SimulateIKDestinations(Quaternion Rotation, BasisLocalRigDriver localRigDriver)
		{
			localRigDriver?.SimulateIKDestinations(Rotation);
		}

		public void SimulateAnimatorAndIk(float DeltaTime, BasisLocalRigDriver localRigDriver)
		{
			localRigDriver?.SimulateAnimatorAndIk(DeltaTime);
		}

		public void CalibrateRoles(BasisLocalRigDriver localRigDriver)
		{
			localRigDriver?.CalibrateRoles();
		}

		public void ApplyHint(BasisBoneTrackedRole RoleWithHint, bool weight, BasisLocalRigDriver localRigDriver)
		{
			localRigDriver?.ApplyHint(RoleWithHint, weight);
		}
	}
}
