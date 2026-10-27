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

    // Always estimated from height (see EstimatedPlayerArmSpan), never measured.
    public static float PlayerArmSpan = FallbackHeightInMeters;
    public static float AvatarArmSpan = FallbackHeightInMeters;

    public static float ObservedEyeConfidence = 0f;

    public enum BasisBodyMeasurementSource
    {
        Fallback,
        Stated,
        Saved,
        Measured,
        SlimeVR,
        Estimated,
    }

    /// <summary>
    /// The player's arm span, estimated from their eye height and the arm span ratio setting. It is
    /// never measured: a T-pose reading depends on straight, level arms that nothing can verify
    /// without elbow trackers, and any slack reads as short arms. A stated height wins over the
    /// measured eye height, since it is not thrown off by a misplaced VR floor.
    /// </summary>
    public static float EstimatedPlayerArmSpan()
    {
        float eye = BasisStatedHeight.IsSet ? BasisStatedHeight.ImpliedEyeHeight : SanitizePositive(PlayerEyeHeight, FallbackHeightInMeters);
        return eye * Mathf.Max(0.1f, BasisSettingsDefaults.FBIKArmHeightRatio.RawValue);
    }

    private static void ApplyEstimatedArmSpan()
    {
        PlayerArmSpan = EstimatedPlayerArmSpan();
    }

    /// <summary>Called when the arm span ratio setting changes.</summary>
    public static void OnArmSpanRatioChanged()
    {
        if (BasisLocalPlayer.Instance != null && BasisLocalPlayer.PlayerReady)
        {
            ApplyScaleAndHeight();
        }
    }

    public static BasisBodyMeasurementSource EyeHeightSource = BasisBodyMeasurementSource.Fallback;

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
    // The arm span is derived from the eye height, so it cannot testify against it: only a stated
    // height can stand in for a lift-poisoned eye.
    public static float SizingEyeHeight => BasisCalibrationMath.SizingEyeHeight(PlayerEyeHeight, PlayerArmSpan, spanGenuine: false, BasisStatedHeight.ImpliedEyeHeight);
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
        // The estimated span is re-derived first so it follows every eye height change (refits,
        // SlimeVR, the auto-scale estimator), not only the ones that go through CapturePlayerHeight.
        ApplyEstimatedArmSpan();
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
        // Re-measuring here reads the headset at whatever pose the player binds in, and any change to the
        // eye height moves the camera by that delta x DeviceScale (the grounding offset re-centres on the
        // new eye). With the option off, keep the current size; CapturePlayerHeight(false) still measures
        // anything that has no genuine value yet, so a first calibration is never left on the fallback.
        bool remeasure = BasisSettingsDefaults.RemeasureHeightOnCalibration.RawValue;
        if (remeasure)
        {
            // An explicit recalibration is the player saying the current fit is wrong, so the observations
            // that produced it have to go with it — otherwise the high-water estimate would simply be
            // re-adopted a moment later and recalibrating could never change anything. This is the escape
            // hatch for a session poisoned by a bad tracking episode or a different person in the headset.
            BasisBodyEvidenceSampler.ResetEvidence();
        }
        CapturePlayerHeight(recaptureEyeHeight: remeasure);
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

        // An eye taller than any standing player was measured while the play space was shifted up (space
        // drag / grounding lift / external offset). Persisting it poisons every subsequent boot's scale, so
        // it never saves.
        bool eyeLooksLiftPoisoned = BasisCalibrationMath.EyeHeightLooksLiftPoisoned(PlayerEyeHeight);

        // A stated height is the strongest veto available: it bounds the answer directly, so a reading
        // that contradicts it never reaches disk. Only the eye is saved; the arm span follows from it.
        if (eyePlausible && eyeLooksLiftPoisoned == false && BasisStatedHeight.IsPlausibleEye(PlayerEyeHeight))
        {
            Basis.BasisUI.BasisSettingsDefaults.SavedPlayerEyeHeight.SetValue(PlayerEyeHeight);
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
        // player was measured while the play space was shifted. Only a stated height can stand in for it.
        if (BasisCalibrationMath.EyeHeightLooksLiftPoisoned(savedEye))
        {
            if (!BasisStatedHeight.IsSet)
            {
                BasisDebug.LogWarning($"Saved eye height {savedEye:F3}m is taller than any standing player (lift-poisoned calibration); ignoring the saved body size. Recalibrate to re-measure.", BasisDebug.LogTag.Avatar);
                return;
            }
            float healedEye = BasisStatedHeight.ImpliedEyeHeight;
            BasisDebug.LogWarning($"Saved eye height {savedEye:F3}m is taller than any standing player (lift-poisoned calibration); using your stated eye {healedEye:F3}m instead. Recalibrate to re-measure.", BasisDebug.LogTag.Avatar);
            savedEye = healedEye;
        }
        PlayerEyeHeight = savedEye;
        HasGenuinePlayerEyeHeight = true;
        EyeHeightSource = BasisBodyMeasurementSource.Saved;
        // The saved size originated from an explicit calibration, so the pre-calibration ballpark
        // auto-scale estimator must not fight it.
        HasUserCalibratedHeight = true;
        BasisDebug.Log($"Seeded last session's calibrated eye height: {PlayerEyeHeight:F3}m", BasisDebug.LogTag.Avatar);
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
        if (BasisCalibrationMath.ShouldRecaptureEyeHeight(recaptureEyeHeight, HasGenuinePlayerHipHeight))
        {
            BasisLocalHeightCalculator.CalculatePlayerHipHeight();
        }

        if (ObservingBodyEvidence)
        {
            AdoptObservedBodyEvidence();
        }

        // Optional safety: sanitize captured values in case calculator produced junk.
        PlayerEyeHeight = SanitizePositive(PlayerEyeHeight, FallbackHeightInMeters);
        ApplyEstimatedArmSpan();
        SMModuleSitStand.RefreshSeatedLift();
    }

    public struct ObservedBodySize
    {
        public float EyeHeight;
        public bool EyeIsGenuine;
    }

    // Only the eye height is observed; the arm span follows from it in ApplyScaleAndHeight.
    private static bool TryGetObservedBodySize(out ObservedBodySize result)
    {
        result = new ObservedBodySize
        {
            EyeHeight = PlayerEyeHeight,
            EyeIsGenuine = HasGenuinePlayerEyeHeight,
        };

        ObservedEyeConfidence = 0f;
        // Seated mode substitutes a virtual standing eye; observing the chair would overwrite it.
        if (BasisDeviceManagement.IsUserInDesktop() || SMModuleSitStand.IsSteatedMode)
        {
            return false;
        }

        if (BasisBodyEvidenceSampler.TryGetEyeHeight(out float observedEye, out float eyeConfidence))
        {
            ObservedEyeConfidence = eyeConfidence;
            // The one way an eye reading CAN come out too long is a vertical shift of the play space; the
            // absolute ceiling and a stated height catch that.
            bool poisoned = BasisCalibrationMath.EyeHeightLooksLiftPoisoned(observedEye);
            if (observedEye > result.EyeHeight && Plausible(observedEye)
                && poisoned == false && BasisStatedHeight.IsPlausibleEye(observedEye))
            {
                result.EyeHeight = observedEye;
                result.EyeIsGenuine = true;
                return true;
            }
        }

        return false;
    }

    private static void AdoptObservedBodyEvidence()
    {
        if (!TryGetObservedBodySize(out ObservedBodySize observed))
        {
            return;
        }
        BasisDebug.Log($"Adopting observed eye height: {PlayerEyeHeight:F3}->{observed.EyeHeight:F3}m", BasisDebug.LogTag.Avatar);
        if (!Mathf.Approximately(PlayerEyeHeight, observed.EyeHeight)) EyeHeightSource = BasisBodyMeasurementSource.Measured;
        PlayerEyeHeight = observed.EyeHeight;
        HasGenuinePlayerEyeHeight = observed.EyeIsGenuine;
    }

    private static bool Plausible(float measure) =>
        measure >= MinPlausibleBodyMeasure && measure <= MaxPlausibleBodyMeasure;

    public const float EvidenceReapplyThresholdMeters = 0.02f;
    private const float EvidenceReapplyIntervalSeconds = 1f;
    private static float s_evidenceReapplyTimer;

    private static float s_targetEye;
    private static bool s_targetEyeGenuine;
    private static bool s_hasRefitTarget;
    private static float s_refitFromEye;
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
        PlayerEyeHeight = s_targetEye;
        HasGenuinePlayerEyeHeight |= s_targetEyeGenuine;
        ApplyScaleAndHeight();
        AnnounceRefit();
    }

    private static void StageObservedBodySize()
    {
        if (!TryGetObservedBodySize(out ObservedBodySize observed))
        {
            return;
        }

        if (Mathf.Abs(observed.EyeHeight - PlayerEyeHeight) <= EvidenceReapplyThresholdMeters)
        {
            return; // real but not worth moving the world for
        }

        if (!s_hasRefitTarget)
        {
            s_refitFromEye = PlayerEyeHeight;
        }
        s_targetEye = observed.EyeHeight;
        s_targetEyeGenuine = observed.EyeIsGenuine;
        s_hasRefitTarget = true;

        BasisDebug.Log(
            $"Observed eye height improved ({s_refitFromEye:F3}->{s_targetEye:F3}m); refitting at the next safe moment.",
            BasisDebug.LogTag.Avatar);
    }

    private static void AnnounceRefit()
    {
        string detail = string.Format(BasisLocalization.Get("calibration.refit.height"), PlayerEyeHeight);

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
        bool blend = TryGetArmToHeightBlend(out float armToHeightBlend);
        float k = BasisLocalPlayer.Instance?.LocalAvatarDriver?.ScaleAvatarModification != null ? BasisLocalPlayer.Instance.LocalAvatarDriver.ScaleAvatarModification.ApplyScale : AppliedUpScale;
        return new BasisScaleInputs
        {
            Mode = ResolveHeightMode(SMModuleCalibration.HeightMode),
            Desktop = BasisDeviceManagement.IsUserInDesktop(),
            Seated = SMModuleSitStand.IsSteatedMode,
            Blend = blend,
            BlendAmount = armToHeightBlend,
            // The span is estimated from height, so it is always usable.
            SpanUsable = true,
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
