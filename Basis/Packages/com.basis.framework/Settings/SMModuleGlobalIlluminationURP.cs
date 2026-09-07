// Global illumination is optional: the define comes from the com.basis.globalillumination package
// being present (asmdef versionDefines), and the effect is not viable on mobile GPUs, so the whole
// integration compiles out on Android.
#if BASIS_HAS_GI && !UNITY_ANDROID
using System;
using System.Collections.Generic;
using Basis.BasisUI;
using Basis.Scripts.Drivers;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Everything the player controls about global illumination, in one value so it can be handed to a
/// volume, compared, or built from the settings bindings without a sixteen argument call.
/// </summary>
public struct BasisGlobalIlluminationState
{
    public bool Enabled;
    public BasisGlobalIlluminationMode Mode;
    public BasisGlobalIlluminationRaySkinnedMode SkinnedMeshes;
    /// <summary>
    /// Which layers the ray traced path walks, kept as the dropdown option rather than a resolved mask.
    /// Nothing on the screen space path reads it.
    ///
    /// A mask here would have to be resolved by layer name, and this struct is built from a field
    /// initializer on SMModuleGlobalIlluminationURP - where Unity forbids NameToLayer outright ("not
    /// allowed to be called from a MonoBehaviour constructor"), and throwing there aborts the module's
    /// construction. The option is resolved in Apply instead, which runs well after Awake.
    /// </summary>
    public string Layers;
    public BasisGlobalIlluminationQuality Quality;
    public BasisGlobalIlluminationResolution Resolution;
    public BasisGlobalIlluminationFallback Fallback;
    /// <summary>Ray traced only - screen space always reads whatever is already on screen. See BasisGlobalIlluminationSettings.respectBakedEmission (inverted: this is the player-facing "ignore" framing).</summary>
    public bool IgnoreBakedEmission;
    /// <summary>How much of the effect a lightmapped surface still receives - its own bounce is already baked. See BasisGlobalIlluminationSettings.lightmappedReceive.</summary>
    public float LightmappedReceive;
    public float Intensity;
    public float Saturation;
    public float Obscurance;
    public float RayLength;
    public float Smoothing;
    public float TemporalResponse;
    public bool TemporalFilter;
    public bool WideBlur;
    public bool RayReuse;
    public bool Emitters;
    public float EmitterIntensity;
    public bool ReflectionProbes;
    public bool Mirrors;
    public bool Specular;
    public float SpecularIntensity;
    public float SpecularMaxRoughness;
    public float SpecularRayLength;
    public float SpecularFadeDistance;
    // The tracing internals. Constants until an artifact needed explaining; see BasisSettingsDefaults.
    public float ObscuranceRadius;
    public float FadeDistance;
    public float NormalBias;
    public float DistanceBias;
    public float BounceThreshold;
    public float FireflyClamp;
    public bool Capture;

    public static BasisGlobalIlluminationState FromDefaults()
    {
        return new BasisGlobalIlluminationState
        {
            Enabled = BasisSettingsDefaults.UseGlobalIllumination.DefaultValue.GetDefault(),
            Mode = SMModuleGlobalIlluminationURP.ReadMode(BasisSettingsDefaults.GlobalIlluminationMode.DefaultValue.GetDefault()),
            SkinnedMeshes = SMModuleGlobalIlluminationURP.ReadSkinnedMode(BasisSettingsDefaults.GlobalIlluminationSkinnedMeshes.DefaultValue.GetDefault()),
            Layers = BasisSettingsDefaults.GlobalIlluminationLayers.DefaultValue.GetDefault(),
            Quality = SMModuleGlobalIlluminationURP.ReadQuality(BasisSettingsDefaults.GlobalIlluminationQuality.DefaultValue.GetDefault()),
            Resolution = SMModuleGlobalIlluminationURP.ReadResolution(BasisSettingsDefaults.GlobalIlluminationResolution.DefaultValue.GetDefault()),
            Fallback = SMModuleGlobalIlluminationURP.ReadFallback(BasisSettingsDefaults.GlobalIlluminationFallback.DefaultValue.GetDefault()),
            IgnoreBakedEmission = BasisSettingsDefaults.GlobalIlluminationIgnoreBakedEmission.DefaultValue.GetDefault(),
            LightmappedReceive = BasisSettingsDefaults.GlobalIlluminationLightmappedReceive.DefaultValue.GetDefault(),
            Intensity = BasisSettingsDefaults.GlobalIlluminationIntensity.DefaultValue.GetDefault(),
            Saturation = BasisSettingsDefaults.GlobalIlluminationSaturation.DefaultValue.GetDefault(),
            Obscurance = BasisSettingsDefaults.GlobalIlluminationObscurance.DefaultValue.GetDefault(),
            RayLength = BasisSettingsDefaults.GlobalIlluminationRayLength.DefaultValue.GetDefault(),
            Smoothing = BasisSettingsDefaults.GlobalIlluminationSmoothing.DefaultValue.GetDefault(),
            TemporalResponse = BasisSettingsDefaults.GlobalIlluminationTemporalResponse.DefaultValue.GetDefault(),
            TemporalFilter = BasisSettingsDefaults.GlobalIlluminationTemporalFilter.DefaultValue.GetDefault(),
            WideBlur = BasisSettingsDefaults.GlobalIlluminationWideBlur.DefaultValue.GetDefault(),
            RayReuse = BasisSettingsDefaults.GlobalIlluminationRayReuse.DefaultValue.GetDefault(),
            Emitters = BasisSettingsDefaults.GlobalIlluminationEmitters.DefaultValue.GetDefault(),
            EmitterIntensity = BasisSettingsDefaults.GlobalIlluminationEmitterIntensity.DefaultValue.GetDefault(),
            ReflectionProbes = BasisSettingsDefaults.GlobalIlluminationReflectionProbes.DefaultValue.GetDefault(),
            Mirrors = BasisSettingsDefaults.GlobalIlluminationMirrors.DefaultValue.GetDefault(),
            Specular = BasisSettingsDefaults.GlobalIlluminationSpecular.DefaultValue.GetDefault(),
            SpecularIntensity = BasisSettingsDefaults.GlobalIlluminationSpecularIntensity.DefaultValue.GetDefault(),
            SpecularMaxRoughness = BasisSettingsDefaults.GlobalIlluminationSpecularMaxRoughness.DefaultValue.GetDefault(),
            SpecularRayLength = BasisSettingsDefaults.GlobalIlluminationSpecularRayLength.DefaultValue.GetDefault(),
            SpecularFadeDistance = BasisSettingsDefaults.GlobalIlluminationSpecularFadeDistance.DefaultValue.GetDefault(),
            ObscuranceRadius = BasisSettingsDefaults.GlobalIlluminationObscuranceRadius.DefaultValue.GetDefault(),
            FadeDistance = BasisSettingsDefaults.GlobalIlluminationFadeDistance.DefaultValue.GetDefault(),
            NormalBias = BasisSettingsDefaults.GlobalIlluminationNormalBias.DefaultValue.GetDefault(),
            DistanceBias = BasisSettingsDefaults.GlobalIlluminationDistanceBias.DefaultValue.GetDefault(),
            BounceThreshold = BasisSettingsDefaults.GlobalIlluminationBounceThreshold.DefaultValue.GetDefault(),
            FireflyClamp = BasisSettingsDefaults.GlobalIlluminationFireflyClamp.DefaultValue.GetDefault(),
            Capture = false
        };
    }

