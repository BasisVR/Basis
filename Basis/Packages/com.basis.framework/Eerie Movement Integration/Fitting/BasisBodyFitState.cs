using Basis.BasisUI;
using Basis.IK;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Settings;
using System.Globalization;
using UnityEngine;
public static class BasisStatedHeight
{
    public const float MinMeters = 1.0f;
    public const float MaxMeters = 2.4f;
    public const float EyeTolerance = 0.08f;
    public const float SpanTolerance = 0.12f;
    public static float Meters => Sanitize(Basis.BasisUI.BasisSettingsDefaults.StatedBodyHeight.RawValue);
    public static bool IsSet => Meters > 0f;
    public static float ImpliedEyeHeight => IsSet ? Meters * BasisCalibrationMath.EyeToHeightRatio : 0f;
    public static float ImpliedArmSpan => IsSet ? Meters * BasisCalibrationMath.SpanToHeightRatio : 0f;
    static float Sanitize(float meters)
    {
        if (float.IsNaN(meters) || float.IsInfinity(meters) || meters < MinMeters || meters > MaxMeters)
        {
            return 0f;
        }
        return meters;
    }
    public static bool IsPlausibleEye(float eyeMeters)
    {
        if (!IsSet || eyeMeters <= 0f)
        {
            return true;
        }
        float expected = ImpliedEyeHeight;
        return Mathf.Abs(eyeMeters - expected) <= expected * EyeTolerance;
    }
    public static bool IsPlausibleSpan(float spanMeters)
    {
        if (!IsSet || spanMeters <= 0f)
        {
            return true;
        }
        return spanMeters <= ImpliedArmSpan * (1f + SpanTolerance);
    }
    public static bool TryParse(string text, out float meters)
    {
        meters = 0f;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string s = text.Trim().ToLowerInvariant()
            .Replace("\"", " ").Replace("''", " ").Replace("’", "'")
            .Replace("feet", "'").Replace("foot", "'").Replace("ft", "'")
            .Replace("inches", " ").Replace("inch", " ").Replace("in", " ");

        bool saysMetric = s.Contains("cm") || s.Contains("m");
        bool imperial = !saysMetric && s.Contains("'");
        if (imperial)
        {
            int tick = s.IndexOf('\'');
            string feetPart = s.Substring(0, tick);
            string inchPart = s.Substring(tick + 1);
            if (!TryNumber(feetPart, out float feet))
            {
                return false;
            }
            TryNumber(inchPart, out float inches);
            meters = (feet * 12f + inches) * 0.0254f;
            return Validate(ref meters);
        }

        string[] parts = s.Split(new[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && TryNumber(parts[0], out float f2) && TryNumber(parts[1], out float i2) && f2 >= 3f && f2 <= 8f && i2 >= 0f && i2 < 12f)
        {
            meters = (f2 * 12f + i2) * 0.0254f;
            return Validate(ref meters);
        }

        if (!TryLeadingNumber(s, out float value))
        {
            return false;
        }
        bool saidCm = s.Contains("cm");
        bool saidM = !saidCm && s.Contains("m");
        meters = saidCm ? value * 0.01f : saidM ? value : (value > 3f ? value * 0.01f : value);
        return Validate(ref meters);
    }
    static bool Validate(ref float meters)
    {
        if (float.IsNaN(meters) || float.IsInfinity(meters) || meters < MinMeters || meters > MaxMeters)
        {
            meters = 0f;
            return false;
        }
        return true;
    }
    static bool TryLeadingNumber(string s, out float value)
    {
        value = 0f;
        var digits = new System.Text.StringBuilder(s.Length);
        bool started = false;
        foreach (char c in s)
        {
            bool part = char.IsDigit(c) || c == '.' || c == ',' || (!started && c == '-');
            if (part)
            {
                started = true;
                digits.Append(c == ',' ? '.' : c);
            }
            else if (started)
            {
                break;
            }
        }
        return started && float.TryParse(digits.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
    static bool TryNumber(string s, out float value)
    {
        value = 0f;
        if (string.IsNullOrWhiteSpace(s))
        {
            return false;
        }
        var digits = new System.Text.StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (char.IsDigit(c) || c == '.' || c == ',' || c == '-')
            {
                digits.Append(c == ',' ? '.' : c);
            }
        }
        return float.TryParse(digits.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
    public static string FormatCompact(float meters)
    {
        return meters > 0f ? $"{Mathf.RoundToInt(meters * 100f)} cm" : string.Empty;
    }
    public static string Format(float meters)
    {
        if (meters <= 0f)
        {
            return "-";
        }
        int cm = Mathf.RoundToInt(meters * 100f);
        int totalInches = Mathf.RoundToInt(meters / 0.0254f);
        int feet = totalInches / 12;
        int inches = totalInches % 12;
        return $"{cm} cm ({feet}' {inches}\")";
    }
}
public static class BasisBodyFitSummary
{
    public struct Facts
    {
        public float BodyHeight;
        public BasisHeightDriver.BasisBodyMeasurementSource HeightSource;
        public float Reach;
        public BasisHeightDriver.BasisBodyMeasurementSource ReachSource;
        public bool ReachMeasured;
        public float ReachConfidence;
        public bool HasAvatar;
        public float AvatarArmDifference, AvatarLegDifference;
        public bool ArmsFitted, LegsFitted;
        public BasisScaleFitStatus FitStatus;
        public float ScaleDeviation;
        public bool DifferentPersonSuspected;
    }
    public static Facts Gather()
    {
        var facts = new Facts
        {
            BodyHeight = BasisCalibrationMath.ImpliedHeightFromEye(BasisHeightDriver.PlayerEyeHeight),
            HeightSource = BasisHeightDriver.EyeHeightSource,
            Reach = BasisHeightDriver.PlayerArmSpan,
            ReachSource = BasisHeightDriver.ArmSpanSource,
            ReachMeasured = BasisHeightDriver.HasGenuinePlayerArmSpan,
            ReachConfidence = BasisHeightDriver.ObservedArmSpanConfidence,
            DifferentPersonSuspected = BasisBodyEvidenceSampler.LooksLikeADifferentPerson(),
        };

        BasisBodyFitResult fit = Basis.Scripts.Drivers.BasisLocalRigDriver.AppliedBodyFit;
        facts.ArmsFitted = fit.HasArmFit;
        facts.LegsFitted = fit.HasBodyFit;

        BasisScaleFitResult scaleFit = BasisHeightDriver.LastScaleFit;
        facts.FitStatus = scaleFit.Status;
        if (scaleFit.IsValid && scaleFit.EyeResidual > 0f)
        {
            facts.ScaleDeviation = (1f / scaleFit.EyeResidual) - 1f;
        }

        facts.HasAvatar = scaleFit.IsValid;
        facts.AvatarArmDifference = scaleFit.ArmResidual - 1f;
        facts.AvatarLegDifference = scaleFit.HipResidual - 1f;
        return facts;
    }
    public static string Build()
    {
        Facts facts = Gather();
        var sb = new System.Text.StringBuilder(320);

        sb.Append(BasisLocalization.Get("calibration.summary.height")).Append(' ')
          .Append(BasisStatedHeight.Format(facts.BodyHeight)).Append("  —  ")
          .Append(DescribeSource(facts.HeightSource)).Append('\n');

        sb.Append(BasisLocalization.Get("calibration.summary.reach")).Append(' ');
        if (facts.ReachMeasured)
        {
            sb.Append(BasisStatedHeight.Format(facts.Reach)).Append("  —  ").Append(DescribeSource(facts.ReachSource));
        }
        else
        {
            sb.Append(BasisLocalization.Get("calibration.summary.reach.unmeasured"));
        }
        sb.Append('\n');

        if (facts.HasAvatar)
        {
            sb.Append(DescribeAvatar(facts)).Append('\n');
            sb.Append(DescribeFit(facts));
        }

        if (facts.DifferentPersonSuspected)
        {
            sb.Append('\n').Append(BasisLocalization.Get("calibration.summary.differentPerson"));
        }

        return sb.ToString();
    }
    static string DescribeSource(BasisHeightDriver.BasisBodyMeasurementSource source) => source switch
    {
        BasisHeightDriver.BasisBodyMeasurementSource.Measured => BasisLocalization.Get("calibration.summary.source.measured"),
        BasisHeightDriver.BasisBodyMeasurementSource.Stated => BasisLocalization.Get("calibration.summary.source.stated"),
        BasisHeightDriver.BasisBodyMeasurementSource.Saved => BasisLocalization.Get("calibration.summary.source.saved"),
        BasisHeightDriver.BasisBodyMeasurementSource.SlimeVR => BasisLocalization.Get("calibration.summary.source.slimevr"),
        _ => BasisLocalization.Get("calibration.summary.source.fallback"),
    };
    static string DescribeAvatar(in Facts facts)
    {
        float arm = facts.AvatarArmDifference;
        if (Mathf.Abs(arm) < 0.02f)
        {
            return BasisLocalization.Get("calibration.summary.avatar.matches");
        }
        string key = arm > 0f ? "calibration.summary.avatar.armsLonger" : "calibration.summary.avatar.armsShorter";
        string phrase = string.Format(BasisLocalization.Get(key), Mathf.Abs(arm));
        string handling = facts.ArmsFitted ? BasisLocalization.Get("calibration.summary.avatar.adjusted") : BasisLocalization.Get("calibration.summary.avatar.beyondAdjustment");
        return $"{phrase} {handling}";
    }
    static string DescribeFit(in Facts facts)
    {
        switch (facts.FitStatus)
        {
            case BasisScaleFitStatus.EyeExact: return BasisLocalization.Get("calibration.summary.fit.exact");
            case BasisScaleFitStatus.Adjusted: return string.Format(BasisLocalization.Get("calibration.summary.fit.adjusted"), Mathf.Abs(facts.ScaleDeviation));
            case BasisScaleFitStatus.Compromised: return BasisLocalization.Get("calibration.summary.fit.compromised");
            default: return BasisLocalization.Get("calibration.summary.fit.none");
        }
    }
}
public static class BasisPerAvatarScale
{
    public const float Min = 0.5f;
    public const float Max = 2f;
    public const float None = 1f;
    public static float Current { get; private set; } = None;
    static string loadedForAvatar;
    static string KeyFor(string avatarId)
    {
        return $"avatarscale::{(uint)avatarId.GetHashCode():X8}";
    }
    public static void RefreshForCurrentAvatar()
    {
        string avatarId = BasisLocalPlayer.CurrentAvatarUniqueID;
        if (string.IsNullOrEmpty(avatarId))
        {
            Current = None;
            loadedForAvatar = null;
            return;
        }
        if (avatarId == loadedForAvatar)
        {
            return;
        }

        loadedForAvatar = avatarId;
        string key = KeyFor(avatarId);
        Current = BasisSettingsSystem.HasSaveData(key) ? Sanitize(BasisSettingsSystem.LoadFloat(key, None)) : None;
        if (!Mathf.Approximately(Current, None))
        {
            BasisDebug.Log($"Per-avatar size nudge for this avatar: {Current:P0}", BasisDebug.LogTag.Avatar);
        }
    }
    public static void SetForCurrentAvatar(float scale)
    {
        string avatarId = BasisLocalPlayer.CurrentAvatarUniqueID;
        if (string.IsNullOrEmpty(avatarId))
        {
            return;
        }
        loadedForAvatar = avatarId;
        Current = Sanitize(scale);
        BasisSettingsSystem.SaveFloat(KeyFor(avatarId), Current);
        BasisHeightDriver.ApplyScaleAndHeight();
    }
    public static void ClearForCurrentAvatar() => SetForCurrentAvatar(None);
    static float Sanitize(float scale)
    {
        if (float.IsNaN(scale) || float.IsInfinity(scale))
        {
            return None;
        }
        return Mathf.Clamp(scale, Min, Max);
    }
}
