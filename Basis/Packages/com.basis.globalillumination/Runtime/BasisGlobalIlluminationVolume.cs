using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public enum BasisGlobalIlluminationQuality
{
    Low,
    Medium,
    High,
    Ultra
}

/// <summary>
/// How the bounce is gathered. Screen Space marches the depth buffer and can only gather what the camera
/// already drew; Ray Traced traces the scene itself, so it also carries light from behind the camera and
/// from surfaces the frame never rasterised - and it shades what it hits with the real lights and emissive
/// materials rather than reading the colour off the screen.
/// </summary>
public enum BasisGlobalIlluminationMode
{
    ScreenSpace,
    RayTraced
}

public enum BasisGlobalIlluminationResolution
{
    Full = 1,
    Half = 2,
    Quarter = 4
}

public enum BasisGlobalIlluminationNormalSource
{
    ReconstructFromDepth,
    NormalsTexture
}

public enum BasisGlobalIlluminationFallback
{
    None,
    Sky,
    ReflectionProbe
}

public enum BasisGlobalIlluminationDebugView
{
    None,
    Indirect,
    Obscurance,
    Normals,
    RayHits,
    IndirectOnly
}

[Serializable]
public sealed class BasisGlobalIlluminationQualityParameter : VolumeParameter<BasisGlobalIlluminationQuality>
{
    public BasisGlobalIlluminationQualityParameter(BasisGlobalIlluminationQuality value, bool overrideState = false) : base(value, overrideState) { }
}

[Serializable]
public sealed class BasisGlobalIlluminationResolutionParameter : VolumeParameter<BasisGlobalIlluminationResolution>
{
    public BasisGlobalIlluminationResolutionParameter(BasisGlobalIlluminationResolution value, bool overrideState = false) : base(value, overrideState) { }
}

[Serializable]
public sealed class BasisGlobalIlluminationNormalSourceParameter : VolumeParameter<BasisGlobalIlluminationNormalSource>
{
    public BasisGlobalIlluminationNormalSourceParameter(BasisGlobalIlluminationNormalSource value, bool overrideState = false) : base(value, overrideState) { }
}

[Serializable]
public sealed class BasisGlobalIlluminationFallbackParameter : VolumeParameter<BasisGlobalIlluminationFallback>
{
    public BasisGlobalIlluminationFallbackParameter(BasisGlobalIlluminationFallback value, bool overrideState = false) : base(value, overrideState) { }
}

[Serializable]
public sealed class BasisGlobalIlluminationModeParameter : VolumeParameter<BasisGlobalIlluminationMode>
{
    public BasisGlobalIlluminationModeParameter(BasisGlobalIlluminationMode value, bool overrideState = false) : base(value, overrideState) { }
}

[Serializable]
public sealed class BasisGlobalIlluminationRaySkinnedModeParameter : VolumeParameter<BasisGlobalIlluminationRaySkinnedMode>
{
    public BasisGlobalIlluminationRaySkinnedModeParameter(BasisGlobalIlluminationRaySkinnedMode value, bool overrideState = false) : base(value, overrideState) { }
}

[VolumeComponentMenu("Lighting/Basis Global Illumination"), SupportedOnRenderPipeline(typeof(UniversalRenderPipelineAsset))]
[VolumeRequiresRendererFeatures(typeof(BasisGlobalIlluminationFeature))]
public sealed class BasisGlobalIlluminationVolume : VolumeComponent, IPostProcessComponent
{
    public const float IntensityMin = 0f, IntensityMax = 4f;
    public const float ObscuranceMin = 0f, ObscuranceMax = 1f;
    public const float SaturationMin = 0f, SaturationMax = 2f;
    public const float RayLengthMin = 0.25f, RayLengthMax = 128f;
    public const float ThicknessMin = 0.02f, ThicknessMax = 4f;
    public const float SmoothingMin = 0f, SmoothingMax = 4f;
    public const float TemporalResponseMin = 0.02f, TemporalResponseMax = 1f;
    public const float FallbackIntensityMin = 0f, FallbackIntensityMax = 4f;
    public const float EmitterIntensityMin = 0f, EmitterIntensityMax = 8f;
    public const float FireflyClampMin = 1f, FireflyClampMax = 32f;
    public const int RayCountMin = 1, RayCountMax = 16;
    public const int RayStepsMin = 4, RayStepsMax = 128;
    public const int BouncesMin = 1, BouncesMax = 4;
    public const float LightIntensityMin = 0f, LightIntensityMax = 4f;
    public const int LightSamplesMax = 4;
    public const float RayTracedNormalBiasMin = 0f, RayTracedNormalBiasMax = 0.5f;
    public const float RescanIntervalMin = 0.1f, RescanIntervalMax = 30f;
    public const int SkinnedBudgetMin = 0, SkinnedBudgetMax = 8;
    public const int SkinnedIntervalMin = 1, SkinnedIntervalMax = 30;
    public const float SkinnedDistanceMin = 0f, SkinnedDistanceMax = 64f;