    public static BasisGlobalIlluminationState FromSettings()
    {
        return new BasisGlobalIlluminationState
        {
            Enabled = BasisSettingsDefaults.UseGlobalIllumination.RawValue,
            Mode = SMModuleGlobalIlluminationURP.ReadMode(BasisSettingsDefaults.GlobalIlluminationMode.RawValue),
            SkinnedMeshes = SMModuleGlobalIlluminationURP.ReadSkinnedMode(BasisSettingsDefaults.GlobalIlluminationSkinnedMeshes.RawValue),
            Layers = BasisSettingsDefaults.GlobalIlluminationLayers.RawValue,
            Quality = SMModuleGlobalIlluminationURP.ReadQuality(BasisSettingsDefaults.GlobalIlluminationQuality.RawValue),
            Resolution = SMModuleGlobalIlluminationURP.ReadResolution(BasisSettingsDefaults.GlobalIlluminationResolution.RawValue),
            Fallback = SMModuleGlobalIlluminationURP.ReadFallback(BasisSettingsDefaults.GlobalIlluminationFallback.RawValue),
            IgnoreBakedEmission = BasisSettingsDefaults.GlobalIlluminationIgnoreBakedEmission.RawValue,
            LightmappedReceive = BasisSettingsDefaults.GlobalIlluminationLightmappedReceive.RawValue,
            Intensity = BasisSettingsDefaults.GlobalIlluminationIntensity.RawValue,
            Saturation = BasisSettingsDefaults.GlobalIlluminationSaturation.RawValue,
            Obscurance = BasisSettingsDefaults.GlobalIlluminationObscurance.RawValue,
            RayLength = BasisSettingsDefaults.GlobalIlluminationRayLength.RawValue,
            Smoothing = BasisSettingsDefaults.GlobalIlluminationSmoothing.RawValue,
            TemporalResponse = BasisSettingsDefaults.GlobalIlluminationTemporalResponse.RawValue,
            TemporalFilter = BasisSettingsDefaults.GlobalIlluminationTemporalFilter.RawValue,
            WideBlur = BasisSettingsDefaults.GlobalIlluminationWideBlur.RawValue,
            RayReuse = BasisSettingsDefaults.GlobalIlluminationRayReuse.RawValue,
            Emitters = BasisSettingsDefaults.GlobalIlluminationEmitters.RawValue,
            EmitterIntensity = BasisSettingsDefaults.GlobalIlluminationEmitterIntensity.RawValue,
            ReflectionProbes = BasisSettingsDefaults.GlobalIlluminationReflectionProbes.RawValue,
            Mirrors = BasisSettingsDefaults.GlobalIlluminationMirrors.RawValue,
            Specular = BasisSettingsDefaults.GlobalIlluminationSpecular.RawValue,
            SpecularIntensity = BasisSettingsDefaults.GlobalIlluminationSpecularIntensity.RawValue,
            SpecularMaxRoughness = BasisSettingsDefaults.GlobalIlluminationSpecularMaxRoughness.RawValue,
            SpecularRayLength = BasisSettingsDefaults.GlobalIlluminationSpecularRayLength.RawValue,
            SpecularFadeDistance = BasisSettingsDefaults.GlobalIlluminationSpecularFadeDistance.RawValue,
            ObscuranceRadius = BasisSettingsDefaults.GlobalIlluminationObscuranceRadius.RawValue,
            FadeDistance = BasisSettingsDefaults.GlobalIlluminationFadeDistance.RawValue,
            NormalBias = BasisSettingsDefaults.GlobalIlluminationNormalBias.RawValue,
            DistanceBias = BasisSettingsDefaults.GlobalIlluminationDistanceBias.RawValue,
            BounceThreshold = BasisSettingsDefaults.GlobalIlluminationBounceThreshold.RawValue,
            FireflyClamp = BasisSettingsDefaults.GlobalIlluminationFireflyClamp.RawValue,
            Capture = false
        };
    }
}

