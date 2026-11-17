#include "common.hlsli"
#include "material.hlsli"

[numthreads(64, 1, 1)]
void EmitterWeights(uint3 id : SV_DispatchThreadID)
{
    ByteAddressBuffer ring = ResourceDescriptorHeap[HEAP_RING];
    uint4 e = ring.Load4(pass.a + id.y * 32u);
    if (id.y >= pass.b || id.x >= e.w) { return; }
    Material material = LoadMaterial(ring.Load(pass.a + id.y * 32u + 16u));
    ByteAddressBuffer vertices = ResourceDescriptorHeap[HEAP_VERTICES];
    ByteAddressBuffer indices = ResourceDescriptorHeap[HEAP_INDICES];
    RWByteAddressBuffer cdf = ResourceDescriptorHeap[HEAP_TRIANGLE_CDF];
    uint3 vi = indices.Load3((e.z + id.x * 3u) * 4u) + e.y;
    float3 p0 = asfloat(vertices.Load3(vi.x * 32u)), p1 = asfloat(vertices.Load3(vi.y * 32u)), p2 = asfloat(vertices.Load3(vi.z * 32u));
    float weight = length(cross(p1 - p0, p2 - p0)) * 0.5;
    if ((material.flags & MATERIAL_TILE_DISCARD) != 0u && TileDiscarded(material, vi)) { weight = 0.0; }
    if (weight > 0.0)
    {
        float2 t0 = LoadUv0(vi.x), t1 = LoadUv0(vi.y), t2 = LoadUv0(vi.z);
        float lod = 0.5 * log2(max(abs((t1.x - t0.x) * (t2.y - t0.y) - (t2.x - t0.x) * (t1.y - t0.y)) * 0.125, 1e-12));
        float2 centre = (t0 + t1 + t2) * (1.0 / 3.0);
        float sum = 0.0;
        [unroll] for (uint k = 0u; k < 4u; k++)
        {
            float2 uv = k == 0u ? centre : lerp(centre, k == 1u ? t0 : (k == 2u ? t1 : t2), 0.5);
            float3 tint = material.baseColor.rgb;
            if (material.emissionBase > 0.0 || (material.alphaMode & 0x8000u) != 0u) { tint *= SampleTexture(material.baseTexture, uv, material.baseST, lod, 1024.0).rgb; }
            sum += Luminance(MaterialEmission(material, uv, lod, 1024.0, tint));
        }
        weight *= sum * 0.25;
    }
    cdf.Store((e.x + id.x) * 4u, asuint(weight > 0.0 && isfinite(weight) ? weight : 0.0));
}

[numthreads(1, 1, 1)]
void EmitterPrefix(uint3 id : SV_DispatchThreadID)
{
    ByteAddressBuffer ring = ResourceDescriptorHeap[HEAP_RING];
    RWByteAddressBuffer cdf = ResourceDescriptorHeap[HEAP_TRIANGLE_CDF];
    uint4 e = ring.Load4(pass.a + id.x * 32u);
    if (id.x >= pass.b) { return; }
    float sum = 0.0, carry = 0.0;
    for (uint i = 0u; i < e.w; i++)
    {
        uint at = (e.x + i) * 4u;
        float y = asfloat(cdf.Load(at)) - carry;
        float t = sum + y;
        carry = (t - sum) - y;
        sum = t;
        cdf.Store(at, asuint(sum));
    }
    bool usable = sum > 0.0 && isfinite(sum);
    float scale = usable ? 1.0 / sum : 0.0;
    for (uint j = 0u; j < e.w; j++)
    {
        uint at = (e.x + j) * 4u;
        float v = usable ? min(asfloat(cdf.Load(at)) * scale, 1.0) : float(j + 1u) / float(e.w);
        cdf.Store(at, asuint(j + 1u == e.w ? 1.0 : v));
    }
}

[numthreads(64, 1, 1)]
void EmitterCuts(uint3 id : SV_DispatchThreadID)
{
    ByteAddressBuffer ring = ResourceDescriptorHeap[HEAP_RING];
    RWByteAddressBuffer cdf = ResourceDescriptorHeap[HEAP_TRIANGLE_CDF];
    uint4 e = ring.Load4(pass.a + id.y * 32u);
    if (id.y >= pass.b || id.x >= e.w + 2u) { return; }
    float threshold = float(max(id.x, 1u) - 1u) / float(e.w);
    uint lo = 0u, hi = e.w - 1u;
    while (lo < hi)
    {
        uint mid = (lo + hi) >> 1;
        if (asfloat(cdf.Load((e.x + mid) * 4u)) <= threshold) { lo = mid + 1u; } else { hi = mid; }
    }
    cdf.Store((e.x + e.w + id.x) * 4u, lo);
}
