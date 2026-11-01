#ifndef BASIS_GLOBAL_ILLUMINATION_VOXEL_CAPTURE_INCLUDED
#define BASIS_GLOBAL_ILLUMINATION_VOXEL_CAPTURE_INCLUDED

#include "./BasisGlobalIlluminationCommon.hlsl"
#include "./BasisGlobalIlluminationWorld.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"

RWTexture3D<uint> _BasisGIVoxSeenWrite : register(u1);
RWTexture3D<float> _BasisGIVoxSeenWeightWrite : register(u2);

#define BASISGI_VOXEL_CAPTURE_BLEND 0.25

float BasisGIVoxelCapture(float2 uv)
{
    if (!BasisGIWorldValid()) { return 0.0; }
    float rawDepth = BasisGISampleRawDepth(uv);
    if (BasisGIIsSky(rawDepth)) { return 0.0; }

    float3 positionWS = BasisGIWorldPosition(uv, rawDepth);
    float3 toCamera = normalize(UNITY_MATRIX_I_V._m03_m13_m23 - positionWS);
    int level = BasisGIWorldLevelAt(positionWS, 0.25);
    if (level < 0) { return 0.0; }
    float4 box = _BasisGIWorldLevel[level];
    float3 nudged = positionWS + toCamera * box.w * 0.25;
    float3 local = (nudged - box.xyz) / box.w;
    float voxels = _BasisGIWorldParams.y;
    if (any(local < 0.0) || any(local >= voxels)) { return 0.0; }
    int3 voxel = BasisGIWorldSeenSlot(nudged, level);

    float3 colour = max(0.0, BasisGISampleSceneColor(uv));
    float weight = _BasisGIVoxSeenWeightWrite[voxel];
    float3 previous = UnpackFromR11G11B10f(_BasisGIVoxSeenWrite[voxel]);
    float3 blended = weight > 0.05 ? lerp(previous, colour, BASISGI_VOXEL_CAPTURE_BLEND) : colour;
    _BasisGIVoxSeenWrite[voxel] = PackToR11G11B10f(min(blended, 60000.0));
    _BasisGIVoxSeenWeightWrite[voxel] = 1.0;
    return 0.0;
}

#endif