/// <summary>
/// A per-photo substitute for the "look" half of <see cref="BasisGlobalIlluminationState"/> — the
/// fields a camera can reasonably want different from the player's live settings for one shot.
/// Deliberately excludes Enabled/Capture (not per-camera concerns), Resolution (already forced
/// Full during capture — see <see cref="SMModuleGlobalIlluminationURP.BeginCapture"/>), and
/// TemporalFilter/TemporalResponse (temporal accumulation is already forced off during capture,
/// so a value here would be inert).
///
/// Mode, SkinnedMeshes, Layers, Quality and Fallback are stored as the canonical option strings
/// (<see cref="SMModuleGlobalIlluminationURP.ModeOptions"/> and its siblings) and resolved through
/// the same <c>Read*</c> parsers the live settings use, rather than the enum types themselves —
/// which means nothing that only ever assigns into this struct needs to name a
/// BasisGlobalIllumination enum type, so com.basis.camera can keep using it without a new asmdef
/// reference (it only ever calls Camera-typed static methods on this class today).
/// </summary>
public struct BasisGlobalIlluminationCaptureOverride
{
    public string Mode;
    public string SkinnedMeshes;
    public string Layers;
    public string Quality;
    public string Fallback;
    public bool IgnoreBakedEmission;
    public float Intensity;
    public float Saturation;
    public float Obscurance;
    public float RayLength;
    public float Smoothing;
    public bool WideBlur;
    public bool RayReuse;
    public bool Emitters;
    public float EmitterIntensity;
    public bool Specular;
    public float ObscuranceRadius;
    public float FadeDistance;
    public float NormalBias;
    public float DistanceBias;
    public float BounceThreshold;
    public float FireflyClamp;
    public bool ReflectionProbes;
    public bool Mirrors;
}

public class SMModuleGlobalIlluminationURP : BasisSettingsBase
{
    public const int CaptureRayCount = 8;
    public const int CaptureRaySteps = 64;

    private static readonly HashSet<Camera> registeredCameras = new HashSet<Camera>();
    private static readonly HashSet<Camera> suspendedCameras = new HashSet<Camera>();
    private static SMModuleGlobalIlluminationURP instance;

    private BasisGlobalIlluminationState state = BasisGlobalIlluminationState.FromDefaults();

    /// <summary>Set only for the duration of a photo capture — see <see cref="BeginCapture"/>.</summary>
    private BasisGlobalIlluminationCaptureOverride? captureOverride;

    /// <summary>Canonical option strings for <see cref="BasisGlobalIlluminationCaptureOverride.Mode"/>, index-matched to the live Mode dropdown. Parsed by <see cref="ReadMode"/>.</summary>
    public static readonly string[] ModeOptions = { "Screen Space", "Ray Traced" };
    /// <summary>See <see cref="ModeOptions"/>. Parsed by <see cref="ReadSkinnedMode"/>.</summary>
    public static readonly string[] SkinnedMeshesOptions = { "Off", "Proxy" };
    /// <summary>See <see cref="ModeOptions"/>. Parsed by <see cref="ReadLayers"/>.</summary>
    public static readonly string[] LayersOptions = { "Avatars", "World", "World And Avatars" };
    /// <summary>See <see cref="ModeOptions"/>. Parsed by <see cref="ReadQuality"/>.</summary>
    public static readonly string[] QualityOptions = { "Low", "Medium", "High", "Ultra" };
    /// <summary>See <see cref="ModeOptions"/>. Parsed by <see cref="ReadFallback"/>.</summary>
    public static readonly string[] FallbackOptions = { "None", "Sky", "Reflection Probe" };

    /// <summary>What the effect is rendering with. The settings provider writes straight into this.</summary>
    public BasisGlobalIlluminationSettings GlobalIllumination => BasisGlobalIlluminationSettings.Current;
    public bool Capturing => state.Capture;
    public BasisGlobalIlluminationState State => state;

