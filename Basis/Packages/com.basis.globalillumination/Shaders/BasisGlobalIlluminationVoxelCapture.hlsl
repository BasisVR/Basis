#ifndef BASIS_GLOBAL_ILLUMINATION_VOXEL_CAPTURE_INCLUDED
#define BASIS_GLOBAL_ILLUMINATION_VOXEL_CAPTURE_INCLUDED

#include "./BasisGlobalIlluminationCommon.hlsl"
#include "./BasisGlobalIlluminationWorld.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"

RWTexture3D<uint> _BasisGIVoxSeenWrite : register(u1);
RWTexture3D<float> _BasisGIVoxSeenWeightWrite : register(u2);
RWTexture3D<uint> _BasisGIVoxSeenAlbedoWrite : register(u3);

#define BASISGI_VOXEL_CAPTURE_BLEND 0.25
#define BASISGI_VOXEL_CAPTURE_SECONDS 0.1

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
    float voxels = _BasisGIWorldParams.y;
    float3 nudged = positionWS + toCamera * box.w * 0.25;
    float3 local = (nudged - box.xyz) / box.w;
    if (any(local < 0.0) || any(local >= voxels)) { return 0.0; }
    if (_BasisGIVoxAlbedo.Load(int4((int3)floor(local) + int3(0, 0, level * (int)voxels), 0)).a < 0.004)
    {
        float3 behind = (positionWS - toCamera * box.w * 0.25 - box.xyz) / box.w;
        if (all(behind >= 0.0) && all(behind < voxels) && _BasisGIVoxAlbedo.Load(int4((int3)floor(behind) + int3(0, 0, level * (int)voxels), 0)).a >= 0.004)
        {
            nudged = positionWS - toCamera * box.w * 0.25;
        }
    }
    int3 voxel = BasisGIWorldSeenSlot(nudged, level);

    float capture = min(BASISGI_VOXEL_CAPTURE_BLEND, 1.0 - exp(-unity_DeltaTime.x / BASISGI_VOXEL_CAPTURE_SECONDS));
    float3 colour = max(0.0, BasisGISampleSceneColor(uv));
    float weight = _BasisGIVoxSeenWeightWrite[voxel];
    float3 previous = UnpackFromR11G11B10f(_BasisGIVoxSeenWrite[voxel]);
    float3 blended = weight > 0.05 ? lerp(previous, colour, capture) : colour;
    _BasisGIVoxSeenWrite[voxel] = PackToR11G11B10f(min(blended, 60000.0));
    _BasisGIVoxSeenWeightWrite[voxel] = 1.0;

    if (_BasisGISurfaceValid > 0.5)
    {
        float4 surface = SAMPLE_TEXTURE2D_X_LOD(_BasisGISurface, sampler_PointClamp, UnityStereoTransformScreenSpaceTex(uv), 0);
        if (surface.a < 0.5)
        {
            uint known = _BasisGIVoxSeenAlbedoWrite[voxel];
            float3 albedo = known != 0u ? lerp(UnpackFromR11G11B10f(known), surface.rgb, capture) : surface.rgb;
            _BasisGIVoxSeenAlbedoWrite[voxel] = PackToR11G11B10f(max(albedo, 1.0 / 255.0));
        }
    }
    return 0.0;
}

#endif
