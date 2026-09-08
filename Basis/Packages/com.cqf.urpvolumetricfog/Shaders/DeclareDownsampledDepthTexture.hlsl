#ifndef DECLARE_DOWNSAMPLED_DEPTH_TEXTURE_INCLUDED
#define DECLARE_DOWNSAMPLED_DEPTH_TEXTURE_INCLUDED

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/GlobalSamplers.hlsl"

TEXTURE2D_X_FLOAT(_DownsampledCameraDepthTexture);
float4 _DownsampledCameraDepthTexture_TexelSize;

// Samples the downsampled camera depth texture.
float SampleDownsampledSceneDepth(float2 uv)
{
    return SAMPLE_TEXTURE2D_X_LOD(_DownsampledCameraDepthTexture, sampler_PointClamp, uv, 0.0).r;
}

#endif