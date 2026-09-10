using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;
namespace Basis.IK
{
    public struct BasisButterflyKneeInput
    {
        public Vector3 HipPosition, FootPosition, FootInstepDir, OutwardDir, DefaultBendDir, PlayerUp, TorsoFacingDir;
        public float UpperLength, LowerLength, MaxOpenDeg, Strength, SupineFloor;
    }
    public struct BasisButterflyKneeResult
    {
        public Vector3 KneeHint;
        public float HintWeight, OpenAngleDeg, Supine01, FootTilt01, PullIn01;
    }
    public struct BasisCervicalInput
    {
        public float BaseDeg, NeckShare, MaxHeadPitchDeg, ExtremeStartDeg, ExtremeFullDeg, ExtremeRollForwardMaxDeg;
        public float ExtremeRollBackwardMaxDeg, ExtremeHipsHorizontalMax, ExtremeChestHorizontalMax;
        public float ExtremeHipsHorizontalLookUp, ExtremeChestHorizontalLookUp, ExtremeHipsDownMax, ExtremeChestDownMax;
        public float ExtremeHipsDownLookUp, ExtremeChestDownLookUp, PitchGainDeg;
        public Vector3 ReferenceUp;
        public Quaternion HeadTargetRot;
        public bool HasUpperChest;
    }
    public struct BasisCervicalResult
    {
        public bool EarlyOut;
        public Quaternion HeadRotClamped;
        public float BhDeg, NeckDeg;
        public bool HasExtreme;
        public float HipsForwardAmount, HipsDownAmount, ChestForwardAmount, ChestDownAmount, HeadPitchInputDeg;
        public float HeadPitchClampedDeg, LordosisDeg, UpperChestLordosisDeg, ExtremeFrac, ExtremeRollDeg, SignedPitch;
        public float LookUpFrac, LookDownFrac;
    }
    public struct BasisNodPivotSettings
    {
        public float PriorWeight, MinPitchRangeDeg, MinFitQuality, MaxPivotSpreadMeters, MaxVerticalRangeMeters;
        public float3 MaxArm;
        public float Scale;
    }
    public struct BasisNodPivotResult
    {
        public float3 Arm;
        public bool Accepted;
        public float FitQuality, PitchRangeDeg, PivotSpreadMeters, VerticalRangeMeters;
    }
    public struct BasisSwivelFilterState
    {
        public float Raw, Vel, Smooth;
    }
    public struct BasisSwivelFrame
    {
        public Vector3 Right, Up, Forward;
        public bool Valid;
    }
    public struct BasisSwivelSmootherInput
    {
        public Vector3 Root, Mid, Tip;
        public Quaternion BodyRotation;
        public Vector3 ReferenceLocal, FallbackLocal, TransportHomeLocal;
        public float Dt, MinCutoffHz, Beta, DerivCutoffHz;
        public BasisSwivelFilterState State;
        public bool Seeded, ConditionOnPole;
        public float SingularMinCutoffHz;
        public bool GuardAnteriorHalfSpace;
        public float AnteriorSoftDeg, AnteriorHardDeg;
        public bool HoldWhenSingular;
        public float HoldCondLo, HoldCondHi;
    }
    public struct BasisSwivelSmootherResult
    {
        public bool Valid, WriteState, Seeded;
        public BasisSwivelFilterState State;
        public Vector3 DesiredMid;
        public float RawSwivelDeg, SmoothSwivelDeg, Conditioning;
        public bool AnteriorGuardApplied;
        public float HoldGate;
    }
}
namespace Basis.Scripts.Drivers
{
    public struct BasisEuroVec3State
    {
        public bool xHasPrev, dxHasPrev;
        public float3 hatX, hatDx;
    }
    public struct BasisEuroQuatState
    {
        public bool hasPrev;
        public quaternion prev;
        public BasisEuroVec3State logVecState;
    }
}
