using Basis.BasisUI;
using Basis.IK;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;

public static class BasisHeightDriver
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
    public static float PlayerEyeToHipDrop = 0f;
    public static float AvatarHipHeight = 0f;
    public static float AvatarLegSpan = 0f;
    public static float AvatarSpineSpan = 0f;
    public static float AvatarShoulderWidth = 0f;

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
    // The avatar's arm span AFTER the body fit has resized its arm bones. Arm-span-based scaling has to
    // divide by the span the avatar actually has, not the authored one, or DeviceScale is wrong by the
    // whole fit ratio -- which reads in headset as the avatar being too large. Eye-height mode is immune
    // because the fit is height-neutral by construction. Equals AvatarArmSpan when no arm fit is applied.
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
        BasisLocalPlayer.Instance?.LocalRigDriver?.RefreshBodyFit();

        RevaluateUnscaledHeight(SMModuleCalibration.HeightMode);
        if (HasRuntimeOscEyeHeightOverride)
        {
            if (SMModuleCalibration.ApplyCustomScale)
            {
                ApplyRuntimeOscEyeHeightOverride(RuntimeOscEyeHeightMeters);
                return;
            }

            ClearRuntimeOscEyeHeightOverride();
        }

        ApplyScale(SMModuleCalibration.ApplyCustomScale, SMModuleCalibration.SelectedScale);
        ChooseHeightToUse(SMModuleCalibration.HeightMode);
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

    public static void ScheduleHeightChangeCallback(HeightModeChange Mode)
    {
        BasisLocalPlayer.Instance.ExecuteNextFrame(() =>
        {
            // Split per subscriber while the tracer is armed. The chain runs in a coroutine,
            // so a subscriber that moves the player or the avatar does it in the one window
            // BasisLocalPose never re-stamps -- and a later subscriber then reads it stale.
            BasisBoneWriteTracer.InvokeTraced(BasisLocalPlayer.OnPlayersHeightChangedNextFrame, Mode, "HeightChange");
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
        SMModuleCalibration.SelectedScale = eyeHeightMeters;
        BasisSettingsDefaults.SelectedScale.SetValueWithoutNotify(eyeHeightMeters);
        SettingsProviderIK.SetAvatarScaleSliderValueWithoutNotify(eyeHeightMeters);
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

        // Mirror the REMOTE avatar's rendered eye height: its authored (already rendered-space) eye height
        // at its current network root scale. Reading local measurements was the bug.
        target.NetworkReceiver.GetLatestNetworkPose(out _, out _, out var networkScale);
        float remoteAuthoredEye = target.BasisAvatar.AvatarEyePosition.x;
        if (float.IsNaN(remoteAuthoredEye) || float.IsInfinity(remoteAuthoredEye) || remoteAuthoredEye <= 0f)
        {
            return false;
        }

        float remoteRootScale = networkScale.y;
        if (float.IsNaN(remoteRootScale) || float.IsInfinity(remoteRootScale) || remoteRootScale <= 0f)
        {
            remoteRootScale = 1f;
        }

        eyeHeightMeters = remoteAuthoredEye * remoteRootScale;
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
        BasisLocalHeightCalculator.CalculatePlayerHipHeight();

        AdoptObservedBodyEvidence();

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

    public static void TickObservedEvidence(float deltaTime)
    {
        if (BasisLocalPlayer.Instance == null
            || BasisDeviceManagement.IsUserInDesktop()
            || BasisSettingsDefaults.ContinuousBodyMeasurement.RawValue == false)
        {
            return;
        }

        s_evidenceReapplyTimer += deltaTime;
        if (s_evidenceReapplyTimer >= EvidenceReapplyIntervalSeconds)
        {
            s_evidenceReapplyTimer = 0f;
            StageObservedBodySize();
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

    public static void SolveUniformScale()
    {
        AppliedUniformScale = 0f;
        LastScaleFit = BasisScaleFitResult.Invalid;

        if (ResolveHeightMode(SMModuleCalibration.HeightMode) != BasisSelectedHeightMode.BestFit)
        {
            return;
        }
        if (TryGetArmToHeightBlend(out _))
        {
            return; // the manual arm-to-height ratio is an explicit override of the fit
        }

        bool fitEnabled = BasisSettingsDefaults.FBIKBodyFit.RawValue;
        // With the stretcher off nothing can absorb a residual, so every limb becomes a hard pin and
        // the solver has to satisfy them with the scale alone.
        float deviation = fitEnabled
            ? Mathf.Clamp(BasisSettingsDefaults.FBIKBodyFitMaxDeviation.RawValue, 0f, BasisBodyFitCore.MaxDeviationCeiling)
            : 0f;

        var measurements = new BasisBodyFitMeasurements
        {
            AvatarArmSpan = AvatarArmSpan,
            AvatarShoulderWidth = AvatarShoulderWidth,
            AvatarLegSpan = AvatarLegSpan,
            AvatarSpineSpan = AvatarSpineSpan,
        };

        // Arm span only earns a say once it is a real measurement of THIS player; the fallback value is
        // just the default height wearing a different name, and letting it steer the scale would size
        // every player as though their reach equalled their height.
        bool spanUnderMeasured = HasGenuinePlayerEyeHeight && BasisCalibrationMath.ArmSpanLooksUnderMeasured(PlayerEyeHeight, PlayerArmSpan);
        float armWeight = HasGenuinePlayerArmSpan && !spanUnderMeasured
            ? BasisScaleFitCore.ArmSpanWeight * Mathf.Lerp(0.5f, 1f, Mathf.Clamp01(ObservedArmSpanConfidence))
            : 0f;

        var input = new BasisScaleFitInput
        {
            MaxEyeDeviation = BasisScaleFitCore.DefaultMaxEyeDeviation,
            Eye = new BasisScaleFitSample
            {
                // The denominator DeviceScale divides by: the player's TRUE standing eye, device->eye
                // correction included, so the fit and DeviceScale describe the same quantity.
                Player = SanitizePositive(PlayerEyeHeight, FallbackHeightInMeters) + PlayerCenterEyeVerticalOffset,
                Avatar = SanitizePositive(AvatarEyeHeight, FallbackHeightInMeters),
                Weight = BasisScaleFitCore.EyeWeight,
            },
            ArmSpan = new BasisScaleFitSample
            {
                Player = PlayerArmSpan,
                Avatar = AvatarArmSpan,
                Slack = BasisBodyFitCore.ArmSpanSlack(in measurements, deviation),
                Weight = armWeight,
            },
            HipHeight = new BasisScaleFitSample
            {
                Player = PlayerHipHeight,
                Avatar = AvatarHipHeight,
                Slack = BasisBodyFitCore.HipHeightSlack(in measurements, deviation),
                Weight = BasisScaleFitCore.HipWeight,
            },
        };

        BasisScaleFitResult fit = BasisScaleFitCore.Solve(in input);
        if (!fit.IsValid)
        {
            // Nothing measurable: fall through to the eye-height behaviour rather than inventing a scale.
            return;
        }

        AppliedUniformScale = fit.Scale;
        LastScaleFit = fit;

        if (fit.Status != s_lastScaleFitStatus)
        {
            s_lastScaleFitStatus = fit.Status;
            BasisDebug.Log(
                $"Scale fit {fit.Status} from {fit.UsedCount} measurement(s): scale {fit.Scale:F4} | " +
                $"leftover for the stretcher — eye {fit.EyeResidual:F3} arm {fit.ArmResidual:F3} hip {fit.HipResidual:F3}",
                BasisDebug.LogTag.Avatar);
        }
    }

    private static BasisScaleFitStatus s_lastScaleFitStatus = BasisScaleFitStatus.NoData;

    public static bool TryGetArmToHeightBlend(out float blend)
    {
        blend = Mathf.Clamp(Basis.BasisUI.BasisSettingsDefaults.ArmToHeightBlend.RawValue,
            BasisCalibrationMath.ArmToHeightBlendMin, BasisCalibrationMath.ArmToHeightBlendMax);
        return Basis.BasisUI.BasisSettingsDefaults.EnableArmToHeightBlend.RawValue
            && BasisDeviceManagement.IsUserInDesktop() == false;
    }

    public static void RevaluateUnscaledHeight(BasisSelectedHeightMode Height)
    {
        if (TryGetArmToHeightBlend(out float armToHeightBlend))
        {
            SelectedUnScaledAvatarHeight = SanitizePositive(BasisCalibrationMath.BlendEyeSpanMetric(AvatarEyeHeight, EffectiveAvatarArmSpan, armToHeightBlend), FallbackHeightInMeters);
            SelectedUnScaledPlayerHeight = SanitizePositive(BasisCalibrationMath.BlendEyeSpanMetric(PlayerEyeHeight, PlayerArmSpan, armToHeightBlend), FallbackHeightInMeters);
            return;
        }
        Height = ResolveHeightMode(Height);
        switch (Height)
        {
            case BasisSelectedHeightMode.ArmSpan:
                SelectedUnScaledAvatarHeight = SanitizePositive(EffectiveAvatarArmSpan, FallbackHeightInMeters);
                SelectedUnScaledPlayerHeight = SanitizePositive(PlayerArmSpan, FallbackHeightInMeters);
                break;

            case BasisSelectedHeightMode.EyeHeight:
                SelectedUnScaledAvatarHeight = SanitizePositive(AvatarEyeHeight, FallbackHeightInMeters);
                SelectedUnScaledPlayerHeight = SanitizePositive(PlayerEyeHeight, FallbackHeightInMeters);
                break;

            case BasisSelectedHeightMode.BestFit:
                BestFitMetrics(out float bestAvatar, out float bestPlayer);
                SelectedUnScaledAvatarHeight = bestAvatar;
                SelectedUnScaledPlayerHeight = bestPlayer;
                break;
        }
    }

    private static void BestFitMetrics(out float avatarMetric, out float playerMetric)
    {
        playerMetric = SanitizePositive(PlayerEyeHeight, FallbackHeightInMeters);
        float scale = AppliedUniformScale;
        if (scale <= 0f || float.IsNaN(scale) || float.IsInfinity(scale))
        {
            avatarMetric = SanitizePositive(AvatarEyeHeight, FallbackHeightInMeters);
            return;
        }
        avatarMetric = SanitizePositive(scale * (playerMetric + PlayerCenterEyeVerticalOffset), FallbackHeightInMeters);
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

        Vector3 calibrationScale = avatarDriver.ScaleAvatarModification.DuringCalibrationScale;

        // sanitize calibration scale to prevent divide-by-zero / negative surprises.
        float calY = SanitizePositive(calibrationScale.y, 1f);
        calibrationScale.y = calY;

        // Current applied avatar scale (1 = unscaled).
        AppliedUpScale = SanitizePositive(avatarDriver.ScaleAvatarModification.ApplyScale, 1f);

        // eyeScaleOffset lifts the measured HMD height up to the player's TRUE standing eye height before
        // DeviceScale divides by it. It carries the backend's device-origin->eye correction
        // (PlayerCenterEyeVerticalOffset; non-zero on OpenVR, 0 when the tracked point is already the eye).
        // The arm-to-height blend weights it by its eye-height share, so blend 0 carries the full
        // correction (pure eye mode) and blend 1 carries none (pure arm mode). The weight is capped at 1
        // for negative blends: the metric extrapolates past eye height, but the physical device->eye gap
        // does not grow with it.
        bool blendHeights = TryGetArmToHeightBlend(out float armToHeightBlend);
        float fullEyeOffset = PlayerCenterEyeVerticalOffset;
        // BestFit carries the full correction: it always measures against the eye denominator, whatever
        // the limbs pulled the scale toward.
        float eyeScaleOffset = blendHeights
            ? fullEyeOffset * Mathf.Clamp01(1f - armToHeightBlend)
            : (Height == BasisSelectedHeightMode.EyeHeight || Height == BasisSelectedHeightMode.BestFit ? fullEyeOffset : 0f);

        // AppliedUpScale multiplies BOTH player and avatar metrics.
        if (blendHeights)
        {
            float avatarBlendMetric = BasisCalibrationMath.BlendEyeSpanMetric(AvatarEyeHeight, EffectiveAvatarArmSpan, armToHeightBlend);
            float playerBlendMetric = BasisCalibrationMath.BlendEyeSpanMetric(PlayerEyeHeight, PlayerArmSpan, armToHeightBlend);

            SelectedScaledPlayerHeight = calY * ((eyeScaleOffset + playerBlendMetric) * AppliedUpScale);
            SelectedScaledAvatarHeight = calY * (avatarBlendMetric * AppliedUpScale);

            SelectedUnScaledAvatarHeight = SanitizePositive(avatarBlendMetric, FallbackHeightInMeters);
            SelectedUnScaledPlayerHeight = SanitizePositive(playerBlendMetric, FallbackHeightInMeters);
        }
        else switch (Height)
        {
            case BasisSelectedHeightMode.ArmSpan:
                float fittedArmSpan = EffectiveAvatarArmSpan;
                SelectedScaledPlayerHeight = calY * (PlayerArmSpan * AppliedUpScale);
                SelectedScaledAvatarHeight = calY * (fittedArmSpan * AppliedUpScale);

                SelectedUnScaledAvatarHeight = SanitizePositive(fittedArmSpan, FallbackHeightInMeters);
                SelectedUnScaledPlayerHeight = SanitizePositive(PlayerArmSpan, FallbackHeightInMeters);
                break;

            case BasisSelectedHeightMode.EyeHeight:
                SelectedScaledPlayerHeight = calY * ((eyeScaleOffset + PlayerEyeHeight) * AppliedUpScale);
                SelectedScaledAvatarHeight = calY * (AvatarEyeHeight * AppliedUpScale);

                SelectedUnScaledAvatarHeight = SanitizePositive(AvatarEyeHeight, FallbackHeightInMeters);
                SelectedUnScaledPlayerHeight = SanitizePositive(PlayerEyeHeight, FallbackHeightInMeters);
                break;

            case BasisSelectedHeightMode.BestFit:
                BestFitMetrics(out float bestAvatar, out float bestPlayer);
                SelectedScaledPlayerHeight = calY * ((eyeScaleOffset + bestPlayer) * AppliedUpScale);
                SelectedScaledAvatarHeight = calY * (bestAvatar * AppliedUpScale);

                SelectedUnScaledAvatarHeight = bestAvatar;
                SelectedUnScaledPlayerHeight = bestPlayer;
                break;
        }

        // stronger guards (NaN/Inf too), not only <=0.
        SelectedScaledPlayerHeight = SanitizePositive(SelectedScaledPlayerHeight, 1.6f);
        SelectedScaledAvatarHeight = SanitizePositive(SelectedScaledAvatarHeight, 1.6f);

        // "Default" denominator in the same space as SelectedScaled* (which currently includes calY)
        float defaultScaled = SanitizePositive(FallbackHeightInMeters * calY, FallbackHeightInMeters);

        PlayerToDefaultRatioScaled = SafeDivide(SelectedScaledPlayerHeight, FallbackHeightInMeters, 1f);
        AvatarToDefaultRatioScaled = SafeDivide(SelectedScaledAvatarHeight, FallbackHeightInMeters, 1f);
        // Use SafeDivide for all ratios (prevents NaN/Inf/0 denom explosions)
        PlayerToDefaultRatioScaledWithAvatarScale = SafeDivide(SelectedScaledPlayerHeight, defaultScaled, 1f);
        AvatarToDefaultRatioScaledWithAvatarScale = SafeDivide(SelectedScaledAvatarHeight, defaultScaled, 1f);

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
        // playerMetric is the player's TRUE standing eye height the avatar is scaled against. Assembled
        // by the shared pure helper so this runtime path and BasisCalibrationMathSweep exercise the same
        // formula. eyeScaleOffset already carries the device-origin->eye correction (OpenVR) plus the
        // gated standing-eye-height correction applied above.
        float playerMetric = SanitizePositive(BasisCalibrationMath.StandingEyeDenominator(SelectedUnScaledPlayerHeight, eyeScaleOffset, 0f), 1f);

        DeviceScale = SafeDivide(avatarScaledMetric, playerMetric, 1f);
        DeviceScale = SanitizePositive(DeviceScale, 1f);

        // The grounding lift's eye term includes the blended eyeScaleOffset so at blend 0 the lift is
        // exactly 0 (eye mode already grounds the feet through the denominator -- never double-count it)
        // and at blend 1 it degenerates to the pure arm-span lift (eyeScaleOffset is 0 there). Any active
        // blend is eligible: either sign can sink the feet, depending on which side of eye height the
        // player's arm measurement lies.
        // BestFit needs it too, but only when the limbs actually pulled the scale off the eye match: at
        // EyeExact the lift computes to exactly zero on its own, so this costs nothing in the common case.
        bool needsGrounding = blendHeights
            || Height == BasisSelectedHeightMode.ArmSpan
            || Height == BasisSelectedHeightMode.BestFit;
        HeightModeGroundingOffset = (needsGrounding && !SMModuleSitStand.IsSteatedMode)
            ? BasisCalibrationMath.ArmSpanFloorGroundingLift(
                SanitizePositive(AvatarEyeHeight, FallbackHeightInMeters),
                AppliedUpScale,
                DeviceScale,
                SanitizePositive(PlayerEyeHeight, FallbackHeightInMeters) + eyeScaleOffset)
            : 0f;

        BasisDebug.Log(
            $"Height Mode: {(blendHeights ? $"ArmToHeightBlend {armToHeightBlend:P0}" : Height.ToString())} | PlayerMetric(scaled): {SelectedScaledPlayerHeight}m | " +
            $"AvatarMetric(scaled): {SelectedScaledAvatarHeight}m | " +
            $"PlayerToAvatar: {PlayerToAvatarRatioScaled} | AvatarToPlayer: {AvatarToPlayerRatioScaled} | " +
            $"PlayerToDefault: {PlayerToDefaultRatioScaledWithAvatarScale} | AvatarToDefault: {AvatarToDefaultRatioScaledWithAvatarScale} | " +
            $"DeviceScale: {DeviceScale}",
            BasisDebug.LogTag.Avatar
        );
        // Denominator breakdown so a systematic eye-height bias stays readable in-headset: compare the
        // true-eye estimate against your tape-measured standing eye height.
        BasisDebug.Log(
            $"Eye-height denominator (true standing eye estimate): {playerMetric:F3}m = raw {SelectedUnScaledPlayerHeight:F3} " +
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
