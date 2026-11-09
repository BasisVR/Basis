#include "common.hlsli"

#define BOUNDS_CHUNK 1024u

groupshared float3 boundsLo[64];
groupshared float3 boundsHi[64];

uint OrderedBits(float v) { uint u = asuint(v); return (u & 0x80000000u) != 0u ? ~u : u | 0x80000000u; }

[numthreads(64, 1, 1)]
void SubmeshBoundsClear(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= pass.b) { return; }
    ByteAddressBuffer ring = ResourceDescriptorHeap[HEAP_RING];
    RWByteAddressBuffer bounds = ResourceDescriptorHeap[HEAP_BOUNDS];
    uint at = ring.Load(pass.a + id.x * 16u) * 32u;
    bounds.Store4(at, uint4(0xffffffffu, 0xffffffffu, 0xffffffffu, 0u));
    bounds.Store4(at + 16u, uint4(0u, 0u, 0u, 0u));
}

[numthreads(64, 1, 1)]
void SubmeshBounds(uint3 group : SV_GroupID, uint thread : SV_GroupIndex)
{
    ByteAddressBuffer ring = ResourceDescriptorHeap[HEAP_RING];
    ByteAddressBuffer vertices = ResourceDescriptorHeap[HEAP_VERTICES];
    ByteAddressBuffer indices = ResourceDescriptorHeap[HEAP_INDICES];
    RWByteAddressBuffer bounds = ResourceDescriptorHeap[HEAP_BOUNDS];
    uint4 e = ring.Load4(pass.a + group.y * 16u);
    uint first = group.x * BOUNDS_CHUNK;
    if (first >= e.w) { return; }
    uint end = min(first + BOUNDS_CHUNK, e.w);
    float3 lo = 3.0e38, hi = -3.0e38;
    for (uint i = first + thread; i < end; i += 64u)
    {
        float3 p = asfloat(vertices.Load3((indices.Load((e.z + i) * 4u) + e.y) * 32u));
        if (all(isfinite(p))) { lo = min(lo, p); hi = max(hi, p); }
    }
    boundsLo[thread] = lo;
    boundsHi[thread] = hi;
    GroupMemoryBarrierWithGroupSync();
    [unroll] for (uint s = 32u; s > 0u; s >>= 1)
    {
        if (thread < s) { boundsLo[thread] = min(boundsLo[thread], boundsLo[thread + s]); boundsHi[thread] = max(boundsHi[thread], boundsHi[thread + s]); }
        GroupMemoryBarrierWithGroupSync();
    }
    if (thread < 3u && boundsLo[0][thread] <= boundsHi[0][thread])
    {
        uint ignored;
        bounds.InterlockedMin(e.x * 32u + thread * 4u, OrderedBits(boundsLo[0][thread]), ignored);
        bounds.InterlockedMax(e.x * 32u + 16u + thread * 4u, OrderedBits(boundsHi[0][thread]), ignored);
    }
}
