using Unity.Scripting.LifecycleManagement;
using Basis.BasisUI;
using Basis.IK;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;

[AutoStaticsCleanup]
public static partial class BasisHeightDriver
{
    public const float FallbackHeightInMeters = 1.61f;

    // Small epsilon to prevent divide-by-zero and ratio explosions.
    private const float Epsilon = 1e-5f;

    public static float PlayerCenterEyeVerticalOffset = 0f;

    public static float AppliedUpScale = 1f;

    public static float ScaledToMatchValue = 1f;

    public static float PlayerEyeHeight = FallbackHeightInMeters;
    public static bool HasGenuinePlayerEyeHeight = false;
    public static bool HasUserCalibratedHeight = false;
    public static float AvatarEyeHeight = FallbackHeightInMeters;

    public static float PlayerArmSpan = FallbackHeightInMeters;
    public static bool HasGenuinePlayerArmSpan = false;
    public static float AvatarArmSpan = FallbackHeightInMeters;

    public static float ObservedEyeConfidence = 0f;
    public static float ObservedArmSpanConfidence = 0f;

    public enum BasisBodyMeasurementSource
    {
        Fallback,
        Stated,
        Saved,
        Measured,
        SlimeVR,
    }

    public static BasisBodyMeasurementSource EyeHeightSource = BasisBodyMeasurementSource.Fallback;
    public static BasisBodyMeasurementSource ArmSpanSource = BasisBodyMeasurementSource.Fallback;

    public static float AppliedUniformScale = 0f;
    public static BasisScaleFitResult LastScaleFit = BasisScaleFitResult.Invalid;

    public static float PlayerHipHeight = 0f;
    public static bool HasGenuinePlayerHipHeight = false;
    public static float PlayerEyeToHipDrop = 0f;
    public static float AvatarHipHeight = 0f;
    public static float AvatarLegSpan = 0f;
    public static float AvatarSpineSpan = 0f;
    public static float AvatarShoulderWidth = 0f;
    public static float AvatarHipJointHeight = 0f;
    public static float AvatarTorsoSpan = 0f;
    public static float ScaledAvatarEyeHeight = FallbackHeightInMeters;

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

    public static bool HasRuntimeOscEyeHeightOverride = false;
    public static float RuntimeOscEyeHeightMeters = FallbackHeightInMeters;

    public static float DeviceScale = 1f;
    public static float HeightModeGroundingOffset = 0f;
    public static float SizingEyeHeight => BasisCalibrationMath.SizingEyeHeight(PlayerEyeHeight, PlayerArmSpan, HasGenuinePlayerArmSpan, BasisStatedHeight.ImpliedEyeHeight);
    public static bool EyeHeightLooksFloorShifted => SizingEyeHeight < PlayerEyeHeight;
    public static float EffectiveAvatarArmSpan
    {
        get
        {
            var fit = Basis.Scripts.Drivers.BasisLocalRigDriver.AppliedBodyFit;
            if (!fit.HasArmFit || Mathf.Approximately(fit.ArmScale, 1f))
            {
                return AvatarArmSpan;
            }
            float shoulders = Mathf.Clamp(AvatarShoulderWidth, 0f, AvatarArmSpan);
            return shoulders + (AvatarArmSpan - shoulders) * fit.ArmScale;
        }
    }

    public static void ApplyScaleAndHeight()
    {
        // Order matters, and it is the opposite of what it looks like. The scale fit reads only the
        // AUTHORED avatar measurements and the player's, so it must run before the stretcher has
        // resized anything -- otherwise the two chase each other, each reacting to the other's last
        // answer. Then the stretcher fits against the scale that was just chosen, taking up exactly
        // the residual that scale left. Only then is every metric below derived from the fitted body.
        SolveUniformScale();

        RevaluateUnscaledHeight(SMModuleCalibration.HeightMode);
        if (HasRuntimeOscEyeHeightOverride)
        {
            if (SMModuleCalibration.ApplyCustomScale)
            {
                BasisLocalPlayer.Instance?.LocalRigDriver?.RefreshBodyFit();
                ApplyRuntimeOscEyeHeightOverride(RuntimeOscEyeHeightMeters);
                return;
            }

            ClearRuntimeOscEyeHeightOverride();
        }

        ApplyScale(SMModuleCalibration.ApplyCustomScale, SMModuleCalibration.SelectedScale);
        BasisLocalPlayer.Instance?.LocalRigDriver?.RefreshBodyFit();
        ChooseHeightToUse(SMModuleCalibration.HeightMode);
        RefreshCameraScaleNow();
        ScheduleHeightChangeCallback(HeightModeChange.OnApplyHeightAndScale);

        // DeviceScale (and possibly the avatar) just re-resolved: re-derive the calibrated FBT position
        // offsets so the existing T-pose calibration keeps fitting (avatar swap / scale slider) instead
        // of going stale by the scale delta. No-op with no stored calibration.
        Basis.Scripts.Avatar.BasisAvatarIKStageCalibration.ReprojectTrackerOffsetsForCurrentAvatar();
    }

