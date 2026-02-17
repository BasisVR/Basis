using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;

/// <summary>
/// Centralized height/scale orchestration for the local player avatar.
/// </summary>
public static class BasisHeightDriver
{
    public const float FallbackHeightInMeters = 1.61f;

    // Small epsilon to prevent divide-by-zero and ratio explosions.
    private const float Epsilon = 1e-5f;

    public static float AdditionalPlayerHeight = 0f;

    public static float AppliedUpScale = 1f;

    /// <summary>
    /// The most recently applied scale factor used to match the avatar to the selected target measurement.
    /// </summary>
    public static float ScaledToMatchValue = 1f;

    public static float PlayerEyeHeight = FallbackHeightInMeters;
    public static float AvatarEyeHeight = FallbackHeightInMeters;

    public static float PlayerArmSpan = FallbackHeightInMeters;
    public static float AvatarArmSpan = FallbackHeightInMeters;

    public static float SelectedScaledPlayerHeight = FallbackHeightInMeters;
    public static float SelectedScaledAvatarHeight = FallbackHeightInMeters;

    public static float SelectedUnScaledAvatarHeight = FallbackHeightInMeters;
    public static float SelectedUnScaledPlayerHeight = FallbackHeightInMeters;

    public static float PlayerToAvatarRatioScaled = 1f;
    public static float AvatarToPlayerRatioScaled = 1f;

    public static float PlayerToDefaultRatioScaledWithAvatarScale = 1f;
    public static float AvatarToDefaultRatioScaledWithAvatarScale = 1f;

    public static float PlayerToDefaultRatioScaled = 1f;
    public static float AvatarToDefaultRatioScaled = 1f;


    public static float DeviceScale = 1f;
    public static void ApplyScaleAndHeight()
    {
        RevaluateUnscaledHeight(SMModuleCalibration.HeightMode);
        ApplyScale(SMModuleCalibration.ApplyCustomScale, SMModuleCalibration.SelectedScale);
        ChooseHeightToUse(SMModuleCalibration.HeightMode);
        ScheduleHeightChangeCallback(HeightModeChange.OnApplyHeightAndScale);
    }

    public static void OnAvatarFBCalibration()
    {
        CapturePlayerHeight();
        ApplyScaleAndHeight();
        ScheduleHeightChangeCallback(HeightModeChange.OnAvatarFBCalibration);
    }
    public static void ScheduleHeightChangeCallback(HeightModeChange Mode)
    {
        BasisLocalPlayer.Instance.ExecuteNextFrame(() =>
        {
            BasisLocalPlayer.OnPlayersHeightChangedNextFrame?.Invoke(Mode);
        });
    }
    /// <summary>
    /// Applies a custom avatar scale based on a target measurement.
    /// </summary>
    public static void ApplyScale(bool ScaleAvatar, float SelectedScale)
    {
        // validate SelectedScale too.
        SelectedScale = SanitizePositive(SelectedScale, FallbackHeightInMeters);

        // Prefer selected unscaled avatar metric; fall back if invalid.
        float unscaled = SanitizePositive(SelectedUnScaledAvatarHeight, FallbackHeightInMeters);

        ScaledToMatchValue = SelectedScale / unscaled;

        // If user has disabled scaling, force to 1.
        if (!ScaleAvatar)
        {
            ScaledToMatchValue = 1f;
        }

        BasisDebug.Log($"Applying Scale to Avatar {ScaledToMatchValue}", BasisDebug.LogTag.Avatar);

        ApplyAvatarScale(ScaledToMatchValue);
    }

    public enum HeightModeChange
    {
        OnAvatarFBCalibration,
        OnTpose,
        OnApplyHeightAndScale
    }

    /// <summary>
    /// Applies a scale factor to the local avatar and updates cached bone offsets.
    /// </summary>
    public static void ApplyAvatarScale(float ScaleFactor)
    {
        // sanitize ScaleFactor to avoid NaN/Inf poisoning bones.
        ScaleFactor = SanitizePositive(ScaleFactor, 1f);

        var player = BasisLocalPlayer.Instance;
        if (player == null)
        {
            BasisDebug.LogError("No local player instance.", BasisDebug.LogTag.Avatar);
            return;
        }

        var avatarDriver = player.LocalAvatarDriver;
        var boneDriver = player.LocalBoneDriver;

        if (avatarDriver == null || boneDriver == null)
        {
            BasisDebug.LogError("Avatar or Bone driver missing; cannot apply custom height.", BasisDebug.LogTag.Avatar);
            return;
        }

        BasisDebug.Log($"Height Scaling Factor is {ScaleFactor}", BasisDebug.LogTag.Avatar);

        // The avatar driver owns the authoritative scale override.
        avatarDriver.ScaleAvatarModification.SetAvatarheightOverride(ScaleFactor);

        // Update cached per-bone data expected to be in "scaled" space.
        int count = boneDriver.ControlsLength;
        for (int Index = 0; Index < count; Index++)
        {
            BasisLocalBoneControl c = boneDriver.Controls[Index];

            c.TposeLocalScaled.position = c.TposeLocal.position * ScaleFactor;
            c.TposeLocalScaled.rotation = c.TposeLocal.rotation;
            c.ScaledOffset = c.Offset * ScaleFactor;
        }
    }

