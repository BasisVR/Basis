#ifndef VOLUMETRIC_FOG_TEMPORAL_INCLUDED
#define VOLUMETRIC_FOG_TEMPORAL_INCLUDED

#include "./DeclareDownsampledDepthTexture.hlsl"
#include "./ProjectionUtils.hlsl"

#define VF_TEMPORAL_FEEDBACK 0.9
#define VF_TEMPORAL_DEPTH_TOLERANCE 0.1

TEXTURE2D_X(_VFFogHistory);
TEXTURE2D_X_FLOAT(_VFDepthHistory);
float4x4 _VFPrevViewProj[2];
float4 _VFTemporalParams; // x = history valid

struct TemporalOutput
{
    float4 resolved : SV_Target0;
    float4 resolvedCopy : SV_Target1;
    float depth : SV_Target2;
};

// Blends the jittered fog of this frame with the reprojected history. Reads the jittered fog from
// _BlitTexture and the depth the rays used from _DownsampledCameraDepthTexture. Writes the resolved fog
// twice (history for next frame, working copy for the blur and composite) plus the linear depth used
// to reject history next frame.
TemporalOutput VolumetricFogTemporalResolve(float2 uv, float2 positionCS)
{
    int2 pixel = int2(positionCS);
    float4 current = LOAD_TEXTURE2D_X(_BlitTexture, pixel);
    float rawDepth = SampleDownsampledSceneDepth(uv);
    float linearDepth = LinearEyeDepth(rawDepth, _ZBufferParams);
#if !UNITY_REVERSED_Z
    rawDepth = lerp(UNITY_NEAR_CLIP_VALUE, 1.0, rawDepth);
#endif
    float3 posWS = ComputeWorldSpacePosition(uv, rawDepth, UNITY_MATRIX_I_VP);

    float4x4 prevViewProj = _VFPrevViewProj[unity_StereoEyeIndex];
    float4 prevClip = mul(prevViewProj, float4(posWS, 1.0));
    float2 prevUV = ComputeNormalizedDeviceCoordinates(posWS, prevViewProj);
    bool valid = _VFTemporalParams.x > 0.5 && prevClip.w > 0.0 && all(prevUV >= 0.0) && all(prevUV <= 1.0);

    float4 resolved = current;

    UNITY_BRANCH
    if (valid)
    {
        float historyDepth = SAMPLE_TEXTURE2D_X_LOD(_VFDepthHistory, sampler_LinearClamp, prevUV, 0.0).r;
        float4 history = SAMPLE_TEXTURE2D_X_LOD(_VFFogHistory, sampler_LinearClamp, prevUV, 0.0);

        int2 maxPixel = int2(_BlitTexture_TexelSize.zw) - 1;
        float4 boxMin = current;
        float4 boxMax = current;

        UNITY_UNROLL
        for (int y = -1; y <= 1; ++y)
        {
            UNITY_UNROLL
            for (int x = -1; x <= 1; ++x)
            {
                if (x == 0 && y == 0)
                    continue;
                float4 neighbor = LOAD_TEXTURE2D_X(_BlitTexture, clamp(pixel + int2(x, y), int2(0, 0), maxPixel));
                boxMin = min(boxMin, neighbor);
                boxMax = max(boxMax, neighbor);
            }
        }

        history = clamp(history, boxMin, boxMax);
        float depthWeight = 1.0 - saturate(abs(historyDepth - prevClip.w) / (VF_TEMPORAL_DEPTH_TOLERANCE * prevClip.w));
        resolved = lerp(current, history, VF_TEMPORAL_FEEDBACK * depthWeight);
    }

    TemporalOutput output;
    output.resolved = resolved;
    output.resolvedCopy = resolved;
    output.depth = linearDepth;
    return output;
}

#endif