    private static string K_USE_GI => BasisSettingsDefaults.UseGlobalIllumination.BindingKey;
    private static string K_GI_MODE => BasisSettingsDefaults.GlobalIlluminationMode.BindingKey;
    private static string K_GI_SKINNED => BasisSettingsDefaults.GlobalIlluminationSkinnedMeshes.BindingKey;
    private static string K_GI_LAYERS => BasisSettingsDefaults.GlobalIlluminationLayers.BindingKey;
    private static string K_GI_OBSCURANCE_RADIUS => BasisSettingsDefaults.GlobalIlluminationObscuranceRadius.BindingKey;
    private static string K_GI_FADE_DISTANCE => BasisSettingsDefaults.GlobalIlluminationFadeDistance.BindingKey;
    private static string K_GI_NORMAL_BIAS => BasisSettingsDefaults.GlobalIlluminationNormalBias.BindingKey;
    private static string K_GI_DISTANCE_BIAS => BasisSettingsDefaults.GlobalIlluminationDistanceBias.BindingKey;
    private static string K_GI_BOUNCE_THRESHOLD => BasisSettingsDefaults.GlobalIlluminationBounceThreshold.BindingKey;
    private static string K_GI_FIREFLY_CLAMP => BasisSettingsDefaults.GlobalIlluminationFireflyClamp.BindingKey;
    private static string K_GI_QUALITY => BasisSettingsDefaults.GlobalIlluminationQuality.BindingKey;
    private static string K_GI_RESOLUTION => BasisSettingsDefaults.GlobalIlluminationResolution.BindingKey;
    private static string K_GI_FALLBACK => BasisSettingsDefaults.GlobalIlluminationFallback.BindingKey;
    private static string K_GI_IGNORE_BAKED_EMISSION => BasisSettingsDefaults.GlobalIlluminationIgnoreBakedEmission.BindingKey;
    private static string K_GI_LIGHTMAPPED_RECEIVE => BasisSettingsDefaults.GlobalIlluminationLightmappedReceive.BindingKey;
    private static string K_GI_INTENSITY => BasisSettingsDefaults.GlobalIlluminationIntensity.BindingKey;
    private static string K_GI_SATURATION => BasisSettingsDefaults.GlobalIlluminationSaturation.BindingKey;
    private static string K_GI_OBSCURANCE => BasisSettingsDefaults.GlobalIlluminationObscurance.BindingKey;
    private static string K_GI_RAY_LENGTH => BasisSettingsDefaults.GlobalIlluminationRayLength.BindingKey;
    private static string K_GI_SMOOTHING => BasisSettingsDefaults.GlobalIlluminationSmoothing.BindingKey;
    private static string K_GI_TEMPORAL_RESPONSE => BasisSettingsDefaults.GlobalIlluminationTemporalResponse.BindingKey;
    private static string K_GI_TEMPORAL_FILTER => BasisSettingsDefaults.GlobalIlluminationTemporalFilter.BindingKey;
    private static string K_GI_WIDE_BLUR => BasisSettingsDefaults.GlobalIlluminationWideBlur.BindingKey;
    private static string K_GI_RAY_REUSE => BasisSettingsDefaults.GlobalIlluminationRayReuse.BindingKey;
    private static string K_GI_EMITTERS => BasisSettingsDefaults.GlobalIlluminationEmitters.BindingKey;
    private static string K_GI_EMITTER_INTENSITY => BasisSettingsDefaults.GlobalIlluminationEmitterIntensity.BindingKey;
    private static string K_GI_REFLECTION_PROBES => BasisSettingsDefaults.GlobalIlluminationReflectionProbes.BindingKey;
    private static string K_GI_MIRRORS => BasisSettingsDefaults.GlobalIlluminationMirrors.BindingKey;
    private static string K_GI_SPECULAR => BasisSettingsDefaults.GlobalIlluminationSpecular.BindingKey;
    private static string K_GI_SPECULAR_INTENSITY => BasisSettingsDefaults.GlobalIlluminationSpecularIntensity.BindingKey;
    private static string K_GI_SPECULAR_MAX_ROUGHNESS => BasisSettingsDefaults.GlobalIlluminationSpecularMaxRoughness.BindingKey;
    private static string K_GI_SPECULAR_RAY_LENGTH => BasisSettingsDefaults.GlobalIlluminationSpecularRayLength.BindingKey;
    private static string K_GI_SPECULAR_FADE_DISTANCE => BasisSettingsDefaults.GlobalIlluminationSpecularFadeDistance.BindingKey;
    private static string K_GI_DEBUG_VIEW => BasisSettingsDefaults.DevGiDebugView.BindingKey;

    public override void Awake()
    {
        base.Awake();
        instance = this;
        BasisGlobalIlluminationFeature.CameraFilter = AcceptsCamera;
        BasisGlobalIlluminationFeature.KeepRenderingWithDebugger = true;
        ApplyOverride();
    }

    public new void OnDestroy()
    {
        base.OnDestroy();
        RestoreAuthoredFeatureValues();
        if (instance == this)
        {
            BasisGlobalIlluminationFeature.CameraFilter = null;
            instance = null;
        }
    }

    public static bool AcceptsCamera(Camera camera)
    {
        Camera localCamera = BasisLocalCameraDriver.CameraInstance;
        if (localCamera == null || ReferenceEquals(camera, localCamera))
        {
            return true;
        }
        // Mirrors are not registered by anything - they are created by whatever world the player walked
        // into, not by a Basis system that could announce them - so an allow-list of registered cameras
        // rejects every one of them. That is why a mirror has never shown a bounce. The feature itself
        // decides whether mirrors are wanted; this only stops the allow-list from being the thing that
        // silently answers the question.
        if (BasisGlobalIlluminationFeature.IsMirrorReflection(camera))
        {
            return !suspendedCameras.Contains(camera);
        }
        return registeredCameras.Contains(camera) && !suspendedCameras.Contains(camera);
    }

    public static bool IsCameraRegistered(Camera camera)
    {
        return !(camera is null) && registeredCameras.Contains(camera);
    }

    public static void RegisterCamera(Camera camera)
    {
        if (camera == null)
        {
            return;
        }
        registeredCameras.Add(camera);
        if (instance != null)
        {
            instance.ApplyOverride();
        }
    }

    public static void UnregisterCamera(Camera camera)
    {
        if (camera is null)
        {
            return;
        }
        registeredCameras.Remove(camera);
        suspendedCameras.Remove(camera);
    }