    public static void OnAvatarFBCalibration()
    {
        HasUserCalibratedHeight = true;
        // An explicit recalibration is the player saying the current fit is wrong, so the observations
        // that produced it have to go with it — otherwise the high-water estimate would simply be
        // re-adopted a moment later and recalibrating could never change anything. This is the escape
        // hatch for a session poisoned by a bad tracking episode or a different person in the headset.
        BasisBodyEvidenceSampler.ResetEvidence();
        HasGenuinePlayerArmSpan = false;
        CapturePlayerHeight();
        PersistCalibratedBodySize();
        ApplyScaleAndHeight();
        ScheduleHeightChangeCallback(HeightModeChange.OnAvatarFBCalibration);
    }

    // Plausibility band for persisted body measurements (metres): outside this, treat as junk.
    public const float MinPlausibleBodyMeasure = 0.8f;
    public const float MaxPlausibleBodyMeasure = 2.8f;

    private static void PersistCalibratedBodySize()
    {
        if (SMModuleSitStand.IsSteatedMode || HasGenuinePlayerEyeHeight == false)
        {
            return;
        }
        bool eyePlausible = PlayerEyeHeight >= MinPlausibleBodyMeasure && PlayerEyeHeight <= MaxPlausibleBodyMeasure;
        bool spanPlausible = PlayerArmSpan >= MinPlausibleBodyMeasure && PlayerArmSpan <= MaxPlausibleBodyMeasure;

        // A measurement that reads far shorter than its sibling implies was under-measured — a
        // physically-seated calibration reads the eye ~25-35% short, bent arms read the span short.
        // Don't overwrite the saved good value with it; the sibling still saves.
        bool eyeLooksUnderMeasured = spanPlausible
            && BasisCalibrationMath.AutoHeightModePicksArmSpan(PlayerEyeHeight, PlayerArmSpan);
        bool spanLooksUnderMeasured = eyePlausible
            && BasisCalibrationMath.ImpliedHeightFromEye(PlayerEyeHeight)
               > BasisCalibrationMath.ImpliedHeightFromSpan(PlayerArmSpan) * BasisCalibrationMath.AutoModeEyePreferenceBand;

        // An eye taller than any standing player was measured while the play space was shifted up (space
        // drag / grounding lift / external offset). Persisting it poisons every subsequent boot's scale, so
        // it never saves. The span does not testify against the eye: a bent-arm span reads exactly like a
        // lifted eye, and shrinking a real eye to match it is the worse failure.
        bool eyeLooksLiftPoisoned = BasisCalibrationMath.EyeHeightLooksLiftPoisoned(PlayerEyeHeight);

        // A stated height is the strongest veto available: it bounds the answer directly rather than
        // inferring it from a sibling measurement, so a reading that contradicts it never reaches disk.
        if (eyePlausible && eyeLooksUnderMeasured == false && eyeLooksLiftPoisoned == false
            && BasisStatedHeight.IsPlausibleEye(PlayerEyeHeight))
        {
            Basis.BasisUI.BasisSettingsDefaults.SavedPlayerEyeHeight.SetValue(PlayerEyeHeight);
        }
        if (spanPlausible && BasisStatedHeight.IsPlausibleSpan(PlayerArmSpan)
            && (spanLooksUnderMeasured == false || PlayerArmSpan > Basis.BasisUI.BasisSettingsDefaults.SavedPlayerArmSpan.RawValue))
        {
            Basis.BasisUI.BasisSettingsDefaults.SavedPlayerArmSpan.SetValue(PlayerArmSpan);
        }
    }

