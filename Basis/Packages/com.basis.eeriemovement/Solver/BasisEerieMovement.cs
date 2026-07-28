using System.Runtime.CompilerServices;
using Basis.Scripts.Common;
using Unity.Collections;
using UnityEngine;
namespace Basis.IK
{
    /// <summary>
    /// Full-body pass: Head + Legs + Hips + Dual Driven TR + Dual TwoBoneIK Hands (with chest/hand capsule & elbow protection).
    /// All driven via a single job.
    /// </summary>
    [Unity.Burst.BurstCompile]
    public partial struct BasisEerieMovement : Unity.Jobs.IJob
    {
        // ===== Numeric tolerances =====
        public const float k_Epsilon = 1e-5f;
        public const float k_MinMag = 1e-6f;
        public const float k_SqrEpsilon = 1e-8f;

        // ===== Per-bone override slots, in HumanBodyBones order =====
        public const int Count = 22;
        public const int UpperChestSlot = Count - 1;
        public FixedList128Bytes<BasisBoneHandle> slotHandles;
        public FixedList512Bytes<Vector3> slotPositions;
        public FixedList512Bytes<Quaternion> slotRotations;
        public FixedList512Bytes<Quaternion> slotOffsets;
        public FixedList64Bytes<bool> slotWeights;

        // ===== Bone handles =====
        public BasisBoneHandle handleHips, handleSpine, handleChest, handleUpperChest, handleNeck, handleHead;
        public BasisBoneHandle handleLeftShoulder, handleLeftUpperArm, handleLeftLowerArm, handleLeftHand;
        public BasisBoneHandle handleRightShoulder, handleRightUpperArm, handleRightLowerArm, handleRightHand;
        public BasisBoneHandle handleLeftUpperArmTwist, handleLeftLowerArmTwist;
        public BasisBoneHandle handleRightUpperArmTwist, handleRightLowerArmTwist;
        public BasisBoneHandle handleLeftUpperLeg, handleLeftLowerLeg, handleLeftFoot, handleLeftToe;
        public BasisBoneHandle handleRightUpperLeg, handleRightLowerLeg, handleRightFoot, handleRightToe;
        // Head -> hips, tip first. The CCD chain, with its per-joint rest frames and ranges of motion.
        public NativeArray<BasisBoneHandle> chainHeadToSpine;
        public NativeArray<BasisSpineRestFrame> chainSpineRestFrames;
        public NativeArray<BasisSpineRom> chainSpineRoms;

        // ===== Per-frame targets: spine =====
        public Vector3 targetPositionHead, targetPositionHips;
        public Quaternion targetRotationHead, targetRotationHips, targetRotationChest;
        // targetPositionChest is head-hint biased; the Raw one is not. SolveChestTarget must use Raw --
        // pinning to the biased one dragged the torso ~8 cm up in desktop / no-tracker mode.
        public Vector3 targetPositionChest, targetPositionChestRaw;
        public Vector3 playerUp;

        // ===== Per-frame targets: arms =====
        public Vector3 targetPositionLeftHand, hintPositionLeftHand;
        public Vector3 targetPositionRightHand, hintPositionRightHand;
        public Quaternion targetRotationLeftHand, hintRotationLeftHand;
        public Quaternion targetRotationRightHand, hintRotationRightHand;
        public Quaternion targetRotationLeftShoulder, targetRotationRightShoulder;

        // ===== Per-frame targets: legs and toes =====
        public Vector3 targetPositionLeftLowerLeg, hintPositionLeftLowerLeg;
        public Vector3 targetPositionRightLowerLeg, hintPositionRightLowerLeg;
        public Quaternion targetRotationLeftLowerLeg, hintRotationLeftLowerLeg;
        public Quaternion targetRotationRightLowerLeg, hintRotationRightLowerLeg;
        public Vector3 kneeBendPrefLeft, kneeBendPrefRight, kneeAnteriorRef;
        public Quaternion leftDrivenTargetRot, rightDrivenTargetRot;
        public float leftToeBendDeg, rightToeBendDeg;
        public Vector3 leftToeBendAxis, rightToeBendAxis;

