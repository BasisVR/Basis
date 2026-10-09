using Basis.IK;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Drivers;
using Unity.Collections;
using UnityEngine;
namespace Basis.Scripts.Drivers
{
    public static class BasisEerieMovementSetup
    {
        public static float AppliedAvatarScale()
        {
            BasisLocalAvatarDriver driver = BasisLocalPlayer.Instance != null ? BasisLocalPlayer.Instance.LocalAvatarDriver : null;
            float scale = driver != null && driver.ScaleAvatarModification != null ? driver.ScaleAvatarModification.ApplyScale : 1f;
            return float.IsNaN(scale) || float.IsInfinity(scale) || scale <= 0f ? 1f : scale;
        }
        public static void SetDefaultValues(ref BasisEerieMovement job)
        {

            job.offsetRotationHead = job.offsetRotationLeftFoot = job.offsetRotationRightFoot = Quaternion.identity;
            job.offsetRotationLeftHand = job.offsetRotationRightHand = Quaternion.identity;
            job.tposeLeftLowerArmTwistBind = job.tposeLeftLowerArmChildBind = Quaternion.identity;
            job.tposeRightLowerArmTwistBind = job.tposeRightLowerArmChildBind = Quaternion.identity;
            job.tposeLeftUpperArmTwistBind = job.tposeLeftUpperArmChildBind = Quaternion.identity;
            job.tposeRightUpperArmTwistBind = job.tposeRightUpperArmChildBind = Quaternion.identity;
            job.tposeArmFitScale = job.tposeTorsoFitScale = 1f;

            job.playerUp = Vector3.up;

            job.minHeadSpineHeight = 0f;
            job.minFactor = 0.95f;
            job.maxFactor = 1.05f;
            job.spineMaxIterations = 20;
            job.spineTolerance = 0.001f;
            job.chainChestIdx = -1;

            job.targetPositionHips = Vector3.zero;
            job.targetRotationHips = Quaternion.identity;
            job.offsetRotationHips = Quaternion.identity;

            job.leftDrivenTargetRot = job.rightDrivenTargetRot = Quaternion.identity;

            job.chestRadius = Basis.BasisUI.BasisSettingsDefaults.FBIKChestRadius.RawValue;
            job.collisionSkin = Basis.BasisUI.BasisSettingsDefaults.FBIKCollisionSkin.RawValue;
            job.collisionsEnabled = Basis.BasisUI.BasisSettingsDefaults.FBIKCollisionsEnabled.RawValue;
            job.handRadius = Basis.BasisUI.BasisSettingsDefaults.FBIKHandRadius.RawValue;
            job.handSkin = Basis.BasisUI.BasisSettingsDefaults.FBIKHandSkin.RawValue;
            job.protectElbow = Basis.BasisUI.BasisSettingsDefaults.FBIKProtectElbow.RawValue;
            job.collideTrackedElbow = Basis.BasisUI.BasisSettingsDefaults.FBIKCollideTrackedElbow.RawValue;

            job.shoulderSolveEnabled = Basis.BasisUI.BasisSettingsDefaults.FBIKShoulderSolveEnabled.RawValue;
            job.shoulderShrugEnabled = Basis.BasisUI.BasisSettingsDefaults.FBIKShoulderShrug.RawValue;
            job.shoulderElevationFactor = Basis.BasisUI.BasisSettingsDefaults.FBIKShoulderElevation.RawValue;
            job.shoulderProtractionFactor = Basis.BasisUI.BasisSettingsDefaults.FBIKShoulderProtraction.RawValue;
            job.shoulderMaxDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKShoulderMaxDeg.RawValue;
            job.shoulderTrackerBlendTime = Basis.BasisUI.BasisSettingsDefaults.FBIKShoulderTrackerBlend.RawValue;
            job.armJointLimits = Basis.BasisUI.BasisSettingsDefaults.FBIKArmJointLimits.RawValue;
            job.armReachSoftness = Basis.BasisUI.BasisSettingsDefaults.FBIKArmReachSoftness.RawValue;
            job.armSwivelSmoothTime = Basis.BasisUI.BasisSettingsDefaults.FBIKArmSwivelSmoothTime.RawValue;
            job.armSwivelMaxRateDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKArmSwivelMaxRate.RawValue;
            job.armSwivelSwitchDwell = Basis.BasisUI.BasisSettingsDefaults.FBIKArmSwivelSwitchDwell.RawValue;
            job.armPriorWeight = Basis.BasisUI.BasisSettingsDefaults.FBIKArmPriorWeight.RawValue;
            job.armPreviousWeight = Basis.BasisUI.BasisSettingsDefaults.FBIKArmPreviousWeight.RawValue;
            job.forearmPronationMaxDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKForearmPronationMax.RawValue;
            job.forearmSupinationMaxDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKForearmSupinationMax.RawValue;
            job.humeralInternalMaxDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKHumeralInternalMax.RawValue;
            job.humeralExternalMaxDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKHumeralExternalMax.RawValue;
            job.wristFlexionMaxDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKWristFlexionMax.RawValue;
            job.wristExtensionMaxDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKWristExtensionMax.RawValue;
            job.wristRadialMaxDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKWristRadialMax.RawValue;
            job.wristUlnarMaxDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKWristUlnarMax.RawValue;
            job.trackedKneeSwivelMinCutoffHz = Basis.BasisUI.BasisSettingsDefaults.FBIKTrackedKneeSwivelMinCutoffHz.RawValue;
            job.trackedKneeSwivelBeta = Basis.BasisUI.BasisSettingsDefaults.FBIKTrackedKneeSwivelBeta.RawValue;
            job.trackedKneeSwivelDerivCutoffHz = Basis.BasisUI.BasisSettingsDefaults.FBIKTrackedKneeSwivelDerivCutoffHz.RawValue;

            job.moveBodyBackWhenCrouching = Basis.BasisUI.BasisSettingsDefaults.FBIKMoveBodyBackWhenCrouching.RawValue;
            job.crouchDepth = 0f;
            job.standingHeadHeight = 0f;
            job.lowerArmTwistFraction = Basis.BasisUI.BasisSettingsDefaults.FBIKLowerArmTwistFraction.RawValue;
            job.upperArmTwistFraction = Basis.BasisUI.BasisSettingsDefaults.FBIKUpperArmTwistFraction.RawValue;

            job.spineAnatomicalRom = Basis.BasisUI.BasisSettingsDefaults.FBIKSpineAnatomicalRom.RawValue;
            job.legSwivelSmoothing = Basis.BasisUI.BasisSettingsDefaults.FBIKLegSwivelSmoothing.RawValue;
            job.kneeFootPoleHold = Basis.BasisUI.BasisSettingsDefaults.FBIKKneeFootPoleHold.RawValue;
            job.kneeFootPoleConditioning = Basis.BasisUI.BasisSettingsDefaults.FBIKKneeFootPoleConditioning.RawValue;

            job.slotPositions.Length = BasisEerieMovement.Count;
            job.slotRotations.Length = BasisEerieMovement.Count;
            job.slotOffsets.Length = BasisEerieMovement.Count;
            job.slotWeights.Length = BasisEerieMovement.Count;
            for (int i = 0; i < BasisEerieMovement.Count; i++)
            {
                job.slotPositions[i] = Vector3.zero;
                job.slotRotations[i] = Quaternion.identity;
                job.slotOffsets[i] = Quaternion.identity;
                job.slotWeights[i] = false;
            }
        }
        private static Quaternion BindLocal(Transform parent, Transform bone)
        {
            if (parent == null || bone == null)
            {
                return Quaternion.identity;
            }

            return Quaternion.Inverse(parent.rotation) * bone.rotation;
        }
        public static void Create(ref BasisEerieMovement job, BasisPoseSkeleton skeleton, BasisTransformMapping Mapping)
        {
            job.handleHips = skeleton.Bind(Mapping.Hips);
            job.handleChest = skeleton.Bind(Mapping.chest);
            job.handleNeck = skeleton.Bind(Mapping.neck);
            job.handleHead = skeleton.Bind(Mapping.head);
            job.handleLeftUpperLeg = skeleton.Bind(Mapping.LeftUpperLeg);
            job.handleLeftLowerLeg = skeleton.Bind(Mapping.LeftLowerLeg);
            job.handleLeftFoot = skeleton.Bind(Mapping.leftFoot);
            job.handleRightUpperLeg = skeleton.Bind(Mapping.RightUpperLeg);
            job.handleRightLowerLeg = skeleton.Bind(Mapping.RightLowerLeg);
            job.handleRightFoot = skeleton.Bind(Mapping.rightFoot);
            job.handleLeftToe = skeleton.Bind(Mapping.leftToe);
            job.handleRightToe = skeleton.Bind(Mapping.rightToe);
            job.handleLeftUpperArm = skeleton.Bind(Mapping.leftUpperArm);
            job.handleLeftLowerArm = skeleton.Bind(Mapping.leftLowerArm);
            job.handleLeftHand = skeleton.Bind(Mapping.leftHand);
            job.handleRightUpperArm = skeleton.Bind(Mapping.RightUpperArm);
            job.handleRightLowerArm = skeleton.Bind(Mapping.RightLowerArm);
            job.handleRightHand = skeleton.Bind(Mapping.rightHand);
            job.handleLeftUpperArmTwist = skeleton.Bind(Mapping.leftUpperArmTwist);
            job.handleLeftLowerArmTwist = skeleton.Bind(Mapping.leftLowerArmTwist);
            job.handleRightUpperArmTwist = skeleton.Bind(Mapping.RightUpperArmTwist);
            job.handleRightLowerArmTwist = skeleton.Bind(Mapping.RightLowerArmTwist);
            job.handleSpine = skeleton.Bind(Mapping.spine);
            job.handleUpperChest = skeleton.Bind(Mapping.Upperchest);
            job.handleLeftShoulder = skeleton.Bind(Mapping.leftShoulder);
            job.handleRightShoulder = skeleton.Bind(Mapping.RightShoulder);

            job.tposeLeftShoulderRot = Mapping.leftShoulder != null ? Mapping.leftShoulder.rotation : Quaternion.identity;
            job.tposeRightShoulderRot = Mapping.RightShoulder != null ? Mapping.RightShoulder.rotation : Quaternion.identity;
            job.tposeChestRot = Mapping.Upperchest != null ? Mapping.Upperchest.rotation : Mapping.chest != null ? Mapping.chest.rotation : Quaternion.identity;
            job.tposeLeftShoulderLocalDir = (Mapping.leftShoulder != null && Mapping.leftUpperArm != null) ? (Mapping.leftUpperArm.position - Mapping.leftShoulder.position).normalized : Vector3.left;
            job.tposeRightShoulderLocalDir = (Mapping.RightShoulder != null && Mapping.RightUpperArm != null) ? (Mapping.RightUpperArm.position - Mapping.RightShoulder.position).normalized : Vector3.right;

            float fallbackArmLength = 0.6f * BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale;
            job.tposeShoulderToHandLeft = (Mapping.leftShoulder != null && Mapping.leftHand != null) ? Vector3.Distance(Mapping.leftShoulder.position, Mapping.leftHand.position) : fallbackArmLength;
            job.tposeShoulderToHandRight = (Mapping.RightShoulder != null && Mapping.rightHand != null) ? Vector3.Distance(Mapping.RightShoulder.position, Mapping.rightHand.position) : fallbackArmLength;
            job.tposeClavicleLenLeft = (Mapping.leftShoulder != null && Mapping.leftUpperArm != null) ? Vector3.Distance(Mapping.leftShoulder.position, Mapping.leftUpperArm.position) : 0f;
            job.tposeClavicleLenRight = (Mapping.RightShoulder != null && Mapping.RightUpperArm != null) ? Vector3.Distance(Mapping.RightShoulder.position, Mapping.RightUpperArm.position) : 0f;
            job.tposeShoulderToElbowLeft = (Mapping.leftShoulder != null && Mapping.leftLowerArm != null) ? Vector3.Distance(Mapping.leftShoulder.position, Mapping.leftLowerArm.position) : 0f;
            job.tposeShoulderToElbowRight = (Mapping.RightShoulder != null && Mapping.RightLowerArm != null) ? Vector3.Distance(Mapping.RightShoulder.position, Mapping.RightLowerArm.position) : 0f;

            job.tposeLeftLowerArmTwistBind = BindLocal(Mapping.leftLowerArm, Mapping.leftLowerArmTwist);
            job.tposeLeftLowerArmChildBind = BindLocal(Mapping.leftLowerArm, Mapping.leftHand);
            job.tposeRightLowerArmTwistBind = BindLocal(Mapping.RightLowerArm, Mapping.RightLowerArmTwist);
            job.tposeRightLowerArmChildBind = BindLocal(Mapping.RightLowerArm, Mapping.rightHand);
            job.tposeLeftUpperArmTwistBind = BindLocal(Mapping.leftUpperArm, Mapping.leftUpperArmTwist);
            job.tposeLeftUpperArmChildBind = BindLocal(Mapping.leftUpperArm, Mapping.leftLowerArm);
            job.tposeRightUpperArmTwistBind = BindLocal(Mapping.RightUpperArm, Mapping.RightUpperArmTwist);
            job.tposeRightUpperArmChildBind = BindLocal(Mapping.RightUpperArm, Mapping.RightLowerArm);

            GenerateHeadToSpine(ref job, skeleton, Mapping);
            job.tposeArmFitScale = job.tposeTorsoFitScale = 1f;
            job.spineMaxIterations = 20;
            job.spineTolerance = 0.001f;
            job.chestSpring = new NativeArray<BasisChestSpringState>(1, Allocator.Persistent);
            job.armState = new NativeArray<BasisArmState>(BasisEerieMovement.armCount, Allocator.Persistent);
            job.legState = new NativeArray<BasisLegSlotState>(2, Allocator.Persistent);
            job.legDiagnostics = new NativeArray<BasisLegDiagnostics>(2, Allocator.Persistent);
            // Raliv initialization reads the bound pose just like SpineTest.Awake.
            // The stream must be assigned before that cache is built.
            job.poseStream = skeleton.Stream;
            job.InitalizeRalivSpineIK();
            BasisEeriePlanner.Bind(ref job);
        }
        static void BuildSpineAnatomy(ref BasisEerieMovement job, Transform[] chain, BasisTransformMapping Mapping)
        {
            int n = chain.Length;
            job.chainSpineRestFrames = new NativeArray<BasisSpineRestFrame>(n, Allocator.Persistent);
            if (Mapping.leftUpperArm == null || Mapping.RightUpperArm == null)
            {
                return;
            }
            Vector3 hipsRight = Mapping.RightUpperArm.position - Mapping.leftUpperArm.position;

            for (int i = 1; i <= n - 2; i++)
            {
                Transform bone = chain[i];
                Transform child = chain[i - 1];
                Transform parent = chain[i + 1];
                if (bone == null || child == null || parent == null)
                {
                    continue;
                }

                BasisSpineSegment segment;
                if (bone == Mapping.spine)
                {
                    segment = BasisSpineSegment.Lumbar;
                }
                else if (bone == Mapping.chest)
                {
                    segment = BasisSpineSegment.LowerThoracic;
                }
                else if (bone == Mapping.Upperchest)
                {
                    segment = BasisSpineSegment.UpperThoracic;
                }
                else if (bone == Mapping.neck)
                {
                    segment = BasisSpineSegment.Cervical;
                }
                else
                {
                    continue;
                }

                BasisSpineRestFrame frame = BasisSpineAnatomy.BuildRestFrame( bone.position, child.position, bone.rotation, parent.rotation, hipsRight);
                frame.Segment = segment;
                job.chainSpineRestFrames[i] = frame;
            }
        }
        public static void GenerateHeadToSpine(ref BasisEerieMovement job, BasisPoseSkeleton skeleton, BasisTransformMapping Mapping)
        {

            Transform[] candidates = { Mapping.head, Mapping.neck, Mapping.Upperchest, Mapping.chest, Mapping.spine, Mapping.Hips };
            bool solvable = Mapping.head != null && Mapping.spine != null && Mapping.Hips != null;
            int presentCount = 0;
            if (solvable)
            {
                for (int i = 0; i < candidates.Length; i++)
                {
                    if (candidates[i] != null)
                    {
                        presentCount++;
                    }
                }
            }
            Transform[] HeadToSpine = new Transform[presentCount];
            int write = 0;
            job.chainChestIdx = -1;
            for (int i = 0; write < presentCount && i < candidates.Length; i++)
            {
                if (candidates[i] == null)
                {
                    continue;
                }
                if (Mapping.chest != null && candidates[i] == Mapping.chest)
                {
                    job.chainChestIdx = write;
                }
                HeadToSpine[write++] = candidates[i];
            }
            int SpineToHeadLength = HeadToSpine.Length;
            job.chainHeadToSpine = new NativeArray<BasisBoneHandle>(SpineToHeadLength, Allocator.Persistent);
            BuildSpineAnatomy(ref job, HeadToSpine, Mapping);

            for (int i = 0; i < SpineToHeadLength; i++)
            {
                job.chainHeadToSpine[i] = skeleton.Bind(HeadToSpine[i]);
            }
            Vector3 headToHips = Mapping.Hips != null && Mapping.head != null ? Mapping.head.position - Mapping.Hips.position : Vector3.zero;
            if (Mapping.head != null && Mapping.neck != null)
            {
                job.tposeHeadToNeckLocal = Quaternion.Inverse(Mapping.head.rotation) * (Mapping.neck.position - Mapping.head.position);
            }
            else
            {
                job.tposeHeadToNeckLocal = Vector3.zero;
            }

            if (Mapping.Hips != null && Mapping.neck != null)
            {
                job.tposeLengthNeckToHips = (Mapping.neck.position - Mapping.Hips.position);
            }
            else
            {
                job.tposeLengthNeckToHips = headToHips;
            }

            job.tposeBakeScale = AppliedAvatarScale();
        }
    }
}