    private static void SeedPersistedBodySize()
    {
        if (HasGenuinePlayerEyeHeight || SMModuleSitStand.IsSteatedMode)
        {
            return;
        }
        float savedEye = Basis.BasisUI.BasisSettingsDefaults.SavedPlayerEyeHeight.RawValue;
        if (savedEye < MinPlausibleBodyMeasure || savedEye > MaxPlausibleBodyMeasure)
        {
            return;
        }
        // Heal a lift-poisoned save from before the persist guard existed: an eye taller than any standing
        // player was measured while the play space was shifted. The stated height, then the saved span,
        // stand in for it. The span alone never testifies against the eye — a bent-arm span reads exactly
        // like a lifted eye, and shrinking a real eye to match it boots a 1.7 m player as a 1.1 m one.
        float savedSpanForCheck = Basis.BasisUI.BasisSettingsDefaults.SavedPlayerArmSpan.RawValue;
        bool savedSpanUsable = savedSpanForCheck >= MinPlausibleBodyMeasure && savedSpanForCheck <= MaxPlausibleBodyMeasure;
        if (BasisCalibrationMath.EyeHeightLooksLiftPoisoned(savedEye))
        {
            if (!BasisStatedHeight.IsSet && !savedSpanUsable)
            {
                BasisDebug.LogWarning($"Saved eye height {savedEye:F3}m is taller than any standing player (lift-poisoned calibration); ignoring the saved body size. Recalibrate to re-measure.", BasisDebug.LogTag.Avatar);
                return;
            }
            float healedEye = BasisStatedHeight.IsSet ? BasisStatedHeight.ImpliedEyeHeight : BasisCalibrationMath.ImpliedHeightFromSpan(savedSpanForCheck) * BasisCalibrationMath.EyeToHeightRatio;
            BasisDebug.LogWarning($"Saved eye height {savedEye:F3}m is taller than any standing player (lift-poisoned calibration); using {(BasisStatedHeight.IsSet ? "your stated" : "the span-implied")} eye {healedEye:F3}m instead. Recalibrate to re-measure.", BasisDebug.LogTag.Avatar);
            savedEye = healedEye;
        }
        else if (savedSpanUsable && BasisCalibrationMath.ArmSpanLooksUnderMeasured(savedEye, savedSpanForCheck))
        {
            BasisDebug.Log($"Saved arm span {savedSpanForCheck:F3}m reads a body shorter than the saved eye height {savedEye:F3}m implies (arms not fully extended when measured); keeping the eye, a fuller reach replaces the span once observed.", BasisDebug.LogTag.Avatar);
        }
        PlayerEyeHeight = savedEye;
        HasGenuinePlayerEyeHeight = true;
        EyeHeightSource = BasisBodyMeasurementSource.Saved;
        // The saved size originated from an explicit calibration, so the pre-calibration ballpark
        // auto-scale estimator must not fight it.
        HasUserCalibratedHeight = true;
        float savedSpan = Basis.BasisUI.BasisSettingsDefaults.SavedPlayerArmSpan.RawValue;
        if (savedSpan >= MinPlausibleBodyMeasure && savedSpan <= MaxPlausibleBodyMeasure)
        {
            PlayerArmSpan = savedSpan;
            // A restored save is a real measurement of this player, so it is not to be re-measured from
            // whatever pose the next avatar load catches them in.
            HasGenuinePlayerArmSpan = true;
            ArmSpanSource = BasisBodyMeasurementSource.Saved;
        }
        BasisDebug.Log($"Seeded last session's calibrated body size: eye {PlayerEyeHeight:F3}m span {PlayerArmSpan:F3}m", BasisDebug.LogTag.Avatar);
    }
    private static void SeedStatedBodyHeight()
    {
        if (HasGenuinePlayerEyeHeight || SMModuleSitStand.IsSteatedMode || !BasisStatedHeight.IsSet)
        {
            return;
        }
        PlayerEyeHeight = BasisStatedHeight.ImpliedEyeHeight;
        HasGenuinePlayerEyeHeight = true;
        EyeHeightSource = BasisBodyMeasurementSource.Stated;
        if (!HasGenuinePlayerArmSpan)
        {
            ArmSpanSource = BasisBodyMeasurementSource.Stated;
            // An ape index of 1 is the population average and a much better opening guess than the
            // fallback, but it stays NOT genuine so a real reach measurement still overrides it.
            PlayerArmSpan = BasisStatedHeight.ImpliedArmSpan;
        }
        BasisDebug.Log($"Using your stated height {BasisStatedHeight.Meters:F2}m (eye {PlayerEyeHeight:F3}m) until something is measured.", BasisDebug.LogTag.Avatar);
    }

    private static void RefreshCameraScaleNow()
    {
        Basis.Scripts.Drivers.BasisLocalCameraDriver camera = Basis.Scripts.Drivers.BasisLocalCameraDriver.Instance;
        if (camera != null)
        {
            camera.UpdateCameraScale(HeightModeChange.OnApplyHeightAndScale);
        }
    }
    public static void ScheduleHeightChangeCallback(HeightModeChange Mode)
    {
        if (BasisLocalPlayer.Instance == null)
        {
            return;
        }
        BasisLocalPlayer.Instance.ExecuteNextFrame(() =>
        {
            BasisLocalPlayer.OnPlayersHeightChangedNextFrame?.Invoke(Mode);
        });
    }
    public static void ApplyScale(bool ScaleAvatar, float SelectedScale)
    {
        SelectedScale = SanitizePositive(SelectedScale, FallbackHeightInMeters);

        // A per-avatar nudge rides on top of the resolved target, so an avatar whose proportions the fit
        // cannot fully rescue can be corrected without distorting the player's measured body size —
        // which would otherwise follow them onto every other avatar they wear.
        BasisPerAvatarScale.RefreshForCurrentAvatar();

        // Resolve target + denominator in eye-height metres (the space admin limits use) so the clamp
        // is correct in every height mode. Scaling off keeps the factor at 1x unless a limit pulls in.
        float avatarEye = SanitizePositive(AvatarEyeHeight, FallbackHeightInMeters);
        float targetEyeMeters = ClampToAdminEyeHeight((ScaleAvatar ? SelectedScale : avatarEye) * BasisPerAvatarScale.Current);
        ScaledToMatchValue = targetEyeMeters / avatarEye;

        BasisDebug.Log($"Applying Scale to Avatar {ScaledToMatchValue}", BasisDebug.LogTag.Avatar);

        ApplyAvatarScale(ScaledToMatchValue);
    }

    public static float ClampToAdminEyeHeight(float eyeHeightMeters)
    {
        if (BasisNetworkModeration.LocalPlayerHasGlobalLockBypass())
        {
            return eyeHeightMeters;
        }

        float min = BasisNetworkModeration.ServerMinAvatarEyeHeightMeters;
        float max = BasisNetworkModeration.ServerMaxAvatarEyeHeightMeters;
        if (float.IsNaN(min) || float.IsInfinity(min) || min <= 0f) min = 0.1f;
        if (float.IsNaN(max) || float.IsInfinity(max) || max <= 0f) max = 100f;
        if (max < min) max = min;
        return Mathf.Clamp(eyeHeightMeters, min, max);
    }

