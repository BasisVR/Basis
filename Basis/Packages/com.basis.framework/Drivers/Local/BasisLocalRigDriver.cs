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
        // === PUBLIC FILTER SETTINGS (tweak in Inspector) ===
        [Header("Smoothing (One Euro Filter)")]
        [Tooltip("Lower = more smoothing; Higher = more responsive.")]
        [Range(0.01f, 10f)]
        public float MinCutoff = 5.5f;
        [Tooltip("How much to raise cutoff when motion is fast (reduces lag during quick moves).")]
        [Range(0f, 10f)]
        public float Beta = 3.25f;
        [Tooltip("Cutoff for derivative smoothing.")]
        [Range(0.01f, 10f)]
        public float DerivativeCutoff = 3f;

        // === IK Constraints ===
        public BasisHipsHeadIKConstraint SpineIK;
        public BasisTwoBoneIKConstraint LeftFootTwoBoneIK;
        public BasisTwoBoneIKConstraint RightFootTwoBoneIK;
        public BasisTwoBoneIKConstraintHand LeftHandTwoBoneIK;
        public BasisTwoBoneIKConstraintHand RightHandTwoBoneIK;
        public BasisTwoBoneIKConstraint UpperChestTwoBoneIK;

        public BasisApplyTranslation LeftToeConstraint;
        public BasisApplyTranslation RightToeConstraint;

        public Rig LeftToeRig;
        public Rig RightToeRig;

        public Rig HeadRig;
        public Rig SpineRig;
        public Rig LeftHandRig;
        public Rig RightHandRig;
        public Rig LeftFootRig;
        public Rig RightFootRig;
        public Rig LeftShoulderRig;
        public Rig RightShoulderRig;

        public RigLayer LeftHandLayer;
        public RigLayer RightHandLayer;
        public RigLayer LeftFootLayer;
        public RigLayer RightFootLayer;
        public RigLayer LeftToeLayer;
        public RigLayer RightToeLayer;
        public RigLayer RigSpineLayer;
        public RigLayer RigChainSpineLayer;
        public RigLayer HeadLayer;

        public RigLayer LeftShoulderLayer;
        public RigLayer RightShoulderLayer;
        public List<Rig> Rigs = new List<Rig>();
        public RigBuilder Builder;
        public List<RigTransform> AdditionalTransforms = new List<RigTransform>();
        public PlayableGraph PlayableGraph;
        private BasisLocalPlayer localPlayer;
        private BasisTransformMapping references;
        private BasisTwoBoneIKConstraint HeadTwoBoneIK;

        // === Per-role smoothers ===
        private readonly Dictionary<BasisBoneTrackedRole, OneEuroFilterVector3> posFilters = new();
        private readonly Dictionary<BasisBoneTrackedRole, OneEuroFilterQuaternion> rotFilters = new();

        // Timestamp accumulator for filters
        private float _timeAccumulator;

        // Helper to fetch or create a filter for a role
        private OneEuroFilterVector3 GetPosFilter(BasisBoneTrackedRole role)
        {
            if (!posFilters.TryGetValue(role, out var f))
            {
                f = new OneEuroFilterVector3(MinCutoff, Beta, DerivativeCutoff);
                posFilters[role] = f;
            }
            else
            {
                // keep runtime params in sync if adjusted at runtime
                f.minCutoff = MinCutoff; f.beta = Beta; f.dCutoff = DerivativeCutoff;
            }
            return f;
        }
        public void Initialize(BasisLocalPlayer localPlayer, BasisTransformMapping references)
        {
            this.localPlayer = localPlayer;
            this.references = references;
            _timeAccumulator = 0f;
        }

        public void SimulateIKDestinations(float DeltaTime)
        {
            _timeAccumulator += Mathf.Max(DeltaTime, 1e-6f);

            // --- IK Target ---
            // Spine (hips + head targets come from calibrated coords)
            var hipsCoords = BasisLocalBoneDriver.HipsControl.OutgoingWorldData;

            var hipsPos = GetPosFilter(BasisBoneTrackedRole.Hips).Filter(hipsCoords.position, _timeAccumulator);
            //  var hipsRot = GetRotFilter(BasisBoneTrackedRole.Hips).Filter(hipsCoords.rotation, _timeAccumulator);

            ApplySpineIKTarget(
                new BasisCalibratedCoords
                {
                    position = hipsPos,
                    rotation = hipsCoords.rotation
                }
            );

            // Head chain IK (two-bone)
            FilterAndApplyTarget(HeadTwoBoneIK, BasisBoneTrackedRole.Head);

            // Feet
            FilterAndApplyTarget(LeftFootTwoBoneIK, BasisBoneTrackedRole.LeftFoot);
            FilterAndApplyTarget(RightFootTwoBoneIK, BasisBoneTrackedRole.RightFoot);

            // Hands
            FilterAndApplyTarget(LeftHandTwoBoneIK, BasisBoneTrackedRole.LeftHand);
            FilterAndApplyTarget(RightHandTwoBoneIK, BasisBoneTrackedRole.RightHand);

            // Toes (apply translation constraint)
            FilterAndApplyTarget(LeftToeConstraint, BasisBoneTrackedRole.LeftToes);
            FilterAndApplyTarget(RightToeConstraint, BasisBoneTrackedRole.RightToes);

            // Direction for knee/neck hints relative to hips orientation (unchanged)
            Vector3 Direction = BasisLocalBoneDriver.HipsControl.OutgoingWorldData.rotation * Vector3.right;

            // --- IK Hints ---
            FilterAndApplyHint(HeadTwoBoneIK, BasisBoneTrackedRole.Chest, Direction);

            FilterAndApplyHint(LeftFootTwoBoneIK, BasisBoneTrackedRole.LeftLowerLeg, Direction);
            FilterAndApplyHint(RightFootTwoBoneIK, BasisBoneTrackedRole.RightLowerLeg, Direction);

            FilterAndApplyHint(LeftHandTwoBoneIK, BasisBoneTrackedRole.LeftLowerArm);
            FilterAndApplyHint(RightHandTwoBoneIK, BasisBoneTrackedRole.RightLowerArm);

            BasisAnimationRiggingHelper.SetHandCollisionScale(LeftHandTwoBoneIK, localPlayer.CurrentHeight.SelectedAvatarToAvatarDefaultScale);
            BasisAnimationRiggingHelper.SetHandCollisionScale(RightHandTwoBoneIK, localPlayer.CurrentHeight.SelectedAvatarToAvatarDefaultScale);

            if (Builder != null)
            {
                // --- Do IK on animator ---
                Builder.SyncLayers();
                PlayableGraph.Evaluate(DeltaTime);
            }
        }

        private void FilterAndApplyTarget(BasisTwoBoneIKConstraint constraint, BasisBoneTrackedRole role)
        {
            var data = GetCoordsForRole(role);
        //    data.position = GetPosFilter(role).Filter(data.position, _timeAccumulator);
        //    data.rotation = GetRotFilter(role).Filter(data.rotation, _timeAccumulator);
            ApplyBoneIKTarget(constraint, data.position, data.rotation);
        }
        private void FilterAndApplyTarget(BasisTwoBoneIKConstraintHand constraint, BasisBoneTrackedRole role)
        {
            var data = GetCoordsForRole(role);
         //   data.position = GetPosFilter(role).Filter(data.position, _timeAccumulator);
        //    data.rotation = GetRotFilter(role).Filter(data.rotation, _timeAccumulator);
            ApplyBoneIKTarget(constraint, data.position, data.rotation);
        }
        private void FilterAndApplyTarget(BasisApplyTranslation constraint, BasisBoneTrackedRole role)
        {
            var data = GetCoordsForRole(role);
          //  data.position = GetPosFilter(role).Filter(data.position, _timeAccumulator);
          //  data.rotation = GetRotFilter(role).Filter(data.rotation, _timeAccumulator);
            ApplyBoneIKTarget(constraint, data.position, data.rotation);
        }

        private void FilterAndApplyHint(BasisTwoBoneIKConstraint constraint, BasisBoneTrackedRole role, Vector3 customDirection)
        {
            var data = GetCoordsForRole(role);
            ApplyBoneIKHint(constraint, data.position, data.rotation, customDirection);
        }
        private void FilterAndApplyHint(BasisTwoBoneIKConstraintHand constraint, BasisBoneTrackedRole role)
        {
            var data = GetCoordsForRole(role);
          // data.position = GetPosFilter(role).Filter(data.position, _timeAccumulator);
         //   data.rotation = GetRotFilter(role).Filter(data.rotation, _timeAccumulator);
            ApplyHandBoneIKHint(constraint, data.position, data.rotation);
        }

        private BasisCalibratedCoords GetCoordsForRole(BasisBoneTrackedRole role)
        {
            // Map roles to driver controls
            switch (role)
            {
                case BasisBoneTrackedRole.Head: return BasisLocalBoneDriver.HeadControl.OutgoingWorldData;
                case BasisBoneTrackedRole.Hips: return BasisLocalBoneDriver.HipsControl.OutgoingWorldData;

                case BasisBoneTrackedRole.LeftHand: return BasisLocalBoneDriver.LeftHandControl.OutgoingWorldData;
                case BasisBoneTrackedRole.RightHand: return BasisLocalBoneDriver.RightHandControl.OutgoingWorldData;

                case BasisBoneTrackedRole.LeftLowerArm: return BasisLocalBoneDriver.LeftLowerArmControl.OutgoingWorldData;
                case BasisBoneTrackedRole.RightLowerArm: return BasisLocalBoneDriver.RightLowerArmControl.OutgoingWorldData;

                case BasisBoneTrackedRole.LeftFoot: return BasisLocalBoneDriver.LeftFootControl.OutgoingWorldData;
                case BasisBoneTrackedRole.RightFoot: return BasisLocalBoneDriver.RightFootControl.OutgoingWorldData;

                case BasisBoneTrackedRole.LeftLowerLeg: return BasisLocalBoneDriver.LeftLowerLegControl.OutgoingWorldData;
                case BasisBoneTrackedRole.RightLowerLeg: return BasisLocalBoneDriver.RightLowerLegControl.OutgoingWorldData;

                case BasisBoneTrackedRole.LeftToes: return BasisLocalBoneDriver.LeftToeControl.OutgoingWorldData;
                case BasisBoneTrackedRole.RightToes: return BasisLocalBoneDriver.RightToeControl.OutgoingWorldData;

                case BasisBoneTrackedRole.Chest: return BasisLocalBoneDriver.ChestControl.OutgoingWorldData;

                default:
                    // Fallback: return identity to avoid null ref
                    return new BasisCalibratedCoords { position = Vector3.zero, rotation = Quaternion.identity };
            }
        }

        public void ApplySpineIKTarget(BasisCalibratedCoords hip)
        {
            SpineIK.data.hipsTargetPosition = hip.position;
            SpineIK.data.hipsTargetRotationEuler = hip.rotation;
        }
        public void ApplyBoneIKHint(BasisTwoBoneIKConstraint Constraint, Vector3 Position, Quaternion Rotation, Vector3 Direction)
        {
            Constraint.data.HintPosition = Position;
            Constraint.data.HintRotation = Rotation;
            Constraint.data.m_HintDirection = Direction;
        }

        public void ApplyHandBoneIKHint(BasisTwoBoneIKConstraintHand Constraint, Vector3 Position, Quaternion Rotation)
        {
            Constraint.data.HintPosition = Position;
            Constraint.data.HintRotation = Rotation;
        }

        public void ApplyBoneIKTarget(BasisTwoBoneIKConstraint Constraint, Vector3 Position, Quaternion Rotation)
        {
            Constraint.data.TargetPosition = Position;
            Constraint.data.TargetRotation = Rotation;
        }

        public void ApplyBoneIKTarget(BasisApplyTranslation basisDamped, Vector3 Position, Quaternion Rotation)
        {
            basisDamped.data.TargetPosition = Position;
            basisDamped.data.TargetRotation = Rotation;
        }

        public void ApplyBoneIKTarget(BasisTwoBoneIKConstraintHand Constraint, Vector3 Position, Quaternion Rotation)
        {
            Constraint.data.TargetPosition = Position;
            Constraint.data.TargetRotation = Rotation;
        }

        public void BuildBuilder()
        {
            PlayableGraph = localPlayer.BasisAvatar.Animator.playableGraph;
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
            if (HeadRig != null)
            {
                GameObject.Destroy(HeadRig.gameObject);
            }
            if (SpineRig != null)
            {
                GameObject.Destroy(SpineRig.gameObject);
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
            SetupSpine(driver);
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
        private void SetupHeadRig(BasisLocalBoneDriver driver)
        {
            GameObject GameobjectHeadRig = CreateOrGetRig("Chest, Neck, Head", true, out HeadRig, out HeadLayer);
            if (references.HasUpperchest)
            {
                BasisAnimationRiggingHelper.CreateTwoBone(localPlayer, GameobjectHeadRig, references.Upperchest, references.neck, references.head, BasisBoneTrackedRole.Head, BasisBoneTrackedRole.Chest, true, out HeadTwoBoneIK, false, false);
            }
            else
            {
                if (references.Haschest)
                {
                    BasisAnimationRiggingHelper.CreateTwoBone(localPlayer, GameobjectHeadRig, references.chest, references.neck, references.head, BasisBoneTrackedRole.Head, BasisBoneTrackedRole.Chest, true, out HeadTwoBoneIK, false, false);

                }
                else
                {
                    BasisAnimationRiggingHelper.CreateTwoBone(localPlayer, GameobjectHeadRig, null, references.neck, references.head, BasisBoneTrackedRole.Head, BasisBoneTrackedRole.Chest, true, out HeadTwoBoneIK, false, false);

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
            WriteUpEvents(controls, HeadLayer);
        }

        private void SetupSpine(BasisLocalBoneDriver driver)
        {
            var spineRig = CreateOrGetRig("Rig Spine", true, out SpineRig, out RigSpineLayer);
            List<BasisLocalBoneControl> controls = new List<BasisLocalBoneControl>();
            if (driver.FindBone(out BasisLocalBoneControl Hip, BasisBoneTrackedRole.Hips))
            {
                controls.Add(Hip);
            }
            if (driver.FindBone(out BasisLocalBoneControl Head, BasisBoneTrackedRole.Head))
            {
                controls.Add(Head);
            }
            WriteUpEvents(controls, RigSpineLayer);
            BasisAnimationRiggingHelper.CreateSpine(localPlayer, spineRig, references.Hips, references.head, BasisBoneTrackedRole.Hips, out SpineIK);
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
            BasisAnimationRiggingHelper.CreateTwoBoneHand(localPlayer, Hands, references.Hips, references.chest, references.leftUpperArm, references.leftLowerArm, references.leftHand, BasisBoneTrackedRole.LeftHand, BasisBoneTrackedRole.LeftLowerArm, true, out LeftHandTwoBoneIK, false, false);
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
            BasisAnimationRiggingHelper.CreateTwoBoneHand(localPlayer, Hands, references.Hips, references.chest, references.RightUpperArm, references.RightLowerArm, references.rightHand, BasisBoneTrackedRole.RightHand, BasisBoneTrackedRole.RightLowerArm, true, out RightHandTwoBoneIK, false, false);
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
                        HeadTwoBoneIK.data.hintWeight = weight;
                        break;

                    case BasisBoneTrackedRole.RightLowerLeg:
                        RightFootTwoBoneIK.data.hintWeight = weight;
                        break;

                    case BasisBoneTrackedRole.LeftLowerLeg:
                        LeftFootTwoBoneIK.data.hintWeight = weight;
                        break;

                    case BasisBoneTrackedRole.RightUpperArm:
                        RightHandTwoBoneIK.data.hintWeight = weight;
                        break;

                    case BasisBoneTrackedRole.LeftUpperArm:
                        LeftHandTwoBoneIK.data.hintWeight = weight;
                        break;
                    case BasisBoneTrackedRole.LeftLowerArm:
                        RightHandTwoBoneIK.data.hintWeight = weight;
                        break;

                    case BasisBoneTrackedRole.RightLowerArm:
                        LeftHandTwoBoneIK.data.hintWeight = weight;
                        break;
                    default:
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
                control.OnHasRigChanged += delegate { UpdateLayerActiveState(Controls, Layer); };
            }
            UpdateLayerActiveState(Controls, Layer);
        }

        void UpdateLayerActiveState(List<BasisLocalBoneControl> Controls, RigLayer Layer)
        {
            Layer.active = Controls.Any(control => control.HasRigLayer == BasisHasRigLayer.HasRigLayer);
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
            GameObject RigGameobject = BasisAnimationRiggingHelper.CreateAndSetParent(localPlayer.BasisAvatar.Animator.transform, $"Rig {Role}");
            Rig = BasisHelpers.GetOrAddComponent<Rig>(RigGameobject);
            Rigs.Add(Rig);
            RigLayer = new RigLayer(Rig, Enabled);
            Builder.layers.Add(RigLayer);
            return RigGameobject;
        }
    }
}
