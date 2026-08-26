using System.Runtime.CompilerServices;
using Unity.Collections;
using UnityEngine;
using Unity.Mathematics;
public struct BasisFootNativeState
{
    public int sideSign;
    public float thighLen, shinLen, legLength;
    public int phase;
    public float3 plantedPos;
    public quaternion plantedRot;
    public float3 plantedBodyFwd, stepStartPos, stepTargetPos;
    public quaternion stepStartRot;
    public float stepTimer, stepDur, stepArcScale, plantedTime, stepUrgency;
    public quaternion landRot;
    public float3 idealPos, filteredNormal, currentPos;
    public quaternion currentRot;
    public float3 kneeHint;
    public float toeBendDeg;
    public float3 toeBendAxis;
    public bool wantsStep;
    public float3 predictedTargetXZ;
}
public struct BasisFootSimState
{
    public float3 prevHeadPos;
    public float prevHeadYaw;
    public float3 smoothedVelocity, smoothedBodyFwd, smoothedBodyRight, prevBodyFwd;
    public float smoothedYawRateDeg, smoothedAccelMag;
    public float3 prevRootFwd;
    public bool wasAirborne;
}
public struct BasisFootSimInput
{
    public float dt;
    public float3 headPos, hipsPos;
    public quaternion hipsRot, chestRot, headRot;
    public float3 avatarForward, avatarRight;
    public bool hasChest, groundHit;
    public float3 groundPoint;
    public bool leftGroundValid, rightGroundValid;
    public float leftGroundUp, rightGroundUp, splayWhenCrouched;
    public float3 playerUp;
}
public struct BasisFootSimParams
{
    public float predictionFactor, velocityBiasFactor, leadOffsetFactor, maxVelocityOffsetFraction;
    public float maxPredictionFraction, plantedLerpSpeed, rotationLerpSpeed, velocitySmoothAccel, velocitySmoothDecel;
    public float bodyFwdRateMoving, bodyFwdRateStationary, kneeHintLerpSpeed, maxFootTiltDegrees, maxFootYawDegrees;
    public float stepArcLiftExp, stepArcDropExp, stepHeightMinFraction, stepHeightStrideRefFraction, idleSpeedThreshold;
    public float idleBoostFraction, maxPlantedYawDegrees, idealSideEnforceFraction, stepTargetSideFraction;
    public float footSideEnforceFraction, maxVerticalDriftFraction, kneeForwardPushFraction, kneeMinSideFraction;
    public float bodyFwdHipsWeight, bodyFwdChestWeight, bodyFwdHeadWeight, hipBobFraction;
    public quaternion footAlignLeft, footAlignRight;
    public float stanceWidth, hipToFoot, leftLegLen, rightLegLen, leftThighLen, leftShinLen, rightThighLen;
    public float rightShinLen, footLength, ankleHeight, stepTriggerDist, strideScale, stepHeightCalc, stepDurSlow;
    public float stepDurFast, raySphereRadius, footHeightOffset, fastSpeedRef, rayCastRange;
}
public struct BasisFootSimOutput
{
    public float hipBob;
    public float3 hipSway;
    public bool airborne;
    public quaternion pelvisDelta;
}
namespace Basis.IK
{
    public struct BasisArmSolveInput
    {
        public Vector3 Shoulder, Elbow, Hand;
        public Quaternion RootRotation, MidRotation;
        public Vector3 TargetPosition;
        public Quaternion TargetRotation;
        public Vector3 HintPosition;
        public bool HintWeight;
        public Quaternion TargetOffset;
        public Vector3 PlayerUp;
        public float HintMaxStepDeg;
        public bool HintIsTracker, HasHintRotation;
        public Quaternion TipRotation, HintRotation;
        public bool HasPrevPole;
        public Vector3 PrevPoleDir;
        public Quaternion PrevHintRotation;
        public int PrevGuardSide;
        public Vector3 ElbowLateralOut, TorsoUp;
        public float ForearmFollowWeight;
    }
    public struct BasisArmSolveResult
    {
        public Quaternion RootRotation, MidRotation, MidDelta, RootDelta, HintDelta, MidPostRoll, TipRotation;
        public bool HintApplied;
        public Vector3 ElbowSolved, HandSolved;
        public Quaternion RootRotationSolved, MidRotationSolved;
        public float UpperLength, LowerLength, TargetDistance, ReachRatio, ElbowAngleDeg, HintFade, HintProjMag;
        public float ArmProjMag;
        public byte AxisSource;
        public float HandError, WristTwistDeg, WristReliefDeg, ForearmRollDeg, WristResidualDeg;
        public bool PoleAnchorValid;
        public Vector3 PoleDirUsed;
        public Quaternion PoleRotUsed;
        public float PoleConditioning;
        public int GuardSideUsed;
    }
    public struct BasisShoulderSolveInput
    {
        public Vector3 ShoulderPos, HandTargetPos, ElbowPos;
        public bool HasElbow, HasShoulderTracker;
        public Quaternion ChestRot, TposeChestRot, TposeShoulderRot;
        public Vector3 TposeArmDirWorld;
        public float TposeArmLength, TposeClavicleLength, TposeElbowLength;
        public bool ShrugEnabled;
        public float ElevationFactor, ProtractionFactor, CoupleRatio, MaxShoulderDeg;
        public Quaternion TrackerFinal;
        public bool IsLeft;
    }
    public struct BasisShoulderSolveResult
    {
        public bool Apply;
        public Quaternion ShoulderRotation;
        public float ReachRatio, Elevation, Protraction, CrossBodyContrib, ComputedWeight, SwingAngleDeg;
        public float AppliedAngleDeg, TwistLeakDeg;
        public bool DriverIsElbow;
        public float ShrugDeg;
    }
    public struct BasisElbowProtectInput
    {
        public Vector3 Shoulder, Elbow, Hand, HipsPos, SpinePos, ChestPos, NeckPos;
        public bool HasHips, HasSpine;
        public float ChestRadiusBase, CollisionSkin, HandRadius, HandSkin;
        public Vector3 PlayerUp, BodyRight;
    }
    public struct BasisElbowProtectResult
    {
        public bool Engaged;
        public int CollisionState;
        public Vector3 DesiredElbow;
        public float WorstPenetration, SideDot, BlendUsed, SwingAngleDeg, ElbowRadius;
        public Vector3 ElbowCenter;
        public float ResidualClearance;
    }
    public struct BasisLegSolveInput
    {
        public Vector3 Root, Mid, Tip;
        public Quaternion RootRotation, MidRotation;
        public Vector3 TargetPosition;
        public Quaternion TargetRotation;
        public Vector3 HintPosition;
        public float HintWeight, HintDistrust;
        public Quaternion TargetOffset;
        public Vector3 BendNormal, AnteriorNormal;
        public Quaternion HintRotation;
        public bool HintIsTracker, HasHintRotation;
    }
    public struct BasisLegSolveResult
    {
        public Quaternion MidDelta, RootDelta, HintDelta, MidPostRoll, TipRotation;
        public bool HintApplied;
        public float ShinRollDeg;
        public Vector3 KneeSolved, FootSolved;
        public Quaternion RootRotationSolved, MidRotationSolved;
        public float UpperLength, LowerLength, TargetDistance, ReachRatio, KneeAngleDeg;
        public byte AxisSource;
        public float FootError;
    }
    public struct BasisKneeForwardInput
    {
        public Vector3 HipPosition, FootPosition, FootForwardDir, BodyForwardDir, PlayerUp;
        public float UpperLength, Coupling, Strength;
    }
    public struct BasisKneeForwardResult
    {
        public Vector3 KneeHint, BendDir;
        public float HintWeight, Upright01, FollowDeg;
    }
    public struct BasisLegDiagnostics
    {
        public float ReachRatio, KneeAngleDeg, AxisSource, HintApplied, ModelHintUsed, ModelConfidence, HintDistrust;
        public float RawSwivelDeg, SmoothSwivelDeg, Conditioning, HoldGate, AnteriorGuardApplied, Seeded, ShinRollDeg;
        public float HipFlexionDeg, HipAbductionDeg, FemurTwistDeg;
        public static string Header => "leg,reach,kneeDeg,axisSrc,hintApplied,modelUsed,modelConf,distrust,rawSwivel,smoothSwivel,cond,holdGate,antGuard,seeded,shinRoll,hipFlex,hipAbd,femurTwist";
        public string ToRow(string leg) => $"{leg},{ReachRatio:F4},{KneeAngleDeg:F2},{AxisSource:F0},{HintApplied:F0},{ModelHintUsed:F0}," + $"{ModelConfidence:F3},{HintDistrust:F3},{RawSwivelDeg:F2},{SmoothSwivelDeg:F2}," + $"{Conditioning:F4},{HoldGate:F3},{AnteriorGuardApplied:F0},{Seeded:F0},{ShinRollDeg:F2}," + $"{HipFlexionDeg:F2},{HipAbductionDeg:F2},{FemurTwistDeg:F2}";
    }
}
