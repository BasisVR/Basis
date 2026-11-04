using Basis.IK;
using Basis.Scripts.BasisSdk.Players;
using UnityEngine;
public struct BasisScaleInputs
{
    public BasisSelectedHeightMode Mode;
    public bool Desktop, Seated, SpanUsable;
    public float PlayerEye, PlayerTrackedEye, PlayerSpan, PlayerEyeOffset, AvatarEye, AvatarSpan, AvatarShoulderWidth, AppliedUpScale, FitDeviation;
}
public struct BasisScaleState
{
    public BasisSelectedHeightMode Mode;
    public BasisScaleFitResult BestFit;
    public float AvatarMetric, PlayerMetric, EyeOffset, UniformScale, DeviceScale, GroundingOffset, ScaledAvatarEye;
}
public static class BasisScaleResolver
{
    public static BasisScaleState Resolve(in BasisScaleInputs i)
    {
        BasisScaleState s = default;
        s.BestFit = BasisScaleFitResult.Invalid;
        s.Mode = i.Desktop ? BasisSelectedHeightMode.EyeHeight : i.Mode == BasisSelectedHeightMode.Auto ? BasisSelectedHeightMode.BestFit : i.Mode;
        float avatarEye = Positive(i.AvatarEye, BasisHeightDriver.FallbackHeightInMeters), playerEye = Positive(i.PlayerEye, BasisHeightDriver.FallbackHeightInMeters);
        float avatarSpan = Positive(i.AvatarSpan, avatarEye), playerSpan = Positive(i.PlayerSpan, playerEye), k = Positive(i.AppliedUpScale, 1f), fullOffset = Finite(i.PlayerEyeOffset), trackedEye = Positive(i.PlayerTrackedEye, playerEye);
        if (s.Mode == BasisSelectedHeightMode.ArmSpan)
        {
            s.AvatarMetric = avatarSpan;
            s.PlayerMetric = playerSpan;
            s.EyeOffset = 0f;
        }
        else
        {
            s.AvatarMetric = avatarEye;
            s.PlayerMetric = playerEye;
            s.EyeOffset = fullOffset;
            if (s.Mode == BasisSelectedHeightMode.BestFit && !i.Seated)
            {
                s.BestFit = SolveBestFit(in i, avatarEye, playerEye + fullOffset, avatarSpan, playerSpan);
                if (s.BestFit.IsValid)
                {
                    s.AvatarMetric = s.BestFit.Scale * (playerEye + fullOffset);
                }
            }
        }
        float denominator = s.PlayerMetric + s.EyeOffset;
        s.UniformScale = denominator > 1e-5f ? Positive(s.AvatarMetric / denominator, 1f) : 1f;
        s.DeviceScale = s.UniformScale * k;
        s.ScaledAvatarEye = avatarEye * k;
        s.GroundingOffset = i.Desktop || i.Seated ? 0f : BasisCalibrationMath.HeightModeGroundingOffset(avatarEye, k, s.DeviceScale, trackedEye + fullOffset);
        return s;
    }
    public static BasisScaleFitResult SolveBestFit(in BasisScaleInputs i, float avatarEye, float playerTrueEye, float avatarSpan, float playerSpan)
    {
        var measurements = new BasisBodyFitMeasurements
        {
            AvatarArmSpan = avatarSpan,
            AvatarShoulderWidth = i.AvatarShoulderWidth,
        };
        var input = new BasisScaleFitInput
        {
            MaxEyeDeviation = BasisScaleFitCore.DefaultMaxEyeDeviation,
            Eye = new BasisScaleFitSample { Player = playerTrueEye, Avatar = avatarEye, Weight = BasisScaleFitCore.EyeWeight },
            ArmSpan = new BasisScaleFitSample { Player = playerSpan, Avatar = avatarSpan, Slack = BasisBodyFitCore.ArmSpanSlack(in measurements, i.FitDeviation), Weight = i.SpanUsable ? BasisScaleFitCore.ArmSpanWeight : 0f },
            HipHeight = BasisScaleFitSample.None,
        };
        return BasisScaleFitCore.Solve(in input);
    }
    static float Positive(float value, float fallback) => float.IsNaN(value) || float.IsInfinity(value) || value <= 0f ? fallback : value;
    static float Finite(float value) => float.IsNaN(value) || float.IsInfinity(value) ? 0f : value;
}
