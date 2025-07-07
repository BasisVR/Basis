using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Device_Management;
using Basis.Scripts.TransformBinders.BoneControl;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Animations.Rigging;
using UnityEngine.Playables;

namespace Basis.Scripts.Drivers
{
	[Serializable]
	public class BasisLocalRigDriver
	{
		public BasisTwoBoneIKConstraint HeadTwoBoneIK;
		public BasisTwoBoneIKConstraint LeftFootTwoBoneIK;
		public BasisTwoBoneIKConstraint RightFootTwoBoneIK;
		public BasisTwoBoneIKConstraintHand LeftHandTwoBoneIK;
		public BasisTwoBoneIKConstraintHand RightHandTwoBoneIK;
		public BasisTwoBoneIKConstraint UpperChestTwoBoneIK;

		public BasisApplyTranslation LeftToeConstraint;
		public BasisApplyTranslation RightToeConstraint;

		public Rig LeftToeRig;
		public Rig RightToeRig;

		public Rig RigSpineRig;
		public Rig RigHeadRig;
		public Rig LeftHandRig;
		public Rig RightHandRig;
		public Rig LeftFootRig;
		public Rig RightFootRig;
		public Rig ChestSpineRig;
		public Rig LeftShoulderRig;
		public Rig RightShoulderRig;

		public RigLayer LeftHandLayer;
		public RigLayer RightHandLayer;
		public RigLayer LeftFootLayer;
		public RigLayer RightFootLayer;
		public RigLayer LeftToeLayer;
		public RigLayer RightToeLayer;

		public RigLayer RigHeadLayer;
		public RigLayer RigSpineLayer;
		public RigLayer ChestSpineLayer;

		public RigLayer LeftShoulderLayer;
		public RigLayer RightShoulderLayer;
		public List<Rig> Rigs = new List<Rig>();
		public RigBuilder Builder;
		public List<RigTransform> AdditionalTransforms = new List<RigTransform>();
		public PlayableGraph PlayableGraph;

		private BasisPlayer player;
		private BasisLocalPlayer localPlayer;
		private BasisTransformMapping references;

		public void Initialize(BasisLocalPlayer localPlayer, BasisPlayer player, BasisTransformMapping references)
		{
			this.localPlayer = localPlayer;
			this.player = player;
			this.references = references;
		}
        public void SimulateIKDestinations(Quaternion Rotation, float DeltaTime)
		{
			// --- IK Target ---
			ApplyBoneIKTarget(HeadTwoBoneIK, BasisLocalBoneDriver.HeadControl.OutgoingWorldData.position, BasisLocalBoneDriver.HeadControl.OutgoingWorldData.rotation);
			ApplyBoneIKTarget(LeftFootTwoBoneIK, BasisLocalBoneDriver.LeftFootControl.OutgoingWorldData.position, BasisLocalBoneDriver.LeftFootControl.OutgoingWorldData.rotation);
			ApplyBoneIKTarget(RightFootTwoBoneIK, BasisLocalBoneDriver.RightFootControl.OutgoingWorldData.position, BasisLocalBoneDriver.RightFootControl.OutgoingWorldData.rotation);
			ApplyBoneIKTarget(LeftHandTwoBoneIK, BasisLocalBoneDriver.LeftHandControl.OutgoingWorldData.position, BasisLocalBoneDriver.LeftHandControl.OutgoingWorldData.rotation);
			ApplyBoneIKTarget(RightHandTwoBoneIK, BasisLocalBoneDriver.RightHandControl.OutgoingWorldData.position, BasisLocalBoneDriver.RightHandControl.OutgoingWorldData.rotation);

			ApplyBoneIKTarget(LeftToeConstraint, BasisLocalBoneDriver.LeftToeControl.OutgoingWorldData.position, BasisLocalBoneDriver.LeftToeControl.OutgoingWorldData.rotation);
			ApplyBoneIKTarget(RightToeConstraint, BasisLocalBoneDriver.RightToeControl.OutgoingWorldData.position, BasisLocalBoneDriver.RightToeControl.OutgoingWorldData.rotation);

			Vector3 Direction = Rotation * Vector3.right;
			// --- IK Hint ---
			ApplyBoneIKHint(HeadTwoBoneIK, BasisLocalBoneDriver.ChestControl.OutgoingWorldData.position, BasisLocalBoneDriver.ChestControl.OutgoingWorldData.rotation, Direction);

			ApplyBoneIKHint(LeftFootTwoBoneIK, BasisLocalBoneDriver.LeftLowerLegControl.OutgoingWorldData.position, BasisLocalBoneDriver.LeftLowerLegControl.OutgoingWorldData.rotation, Direction);
			ApplyBoneIKHint(RightFootTwoBoneIK, BasisLocalBoneDriver.RightLowerLegControl.OutgoingWorldData.position, BasisLocalBoneDriver.RightLowerLegControl.OutgoingWorldData.rotation, Direction);

			ApplyBoneIKHint(LeftHandTwoBoneIK, BasisLocalBoneDriver.LeftLowerArmControl.OutgoingWorldData.position, BasisLocalBoneDriver.LeftLowerArmControl.OutgoingWorldData.rotation);
			ApplyBoneIKHint(RightHandTwoBoneIK, BasisLocalBoneDriver.RightLowerArmControl.OutgoingWorldData.position, BasisLocalBoneDriver.RightLowerArmControl.OutgoingWorldData.rotation);
            // --- Do IK on animator ---
            Builder.SyncLayers();
            PlayableGraph.Evaluate(DeltaTime);
        }

