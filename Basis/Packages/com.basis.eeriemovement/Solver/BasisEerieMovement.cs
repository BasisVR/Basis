using System.Runtime.CompilerServices;
using Basis.Scripts.Common;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
namespace Basis.IK
{
    [Unity.Burst.BurstCompile]
    public partial struct BasisEerieMovement : Unity.Jobs.IJob
    {
        public const float epsilon = 1e-5f, minMag = 1e-6f, sqrEpsilon = 1e-8f;
        public const int Count = 22, UpperChestSlot = Count - 1;
        public FixedList512Bytes<Vector3> slotPositions;
        public FixedList512Bytes<Quaternion> slotRotations, slotOffsets;
        public FixedList64Bytes<bool> slotWeights;
        public BasisBoneHandle handleHips, handleSpine, handleChest, handleUpperChest, handleNeck, handleHead;
        public BasisBoneHandle handleLeftShoulder, handleLeftUpperArm, handleLeftLowerArm, handleLeftHand;
        public BasisBoneHandle handleRightShoulder, handleRightUpperArm, handleRightLowerArm, handleRightHand;
        public BasisBoneHandle handleLeftUpperArmTwist, handleLeftLowerArmTwist, handleRightUpperArmTwist;
        public BasisBoneHandle handleRightLowerArmTwist, handleLeftUpperLeg, handleLeftLowerLeg, handleLeftFoot;
        public BasisBoneHandle handleLeftToe, handleRightUpperLeg, handleRightLowerLeg, handleRightFoot, handleRightToe;
        public NativeArray<BasisBoneHandle> chainHeadToSpine;
        public NativeArray<BasisSpineRestFrame> chainSpineRestFrames;
        public int chainChestIdx;
        public Vector3 targetPositionHead, targetPositionHips;
        public Quaternion targetRotationHead, targetRotationHips, targetRotationChest;
        public Vector3 targetPositionChest, targetPositionChestRaw, playerUp, targetPositionLeftHand;
        public Vector3 hintPositionLeftHand, targetPositionRightHand, hintPositionRightHand;
        public Quaternion targetRotationLeftHand, hintRotationLeftHand, targetRotationRightHand, hintRotationRightHand;
        public Quaternion targetRotationLeftShoulder, targetRotationRightShoulder;
        public Vector3 targetPositionLeftLowerLeg, hintPositionLeftLowerLeg, targetPositionRightLowerLeg;
        public Vector3 hintPositionRightLowerLeg;
        public Quaternion targetRotationLeftLowerLeg, hintRotationLeftLowerLeg, targetRotationRightLowerLeg;
        public Quaternion hintRotationRightLowerLeg;
        public Vector3 kneeBendPrefLeft, kneeBendPrefRight, kneeAnteriorRef;
        public Quaternion leftDrivenTargetRot, rightDrivenTargetRot;
        public float leftToeBendDeg, rightToeBendDeg;
        public Vector3 leftToeBendAxis, rightToeBendAxis;
        public Quaternion offsetRotationHips, offsetRotationHead, offsetRotationChest, offsetRotationLeftFoot;
        public Quaternion offsetRotationRightFoot, offsetRotationLeftToe, offsetRotationRightToe;
        public Quaternion offsetRotationLeftShoulder, offsetRotationRightShoulder, offsetRotationLeftHand;
        public Quaternion offsetRotationRightHand;
        public BasisEeriePlan plan;
        public float tposeBakeScale;
        public float tposeArmFitScale, tposeTorsoFitScale;
        public Vector3 tposeLengthNeckToHips, tposeHeadToNeckLocal, tposeLeftShoulderLocalDir;
        public Vector3 tposeRightShoulderLocalDir;
        public Quaternion tposeLeftShoulderRot, tposeRightShoulderRot, tposeChestRot;
        public float tposeShoulderToHandLeft, tposeShoulderToHandRight, tposeClavicleLenLeft, tposeClavicleLenRight;
        public float tposeShoulderToElbowLeft, tposeShoulderToElbowRight;
        public Quaternion tposeLeftLowerArmTwistBind, tposeLeftLowerArmChildBind, tposeRightLowerArmTwistBind;
        public Quaternion tposeRightLowerArmChildBind, tposeLeftUpperArmTwistBind, tposeLeftUpperArmChildBind;
        public Quaternion tposeRightUpperArmTwistBind, tposeRightUpperArmChildBind;
        public BasisIKLockMode ikLockMode;
        public int spineMaxIterations;
        public float spineTolerance, minHeadSpineHeight, maxBendDeg, minFactor, maxFactor, maxChestDeltaDeg;
        public float spineBendPitch, spineBendYaw, spineBendRoll, upperChestBendPitch, upperChestBendYaw;
        public float upperChestBendRoll, spineMaxForwardDeg, spineMaxBackwardDeg, spineMaxLateralDeg, spineSquishBoost;
        public float spineGazeFollow, neckGazeFollow, neckExtensionDamp, neckFlexionDamp, spineCCDRelax, neckMaxConeDeg;
        public float spineTwistKeep, spineNeckTwistKeep, chestSpringHz, chestSpringDamping, hipHingeStartDeg;
        public float hipHingeMaxAddDeg, moveBodyBackWhenCrouching, crouchDepth, standingHeadHeight, trunkCounterbalance;
        public float trunkCounterbalanceMaxSpineFrac, thoracicBendStiffen, spineTautBandFrac, bendTwistCoupling;
        public float neckGazeFollowMaxDeg, chestBendPitch, chestBendYaw, chestBendRoll, neckYawShare, spineStretchMax;
        public float restChordHeadHips, restChordHeadLumbar, restChordHeadUpper, restReachHeadLumbar;
        public Quaternion chestTrackedRot, chestRestFromHead;
        public bool chestIkTarget;
        public float chestIkWeight, chestPosPullMaxDeg, chestPullMaxDist;
        public int chestIkIterations, chestIkHeadRestoreSweeps;
        public float chestArmSwingFactor, chestArmSwingMaxDeg, chestFollowChestShare;
        public bool anatDifferentialStiffness, anatShoulderSlide, anatCervicalLordosis, anatPelvicTwistRouting;
        public bool spineAnatomicalRom;
        public float lordosisPitchGainDeg, lordosisBaseDeg, lordosisNeckShare, lordosisMaxHeadPitchDeg;
        public float lordosisExtremeStartDeg, lordosisExtremeFullDeg, lordosisExtremeRollForwardMaxDeg;
        public float lordosisExtremeRollBackwardMaxDeg, lordosisExtremeHipsHorizontalMax;
        public float lordosisExtremeChestHorizontalMax, lordosisExtremeHipsHorizontalLookUp;
        public float lordosisExtremeChestHorizontalLookUp, lordosisExtremeHipsDownMax, lordosisExtremeChestDownMax;
        public float lordosisExtremeHipsDownLookUp, lordosisExtremeChestDownLookUp;
        public bool shoulderSolveEnabled, shoulderShrugEnabled;
        public float shoulderElevationFactor, shoulderProtractionFactor, shoulderCoupleRatio, shoulderMaxDeg;
        public float shoulderSlideStartDeg, shoulderSlideMaxDeg, shoulderSlideFraction, lowerArmTwistFraction;
        public float upperArmTwistFraction, swingSmoothRateDeg;
        public bool protectElbow, collideTrackedElbow, elbowDragEnabled;
        public float elbowDragHz;
        public bool legSwivelSmoothing, kneeFootPoleHold, kneeFootPoleConditioning;
        public float trackedKneeSwivelMinCutoffHz, trackedKneeSwivelBeta, trackedKneeSwivelDerivCutoffHz;
        public bool collisionsEnabled;
        public float chestRadius, collisionSkin, handRadius, handSkin;
        public NativeArray<BasisChestSpringState> chestSpring;
        public const int swingLeftElbow = 0, swingRightElbow = 1, swingCount = 2;
        public NativeArray<BasisSwingContinuityState> swingContinuity;
        public NativeArray<BasisArmSlotState> armState;
        public NativeArray<BasisLegSlotState> legState;
        public NativeArray<BasisLegDiagnostics> legDiagnostics;
        public BasisPoseStream poseStream;
        public BasisIKGizmoRecorder gizmos;
        static unsafe ref T Ref<T>(NativeArray<T> array, int index) where T : unmanaged
        {
            return ref UnsafeUtility.ArrayElementAsRef<T>(array.GetUnsafePtr(), index);
        }
        internal BasisBoneHandle SlotHandle(int slot)
        {
            switch (slot)
            {
                case 0: return handleHips;
                case 1: return handleLeftUpperLeg;
                case 2: return handleRightUpperLeg;
                case 3: return handleLeftLowerLeg;
                case 4: return handleRightLowerLeg;
                case 5: return handleLeftFoot;
                case 6: return handleRightFoot;
                case 7: return handleSpine;
                case 8: return handleChest;
                case 9: return handleNeck;
                case 10: return handleHead;
                case 11: return handleLeftShoulder;
                case 12: return handleRightShoulder;
                case 13: return handleLeftUpperArm;
                case 14: return handleRightUpperArm;
                case 15: return handleLeftLowerArm;
                case 16: return handleRightLowerArm;
                case 17: return handleLeftHand;
                case 18: return handleRightHand;
                case 19: return handleLeftToe;
                case 20: return handleRightToe;
                case UpperChestSlot: return handleUpperChest;
                default: return BasisBoneHandle.Unbound;
            }
        }
        public void Execute() => ProcessAnimation();
        public void ProcessAnimation()
        {
            poseStream.InvalidateWorldCache();
            RecordTargetGizmos();
            BasisEerieMarkers.Spine.Begin();
            SolveSpinePass();
            BasisEerieMarkers.Spine.End();
            RecordSpineGizmos();
            //BasisEerieMarkers.Shoulders.Begin();
            //SolveShoulderPass();
            //BasisEerieMarkers.Shoulders.End();
            //RecordShoulderGizmos();
            BasisEerieMarkers.Legs.Begin();
            SolveLegPass();
            BasisEerieMarkers.Legs.End();
            RecordLegGizmos();
            BasisEerieMarkers.Arms.Begin();
            SolveArmPass();
            BasisEerieMarkers.Arms.End();
            RecordArmGizmos();
            BasisEerieMarkers.Toes.Begin();
            SolveToePass();
            BasisEerieMarkers.Toes.End();
            RecordToeGizmos();
            BasisEerieMarkers.TrackerOverrides.Begin();
            ApplyTrackerOverrides();
            BasisEerieMarkers.TrackerOverrides.End();
            RecordOverrideGizmos();
            RecordFrameGizmos();
            RecordLimitGizmos();
            RecordReachGizmos();
            RecordNumberGizmos();
            RecordSkeletonGizmos();
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
            tposeHeadToNeckLocal *= k;
            tposeLengthNeckToHips *= k;

            tposeBakeScale = newScale;
        }
        public void RescaleTposeFit(float armScale, float torsoScale)
        {
            if (!(armScale > 0f) || !(torsoScale > 0f) || float.IsInfinity(armScale) || float.IsInfinity(torsoScale))
            {
                return;
            }
            float ka = armScale / (tposeArmFitScale > 0f ? tposeArmFitScale : 1f), kt = torsoScale / (tposeTorsoFitScale > 0f ? tposeTorsoFitScale : 1f);
            if (Mathf.Abs(ka - 1f) >= 1e-6f)
            {
                tposeShoulderToHandLeft = tposeClavicleLenLeft + (tposeShoulderToHandLeft - tposeClavicleLenLeft) * ka;
                tposeShoulderToHandRight = tposeClavicleLenRight + (tposeShoulderToHandRight - tposeClavicleLenRight) * ka;
                tposeShoulderToElbowLeft = tposeClavicleLenLeft + (tposeShoulderToElbowLeft - tposeClavicleLenLeft) * ka;
                tposeShoulderToElbowRight = tposeClavicleLenRight + (tposeShoulderToElbowRight - tposeClavicleLenRight) * ka;
            }
            if (Mathf.Abs(kt - 1f) >= 1e-6f)
            {
                tposeHeadToNeckLocal *= kt;
                tposeLengthNeckToHips *= kt;
                minHeadSpineHeight *= kt;
            }
            tposeArmFitScale = armScale;
            tposeTorsoFitScale = torsoScale;
        }
        public void Destroy()
        {
            if (chainHeadToSpine.IsCreated) chainHeadToSpine.Dispose();
            if (chainSpineRestFrames.IsCreated) chainSpineRestFrames.Dispose();
            if (chestSpring.IsCreated) chestSpring.Dispose();
            if (swingContinuity.IsCreated) swingContinuity.Dispose();
            if (armState.IsCreated) armState.Dispose();
            if (legState.IsCreated) legState.Dispose();
            if (legDiagnostics.IsCreated) legDiagnostics.Dispose();

            gizmos.Dispose();
        }
    }
}
