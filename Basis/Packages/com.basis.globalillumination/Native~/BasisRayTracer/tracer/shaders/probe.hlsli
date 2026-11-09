#ifndef BASIS_RT_PROBE
#define BASIS_RT_PROBE

#include "cache.hlsli"

#define CTX_PROBES 11
#define CTX_PROBE_RAYS 13
#define PROBE_STRIDE 96u
#define PROBE_RAYS 16u
#define PROBE_TILE 16u

uint ProbeIndex(uint2 tile) { return tile.y * (((frame.width + PROBE_TILE - 1u) / PROBE_TILE) * frame.viewCount) + tile.x; }

void StoreProbe(RWByteAddressBuffer b, uint index, float3 position, float valid, float3 normal, float age, SH s)
{
    uint o = index * PROBE_STRIDE;
    b.Store4(o, asuint(float4(position, valid)));
    b.Store4(o + 16, asuint(float4(normal, age)));
    CacheStoreSH(b, o + 32, s, 0.0);
}

bool LoadProbe(RWByteAddressBuffer b, uint index, out float3 position, out float3 normal, out float age, out SH s)
{
    uint o = index * PROBE_STRIDE;
    float4 p = asfloat(b.Load4(o)), n = asfloat(b.Load4(o + 16));
    position = p.xyz;
    normal = n.xyz;
    age = n.w;
    float ignored;
    s = CacheLoadSH(b, o + 32, ignored);
    return p.w > 0.5;
}

#endif