    public static void SuspendCamera(Camera camera, bool suspended)
    {
        if (camera is null)
        {
            return;
        }
        if (suspended)
        {
            suspendedCameras.Add(camera);
        }
        else
        {
            suspendedCameras.Remove(camera);
        }
    }

    /// <summary>
    /// <paramref name="photoOverride"/>, when given, substitutes its fields into the effective
    /// state for exactly the duration of this capture — see <see cref="ApplyOverride"/>, which is
    /// where it is actually applied, so every caller of that method (not just this one) sees a
    /// consistent picture for as long as the capture is open.
    /// </summary>
    public static void BeginCapture(Camera camera, BasisGlobalIlluminationCaptureOverride? photoOverride = null)
    {
        if (instance == null || instance.state.Capture || !instance.state.Enabled || !IsCameraRegistered(camera))
        {
            return;
        }
        instance.state.Capture = true;
        instance.captureOverride = photoOverride;
        instance.ApplyOverride();
    }

    public static void EndCapture()
    {
        if (instance == null || !instance.state.Capture)
        {
            return;
        }
        instance.state.Capture = false;
        instance.captureOverride = null;
        instance.ApplyOverride();
    }

    public override void ValidSettingsChange(string matchedSettingName, string optionValue)
    {
        if (matchedSettingName == K_USE_GI) { state.Enabled = optionValue == "true"; }
        else if (matchedSettingName == K_GI_MODE) { state.Mode = ReadMode(optionValue); }
        else if (matchedSettingName == K_GI_SKINNED) { state.SkinnedMeshes = ReadSkinnedMode(optionValue); }
        else if (matchedSettingName == K_GI_LAYERS) { state.Layers = optionValue; }
        else if (matchedSettingName == K_GI_OBSCURANCE_RADIUS)
        {
            if (!SliderReadOption(optionValue, out float value)) { return; }
            state.ObscuranceRadius = value;
        }
        else if (matchedSettingName == K_GI_FADE_DISTANCE)
        {
            if (!SliderReadOption(optionValue, out float value)) { return; }
            state.FadeDistance = value;
        }
        else if (matchedSettingName == K_GI_NORMAL_BIAS)
        {
            if (!SliderReadOption(optionValue, out float value)) { return; }
            state.NormalBias = value;
        }
        else if (matchedSettingName == K_GI_DISTANCE_BIAS)
        {
            if (!SliderReadOption(optionValue, out float value)) { return; }
            state.DistanceBias = value;
        }
        else if (matchedSettingName == K_GI_BOUNCE_THRESHOLD)
        {
            if (!SliderReadOption(optionValue, out float value)) { return; }
            state.BounceThreshold = value;
        }
        else if (matchedSettingName == K_GI_FIREFLY_CLAMP)
        {
            if (!SliderReadOption(optionValue, out float value)) { return; }
            state.FireflyClamp = value;
        }
        else if (matchedSettingName == K_GI_QUALITY) { state.Quality = ReadQuality(optionValue); }
        else if (matchedSettingName == K_GI_RESOLUTION) { state.Resolution = ReadResolution(optionValue); }
        else if (matchedSettingName == K_GI_FALLBACK) { state.Fallback = ReadFallback(optionValue); }
        else if (matchedSettingName == K_GI_IGNORE_BAKED_EMISSION) { state.IgnoreBakedEmission = optionValue == "true"; }
        else if (matchedSettingName == K_GI_LIGHTMAPPED_RECEIVE)
        {
            if (!SliderReadOption(optionValue, out float value)) { return; }
            state.LightmappedReceive = value;
        }
        else if (matchedSettingName == K_GI_TEMPORAL_FILTER) { state.TemporalFilter = optionValue == "true"; }
        else if (matchedSettingName == K_GI_WIDE_BLUR) { state.WideBlur = optionValue == "true"; }
        else if (matchedSettingName == K_GI_RAY_REUSE) { state.RayReuse = optionValue == "true"; }
        else if (matchedSettingName == K_GI_EMITTERS) { state.Emitters = optionValue == "true"; }
        else if (matchedSettingName == K_GI_REFLECTION_PROBES) { state.ReflectionProbes = optionValue == "true"; }
        else if (matchedSettingName == K_GI_MIRRORS) { state.Mirrors = optionValue == "true"; }
        else if (matchedSettingName == K_GI_SPECULAR) { state.Specular = optionValue == "true"; }
        else if (matchedSettingName == K_GI_SPECULAR_INTENSITY)
        {
            if (!SliderReadOption(optionValue, out float value)) { return; }
            state.SpecularIntensity = value;
        }
        else if (matchedSettingName == K_GI_SPECULAR_MAX_ROUGHNESS)
        {
            if (!SliderReadOption(optionValue, out float value)) { return; }
            state.SpecularMaxRoughness = value;
        }
        else if (matchedSettingName == K_GI_SPECULAR_RAY_LENGTH)
        {
            if (!SliderReadOption(optionValue, out float value)) { return; }
            state.SpecularRayLength = value;
        }
        else if (matchedSettingName == K_GI_SPECULAR_FADE_DISTANCE)
        {
            if (!SliderReadOption(optionValue, out float value)) { return; }
            state.SpecularFadeDistance = value;
        }
        else if (matchedSettingName == K_GI_DEBUG_VIEW)
        {
            BasisGlobalIlluminationFeature feature = FindFeature();
            if (feature != null) { feature.DebugView = ReadDebugView(optionValue); }
            return;
        }
        else if (matchedSettingName == K_GI_INTENSITY)
        {
            if (!SliderReadOption(optionValue, out float value)) { return; }
            state.Intensity = value;
        }
        else if (matchedSettingName == K_GI_SATURATION)
        {
            if (!SliderReadOption(optionValue, out float value)) { return; }
            state.Saturation = value;
        }
        else if (matchedSettingName == K_GI_OBSCURANCE)
        {
            if (!SliderReadOption(optionValue, out float value)) { return; }
            state.Obscurance = value;
        }
        else if (matchedSettingName == K_GI_RAY_LENGTH)
        {
            if (!SliderReadOption(optionValue, out float value)) { return; }
            state.RayLength = value;
        }
        else if (matchedSettingName == K_GI_SMOOTHING)
        {
            if (!SliderReadOption(optionValue, out float value)) { return; }
            state.Smoothing = value;
        }
        else if (matchedSettingName == K_GI_TEMPORAL_RESPONSE)
        {
            if (!SliderReadOption(optionValue, out float value)) { return; }
            state.TemporalResponse = value;
        }
        else if (matchedSettingName == K_GI_EMITTER_INTENSITY)
        {
            if (!SliderReadOption(optionValue, out float value)) { return; }
            state.EmitterIntensity = value;
        }
        else
        {
            return;
        }
        ApplyOverride();
    }

