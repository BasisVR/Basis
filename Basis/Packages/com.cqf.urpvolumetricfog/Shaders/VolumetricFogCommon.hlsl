#ifndef VOLUMETRIC_FOG_COMMON_INCLUDED
#define VOLUMETRIC_FOG_COMMON_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Macros.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Random.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/GlobalSamplers.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/VolumeRendering.hlsl"
#if UNITY_VERSION >= 202310 && (defined(PROBE_VOLUMES_L1) || defined(PROBE_VOLUMES_L2))
    #include "Packages/com.unity.render-pipelines.core/Runtime/Lighting/ProbeVolume/ProbeVolume.hlsl"
#endif
#include "./VolumetricShadows.hlsl"
#include "./ProjectionUtils.hlsl"

// Optional LTCGI (area lights: screens, video) integration. Off by default so the fog compiles
// standalone when the LTCGI package is absent. To enable it, install the LTCGI package
// (at.pimaker.ltcgi) and uncomment the define below.
// #define VF_LTCGI

#if defined(VF_LTCGI) && !defined(SHADER_STAGE_COMPUTE)
    #define VF_LTCGI_ACTIVE
    #define LTCGI_ALWAYS_LTC_DIFFUSE
    #define LTCGI_SPECULAR_OFF
    #include "Packages/at.pimaker.ltcgi/Shaders/LTCGI_URP_Compat.hlsl"
    #include "Packages/at.pimaker.ltcgi/Shaders/LTCGI.cginc"
#endif

// Stop marching once the medium is dense enough that the remaining steps are imperceptible.
#define VF_MIN_TRANSMITTANCE 0.003
// Distance-adaptive stepping: ratio of the last (far) to the first (near) step length. Steps are
// distributed geometrically so samples concentrate near the camera, where detail matters most.
#define VF_STEP_GROWTH_RATIO 8.0
// Skip steps whose density falls below this fraction of the configured maximum; the faint top-of-band
// sliver they cover is imperceptible but still pays for shadow, APV and LTCGI sampling.
#define VF_MIN_DENSITY_FRACTION 0.01

float _Distance;
float _BaseHeight;
float _MaximumHeight;
float _GroundHeight;
float _Density;
float _Absortion;
float _APVContributionWeight;
float3 _Tint;
int _MaxSteps;

float _MainLightAnisotropy;
float _MainLightScattering;

float _VFAdditionalAnisotropy;
float _VFAdditionalScattering;

float _VFAnalyticOpticalDepth;
float _VFSunTrims;

#if defined(VF_LTCGI_ACTIVE)
float _LTCGIScattering;
#endif

// Blue-noise texture for raymarch jitter.
TEXTURE2D(_BlueNoiseTexture);
float4 _BlueNoiseParams; // xy = 1 / textureSize, zw = per-frame scroll offset

// Gets the fog density at the given world height.
float GetFogDensity(float posWSy, float invHeightRange)
{
    float t = saturate((posWSy - _BaseHeight) * invHeightRange);
    t = 1.0 - t;
    t = lerp(t, 0.0, posWSy < _GroundHeight);

    return _Density * t;
}

// Baked APV in-scatter volume: a world-space 3D texture of pre-integrated APV irradiance (RGB). When
// _APV_BAKED is set, the per-step APV evaluation becomes a single trilinear tap into this texture
// instead of a full SH reconstruction - and it needs no live APV (PROBE_VOLUMES) data, so it works
// even with Unity's APV runtime disabled.
TEXTURE3D(_BakedAPVFogVolume);
float3 _BakedAPVVolumeBoundsMin;   // world-space min corner of the baked volume
float3 _BakedAPVVolumeInvSize;     // 1 / world-space size; maps a world position into [0,1] uvw

real3 EvaluateBakedAPV(float3 posWS)
{
    float3 uvw = (posWS - _BakedAPVVolumeBoundsMin) * _BakedAPVVolumeInvSize;
    float4 baked = SAMPLE_TEXTURE3D_LOD(_BakedAPVFogVolume, sampler_LinearClamp, uvw, 0.0);
    return baked.rgb * (baked.a * _APVContributionWeight);
}

