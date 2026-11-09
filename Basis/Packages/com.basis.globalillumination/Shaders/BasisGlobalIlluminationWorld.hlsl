#ifndef BASIS_GLOBAL_ILLUMINATION_WORLD_INCLUDED
#define BASIS_GLOBAL_ILLUMINATION_WORLD_INCLUDED

#define BASISGI_WORLD_MAX_LEVELS 4
#define BASISGI_WORLD_SH_Y0 0.282095
#define BASISGI_WORLD_SH_Y1 0.488603

#ifndef BASISGI_WORLD_GLOBALS_DECLARED
#define BASISGI_WORLD_GLOBALS_DECLARED
float4 _BasisGIWorldLevel[4];
float4 _BasisGIWorldParams;
float4 _BasisGIWorldSHParams;
TEXTURE3D(_BasisGIWorldSH0);
TEXTURE3D(_BasisGIWorldSH1);
TEXTURE3D(_BasisGIWorldSH2);
TEXTURE3D(_BasisGIWorldSH3);
Texture3D<float4> _BasisGIVoxAlbedo;
#endif

#ifndef BASISGI_WORLD_SPECULAR_DECLARED
#define BASISGI_WORLD_SPECULAR_DECLARED
TEXTURE2D_ARRAY(_BasisGIWorldSpecular);
float4 _BasisGIWorldSpecularParams;
#endif

int BasisGIWorldLevels() { return (int)_BasisGIWorldParams.x; }
bool BasisGIWorldValid() { return _BasisGIWorldParams.w > 0.5; }

int BasisGIWorldLevelAt(float3 positionWS, float marginProbes)
{
    int levels = BasisGIWorldLevels();
    float voxels = _BasisGIWorldParams.y;
    UNITY_LOOP
    for (int level = 0; level < levels; level++)
    {
        float4 box = _BasisGIWorldLevel[level];
        float size = box.w * voxels;
        float margin = box.w * 2.0 * marginProbes;
        float3 local = positionWS - box.xyz;
        if (all(local >= margin) && all(local <= size - margin)) { return level; }
    }
    return -1;
}

int3 BasisGIWorldWrap(int3 index, int count)
{
    return index & (count - 1);
}

int3 BasisGIWorldSeenSlot(float3 positionWS, int level)
{
    float4 box = _BasisGIWorldLevel[level];
    int voxels = (int)_BasisGIWorldParams.y;
    int3 slot = BasisGIWorldWrap((int3)floor(positionWS / box.w), voxels);
    slot.z += level * voxels;
    return slot;
}

bool BasisGIWorldBlocked(float3 positionWS, int level)
{
    float4 box = _BasisGIWorldLevel[level];
    float voxels = _BasisGIWorldParams.y;
    int3 voxel = (int3)floor((positionWS - box.xyz) / box.w);
    if (any(voxel < 0) || any(voxel >= (int)voxels)) { return false; }
    voxel.z += level * (int)voxels;
    return _BasisGIVoxAlbedo.Load(int4(voxel, 0)).a > 0.5;
}

SamplerState sampler_basisgi_world_linear_clamp;

/// <summary>How open the space at a point is, from the filtered voxel occupancy rather than one voxel.
/// A single voxel lookup switched a probe fully on or off as the point stepped from voxel to voxel,
/// and every surface sampling the probes showed that as a grid of blocks.</summary>
float BasisGIWorldOpen(float3 positionWS, int level)
{
    float4 box = _BasisGIWorldLevel[level];
    float voxels = _BasisGIWorldParams.y;
    float3 local = (positionWS - box.xyz) / box.w;
    if (any(local < 0.0) || any(local >= voxels)) { return 1.0; }
    local = clamp(local, 0.5, voxels - 0.5);
    local.z += level * voxels;
    float occupancy = _BasisGIVoxAlbedo.SampleLevel(sampler_basisgi_world_linear_clamp, local / float3(voxels, voxels, voxels * BasisGIWorldLevels()), 0).a;
    return saturate((0.6 - occupancy) * 5.0);
}

