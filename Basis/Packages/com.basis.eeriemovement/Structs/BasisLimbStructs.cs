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
    public bool wasAirborne, hasPrevHeadPos;
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
    public struct BasisArmLimits
    {
        public float PronationMaxDeg, SupinationMaxDeg, HumeralInternalMaxDeg, HumeralExternalMaxDeg, WristFlexionMaxDeg, WristExtensionMaxDeg, WristRadialMaxDeg, WristUlnarMaxDeg;
        public static BasisArmLimits Default => new BasisArmLimits { PronationMaxDeg = 90f, SupinationMaxDeg = 90f, HumeralInternalMaxDeg = 70f, HumeralExternalMaxDeg = 90f, WristFlexionMaxDeg = 80f, WristExtensionMaxDeg = 70f, WristRadialMaxDeg = 20f, WristUlnarMaxDeg = 30f };
    }
    public struct BasisArmSolveInput
    {
        public Vector3 Shoulder, RestElbow, RestHand, TargetPosition, HeadPosition, HintPosition, TorsoUp, TorsoForward, TorsoOut, TorsoA, TorsoB;
        public Quaternion TargetRotation, RestHandRotation, HintRotation;
        public bool HasHead, HasHint, HasHintRotation, IsLeft, TorsoCapsule, JointLimits;
        public float TorsoRadius, Dt, ReachSoftness, SmoothTime, MaxRateDeg, SwitchDwell, PriorWeight, PreviousWeight;
        public BasisArmLimits Limits;
        public static BasisArmSolveInput Defaults(bool isLeft) => new BasisArmSolveInput { TorsoUp = Vector3.up, TorsoForward = Vector3.forward, TorsoOut = isLeft ? Vector3.left : Vector3.right, IsLeft = isLeft, JointLimits = true, Dt = 1f / 90f, ReachSoftness = 0.02f, SmoothTime = 0.08f, MaxRateDeg = 720f, SwitchDwell = 0.2f, PriorWeight = 0.5f, PreviousWeight = 0.25f, Limits = BasisArmLimits.Default, TargetRotation = Quaternion.identity, RestHandRotation = Quaternion.identity, HintRotation = Quaternion.identity };
    }
    public struct BasisArmSolveResult
    {
        public bool Valid, Switched;
        public Vector3 Elbow, Hand, Axis, Hinge, ElbowDir;
        public float SwivelDeg, PriorDeg, RawDeg, ReachRatio, ElbowDeg, HumeralDeg, PronationDeg, WristFlexDeg, WristDevDeg, TargetDistance, EffectiveDistance, UpperLength, LowerLength, Cost;
    }
    public struct BasisShoulderSolveInput
    {
        public Vector3 ShoulderPos, UpperArmPos, HandTargetPos, TorsoUp, TorsoForward, TorsoOut;
        public float ArmLength, ElevationFactor, ProtractionFactor, MaxDeg;
        public bool ShrugEnabled;
    }
    public struct BasisShoulderSolveResult
    {
        public bool Apply;
        public Quaternion Delta;
        public float ElevationDeg, ProtractionDeg, HumeralElevationDeg, ReachRatio;
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
