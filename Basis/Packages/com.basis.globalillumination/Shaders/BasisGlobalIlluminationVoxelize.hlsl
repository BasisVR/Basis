#ifndef BASIS_GLOBAL_ILLUMINATION_VOXELIZE_INCLUDED
#define BASIS_GLOBAL_ILLUMINATION_VOXELIZE_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

float4x4 _BasisGIVoxViewProj;
float4 _BasisGIVoxLevel;
float4 _BasisGIVoxParams;

RWTexture3D<float4> _BasisGIVoxAlbedoWrite : register(u1);
RWTexture3D<float4> _BasisGIVoxNormalWrite : register(u2);
RWTexture3D<uint> _BasisGIVoxFacesWrite : register(u3);

struct VoxelAttributes
{
    float4 positionOS : POSITION;
    float3 normalOS : NORMAL;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct VoxelVaryings
{
    float4 positionCS : SV_POSITION;
    float3 positionWS : TEXCOORD0;
    float3 normalWS : TEXCOORD1;
};

VoxelVaryings VoxelVert(VoxelAttributes input)
{
    VoxelVaryings output;
    UNITY_SETUP_INSTANCE_ID(input);
    output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
    output.positionCS = mul(_BasisGIVoxViewProj, float4(output.positionWS, 1.0));
    output.normalWS = TransformObjectToWorldNormal(input.normalOS);
    return output;
}

#define BASISGI_VOXEL_UNSEEN_ALBEDO 0.5

float VoxelFrag(VoxelVaryings input) : SV_Target
{
    float voxels = _BasisGIVoxParams.x;
    float3 local = (input.positionWS - _BasisGIVoxLevel.xyz) / _BasisGIVoxLevel.w;
    if (any(local < 0.0) || any(local >= voxels)) { return 0.0; }
    int3 voxel = (int3)floor(local);
    voxel.z += (int)_BasisGIVoxParams.y;

    float3 normal = normalize(input.normalWS);
    _BasisGIVoxAlbedoWrite[voxel] = float4(BASISGI_VOXEL_UNSEEN_ALBEDO.xxx, 1.0);
    _BasisGIVoxNormalWrite[voxel] = float4(normal * 0.5 + 0.5, 1.0);
    uint faces = (normal.x > 0.3 ? 1u : 0u) | (normal.x < -0.3 ? 2u : 0u) | (normal.y > 0.3 ? 4u : 0u) | (normal.y < -0.3 ? 8u : 0u) | (normal.z > 0.3 ? 16u : 0u) | (normal.z < -0.3 ? 32u : 0u);
    InterlockedOr(_BasisGIVoxFacesWrite[voxel], faces);
    return 0.0;
}

#endif
