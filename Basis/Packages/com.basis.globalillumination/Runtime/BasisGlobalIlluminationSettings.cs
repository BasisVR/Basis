using UnityEngine;

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

/// <summary>
/// Everything the effect reads, as plain values, owned by whoever is driving it.
///
/// This used to be a VolumeComponent blended out of URP's volume stack, and the volume model cost far
/// more than it paid. The settings module had to create its own volume at priority 1000 to beat anything
/// a scene had authored, which meant a scene volume and the player's settings quietly disagreed and the
/// higher priority won; it wrote the player's values into the pipeline's SHARED default profile assets
/// and had to remember the authored values to put back, so a crash or a skipped OnDestroy left a profile
/// on disk holding somebody's runtime state; and because the handheld camera renders on its own volume
/// layer, a duplicate volume had to be built per uncovered layer just so a second camera could see the
/// same numbers. Three mechanisms, all of them load-bearing, none of them visible in a debugger - and
/// the effect's on/off state depended on all three agreeing.
///
/// The settings provider is the source of truth now and writes here directly. What a camera renders with
/// is the value that is in this object.
/// </summary>
public sealed class BasisGlobalIlluminationSettings
{
    public const float IntensityMin = 0f, IntensityMax = 4f;
    public const float ObscuranceMin = 0f, ObscuranceMax = 1f;
    public const float SaturationMin = 0f, SaturationMax = 2f;
    public const float RayLengthMin = 0.25f, RayLengthMax = 128f;
    // A reflection carries much further than a bounce does - the far wall of a room is a bounce nobody can
    // see and a reflection everybody can - so its reach is allowed past the diffuse ceiling.
    public const float SpecularRayLengthMax = 512f;
    public const float SpecularRoughnessMin = 0.05f, SpecularRoughnessMax = 1f;
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

    /// <summary>
    /// What every camera renders with. One object, assigned in place by the settings module, read by the
    /// feature and both passes. Never null, so nothing downstream has to guard it - a client that never
    /// touches settings runs the authored defaults below.
    /// </summary>
    public static readonly BasisGlobalIlluminationSettings Current = new BasisGlobalIlluminationSettings();

    public bool enable = false;
    public BasisGlobalIlluminationMode mode = BasisGlobalIlluminationMode.ScreenSpace;

    public float intensity = 1f;
    public float saturation = 1f;
    public Color tint = Color.white;
    public float obscuranceIntensity = 0.5f;
    public float obscuranceRadius = 0.5f;
    public float maxRayLength = 16f;
    public float fadeDistance = 120f;

    public BasisGlobalIlluminationQuality quality = BasisGlobalIlluminationQuality.Medium;
    public bool overrideQualityCounts = false;
    public int rayCount = 2;
    public int rayMaxSteps = 24;
    public float thickness = 0.35f;

    /// <summary>
    /// Walks the screen space ray through a coarse depth summary first, and only looks at individual
    /// texels inside a region that summary says could contain a hit.
    ///
    /// The plain march spends its whole step budget uniformly along the ray: Ray Steps steps over the
    /// entire Max Ray Length, so at the shipped default that is twenty steps across sixteen metres. Near
    /// the origin each stride can be tens of texels, and a stride that long simply passes over anything
    /// thinner than itself - the Thickness setting exists to paper over the resulting mess, and it papers
    /// over misses and false hits in equal measure.
    /// </summary>
    public bool hierarchicalMarch = true;
    public float jitter = 1f;
    public float smoothing = 1f;
    public bool wideBlur = true;
    public BasisGlobalIlluminationNormalSource normalSource = BasisGlobalIlluminationNormalSource.ReconstructFromDepth;

    public BasisGlobalIlluminationFallback fallback = BasisGlobalIlluminationFallback.ReflectionProbe;
    public float fallbackIntensity = 1f;
    public bool rayReuse = true;
    public bool emitters = true;
    public float emitterIntensity = 1f;
    public bool emitterOcclusion = true;

    public int bounces = 1;
    public bool rayTracedLights = true;
    public float rayTracedLightIntensity = 1f;
    public bool rayTracedShadows = true;
    public bool rayTracedEmissiveSurfaces = true;