    public static void CapturePlayerHeight()
    {
        BasisDebug.Log("Capturing Player Height", BasisDebug.LogTag.IK);
        BasisLocalHeightCalculator.CalculatePlayerEyeHeight();
        BasisLocalHeightCalculator.CalculatePlayerArmSpan();
        BasisLocalHeightCalculator.ValidateEyeToArmSizesPlayer();

        // Optional safety: sanitize captured values in case calculator produced junk.
        PlayerEyeHeight = SanitizePositive(PlayerEyeHeight, FallbackHeightInMeters);
        PlayerArmSpan = SanitizePositive(PlayerArmSpan, FallbackHeightInMeters);
    }

    public static void CaptureAvatarHeightDuringTpose()
    {
        var player = BasisLocalPlayer.Instance;
        if (player == null)
        {
            BasisDebug.LogError("No local player instance.", BasisDebug.LogTag.Avatar);
            return;
        }

        var avatarDriver = player.LocalAvatarDriver;
        if (avatarDriver == null)
        {
            BasisDebug.LogError("Avatar driver missing; cannot capture avatar height.", BasisDebug.LogTag.Avatar);
            return;
        }

        // do not use AppliedUpScale as a temp restore var; use a local snapshot.
        float previousScale = SanitizePositive(avatarDriver.ScaleAvatarModification.ApplyScale, 1f);

        AppliedUpScale = previousScale;

        ApplyAvatarScale(1f); // Force unscaled to capture correct baseline measurements.

        BasisLocalHeightCalculator.CalculateAvatarEyeHeight();
        BasisLocalHeightCalculator.CalculateAvatarArmSpan();
        BasisLocalHeightCalculator.ValidateEyeToArmSizesAvatar();

        // Sanitize captured values (protect against NaN/Inf/<=0 from rig issues)
        AvatarEyeHeight = SanitizePositive(AvatarEyeHeight, FallbackHeightInMeters);
        AvatarArmSpan = SanitizePositive(AvatarArmSpan, FallbackHeightInMeters);

        ApplyAvatarScale(previousScale);
        ScheduleHeightChangeCallback(HeightModeChange.OnTpose);
    }

    public static void RevaluateUnscaledHeight(BasisSelectedHeightMode Height)
    {
        switch (Height)
        {
            case BasisSelectedHeightMode.ArmSpan:
                SelectedUnScaledAvatarHeight = SanitizePositive(AvatarArmSpan, FallbackHeightInMeters);
                SelectedUnScaledPlayerHeight = SanitizePositive(PlayerArmSpan, FallbackHeightInMeters);
                break;

            case BasisSelectedHeightMode.EyeHeight:
                SelectedUnScaledAvatarHeight = SanitizePositive(AvatarEyeHeight, FallbackHeightInMeters);
                SelectedUnScaledPlayerHeight = SanitizePositive(PlayerEyeHeight, FallbackHeightInMeters);
                break;
        }
    }

