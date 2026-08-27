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
    public BasisGlobalIlluminationQuality Quality;
    public BasisGlobalIlluminationResolution Resolution;
    public BasisGlobalIlluminationFallback Fallback;
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
    public bool Capture;

    public static BasisGlobalIlluminationState FromDefaults()
    {
        return new BasisGlobalIlluminationState
        {
            Enabled = BasisSettingsDefaults.UseGlobalIllumination.DefaultValue.GetDefault(),
            Mode = SMModuleGlobalIlluminationURP.ReadMode(BasisSettingsDefaults.GlobalIlluminationMode.DefaultValue.GetDefault()),
            SkinnedMeshes = SMModuleGlobalIlluminationURP.ReadSkinnedMode(BasisSettingsDefaults.GlobalIlluminationSkinnedMeshes.DefaultValue.GetDefault()),
            Quality = SMModuleGlobalIlluminationURP.ReadQuality(BasisSettingsDefaults.GlobalIlluminationQuality.DefaultValue.GetDefault()),
            Resolution = SMModuleGlobalIlluminationURP.ReadResolution(BasisSettingsDefaults.GlobalIlluminationResolution.DefaultValue.GetDefault()),
            Fallback = SMModuleGlobalIlluminationURP.ReadFallback(BasisSettingsDefaults.GlobalIlluminationFallback.DefaultValue.GetDefault()),
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
            Quality = SMModuleGlobalIlluminationURP.ReadQuality(BasisSettingsDefaults.GlobalIlluminationQuality.RawValue),
            Resolution = SMModuleGlobalIlluminationURP.ReadResolution(BasisSettingsDefaults.GlobalIlluminationResolution.RawValue),
            Fallback = SMModuleGlobalIlluminationURP.ReadFallback(BasisSettingsDefaults.GlobalIlluminationFallback.RawValue),
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
            Capture = false
        };
    }
}

public class SMModuleGlobalIlluminationURP : BasisSettingsBase
{
    public const int CaptureRayCount = 8;
    public const int CaptureRaySteps = 64;

    private static readonly HashSet<Camera> registeredCameras = new HashSet<Camera>();
    private static readonly HashSet<Camera> suspendedCameras = new HashSet<Camera>();
    private static SMModuleGlobalIlluminationURP instance;

    private BasisGlobalIlluminationState state = BasisGlobalIlluminationState.FromDefaults();

    /// <summary>What the effect is rendering with. The settings provider writes straight into this.</summary>
    public BasisGlobalIlluminationSettings GlobalIllumination => BasisGlobalIlluminationSettings.Current;
    public bool Capturing => state.Capture;
    public BasisGlobalIlluminationState State => state;

    private static string K_USE_GI => BasisSettingsDefaults.UseGlobalIllumination.BindingKey;
    private static string K_GI_MODE => BasisSettingsDefaults.GlobalIlluminationMode.BindingKey;
    private static string K_GI_SKINNED => BasisSettingsDefaults.GlobalIlluminationSkinnedMeshes.BindingKey;
    private static string K_GI_QUALITY => BasisSettingsDefaults.GlobalIlluminationQuality.BindingKey;
    private static string K_GI_RESOLUTION => BasisSettingsDefaults.GlobalIlluminationResolution.BindingKey;
    private static string K_GI_FALLBACK => BasisSettingsDefaults.GlobalIlluminationFallback.BindingKey;
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

    public static void BeginCapture(Camera camera)
    {
        if (instance == null || instance.state.Capture || !instance.state.Enabled || !IsCameraRegistered(camera))
        {
            return;
        }
        instance.state.Capture = true;
        instance.ApplyOverride();
    }

    public static void EndCapture()
    {
        if (instance == null || !instance.state.Capture)
        {
            return;
        }
        instance.state.Capture = false;
        instance.ApplyOverride();
    }

    public override void ValidSettingsChange(string matchedSettingName, string optionValue)
    {
        if (matchedSettingName == K_USE_GI) { state.Enabled = optionValue == "true"; }
        else if (matchedSettingName == K_GI_MODE) { state.Mode = ReadMode(optionValue); }
        else if (matchedSettingName == K_GI_SKINNED) { state.SkinnedMeshes = ReadSkinnedMode(optionValue); }
        else if (matchedSettingName == K_GI_QUALITY) { state.Quality = ReadQuality(optionValue); }
        else if (matchedSettingName == K_GI_RESOLUTION) { state.Resolution = ReadResolution(optionValue); }
        else if (matchedSettingName == K_GI_FALLBACK) { state.Fallback = ReadFallback(optionValue); }
        else if (matchedSettingName == K_GI_TEMPORAL_FILTER) { state.TemporalFilter = optionValue == "true"; }
        else if (matchedSettingName == K_GI_WIDE_BLUR) { state.WideBlur = optionValue == "true"; }
        else if (matchedSettingName == K_GI_RAY_REUSE) { state.RayReuse = optionValue == "true"; }
        else if (matchedSettingName == K_GI_EMITTERS) { state.Emitters = optionValue == "true"; }
        else if (matchedSettingName == K_GI_REFLECTION_PROBES) { state.ReflectionProbes = optionValue == "true"; }
        else if (matchedSettingName == K_GI_MIRRORS) { state.Mirrors = optionValue == "true"; }
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

    public static BasisGlobalIlluminationRaySkinnedMode ReadSkinnedMode(string optionValue)
    {
        switch (optionValue?.ToLowerInvariant())
        {
            case "off": return BasisGlobalIlluminationRaySkinnedMode.Off;
            case "static": return BasisGlobalIlluminationRaySkinnedMode.Static;
            case "dynamic": return BasisGlobalIlluminationRaySkinnedMode.Dynamic;
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
        Apply(BasisGlobalIlluminationSettings.Current, state);
        ApplyFeature();
    }


    /// <summary>
    /// Pushes the renderer-level options onto the feature itself, and the master switch onto the feature's
    /// own active flag. Turning the feature off is what actually stops the effect: the volume stack only
    /// decides what the feature does once URP has already called into it, so a profile the player's volume
    /// never reaches can hold the effect on by itself.
    /// </summary>
    public void ApplyFeature()
    {
        BasisGlobalIlluminationFeature feature = FindFeature();
        RememberAuthoredFeatureValues(feature);
        Apply(feature, state.Enabled, state.ReflectionProbes, state.Mirrors);
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

        target.quality = state.Quality;
        target.resolution = state.Capture ? BasisGlobalIlluminationResolution.Full : state.Resolution;

        // A photo is a single frame, so the temporal filter has nothing to accumulate from and the ray
        // budget is the only thing that decides how clean it is.
        target.overrideQualityCounts = state.Capture;
        target.rayCount = CaptureRayCount;
        target.rayMaxSteps = CaptureRaySteps;

        target.fallback = state.Fallback;

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