real3 EvaluateLiveAPV(float2 positionSS, float3 posWS)
{
    float3 apvDiffuseGI = float3(0.0, 0.0, 0.0);
#if UNITY_VERSION >= 202310 && (defined(PROBE_VOLUMES_L1) || defined(PROBE_VOLUMES_L2))
    EvaluateAdaptiveProbeVolume(posWS, positionSS, apvDiffuseGI);
    apvDiffuseGI *= _APVContributionWeight;
#endif
    return apvDiffuseGI;
}

// Evaluates the adaptive probe volume irradiance (weighted, WITHOUT density) at a world position.
// Keyword driven: used by the fragment raymarch.
real3 EvaluateWeightedAPV(float2 positionSS, float3 posWS)
{
#if _APV_CONTRIBUTION_ENABLED
    #if _APV_BAKED
        return EvaluateBakedAPV(posWS);
    #else
        return EvaluateLiveAPV(positionSS, posWS);
    #endif
#else
    return real3(0.0, 0.0, 0.0);
#endif
}

// Same evaluation driven by uniform flags: used by the froxel kernels, which keep their variant count down.
real3 EvaluateWeightedAPVDynamic(float2 positionSS, float3 posWS, bool enabled, bool baked)
{
    UNITY_BRANCH
    if (!enabled)
        return real3(0.0, 0.0, 0.0);
    UNITY_BRANCH
    if (baked)
        return EvaluateBakedAPV(posWS);
    return EvaluateLiveAPV(positionSS, posWS);
}

// Gets the main light color at one sample, using the per-ray hoisted constant term
// (light colour * tint * phase * scattering). Only shadow, cookie and density vary per sample.
real3 GetStepMainLightColor(float3 currPosWS, real3 mainLightConst, float density)
{
#if _MAIN_LIGHT_CONTRIBUTION_DISABLED
    return real3(0.0, 0.0, 0.0);
#else
    float4 shadowCoord = TransformWorldToShadowCoord(currPosWS);
    real shadow = VolumetricMainLightRealtimeShadow(shadowCoord);
    real3 color = mainLightConst * (shadow * density);
#if _LIGHT_COOKIES
    color *= SampleMainLightCookie(currPosWS);
#endif
    return color;
#endif
}

// Gets the accumulated color from LTCGI area lights (screens, video) at one raymarch step.
real3 GetStepLTCGIColor(float3 currPosWS, float3 viewToCam, float density)
{
#if defined(VF_LTCGI_ACTIVE)
    UNITY_BRANCH
    if (_LTCGIScattering <= 0.0)
        return real3(0.0, 0.0, 0.0);

    // Fog has no surface normal, so a single normal hard-clips every screen in the opposite
    // hemisphere to zero (the "only lit when looking away from the screen" artifact). Evaluate
    // both hemispheres around the view ray and sum so screens light the fog from any view angle.
    // The view dir handed to LTCGI must be perpendicular to the normal or its tangent basis is
    // normalize(0) = NaN.
    float3 ltcgiN = -viewToCam;
    float3 ltcgiUp = abs(ltcgiN.y) < 0.999 ? float3(0.0, 1.0, 0.0) : float3(1.0, 0.0, 0.0);
    float3 ltcgiView = normalize(cross(ltcgiUp, ltcgiN));

    half3 diffuse = half3(0.0, 0.0, 0.0);
    LTCGI_Contribution(currPosWS,  ltcgiN, ltcgiView, 1.0, float2(0.0, 0.0), diffuse);
    LTCGI_Contribution(currPosWS, -ltcgiN, ltcgiView, 1.0, float2(0.0, 0.0), diffuse);
    return diffuse * (_LTCGIScattering * density);
#else
    return real3(0.0, 0.0, 0.0);
#endif
}

#endif