struct BasisGIWorldSH
{
    float4 c0;
    float3 cx;
    float3 cy;
    float3 cz;
};

BasisGIWorldSH BasisGIWorldSampleSH(float3 positionWS, int level)
{
    BasisGIWorldSH sh;
    sh.c0 = float4(0.0, 0.0, 0.0, 0.0);
    sh.cx = sh.cy = sh.cz = float3(0.0, 0.0, 0.0);

    float4 box = _BasisGIWorldLevel[level];
    int probes = (int)_BasisGIWorldParams.z;
    float spacing = box.w * 2.0;
    float3 grid = positionWS / spacing - 0.5;
    int3 cell = (int3)floor(grid);
    float3 blend = grid - (float3)cell;
    int3 first = (int3)round(box.xyz / spacing);
    int slab = (int)(_BasisGIWorldSHParams.x * _BasisGIWorldSHParams.y) + level * probes;

    float weightSum = 0.0;
    bool anyHidden = false;
    UNITY_LOOP
    for (int index = 0; index < 8; index++)
    {
        int3 offset = int3(index & 1, (index >> 1) & 1, index >> 2);
        int3 probe = cell + offset;
        if (any(probe < first) || any(probe >= first + probes)) { continue; }
        int3 slot = BasisGIWorldWrap(probe, probes);
        slot.z += slab;
        float3 trilinear = lerp(1.0 - blend, blend, (float3)offset);
        float4 c0 = LOAD_TEXTURE3D(_BasisGIWorldSH0, slot);
        float weight = trilinear.x * trilinear.y * trilinear.z * c0.a;
        if (weight <= 1e-5) { continue; }
        float3 probeWS = ((float3)probe + 0.5) * spacing;
        float open = BasisGIWorldOpen((positionWS + probeWS) * 0.5, level);
        if (open < 1.0) { anyHidden = true; }
        weight *= open;
        if (weight <= 1e-5) { continue; }
        sh.c0 += float4(c0.rgb, 1.0) * weight;
        sh.cx += LOAD_TEXTURE3D(_BasisGIWorldSH1, slot).rgb * weight;
        sh.cy += LOAD_TEXTURE3D(_BasisGIWorldSH2, slot).rgb * weight;
        sh.cz += LOAD_TEXTURE3D(_BasisGIWorldSH3, slot).rgb * weight;
        weightSum += weight;
    }
    if (weightSum <= 1e-5 && anyHidden)
    {
        sh.c0.a = 1.0;
        return sh;
    }

    if (weightSum > 1e-5)
    {
        float inverse = 1.0 / weightSum;
        sh.c0.rgb *= inverse;
        sh.cx *= inverse;
        sh.cy *= inverse;
        sh.cz *= inverse;
        sh.c0.a = 1.0;
    }
    return sh;
}

float3 BasisGIWorldRadianceSH(BasisGIWorldSH sh, float3 direction)
{
    float3 value = sh.c0.rgb * BASISGI_WORLD_SH_Y0 + (sh.cx * direction.x + sh.cy * direction.y + sh.cz * direction.z) * BASISGI_WORLD_SH_Y1;
    return max(0.0, value);
}

float3 BasisGIWorldIrradianceSH(BasisGIWorldSH sh, float3 normal)
{
    float3 value = sh.c0.rgb * BASISGI_WORLD_SH_Y0 + (sh.cx * normal.x + sh.cy * normal.y + sh.cz * normal.z) * (BASISGI_WORLD_SH_Y1 * 2.0 / 3.0);
    return max(0.0, value);
}

float4 BasisGIWorldRadiance(float3 positionWS, float3 direction)
{
    if (!BasisGIWorldValid()) { return float4(0.0, 0.0, 0.0, 0.0); }
    int level = BasisGIWorldLevelAt(positionWS, 1.0);
    if (level < 0) { return float4(0.0, 0.0, 0.0, 0.0); }
    BasisGIWorldSH sh = BasisGIWorldSampleSH(positionWS, level);
    return float4(BasisGIWorldRadianceSH(sh, direction), sh.c0.a);
}

#endif