    public enum HeightModeChange
    {
        OnAvatarFBCalibration,
        OnTpose,
        OnApplyHeightAndScale,
        // Sit/stand mode switch: the eye teleports vertically, so consumers that normally hold their
        // anchor through scale changes (the play-space-stable menu) must re-anchor fully.
        OnSitStandChanged
    }

    public static bool ApplyRuntimeOscEyeHeightOverride(float eyeHeightMeters)
    {
        eyeHeightMeters = SanitizePositive(eyeHeightMeters, FallbackHeightInMeters);
        eyeHeightMeters = ClampToAdminEyeHeight(eyeHeightMeters);

        float unscaledAvatarEyeHeight = SanitizePositive(AvatarEyeHeight, FallbackHeightInMeters);
        float scaleFactor = eyeHeightMeters / unscaledAvatarEyeHeight;

        HasRuntimeOscEyeHeightOverride = true;
        RuntimeOscEyeHeightMeters = eyeHeightMeters;
        BasisPerAvatarScale.RefreshForCurrentAvatar();
        float selectedEyeHeight = eyeHeightMeters / Mathf.Max(BasisPerAvatarScale.Current, 1e-3f);
        SMModuleCalibration.SelectedScale = selectedEyeHeight;
        BasisSettingsDefaults.SelectedScale.SetValueWithoutNotify(selectedEyeHeight);
        SettingsProviderIK.SetAvatarScaleSliderValueWithoutNotify(selectedEyeHeight);
        ScaledToMatchValue = scaleFactor;

        ApplyAvatarScale(scaleFactor);
        RefreshScaledHeightState(HeightModeChange.OnApplyHeightAndScale);
        return true;
    }

    public static void ClearRuntimeOscEyeHeightOverride()
    {
        HasRuntimeOscEyeHeightOverride = false;
        RuntimeOscEyeHeightMeters = FallbackHeightInMeters;
    }

    public static void RefreshScaledHeightState(HeightModeChange mode)
    {
        RevaluateUnscaledHeight(SMModuleCalibration.HeightMode);
        ChooseHeightToUse(SMModuleCalibration.HeightMode);
        ScheduleHeightChangeCallback(mode);
        Basis.Scripts.Avatar.BasisAvatarIKStageCalibration.ReprojectTrackerOffsetsForCurrentAvatar();
    }

    public static bool TryGetMatchedEyeHeightOverrideMeters(BasisRemotePlayer target, out float eyeHeightMeters)
    {
        eyeHeightMeters = 0f;
        if (target?.NetworkReceiver == null || target.BasisAvatar == null || BasisLocalPlayer.Instance?.LocalAvatarDriver == null)
        {
            return false;
        }

        // Mirror the REMOTE avatar's rendered eye height. AvatarEyePosition is already authored in
        // metres with the model/import root scale baked in, while the network sends the full current
        // Animator-root scale. Only their ratio is a runtime override; multiplying by the full network
        // scale applied a common 0.01 import scale twice.
        target.NetworkReceiver.GetLatestNetworkPose(out _, out _, out var networkScale);
        float remoteAuthoredEye = target.BasisAvatar.AvatarEyePosition.x;
        if (float.IsNaN(remoteAuthoredEye) || float.IsInfinity(remoteAuthoredEye) || remoteAuthoredEye <= 0f)
        {
            return false;
        }

        float authoredRootScale = target.RemoteAvatarDriver != null
            ? target.RemoteAvatarDriver.ColliderScaleReference.y
            : 1f;
        eyeHeightMeters = BasisCalibrationMath.ScaleAuthoredMetricByRootRatio(
            remoteAuthoredEye, networkScale.y, authoredRootScale);
        return !float.IsNaN(eyeHeightMeters) && !float.IsInfinity(eyeHeightMeters) && eyeHeightMeters > 0f;
    }

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

            c.SetTposeScaled(c.TposeLocal.position * ScaleFactor, c.TposeLocal.rotation);
            c.ScaledOffset = c.Offset * ScaleFactor;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void HookBootModeChanged()
    {
        BasisDeviceManagement.OnBootModeChanged -= OnBootModeChangedReapplyCalibration;
        BasisDeviceManagement.OnBootModeChanged += OnBootModeChangedReapplyCalibration;
    }

    private static void OnBootModeChangedReapplyCalibration(string mode)
    {
        if (BasisDeviceManagement.IsCurrentModeVR() == false)
        {
            return;
        }
        // Remove the previous mode's applied measurement…
        HasGenuinePlayerEyeHeight = false;
        HasGenuinePlayerArmSpan = false;
        HasGenuinePlayerHipHeight = false;
        // …including anything observed, which on desktop described a mouse-driven head and a pair of
        // synthesized hands rather than a body.
        BasisBodyEvidenceSampler.ResetEvidence();
        if (BasisLocalPlayer.Instance == null)
        {
            return; // boot-time switch: the first avatar load runs this same reapply flow itself
        }
        // …and reapply the stored calibration through the normal pipeline.
        CapturePlayerHeight(recaptureEyeHeight: false);
        ApplyScaleAndHeight();
        Basis.Scripts.Avatar.BasisAvatarIKStageCalibration.ApplyCalibrationToCurrentAvatar();
    }