    public override void ChangedSettings()
    {
        bool capture = state.Capture;
        state = BasisGlobalIlluminationState.FromSettings();
        state.Capture = capture;
        BasisGlobalIlluminationFeature debugTarget = FindFeature();
        if (debugTarget != null)
        {
            debugTarget.DebugView = ReadDebugView(BasisSettingsDefaults.DevGiDebugView.RawValue);
        }
        ApplyOverride();
    }

    /// <summary>
    /// Which gather the effect runs. A GPU with no ray tracing backend still reports Ray Traced here - the
    /// renderer feature is what falls the frame back to the screen space gather, so the player's choice is
    /// remembered rather than rewritten under them.
    /// </summary>
    public static BasisGlobalIlluminationMode ReadMode(string optionValue)
    {
        switch (optionValue?.ToLowerInvariant())
        {
            case "ray traced":
            case "raytraced": return BasisGlobalIlluminationMode.RayTraced;
            default: return BasisGlobalIlluminationMode.ScreenSpace;
        }
    }

    /// <summary>
    /// Static and Dynamic still read, because a settings file written before they were removed keeps
    /// saying one of them for as long as that file survives. Both meant "put avatars in the trace", which
    /// is what Proxy does now, so they land there rather than on Off - answering Off would quietly take
    /// the bounce off avatars away from everyone who had asked for it.
    /// </summary>
    /// <summary>
    /// Which layers the ray traced path walks, as the three sets a player can actually tell apart. Anything
    /// finer belongs on the renderer feature, where a full mask field already exists.
    ///
    /// The default is the widest set, because that is what the trace walked before this was a choice and
    /// bounce light off the room is most of what the effect is for.
    /// </summary>
    public static LayerMask ReadLayers(string optionValue)
    {
        switch (optionValue?.ToLowerInvariant())
        {
            case "avatars": return BasisGlobalIlluminationSettings.AvatarLayers();
            case "world": return BasisGlobalIlluminationSettings.WorldLayers();
            default: return BasisGlobalIlluminationSettings.DefaultRayTracedLayers();
        }
    }

    public static BasisGlobalIlluminationRaySkinnedMode ReadSkinnedMode(string optionValue)
    {
        switch (optionValue?.ToLowerInvariant())
        {
            case "off": return BasisGlobalIlluminationRaySkinnedMode.Off;
            default: return BasisGlobalIlluminationRaySkinnedMode.Proxy;
        }
    }

    public static BasisGlobalIlluminationQuality ReadQuality(string optionValue)
    {
        switch (optionValue?.ToLowerInvariant())
        {
            case "low": return BasisGlobalIlluminationQuality.Low;
            case "high": return BasisGlobalIlluminationQuality.High;
            case "ultra": return BasisGlobalIlluminationQuality.Ultra;
            default: return BasisGlobalIlluminationQuality.Medium;
        }
    }

    public static BasisGlobalIlluminationResolution ReadResolution(string optionValue)
    {
        switch (optionValue?.ToLowerInvariant())
        {
            case "full": return BasisGlobalIlluminationResolution.Full;
            case "quarter": return BasisGlobalIlluminationResolution.Quarter;
            default: return BasisGlobalIlluminationResolution.Half;
        }
    }

    public static BasisGlobalIlluminationFallback ReadFallback(string optionValue)
    {
        switch (optionValue?.ToLowerInvariant())
        {
            case "none": return BasisGlobalIlluminationFallback.None;
            case "sky": return BasisGlobalIlluminationFallback.Sky;
            default: return BasisGlobalIlluminationFallback.ReflectionProbe;
        }
    }

    public static BasisGlobalIlluminationDebugView ReadDebugView(string optionValue)
    {
        switch (optionValue?.ToLowerInvariant())
        {
            case "indirect": return BasisGlobalIlluminationDebugView.Indirect;
            case "obscurance": return BasisGlobalIlluminationDebugView.Obscurance;
            case "normals": return BasisGlobalIlluminationDebugView.Normals;
            case "ray hits": return BasisGlobalIlluminationDebugView.RayHits;
            case "indirect only": return BasisGlobalIlluminationDebugView.IndirectOnly;
            default: return BasisGlobalIlluminationDebugView.None;
        }
    }

