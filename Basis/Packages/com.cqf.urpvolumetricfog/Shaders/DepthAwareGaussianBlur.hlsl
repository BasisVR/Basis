#ifndef DEPTH_AWARE_GAUSSIAN_BLUR_INCLUDED
#define DEPTH_AWARE_GAUSSIAN_BLUR_INCLUDED

#include "./DeclareDownsampledDepthTexture.hlsl"
#include "./ProjectionUtils.hlsl"

#define BLUR_DEPTH_FALLOFF 0.5

// 9-tap radius-4 Gaussian folded into 5 bilinear fetches: each side tap lands between two texels so one
// hardware-filtered sample covers a pair. Offsets are the per-pair weighted centroids, BlurTapWeights the
// summed pair weights, of the original kernel { 0.2026, 0.1790, 0.1240, 0.0672, 0.0285 }.
#define BLUR_CENTER_WEIGHT 0.2026
static const float BlurTapOffsets[2] = { 1.40924, 3.29781 };
static const float BlurTapWeights[2] = { 0.30300, 0.09570 };

// Blurs the RGB channels of the given texture using depth aware gaussian blur, which uses the downsampled camera depth to apply weights to the blur.
// The alpha channel is not blurred so the original value is returned. Requires a bilinear sampler so the paired taps blend correctly.
float4 DepthAwareGaussianBlur(float2 uv, float2 dir, TEXTURE2D_X(textureToBlur), SAMPLER(sampler_TextureToBlur), float2 textureToBlurTexelSizeXy)
{
    float4 centerSample = SAMPLE_TEXTURE2D_X_LOD(textureToBlur, sampler_TextureToBlur, uv, 0.0);
    float centerLinearEyeDepth = LinearEyeDepthConsiderProjection(SampleDownsampledSceneDepth(uv));

    float3 rgbResult = centerSample.rgb * BLUR_CENTER_WEIGHT;
    float weights = BLUR_CENTER_WEIGHT;

    float2 texelSizeTimesDir = textureToBlurTexelSizeXy * dir;

    UNITY_UNROLL
    for (int i = 0; i < 2; ++i)
    {
        float2 uvOffset = BlurTapOffsets[i] * texelSizeTimesDir;

        UNITY_UNROLL
        for (int s = -1; s <= 1; s += 2)
        {
            float2 uvSample = uv + (float)s * uvOffset;

            float linearEyeDepth = LinearEyeDepthConsiderProjection(SampleDownsampledSceneDepth(uvSample));
            float depthDiff = abs(centerLinearEyeDepth - linearEyeDepth);
            float r2 = BLUR_DEPTH_FALLOFF * depthDiff;
            float g = exp(-r2 * r2);
            float weight = g * BlurTapWeights[i];

            rgbResult += SAMPLE_TEXTURE2D_X_LOD(textureToBlur, sampler_TextureToBlur, uvSample, 0.0).rgb * weight;
            weights += weight;
        }
    }

    return float4(rgbResult * rcp(weights), centerSample.a);
}

#endif