    public static void CapturePlayerHeight(bool recaptureEyeHeight = true)
    {
        BasisDebug.Log("Capturing Player Height", BasisDebug.LogTag.IK);
        SeedPersistedBodySize();
        SeedStatedBodyHeight();
        if (BasisCalibrationMath.ShouldRecaptureEyeHeight(recaptureEyeHeight, HasGenuinePlayerEyeHeight))
        {
            BasisLocalHeightCalculator.CalculatePlayerEyeHeight();
        }
        // The span is gated exactly like the eye height, and it matters MORE here: an avatar load can
        // happen at any moment, and a player with their arms at their sides measures barely a third of
        // their reach. Re-measuring it there handed the body fit a span short by half a metre, which it
        // faithfully turned into arms shrunk by the full deviation budget on every single avatar load.
        if (BasisCalibrationMath.ShouldRecaptureEyeHeight(recaptureEyeHeight, HasGenuinePlayerArmSpan))
        {
            BasisLocalHeightCalculator.CalculatePlayerArmSpan();
        }
        if (BasisCalibrationMath.ShouldRecaptureEyeHeight(recaptureEyeHeight, HasGenuinePlayerHipHeight))
        {
            BasisLocalHeightCalculator.CalculatePlayerHipHeight();
        }

        if (ObservingBodyEvidence)
        {
            AdoptObservedBodyEvidence();
        }

        if (Basis.BasisUI.BasisSettingsDefaults.FBIKArmHeightRatioEnabled.RawValue)
        {
            float ratio = Mathf.Max(0.1f, Basis.BasisUI.BasisSettingsDefaults.FBIKArmHeightRatio.RawValue);
            PlayerArmSpan = SanitizePositive(PlayerEyeHeight, FallbackHeightInMeters) * ratio;
        }
        else
        {
            BasisLocalHeightCalculator.ValidateEyeToArmSizesPlayer();
        }

        // Optional safety: sanitize captured values in case calculator produced junk.
        PlayerEyeHeight = SanitizePositive(PlayerEyeHeight, FallbackHeightInMeters);
        PlayerArmSpan = SanitizePositive(PlayerArmSpan, FallbackHeightInMeters);
        SMModuleSitStand.RefreshSeatedLift();
    }

    public struct ObservedBodySize
    {
        public float EyeHeight;
        public float ArmSpan;
        public bool EyeIsGenuine;
        public bool SpanIsGenuine;
    }

    private static bool TryGetObservedBodySize(out ObservedBodySize result)
    {
        result = new ObservedBodySize
        {
            EyeHeight = PlayerEyeHeight,
            ArmSpan = PlayerArmSpan,
            EyeIsGenuine = HasGenuinePlayerEyeHeight,
            SpanIsGenuine = HasGenuinePlayerArmSpan,
        };

        ObservedEyeConfidence = 0f;
        ObservedArmSpanConfidence = 0f;
        if (BasisDeviceManagement.IsUserInDesktop())
        {
            return false;
        }

        bool changed = false;

        if (BasisBodyEvidenceSampler.TryGetArmSpan(out float observedSpan, out float spanConfidence))
        {
            ObservedArmSpanConfidence = spanConfidence;
            // A stated height caps how long anyone's reach can credibly be; past that it is a glitch.
            if (observedSpan > result.ArmSpan && Plausible(observedSpan) && BasisStatedHeight.IsPlausibleSpan(observedSpan))
            {
                result.ArmSpan = observedSpan;
                result.SpanIsGenuine = true;
                changed = true;
            }
        }

        // Seated mode substitutes a virtual standing eye; observing the chair would overwrite it.
        if (SMModuleSitStand.IsSteatedMode)
        {
            return changed;
        }
        if (BasisBodyEvidenceSampler.TryGetEyeHeight(out float observedEye, out float eyeConfidence))
        {
            ObservedEyeConfidence = eyeConfidence;
            // The one way an eye reading CAN come out too long is a vertical shift of the play space; the
            // absolute ceiling and a stated height catch that. The arm span is not a witness: a bent-arm
            // span would veto every honest eye reading above it and freeze the scale at the short reach.
            bool poisoned = BasisCalibrationMath.EyeHeightLooksLiftPoisoned(observedEye);
            if (observedEye > result.EyeHeight && Plausible(observedEye)
                && poisoned == false && BasisStatedHeight.IsPlausibleEye(observedEye))
            {
                result.EyeHeight = observedEye;
                result.EyeIsGenuine = true;
                changed = true;
            }
        }

        return changed;
    }

    private static void AdoptObservedBodyEvidence()
    {
        if (!TryGetObservedBodySize(out ObservedBodySize observed))
        {
            return;
        }
        BasisDebug.Log(
            $"Adopting observed body size: eye {PlayerEyeHeight:F3}->{observed.EyeHeight:F3}m, span {PlayerArmSpan:F3}->{observed.ArmSpan:F3}m",
            BasisDebug.LogTag.Avatar);
        if (!Mathf.Approximately(PlayerEyeHeight, observed.EyeHeight)) EyeHeightSource = BasisBodyMeasurementSource.Measured;
        if (!Mathf.Approximately(PlayerArmSpan, observed.ArmSpan)) ArmSpanSource = BasisBodyMeasurementSource.Measured;
        PlayerEyeHeight = observed.EyeHeight;
        PlayerArmSpan = observed.ArmSpan;
        HasGenuinePlayerEyeHeight = observed.EyeIsGenuine;
        HasGenuinePlayerArmSpan = observed.SpanIsGenuine;
    }