		public void ApplyBoneIKHint(BasisTwoBoneIKConstraint Constraint, Vector3 Position, Quaternion Rotation, Vector3 Direction)
		{
			Constraint.data.HintPosition = Position;
			Constraint.data.HintRotation = Rotation.eulerAngles;
			Constraint.data.m_HintDirection = Direction;
		}

		public void ApplyBoneIKHint(BasisTwoBoneIKConstraintHand Constraint, Vector3 Position, Quaternion Rotation)
		{
			Constraint.data.HintPosition = Position;
			Constraint.data.HintRotation = Rotation.eulerAngles;
		}

		public void ApplyBoneIKTarget(BasisTwoBoneIKConstraint Constraint, Vector3 Position, Quaternion Rotation)
		{
			Constraint.data.TargetPosition = Position;
			Constraint.data.TargetRotation = Rotation.eulerAngles;
		}

		public void ApplyBoneIKTarget(BasisApplyTranslation basisDamped, Vector3 Position, Quaternion Rotation)
		{
			basisDamped.data.TargetPosition = Position;
			basisDamped.data.TargetRotation = Rotation.eulerAngles;
		}

		public void ApplyBoneIKTarget(BasisTwoBoneIKConstraintHand Constraint, Vector3 Position, Quaternion Rotation)
		{
			Constraint.data.TargetPosition = Position;
			Constraint.data.TargetRotation = Rotation.eulerAngles;
		}

		public void BuildBuilder()
		{
			PlayableGraph = player.BasisAvatar.Animator.playableGraph;
			PlayableGraph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
			Builder.Build(PlayableGraph);
		}

		public void OnTPose()
		{
			OnTPose(BasisLocalAvatarDriver.CurrentlyTposing);
		}

		public void OnTPose(bool currentlyTposing)
		{
			if (Builder != null)
			{
				foreach (RigLayer Layer in Builder.layers)
				{
					if (currentlyTposing)
					{
						Layer.active = false;
					}
				}
				if (currentlyTposing == false)
				{
					foreach (BasisLocalBoneControl control in BasisLocalPlayer.Instance.LocalBoneDriver.Controls)
					{
						control.OnHasRigChanged?.Invoke();
					}
				}
			}
		}

