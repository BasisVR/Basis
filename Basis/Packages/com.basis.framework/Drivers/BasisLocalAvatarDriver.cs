using Basis.Scripts.Avatar;
using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.TransformBinders.BoneControl;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Animations.Rigging;
using UnityEngine.Playables;

namespace Basis.Scripts.Drivers
{
    [System.Serializable]
    public class BasisLocalAvatarDriver : BasisAvatarDriver
    {
        public static Vector3 HeadScale = Vector3.one;
        public static Vector3 HeadScaledDown = Vector3.zero;
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
        public bool HasTPoseEvent = false;
        public string Locomotion = "Locomotion";
        public PlayableGraph PlayableGraph;
        public float MaxExtendedDistance;
        public static BasisLocalAvatarDriver Instance;
        public Vector3 AvatarDefaultScale = Vector3.one;
        public Vector3 CurrentLocalScale = Vector3.one;
        public Vector3 CurrentScaleValidated = Vector3.one;
        public static bool IsNormalHead;
        public Dictionary<BasisBoneTrackedRole, Transform> StoredRolesTransforms;
        public void SetAvatarheightOverride(Vector3 Scale)
        {
            CurrentLocalScale = Scale;
            if (BasisLocalPlayer.Instance.BasisAvatar != null)
            {
                BasisLocalPlayer.Instance.BasisAvatar.transform.localScale = Scale;
            }
            // Final scale = Default scale * Override scale (component-wise)
            CurrentScaleValidated = new Vector3(
                AvatarDefaultScale.x * CurrentLocalScale.x,
                AvatarDefaultScale.y * CurrentLocalScale.y,
                AvatarDefaultScale.z * CurrentLocalScale.z
            );
        }
        public void SimulateIKDestinations(Quaternion Rotation)
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
        public void InitialLocalCalibration(BasisLocalPlayer player)
        {
            Instance = this;
            BasisDebug.Log("InitialLocalCalibration");
            if (HasTPoseEvent == false)
            {
                TposeStateChange += OnTPose;
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
            CleanupBeforeContinue();
            AdditionalTransforms.Clear();
            Rigs.Clear();
            GameObject AvatarAnimatorParent = player.BasisAvatar.Animator.gameObject;
            AvatarDefaultScale = AvatarAnimatorParent.transform.localScale;
            CurrentLocalScale = AvatarDefaultScale;
            SetAvatarheightOverride(AvatarDefaultScale);
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
            Builder = BasisHelpers.GetOrAddComponent<RigBuilder>(AvatarAnimatorParent);
            Builder.enabled = false;
            Calibration(player.BasisAvatar);
            BasisLocalPlayer.Instance.LocalBoneDriver.RemoveAllListeners();
            BasisLocalPlayer.Instance.LocalEyeDriver.Initalize(this, player);
            SetMatrixOverride();
            updateWhenOffscreen(true);
            if (References.Hashead)
            {
                HeadScale = References.head.localScale;
            }
            else
            {
                HeadScale = Vector3.one;
            }
            player.LocalHandDriver.ReInitialize(player.BasisAvatar.Animator, References);
            SetBodySettings(player.LocalBoneDriver);
            CalculateTransformPositions(player, player.LocalBoneDriver);
            ComputeOffsets(player.LocalBoneDriver);

            CalibrationComplete?.Invoke();
            player.LocalAnimatorDriver.Initialize(player);
            //stop Tpose
            ResetAvatarAnimator();
            BasisAvatarIKStageCalibration.HasFBIKTrackers = false;
            if (BasisLocalPlayer.Instance.LocalBoneDriver.FindBone(out BasisBoneControl Head, BasisBoneTrackedRole.Head))
            {
                Head.HasRigLayer = BasisHasRigLayer.HasRigLayer;
            }
            if (BasisLocalPlayer.Instance.LocalBoneDriver.FindBone(out BasisBoneControl Hips, BasisBoneTrackedRole.Hips))
            {
                Hips.HasRigLayer = BasisHasRigLayer.HasRigLayer;
            }
            if (BasisLocalPlayer.Instance.LocalBoneDriver.FindBone(out BasisBoneControl Spine, BasisBoneTrackedRole.Spine))
            {
                Spine.HasRigLayer = BasisHasRigLayer.HasRigLayer;
            }
            StoredRolesTransforms = BasisAvatarIKStageCalibration.GetAllRolesAsTransform();
            player.BasisAvatarTransform.parent = player.transform;
            player.BasisAvatarTransform.SetLocalPositionAndRotation(-Hips.TposeLocalScaled.position, Quaternion.identity);
            CalculateMaxExtended();
            BuildBuilder();
            IsNormalHead = true;
        }
        public void CalculateMaxExtended()
        {
            MaxExtendedDistance = Vector3.Distance(BasisLocalBoneDriver.Head.TposeLocalScaled.position, BasisLocalBoneDriver.Hips.TposeLocalScaled.position);
        }
        public static void ScaleHeadToNormal()
        {
            if (IsNormalHead)
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
        public void BuildBuilder()
        {
            PlayableGraph = Player.BasisAvatar.Animator.playableGraph;
            PlayableGraph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            Builder.Build(PlayableGraph);
        }
        public void OnTPose()
        {
            if (Builder != null)
            {
                foreach (RigLayer Layer in Builder.layers)
                {
                    if (CurrentlyTposing)
                    {
                        Layer.active = false;
                    }
                }
                if (CurrentlyTposing == false)
                {
                    foreach (BasisBoneControl control in BasisLocalPlayer.Instance.LocalBoneDriver.Controls)
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
        public void ComputeOffsets(BasisBaseBoneDriver BaseBoneDriver)
        {
            Vector3 Scale = BasisLocalPlayer.Instance.LocalAvatarDriver.CurrentScaleValidated;
            //head
            SetAndCreateLock(Scale, BaseBoneDriver, BasisBoneTrackedRole.CenterEye, BasisBoneTrackedRole.Head, 40, 35, true);
            SetAndCreateLock(Scale, BaseBoneDriver, BasisBoneTrackedRole.Head, BasisBoneTrackedRole.Neck, 40, 35, true);

            SetAndCreateLock(Scale, BaseBoneDriver, BasisBoneTrackedRole.Head, BasisBoneTrackedRole.Mouth, 40, 30, true);


            SetAndCreateLock(Scale, BaseBoneDriver, BasisBoneTrackedRole.Neck, BasisBoneTrackedRole.Chest, 40, 30, true);



            SetAndCreateLock(Scale, BaseBoneDriver, BasisBoneTrackedRole.Chest, BasisBoneTrackedRole.Spine, 40, 14, true);
            SetAndCreateLock(Scale, BaseBoneDriver, BasisBoneTrackedRole.Spine, BasisBoneTrackedRole.Hips, 40, 14, true);

            SetAndCreateLock(Scale, BaseBoneDriver, BasisBoneTrackedRole.Chest, BasisBoneTrackedRole.LeftShoulder, 40, 14, true);
            SetAndCreateLock(Scale, BaseBoneDriver, BasisBoneTrackedRole.Chest, BasisBoneTrackedRole.RightShoulder, 40, 14, true);

            SetAndCreateLock(Scale, BaseBoneDriver, BasisBoneTrackedRole.LeftShoulder, BasisBoneTrackedRole.LeftUpperArm, 40, 14, true);
            SetAndCreateLock(Scale, BaseBoneDriver, BasisBoneTrackedRole.RightShoulder, BasisBoneTrackedRole.RightUpperArm, 40, 14, true);

            SetAndCreateLock(Scale, BaseBoneDriver, BasisBoneTrackedRole.LeftUpperArm, BasisBoneTrackedRole.LeftLowerArm, 40, 14, true);
            SetAndCreateLock(Scale, BaseBoneDriver, BasisBoneTrackedRole.RightUpperArm, BasisBoneTrackedRole.RightLowerArm, 40, 14, true);

            SetAndCreateLock(Scale, BaseBoneDriver, BasisBoneTrackedRole.LeftLowerArm, BasisBoneTrackedRole.LeftHand, 40, 14, true);
            SetAndCreateLock(Scale, BaseBoneDriver, BasisBoneTrackedRole.RightLowerArm, BasisBoneTrackedRole.RightHand, 40, 14, true);

            //legs
            SetAndCreateLock(Scale, BaseBoneDriver, BasisBoneTrackedRole.Hips, BasisBoneTrackedRole.LeftUpperLeg, 40, 14, true);
            SetAndCreateLock(Scale, BaseBoneDriver, BasisBoneTrackedRole.Hips, BasisBoneTrackedRole.RightUpperLeg, 40, 14, true);

            SetAndCreateLock(Scale, BaseBoneDriver, BasisBoneTrackedRole.LeftUpperLeg, BasisBoneTrackedRole.LeftLowerLeg, 40, 14, true);
            SetAndCreateLock(Scale, BaseBoneDriver, BasisBoneTrackedRole.RightUpperLeg, BasisBoneTrackedRole.RightLowerLeg, 40, 14, true);

            SetAndCreateLock(Scale, BaseBoneDriver, BasisBoneTrackedRole.LeftLowerLeg, BasisBoneTrackedRole.LeftFoot, 40, 14, true);
            SetAndCreateLock(Scale, BaseBoneDriver, BasisBoneTrackedRole.RightLowerLeg, BasisBoneTrackedRole.RightFoot, 40, 14, true);

            SetAndCreateLock(Scale, BaseBoneDriver, BasisBoneTrackedRole.LeftFoot, BasisBoneTrackedRole.LeftToes, 40, 14, true);
            SetAndCreateLock(Scale, BaseBoneDriver, BasisBoneTrackedRole.RightFoot, BasisBoneTrackedRole.RightToes, 4, 14, true);
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
        public void SetBodySettings(BasisLocalBoneDriver driver)
        {
            SetupHeadRig(driver);
            //  SetupTwistBoneSpine(driver);
            //  SetupRightShoulderRig(driver);
            //  SetupLeftShoulderRig(driver);
            LeftHand(driver);
            RightHand(driver);
            LeftFoot(driver);
            RightFoot(driver);

            LeftToe(driver);
            RightToe(driver);
            if (References.Hips.gameObject.TryGetComponent<RigTransform>(out RigTransform RigTransform) == false)
            {
                RigTransform Hips = References.Hips.gameObject.AddComponent<RigTransform>();
            }
        }
        /// <summary>
        /// Sets up the Head rig, including chest, neck, and head bones.
        /// </summary>
        private void SetupTwistBoneSpine(BasisLocalBoneDriver driver)
        {
            GameObject HeadRig = CreateOrGetRig("Rig Chest", true, out RigSpineRig, out RigSpineLayer);
            BasisAnimationRiggingHelper.TwistChain(driver, HeadRig, References.Hips, References.neck, BasisBoneTrackedRole.Hips, BasisBoneTrackedRole.Neck, 1, 1);
            List<BasisBoneControl> controls = new List<BasisBoneControl>();
            if (driver.FindBone(out BasisBoneControl Neck, BasisBoneTrackedRole.Neck))
            {
                controls.Add(Neck);
            }
            if (driver.FindBone(out BasisBoneControl Head, BasisBoneTrackedRole.Head))
            {
                controls.Add(Head);
            }
            WriteUpEvents(controls, RigSpineLayer);
        }
        /// <summary>
        /// Sets up the Head rig, including chest, neck, and head bones.
        /// </summary>
        private void SetupHeadRig(BasisLocalBoneDriver driver)
        {
            GameObject HeadRig = CreateOrGetRig("Chest, Neck, Head", true, out RigHeadRig, out RigHeadLayer);
            if (References.HasUpperchest)
            {
                BasisAnimationRiggingHelper.CreateTwoBone(this, driver, HeadRig, References.Upperchest, References.neck, References.head, BasisBoneTrackedRole.Head, BasisBoneTrackedRole.Chest, true, out HeadTwoBoneIK, false, false);
            }
            else
            {
                if (References.Haschest)
                {
                    BasisAnimationRiggingHelper.CreateTwoBone(this, driver, HeadRig, References.chest, References.neck, References.head, BasisBoneTrackedRole.Head, BasisBoneTrackedRole.Chest, true, out HeadTwoBoneIK, false, false);

                }
                else
                {
                    BasisAnimationRiggingHelper.CreateTwoBone(this, driver, HeadRig, null, References.neck, References.head, BasisBoneTrackedRole.Head, BasisBoneTrackedRole.Chest, true, out HeadTwoBoneIK, false, false);

                }
            }
            List<BasisBoneControl> controls = new List<BasisBoneControl>();
            if (driver.FindBone(out BasisBoneControl Head, BasisBoneTrackedRole.Head))
            {
                controls.Add(Head);
            }
            if (driver.FindBone(out BasisBoneControl Chest, BasisBoneTrackedRole.Chest))
            {
                controls.Add(Chest);
            }
            WriteUpEvents(controls, RigHeadLayer);
        }

        /// <summary>
        /// Sets up the Right Shoulder rig, including chest, right shoulder, and right upper arm bones.
        /// </summary>
        private void SetupRightShoulderRig(BasisLocalBoneDriver driver)
        {
            GameObject RightShoulder = CreateOrGetRig("RightShoulder", false, out RightShoulderRig, out RightShoulderLayer);
            BasisAnimationRiggingHelper.Damp(this, driver, RightShoulder, References.RightShoulder, BasisBoneTrackedRole.RightShoulder, 1, 1);
            List<BasisBoneControl> controls = new List<BasisBoneControl>();
            if (driver.FindBone(out BasisBoneControl RightShoulderRole, BasisBoneTrackedRole.RightShoulder))
            {
                controls.Add(RightShoulderRole);
            }
            WriteUpEvents(controls, RightShoulderLayer);
        }

        /// <summary>
        /// Sets up the Left Shoulder rig, including chest, left shoulder, and left upper arm bones.
        /// </summary>
        private void SetupLeftShoulderRig(BasisLocalBoneDriver driver)
        {
            GameObject LeftShoulder = CreateOrGetRig("LeftShoulder", false, out LeftShoulderRig, out LeftShoulderLayer);
            BasisAnimationRiggingHelper.Damp(this, driver, LeftShoulder, References.leftShoulder, BasisBoneTrackedRole.LeftShoulder, 1, 1);
            List<BasisBoneControl> controls = new List<BasisBoneControl>();
            if (driver.FindBone(out BasisBoneControl LeftShoulderRole, BasisBoneTrackedRole.LeftShoulder))
            {
                controls.Add(LeftShoulderRole);
            }
            WriteUpEvents(controls, LeftShoulderLayer);
        }
        public void LeftHand(BasisLocalBoneDriver driver)
        {
            GameObject Hands = CreateOrGetRig("LeftUpperArm, LeftLowerArm, LeftHand", false, out LeftHandRig, out LeftHandLayer);
            List<BasisBoneControl> controls = new List<BasisBoneControl>();
            if (driver.FindBone(out BasisBoneControl LeftHand, BasisBoneTrackedRole.LeftHand))
            {
                controls.Add(LeftHand);
            }
            if (driver.FindBone(out BasisBoneControl LeftLowerArm, BasisBoneTrackedRole.LeftLowerArm))
            {
                controls.Add(LeftLowerArm);
            }
            WriteUpEvents(controls, LeftHandLayer);
            BasisAnimationRiggingHelper.CreateTwoBoneHand(this, driver, Hands, References.leftUpperArm, References.leftLowerArm, References.leftHand, BasisBoneTrackedRole.LeftHand, BasisBoneTrackedRole.LeftLowerArm, true, out LeftHandTwoBoneIK, false, false);
        }
        public void RightHand(BasisLocalBoneDriver driver)
        {
            GameObject Hands = CreateOrGetRig("RightUpperArm, RightLowerArm, RightHand", false, out RightHandRig, out RightHandLayer);
            List<BasisBoneControl> controls = new List<BasisBoneControl>();
            if (driver.FindBone(out BasisBoneControl RightHand, BasisBoneTrackedRole.RightHand))
            {
                controls.Add(RightHand);
            }
            if (driver.FindBone(out BasisBoneControl RightLowerArm, BasisBoneTrackedRole.RightLowerArm))
            {
                controls.Add(RightLowerArm);
            }
            WriteUpEvents(controls, RightHandLayer);
            BasisAnimationRiggingHelper.CreateTwoBoneHand(this, driver, Hands, References.RightUpperArm, References.RightLowerArm, References.rightHand, BasisBoneTrackedRole.RightHand, BasisBoneTrackedRole.RightLowerArm, true, out RightHandTwoBoneIK, false, false);
        }
        public void LeftFoot(BasisLocalBoneDriver driver)
        {
            GameObject feet = CreateOrGetRig("LeftUpperLeg, LeftLowerLeg, LeftFoot", false, out LeftFootRig, out LeftFootLayer);
            List<BasisBoneControl> controls = new List<BasisBoneControl>();
            if (driver.FindBone(out BasisBoneControl LeftFoot, BasisBoneTrackedRole.LeftFoot))
            {
                controls.Add(LeftFoot);
            }
            if (driver.FindBone(out BasisBoneControl LeftLowerLeg, BasisBoneTrackedRole.LeftLowerLeg))
            {
                controls.Add(LeftLowerLeg);
            }

            WriteUpEvents(controls, LeftFootLayer);

            BasisAnimationRiggingHelper.CreateTwoBone(this, driver, feet, References.LeftUpperLeg, References.LeftLowerLeg, References.leftFoot, BasisBoneTrackedRole.LeftFoot, BasisBoneTrackedRole.LeftLowerLeg, true, out LeftFootTwoBoneIK, false, true);
        }
        public void RightFoot(BasisLocalBoneDriver driver)
        {
            GameObject feet = CreateOrGetRig("RightUpperLeg, RightLowerLeg, RightFoot", false, out RightFootRig, out RightFootLayer);
            List<BasisBoneControl> controls = new List<BasisBoneControl>();
            if (driver.FindBone(out BasisBoneControl RightFoot, BasisBoneTrackedRole.RightFoot))
            {
                controls.Add(RightFoot);
            }
            if (driver.FindBone(out BasisBoneControl RightLowerLeg, BasisBoneTrackedRole.RightLowerLeg))
            {
                controls.Add(RightLowerLeg);
            }

            WriteUpEvents(controls, RightFootLayer);

            BasisAnimationRiggingHelper.CreateTwoBone(this, driver, feet, References.RightUpperLeg, References.RightLowerLeg, References.rightFoot, BasisBoneTrackedRole.RightFoot, BasisBoneTrackedRole.RightLowerLeg, true, out RightFootTwoBoneIK, false, true);
        }
        public void LeftToe(BasisLocalBoneDriver driver)
        {
            GameObject LeftToe = CreateOrGetRig("LeftToe", false, out LeftToeRig, out LeftToeLayer);
            if (driver.FindBone(out BasisBoneControl Control, BasisBoneTrackedRole.LeftToes))
            {
                WriteUpEvents(new List<BasisBoneControl>() { Control }, LeftToeLayer);
            }
            LeftToeConstraint = BasisAnimationRiggingHelper.Damp(this, driver, LeftToe, References.leftToes, BasisBoneTrackedRole.LeftToes, 0, 0);
        }
        public void RightToe(BasisLocalBoneDriver driver)
        {
            GameObject RightToe = CreateOrGetRig("RightToe", false, out RightToeRig, out RightToeLayer);
            if (driver.FindBone(out BasisBoneControl Control, BasisBoneTrackedRole.RightToes))
            {
                WriteUpEvents(new List<BasisBoneControl>() { Control }, RightToeLayer);
            }
            RightToeConstraint = BasisAnimationRiggingHelper.Damp(this, driver, RightToe, References.rightToes, BasisBoneTrackedRole.RightToes, 0, 0);
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
        /// <summary>
        /// Clears on a calibration, setting up event listeners for a list of controls.
        /// </summary>
        /// <param name="Controls">List of BasisBoneControl objects</param>
        /// <param name="Layer">The RigLayer to update</param>
        public void WriteUpEvents(List<BasisBoneControl> Controls, RigLayer Layer)
        {
            foreach (var control in Controls)
            {
                // Add event listener for each control to update Layer's active state when HasRigLayer changes
                control.OnHasRigChanged += delegate { UpdateLayerActiveState(Controls, Layer); };
                control.HasEvents = true;
            }

            // Set the initial state based on the current controls' states
            UpdateLayerActiveState(Controls, Layer);
        }

        // Define a method to update the active state of the Layer based on the list of controls
        void UpdateLayerActiveState(List<BasisBoneControl> Controls, RigLayer Layer)
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
            GameObject RigGameobject = BasisAnimationRiggingHelper.CreateAndSetParent(Player.BasisAvatar.Animator.transform, $"Rig {Role}");
            Rig = BasisHelpers.GetOrAddComponent<Rig>(RigGameobject);
            Rigs.Add(Rig);
            RigLayer = new RigLayer(Rig, Enabled);
            Builder.layers.Add(RigLayer);
            return RigGameobject;
        }
        public void SimulateAnimatorAndIk(float DeltaTime)
        {
            Builder.SyncLayers();
            PlayableGraph.Evaluate(DeltaTime);
        }
    }
}
