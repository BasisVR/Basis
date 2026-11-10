#include "probe.hlsli"

[numthreads(8, 8, 1)]
void ProbeResolve(uint3 id : SV_DispatchThreadID)
{
    uint tilesPerView = (frame.width + PROBE_TILE - 1u) / PROBE_TILE;
    uint2 tile = id.xy;
    if (tile.x >= tilesPerView * frame.viewCount || tile.y >= (frame.height + PROBE_TILE - 1u) / PROBE_TILE) { return; }
    RWByteAddressBuffer probesOut = ResourceDescriptorHeap[frame.contextBase + CTX_PROBES + frame.historyWrite];
    RWByteAddressBuffer probesIn = ResourceDescriptorHeap[frame.contextBase + CTX_PROBES + frame.historyRead];
    RWByteAddressBuffer partials = ResourceDescriptorHeap[frame.contextBase + CTX_PROBE_RAYS];
    uint probe = ProbeIndex(tile);
    float4 header = asfloat(probesOut.Load4(probe * PROBE_STRIDE));
    if (header.w < 0.5) { return; }
    float3 position = header.xyz;
    float3 normal = asfloat(probesOut.Load3(probe * PROBE_STRIDE + 16));
    uint viewIndex = min(tile.x / tilesPerView, frame.viewCount - 1u);
    float viewDistance = length(position - frame.views[viewIndex].position.xyz);
    SH sh = ShZero();
    [unroll] for (uint r = 0; r < PROBE_RAYS; r++)
    {
        float ignored;
        SH s = CacheLoadSH(partials, (probe * PROBE_RAYS + r) * CACHE_STRIDE, ignored);
        sh.c0 += s.c0; sh.cx += s.cx; sh.cy += s.cy; sh.cz += s.cz; sh.mean += s.mean;
    }
    StructuredBuffer<float4> dirty = ResourceDescriptorHeap[HEAP_DIRTY + frame.slot];
    float age = 1.0;
    float2 previousPixel;
    if ((frame.flags & FLAG_RESET_HISTORY) == 0 && ProjectToPixel(frame.views[viewIndex].previousViewProjection, position, previousPixel))
    {
        uint2 previousTile = uint2(uint(previousPixel.x) / PROBE_TILE + viewIndex * tilesPerView, uint(previousPixel.y) / PROBE_TILE);
        float3 pp, pn; float pa; SH ps;
        if (LoadProbe(probesIn, ProbeIndex(previousTile), pp, pn, pa, ps) && distance(pp, position) < max(0.05, viewDistance * 0.03) && dot(pn, normal) > 0.8)
        {
            age = min(pa + 1.0, 32.0);
            for (uint d = 0; d < frame.dirtyCount; d++)
            {
                float4 s = dirty[d];
                bool clear = s.w >= 100000.0;
                if (distance(position, s.xyz) < (clear ? s.w - 100000.0 : abs(s.w))) { age = min(age, clear ? 1.0 : (s.w < 0.0 ? 16.0 : 3.0)); }
            }
            sh = ShLerp(ps, sh, 1.0 / age);
        }
    }
    StoreProbe(probesOut, probe, position, 1.0, normal, age, sh);
}