    public BoolParameter enable = new BoolParameter(false, BoolParameter.DisplayType.EnumPopup);
    public BasisGlobalIlluminationModeParameter mode = new BasisGlobalIlluminationModeParameter(BasisGlobalIlluminationMode.ScreenSpace, true);

    [Header("General")]
    public ClampedFloatParameter intensity = new ClampedFloatParameter(1f, IntensityMin, IntensityMax);
    public ClampedFloatParameter saturation = new ClampedFloatParameter(1f, SaturationMin, SaturationMax);
    public ColorParameter tint = new ColorParameter(Color.white, hdr: false, showAlpha: false, showEyeDropper: false);
    public ClampedFloatParameter obscuranceIntensity = new ClampedFloatParameter(0.5f, ObscuranceMin, ObscuranceMax);
    public ClampedFloatParameter obscuranceRadius = new ClampedFloatParameter(0.5f, 0.05f, 4f);
    public ClampedFloatParameter maxRayLength = new ClampedFloatParameter(16f, RayLengthMin, RayLengthMax);
    public MinFloatParameter fadeDistance = new MinFloatParameter(120f, 1f);

    [Header("Quality")]
    public BasisGlobalIlluminationQualityParameter quality = new BasisGlobalIlluminationQualityParameter(BasisGlobalIlluminationQuality.Medium, true);
    public BoolParameter overrideQualityCounts = new BoolParameter(false);
    public ClampedIntParameter rayCount = new ClampedIntParameter(2, RayCountMin, RayCountMax);
    public ClampedIntParameter rayMaxSteps = new ClampedIntParameter(24, RayStepsMin, RayStepsMax);
    public ClampedFloatParameter thickness = new ClampedFloatParameter(0.35f, ThicknessMin, ThicknessMax);
    public ClampedFloatParameter jitter = new ClampedFloatParameter(1f, 0f, 1f);
    public ClampedFloatParameter smoothing = new ClampedFloatParameter(1f, SmoothingMin, SmoothingMax);
    public BoolParameter wideBlur = new BoolParameter(true);
    public BasisGlobalIlluminationNormalSourceParameter normalSource = new BasisGlobalIlluminationNormalSourceParameter(BasisGlobalIlluminationNormalSource.ReconstructFromDepth);

    [Header("Fallbacks")]
    public BasisGlobalIlluminationFallbackParameter fallback = new BasisGlobalIlluminationFallbackParameter(BasisGlobalIlluminationFallback.ReflectionProbe);
    public ClampedFloatParameter fallbackIntensity = new ClampedFloatParameter(1f, FallbackIntensityMin, FallbackIntensityMax);
    public BoolParameter rayReuse = new BoolParameter(true);
    public BoolParameter emitters = new BoolParameter(true);
    public ClampedFloatParameter emitterIntensity = new ClampedFloatParameter(1f, EmitterIntensityMin, EmitterIntensityMax);
    public BoolParameter emitterOcclusion = new BoolParameter(true);

    [Header("Ray Tracing")]
    public ClampedIntParameter bounces = new ClampedIntParameter(1, BouncesMin, BouncesMax);
    public BoolParameter rayTracedLights = new BoolParameter(true);
    public ClampedFloatParameter rayTracedLightIntensity = new ClampedFloatParameter(1f, LightIntensityMin, LightIntensityMax);
    public BoolParameter rayTracedShadows = new BoolParameter(true);
    public BoolParameter rayTracedEmissiveSurfaces = new BoolParameter(true);
    public BoolParameter rayTracedTextureAlbedo = new BoolParameter(true);
    public BasisGlobalIlluminationRaySkinnedModeParameter rayTracedSkinnedMeshes = new BasisGlobalIlluminationRaySkinnedModeParameter(BasisGlobalIlluminationRaySkinnedMode.Dynamic, true);
    public ClampedIntParameter rayTracedSkinnedBudget = new ClampedIntParameter(2, SkinnedBudgetMin, SkinnedBudgetMax);
    public ClampedIntParameter rayTracedSkinnedInterval = new ClampedIntParameter(4, SkinnedIntervalMin, SkinnedIntervalMax);
    public ClampedFloatParameter rayTracedSkinnedDistance = new ClampedFloatParameter(16f, SkinnedDistanceMin, SkinnedDistanceMax);
    public LayerMaskParameter rayTracedLayerMask = new LayerMaskParameter(~0);
    public BoolParameter rayTracedShadowCastersOnly = new BoolParameter(false);
    public ClampedFloatParameter rayTracedRescanInterval = new ClampedFloatParameter(2f, RescanIntervalMin, RescanIntervalMax);
    public ClampedFloatParameter rayTracedNormalBias = new ClampedFloatParameter(0.02f, RayTracedNormalBiasMin, RayTracedNormalBiasMax);

