using Basis.IK;
using Basis.Scripts.Common;
using Basis.Scripts.Drivers;
using Unity.Collections;
using UnityEngine;
namespace Basis.Scripts.Drivers
{
    public static class BasisEerieMovementSetup
    {
        public const float ChestHeadBudgetMeters = 0.005f;
        public static void SetDefaultValues(ref BasisEerieMovement job)
        {
            job.ikLockMode = BasisIKLockMode.LockHead;

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
            job.elbowDragEnabled = Basis.BasisUI.BasisSettingsDefaults.FBIKElbowDrag.RawValue;
            job.elbowDragHz = Basis.BasisUI.BasisSettingsDefaults.FBIKElbowDragHz.RawValue;
            job.collideTrackedElbow = Basis.BasisUI.BasisSettingsDefaults.FBIKCollideTrackedElbow.RawValue;

            job.shoulderSolveEnabled = Basis.BasisUI.BasisSettingsDefaults.FBIKShoulderSolveEnabled.RawValue;
            job.shoulderShrugEnabled = Basis.BasisUI.BasisSettingsDefaults.FBIKShoulderShrug.RawValue;
            job.shoulderElevationFactor = Basis.BasisUI.BasisSettingsDefaults.FBIKShoulderElevation.RawValue;
            job.shoulderProtractionFactor = Basis.BasisUI.BasisSettingsDefaults.FBIKShoulderProtraction.RawValue;
            job.shoulderCoupleRatio = Basis.BasisUI.BasisSettingsDefaults.FBIKShoulderCoupleRatio.RawValue;
            job.shoulderMaxDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKShoulderMaxDeg.RawValue;
            job.shoulderSlideStartDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKShoulderSlideStartDeg.RawValue;
            job.shoulderSlideMaxDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKShoulderSlideMaxDeg.RawValue;
            job.shoulderSlideFraction = Basis.BasisUI.BasisSettingsDefaults.FBIKShoulderSlideFraction.RawValue;
            job.thoracicBendStiffen = Basis.BasisUI.BasisSettingsDefaults.FBIKThoracicBendStiffen.RawValue;
            job.spineTautBandFrac = Basis.BasisUI.BasisSettingsDefaults.FBIKSpineTautBandFrac.RawValue;
            job.bendTwistCoupling = Basis.BasisUI.BasisSettingsDefaults.FBIKBendTwistCoupling.RawValue;
            job.neckGazeFollowMaxDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKNeckGazeFollowMaxDeg.RawValue;
            job.trunkCounterbalanceMaxSpineFrac = Basis.BasisUI.BasisSettingsDefaults.FBIKTrunkCounterbalanceMaxFrac.RawValue;
            job.chestIkWeight = Basis.BasisUI.BasisSettingsDefaults.FBIKChestIkWeight.RawValue;
            job.chestIkIterations = Mathf.Max(1, Mathf.RoundToInt(Basis.BasisUI.BasisSettingsDefaults.FBIKChestIkIterations.RawValue));
            job.chestIkHeadRestoreSweeps = Mathf.Max(1, Mathf.RoundToInt(Basis.BasisUI.BasisSettingsDefaults.FBIKChestIkHeadRestoreSweeps.RawValue));
            job.chestPosPullMaxDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKChestPosPullMaxDeg.RawValue;
            job.chestPullMaxDist = Basis.BasisUI.BasisSettingsDefaults.FBIKChestPullMaxDist.RawValue;
            job.chestHeadBudget = ChestHeadBudgetMeters;
            job.chestFollowChestShare = Basis.BasisUI.BasisSettingsDefaults.FBIKChestFollowChestShare.RawValue;
            job.trackedKneeSwivelMinCutoffHz = Basis.BasisUI.BasisSettingsDefaults.FBIKTrackedKneeSwivelMinCutoffHz.RawValue;
            job.trackedKneeSwivelBeta = Basis.BasisUI.BasisSettingsDefaults.FBIKTrackedKneeSwivelBeta.RawValue;
            job.trackedKneeSwivelDerivCutoffHz = Basis.BasisUI.BasisSettingsDefaults.FBIKTrackedKneeSwivelDerivCutoffHz.RawValue;

            job.maxBendDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKMaxBendDeg.RawValue;
            job.maxChestDeltaDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKMaxChestDelta.RawValue;
            job.spineBendPitch = Basis.BasisUI.BasisSettingsDefaults.FBIKSpineBendPitch.RawValue;
            job.spineBendYaw = Basis.BasisUI.BasisSettingsDefaults.FBIKSpineBendYaw.RawValue;
            job.spineBendRoll = Basis.BasisUI.BasisSettingsDefaults.FBIKSpineBendRoll.RawValue;
            job.upperChestBendPitch = Basis.BasisUI.BasisSettingsDefaults.FBIKUpperChestBendPitch.RawValue;
            job.upperChestBendYaw = Basis.BasisUI.BasisSettingsDefaults.FBIKUpperChestBendYaw.RawValue;
            job.upperChestBendRoll = Basis.BasisUI.BasisSettingsDefaults.FBIKUpperChestBendRoll.RawValue;
            job.chestBendPitch = Basis.BasisUI.BasisSettingsDefaults.FBIKChestBendPitch.RawValue;
            job.chestBendYaw = Basis.BasisUI.BasisSettingsDefaults.FBIKChestBendYaw.RawValue;
            job.chestBendRoll = Basis.BasisUI.BasisSettingsDefaults.FBIKChestBendRoll.RawValue;
            job.neckYawShare = Basis.BasisUI.BasisSettingsDefaults.FBIKNeckYawShare.RawValue;
            job.spineStretchMax = Basis.BasisUI.BasisSettingsDefaults.FBIKSpineStretchMax.RawValue;
            job.hipHingeStartDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKHipHingeStartDeg.RawValue;
            job.hipHingeMaxAddDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKHipHingeMaxAddDeg.RawValue;
            job.chestSpringHz = Basis.BasisUI.BasisSettingsDefaults.FBIKChestSpringHz.RawValue;
            job.chestSpringDamping = Basis.BasisUI.BasisSettingsDefaults.FBIKChestSpringDamping.RawValue;
            job.spineMaxForwardDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKSpineMaxForwardDeg.RawValue;
            job.spineMaxBackwardDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKSpineMaxBackwardDeg.RawValue;
            job.spineMaxLateralDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKSpineMaxLateralDeg.RawValue;
            job.spineSquishBoost = Basis.BasisUI.BasisSettingsDefaults.FBIKSpineSquishBoost.RawValue;
            job.spineGazeFollow = Basis.BasisUI.BasisSettingsDefaults.FBIKSpineGazeFollow.RawValue;
            job.neckGazeFollow = Basis.BasisUI.BasisSettingsDefaults.FBIKNeckGazeFollow.RawValue;
            job.neckExtensionDamp = Basis.BasisUI.BasisSettingsDefaults.FBIKNeckExtensionDamp.RawValue;
            job.neckFlexionDamp = Basis.BasisUI.BasisSettingsDefaults.FBIKNeckFlexionDamp.RawValue;
            job.moveBodyBackWhenCrouching = Basis.BasisUI.BasisSettingsDefaults.FBIKMoveBodyBackWhenCrouching.RawValue;
            job.crouchDepth = 0f;
            job.standingHeadHeight = 0f;
            job.trunkCounterbalance = Basis.BasisUI.BasisSettingsDefaults.FBIKTrunkCounterbalance.RawValue;
            job.swingSmoothRateDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKElbowSwingEnabled.RawValue ? Basis.BasisUI.BasisSettingsDefaults.FBIKSwingSmoothRate.RawValue : 0f;
            job.chestArmSwingFactor = Basis.BasisUI.BasisSettingsDefaults.FBIKChestArmSwingFactor.RawValue;
            job.chestArmSwingMaxDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKChestArmSwingMaxDeg.RawValue;
            job.lowerArmTwistFraction = Basis.BasisUI.BasisSettingsDefaults.FBIKLowerArmTwistFraction.RawValue;
            job.upperArmTwistFraction = Basis.BasisUI.BasisSettingsDefaults.FBIKUpperArmTwistFraction.RawValue;

            job.anatDifferentialStiffness = Basis.BasisUI.BasisSettingsDefaults.FBIKAnatDifferentialStiffness.RawValue;
            job.anatShoulderSlide = Basis.BasisUI.BasisSettingsDefaults.FBIKAnatShoulderSlide.RawValue;
            job.anatCervicalLordosis = Basis.BasisUI.BasisSettingsDefaults.FBIKAnatCervicalLordosis.RawValue;
            job.anatPelvicTwistRouting = Basis.BasisUI.BasisSettingsDefaults.FBIKAnatPelvicTwistRouting.RawValue;
            job.spineAnatomicalRom = Basis.BasisUI.BasisSettingsDefaults.FBIKSpineAnatomicalRom.RawValue;
            job.chestIkTarget = Basis.BasisUI.BasisSettingsDefaults.FBIKChestIKTarget.RawValue;
            job.legSwivelSmoothing = Basis.BasisUI.BasisSettingsDefaults.FBIKLegSwivelSmoothing.RawValue;
            job.kneeFootPoleHold = Basis.BasisUI.BasisSettingsDefaults.FBIKKneeFootPoleHold.RawValue;
            job.kneeFootPoleConditioning = Basis.BasisUI.BasisSettingsDefaults.FBIKKneeFootPoleConditioning.RawValue;
            job.lordosisPitchGainDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKLordosisPitchGainDeg.RawValue;
            job.lordosisBaseDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKLordosisBaseDeg.RawValue;
            job.lordosisNeckShare = Basis.BasisUI.BasisSettingsDefaults.FBIKLordosisNeckShare.RawValue;
            job.lordosisMaxHeadPitchDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKLordosisMaxHeadPitchDeg.RawValue;
            job.lordosisExtremeStartDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKLordosisExtremeStartDeg.RawValue;
            job.lordosisExtremeFullDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKLordosisExtremeFullDeg.RawValue;
            job.lordosisExtremeRollForwardMaxDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKLordosisExtremeRollForwardMaxDeg.RawValue;
            job.lordosisExtremeRollBackwardMaxDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKLordosisExtremeRollBackwardMaxDeg.RawValue;
            job.lordosisExtremeHipsHorizontalMax = Basis.BasisUI.BasisSettingsDefaults.FBIKLordosisExtremeHipsHorizontalMax.RawValue;
            job.lordosisExtremeChestHorizontalMax = Basis.BasisUI.BasisSettingsDefaults.FBIKLordosisExtremeChestHorizontalMax.RawValue;
            job.lordosisExtremeHipsHorizontalLookUp = Basis.BasisUI.BasisSettingsDefaults.FBIKLordosisExtremeHipsHorizontalLookUp.RawValue;
            job.lordosisExtremeChestHorizontalLookUp = Basis.BasisUI.BasisSettingsDefaults.FBIKLordosisExtremeChestHorizontalLookUp.RawValue;
            job.lordosisExtremeHipsDownMax = Basis.BasisUI.BasisSettingsDefaults.FBIKLordosisExtremeHipsDownMax.RawValue;
            job.lordosisExtremeChestDownMax = Basis.BasisUI.BasisSettingsDefaults.FBIKLordosisExtremeChestDownMax.RawValue;
            job.lordosisExtremeHipsDownLookUp = Basis.BasisUI.BasisSettingsDefaults.FBIKLordosisExtremeHipsDownLookUp.RawValue;
            job.lordosisExtremeChestDownLookUp = Basis.BasisUI.BasisSettingsDefaults.FBIKLordosisExtremeChestDownLookUp.RawValue;

            job.spineCCDRelax = Basis.BasisUI.BasisSettingsDefaults.FBIKSpineCCDRelax.RawValue;
            job.neckMaxConeDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKNeckMaxConeDeg.RawValue;
            job.spineTwistKeep = Basis.BasisUI.BasisSettingsDefaults.FBIKSpineTwistKeep.RawValue;
            job.spineNeckTwistKeep = Basis.BasisUI.BasisSettingsDefaults.FBIKSpineNeckTwistKeep.RawValue;

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
            job.swingContinuity = new NativeArray<BasisSwingContinuityState>(BasisEerieMovement.swingCount, Allocator.Persistent);
            job.armState = new NativeArray<BasisArmSlotState>(BasisEerieMovement.swingCount, Allocator.Persistent);
            job.legState = new NativeArray<BasisLegSlotState>(2, Allocator.Persistent);
            job.legDiagnostics = new NativeArray<BasisLegDiagnostics>(2, Allocator.Persistent);
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

            job.tposeBakeScale = BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale;
        }
    }
}