    private static bool Plausible(float measure) =>
        measure >= MinPlausibleBodyMeasure && measure <= MaxPlausibleBodyMeasure;

    public const float EvidenceReapplyThresholdMeters = 0.02f;
    private const float EvidenceReapplyIntervalSeconds = 1f;
    private static float s_evidenceReapplyTimer;

    private static float s_targetEye, s_targetSpan;
    private static bool s_targetEyeGenuine, s_targetSpanGenuine;
    private static bool s_hasRefitTarget;
    private static float s_refitFromEye, s_refitFromSpan;
    private static bool s_refitHeldLogged;
    public const float MeasureWindowSeconds = 60f;
    private static float s_measureWindowRemaining;
    public static bool MeasureWindowOpen => s_measureWindowRemaining > 0f;
    public static bool ObservingBodyEvidence => BasisSettingsDefaults.ContinuousBodyMeasurement.RawValue || MeasureWindowOpen;

    public static void BeginMeasureWindow()
    {
        s_measureWindowRemaining = MeasureWindowSeconds;
    }

    public static void TickObservedEvidence(float deltaTime)
    {
        if (BasisLocalPlayer.Instance == null || BasisDeviceManagement.IsUserInDesktop())
        {
            return;
        }

        if (s_measureWindowRemaining > 0f)
        {
            s_measureWindowRemaining -= deltaTime;
        }

        if (ObservingBodyEvidence)
        {
            s_evidenceReapplyTimer += deltaTime;
            if (s_evidenceReapplyTimer >= EvidenceReapplyIntervalSeconds)
            {
                s_evidenceReapplyTimer = 0f;
                StageObservedBodySize();
            }
        }

        if (!s_hasRefitTarget)
        {
            return;
        }

        // The measurement keeps. Waiting costs nothing; snapping someone's scale mid-grab does.
        if (BasisCalibrationRefitGate.ShouldHoldRefit(out string reason))
        {
            if (!s_refitHeldLogged)
            {
                s_refitHeldLogged = true;
                BasisDebug.Log($"Better body measurement is ready; holding the refit until later ({reason}).", BasisDebug.LogTag.Avatar);
            }
            return;
        }
        s_refitHeldLogged = false;
        s_hasRefitTarget = false;

        if (!Mathf.Approximately(PlayerEyeHeight, s_targetEye)) EyeHeightSource = BasisBodyMeasurementSource.Measured;
        if (!Mathf.Approximately(PlayerArmSpan, s_targetSpan)) ArmSpanSource = BasisBodyMeasurementSource.Measured;
        PlayerEyeHeight = s_targetEye;
        PlayerArmSpan = s_targetSpan;
        HasGenuinePlayerEyeHeight |= s_targetEyeGenuine;
        HasGenuinePlayerArmSpan |= s_targetSpanGenuine;
        ApplyScaleAndHeight();
        AnnounceRefit(s_refitFromEye, s_refitFromSpan);
    }

    private static void StageObservedBodySize()
    {
        if (!TryGetObservedBodySize(out ObservedBodySize observed))
        {
            return;
        }

        if (Mathf.Abs(observed.EyeHeight - PlayerEyeHeight) <= EvidenceReapplyThresholdMeters
            && Mathf.Abs(observed.ArmSpan - PlayerArmSpan) <= EvidenceReapplyThresholdMeters)
        {
            return; // real but not worth moving the world for
        }

        if (!s_hasRefitTarget)
        {
            s_refitFromEye = PlayerEyeHeight;
            s_refitFromSpan = PlayerArmSpan;
        }
        s_targetEye = observed.EyeHeight;
        s_targetSpan = observed.ArmSpan;
        s_targetEyeGenuine = observed.EyeIsGenuine;
        s_targetSpanGenuine = observed.SpanIsGenuine;
        s_hasRefitTarget = true;

        BasisDebug.Log(
            $"Observed body size improved (eye {PlayerEyeHeight:F3}->{s_targetEye:F3}m, span {PlayerArmSpan:F3}->{s_targetSpan:F3}m); refitting at the next safe moment.",
            BasisDebug.LogTag.Avatar);
    }

    private static void AnnounceRefit(float fromEye, float fromSpan)
    {
        float eyeDelta = PlayerEyeHeight - fromEye;
        float spanDelta = PlayerArmSpan - fromSpan;
        string detail = Mathf.Abs(spanDelta) >= Mathf.Abs(eyeDelta)
            ? string.Format(BasisLocalization.Get("calibration.refit.reach"), PlayerArmSpan)
            : string.Format(BasisLocalization.Get("calibration.refit.height"), PlayerEyeHeight);

        BasisNotificationCenter.LogResolved(
            BasisLocalization.Get("calibration.refit.title"),
            detail,
            AddressableAssets.Sprites.Information,
            BasisNotificationStatus.Accepted,
            BasisNotificationCategory.Avatar);
    }