    public void ApplyOverride()
    {
        BasisGlobalIlluminationState effective = state;
        if (effective.Capture && captureOverride.HasValue)
        {
            BasisGlobalIlluminationCaptureOverride o = captureOverride.Value;
            effective.Mode = ReadMode(o.Mode);
            effective.SkinnedMeshes = ReadSkinnedMode(o.SkinnedMeshes);
            effective.Layers = o.Layers;
            effective.Quality = ReadQuality(o.Quality);
            effective.Fallback = ReadFallback(o.Fallback);
            effective.IgnoreBakedEmission = o.IgnoreBakedEmission;
            effective.Intensity = o.Intensity;
            effective.Saturation = o.Saturation;
            effective.Obscurance = o.Obscurance;
            effective.RayLength = o.RayLength;
            effective.Smoothing = o.Smoothing;
            effective.WideBlur = o.WideBlur;
            effective.RayReuse = o.RayReuse;
            effective.Emitters = o.Emitters;
            effective.EmitterIntensity = o.EmitterIntensity;
            effective.Specular = o.Specular;
            effective.ObscuranceRadius = o.ObscuranceRadius;
            effective.FadeDistance = o.FadeDistance;
            effective.NormalBias = o.NormalBias;
            effective.DistanceBias = o.DistanceBias;
            effective.BounceThreshold = o.BounceThreshold;
            effective.FireflyClamp = o.FireflyClamp;
            effective.ReflectionProbes = o.ReflectionProbes;
            effective.Mirrors = o.Mirrors;
        }
        Apply(BasisGlobalIlluminationSettings.Current, effective);
        ApplyFeature(effective);
    }


    /// <summary>
    /// Pushes the renderer-level options onto the feature itself, and the master switch onto the feature's
    /// own active flag. Turning the feature off is what actually stops the effect: the volume stack only
    /// decides what the feature does once URP has already called into it, so a profile the player's volume
    /// never reaches can hold the effect on by itself.
    /// </summary>
    public void ApplyFeature(BasisGlobalIlluminationState effective)
    {
        BasisGlobalIlluminationFeature feature = FindFeature();
        RememberAuthoredFeatureValues(feature);
        Apply(feature, effective.Enabled, effective.ReflectionProbes, effective.Mirrors);
    }

    // The feature is a sub-asset of the renderer, not a scene object, so writing to it in the editor
    // edits the project. The authored values are kept so play mode leaves the asset as it found it.
    private bool hasAuthoredFeatureValues;
    private bool authoredActive;
    private bool authoredReflectionProbes;
    private bool authoredMirrors;

    private void RememberAuthoredFeatureValues(BasisGlobalIlluminationFeature feature)
    {
        if (hasAuthoredFeatureValues || feature == null)
        {
            return;
        }
        hasAuthoredFeatureValues = true;
        authoredActive = feature.isActive;
        authoredReflectionProbes = feature.ReflectionProbes;
        authoredMirrors = feature.Mirrors;
    }

    public void RestoreAuthoredFeatureValues()
    {
        if (!hasAuthoredFeatureValues)
        {
            return;
        }
        hasAuthoredFeatureValues = false;
        Apply(FindFeature(), authoredActive, authoredReflectionProbes, authoredMirrors);
    }

    public static void Apply(BasisGlobalIlluminationFeature feature, bool enabled, bool reflectionProbes, bool mirrors)
    {
        if (feature == null)
        {
            return;
        }
        feature.ReflectionProbes = reflectionProbes;
        feature.Mirrors = mirrors;
        if (feature.isActive != enabled)
        {
            feature.SetActive(enabled);
        }
    }

    /// <summary>
    /// The global illumination feature on the active pipeline asset, or null when the platform's renderer
    /// does not carry one (Android ships without it).
    /// </summary>
    public static BasisGlobalIlluminationFeature FindFeature()
    {
        return FindFeature(QualitySettings.renderPipeline as UniversalRenderPipelineAsset)
            ?? FindFeature(GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset)
            ?? FindFeature(GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset);
    }

    public static BasisGlobalIlluminationFeature FindFeature(UniversalRenderPipelineAsset asset)
    {
        if (asset == null)
        {
            return null;
        }
        ReadOnlySpan<ScriptableRendererData> renderers = asset.rendererDataList;
        for (int Index = 0; Index < renderers.Length; Index++)
        {
            ScriptableRendererData data = renderers[Index];
            if (data == null)
            {
                continue;
            }
            List<ScriptableRendererFeature> features = data.rendererFeatures;
            for (int Feature = 0; Feature < features.Count; Feature++)
            {
                if (features[Feature] is BasisGlobalIlluminationFeature giFeature)
                {
                    return giFeature;
                }
            }
        }
        return null;
    }