    [Header("Performance")]
    public BasisGlobalIlluminationResolutionParameter resolution = new BasisGlobalIlluminationResolutionParameter(BasisGlobalIlluminationResolution.Half, true);
    public BoolParameter temporalFilter = new BoolParameter(true);
    public ClampedFloatParameter temporalResponse = new ClampedFloatParameter(0.15f, TemporalResponseMin, TemporalResponseMax);
    public ClampedFloatParameter depthRejection = new ClampedFloatParameter(0.1f, 0.005f, 1f);
    /// <summary>
    /// Clips the reprojected history into the current frame's neighbourhood, to reject ghosting.
    ///
    /// ⚠️ Measured 2026-08-27: this barely engages any more, and in the ray traced path not at all - it moves
    /// a settled image by 0.0003 against a repeatability floor of 0.024, where every live setting moves it by
    /// more than the floor. Two independent changes did that and they compound, so anyone reconsidering this
    /// toggle has to look at both:
    ///
    ///   - the clip box gained a floor (BASISGI_TEMPORAL_CLIP_RARE) so a neighbourhood of misses could not
    ///     collapse it onto zero and erase an accumulated highlight. That alone already had it an order of
    ///     magnitude under its own floor in the traced path: delta 0.0032 against floor 0.0859.
    ///   - the temporal blend then started taking a plane-gated neighbourhood mean rather than the raw pixel,
    ///     so the value being clipped now arrives close to the box centre. That took it 10x further, to 0.0003.
    ///
    /// Neither is a defect - a safety net that stops engaging because its input got clean is working. But the
    /// toggle now costs a 3x3 fetch and a branch to do nothing measurable, and the honest options are to drop
    /// it or to give it back its bite where a slow Temporal Response still lets history survive long enough
    /// to ghost. It is deliberately left as a failing sweep entry rather than annotated away, so the decision
    /// stays visible. Do not remove the binding without handling the persisted setting key.
    /// </summary>
    public BoolParameter neighbourhoodClamp = new BoolParameter(true);
    public ClampedFloatParameter fireflyClamp = new ClampedFloatParameter(6f, FireflyClampMin, FireflyClampMax);
    public BoolParameter bilateralUpsample = new BoolParameter(true);

    public bool IsActive() => enable.value && intensity.value > 0f;

    public int ResolvedRayCount()
    {
        if (overrideQualityCounts.value) { return rayCount.value; }
        switch (quality.value)
        {
            case BasisGlobalIlluminationQuality.Low: return 1;
            case BasisGlobalIlluminationQuality.High: return 4;
            case BasisGlobalIlluminationQuality.Ultra: return 8;
            default: return 2;
        }
    }

    public int ResolvedRaySteps()
    {
        if (overrideQualityCounts.value) { return rayMaxSteps.value; }
        switch (quality.value)
        {
            case BasisGlobalIlluminationQuality.Low: return 12;
            case BasisGlobalIlluminationQuality.High: return 32;
            case BasisGlobalIlluminationQuality.Ultra: return 48;
            default: return 20;
        }
    }

    public int ResolvedMaxEmitters()
    {
        switch (quality.value)
        {
            case BasisGlobalIlluminationQuality.Low: return 4;
            case BasisGlobalIlluminationQuality.High: return 24;
            case BasisGlobalIlluminationQuality.Ultra: return 48;
            default: return 12;
        }
    }

    public int ResolvedResolutionDivisor()
    {
        int divisor = (int)resolution.value;
        return divisor < 1 ? 1 : divisor;
    }

    public bool IsRayTraced() => mode.value == BasisGlobalIlluminationMode.RayTraced;

