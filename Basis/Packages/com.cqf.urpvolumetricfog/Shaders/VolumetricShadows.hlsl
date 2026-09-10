
#ifndef VOLUMETRIC_SHADOWS_INCLUDED
#define VOLUMETRIC_SHADOWS_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

// Copied and modified from SampleShadowmap from Shadows.hlsl. 
real VolumetricSampleShadowmap(TEXTURE2D_SHADOW_PARAM( ShadowMap, sampler_ShadowMap), float4 shadowCoord,ShadowSamplingData ShadowSamplingDatasamplingData,half4 shadowParams, bool isPerspectiveProjection = true)
{
    if (isPerspectiveProjection)
        shadowCoord.xyz /= max(0.00001, shadowCoord.w);

real attenuation = real(SAMPLE_TEXTURE2D_SHADOW(ShadowMap, sampler_ShadowMap, shadowCoord.xyz));
real shadowStrength = shadowParams.x;

    attenuation = LerpWhiteTo(attenuation, shadowStrength);
                
    return BEYOND_SHADOW_FAR(shadowCoord) ? 1.0 :
attenuation;
}

// Copied and modified from MainLightRealTimeShadow from Shadows.hlsl. 
half VolumetricMainLightRealtimeShadow(float4 shadowCoord)
{
#if !defined(MAIN_LIGHT_CALCULATE_SHADOWS)
    return half(1.0);
#elif defined(_MAIN_LIGHT_SHADOWS_SCREEN) && !defined(_SURFACE_TYPE_TRANSPARENT)
    return SampleScreenSpaceShadowmap(shadowCoord);
#else
    ShadowSamplingData shadowSamplingData = GetMainLightShadowSamplingData();
    half4 shadowParams = GetMainLightShadowParams();
    return VolumetricSampleShadowmap(TEXTURE2D_ARGS(_MainLightShadowmapTexture, sampler_LinearClampCompare), shadowCoord, shadowSamplingData, shadowParams, false);
#endif
}
float4 VolumetricShadowCoord(float3 positionWS, bool reuseCascade, inout half cascadeIndex, inout float3 cascadeCenter, inout float cascadeRadius2)
{
#if defined(_MAIN_LIGHT_SHADOWS_CASCADE)
    float3 fromCenter = positionWS - cascadeCenter;
    UNITY_BRANCH
    if (!reuseCascade || dot(fromCenter, fromCenter) >= cascadeRadius2)
    {
        cascadeIndex = ComputeCascadeIndex(positionWS);
        float4 sphere = cascadeIndex < 0.5 ? _CascadeShadowSplitSpheres0 : (cascadeIndex < 1.5 ? _CascadeShadowSplitSpheres1 : (cascadeIndex < 2.5 ? _CascadeShadowSplitSpheres2 : _CascadeShadowSplitSpheres3));
        float radius2 = cascadeIndex < 0.5 ? _CascadeShadowSplitSphereRadii.x : (cascadeIndex < 1.5 ? _CascadeShadowSplitSphereRadii.y : (cascadeIndex < 2.5 ? _CascadeShadowSplitSphereRadii.z : _CascadeShadowSplitSphereRadii.w));
        cascadeCenter = sphere.xyz;
        cascadeRadius2 = cascadeIndex > 3.5 ? FLT_MAX : radius2;
    }
#else
    cascadeIndex = half(0.0);
#endif
    return float4(mul(_MainLightWorldToShadow[cascadeIndex], float4(positionWS, 1.0)).xyz, 0.0);
}
#endif