        // ===== Calibration rotation offsets =====
        // offsetRotation* are the inputs the driver re-applies every frame (issue #531); targetOffset* are
        // the copies CaptureCalibrationOffsets takes at the top of the solve.
        public Quaternion offsetRotationHips, offsetRotationHead, offsetRotationChest;
        public Quaternion offsetRotationLeftFoot, offsetRotationRightFoot;
        public Quaternion offsetRotationLeftToe, offsetRotationRightToe;
        public Quaternion offsetRotationLeftShoulder, offsetRotationRightShoulder;
        public Quaternion offsetRotationLeftHand, offsetRotationRightHand;
        public Quaternion targetOffsetHead, targetOffsetChest;
        public Quaternion targetOffsetLeftFoot, targetOffsetRightFoot;
        public Quaternion targetOffsetLeftToe, targetOffsetRightToe;
        public Quaternion targetOffsetLeftShoulder, targetOffsetRightShoulder;
        public Quaternion targetOffsetLeftHand, targetOffsetRightHand;

        // ===== Effector weights and tracker presence =====
        public float enabledLeftHand, enabledRightHand;
        public float enabledLeftLowerLeg, enabledRightLowerLeg;
        public float hintWeightLeftLowerLeg, hintWeightRightLowerLeg;
        public bool hintWeightLeftHand, hintWeightRightHand;
        public bool enabledSpineIK, enabledLeftShoulder, enabledRightShoulder;
        public bool leftToeEnabled, rightToeEnabled;
        public bool hasChestTracker, hasHipsTracker;
        public bool hintIsTrackerLeftLowerLeg, hintIsTrackerRightLowerLeg;
        public bool footIsTrackerLeftLeg, footIsTrackerRightLeg;

        // ===== T-pose bake =====
        // Measured at tposeBakeScale; RescaleTposeScalars carries them across an avatar resize.
        public float tposeBakeScale;
        public Vector3 tposeLengthHeadToHips, tposeLengthNeckToHips, tposeHeadToNeckLocal;
        public Vector3 tposeLeftShoulderLocalDir, tposeRightShoulderLocalDir;
        public Quaternion tposeLeftShoulderRot, tposeRightShoulderRot, tposeChestRot;
        public float tposeShoulderToHandLeft, tposeShoulderToHandRight;
        public float tposeClavicleLenLeft, tposeClavicleLenRight;
        public float tposeShoulderToElbowLeft, tposeShoulderToElbowRight;

        // ===== Tunables: spine =====
        public BasisIKLockMode ikLockMode;
        public int spineMaxIterations;
        public float spineTolerance;
        public float minHeadSpineHeight, maxBendDeg, minFactor, maxFactor, maxChestDeltaDeg;
        public float spineBendPitch, spineBendYaw, spineBendRoll;
        public float upperChestBendPitch, upperChestBendYaw, upperChestBendRoll;
        public float spineMaxForwardDeg, spineMaxBackwardDeg, spineMaxLateralDeg;
        public float spineSquishBoost, spineGazeFollow, neckGazeFollow;
        public float spineCCDRelax, neckMaxConeDeg, spineTwistKeep, spineNeckTwistKeep;
        public float chestSpringHz, chestSpringDamping;
        public float hipHingeStartDeg, hipHingeMaxAddDeg;
        public float moveBodyBackWhenCrouching, crouchDepth, standingHeadHeight;
        public float trunkCounterbalance;
        // Ceiling on the posterior pelvic shift, as a fraction of T-pose spine length: ~25 cm on a 0.55 m
        // spine, the top of the measured range for a real full forward bend. Eased into, never a step.
        public float trunkCounterbalanceMaxSpineFrac;
        // Mid-thoracic bend stiffness for the spine CCD: the swing of the mid joints is scaled down by this
        // (ends unaffected) so a lean curves at the flexible lumbar + cervical and stays firm through the
        // ribcage, distributing the bend instead of kinking at one joint. 0 = uniform (off).
        public float thoracicBendStiffen;
        // Width of the spine CCD's taut band as a fraction of the hips->head chain length (~11 mm on a
        // 1.7 m avatar). Must comfortably exceed the compressions an upright head commands through the
        // neck-pivot lever (quadratic in pitch: ~1.4 mm at 8 deg, ~5.6 mm at 20 deg) — those are the
        // noise-scale demands that sat the solver on its full-extension singularity. See SolveSequentialSpineIK.
        public float spineTautBandFrac;
        // Lateral bend -> a little same-side axial rotation in the pre-bend, so a sustained lean reads as an
        // organic spinal coupling rather than a pure hinge. Small; clamped by the lateral limit downstream.
        public float bendTwistCoupling;
        // Cap on how far the neck may lead a gaze ahead of the spine chain.
        public float neckGazeFollowMaxDeg;
        // Chest-as-secondary-IK-target: pull weight, solver iterations, head-restore sweeps per iteration, the
        // cap on the spine's positional pull, and the distance past which a chest target is treated as a glitch.
        public bool chestIkTarget;
        public float chestIkWeight, chestPosPullMaxDeg, chestPullMaxDist;
        public int chestIkIterations, chestIkHeadRestoreSweeps;
        // Chest share of the arm-swing torso follow; the upper chest takes the remainder.
        public float chestArmSwingFactor, chestArmSwingMaxDeg, chestFollowChestShare;
        // Anatomy toggles, and the cervical lordosis curve that rides on anatCervicalLordosis.
        public bool anatDifferentialStiffness, anatShoulderSlide, anatCervicalLordosis, anatPelvicTwistRouting;
        public bool spineAnatomicalRom;
        public float lordosisPitchGainDeg, lordosisBaseDeg, lordosisNeckShare, lordosisMaxHeadPitchDeg;
        public float lordosisExtremeStartDeg, lordosisExtremeFullDeg;
        public float lordosisExtremeRollForwardMaxDeg, lordosisExtremeRollBackwardMaxDeg;
        public float lordosisExtremeHipsHorizontalMax, lordosisExtremeChestHorizontalMax;
        public float lordosisExtremeHipsDownMax, lordosisExtremeChestDownMax;
        public float lordosisExtremeHipsDownLookUp, lordosisExtremeChestDownLookUp;