		public void CleanupBeforeContinue()
		{
			if (RigSpineRig != null)
			{
				GameObject.Destroy(RigSpineRig.gameObject);
			}
			if (RigHeadRig != null)
			{
				GameObject.Destroy(RigHeadRig.gameObject);
			}
			if (LeftHandRig != null)
			{
				GameObject.Destroy(LeftHandRig.gameObject);
			}
			if (RightHandRig != null)
			{
				GameObject.Destroy(RightHandRig.gameObject);
			}
			if (LeftFootRig != null)
			{
				GameObject.Destroy(LeftFootRig.gameObject);
			}
			if (RightFootRig != null)
			{
				GameObject.Destroy(RightFootRig.gameObject);
			}
			if (ChestSpineRig != null)
			{
				GameObject.Destroy(ChestSpineRig.gameObject);
			}
			if (LeftShoulderRig != null)
			{
				GameObject.Destroy(LeftShoulderRig.gameObject);
			}
			if (RightShoulderRig != null)
			{
				GameObject.Destroy(RightShoulderRig.gameObject);
			}

			if (LeftToeRig != null)
			{
				GameObject.Destroy(LeftToeRig.gameObject);
			}
			if (RightToeRig != null)
			{
				GameObject.Destroy(RightToeRig.gameObject);
			}
		}

		public void SetBodySettings(BasisLocalBoneDriver driver)
		{
			SetupHeadRig(driver);
			LeftHand(driver);
			RightHand(driver);
			LeftFoot(driver);
			RightFoot(driver);

			LeftToe(driver);
			RightToe(driver);
			if (references.Hips.gameObject.TryGetComponent<RigTransform>(out RigTransform RigTransform) == false)
			{
				RigTransform Hips = references.Hips.gameObject.AddComponent<RigTransform>();
			}
            BasisLocalBoneControl.HasEvents = true;
        }

		private void SetupTwistBoneSpine(BasisLocalBoneDriver driver)
		{
			GameObject HeadRig = CreateOrGetRig("Rig Chest", true, out RigSpineRig, out RigSpineLayer);
			BasisAnimationRiggingHelper.TwistChain(driver, HeadRig, references.Hips, references.neck, BasisBoneTrackedRole.Hips, BasisBoneTrackedRole.Neck, 1, 1);
			List<BasisLocalBoneControl> controls = new List<BasisLocalBoneControl>();
			if (driver.FindBone(out BasisLocalBoneControl Neck, BasisBoneTrackedRole.Neck))
			{
				controls.Add(Neck);
			}
			if (driver.FindBone(out BasisLocalBoneControl Head, BasisBoneTrackedRole.Head))
			{
				controls.Add(Head);
			}
			WriteUpEvents(controls, RigSpineLayer);
		}

		private void SetupHeadRig(BasisLocalBoneDriver driver)
		{
			GameObject HeadRig = CreateOrGetRig("Chest, Neck, Head", true, out RigHeadRig, out RigHeadLayer);
			if (references.HasUpperchest)
			{
				BasisAnimationRiggingHelper.CreateTwoBone(localPlayer, HeadRig, references.Upperchest, references.neck, references.head, BasisBoneTrackedRole.Head, BasisBoneTrackedRole.Chest, true, out HeadTwoBoneIK, false, false);
			}
			else
			{
				if (references.Haschest)
				{
					BasisAnimationRiggingHelper.CreateTwoBone(localPlayer, HeadRig, references.chest, references.neck, references.head, BasisBoneTrackedRole.Head, BasisBoneTrackedRole.Chest, true, out HeadTwoBoneIK, false, false);

				}
				else
				{
					BasisAnimationRiggingHelper.CreateTwoBone(localPlayer, HeadRig, null, references.neck, references.head, BasisBoneTrackedRole.Head, BasisBoneTrackedRole.Chest, true, out HeadTwoBoneIK, false, false);

				}
			}
			List<BasisLocalBoneControl> controls = new List<BasisLocalBoneControl>();
			if (driver.FindBone(out BasisLocalBoneControl Head, BasisBoneTrackedRole.Head))
			{
				controls.Add(Head);
			}
			if (driver.FindBone(out BasisLocalBoneControl Chest, BasisBoneTrackedRole.Chest))
			{
				controls.Add(Chest);
			}
			WriteUpEvents(controls, RigHeadLayer);
		}

