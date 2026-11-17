#ifndef BASIS_RT_MATERIAL
#define BASIS_RT_MATERIAL

Instance LoadInstance(uint index) { StructuredBuffer<Instance> b = ResourceDescriptorHeap[HEAP_INSTANCES + frame.slot]; return b[index]; }
Material LoadMaterial(uint index) { StructuredBuffer<Material> b = ResourceDescriptorHeap[HEAP_MATERIALS + frame.slot]; return b[index]; }
Light LoadLight(uint index) { StructuredBuffer<Light> b = ResourceDescriptorHeap[HEAP_LIGHTS + frame.slot]; return b[index]; }
Mesh LoadMesh(uint index) { StructuredBuffer<Mesh> b = ResourceDescriptorHeap[HEAP_MESHES]; return b[index]; }
Submesh LoadSubmesh(uint index) { StructuredBuffer<Submesh> b = ResourceDescriptorHeap[HEAP_SUBMESHES]; return b[index]; }

void LoadTriangle(Instance instance, uint geometry, uint primitive, out uint3 vertexIndex, out Mesh mesh)
{
    mesh = LoadMesh(instance.mesh);
    Submesh submesh = LoadSubmesh(mesh.submeshOffset + geometry);
    ByteAddressBuffer indices = ResourceDescriptorHeap[HEAP_INDICES];
    vertexIndex = indices.Load3((mesh.indexOffset + submesh.indexStart + primitive * 3) * 4) + mesh.vertexOffset;
}

float2 LoadUv0(uint v) { ByteAddressBuffer b = ResourceDescriptorHeap[HEAP_VERTICES]; uint p = b.Load(v * 32 + 16); return f16tof32(uint2(p, p >> 16)); }

bool TileHidden(Material material, uint vertex)
{
    ByteAddressBuffer b = ResourceDescriptorHeap[HEAP_VERTICES];
    uint p = b.Load(vertex * 32 + 16 + ((material.flags >> 5) & 3u) * 4);
    float2 tile = floor(f16tof32(uint2(p, p >> 16)));
    if (any(tile < 0.0) || any(tile >= 4.0)) { return false; }
    return ((uint(material.emissionTexture) >> (16u + uint(tile.y) * 4u + uint(tile.x))) & 1u) != 0u;
}

bool TileDiscarded(Material material, uint3 vi) { return TileHidden(material, vi.x) || TileHidden(material, vi.y) || TileHidden(material, vi.z); }

int EmissionTexture(Material material) { return (material.flags & MATERIAL_TILE_DISCARD) != 0u ? int(material.emissionTexture << 16) >> 16 : material.emissionTexture; }

float4 SampleTexture(int index, float2 uv, float4 st, float lod, float size)
{
    if (index < 0) { return 1.0; }
    Texture2D<float4> t = ResourceDescriptorHeap[HEAP_TEXTURES + index];
    return t.SampleLevel(linearWrap, uv * st.xy + st.zw, max(0.0, lod + log2(size)));
}

float EmissionMask(Material material, float2 uv, float lod, float size)
{
    uint packed = asuint(material.emission.w);
    if ((packed & 0x40000000u) == 0u) { return 1.0; }
    float v = SampleTexture(int((packed >> 8) & 0xfffu), uv, material.emissionST, lod, size)[packed & 3u];
    return (packed & 4u) != 0u ? saturate(1.0 - v) : v;
}

float3 MaterialEmission(Material material, float2 uv, float lod, float size, float3 tint)
{
    float3 emission = material.emission.rgb;
    if (any(emission > 0.0))
    {
        int map = EmissionTexture(material);
        if (map >= 0) { emission *= SampleTexture(map, uv, material.emissionST, lod, size).rgb; }
        emission *= EmissionMask(material, uv, lod, size) * lerp(1.0, saturate(tint), material.emissionBase);
    }
    if (material.emission2 != 0u)
    {
        uint bits = material.alphaMode;
        float3 second = UnpackRGB9E5(material.emission2);
        int map = int((bits >> 2) & 0x1fffu) - 1, mask = int((bits >> 16) & 0x1fffu) - 1;
        if (map >= 0) { second *= SampleTexture(map, uv, float4(1, 1, 0, 0), lod, size).rgb; }
        if (mask >= 0) { float v = SampleTexture(mask, uv, float4(1, 1, 0, 0), lod, size)[(bits >> 29) & 3u]; second *= (bits >> 31) != 0u ? saturate(1.0 - v) : v; }
        if ((bits & 0x8000u) != 0u) { second *= saturate(tint); }
        emission += second;
    }
    return emission;
}

#define HEAP_TRIANGLE_CDF 12
#define TRIANGLE_CDF_UNIFORM 0.25

struct TriangleCdf
{
    uint base, count;
    bool valid;
};

TriangleCdf LoadTriangleCdf(Submesh submesh, uint count)
{
    TriangleCdf c;
    c.base = submesh.cdf - 1u;
    c.count = count;
    c.valid = submesh.cdf != 0u && count != 0u && submesh.indexCount == count * 3u;
    return c;
}

float TriangleProbability(TriangleCdf c, uint primitive)
{
    float even = 1.0 / float(max(c.count, 1u));
    if (!c.valid) { return even; }
    RWByteAddressBuffer b = ResourceDescriptorHeap[HEAP_TRIANGLE_CDF];
    float below = primitive > 0u ? asfloat(b.Load((c.base + primitive - 1u) * 4u)) : 0.0;
    return TRIANGLE_CDF_UNIFORM * even + (1.0 - TRIANGLE_CDF_UNIFORM) * max(asfloat(b.Load((c.base + primitive) * 4u)) - below, 0.0);
}

uint PickTriangle(TriangleCdf c, float u, out float probability)
{
    uint last = max(c.count, 1u) - 1u;
    if (!c.valid) { probability = 1.0 / float(last + 1u); return min(uint(u * c.count), last); }
    uint lo = 0u;
    if (u < TRIANGLE_CDF_UNIFORM) { lo = min(uint(u * (1.0 / TRIANGLE_CDF_UNIFORM) * c.count), last); }
    else
    {
        RWByteAddressBuffer b = ResourceDescriptorHeap[HEAP_TRIANGLE_CDF];
        float target = (u - TRIANGLE_CDF_UNIFORM) * (1.0 / (1.0 - TRIANGLE_CDF_UNIFORM));
        uint3 cut = b.Load3((c.base + c.count + min(uint(target * c.count), last)) * 4u);
        lo = min(cut.x, last);
        uint hi = min(cut.z, last);
        while (lo < hi)
        {
            uint mid = (lo + hi) >> 1;
            if (asfloat(b.Load((c.base + mid) * 4u)) <= target) { lo = mid + 1u; } else { hi = mid; }
        }
    }
    probability = TriangleProbability(c, lo);
    return lo;
}

#endif
