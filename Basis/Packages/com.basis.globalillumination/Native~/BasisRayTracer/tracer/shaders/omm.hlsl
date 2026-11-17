#include "common.hlsli"
#include "material.hlsli"

#define HEAP_OMM 13
#ifndef OMM_LEVEL
#define OMM_LEVEL 3
#endif
#define OMM_SIDE (1u << OMM_LEVEL)
#define OMM_WORDS ((1u << (2 * OMM_LEVEL)) / 16u)
#define OMM_TRANSPARENT 0u
#define OMM_OPAQUE 1u
#define OMM_UNKNOWN 3u

uint OmmSpread(uint b)
{
    b = (b | (b << 8u)) & 0x00ff00ffu;
    b = (b | (b << 4u)) & 0x0f0f0f0fu;
    b = (b | (b << 2u)) & 0x33333333u;
    return (b | (b << 1u)) & 0x55555555u;
}

uint OmmCurveIndex(uint iu, uint iv, bool upper)
{
    uint iuv = iu + iv;
    uint iw = ~iuv;
    if (upper && iuv < OMM_SIDE - 1u) { iw--; }
    uint b0 = ~(iu ^ iw) & (OMM_SIDE - 1u);
    uint t = (iu ^ iv) & b0;
    uint f = t;
    f ^= f >> 1u;
    f ^= f >> 2u;
    f ^= f >> 4u;
    f ^= f >> 8u;
    uint b1 = ((f ^ iu) & ~b0) | t;
    return OmmSpread(b0) | (OmmSpread(b1) << 1u);
}

