using Basis.IK;
using Basis.Scripts.Common;
using Basis.Scripts.Drivers;
using Unity.Collections;
using UnityEngine;
namespace Basis.Scripts.Drivers
{
    /// <summary>
    /// Builds a <see cref="BasisEerieMovement"/> for an avatar. This is the seam that keeps the solver
    /// itself framework-free: everything that reads persisted settings, the avatar's transform mapping or
    /// the height driver lives here, on the framework side, rather than inside the job.
    /// </summary>
    public static class BasisEerieMovementSetup
    {
        public static void SetDefaultValues(ref BasisEerieMovement job)
        {
            job.hasChestTracker = true;
            job.hintWeightLeftLowerLeg = job.hintWeightRightLowerLeg = 1f;
            job.enabledSpineIK = true;
            job.hasHipsTracker = false;
            job.footIsTrackerLeftLeg = job.footIsTrackerRightLeg = false;
            job.enabledLeftLowerLeg = job.enabledRightLowerLeg = 1f;
            job.hintIsTrackerLeftLowerLeg = job.hintIsTrackerRightLowerLeg = false;
            job.ikLockMode = BasisIKLockMode.LockHead;

            job.hintWeightLeftHand = job.hintWeightRightHand = true;
            job.enabledLeftHand = job.enabledRightHand = 1f;
            job.enabledLeftShoulder = job.enabledRightShoulder = false;
            job.offsetRotationHead = job.offsetRotationLeftFoot = job.offsetRotationRightFoot = Quaternion.identity;
            job.offsetRotationLeftHand = job.offsetRotationRightHand = Quaternion.identity;

            job.playerUp = Vector3.up;

            // Avatar-measured; the rig driver overwrites both once the T-pose spine is known.
            job.minHeadSpineHeight = 0f;
            job.minFactor = 0.95f;
            job.maxFactor = 1.05f;
            job.spineMaxIterations = 20;
            job.spineTolerance = 0.001f;

            job.targetPositionHips = Vector3.zero;
            job.targetRotationHips = Quaternion.identity;
            job.offsetRotationHips = Quaternion.identity;

            // Integrated driven TR defaults

            job.leftDrivenTargetRot = job.rightDrivenTargetRot = Quaternion.identity;
            job.leftToeEnabled = false;
            job.rightToeEnabled = false;

            // Chest/hand capsule defaults — read from persisted settings
            job.chestRadius = Basis.BasisUI.BasisSettingsDefaults.FBIKChestRadius.RawValue;
            job.collisionSkin = Basis.BasisUI.BasisSettingsDefaults.FBIKCollisionSkin.RawValue;
            job.collisionsEnabled = Basis.BasisUI.BasisSettingsDefaults.FBIKCollisionsEnabled.RawValue;
            job.handRadius = Basis.BasisUI.BasisSettingsDefaults.FBIKHandRadius.RawValue;
            job.handSkin = Basis.BasisUI.BasisSettingsDefaults.FBIKHandSkin.RawValue;
            job.protectElbow = Basis.BasisUI.BasisSettingsDefaults.FBIKProtectElbow.RawValue;
            job.elbowDragEnabled = Basis.BasisUI.BasisSettingsDefaults.FBIKElbowDrag.RawValue;
            job.elbowDragHz = Basis.BasisUI.BasisSettingsDefaults.FBIKElbowDragHz.RawValue;
            job.collideTrackedElbow = Basis.BasisUI.BasisSettingsDefaults.FBIKCollideTrackedElbow.RawValue;
            job.useNeuralPole = false;

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
            job.moveBodyBackWhenCrouching = Basis.BasisUI.BasisSettingsDefaults.FBIKMoveBodyBackWhenCrouching.RawValue;
            job.crouchDepth = 0f;
            job.standingHeadHeight = 0f; // 0 = sit-back inert until the rig driver packs the real height
            job.trunkCounterbalance = Basis.BasisUI.BasisSettingsDefaults.FBIKTrunkCounterbalance.RawValue;
            job.swingSmoothRateDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKElbowSwingEnabled.RawValue
                ? Basis.BasisUI.BasisSettingsDefaults.FBIKSwingSmoothRate.RawValue
                : 0f;
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
            job.lordosisExtremeHipsDownMax = Basis.BasisUI.BasisSettingsDefaults.FBIKLordosisExtremeHipsDownMax.RawValue;
            job.lordosisExtremeChestDownMax = Basis.BasisUI.BasisSettingsDefaults.FBIKLordosisExtremeChestDownMax.RawValue;
            job.lordosisExtremeHipsDownLookUp = Basis.BasisUI.BasisSettingsDefaults.FBIKLordosisExtremeHipsDownLookUp.RawValue;
            job.lordosisExtremeChestDownLookUp = Basis.BasisUI.BasisSettingsDefaults.FBIKLordosisExtremeChestDownLookUp.RawValue;
            // 1.0 (was 0.8), retuned against the mocap corpus: full relax is strictly better measured —
            // closer to the human spine AND a quieter standing noise floor. See FBIKSpineCCDRelax.
            job.spineCCDRelax = Basis.BasisUI.BasisSettingsDefaults.FBIKSpineCCDRelax.RawValue;
            job.neckMaxConeDeg = Basis.BasisUI.BasisSettingsDefaults.FBIKNeckMaxConeDeg.RawValue;
            job.spineTwistKeep = Basis.BasisUI.BasisSettingsDefaults.FBIKSpineTwistKeep.RawValue;
            job.spineNeckTwistKeep = Basis.BasisUI.BasisSettingsDefaults.FBIKSpineNeckTwistKeep.RawValue;

            // Slots: identity rotations, zero positions, weights disabled.
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
        public static void Create(ref BasisEerieMovement job, BasisPoseSkeleton skeleton, BasisTransformMapping Mapping)
        {
            job.handleHips = BindHandle(skeleton, Mapping.Hips);
            job.handleChest = BindHandle(skeleton, Mapping.chest);
            job.handleNeck = BindHandle(skeleton, Mapping.neck);
            job.handleHead = BindHandle(skeleton, Mapping.head);
            job.handleLeftUpperLeg = BindHandle(skeleton, Mapping.LeftUpperLeg);
            job.handleLeftLowerLeg = BindHandle(skeleton, Mapping.LeftLowerLeg);
            job.handleLeftFoot = BindHandle(skeleton, Mapping.leftFoot);
            job.handleRightUpperLeg = BindHandle(skeleton, Mapping.RightUpperLeg);
            job.handleRightLowerLeg = BindHandle(skeleton, Mapping.RightLowerLeg);
            job.handleRightFoot = BindHandle(skeleton, Mapping.rightFoot);
            job.handleLeftToe = BindHandle(skeleton, Mapping.leftToe);
            job.handleRightToe = BindHandle(skeleton, Mapping.rightToe);
            job.handleLeftUpperArm = BindHandle(skeleton, Mapping.leftUpperArm);
            job.handleLeftLowerArm = BindHandle(skeleton, Mapping.leftLowerArm);
            job.handleLeftHand = BindHandle(skeleton, Mapping.leftHand);
            job.handleRightUpperArm = BindHandle(skeleton, Mapping.RightUpperArm);
            job.handleRightLowerArm = BindHandle(skeleton, Mapping.RightLowerArm);
            job.handleRightHand = BindHandle(skeleton, Mapping.rightHand);
            job.handleLeftUpperArmTwist = BindHandle(skeleton, Mapping.leftUpperArmTwist);
            job.handleLeftLowerArmTwist = BindHandle(skeleton, Mapping.leftLowerArmTwist);
            job.handleRightUpperArmTwist = BindHandle(skeleton, Mapping.RightUpperArmTwist);
            job.handleRightLowerArmTwist = BindHandle(skeleton, Mapping.RightLowerArmTwist);
            job.handleSpine = BindHandle(skeleton, Mapping.spine);
            job.handleUpperChest = BindHandle(skeleton, Mapping.Upperchest);
            job.handleLeftShoulder = BindHandle(skeleton, Mapping.leftShoulder);
            job.handleRightShoulder = BindHandle(skeleton, Mapping.RightShoulder);

            // Baked T-pose data for shoulder solve
            job.tposeLeftShoulderRot = Mapping.leftShoulder != null ? Mapping.leftShoulder.rotation : Quaternion.identity;
            job.tposeRightShoulderRot = Mapping.RightShoulder != null ? Mapping.RightShoulder.rotation : Quaternion.identity;
            job.tposeChestRot = Mapping.chest != null ? Mapping.chest.rotation : Quaternion.identity;
            job.tposeLeftShoulderLocalDir = (Mapping.leftShoulder != null && Mapping.leftUpperArm != null)
                ? (Mapping.leftUpperArm.position - Mapping.leftShoulder.position).normalized : Vector3.left;
            job.tposeRightShoulderLocalDir = (Mapping.RightShoulder != null && Mapping.RightUpperArm != null)
                ? (Mapping.RightUpperArm.position - Mapping.RightShoulder.position).normalized : Vector3.right;
            // 0.6 m is an adult arm; on a small avatar it is the same shoulder-inert / shrug-latched failure
            // a stale bake produces, so the fallback tracks avatar size too.
            float fallbackArmLength = 0.6f * BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale;
            job.tposeShoulderToHandLeft = (Mapping.leftShoulder != null && Mapping.leftHand != null)
                ? Vector3.Distance(Mapping.leftShoulder.position, Mapping.leftHand.position) : fallbackArmLength;
            job.tposeShoulderToHandRight = (Mapping.RightShoulder != null && Mapping.rightHand != null)
                ? Vector3.Distance(Mapping.RightShoulder.position, Mapping.rightHand.position) : fallbackArmLength;
            job.tposeClavicleLenLeft = (Mapping.leftShoulder != null && Mapping.leftUpperArm != null)
                ? Vector3.Distance(Mapping.leftShoulder.position, Mapping.leftUpperArm.position) : 0f;
            job.tposeClavicleLenRight = (Mapping.RightShoulder != null && Mapping.RightUpperArm != null)
                ? Vector3.Distance(Mapping.RightShoulder.position, Mapping.RightUpperArm.position) : 0f;
            job.tposeShoulderToElbowLeft = (Mapping.leftShoulder != null && Mapping.leftLowerArm != null)
                ? Vector3.Distance(Mapping.leftShoulder.position, Mapping.leftLowerArm.position) : 0f;
            job.tposeShoulderToElbowRight = (Mapping.RightShoulder != null && Mapping.RightLowerArm != null)
                ? Vector3.Distance(Mapping.RightShoulder.position, Mapping.RightLowerArm.position) : 0f;

            // Pair each slot with its bone handle, in HumanBodyBones order.
            job.slotHandles.Length = BasisEerieMovement.Count;
            job.slotHandles[0] = job.handleHips;
            job.slotHandles[1] = job.handleLeftUpperLeg;
            job.slotHandles[2] = job.handleRightUpperLeg;
            job.slotHandles[3] = job.handleLeftLowerLeg;
            job.slotHandles[4] = job.handleRightLowerLeg;
            job.slotHandles[5] = job.handleLeftFoot;
            job.slotHandles[6] = job.handleRightFoot;
            job.slotHandles[7] = job.handleSpine;
            job.slotHandles[8] = job.handleChest;
            job.slotHandles[9] = job.handleNeck;
            job.slotHandles[10] = job.handleHead;
            job.slotHandles[11] = job.handleLeftShoulder;
            job.slotHandles[12] = job.handleRightShoulder;
            job.slotHandles[13] = job.handleLeftUpperArm;
            job.slotHandles[14] = job.handleRightUpperArm;
            job.slotHandles[15] = job.handleLeftLowerArm;
            job.slotHandles[16] = job.handleRightLowerArm;
            job.slotHandles[17] = job.handleLeftHand;
            job.slotHandles[18] = job.handleRightHand;
            job.slotHandles[19] = job.handleLeftToe;
            job.slotHandles[20] = job.handleRightToe;
            job.slotHandles[BasisEerieMovement.UpperChestSlot] = job.handleUpperChest;

            GenerateHeadToSpine(ref job, skeleton, Mapping);
            job.spineMaxIterations = 20;
            job.spineTolerance = 0.001f;
            job.chestSpringState = new NativeArray<Vector3>(2, Allocator.Persistent);
            job.chestSpringInit = new NativeArray<int>(1, Allocator.Persistent);

            job.swingLastDir = new NativeArray<Vector3>(BasisEerieMovement.k_SwingCount, Allocator.Persistent);
            job.swingLastAxis = new NativeArray<Vector3>(BasisEerieMovement.k_SwingCount, Allocator.Persistent);
            job.swingLastTarget = new NativeArray<Vector3>(BasisEerieMovement.k_SwingCount, Allocator.Persistent);
            job.swingContinuityInit = new NativeArray<int>(BasisEerieMovement.k_SwingCount, Allocator.Persistent);
            job.swingCollided = new NativeArray<int>(BasisEerieMovement.k_SwingCount, Allocator.Persistent);
            job.swingSmoothState = new NativeArray<int>(BasisEerieMovement.k_SwingCount, Allocator.Persistent);
            job.swingHintBend = new NativeArray<Vector3>(BasisEerieMovement.k_SwingCount, Allocator.Persistent);
            job.swingHintAxis = new NativeArray<Vector3>(BasisEerieMovement.k_SwingCount, Allocator.Persistent);
            job.swingHintDrag = new NativeArray<Vector3>(BasisEerieMovement.k_SwingCount, Allocator.Persistent);
            job.swingHintBodyRot = new NativeArray<Quaternion>(BasisEerieMovement.k_SwingCount, Allocator.Persistent);
            job.swingHintInit = new NativeArray<int>(BasisEerieMovement.k_SwingCount, Allocator.Persistent);
            job.legSwivelRaw = new NativeArray<Vector3>(2, Allocator.Persistent);
            job.legSwivelSmooth = new NativeArray<Vector3>(2, Allocator.Persistent);
            job.legSwivelInit = new NativeArray<int>(2, Allocator.Persistent);
            job.legDiagnostics = new NativeArray<BasisLegDiagnostics>(2, Allocator.Persistent);
        }
        static void BuildSpineAnatomy(ref BasisEerieMovement job, Transform[] chain, BasisTransformMapping Mapping)
        {
            int n = chain.Length;
            job.chainSpineRestFrames = new NativeArray<BasisSpineRestFrame>(n, Allocator.Persistent);
            job.chainSpineRoms = new NativeArray<BasisSpineRom>(n, Allocator.Persistent);
            if (Mapping.leftUpperArm == null || Mapping.RightUpperArm == null)
            {
                return;   // every frame stays Valid=false, so the guard is a no-op. Decline, never guess.
            }
            Vector3 hipsRight = Mapping.RightUpperArm.position - Mapping.leftUpperArm.position;

            for (int i = 1; i <= n - 2; i++)   // skip the head (0) and the hips (n-1)
            {
                Transform bone = chain[i];
                Transform child = chain[i - 1];    // the chain runs tip -> root, so the CHILD is i-1
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

                job.chainSpineRestFrames[i] = BasisSpineAnatomy.BuildRestFrame(
                    bone.position, child.position, bone.rotation, parent.rotation, hipsRight);
                job.chainSpineRoms[i] = BasisSpineAnatomy.Rom(segment);
            }
        }
        public static void GenerateHeadToSpine(ref BasisEerieMovement job, BasisPoseSkeleton skeleton, BasisTransformMapping Mapping)
        {
            var HeadToSpine = Mapping.Upperchest != null
                ? new Transform[] { Mapping.head, Mapping.neck, Mapping.Upperchest, Mapping.chest, Mapping.spine, Mapping.Hips }
                : new Transform[] { Mapping.head, Mapping.neck, Mapping.chest, Mapping.spine, Mapping.Hips };
            int SpineToHeadLength = HeadToSpine.Length;
            job.chainHeadToSpine = new NativeArray<BasisBoneHandle>(SpineToHeadLength, Allocator.Persistent);
            BuildSpineAnatomy(ref job, HeadToSpine, Mapping);

            for (int i = 0; i < SpineToHeadLength; i++)
            {
                job.chainHeadToSpine[i] = skeleton.Bind(HeadToSpine[i]);
            }
            if (Mapping.Hips != null && Mapping.head != null)
            {
                job.tposeLengthHeadToHips = (Mapping.head.position - Mapping.Hips.position);
            }
            else
            {
                job.tposeLengthHeadToHips = Vector3.zero;
            }
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
                job.tposeLengthNeckToHips = job.tposeLengthHeadToHips;
            }

            // Record the size these were measured at, so a later rescale can carry them along.
            job.tposeBakeScale = BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale;
        }
        internal static BasisBoneHandle BindHandle(BasisPoseSkeleton skeleton, Transform t) => (t != null) ? skeleton.Bind(t) : default;
    }
}