        // ===== Tunables: shoulders and arms =====
        public bool shoulderSolveEnabled, shoulderShrugEnabled;
        public float shoulderElevationFactor, shoulderProtractionFactor;
        // Scapulohumeral coupling: girdle share of the humeral swing, and the clamp on the applied girdle rotation.
        public float shoulderCoupleRatio, shoulderMaxDeg;
        // Anatomical shoulder slide: past shoulderSlideStartDeg of chest yaw the girdle counter-rotates by
        // shoulderSlideFraction of the excess, capped at shoulderSlideMaxDeg.
        public float shoulderSlideStartDeg, shoulderSlideMaxDeg, shoulderSlideFraction;
        public float lowerArmTwistFraction, upperArmTwistFraction;
        public float swingSmoothRateDeg;
        public bool protectElbow, collideTrackedElbow, elbowDragEnabled, useNeuralPole;
        public float elbowDragHz;

        // ===== Tunables: legs =====
        public bool legSwivelSmoothing, kneeFootPoleHold, kneeFootPoleConditioning;
        // One Euro parameters for a knee whose pole comes from a tracker: a higher floor than the standing
        // path, and 4x the beta so real shin motion isn't lagged.
        public float trackedKneeSwivelMinCutoffHz, trackedKneeSwivelBeta, trackedKneeSwivelDerivCutoffHz;

        // ===== Tunables: collision =====
        public bool collisionsEnabled;
        public float chestRadius, collisionSkin, handRadius, handSkin;

        // ===== Solver scratch, persistent across frames =====
        public NativeArray<Vector3> chestSpringState;
        public NativeArray<int> chestSpringInit;
        public const int k_SwingLeftElbow = 0, k_SwingRightElbow = 1, k_SwingLeftKnee = 2, k_SwingRightKnee = 3, k_SwingCount = 4;
        public NativeArray<Vector3> swingLastDir, swingLastAxis, swingLastTarget;
        public NativeArray<Vector3> swingHintBend, swingHintAxis, swingHintDrag;
        public NativeArray<Quaternion> swingHintBodyRot;
        public NativeArray<int> swingContinuityInit, swingCollided, swingSmoothState, swingHintInit;
        public NativeArray<Vector3> legSwivelRaw, legSwivelSmooth;
        public NativeArray<int> legSwivelInit;
        public NativeArray<BasisLegDiagnostics> legDiagnostics;

        // ===== The pose being solved =====
        public BasisPoseStream poseStream;

        public void Execute() => ProcessAnimation(poseStream);

        /// <summary>
        /// The frame. Each pass lives in its own file next to the cores it drives -- spine in
        /// Spine/, shoulders and arms in Arms/, legs and toes in Legs/, the bone-write helpers in
        /// BasisEerieMovement.Shared.cs. The ORDER here is the contract: the spine places the torso the
        /// girdle hangs off, the girdle places the shoulders the arms hang off, and the legs run before
        /// the arms because the arm pass collides against the torso the spine has already settled.
        /// </summary>
        public void ProcessAnimation(BasisPoseStream stream)
        {
            CaptureCalibrationOffsets();
            SolveSpinePass(stream);
            SolveShoulderPass(stream);
            SolveLegPass(stream);
            SolveArmPass(stream);
            SolveToePass(stream);
            ApplyTrackerOverrides(stream);
        }