		private void SetupRightShoulderRig(BasisLocalBoneDriver driver)
		{
			GameObject RightShoulder = CreateOrGetRig("RightShoulder", false, out RightShoulderRig, out RightShoulderLayer);
			BasisAnimationRiggingHelper.Damp(localPlayer, RightShoulder, references.RightShoulder, BasisBoneTrackedRole.RightShoulder);
			List<BasisLocalBoneControl> controls = new List<BasisLocalBoneControl>();
			if (driver.FindBone(out BasisLocalBoneControl RightShoulderRole, BasisBoneTrackedRole.RightShoulder))
			{
				controls.Add(RightShoulderRole);
			}
			WriteUpEvents(controls, RightShoulderLayer);
		}

		private void SetupLeftShoulderRig(BasisLocalBoneDriver driver)
		{
			GameObject LeftShoulder = CreateOrGetRig("LeftShoulder", false, out LeftShoulderRig, out LeftShoulderLayer);
			BasisAnimationRiggingHelper.Damp(localPlayer, LeftShoulder, references.leftShoulder, BasisBoneTrackedRole.LeftShoulder);
			List<BasisLocalBoneControl> controls = new List<BasisLocalBoneControl>();
			if (driver.FindBone(out BasisLocalBoneControl LeftShoulderRole, BasisBoneTrackedRole.LeftShoulder))
			{
				controls.Add(LeftShoulderRole);
			}
			WriteUpEvents(controls, LeftShoulderLayer);
		}

		public void LeftHand(BasisLocalBoneDriver driver)
		{
			GameObject Hands = CreateOrGetRig("LeftUpperArm, LeftLowerArm, LeftHand", false, out LeftHandRig, out LeftHandLayer);
			List<BasisLocalBoneControl> controls = new List<BasisLocalBoneControl>();
			if (driver.FindBone(out BasisLocalBoneControl LeftHand, BasisBoneTrackedRole.LeftHand))
			{
				controls.Add(LeftHand);
			}
			if (driver.FindBone(out BasisLocalBoneControl LeftLowerArm, BasisBoneTrackedRole.LeftLowerArm))
			{
				controls.Add(LeftLowerArm);
			}
			WriteUpEvents(controls, LeftHandLayer);
			BasisAnimationRiggingHelper.CreateTwoBoneHand(localPlayer, Hands, references.leftUpperArm, references.leftLowerArm, references.leftHand, BasisBoneTrackedRole.LeftHand, BasisBoneTrackedRole.LeftLowerArm, true, out LeftHandTwoBoneIK, false, false);
		}

		public void RightHand(BasisLocalBoneDriver driver)
		{
			GameObject Hands = CreateOrGetRig("RightUpperArm, RightLowerArm, RightHand", false, out RightHandRig, out RightHandLayer);
			List<BasisLocalBoneControl> controls = new List<BasisLocalBoneControl>();
			if (driver.FindBone(out BasisLocalBoneControl RightHand, BasisBoneTrackedRole.RightHand))
			{
				controls.Add(RightHand);
			}
			if (driver.FindBone(out BasisLocalBoneControl RightLowerArm, BasisBoneTrackedRole.RightLowerArm))
			{
				controls.Add(RightLowerArm);
			}
			WriteUpEvents(controls, RightHandLayer);
			BasisAnimationRiggingHelper.CreateTwoBoneHand(localPlayer, Hands, references.RightUpperArm, references.RightLowerArm, references.rightHand, BasisBoneTrackedRole.RightHand, BasisBoneTrackedRole.RightLowerArm, true, out RightHandTwoBoneIK, false, false);
		}

