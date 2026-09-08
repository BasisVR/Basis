#ifndef DEPTH_AWARE_UPSAMPLE_INCLUDED
#define DEPTH_AWARE_UPSAMPLE_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
#include "./DeclareDownsampledDepthTexture.hlsl"
#include "./ProjectionUtils.hlsl"

// Upsamples the given texture using both the downsampled and full resolution depth information.
float4 DepthAwareUpsample(float2 uv, TEXTURE2D_X(textureToUpsample))
{
    float2 downsampledTexelSize = _DownsampledCameraDepthTexture_TexelSize.xy;
    float2 downsampledTopLeftCornerUv = uv - (downsampledTexelSize * 0.5);
    float2 uvs[4] =
    {
        downsampledTopLeftCornerUv + float2(0.0, downsampledTexelSize.y),
        downsampledTopLeftCornerUv + downsampledTexelSize.xy,
        downsampledTopLeftCornerUv + float2(downsampledTexelSize.x, 0.0),
        downsampledTopLeftCornerUv
    };

    float4 downsampledDepths;
    
#if SHADER_TARGET >= 45
    downsampledDepths = GATHER_RED_TEXTURE2D_X(_DownsampledCameraDepthTexture, sampler_PointClamp, uv);
#else
    downsampledDepths.x = SampleDownsampledSceneDepth(uvs[0]);
    downsampledDepths.y = SampleDownsampledSceneDepth(uvs[1]);
    downsampledDepths.z = SampleDownsampledSceneDepth(uvs[2]);
    downsampledDepths.w = SampleDownsampledSceneDepth(uvs[3]);
#endif

    float fullResDepth = SampleSceneDepth(uv);
    float fullResLinearEyeDepth = LinearEyeDepthConsiderProjection(fullResDepth);
    float relativeDepthThreshold = fullResLinearEyeDepth * 0.1;

    float4 linearEyeDepths;
    linearEyeDepths.x = LinearEyeDepthConsiderProjection(downsampledDepths.x);
    linearEyeDepths.y = LinearEyeDepthConsiderProjection(downsampledDepths.y);
    linearEyeDepths.z = LinearEyeDepthConsiderProjection(downsampledDepths.z);
    linearEyeDepths.w = LinearEyeDepthConsiderProjection(downsampledDepths.w);

    float4 linearEyeDepthDists = abs(fullResLinearEyeDepth - linearEyeDepths);

    float minLinearEyeDepthDist = linearEyeDepthDists.x;
    float2 nearestUv = uvs[0];
    int numValidDepths = linearEyeDepthDists.x < relativeDepthThreshold;

    UNITY_UNROLL
    for (int i = 1; i < 4; ++i)
    {
        bool updateNearest = linearEyeDepthDists[i] < minLinearEyeDepthDist;
        minLinearEyeDepthDist = updateNearest ? linearEyeDepthDists[i] : minLinearEyeDepthDist;
        nearestUv = updateNearest ? uvs[i] : nearestUv;

        numValidDepths += (linearEyeDepthDists[i] < relativeDepthThreshold);
    }

    UNITY_BRANCH
    if (numValidDepths == 4)
        return SAMPLE_TEXTURE2D_X_LOD(textureToUpsample, sampler_LinearClamp, uv, 0.0);

    // At depth discontinuities, blend the four low-res taps by bilinear weight times depth similarity
    // instead of point-picking the single nearest-depth tap, so silhouettes get a smooth fog edge
    // rather than a low-res stair-step. Falls back to the nearest tap when no tap is depth-compatible.
    float2 f = frac(uv / downsampledTexelSize - 0.5);
    float4 bilinearWeights = float4(
        (1.0 - f.x) * f.y,
        f.x * f.y,
        f.x * (1.0 - f.y),
        (1.0 - f.x) * (1.0 - f.y));

    float4 normalizedDists = linearEyeDepthDists / relativeDepthThreshold;
    float4 weights = bilinearWeights * exp2(-normalizedDists * normalizedDists);
    float totalWeight = dot(weights, float4(1.0, 1.0, 1.0, 1.0));

    UNITY_BRANCH
    if (totalWeight < 1e-4)
        return SAMPLE_TEXTURE2D_X_LOD(textureToUpsample, sampler_PointClamp, nearestUv, 0.0);

    float4 result = SAMPLE_TEXTURE2D_X_LOD(textureToUpsample, sampler_PointClamp, uvs[0], 0.0) * weights.x
                  + SAMPLE_TEXTURE2D_X_LOD(textureToUpsample, sampler_PointClamp, uvs[1], 0.0) * weights.y
                  + SAMPLE_TEXTURE2D_X_LOD(textureToUpsample, sampler_PointClamp, uvs[2], 0.0) * weights.z
                  + SAMPLE_TEXTURE2D_X_LOD(textureToUpsample, sampler_PointClamp, uvs[3], 0.0) * weights.w;

    return result * rcp(totalWeight);
}

#endif