    public static void CaptureAvatarHeightDuringTpose()
    {
        ClearRuntimeOscEyeHeightOverride();

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
        BasisLocalHeightCalculator.CalculateAvatarBodySegments();
        BasisLocalHeightCalculator.ValidateEyeToArmSizesAvatar();

        // Sanitize captured values (protect against NaN/Inf/<=0 from rig issues)
        AvatarEyeHeight = SanitizePositive(AvatarEyeHeight, FallbackHeightInMeters);
        AvatarArmSpan = SanitizePositive(AvatarArmSpan, FallbackHeightInMeters);

        ApplyAvatarScale(previousScale);
        ScheduleHeightChangeCallback(HeightModeChange.OnTpose);
    }

    private static BasisSelectedHeightMode s_lastAutoResolvedMode = BasisSelectedHeightMode.EyeHeight;

    public static BasisSelectedHeightMode ResolveHeightMode(BasisSelectedHeightMode mode)
    {
        if (mode != BasisSelectedHeightMode.Auto)
        {
            return mode;
        }
        if (BasisDeviceManagement.IsUserInDesktop())
        {
            return BasisSelectedHeightMode.EyeHeight;
        }
        if (s_lastAutoResolvedMode != BasisSelectedHeightMode.BestFit)
        {
            s_lastAutoResolvedMode = BasisSelectedHeightMode.BestFit;
            BasisDebug.Log("Auto height mode resolved to BestFit", BasisDebug.LogTag.Avatar);
        }
        return BasisSelectedHeightMode.BestFit;
    }

    public static BasisScaleInputs CurrentScaleInputs()
    {
        bool fitEnabled = BasisSettingsDefaults.FBIKBodyFit.RawValue;
        float sizingEye = SizingEyeHeight;
        bool spanUnderMeasured = HasGenuinePlayerEyeHeight && BasisCalibrationMath.ArmSpanLooksUnderMeasured(sizingEye, PlayerArmSpan);
        bool blend = TryGetArmToHeightBlend(out float armToHeightBlend);
        float k = BasisLocalPlayer.Instance?.LocalAvatarDriver?.ScaleAvatarModification != null ? BasisLocalPlayer.Instance.LocalAvatarDriver.ScaleAvatarModification.ApplyScale : AppliedUpScale;
        return new BasisScaleInputs
        {
            Mode = ResolveHeightMode(SMModuleCalibration.HeightMode),
            Desktop = BasisDeviceManagement.IsUserInDesktop(),
            Seated = SMModuleSitStand.IsSteatedMode,
            Blend = blend,
            BlendAmount = armToHeightBlend,
            SpanUsable = HasGenuinePlayerArmSpan && !spanUnderMeasured,
            PlayerEye = SanitizePositive(sizingEye, FallbackHeightInMeters),
            PlayerTrackedEye = SanitizePositive(PlayerEyeHeight, FallbackHeightInMeters),
            PlayerSpan = SanitizePositive(PlayerArmSpan, FallbackHeightInMeters),
            PlayerEyeOffset = PlayerCenterEyeVerticalOffset,
            AvatarEye = SanitizePositive(AvatarEyeHeight, FallbackHeightInMeters),
            AvatarSpan = SanitizePositive(AvatarArmSpan, FallbackHeightInMeters),
            AvatarShoulderWidth = AvatarShoulderWidth,
            AppliedUpScale = SanitizePositive(k, 1f),
            FitDeviation = fitEnabled ? Mathf.Clamp(BasisSettingsDefaults.FBIKBodyFitMaxDeviation.RawValue, 0f, BasisBodyFitCore.MaxDeviationCeiling) : 0f,
        };
    }
    public static void SolveUniformScale()
    {
        BasisScaleState state = BasisScaleResolver.Resolve(CurrentScaleInputs());
        LastScaleFit = state.BestFit;
        AppliedUniformScale = state.UniformScale;
        if (state.BestFit.IsValid && state.BestFit.Status != s_lastScaleFitStatus)
        {
            s_lastScaleFitStatus = state.BestFit.Status;
            BasisDebug.Log($"Scale fit {state.BestFit.Status} from {state.BestFit.UsedCount} measurement(s): scale {state.BestFit.Scale:F4} | leftover for the stretcher — eye {state.BestFit.EyeResidual:F3} arm {state.BestFit.ArmResidual:F3}", BasisDebug.LogTag.Avatar);
        }
        float sizingEye = SizingEyeHeight;
        if (sizingEye < PlayerEyeHeight && !Mathf.Approximately(sizingEye, s_lastLoggedSizingEye))
        {
            s_lastLoggedSizingEye = sizingEye;
            BasisDebug.LogWarning($"Your eye height reads {PlayerEyeHeight:F3} m, taller than anyone standing, so the VR floor is likely {PlayerEyeHeight - sizingEye:F2} m too low. Sizing you from your {(BasisStatedHeight.IsSet ? "stated height" : "arm span")} as a {sizingEye:F3} m eye height and lowering the play space to match.", BasisDebug.LogTag.Avatar);
        }
    }
    private static BasisScaleFitStatus s_lastScaleFitStatus = BasisScaleFitStatus.NoData;
    private static float s_lastLoggedSizingEye;