[numthreads(64, 1, 1)]
void OmmBake(uint3 id : SV_DispatchThreadID)
{
    ByteAddressBuffer ring = ResourceDescriptorHeap[HEAP_RING];
    uint4 e = ring.Load4(pass.a + id.y * 32u);
    uint4 g = ring.Load4(pass.a + id.y * 32u + 16u);
    if (id.y >= pass.b || id.x >= e.w) { return; }
    Material material = LoadMaterial(g.z);
    ByteAddressBuffer indices = ResourceDescriptorHeap[HEAP_INDICES];
    RWByteAddressBuffer omm = ResourceDescriptorHeap[HEAP_OMM];
    uint3 vi = indices.Load3((g.y + id.x * 3u) * 4u) + g.x;
    uint mode = material.alphaMode & 3u;
    uint words[OMM_WORDS];
    uint w;
    for (w = 0u; w < OMM_WORDS; w++) { words[w] = 0u; }
    bool hidden = ((material.flags & MATERIAL_TILE_DISCARD) != 0u && TileDiscarded(material, vi)) || material.cutoff > 1.0;
    bool solid = mode == ALPHA_OPAQUE;
    uint transparent = 0u, opaque = 0u;
    if (!hidden && !solid)
    {
        uint channel = AlphaMaskTexture(material) == 0u ? 0u : (material.flags >> 8) & 3u;
        float2 a0 = LoadUv0(vi.x), a1 = LoadUv0(vi.y), a2 = LoadUv0(vi.z);
        float2 m0 = LoadUv(vi.x, channel), m1 = LoadUv(vi.y, channel), m2 = LoadUv(vi.z, channel);
        float2 size = float2(1.0, 1.0);
        if (material.baseTexture >= 0)
        {
            Texture2D<float4> t = ResourceDescriptorHeap[HEAP_TEXTURES + material.baseTexture];
            uint width, height;
            t.GetDimensions(width, height);
            size = float2(width, height);
        }
        if (AlphaMaskTexture(material) != 0u)
        {
            Texture2D<float4> t = ResourceDescriptorHeap[HEAP_TEXTURES + AlphaMaskTexture(material) - 1u];
            uint width, height;
            t.GetDimensions(width, height);
            size = max(size, float2(width, height));
        }
        float2 s = abs(material.baseST.xy) * size;
        float2 d1 = (a1 - a0) * s, d2 = (a2 - a0) * s, n1 = (m1 - m0) * s, n2 = (m2 - m0) * s;
        float texels = max(abs(d1.x * d2.y - d1.y * d2.x), abs(n1.x * n2.y - n1.y * n2.x));
        float fine = mode == ALPHA_CUTOUT ? 1.0 : 0.0;
        float coarse = max(fine, log2(max(sqrt(texels) / (float)OMM_SIDE, 1.0)) + 1.0);
        float accept = mode == ALPHA_CUTOUT ? material.cutoff : 0.999;
        float fail = mode == ALPHA_CUTOUT ? material.cutoff : max(material.cutoff, 1.0 / 255.0);
        float sure = mode == ALPHA_CUTOUT ? material.cutoff + (1.0 - material.cutoff) * 0.9 : 0.999;
        float none = fail * 0.1;
        float scale = 1.0 / (float)OMM_SIDE;
        for (uint j = 0u; j < OMM_SIDE; j++)
        {
            for (uint i = 0u; i + j < OMM_SIDE; i++)
            {
                for (uint k = 0u; k < 2u; k++)
                {
                    if (k == 1u && i + j + 1u >= OMM_SIDE) { continue; }
                    float2 c0 = k == 0u ? float2(i, j) : float2(i + 1u, j + 1u);
                    float2 c1 = float2(i + 1u, j), c2 = float2(i, j + 1u);
                    bool allPass = true, allFail = true;
                    for (uint p = 0u; p < 7u; p++)
                    {
                        float3 q = p == 0u ? float3(1.0, 0.0, 0.0) : p == 1u ? float3(0.0, 1.0, 0.0) : p == 2u ? float3(0.0, 0.0, 1.0) : p == 3u ? float3(0.5, 0.5, 0.0) : p == 4u ? float3(0.0, 0.5, 0.5) : p == 5u ? float3(0.5, 0.0, 0.5) : float3(1.0, 1.0, 1.0) / 3.0;
                        float2 b = (c0 * q.x + c1 * q.y + c2 * q.z) * scale;
                        float3 weights = float3(1.0 - b.x - b.y, b.x, b.y);
                        float2 uv = a0 * weights.x + a1 * weights.y + a2 * weights.z;
                        float2 maskUv = m0 * weights.x + m1 * weights.y + m2 * weights.z;
                        float alpha = AlphaAtUvLod(material, uv, maskUv, fine, 0.0);
                        if (alpha < accept) { allPass = false; }
                        if (alpha >= fail) { allFail = false; }
                        if (p < 3u || p == 6u)
                        {
                            float wide = AlphaAtUvLod(material, uv, maskUv, coarse, coarse);
                            if (wide < sure) { allPass = false; }
                            if (wide >= none) { allFail = false; }
                        }
                    }
                    uint state = allPass ? OMM_OPAQUE : allFail ? OMM_TRANSPARENT : OMM_UNKNOWN;
#ifdef OMM_POINT
                    float2 mid = (c0 + c1 + c2) * scale / 3.0;
                    float3 mw = float3(1.0 - mid.x - mid.y, mid.x, mid.y);
                    state = AlphaAtUvLod(material, a0 * mw.x + a1 * mw.y + a2 * mw.z, m0 * mw.x + m1 * mw.y + m2 * mw.z, fine, 0.0) >= material.cutoff ? OMM_OPAQUE : OMM_TRANSPARENT;
#endif
                    transparent += state == OMM_TRANSPARENT ? 1u : 0u;
                    opaque += state == OMM_OPAQUE ? 1u : 0u;
                    uint index = OmmCurveIndex(i, j, k == 1u);
                    words[index >> 4u] |= state << ((index & 15u) * 2u);
                }
            }
        }
    }
    uint total = OMM_SIDE * OMM_SIDE;
    uint data = e.x * 4u + id.x * OMM_WORDS * 4u;
    for (w = 0u; w < OMM_WORDS; w++) { omm.Store(data + w * 4u, words[w]); }
    omm.Store2(e.y * 4u + id.x * 8u, uint2((g.w + id.x) * OMM_WORDS * 4u, OMM_LEVEL | (2u << 16u)));
    omm.Store(e.z * 4u + id.x * 4u, hidden || transparent == total ? 0xffffffffu : solid || opaque == total ? 0xfffffffeu : g.w + id.x);
}