        // Per-frame reads so FBT recalibration (which updates these on the constraint data)
        // reaches the running job; the originals were copied once at job build (issue #531).
        void CaptureCalibrationOffsets()
        {
            targetOffsetHead = offsetRotationHead;
            targetOffsetChest = offsetRotationChest;
            targetOffsetLeftFoot = offsetRotationLeftFoot;
            targetOffsetRightFoot = offsetRotationRightFoot;
            targetOffsetLeftToe = offsetRotationLeftToe;
            targetOffsetRightToe = offsetRotationRightToe;
            targetOffsetLeftShoulder = offsetRotationLeftShoulder;
            targetOffsetRightShoulder = offsetRotationRightShoulder;
            targetOffsetLeftHand = offsetRotationLeftHand;
            targetOffsetRightHand = offsetRotationRightHand;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Slot(int humanBodyBone)
        {
            if (humanBodyBone >= 0 && humanBodyBone <= (int)HumanBodyBones.RightToes)
            {
                return humanBodyBone;
            }
            return humanBodyBone == (int)HumanBodyBones.UpperChest ? UpperChestSlot : -1;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetTargetPosition(int idx, in Vector3 v)
        {
            int s = Slot(idx);
            if (s >= 0 && s < slotPositions.Length)
            {
                slotPositions[s] = v;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetTargetRotation(int idx, in Quaternion q)
        {
            int s = Slot(idx);
            if (s >= 0 && s < slotRotations.Length)
            {
                slotRotations[s] = q;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetOffsetRotation(int idx, in Quaternion q)
        {
            int s = Slot(idx);
            if (s >= 0 && s < slotOffsets.Length)
            {
                slotOffsets[s] = q;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetWeight(int idx, bool State)
        {
            int s = Slot(idx);
            if (s >= 0 && s < slotWeights.Length)
            {
                slotWeights[s] = State;
            }
        }

        public void RescaleTposeScalars(float newScale)
        {
            if (float.IsNaN(newScale) || float.IsInfinity(newScale) || newScale <= 0f || tposeBakeScale <= 0f)
            {
                return;
            }

            float k = newScale / tposeBakeScale;
            if (Mathf.Abs(k - 1f) < 1e-6f)
            {
                return;
            }

            tposeShoulderToHandLeft *= k;
            tposeShoulderToHandRight *= k;
            tposeClavicleLenLeft *= k;
            tposeClavicleLenRight *= k;
            tposeShoulderToElbowLeft *= k;
            tposeShoulderToElbowRight *= k;
            tposeLengthHeadToHips *= k;
            tposeHeadToNeckLocal *= k;
            tposeLengthNeckToHips *= k;

            tposeBakeScale = newScale;
        }
        public void Destroy()
        {
            if (chainHeadToSpine.IsCreated) chainHeadToSpine.Dispose();
            if (chainSpineRestFrames.IsCreated) chainSpineRestFrames.Dispose();
            if (chainSpineRoms.IsCreated) chainSpineRoms.Dispose();

            if (chestSpringState.IsCreated) chestSpringState.Dispose();
            if (chestSpringInit.IsCreated) chestSpringInit.Dispose();

            if (swingLastDir.IsCreated) swingLastDir.Dispose();
            if (swingLastAxis.IsCreated) swingLastAxis.Dispose();
            if (swingLastTarget.IsCreated) swingLastTarget.Dispose();
            if (swingContinuityInit.IsCreated) swingContinuityInit.Dispose();
            if (swingCollided.IsCreated) swingCollided.Dispose();
            if (swingSmoothState.IsCreated) swingSmoothState.Dispose();
            if (swingHintBend.IsCreated) swingHintBend.Dispose();
            if (swingHintAxis.IsCreated) swingHintAxis.Dispose();
            if (swingHintDrag.IsCreated) swingHintDrag.Dispose();
            if (swingHintBodyRot.IsCreated) swingHintBodyRot.Dispose();
            if (swingHintInit.IsCreated) swingHintInit.Dispose();
            if (legDiagnostics.IsCreated) legDiagnostics.Dispose();
            if (legSwivelRaw.IsCreated) legSwivelRaw.Dispose();
            if (legSwivelSmooth.IsCreated) legSwivelSmooth.Dispose();
            if (legSwivelInit.IsCreated) legSwivelInit.Dispose();
        }
    }
}
