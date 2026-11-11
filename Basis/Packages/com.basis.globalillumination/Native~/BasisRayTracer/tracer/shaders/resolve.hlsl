#include "cache.hlsli"

struct Dirty { float4 sphere; };

#ifndef CACHE_DRIFT_RUN
#define CACHE_DRIFT_RUN 8u
#endif

#ifndef CACHE_DIRTY_CEILING
#define CACHE_DIRTY_CEILING 192.0
#endif

#ifndef CACHE_EVICT_AGE
#define CACHE_EVICT_AGE 20000u
#endif

void ClearAccum(RWByteAddressBuffer accum, uint o)
{
    accum.Store4(o, uint4(0, 0, 0, 0));
    accum.Store4(o + 16, uint4(0, 0, 0, 0));
    accum.Store4(o + 32, uint4(0, 0, 0, 0));
    accum.Store4(o + 48, uint4(0, 0, 0, 0));
}

[numthreads(64, 1, 1)]
void CacheMaintain(uint3 id : SV_DispatchThreadID)
{
    if (id.x == 0)
    {
        RWByteAddressBuffer list = ResourceDescriptorHeap[HEAP_CACHE_LIST];
        RWByteAddressBuffer args = ResourceDescriptorHeap[HEAP_CACHE_ARGS];
        uint count = min(list.Load(0), CACHE_SIZE);
        list.Store2(0, uint2(0u, count));
        args.Store4(0, uint4((count + 63u) / 64u, 1u, 1u, 0u));
    }
    uint i = id.x + pass.a;
    if (i >= CACHE_SIZE) { return; }
    RWByteAddressBuffer keys = ResourceDescriptorHeap[HEAP_CACHE_KEYS];
    uint key = keys.Load(i * 8);
    if (key == 0u) { return; }
    if (key == CACHE_TOMBSTONE)
    {
        if (keys.Load(((i + 1u) & CACHE_MASK) * 8) == 0u) { keys.Store(i * 8, 0u); }
        return;
    }
    RWByteAddressBuffer accum = ResourceDescriptorHeap[HEAP_CACHE_ACCUM];
    RWByteAddressBuffer resolved = ResourceDescriptorHeap[HEAP_CACHE_RESOLVED];
    uint o = i * CACHE_STRIDE;
    if ((frame.flags & FLAG_CACHE_RESET) != 0) { CacheStoreSH(resolved, o, ShZero(), 0.0); }
    else if (frame.dirtyCount > 0)
    {
        RWByteAddressBuffer positions = ResourceDescriptorHeap[HEAP_CACHE_POSITIONS];
        float4 cell = asfloat(positions.Load4(i * 16));
        StructuredBuffer<Dirty> dirty = ResourceDescriptorHeap[HEAP_DIRTY + frame.slot];
        float samples = asfloat(resolved.Load(o + 48));
        float capped = samples;
        bool cleared = false;
        for (uint d = 0; d < frame.dirtyCount; d++)
        {
            float4 s = dirty[d].sphere;
            bool clear = s.w >= 100000.0;
            float radius = clear ? s.w - 100000.0 : abs(s.w);
            float reach = distance(cell.xyz, s.xyz);
            if (reach < radius + cell.w)
            {
                if (clear) { cleared = true; }
                else
                {
                    float edge = saturate((reach - cell.w) / max(radius, 1e-3));
                    capped = min(capped, lerp(s.w < 0.0 ? 64.0 : 8.0, CACHE_DIRTY_CEILING, edge * edge));
                }
            }
        }
        if (cleared)
        {
            CacheStoreSH(resolved, o, ShZero(), 0.0);
            ClearAccum(accum, o);
            return;
        }
        if (capped < samples) { resolved.Store(o + 48, asuint(capped)); }
    }
    if (accum.Load(o + 48) == 0u)
    {
        RWByteAddressBuffer touched = ResourceDescriptorHeap[HEAP_CACHE_TOUCHED];
        if (((frame.frameIndex - touched.Load(i * 4)) & 0xffffffu) > CACHE_EVICT_AGE)
        {
            keys.Store2(i * 8, uint2(keys.Load(((i + 1u) & CACHE_MASK) * 8) == 0u ? 0u : CACHE_TOMBSTONE, 0u));
            CacheStoreSH(resolved, o, ShZero(), 0.0);
        }
    }
}

[numthreads(64, 1, 1)]
void CacheResolve(uint3 id : SV_DispatchThreadID)
{
    RWByteAddressBuffer list = ResourceDescriptorHeap[HEAP_CACHE_LIST];
    if (id.x >= list.Load(4)) { return; }
    uint i = list.Load(CACHE_LIST_HEADER + id.x * 4);
    RWByteAddressBuffer accum = ResourceDescriptorHeap[HEAP_CACHE_ACCUM];
    RWByteAddressBuffer resolved = ResourceDescriptorHeap[HEAP_CACHE_RESOLVED];
    RWByteAddressBuffer touched = ResourceDescriptorHeap[HEAP_CACHE_TOUCHED];
    uint o = i * CACHE_STRIDE;
    uint count;
    SH added = CacheLoadAccum(accum, o, count);
    if (count == 0u) { return; }
    float samples;
    SH r = CacheLoadSH(resolved, o, samples);
    SH average = ShScale(added, 1.0 / float(count));
    float weight = float(count);
    uint run = touched.Load(i * 4) >> 24;
    if (samples >= 16.0)
    {
        uint side = Luminance(average.mean) > Luminance(r.mean) ? 0x80u : 0u;
        run = (run & 0x80u) == side ? side | min((run & 0x7fu) + 1u, 127u) : side | 1u;
        if ((run & 0x7fu) >= CACHE_DRIFT_RUN) { samples = max(samples * 0.7, 16.0); }
    }
    else { run = 0u; }
    float alpha = samples <= 0.0 ? 1.0 : weight / (samples + weight);
    r = ShLerp(r, average, alpha);
    samples = min(samples + weight, 1024.0);
    ClearAccum(accum, o);
    touched.Store(i * 4, (frame.frameIndex & 0xffffffu) | (run << 24));
    CacheStoreSH(resolved, o, r, samples);
}