    public static bool TryGetArmToHeightBlend(out float blend)
    {
        blend = Mathf.Clamp(Basis.BasisUI.BasisSettingsDefaults.ArmToHeightBlend.RawValue,
            BasisCalibrationMath.ArmToHeightBlendMin, BasisCalibrationMath.ArmToHeightBlendMax);
        return Basis.BasisUI.BasisSettingsDefaults.EnableArmToHeightBlend.RawValue
            && BasisDeviceManagement.IsUserInDesktop() == false;
    }

    public static void RevaluateUnscaledHeight(BasisSelectedHeightMode Height)
    {
        BasisScaleInputs inputs = CurrentScaleInputs();
        inputs.Mode = ResolveHeightMode(Height);
        BasisScaleState state = BasisScaleResolver.Resolve(inputs);
        SelectedUnScaledAvatarHeight = SanitizePositive(state.AvatarMetric, FallbackHeightInMeters);
        SelectedUnScaledPlayerHeight = SanitizePositive(state.PlayerMetric, FallbackHeightInMeters);
    }

    public static void ChooseHeightToUse(BasisSelectedHeightMode Height)
    {
        // Desktop uses eye-height as the stable metric.
        if (BasisDeviceManagement.IsUserInDesktop())
        {
            Height = BasisSelectedHeightMode.EyeHeight;
        }
        Height = ResolveHeightMode(Height);

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

        // Current applied avatar scale (1 = unscaled).
        AppliedUpScale = SanitizePositive(avatarDriver.ScaleAvatarModification.ApplyScale, 1f);

        BasisScaleInputs inputs = CurrentScaleInputs();
        inputs.Mode = Height;
        inputs.AppliedUpScale = AppliedUpScale;
        BasisScaleState state = BasisScaleResolver.Resolve(inputs);
        bool blendHeights = state.Blended;
        float armToHeightBlend = inputs.BlendAmount, eyeScaleOffset = state.EyeOffset;

        // AppliedUpScale multiplies BOTH player and avatar metrics.
        SelectedScaledPlayerHeight = SanitizePositive((eyeScaleOffset + state.PlayerMetric) * AppliedUpScale, 1.6f);
        SelectedScaledAvatarHeight = SanitizePositive(state.AvatarMetric * AppliedUpScale, 1.6f);
        SelectedUnScaledAvatarHeight = SanitizePositive(state.AvatarMetric, FallbackHeightInMeters);
        SelectedUnScaledPlayerHeight = SanitizePositive(state.PlayerMetric, FallbackHeightInMeters);
        ScaledAvatarEyeHeight = SanitizePositive(state.ScaledAvatarEye, FallbackHeightInMeters);

        PlayerToDefaultRatioScaled = SafeDivide(SelectedScaledPlayerHeight, FallbackHeightInMeters, 1f);
        AvatarToDefaultRatioScaled = SafeDivide(ScaledAvatarEyeHeight, FallbackHeightInMeters, 1f);
        PlayerToDefaultRatioScaledWithAvatarScale = PlayerToDefaultRatioScaled;
        AvatarToDefaultRatioScaledWithAvatarScale = AvatarToDefaultRatioScaled;

        // Relative ratios between player and avatar.
        PlayerToAvatarRatioScaled = SanitizePositive(SafeDivide(SelectedScaledPlayerHeight, SelectedScaledAvatarHeight, 1f), 1f);
        AvatarToPlayerRatioScaled = SanitizePositive(SafeDivide(SelectedScaledAvatarHeight, SelectedScaledPlayerHeight, 1f), 1f);

        float denominator = BasisCalibrationMath.StandingEyeDenominator(SelectedUnScaledPlayerHeight, eyeScaleOffset, 0f);
        DeviceScale = SanitizePositive(state.DeviceScale, 1f);
        AppliedUniformScale = state.UniformScale;
        HeightModeGroundingOffset = state.GroundingOffset;

        BasisDebug.Log(
            $"Height Mode: {(blendHeights ? $"ArmToHeightBlend {armToHeightBlend:P0}" : Height.ToString())} | PlayerMetric(scaled): {SelectedScaledPlayerHeight}m | " +
            $"AvatarMetric(scaled): {SelectedScaledAvatarHeight}m | " +
            $"PlayerToAvatar: {PlayerToAvatarRatioScaled} | AvatarToPlayer: {AvatarToPlayerRatioScaled} | " +
            $"PlayerToDefault: {PlayerToDefaultRatioScaledWithAvatarScale} | AvatarToDefault: {AvatarToDefaultRatioScaledWithAvatarScale} | " +
            $"DeviceScale: {DeviceScale} | Grounding: {HeightModeGroundingOffset:F4}m",
            BasisDebug.LogTag.Avatar
        );
        // Denominator breakdown so a systematic eye-height bias stays readable in-headset: compare the
        // true-eye estimate against your tape-measured standing eye height.
        BasisDebug.Log(
            $"Eye-height denominator (true standing eye estimate): {denominator:F3}m = eye {SelectedUnScaledPlayerHeight:F3} (measured {PlayerEyeHeight:F3}) " +
            $"+ eye offset {eyeScaleOffset:F3} (device->eye {PlayerCenterEyeVerticalOffset:F3}" +
            $"{(blendHeights ? $", weighted {Mathf.Clamp01(1f - armToHeightBlend):P0} by the arm-to-height ratio" : "")})",
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
