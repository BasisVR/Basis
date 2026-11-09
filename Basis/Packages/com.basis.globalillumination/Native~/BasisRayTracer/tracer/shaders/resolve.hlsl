#include "cache.hlsli"

struct Dirty { float4 sphere; };

[numthreads(64, 1, 1)]
void CacheResolve(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x;
    if (i >= CACHE_SIZE) { return; }
    RWByteAddressBuffer keys = ResourceDescriptorHeap[HEAP_CACHE_KEYS];
    RWByteAddressBuffer checks = ResourceDescriptorHeap[HEAP_CACHE_CHECKS];
    RWByteAddressBuffer accum = ResourceDescriptorHeap[HEAP_CACHE_ACCUM];
    RWByteAddressBuffer resolved = ResourceDescriptorHeap[HEAP_CACHE_RESOLVED];
    RWByteAddressBuffer touched = ResourceDescriptorHeap[HEAP_CACHE_TOUCHED];
    RWByteAddressBuffer positions = ResourceDescriptorHeap[HEAP_CACHE_POSITIONS];
    if (keys.Load(i * 4) == 0u) { return; }
    uint o = i * CACHE_STRIDE;
    float samples;
    SH r = CacheLoadSH(resolved, o, samples);
    if ((frame.flags & FLAG_CACHE_RESET) != 0) { r = ShZero(); samples = 0.0; }
    else if (frame.dirtyCount > 0)
    {
        float4 cell = asfloat(positions.Load4(i * 16));
        StructuredBuffer<Dirty> dirty = ResourceDescriptorHeap[HEAP_DIRTY + frame.slot];
        for (uint d = 0; d < frame.dirtyCount; d++)
        {
            float4 s = dirty[d].sphere;
            if (distance(cell.xyz, s.xyz) < abs(s.w) + cell.w) { samples = min(samples, s.w < 0.0 ? 64.0 : 8.0); }
        }
    }
    uint count;
    SH added = CacheLoadAccum(accum, o, count);
    if (count > 0u)
    {
        SH average = ShScale(added, 1.0 / float(count));
        float weight = float(count);
        float alpha = samples <= 0.0 ? 1.0 : weight / (samples + weight);
        r = ShLerp(r, average, alpha);
        samples = min(samples + weight, 1024.0);
        accum.Store4(o, uint4(0, 0, 0, 0));
        accum.Store4(o + 16, uint4(0, 0, 0, 0));
        accum.Store4(o + 32, uint4(0, 0, 0, 0));
        accum.Store4(o + 48, uint4(0, 0, 0, 0));
        touched.Store(i * 4, frame.frameIndex);
    }
    else if (frame.frameIndex - touched.Load(i * 4) > 20000u)
    {
        keys.Store(i * 4, 0u);
        checks.Store(i * 4, 0u);
        r = ShZero();
        samples = 0.0;
    }
    CacheStoreSH(resolved, o, r, samples);
}