		public void LeftFoot(BasisLocalBoneDriver driver)
		{
			GameObject feet = CreateOrGetRig("LeftUpperLeg, LeftLowerLeg, LeftFoot", false, out LeftFootRig, out LeftFootLayer);
			List<BasisLocalBoneControl> controls = new List<BasisLocalBoneControl>();
			if (driver.FindBone(out BasisLocalBoneControl LeftFoot, BasisBoneTrackedRole.LeftFoot))
			{
				controls.Add(LeftFoot);
			}
			if (driver.FindBone(out BasisLocalBoneControl LeftLowerLeg, BasisBoneTrackedRole.LeftLowerLeg))
			{
				controls.Add(LeftLowerLeg);
			}

			WriteUpEvents(controls, LeftFootLayer);

			BasisAnimationRiggingHelper.CreateTwoBone(localPlayer, feet, references.LeftUpperLeg, references.LeftLowerLeg, references.leftFoot, BasisBoneTrackedRole.LeftFoot, BasisBoneTrackedRole.LeftLowerLeg, true, out LeftFootTwoBoneIK, false, true);
		}

		public void RightFoot(BasisLocalBoneDriver driver)
		{
			GameObject feet = CreateOrGetRig("RightUpperLeg, RightLowerLeg, RightFoot", false, out RightFootRig, out RightFootLayer);
			List<BasisLocalBoneControl> controls = new List<BasisLocalBoneControl>();
			if (driver.FindBone(out BasisLocalBoneControl RightFoot, BasisBoneTrackedRole.RightFoot))
			{
				controls.Add(RightFoot);
			}
			if (driver.FindBone(out BasisLocalBoneControl RightLowerLeg, BasisBoneTrackedRole.RightLowerLeg))
			{
				controls.Add(RightLowerLeg);
			}

			WriteUpEvents(controls, RightFootLayer);

			BasisAnimationRiggingHelper.CreateTwoBone(localPlayer, feet, references.RightUpperLeg, references.RightLowerLeg, references.rightFoot, BasisBoneTrackedRole.RightFoot, BasisBoneTrackedRole.RightLowerLeg, true, out RightFootTwoBoneIK, false, true);
		}

		public void LeftToe(BasisLocalBoneDriver driver)
		{
			GameObject LeftToe = CreateOrGetRig("LeftToe", false, out LeftToeRig, out LeftToeLayer);
			if (driver.FindBone(out BasisLocalBoneControl Control, BasisBoneTrackedRole.LeftToes))
			{
				WriteUpEvents(new List<BasisLocalBoneControl>() { Control }, LeftToeLayer);
			}
			LeftToeConstraint = BasisAnimationRiggingHelper.Damp(localPlayer, LeftToe, references.leftToes, BasisBoneTrackedRole.LeftToes);
		}

		public void RightToe(BasisLocalBoneDriver driver)
		{
			GameObject RightToe = CreateOrGetRig("RightToe", false, out RightToeRig, out RightToeLayer);
			if (driver.FindBone(out BasisLocalBoneControl Control, BasisBoneTrackedRole.RightToes))
			{
				WriteUpEvents(new List<BasisLocalBoneControl>() { Control }, RightToeLayer);
			}
			RightToeConstraint = BasisAnimationRiggingHelper.Damp(localPlayer, RightToe, references.rightToes, BasisBoneTrackedRole.RightToes);
		}

		public void CalibrateRoles()
		{
			foreach (BasisBoneTrackedRole Role in Enum.GetValues(typeof(BasisBoneTrackedRole)))
			{
				ApplyHint(Role, false);
			}
			for (int Index = 0; Index < BasisDeviceManagement.Instance.AllInputDevices.Count; Index++)
			{
				Device_Management.Devices.BasisInput BasisInput = BasisDeviceManagement.Instance.AllInputDevices[Index];
				if (BasisInput.TryGetRole(out BasisBoneTrackedRole Role))
				{
					ApplyHint(Role, true);
				}
			}
		}