    public static void ChooseHeightToUse(BasisSelectedHeightMode Height)
    {
        // Desktop uses eye-height as the stable metric.
        if (BasisDeviceManagement.IsUserInDesktop())
        {
            Height = BasisSelectedHeightMode.EyeHeight;
        }

        var player = BasisLocalPlayer.Instance;
        if (player == null)
        {
            BasisDebug.LogError("No local player instance.", BasisDebug.LogTag.Avatar);
            return;
        }

        var avatarDriver = player.LocalAvatarDriver;
        if (avatarDriver == null)
        {
            BasisDebug.LogError("Avatar driver missing; cannot choose height.", BasisDebug.LogTag.Avatar);
            return;
        }

        Vector3 calibrationScale = avatarDriver.ScaleAvatarModification.DuringCalibrationScale;

        // sanitize calibration scale to prevent divide-by-zero / negative surprises.
        float calY = SanitizePositive(calibrationScale.y, 1f);
        calibrationScale.y = calY;

        // Current applied avatar scale (1 = unscaled).
        AppliedUpScale = SanitizePositive(avatarDriver.ScaleAvatarModification.ApplyScale, 1f);

        // AppliedUpScale multiplies BOTH player and avatar metrics.
        switch (Height)
        {
            case BasisSelectedHeightMode.ArmSpan:
                SelectedScaledPlayerHeight = calY * ((AdditionalPlayerHeight + PlayerArmSpan) * AppliedUpScale);
                SelectedScaledAvatarHeight = calY * (AvatarArmSpan * AppliedUpScale);

                SelectedUnScaledAvatarHeight = SanitizePositive(AvatarArmSpan, FallbackHeightInMeters);
                SelectedUnScaledPlayerHeight = SanitizePositive(PlayerArmSpan, FallbackHeightInMeters);
                break;

            case BasisSelectedHeightMode.EyeHeight:
                SelectedScaledPlayerHeight = calY * ((AdditionalPlayerHeight + PlayerEyeHeight) * AppliedUpScale);
                SelectedScaledAvatarHeight = calY * (AvatarEyeHeight * AppliedUpScale);

                SelectedUnScaledAvatarHeight = SanitizePositive(AvatarEyeHeight, FallbackHeightInMeters);
                SelectedUnScaledPlayerHeight = SanitizePositive(PlayerEyeHeight, FallbackHeightInMeters);
                break;
        }

        // stronger guards (NaN/Inf too), not only <=0.
        SelectedScaledPlayerHeight = SanitizePositive(SelectedScaledPlayerHeight, 1.6f);
        SelectedScaledAvatarHeight = SanitizePositive(SelectedScaledAvatarHeight, 1.6f);

        // "Default" denominator in the same space as SelectedScaled* (which currently includes calY)
        float defaultScaled = SanitizePositive(FallbackHeightInMeters * calY, FallbackHeightInMeters);

        PlayerToDefaultRatioScaled = SafeDivide(SelectedScaledAvatarHeight, FallbackHeightInMeters, 1f);
        AvatarToDefaultRatioScaled = SafeDivide(SelectedScaledPlayerHeight, FallbackHeightInMeters, 1f);
        // Use SafeDivide for all ratios (prevents NaN/Inf/0 denom explosions)
        PlayerToDefaultRatioScaledWithAvatarScale = SafeDivide(SelectedScaledAvatarHeight, defaultScaled, 1f);
        AvatarToDefaultRatioScaledWithAvatarScale = SafeDivide(SelectedScaledPlayerHeight, defaultScaled, 1f);

        // Relative ratios between player and avatar.
        PlayerToAvatarRatioScaled = SafeDivide(SelectedScaledPlayerHeight, SelectedScaledAvatarHeight, 1f);
        AvatarToPlayerRatioScaled = SafeDivide(SelectedScaledAvatarHeight, SelectedScaledPlayerHeight, 1f);

        // clamp/clean ratios against NaN/Inf/<=0.
        PlayerToAvatarRatioScaled = SanitizePositive(PlayerToAvatarRatioScaled, 1f);
        AvatarToPlayerRatioScaled = SanitizePositive(AvatarToPlayerRatioScaled, 1f);

        // Defensive clamps for unscaled metrics.
        SelectedUnScaledAvatarHeight = SanitizePositive(SelectedUnScaledAvatarHeight, 1f);
        SelectedUnScaledPlayerHeight = SanitizePositive(SelectedUnScaledPlayerHeight, 1f);

        // DeviceScale: keep your original intent/math, but make it safe.
        // avatarScaledMetric in meters-equivalent (unscaled metric * applied scale).
        float avatarScaledMetric = SanitizePositive(SelectedUnScaledAvatarHeight * AppliedUpScale, 1f);
        float playerMetric = SanitizePositive(AdditionalPlayerHeight + SelectedUnScaledPlayerHeight, 1f);

        DeviceScale = SafeDivide(avatarScaledMetric, playerMetric, 1f);
        DeviceScale = SanitizePositive(DeviceScale, 1f);

        BasisDebug.Log(
            $"Height Mode: {Height} | PlayerMetric(scaled): {SelectedScaledPlayerHeight}m | " +
            $"AvatarMetric(scaled): {SelectedScaledAvatarHeight}m | " +
            $"PlayerToAvatar: {PlayerToAvatarRatioScaled} | AvatarToPlayer: {AvatarToPlayerRatioScaled} | " +
            $"PlayerToDefault: {AvatarToDefaultRatioScaledWithAvatarScale} | AvatarToDefault: {PlayerToDefaultRatioScaledWithAvatarScale} | " +
            $"DeviceScale: {DeviceScale}",
            BasisDebug.LogTag.Avatar
        );
    }
    private static float SanitizePositive(float value, float fallback)
    {
        if (float.IsNaN(value) || float.IsInfinity(value) || value <= 0f)
        {
            BasisDebug.LogError("Data In Height Driver failed validation Stage 1", BasisDebug.LogTag.IK);
            return fallback;
        }

        return value;
    }

    private static float SafeDivide(float numerator, float denominator, float fallback)
    {
        if (float.IsNaN(numerator) || float.IsInfinity(numerator))
        {
            BasisDebug.LogError("Data In Height Driver failed validation Stage 2", BasisDebug.LogTag.IK);
            return fallback;
        }

        if (float.IsNaN(denominator) || float.IsInfinity(denominator))
        {
            BasisDebug.LogError("Data In Height Driver failed validation Stage 3", BasisDebug.LogTag.IK);
            return fallback;
        }

        if (denominator > -Epsilon && denominator < Epsilon)
        {
            BasisDebug.LogError("Data In Height Driver failed validation Stage 4", BasisDebug.LogTag.IK);
            return fallback;
        }

        return numerator / denominator;
    }
}