    /// <summary>
    /// Leave a baked-emissive surface's light to the lightmap that already holds it.
    ///
    /// An emissive quad used as an area light is the standard way a lightmapped world is lit. Its light was
    /// computed once, at bake time, and written into the lightmap; the surface still renders bright because
    /// URP draws emission regardless of how it was baked. A gather that reads that brightness and injects
    /// it again is lighting the room twice from one lamp, and it is the reason this effect can make a
    /// carefully baked world look blown out the moment it is switched on.
    ///
    /// Only surfaces that are BOTH flagged baked-emissive AND carrying a real lightmap are skipped, which
    /// is what keeps this from stealing light in a world nobody ever baked.
    /// </summary>
    public bool respectBakedEmission = true;
    public bool rayTracedTextureAlbedo = true;
    public BasisGlobalIlluminationRaySkinnedMode rayTracedSkinnedMeshes = BasisGlobalIlluminationRaySkinnedMode.Dynamic;
    public int rayTracedSkinnedBudget = 2;
    public int rayTracedSkinnedInterval = 4;
    public float rayTracedSkinnedDistance = 16f;
    public LayerMask rayTracedLayerMask = ~0;
    public bool rayTracedShadowCastersOnly = false;
    public float rayTracedRescanInterval = 2f;
    public float rayTracedNormalBias = 0.02f;

    /// <summary>
    /// Ray traced reflections. The gather is a single mirror ray per pixel, shaded at the hit with the same
    /// lights and emissive surfaces the diffuse bounce uses, published for URP's lit shaders to consume in
    /// place of the reflection probe. Deliberately independent of Mode, because reflections are worth
    /// having over a screen space diffuse gather, and a diffuse gather is worth having without them.
    /// </summary>
    public bool specular = false;
    public float specularIntensity = 1f;
    /// <summary>
    /// The roughness at which the traced mirror ray stops being a usable stand-in and the reflection probe
    /// takes over completely. Below it the two are blended, so there is no visible line across a surface
    /// whose roughness varies.
    /// </summary>
    public float specularMaxRoughness = 0.5f;
    public float specularRayLength = 64f;
    public float specularFadeDistance = 80f;
    /// <summary>
    /// Path length from the mirror hit. 1 shades the hit with direct light and emission only. Past that the
    /// continuation is diffuse rather than a second mirror, because the instance buffer carries albedo and
    /// emission and no roughness - which is what stops the reflection of an unlit corner being black.
    /// </summary>
    public int specularBounces = 1;
    public bool specularTemporal = true;

    public BasisGlobalIlluminationResolution resolution = BasisGlobalIlluminationResolution.Half;
    public bool temporalFilter = true;
    public float temporalResponse = 0.15f;

    /// <summary>
    /// Reprojects the history through the frame's motion vectors rather than through the previous
    /// view-projection alone. The matrix carries the CAMERA's motion and nothing else, so it is only
    /// correct where the world stood still.
    ///
    /// ⚠️ OFF BY DEFAULT BECAUSE IT IS UNVERIFIED. URP advances the previous-frame matrix that feeds the
    /// motion vector pass once per ENGINE frame, and an EditMode test drives the camera with
    /// Camera.Render() in a loop, which never advances that counter - measured 2026-08-27:
    /// `Time.frameCount` moved by zero across a twelve frame run, and in a scene where nothing moved the
    /// motion texture still read about 1.5 pixels where every vector had to be exactly zero. So every
    /// number the render harness produces about this setting measures that broken input, not the
    /// reprojection. What would settle it: play mode, or a headset, with a person walking across the view.
    /// </summary>
    public bool motionVectors = false;
    public float depthRejection = 0.1f;

    /// <summary>
    /// Clips the reprojected history into the current frame's neighbourhood, to reject ghosting.
    ///
    /// ⚠️ Measured 2026-08-27: this barely engages any more, and in the ray traced path not at all - it moves
    /// a settled image by 0.0003 against a repeatability floor of 0.024, where every live setting moves it by
    /// more than the floor. The clip box gained a floor so a neighbourhood of misses could not collapse it
    /// onto zero, and the temporal blend then started taking a plane-gated neighbourhood mean rather than the
    /// raw pixel, so the value being clipped now arrives close to the box centre. Neither is a defect - a
    /// safety net that stops engaging because its input got clean is working - but the toggle now costs a
    /// 3x3 fetch and a branch to do nothing measurable. It is deliberately left as a failing sweep entry
    /// rather than annotated away, so the decision stays visible.
    /// </summary>
    public bool neighbourhoodClamp = true;
    public float fireflyClamp = 6f;
    public bool bilateralUpsample = true;

