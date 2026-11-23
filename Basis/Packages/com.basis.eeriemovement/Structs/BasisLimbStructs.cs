using System.Runtime.CompilerServices;
using Unity.Collections;
using UnityEngine;
using Unity.Mathematics;
public struct BasisFootNativeState
{
    public const float SettledPlantedTime = 10f;
    public int sideSign;
    public float thighLen, shinLen, legLength;
    public bool placed;
    public int phase;
    public float3 plantedPos, plantedBodyFwd, stepStartPos, stepTargetPos;
    public quaternion stepStartRot;
    public float stepTimer, stepDur, stepProgress, plantedTime;
    public float3 idealPos, filteredNormal, currentPos;
    public quaternion currentRot;
    public float toeBendDeg;
    public float3 toeBendAxis;
}
public struct BasisFootSimState
{
    public float3 prevHipsPos, smoothedVelocity;
    public float stillTime;
    public bool hasPrevHipsPos, settlePending;
}
public struct BasisFootSimInput
{
    public float dt;
    public float3 hipsPos, facingFwd, avatarForward, playerUp;
    public bool leftHit, rightHit;
    public float3 leftPoint, leftNormal, leftOrigin, rightPoint, rightNormal, rightOrigin;
}
public struct BasisFootSimParams
{
    public float stanceWidth, minFootGap, stepDistance, stepDuration, minStepDuration, stepHeight, stepAngle, overstretch;
    public float stepOverlap, settleDelay, settleDistance, settleAngle, teleportDistance, maxFootTiltDegrees, rayLength;
    public float legLength, leftLegLen, rightLegLen, hipToFoot, hipJointDrop, footHeightOffset, hipBobFraction, fastSpeedRef;
    public quaternion footAlignLeft, footAlignRight;
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
        public Vector3 Chest, Shoulder, RestElbow, RestHand, TargetPosition, HeadPosition, HintPosition, TorsoUp, TorsoForward, TorsoOut, TorsoA, TorsoB;
        public Quaternion TargetRotation, RestHandRotation, RestMidRotation, HintRotation;
        public bool HasHead, HasTrackerHint, HasHintRotation, HintRotationOnly, IsLeft, TorsoCapsule;
        public float TorsoRadius, Dt, SmoothTime, MaxRateDeg;
        public float ReachSoftness;
        public static BasisArmSolveInput Defaults(bool isLeft) => new BasisArmSolveInput { TorsoUp = Vector3.up, TorsoForward = Vector3.forward, TorsoOut = isLeft ? Vector3.left : Vector3.right, IsLeft = isLeft, Dt = 1f / 90f, SmoothTime = 0.08f, MaxRateDeg = 720f, TargetRotation = Quaternion.identity, RestHandRotation = Quaternion.identity, RestMidRotation = Quaternion.identity, HintRotation = Quaternion.identity };
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
        public float ArmLength, ElevationFactor, ProtractionFactor;
        // Tracked elbow: drives the lift and the shrug. UpperArmLength is the rest upper-arm joint -> elbow distance.
        public bool HasElbow;
        public Vector3 ElbowPos;
        public float UpperArmLength;
        // World direction the back of the hand points (zero if unknown). Used for the upside-down hand shrug.
        public Vector3 HandBack;
    }
    public struct BasisShoulderSolveResult
    {
        public bool Apply;
        public Quaternion Delta;
        public float ElevationDeg, ProtractionDeg, HumeralElevationDeg, ReachRatio, ShrugDeg;
    }
    public struct BasisLegSolveInput
    {
        public Vector3 Root, Mid, Tip;
        public Quaternion RootRotation, MidRotation;
        public Vector3 TargetPosition;
        public Quaternion TargetRotation;
        public Vector3 HintPosition;
        public float HintWeight;
        public float ReachSoftness;
        public Quaternion TargetOffset;
        public Vector3 BendNormal, AnteriorNormal;
        public Quaternion HintRotation;
        public bool HintIsTracker, HasHintRotation;
        public Vector3 FootForward, Up, HipsForward, HipsRight;
        public bool HasBodyFrame;
    }
    public struct BasisLegSolveResult
    {
        public Vector3 HintPosition;
        public Vector3 PoleDir;
        public Quaternion MidDelta, RootDelta, HintDelta, MidPostRoll, TipRotation;
        public bool HintApplied;
        public float ShinRollDeg;
        public Vector3 KneeSolved, FootSolved;
        public Quaternion RootRotationSolved, MidRotationSolved;
        public Quaternion ThighUntwist;
        public float UpperLength, LowerLength, TargetDistance, ReachRatio, KneeAngleDeg;
        public byte AxisSource;
        public float FootError;
    }
    public struct BasisLegDiagnostics
    {
        public float ReachRatio, KneeAngleDeg, AxisSource, HintApplied;
        public float RawSwivelDeg, SmoothSwivelDeg, Conditioning, HoldGate, AnteriorGuardApplied, Seeded, ShinRollDeg;
        public float HipFlexionDeg, HipAbductionDeg, FemurTwistDeg;
        public static string Header => "leg,reach,kneeDeg,axisSrc,hintApplied,rawSwivel,smoothSwivel,cond,holdGate,antGuard,seeded,shinRoll,hipFlex,hipAbd,femurTwist";
        public string ToRow(string leg) => $"{leg},{ReachRatio:F4},{KneeAngleDeg:F2},{AxisSource:F0},{HintApplied:F0}," + $"{RawSwivelDeg:F2},{SmoothSwivelDeg:F2}," + $"{Conditioning:F4},{HoldGate:F3},{AnteriorGuardApplied:F0},{Seeded:F0},{ShinRollDeg:F2}," + $"{HipFlexionDeg:F2},{HipAbductionDeg:F2},{FemurTwistDeg:F2}";
    }
}