    public static void Apply(BasisGlobalIlluminationSettings target, BasisGlobalIlluminationState state)
    {
        if (target == null)
        {
            return;
        }

        target.enable = state.Enabled;

        // Ray traced costs a great deal more than screen space, and putting every skinned mesh in the room
        // into the acceleration structure is what makes it cost that on a busy instance, so both are the
        // player's call.
        target.mode = state.Mode;
        target.rayTracedSkinnedMeshes = state.SkinnedMeshes;
        target.rayTracedLayerMask = ReadLayers(state.Layers);

        // Independent of Mode by design (see BasisGlobalIlluminationSettings.specular) - a reflection is
        // worth having over a screen space diffuse gather, so this is not gated on target.IsRayTraced().
        // Mode still decides the backend: Ray Traced walks the shared acceleration structure, Screen
        // Space (and any GPU without ray tracing) walks the depth buffer against the previous frame.
        target.specular = state.Specular;
        target.specularIntensity = Mathf.Clamp(state.SpecularIntensity, BasisSettingsDefaults.GI_SPECULAR_INTENSITY_MIN, BasisSettingsDefaults.GI_SPECULAR_INTENSITY_MAX);
        target.specularMaxRoughness = Mathf.Clamp(state.SpecularMaxRoughness, BasisSettingsDefaults.GI_SPECULAR_MAX_ROUGHNESS_MIN, BasisSettingsDefaults.GI_SPECULAR_MAX_ROUGHNESS_MAX);
        target.specularRayLength = Mathf.Clamp(state.SpecularRayLength, BasisSettingsDefaults.GI_SPECULAR_RAY_LENGTH_MIN, BasisSettingsDefaults.GI_SPECULAR_RAY_LENGTH_MAX);
        target.specularFadeDistance = Mathf.Clamp(state.SpecularFadeDistance, BasisSettingsDefaults.GI_SPECULAR_FADE_DISTANCE_MIN, BasisSettingsDefaults.GI_SPECULAR_FADE_DISTANCE_MAX);

        // The tracing internals. Clamp() below holds every one of them inside its documented range, so a
        // hand edited settings file cannot hand the tracer a radius of zero or a bias of a metre.
        target.obscuranceRadius = state.ObscuranceRadius;
        target.fadeDistance = state.FadeDistance;
        target.rayTracedNormalBias = state.NormalBias;
        target.rayDistanceBias = state.DistanceBias;
        target.rayBounceThreshold = state.BounceThreshold;
        target.fireflyClamp = state.FireflyClamp;

        target.quality = state.Quality;
        target.resolution = state.Capture ? BasisGlobalIlluminationResolution.Full : state.Resolution;

        // A photo is a single frame, so the temporal filter has nothing to accumulate from and the ray
        // budget is the only thing that decides how clean it is.
        target.overrideQualityCounts = state.Capture;
        target.rayCount = CaptureRayCount;
        target.rayMaxSteps = CaptureRaySteps;

        target.fallback = state.Fallback;

        // The player-facing toggle is framed as "ignore" (double up the bounce); the field it drives is
        // framed as "respect" (do not double count). Same bit, opposite polarity.
        target.respectBakedEmission = !state.IgnoreBakedEmission;

        // The receiving half of the same double-count: how much of the effect a lightmapped surface keeps.
        target.lightmappedReceive = Mathf.Clamp(state.LightmappedReceive, BasisSettingsDefaults.GI_LIGHTMAPPED_RECEIVE_MIN, BasisSettingsDefaults.GI_LIGHTMAPPED_RECEIVE_MAX);

        // Clamped to the range the SLIDER advertises, which is narrower than the range the value itself
        // permits - Max Ray Length is the clearest case, 64 on the panel against a 128 ceiling on the
        // field. Clamp() below is the backstop for anything that never came through a slider; this is
        // what stops a hand-edited settings file handing the player a value their panel cannot show.
        target.intensity = Mathf.Clamp(state.Intensity, BasisSettingsDefaults.GI_INTENSITY_MIN, BasisSettingsDefaults.GI_INTENSITY_MAX);
        target.saturation = Mathf.Clamp(state.Saturation, BasisSettingsDefaults.GI_SATURATION_MIN, BasisSettingsDefaults.GI_SATURATION_MAX);
        target.obscuranceIntensity = Mathf.Clamp(state.Obscurance, BasisSettingsDefaults.GI_OBSCURANCE_MIN, BasisSettingsDefaults.GI_OBSCURANCE_MAX);
        target.maxRayLength = Mathf.Clamp(state.RayLength, BasisSettingsDefaults.GI_RAY_LENGTH_MIN, BasisSettingsDefaults.GI_RAY_LENGTH_MAX);
        target.smoothing = Mathf.Clamp(state.Smoothing, BasisSettingsDefaults.GI_SMOOTHING_MIN, BasisSettingsDefaults.GI_SMOOTHING_MAX);

        // Temporal accumulation is a comfort setting: at a low response the bounce trails behind anything
        // that moves, so the player's choice is what drives it.
        target.temporalFilter = state.TemporalFilter && !state.Capture;
        target.temporalResponse = Mathf.Clamp(state.TemporalResponse, BasisSettingsDefaults.GI_TEMPORAL_RESPONSE_MIN, BasisSettingsDefaults.GI_TEMPORAL_RESPONSE_MAX);

        target.wideBlur = state.WideBlur;
        target.rayReuse = state.RayReuse;

        target.emitters = state.Emitters;
        target.emitterIntensity = Mathf.Clamp(state.EmitterIntensity, BasisSettingsDefaults.GI_EMITTER_INTENSITY_MIN, BasisSettingsDefaults.GI_EMITTER_INTENSITY_MAX);

        // The sliders carry their own ranges, but a persisted settings file is just text on disk and a
        // hand-edited one reaches here unchecked. One call holds the whole object inside its documented
        // ranges rather than clamping value by value at the twelve call sites that used to.
        target.Clamp();
    }

}
#endif