    private static int interfaceFilteredLayers;
    private static bool interfaceFilteredLayersResolved;

    /// <summary>
    /// Everything except the UI layers. A menu panel in the acceleration structure bounces its own
    /// brightness onto the room and casts a shadow from a surface the player reads as an overlay.
    /// Resolved on first use rather than in a field initializer: this is a ScriptableObject, and layer
    /// names cannot be looked up while one is being deserialized.
    /// </summary>
    public static LayerMask DefaultRayTracedLayers()
    {
        if (interfaceFilteredLayersResolved) { return interfaceFilteredLayers; }

        int mask = ~0;
        string[] interfaceLayers = { "UI", "OverlayUI", "HandHeldCameraUI" };
        for (int index = 0; index < interfaceLayers.Length; index++)
        {
            int layer = LayerMask.NameToLayer(interfaceLayers[index]);
            if (layer >= 0) { mask &= ~(1 << layer); }
        }

        interfaceFilteredLayers = mask;
        interfaceFilteredLayersResolved = true;
        return mask;
    }

    /// <summary>
    /// The layers the trace actually walks. Everything means the mask was left alone, and the interface
    /// layers come out of it; any other mask was chosen by somebody and is taken exactly as written.
    /// </summary>
    public LayerMask ResolvedTraceLayers()
    {
        int layers = rayTracedLayerMask.value;
        return layers == ~0 ? DefaultRayTracedLayers() : layers;
    }

    /// <summary>
    /// A second bounce doubles the ray budget, so the quality ladder owns it unless the volume was told to
    /// drive the counts itself.
    /// </summary>
    public int ResolvedBounces()
    {
        if (overrideQualityCounts.value) { return Mathf.Clamp(bounces.value, BouncesMin, BouncesMax); }
        switch (quality.value)
        {
            case BasisGlobalIlluminationQuality.Low: return 1;
            case BasisGlobalIlluminationQuality.High: return 2;
            case BasisGlobalIlluminationQuality.Ultra: return 3;
            default: return 1;
        }
    }

    /// <summary>
    /// How many lights a hit may be shaded by. A hit shadow-rays only the ones resampling drew for it,
    /// so the size of this list no longer decides the frame cost - which is why it can be large enough
    /// that a light does not have to be thrown out of it as the player walks. A light leaving the budget
    /// takes all of its contribution with it, and that step is seen as a blink.
    /// </summary>
    public int ResolvedRayTracedLightLimit()
    {
        if (!rayTracedLights.value) { return 0; }
        switch (quality.value)
        {
            case BasisGlobalIlluminationQuality.Low: return 16;
            case BasisGlobalIlluminationQuality.High: return 48;
            case BasisGlobalIlluminationQuality.Ultra: return BasisGlobalIlluminationRayLights.MaxLights;
            default: return 32;
        }
    }

    /// <summary>How many of those lights a hit actually pays a shadow ray for.</summary>
    public int ResolvedRayTracedLightSamples()
    {
        if (!rayTracedLights.value) { return 1; }
        switch (quality.value)
        {
            case BasisGlobalIlluminationQuality.High: return 2;
            case BasisGlobalIlluminationQuality.Ultra: return LightSamplesMax;
            default: return 1;
        }
    }

    public BasisGlobalIlluminationRaySceneSettings ResolvedSceneSettings()
    {
        return new BasisGlobalIlluminationRaySceneSettings
        {
            layerMask = ResolvedTraceLayers(),
            shadowCastersOnly = rayTracedShadowCastersOnly.value,
            rescanInterval = rayTracedRescanInterval.value,
            skinnedMode = rayTracedSkinnedMeshes.value,
            skinnedBakesPerFrame = rayTracedSkinnedBudget.value,
            skinnedBakeInterval = rayTracedSkinnedInterval.value,
            skinnedMaxDistance = rayTracedSkinnedDistance.value,
            textureAlbedo = rayTracedTextureAlbedo.value,
            emissiveSurfaces = rayTracedEmissiveSurfaces.value
        };
    }

    public BasisGlobalIlluminationRayLightSettings ResolvedLightSettings()
    {
        return new BasisGlobalIlluminationRayLightSettings
        {
            layerMask = ResolvedTraceLayers(),
            limit = ResolvedRayTracedLightLimit(),
            shadowRays = rayTracedShadows.value,
            emitters = emitters.value,
            emitterIntensity = emitterIntensity.value,
            rescanInterval = rayTracedRescanInterval.value
        };
    }
}
