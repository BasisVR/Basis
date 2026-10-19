using Unity.Collections;
using UnityEngine;
namespace Basis.IK
{
    public struct BasisBodyFitMeasurements
    {
        public float PlayerEyeHeight, PlayerArmSpan, AvatarEyeHeight, AvatarArmSpan, AvatarShoulderWidth, UniformScale;
    }
    public struct BasisBodyFitResult
    {
        public float ArmScale, LegScale, TorsoScale;
        public BasisBodyFitStatus ArmStatus, BodyStatus;
        public bool HasArmFit => ArmStatus == BasisBodyFitStatus.Fitted;
        public bool HasBodyFit => BodyStatus == BasisBodyFitStatus.Fitted;
        public static BasisBodyFitResult Identity => new BasisBodyFitResult
        {
            ArmScale = 1f,
            LegScale = 1f,
            TorsoScale = 1f,
            ArmStatus = BasisBodyFitStatus.Disabled,
            BodyStatus = BasisBodyFitStatus.Disabled,
        };
        public bool IsIdentity => Mathf.Approximately(ArmScale, 1f) && Mathf.Approximately(LegScale, 1f) && Mathf.Approximately(TorsoScale, 1f);
    }
    public struct BasisBodyEvidenceTrack
    {
        public FixedList64Bytes<float> Top;
        public int SampleCount;
        public float Previous;
        public bool HasPrevious;
        public int LowStreak;
    }
    public struct BasisBodyEvidenceSample
    {
        public float HeadY, HandSpan, InjectedVerticalOffset, DeltaSeconds;
        public bool HeadValid, HandsValid;
    }
    public struct BasisBodyEvidenceState
    {
        public BasisBodyEvidenceTrack Eye, ArmSpan;
    }
    public struct BasisScaleFitSample
    {
        public float Player, Avatar, Slack, Weight;
        public static BasisScaleFitSample None => default;
    }
    public struct BasisScaleFitInput
    {
        public BasisScaleFitSample Eye, ArmSpan, HipHeight;
        public float MaxEyeDeviation;
    }
    public struct BasisScaleFitResult
    {
        public float Scale;
        public BasisScaleFitStatus Status;
        public int UsedCount;
        public float EyeResidual, ArmResidual, HipResidual;
        public bool IsValid => Status != BasisScaleFitStatus.NoData && Scale > 0f;
        public static BasisScaleFitResult Invalid => new BasisScaleFitResult
        {
            Scale = 0f,
            Status = BasisScaleFitStatus.NoData,
            UsedCount = 0,
            EyeResidual = 1f,
            ArmResidual = 1f,
            HipResidual = 1f,
        };
    }
}