    /// <summary>
    /// Holds every value inside the range its slider advertises. The volume parameters used to do this on
    /// assignment; plain fields do not, and a caller that writes a raw setting string is exactly the caller
    /// that can put a negative intensity or a zero ray count in here.
    /// </summary>
    public void Clamp()
    {
        intensity = Mathf.Clamp(intensity, IntensityMin, IntensityMax);
        saturation = Mathf.Clamp(saturation, SaturationMin, SaturationMax);
        obscuranceIntensity = Mathf.Clamp(obscuranceIntensity, ObscuranceMin, ObscuranceMax);
        obscuranceRadius = Mathf.Clamp(obscuranceRadius, 0.05f, 4f);
        maxRayLength = Mathf.Clamp(maxRayLength, RayLengthMin, RayLengthMax);
        fadeDistance = Mathf.Max(1f, fadeDistance);
        rayCount = Mathf.Clamp(rayCount, RayCountMin, RayCountMax);
        rayMaxSteps = Mathf.Clamp(rayMaxSteps, RayStepsMin, RayStepsMax);
        thickness = Mathf.Clamp(thickness, ThicknessMin, ThicknessMax);
        jitter = Mathf.Clamp01(jitter);
        smoothing = Mathf.Clamp(smoothing, SmoothingMin, SmoothingMax);
        fallbackIntensity = Mathf.Clamp(fallbackIntensity, FallbackIntensityMin, FallbackIntensityMax);
        emitterIntensity = Mathf.Clamp(emitterIntensity, EmitterIntensityMin, EmitterIntensityMax);
        bounces = Mathf.Clamp(bounces, BouncesMin, BouncesMax);
        rayTracedLightIntensity = Mathf.Clamp(rayTracedLightIntensity, LightIntensityMin, LightIntensityMax);
        rayTracedSkinnedBudget = Mathf.Clamp(rayTracedSkinnedBudget, SkinnedBudgetMin, SkinnedBudgetMax);
        rayTracedSkinnedInterval = Mathf.Clamp(rayTracedSkinnedInterval, SkinnedIntervalMin, SkinnedIntervalMax);
        rayTracedSkinnedDistance = Mathf.Clamp(rayTracedSkinnedDistance, SkinnedDistanceMin, SkinnedDistanceMax);
        rayTracedRescanInterval = Mathf.Clamp(rayTracedRescanInterval, RescanIntervalMin, RescanIntervalMax);
        rayTracedNormalBias = Mathf.Clamp(rayTracedNormalBias, RayTracedNormalBiasMin, RayTracedNormalBiasMax);
        specularIntensity = Mathf.Clamp(specularIntensity, IntensityMin, IntensityMax);
        specularMaxRoughness = Mathf.Clamp(specularMaxRoughness, SpecularRoughnessMin, SpecularRoughnessMax);
        specularRayLength = Mathf.Clamp(specularRayLength, RayLengthMin, SpecularRayLengthMax);
        specularFadeDistance = Mathf.Max(1f, specularFadeDistance);
        specularBounces = Mathf.Clamp(specularBounces, BouncesMin, BouncesMax);
        temporalResponse = Mathf.Clamp(temporalResponse, TemporalResponseMin, TemporalResponseMax);
        depthRejection = Mathf.Clamp(depthRejection, 0.005f, 1f);
        fireflyClamp = Mathf.Clamp(fireflyClamp, FireflyClampMin, FireflyClampMax);
    }

    /// <summary>Copies every value from <paramref name="other"/>. For save/restore around a capture.</summary>
    public void CopyFrom(BasisGlobalIlluminationSettings other)
    {
        if (other == null) { return; }
        enable = other.enable; mode = other.mode;
        intensity = other.intensity; saturation = other.saturation; tint = other.tint;
        obscuranceIntensity = other.obscuranceIntensity; obscuranceRadius = other.obscuranceRadius;
        maxRayLength = other.maxRayLength; fadeDistance = other.fadeDistance;
        quality = other.quality; overrideQualityCounts = other.overrideQualityCounts;
        rayCount = other.rayCount; rayMaxSteps = other.rayMaxSteps; thickness = other.thickness;
        hierarchicalMarch = other.hierarchicalMarch; jitter = other.jitter; smoothing = other.smoothing;
        wideBlur = other.wideBlur; normalSource = other.normalSource;
        fallback = other.fallback; fallbackIntensity = other.fallbackIntensity; rayReuse = other.rayReuse;
        emitters = other.emitters; emitterIntensity = other.emitterIntensity; emitterOcclusion = other.emitterOcclusion;
        bounces = other.bounces; rayTracedLights = other.rayTracedLights;
        rayTracedLightIntensity = other.rayTracedLightIntensity; rayTracedShadows = other.rayTracedShadows;
        rayTracedEmissiveSurfaces = other.rayTracedEmissiveSurfaces; respectBakedEmission = other.respectBakedEmission;
        rayTracedTextureAlbedo = other.rayTracedTextureAlbedo; rayTracedSkinnedMeshes = other.rayTracedSkinnedMeshes;
        rayTracedSkinnedBudget = other.rayTracedSkinnedBudget; rayTracedSkinnedInterval = other.rayTracedSkinnedInterval;
        rayTracedSkinnedDistance = other.rayTracedSkinnedDistance; rayTracedLayerMask = other.rayTracedLayerMask;
        rayTracedShadowCastersOnly = other.rayTracedShadowCastersOnly;
        rayTracedRescanInterval = other.rayTracedRescanInterval; rayTracedNormalBias = other.rayTracedNormalBias;
        specular = other.specular; specularIntensity = other.specularIntensity;
        specularMaxRoughness = other.specularMaxRoughness; specularRayLength = other.specularRayLength;
        specularFadeDistance = other.specularFadeDistance; specularBounces = other.specularBounces;
        specularTemporal = other.specularTemporal;
        resolution = other.resolution; temporalFilter = other.temporalFilter; temporalResponse = other.temporalResponse;
        motionVectors = other.motionVectors; depthRejection = other.depthRejection;
        neighbourhoodClamp = other.neighbourhoodClamp; fireflyClamp = other.fireflyClamp;
        bilateralUpsample = other.bilateralUpsample;
    }

