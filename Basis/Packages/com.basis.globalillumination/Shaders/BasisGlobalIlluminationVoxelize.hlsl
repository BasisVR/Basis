#ifndef BASIS_GLOBAL_ILLUMINATION_VOXELIZE_INCLUDED
#define BASIS_GLOBAL_ILLUMINATION_VOXELIZE_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

float4x4 _BasisGIVoxViewProj;
float4 _BasisGIVoxLevel;
float4 _BasisGIVoxParams;

RWTexture3D<float4> _BasisGIVoxAlbedoWrite : register(u1);
RWTexture3D<uint> _BasisGIVoxNormalWrite : register(u2);
RWTexture3D<uint> _BasisGIVoxFacesWrite : register(u3);

uint BasisGIVoxelPackNormal(float3 normal)
{
    uint3 quantized = (uint3)round(saturate(normal * 0.5 + 0.5) * 1023.0);
    return 0x80000000u | (quantized.x << 20) | (quantized.y << 10) | quantized.z;
}

uint BasisGIVoxelPackEmission(float3 emission, float3 within)
{
    float peak = max(emission.r, max(emission.g, emission.b));
    if (peak <= 1.0 / 65536.0) { return 0u; }
    float exponent = clamp(ceil(log2(peak)) + 15.0, 0.0, 31.0);
    float3 mantissa = saturate(emission / exp2(exponent - 15.0));
    uint3 cell = (uint3)(saturate(within) * 7.999);
    uint key = ((cell.x * 73856093u) ^ (cell.y * 19349663u) ^ (cell.z * 83492791u)) >> 7;
    return ((key & 15u) << 28) | ((uint)exponent << 23) | ((uint)round(mantissa.r * 255.0) << 15) | ((uint)round(mantissa.g * 255.0) << 7) | (uint)round(mantissa.b * 127.0);
}

TEXTURE2D(_EmissionMap);
SAMPLER(sampler_EmissionMap);
CBUFFER_START(UnityPerMaterial)
float4 _EmissionMap_ST;
float4 _EmissionColor;
CBUFFER_END

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

/// <summary>Where a vertex lands when its triangle is drawn down one axis of the level box: the same
/// projection VoxelAxisMatrix builds on the CPU, picked per triangle instead of per draw.</summary>
float4 BasisGIVoxelProject(float3 positionWS, int axis)
{
    float half = _BasisGIVoxLevel.w * _BasisGIVoxParams.x * 0.5;
    float3 local = (positionWS - (_BasisGIVoxLevel.xyz + half)) / half;
    float2 flat = axis == 0 ? local.zy : (axis == 1 ? local.xz : local.xy);
    return float4(flat, 0.5, 1.0);
}

/// <summary>The axis a triangle covers the most pixels down. Drawing every triangle down all three axes
/// was three passes over the whole scene; drawing each down the one it faces covers the same voxels in
/// one.</summary>
int BasisGIVoxelAxis(float3 a, float3 b, float3 c)
{
    float3 facing = abs(cross(b - a, c - a));
    return facing.x >= facing.y && facing.x >= facing.z ? 0 : (facing.y >= facing.z ? 1 : 2);
}

#if defined(BASISGI_VOXEL_GEOMETRY)
[maxvertexcount(3)]
void VoxelGeom(triangle VoxelVaryings input[3], inout TriangleStream<VoxelVaryings> stream)
{
    int axis = BasisGIVoxelAxis(input[0].positionWS, input[1].positionWS, input[2].positionWS);
    UNITY_UNROLL
    for (int corner = 0; corner < 3; corner++)
    {
        VoxelVaryings output = input[corner];
        output.positionCS = BasisGIVoxelProject(output.positionWS, axis);
        stream.Append(output);
    }
}
#endif

#define BASISGI_VOXEL_UNSEEN_ALBEDO 0.5

float VoxelFrag(VoxelVaryings input) : SV_Target
{
    float voxels = _BasisGIVoxParams.x;
    float3 local = (input.positionWS - _BasisGIVoxLevel.xyz) / _BasisGIVoxLevel.w;
    if (any(local < 0.0) || any(local >= voxels) || _EmissionMap_ST.x == -65504.0) { return 0.0; }
    int3 voxel = (int3)floor(local);
    voxel.z += (int)_BasisGIVoxParams.y;

    float3 normal = normalize(input.normalWS);
    _BasisGIVoxAlbedoWrite[voxel] = float4(BASISGI_VOXEL_UNSEEN_ALBEDO.xxx, 1.0);
    InterlockedMax(_BasisGIVoxNormalWrite[voxel], BasisGIVoxelPackNormal(normal));
    uint faces = (normal.x > 0.3 ? 1u : 0u) | (normal.x < -0.3 ? 2u : 0u) | (normal.y > 0.3 ? 4u : 0u) | (normal.y < -0.3 ? 8u : 0u) | (normal.z > 0.3 ? 16u : 0u) | (normal.z < -0.3 ? 32u : 0u);
    InterlockedOr(_BasisGIVoxFacesWrite[voxel], faces);
    return 0.0;
}

#if defined(BASISGI_VOXEL_EMISSION)
RWTexture3D<uint> _BasisGIVoxEmissionWrite : register(u4);

struct VoxelEmissionAttributes
{
    float4 positionOS : POSITION;
    float2 uv : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct VoxelEmissionVaryings
{
    float4 positionCS : SV_POSITION;
    float3 positionWS : TEXCOORD0;
    float2 uv : TEXCOORD1;
};

VoxelEmissionVaryings VoxelEmissionVert(VoxelEmissionAttributes input)
{
    VoxelEmissionVaryings output;
    UNITY_SETUP_INSTANCE_ID(input);
    output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
    output.positionCS = mul(_BasisGIVoxViewProj, float4(output.positionWS, 1.0));
    output.uv = input.uv;
    return output;
}

#if defined(BASISGI_VOXEL_GEOMETRY)
[maxvertexcount(3)]
void VoxelEmissionGeom(triangle VoxelEmissionVaryings input[3], inout TriangleStream<VoxelEmissionVaryings> stream)
{
    int axis = BasisGIVoxelAxis(input[0].positionWS, input[1].positionWS, input[2].positionWS);
    UNITY_UNROLL
    for (int corner = 0; corner < 3; corner++)
    {
        VoxelEmissionVaryings output = input[corner];
        output.positionCS = BasisGIVoxelProject(output.positionWS, axis);
        stream.Append(output);
    }
}
#endif

float VoxelEmissionFrag(VoxelEmissionVaryings input) : SV_Target
{
    float voxels = _BasisGIVoxParams.x;
    float3 local = (input.positionWS - _BasisGIVoxLevel.xyz) / _BasisGIVoxLevel.w;
    if (any(local < 0.0) || any(local >= voxels)) { return 0.0; }
    int3 voxel = (int3)floor(local);
    voxel.z += (int)_BasisGIVoxParams.y;
    float3 emission = max(0.0, _EmissionColor.rgb);
    if (any(_EmissionMap_ST.xy != 0.0)) { emission *= SAMPLE_TEXTURE2D(_EmissionMap, sampler_EmissionMap, input.uv * _EmissionMap_ST.xy + _EmissionMap_ST.zw).rgb; }
    uint encoded = BasisGIVoxelPackEmission(min(emission, 60000.0), frac(local));
    if (encoded != 0u) { InterlockedMax(_BasisGIVoxEmissionWrite[voxel], encoded); }
    return 0.0;
}
#endif

#endif