		public void ApplyHint(BasisBoneTrackedRole RoleWithHint, bool weight)
		{
			try
			{
				switch (RoleWithHint)
				{
					case BasisBoneTrackedRole.Chest:
						// BasisDebug.Log("Setting Hint For " + RoleWithHint + " with weight " + weight);
						HeadTwoBoneIK.data.hintWeight = weight;
						break;

					case BasisBoneTrackedRole.RightLowerLeg:
						// BasisDebug.Log("Setting Hint For " + RoleWithHint + " with weight " + weight);
						RightFootTwoBoneIK.data.hintWeight = weight;
						break;

					case BasisBoneTrackedRole.LeftLowerLeg:
						// BasisDebug.Log("Setting Hint For " + RoleWithHint + " with weight " + weight);
						LeftFootTwoBoneIK.data.hintWeight = weight;
						break;

					case BasisBoneTrackedRole.RightUpperArm:
						// BasisDebug.Log("Setting Hint For " + RoleWithHint + " with weight " + weight);
						RightHandTwoBoneIK.data.hintWeight = weight;
						break;

					case BasisBoneTrackedRole.LeftUpperArm:
						// BasisDebug.Log("Setting Hint For " + RoleWithHint + " with weight " + weight);
						LeftHandTwoBoneIK.data.hintWeight = weight;
						break;
					case BasisBoneTrackedRole.LeftLowerArm:
						// BasisDebug.Log("Setting Hint For " + RoleWithHint + " with weight " + weight);
						RightHandTwoBoneIK.data.hintWeight = weight;
						break;

					case BasisBoneTrackedRole.RightLowerArm:
						// BasisDebug.Log("Setting Hint For " + RoleWithHint + " with weight " + weight);
						LeftHandTwoBoneIK.data.hintWeight = weight;
						break;
					default:
						// Optional: Handle cases where RoleWithHint does not match any of the expected roles
						// BasisDebug.Log("Unknown role: " + RoleWithHint);
						break;
				}
			}
			catch (Exception e)
			{
				BasisDebug.Log($"{e.Message} {e.StackTrace}");
			}
		}

		public void WriteUpEvents(List<BasisLocalBoneControl> Controls, RigLayer Layer)
		{
			foreach (var control in Controls)
			{
				// Add event listener for each control to update Layer's active state when HasRigLayer changes
				control.OnHasRigChanged += delegate { UpdateLayerActiveState(Controls, Layer); };
			}

			// Set the initial state based on the current controls' states
			UpdateLayerActiveState(Controls, Layer);
		}

		// Define a method to update the active state of the Layer based on the list of controls
		void UpdateLayerActiveState(List<BasisLocalBoneControl> Controls, RigLayer Layer)
		{
			// Check if any control in the list has HasRigLayer set to true
			Layer.active = Controls.Any(control => control.HasRigLayer == BasisHasRigLayer.HasRigLayer);
			// BasisDebug.Log("Update Layer to State " + Layer.active + " for layer " + Layer);
		}

		public GameObject CreateOrGetRig(string Role, bool Enabled, out Rig Rig, out RigLayer RigLayer)
		{
			foreach (RigLayer Layer in Builder.layers)
			{
				if (Layer.rig.name == $"Rig {Role}")
				{
					RigLayer = Layer;
					Rig = Layer.rig;
					return Layer.rig.gameObject;
				}
			}
			GameObject RigGameobject = BasisAnimationRiggingHelper.CreateAndSetParent(player.BasisAvatar.Animator.transform, $"Rig {Role}");
			Rig = BasisHelpers.GetOrAddComponent<Rig>(RigGameobject);
			Rigs.Add(Rig);
			RigLayer = new RigLayer(Rig, Enabled);
			Builder.layers.Add(RigLayer);
			return RigGameobject;
		}
	}
}