    public BasisGlobalIlluminationSettings Clone()
    {
        BasisGlobalIlluminationSettings copy = new BasisGlobalIlluminationSettings();
        copy.CopyFrom(this);
        return copy;
    }

    /// <summary>The diffuse gather. Intensity 0 has always meant off, and still does.</summary>
    public bool DiffuseActive() => enable && intensity > 0f;

    /// <summary>
    /// Ray traced reflections. Whether the backend can actually serve them is a separate question the
    /// feature answers - this is only what was asked for.
    /// </summary>
    public bool SpecularActive() => enable && specular && specularIntensity > 0f;

    public bool IsActive() => DiffuseActive() || SpecularActive();

    public int ResolvedRayCount()
    {
        if (overrideQualityCounts) { return rayCount; }
        switch (quality)
        {
            case BasisGlobalIlluminationQuality.Low: return 1;
            case BasisGlobalIlluminationQuality.High: return 4;
            case BasisGlobalIlluminationQuality.Ultra: return 8;
            default: return 2;
        }
    }

    public int ResolvedRaySteps()
    {
        if (overrideQualityCounts) { return rayMaxSteps; }
        switch (quality)
        {
            case BasisGlobalIlluminationQuality.Low: return 12;
            case BasisGlobalIlluminationQuality.High: return 32;
            case BasisGlobalIlluminationQuality.Ultra: return 48;
            default: return 20;
        }
    }

    public int ResolvedMaxEmitters()
    {
        switch (quality)
        {
            case BasisGlobalIlluminationQuality.Low: return 4;
            case BasisGlobalIlluminationQuality.High: return 24;
            case BasisGlobalIlluminationQuality.Ultra: return 48;
            default: return 12;
        }
    }

    public int ResolvedResolutionDivisor()
    {
        int divisor = (int)resolution;
        return divisor < 1 ? 1 : divisor;
    }

    public bool IsRayTraced() => mode == BasisGlobalIlluminationMode.RayTraced;

    private static int interfaceFilteredLayers;
    private static bool interfaceFilteredLayersResolved;

    /// <summary>
    /// Everything except the UI layers. A menu panel in the acceleration structure bounces its own
    /// brightness onto the room and casts a shadow from a surface the player reads as an overlay.
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
    /// A second bounce doubles the ray budget, so the quality ladder owns it unless the counts were
    /// explicitly taken over.
    /// </summary>
    public int ResolvedBounces()
    {
        if (overrideQualityCounts) { return Mathf.Clamp(bounces, BouncesMin, BouncesMax); }
        switch (quality)
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
        if (!rayTracedLights) { return 0; }
        switch (quality)
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
        if (!rayTracedLights) { return 1; }
        switch (quality)
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
            shadowCastersOnly = rayTracedShadowCastersOnly,
            rescanInterval = rayTracedRescanInterval,
            skinnedMode = rayTracedSkinnedMeshes,
            skinnedBakesPerFrame = rayTracedSkinnedBudget,
            skinnedBakeInterval = rayTracedSkinnedInterval,
            skinnedMaxDistance = rayTracedSkinnedDistance,
            textureAlbedo = rayTracedTextureAlbedo,
            emissiveSurfaces = rayTracedEmissiveSurfaces,
            respectBakedEmission = respectBakedEmission
        };
    }

    public BasisGlobalIlluminationRayLightSettings ResolvedLightSettings()
    {
        return new BasisGlobalIlluminationRayLightSettings
        {
            layerMask = ResolvedTraceLayers(),
            limit = ResolvedRayTracedLightLimit(),
            shadowRays = rayTracedShadows,
            emitters = emitters,
            emitterIntensity = emitterIntensity,
            rescanInterval = rayTracedRescanInterval
        };
    }
}
