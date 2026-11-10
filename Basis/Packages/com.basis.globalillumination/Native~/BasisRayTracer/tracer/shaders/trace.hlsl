#include "common.hlsli"
#include "cache.hlsli"

#include "hitobject.hlsli"

struct Hit
{
    float3 position;
    float3 normal;
    float3 geometricNormal;
    float2 uv0;
    float2 uv1;
    float lod;
    uint materialIndex;
    uint instanceIndex;
    float area;
    bool front;
};

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

float AlphaAt(Instance instance, uint geometry, uint primitive, float2 barycentrics, out Material material)
{
    material = LoadMaterial(instance.materialBase + geometry);
    float alpha = material.baseColor.a;
    if (material.baseTexture >= 0)
    {
        uint3 vi; Mesh mesh;
        LoadTriangle(instance, geometry, primitive, vi, mesh);
        float3 w = float3(1.0 - barycentrics.x - barycentrics.y, barycentrics);
        float2 uv = LoadUv0(vi.x) * w.x + LoadUv0(vi.y) * w.y + LoadUv0(vi.z) * w.z;
        Texture2D<float4> t = ResourceDescriptorHeap[HEAP_TEXTURES + material.baseTexture];
        alpha *= t.SampleLevel(linearWrap, uv * material.baseST.xy + material.baseST.zw, 1.0).a;
    }
    return alpha;
}

[shader("anyhit")]
void AnyHit(inout Payload payload, BuiltInTriangleIntersectionAttributes attributes)
{
    Instance instance = LoadInstance(InstanceID());
    Material material;
    float alpha = AlphaAt(instance, GeometryIndex(), PrimitiveIndex(), attributes.barycentrics, material);
    if ((material.alphaMode & 3u) == ALPHA_CUTOUT)
    {
        if (alpha < material.cutoff) { IgnoreHit(); }
        return;
    }
    uint2 p = DispatchRaysIndex().xy;
    uint h = Hash(p.x * 9781u + p.y * 6271u + PrimitiveIndex() * 26699u + frame.frameIndex * 1973u + asuint(RayTCurrent()));
    if ((h & 0xffff) * (1.0 / 65536.0) >= alpha) { IgnoreHit(); }
}

[shader("miss")]
void Miss(inout Payload payload) { }

[shader("closesthit")]
void ClosestHit(inout Payload payload, BuiltInTriangleIntersectionAttributes attributes) { STORE_HIT(payload, attributes); }

Hit ResolveHit(HitObj hitObject, float coneSpread)
{
    Hit hit;
    BuiltInTriangleIntersectionAttributes attributes;
    hitObject.GetAttributes(attributes);
    hit.instanceIndex = hitObject.GetInstanceID();
    Instance instance = LoadInstance(hit.instanceIndex);
    uint geometry = hitObject.GetGeometryIndex();
    hit.materialIndex = instance.materialBase + geometry;
    uint3 vi; Mesh mesh;
    LoadTriangle(instance, geometry, hitObject.GetPrimitiveIndex(), vi, mesh);
    ByteAddressBuffer vertices = ResourceDescriptorHeap[HEAP_VERTICES];
    uint4 a0 = vertices.Load4(vi.x * 32), a1 = vertices.Load4(vi.y * 32), a2 = vertices.Load4(vi.z * 32);
    uint4 b0 = vertices.Load4(vi.x * 32 + 16), b1 = vertices.Load4(vi.y * 32 + 16), b2 = vertices.Load4(vi.z * 32 + 16);
    float3 w = float3(1.0 - attributes.barycentrics.x - attributes.barycentrics.y, attributes.barycentrics);
    float3x4 o2w = hitObject.GetObjectToWorld3x4();
    float3x4 w2o = hitObject.GetWorldToObject3x4();
    float3 p0 = mul(o2w, float4(asfloat(a0.xyz), 1.0)), p1 = mul(o2w, float4(asfloat(a1.xyz), 1.0)), p2 = mul(o2w, float4(asfloat(a2.xyz), 1.0));
    hit.position = p0 * w.x + p1 * w.y + p2 * w.z;
    float3 objectNormal = UnpackNormal(a0.w) * w.x + UnpackNormal(a1.w) * w.y + UnpackNormal(a2.w) * w.z;
    float3 smooth = mul(objectNormal, (float3x3)w2o);
    float3 direction = hitObject.GetWorldRayDirection();
    float3 cr = cross(p1 - p0, p2 - p0);
    float area = length(cr);
    hit.normal = dot(smooth, smooth) > 0.0 && all(isfinite(smooth)) ? normalize(smooth) : (area > 0.0 ? cr / area : -direction);
    hit.geometricNormal = area > 0.0 ? cr / area : hit.normal;
    hit.area = area * 0.5;
    hit.front = dot(hit.geometricNormal, direction) * determinant((float3x3)o2w) <= 0.0;
    float2 t0 = f16tof32(uint2(b0.x, b0.x >> 16)), t1 = f16tof32(uint2(b1.x, b1.x >> 16)), t2 = f16tof32(uint2(b2.x, b2.x >> 16));
    hit.uv0 = t0 * w.x + t1 * w.y + t2 * w.z;
    float2 l0 = f16tof32(uint2(b0.y, b0.y >> 16)), l1 = f16tof32(uint2(b1.y, b1.y >> 16)), l2 = f16tof32(uint2(b2.y, b2.y >> 16));
    hit.uv1 = l0 * w.x + l1 * w.y + l2 * w.z;
    float uvArea = abs((t1.x - t0.x) * (t2.y - t0.y) - (t2.x - t0.x) * (t1.y - t0.y));
    float footprint = hitObject.GetRayTCurrent() * coneSpread;
    hit.lod = 0.5 * log2(max(footprint * footprint * uvArea / max(area, 1e-12), 1e-12));
    if (dot(hit.geometricNormal, direction) > 0.0) { hit.geometricNormal = -hit.geometricNormal; }
    if (dot(hit.normal, hit.geometricNormal) < 0.0) { hit.normal = reflect(hit.normal, hit.geometricNormal); }
    return hit;
}

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
        if (material.emissionTexture >= 0) { emission *= SampleTexture(material.emissionTexture, uv, material.emissionST, lod, size).rgb; }
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

bool Occluded(RaytracingAccelerationStructure tlas, float3 origin, float3 direction, float distance, bool opaque = false)
{
    RayDesc ray;
    ray.Origin = origin;
    ray.Direction = direction;
    ray.TMin = 0.0;
    ray.TMax = distance;
    Payload payload = (Payload)0;
    HitObj h = TraceHit(tlas, RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_SKIP_CLOSEST_HIT_SHADER | (opaque ? RAY_FLAG_FORCE_OPAQUE : RAY_FLAG_NONE), 0xFE, ray, payload);
    return h.IsHit();
}

Emitter LoadEmitter(uint index) { StructuredBuffer<Emitter> b = ResourceDescriptorHeap[HEAP_EMITTERS + frame.slot]; return b[index]; }

#ifndef LIGHT_GRID
#define LIGHT_GRID 1
#endif
#define HEAP_LIGHT_GRID 256
#define GRID_X 32
#define GRID_Y 16
#define GRID_Z 32
#define GRID_CELLS (GRID_X * GRID_Y * GRID_Z)
#define GRID_HEADER_BYTES 64u
#define COARSE_X 16
#define COARSE_Y 8
#define COARSE_Z 16
#define GRID_ENTRIES_BYTES (GRID_HEADER_BYTES + (GRID_CELLS + COARSE_X * COARSE_Y * COARSE_Z) * 8u)
#define GRID_CDF_ONE 1048576.0
#define GRID_EMITTER_ID 64u
#define GRID_MIX 0.8

struct GridCell
{
    uint base;
    uint lightEnd;
    uint counts;
    uint flags;
    bool valid;
};

GridCell LoadGridCell(float3 position)
{
    GridCell g = (GridCell)0;
#if LIGHT_GRID
    ByteAddressBuffer grid = ResourceDescriptorHeap[HEAP_LIGHT_GRID + frame.slot];
    uint4 h0 = grid.Load4(0);
    uint4 h1 = grid.Load4(16);
    if (h1.x == 0u) { return g; }
    float3 f = (position - asfloat(h0.xyz)) / asfloat(h0.w);
    uint level = 0u;
    if (any(f < 0.0) || any(f >= float3(GRID_X, GRID_Y, GRID_Z)) || any(!isfinite(f)))
    {
        f = (position - asfloat(uint3(h1.yzw))) / asfloat(grid.Load(32));
        if (any(f < 0.0) || any(f >= float3(COARSE_X, COARSE_Y, COARSE_Z)) || any(!isfinite(f))) { return g; }
        level = 1u;
    }
    uint3 c = uint3(f);
    uint3 b = c >> 2, l = c & 3u;
    uint2 blocks = level == 0u ? uint2(GRID_X / 4, GRID_Y / 4) : uint2(COARSE_X / 4, COARSE_Y / 4);
    uint index = level * GRID_CELLS + ((b.z * blocks.y + b.y) * blocks.x + b.x) * 64u + (l.z * 4u + l.y) * 4u + l.x;
    uint2 word = grid.Load2(GRID_HEADER_BYTES + index * 8u);
    g.base = GRID_ENTRIES_BYTES + (word.x & 0x3fffffu) * 4u;
    g.flags = word.x >> 22;
    g.lightEnd = word.y & 15u;
    g.counts = word.y;
    g.valid = true;
#endif
    return g;
}

void GridEmitterRange(GridCell g, uint part, out uint start, out uint end, out bool complete)
{
    uint all = (g.counts >> 4) & 31u, near = (g.counts >> 9) & 31u, far = (g.counts >> 14) & 31u;
    if ((g.flags & 16u) != 0u) { part = 0u; }
    start = g.lightEnd + (part >= 1u ? all : 0u) + (part >= 2u ? near : 0u);
    end = start + (part == 0u ? all : (part == 1u ? near : far));
    complete = g.valid && ((g.flags >> (1u + min(part, 2u))) & 1u) != 0u;
}

bool GridNoEmitters(GridCell g, uint part)
{
    uint start, end;
    bool complete;
    GridEmitterRange(g, part, start, end, complete);
    return complete && end == start;
}

float GridCdf(ByteAddressBuffer grid, GridCell g, uint i) { return float((grid.Load(g.base + i * 4u) >> 12) + 1u); }

uint GridPick(GridCell g, uint start, uint end, float u, out float p)
{
    ByteAddressBuffer grid = ResourceDescriptorHeap[HEAP_LIGHT_GRID + frame.slot];
    float target = u * GRID_CDF_ONE;
    uint lo = start, hi = end - 1u;
    while (lo < hi)
    {
        uint mid = (lo + hi) >> 1;
        if (GridCdf(grid, g, mid) <= target) { lo = mid + 1u; } else { hi = mid; }
    }
    uint entry = grid.Load(g.base + lo * 4u);
    float below = lo > start ? GridCdf(grid, g, lo - 1u) : 0.0;
    p = (float((entry >> 12) + 1u) - below) / GRID_CDF_ONE;
    return entry & 0xfffu;
}

float GridProbability(GridCell g, uint start, uint end, uint id)
{
    ByteAddressBuffer grid = ResourceDescriptorHeap[HEAP_LIGHT_GRID + frame.slot];
    uint lo = start, hi = end;
    while (lo < hi)
    {
        uint mid = (lo + hi) >> 1;
        if ((grid.Load(g.base + mid * 4u) & 0xfffu) < id) { lo = mid + 1u; } else { hi = mid; }
    }
    if (lo >= end || (grid.Load(g.base + lo * 4u) & 0xfffu) != id) { return 0.0; }
    float below = lo > start ? GridCdf(grid, g, lo - 1u) : 0.0;
    return (GridCdf(grid, g, lo) - below) / GRID_CDF_ONE;
}

float3 TransformPoint(Instance i, float3 p) { return float3(dot(i.row0, float4(p, 1.0)), dot(i.row1, float4(p, 1.0)), dot(i.row2, float4(p, 1.0))); }

struct LightCandidate
{
    float3 value;
    float3 direction;
    float distance;
    float pdf;
    bool shadow, emitter;
    uint4 id;
};

#define CTX_RESERVOIRS 14
#define RESERVOIR_STRIDE 32u
#define RESERVOIR_INITIAL 4u
#define RESERVOIR_HISTORY 20.0
#define CTX_RESERVOIR_SCRATCH 16
#define RESERVOIR_SCRATCH_STRIDE 64u
#define SPATIAL_NEIGHBOURS 2u
#define SPATIAL_RADIUS 16.0

LightCandidate EvaluateEmitterPoint(Instance instance, uint geometry, uint primitive, float r1, float r2, float3 position, float3 normal, uint part, float nearRadius, out float area)
{
    LightCandidate c = (LightCandidate)0;
    area = 0.0;
    uint3 vi; Mesh mesh;
    LoadTriangle(instance, geometry, primitive, vi, mesh);
    ByteAddressBuffer vertices = ResourceDescriptorHeap[HEAP_VERTICES];
    float3 p0 = TransformPoint(instance, asfloat(vertices.Load3(vi.x * 32)));
    float3 p1 = TransformPoint(instance, asfloat(vertices.Load3(vi.y * 32)));
    float3 p2 = TransformPoint(instance, asfloat(vertices.Load3(vi.z * 32)));
    float3 w = float3(1.0 - r1, r1 * (1.0 - r2), r1 * r2);
    float3 lightPoint = p0 * w.x + p1 * w.y + p2 * w.z;
    float3 cr = cross(p1 - p0, p2 - p0);
    area = length(cr) * 0.5;
    if (area <= 1e-10) { return c; }
    float3 lightNormal = cr / (area * 2.0);
    float3 d = lightPoint - position;
    float distance2 = dot(d, d);
    c.distance = sqrt(distance2);
    c.direction = d / max(c.distance, 1e-5);
    float cosReceiver = dot(normal, c.direction);
    Material material = LoadMaterial(instance.materialBase + geometry);
    float cosLight = -dot(lightNormal, c.direction);
    if (dot(instance.row0.xyz, cross(instance.row1.xyz, instance.row2.xyz)) < 0.0) { cosLight = -cosLight; }
    if ((material.flags & MATERIAL_DOUBLE_SIDED) != 0u) { cosLight = abs(cosLight); }
    if (cosReceiver <= 0.0 || cosLight <= 1e-4 || c.distance < frame.normalBias) { return c; }
    if ((part == 1 && c.distance >= nearRadius) || (part == 2 && c.distance < nearRadius)) { return c; }
    float2 uv = LoadUv0(vi.x) * w.x + LoadUv0(vi.y) * w.y + LoadUv0(vi.z) * w.z;
    float3 tint = material.baseColor.rgb;
    if (material.emissionBase > 0.0 || (material.alphaMode & 0x8000u) != 0u) { tint *= SampleTexture(material.baseTexture, uv, material.baseST, 2.0, 1.0).rgb; }
    float3 emission = MaterialEmission(material, uv, 2.0, 1.0, tint) * frame.emissionScale;
    c.value = emission * min(cosReceiver * cosLight / max(distance2, 1e-6), PI / area) / PI;
    c.shadow = true;
    c.emitter = true;
    c.id = uint4(0u, geometry, primitive, f32tof16(r1) | (f32tof16(r2) << 16));
    return c;
}

LightCandidate PunctualCandidate(float3 position, float3 normal, uint index)
{
    LightCandidate c = (LightCandidate)0;
    Light light = LoadLight(index);
    uint type = asuint(light.position.w);
    float attenuation = 1.0;
    if (type == LIGHT_DIRECTIONAL)
    {
        c.direction = -light.direction.xyz;
        c.distance = frame.maxDistance;
    }
    else
    {
        float3 d = light.position.xyz - position;
        c.distance = length(d);
        c.direction = d / max(c.distance, 1e-4);
        float r = c.distance / max(light.direction.w, 1e-4);
        attenuation = saturate(1.0 - r * r * r * r);
        attenuation = attenuation * attenuation / max(c.distance * c.distance, 1e-4);
        if (type == LIGHT_SPOT) { float cone = saturate(dot(-c.direction, light.direction.xyz) * light.spot.x + light.spot.y); attenuation *= cone * cone; }
    }
    float ndl = dot(normal, c.direction);
    if (ndl > 0.0 && attenuation > 0.0) { c.value = light.color.rgb * (ndl * attenuation * frame.lightIntensity); }
    c.shadow = (frame.flags & FLAG_SHADOWS) != 0 && light.color.w > 0.0;
    return c;
}

LightCandidate EmitterCandidate(GridCell g, float3 position, float3 normal, inout uint rng, uint part, float nearRadius)
{
    LightCandidate c = (LightCandidate)0;
    uint count = frame.emitterCount;
    uint start, end;
    bool complete;
    GridEmitterRange(g, part, start, end, complete);
    if (complete && end == start) { return c; }
    float q = end > start ? (complete ? 1.0 : GRID_MIX) : 0.0;
    float u = Random(rng);
    uint lo = 0;
    float cell = 0.0;
    if (u < q)
    {
        lo = GridPick(g, start, end, u / q, cell) - GRID_EMITTER_ID;
        if (lo >= count) { return c; }
    }
    else
    {
        u = q > 0.0 ? (u - q) / (1.0 - q) : u;
        uint hi = count - 1;
        while (lo < hi)
        {
            uint mid = (lo + hi) >> 1;
            if (LoadEmitter(mid).cdf < u) { lo = mid + 1; } else { hi = mid; }
        }
        if (q > 0.0) { cell = GridProbability(g, start, end, lo + GRID_EMITTER_ID); }
    }
    Emitter e = LoadEmitter(lo);
    float selection = q * cell + (1.0 - q) * (e.cdf - (lo > 0 ? LoadEmitter(lo - 1).cdf : 0.0));
    if (selection <= 0.0 || e.triangleCount == 0) { return c; }
    Instance instance = LoadInstance(e.instance);
    if (part != 0u)
    {
        RWByteAddressBuffer bounds = ResourceDescriptorHeap[HEAP_BOUNDS];
        uint at = (LoadMesh(instance.mesh).submeshOffset + e.geometry) * 32u;
        uint3 loBits = bounds.Load3(at), hiBits = bounds.Load3(at + 16u);
        float3 lo = asfloat(select((loBits & 0x80000000u) != 0u, loBits & 0x7fffffffu, ~loBits)), hi = asfloat(select((hiBits & 0x80000000u) != 0u, hiBits & 0x7fffffffu, ~hiBits));
        if (all(lo <= hi))
        {
            float3 extent = (hi - lo) * 0.5;
            float reach = length(float3(dot(abs(instance.row0.xyz), extent), dot(abs(instance.row1.xyz), extent), dot(abs(instance.row2.xyz), extent))) * 1.001 + 1e-4;
            float d = distance(position, TransformPoint(instance, (lo + hi) * 0.5));
            if ((part == 1u && d - reach > nearRadius) || (part == 2u && d + reach < nearRadius)) { Random(rng); Random(rng); Random(rng); return c; }
        }
    }
    uint primitive = min(uint(Random(rng) * e.triangleCount), e.triangleCount - 1);
    float r1 = sqrt(Random(rng)), r2 = Random(rng);
    float area;
    c = EvaluateEmitterPoint(instance, e.geometry, primitive, r1, r2, position, normal, part, nearRadius, area);
    c.id.x = e.instance;
    if (area > 1e-10) { c.pdf = selection / (e.triangleCount * area); }
    return c;
}

LightCandidate SampleLights(RaytracingAccelerationStructure tlas, float3 position, float3 normal, bool punctual, inout uint rng, uint part = 0, float nearRadius = 0.0)
{
    LightCandidate chosen = (LightCandidate)0;
    GridCell g = LoadGridCell(position);
    bool lightsComplete = g.valid && (g.flags & 1u) != 0u;
    uint lights = punctual && !(lightsComplete && g.lightEnd == 0u) ? frame.lightCount : 0u;
    uint emitters = GridNoEmitters(g, part) ? 0u : frame.emitterCount;
    if (lights + emitters == 0) { return chosen; }
    float qLights = g.lightEnd > 0u ? (lightsComplete ? 1.0 : GRID_MIX) : 0.0;
    float pPunctual = lights == 0 ? 0.0 : (emitters == 0 ? 1.0 : 0.5);
    float total = 0.0, chosenTarget = 0.0;
    const uint candidates = 4;
    uint flip = Random(rng) < 0.5 ? 1u : 0u;
    [loop] for (uint i = 0; i < candidates; i++)
    {
        LightCandidate c;
        if (pPunctual >= 1.0 || (pPunctual > 0.0 && ((i + flip) & 1u) == 0u))
        {
            float u = Random(rng), cell = 0.0;
            uint index;
            if (u < qLights) { index = GridPick(g, 0u, g.lightEnd, u / qLights, cell); }
            else
            {
                index = min(uint((qLights > 0.0 ? (u - qLights) / (1.0 - qLights) : u) * lights), lights - 1);
                if (qLights > 0.0) { cell = GridProbability(g, 0u, g.lightEnd, index); }
            }
            c = PunctualCandidate(position, normal, min(index, lights - 1));
            c.pdf = pPunctual * (qLights * cell + (1.0 - qLights) / lights);
        }
        else
        {
            c = EmitterCandidate(g, position, normal, rng, part, nearRadius);
            c.pdf *= 1.0 - pPunctual;
        }
        float target = Luminance(c.value);
        if (target <= 0.0 || c.pdf <= 0.0) { continue; }
        float w = target / c.pdf;
        total += w;
        if (Random(rng) * total < w) { chosen = c; chosenTarget = target; }
    }
    if (total <= 0.0 || chosenTarget <= 0.0) { return (LightCandidate)0; }
    if (chosen.shadow && Occluded(tlas, position, chosen.direction, max(chosen.distance * 0.999 - frame.normalBias * 2.0, 0.0), part == 2u)) { return (LightCandidate)0; }
    chosen.value *= total / (candidates * chosenTarget);
    return chosen;
}

bool ScreenRadiance(Texture2D<uint4> gbuffer, float3 position, float3 normal, out float3 radiance)
{
    radiance = 0.0;
    for (uint v = 0; v < frame.viewCount; v++)
    {
        float4x4 vp = mul(frame.views[v].projection, frame.views[v].view);
        float2 pixel;
        if (!ProjectToPixel(vp, position, pixel)) { continue; }
        uint2 p = uint2(pixel) + uint2(v * frame.width, 0);
        Surface s = LoadSurface(gbuffer, p, v);
        if (!s.valid) { continue; }
        float expected = length(position - frame.views[v].position.xyz);
        if (abs(s.distance - expected) > max(0.02, expected * 0.01)) { continue; }
        if (dot(s.normal, normal) < 0.7) { continue; }
        radiance = s.radiance;
        return true;
    }
    return false;
}

Surface NeighbourSurface(Texture2D<uint4> gbuffer, uint2 pixel, uint2 local, int2 step, uint viewIndex, out bool ok)
{
    int2 q = int2(local) + step;
    ok = all(q >= 0) && q.x < int(frame.width) && q.y < int(frame.height);
    Surface s = (Surface)0;
    if (!ok) { return s; }
    s = LoadSurface(gbuffer, uint2(int2(pixel) + step), viewIndex);
    ok = s.valid;
    return s;
}

float3 PrimaryGeometric(Texture2D<uint4> gbuffer, uint2 pixel, uint2 local, uint viewIndex, inout Surface surface)
{
    float3 toViewer = normalize(frame.views[viewIndex].position.xyz - surface.position);
    if (dot(surface.normal, toViewer) < 0.0) { surface.normal = normalize(surface.normal - 2.0 * dot(surface.normal, toViewer) * toViewer); }
    bool okR, okL, okU, okD;
    Surface r = NeighbourSurface(gbuffer, pixel, local, int2(1, 0), viewIndex, okR);
    Surface l = NeighbourSurface(gbuffer, pixel, local, int2(-1, 0), viewIndex, okL);
    Surface u = NeighbourSurface(gbuffer, pixel, local, int2(0, 1), viewIndex, okU);
    Surface d = NeighbourSurface(gbuffer, pixel, local, int2(0, -1), viewIndex, okD);
    float dr = okR ? abs(r.distance - surface.distance) : 1e30, dl = okL ? abs(l.distance - surface.distance) : 1e30;
    float du = okU ? abs(u.distance - surface.distance) : 1e30, dd = okD ? abs(d.distance - surface.distance) : 1e30;
    float3 geometric = surface.normal;
    if (min(dr, dl) < 1e29 && min(du, dd) < 1e29)
    {
        float3 dx = dr <= dl ? r.position - surface.position : surface.position - l.position;
        float3 dy = du <= dd ? u.position - surface.position : surface.position - d.position;
        float3 g = cross(dx, dy);
        float limit = max(0.25, surface.distance * 0.25);
        if (dot(g, g) > 1e-12 * dot(dx, dx) * dot(dy, dy) && dot(g, g) > 0.0 && min(dr, dl) < limit && min(du, dd) < limit)
        {
            g = normalize(g);
            if (dot(g, toViewer) < 0.0) { g = -g; }
            geometric = g;
        }
    }
    if (dot(surface.normal, geometric) < 0.2) { surface.normal = geometric; }
    return geometric;
}

bool ReusePrimary(uint2 pixel, inout Surface surface, float3 geometric, float3 origin)
{
    if ((frame.flags & FLAG_RESET_HISTORY) != 0) { return false; }
    RWByteAddressBuffer scratch = ResourceDescriptorHeap[frame.contextBase + CTX_RESERVOIR_SCRATCH];
    float4 at = asfloat(scratch.Load4((pixel.y * frame.width * frame.viewCount + pixel.x) * RESERVOIR_SCRATCH_STRIDE + 32u));
    if (!all(isfinite(at.xyz)) || distance(at.xyz, origin) > max(0.005, surface.distance * 0.002)) { return false; }
    float3 n = UnpackNormal(asuint(at.w));
    if (dot(n, surface.normal) < 0.9) { return false; }
    surface.normal = n;
    return true;
}

bool PrimarySurface(RaytracingAccelerationStructure tlas, float3 viewer, inout Surface surface, float3 geometric)
{
    float3 d = surface.position - viewer;
    float distance = length(d);
    if (distance <= 1e-4) { return false; }
    float tolerance = max(0.02, distance * 0.02);
    RayDesc ray;
    ray.Origin = viewer;
    ray.Direction = d / distance;
    ray.TMin = max(distance - tolerance, 0.0);
    ray.TMax = distance + tolerance;
    Payload payload = (Payload)0;
    HitObj h = TraceHit(tlas, RAY_FLAG_NONE, MASK_VISIBLE, ray, payload);
    if (!h.IsHit()) { return false; }
    bool avatar = (LoadInstance(h.GetInstanceID()).flags & INSTANCE_AVATAR) != 0u;
    Hit hit = ResolveHit(h, 0.0);
    if (dot(hit.geometricNormal, surface.normal) < 0.9 || dot(hit.normal, geometric) < 0.2) { return avatar; }
    surface.normal = hit.normal;
    return avatar;
}

float3 HitLighting(RaytracingAccelerationStructure tlas, float3 origin, float3 position, float3 normal, float3 geometric, float3 viewer, bool cache, inout uint rng, out float coverage)
{
    float nearRadius = CacheCellSize(position, viewer) * 3.0;
    float4 cached = cache ? CacheLookup(position, geometric, viewer, Random(rng)) : 0.0;
    coverage = saturate(cached.a * 2.0);
    bool far = (frame.flags & FLAG_BAKED_HYBRID) == 0;
    bool split = coverage >= 1.0 && far;
    LightCandidate light = SampleLights(tlas, origin, normal, true, rng, split ? 1u : 0u, nearRadius);
    if (!split && far && light.emitter && light.distance >= nearRadius) { light.value *= 1.0 - coverage; }
    return light.value + cached.rgb * coverage;
}

#ifndef PATH_SEGMENTS
#define PATH_SEGMENTS 12
#endif

struct Bounce
{
    float3 radiance;
    bool hit, terminal;
    float3 position, normal, geometric;
};

float3 UniformHemisphere(float3 n, float2 u)
{
    float3 t = abs(n.z) < 0.999 ? normalize(cross(n, float3(0, 0, 1))) : normalize(cross(n, float3(1, 0, 0)));
    float3 b = cross(n, t);
    float z = u.x, r = sqrt(max(0.0, 1.0 - z * z)), phi = 2.0 * PI * u.y;
    return normalize(t * (r * cos(phi)) + b * (r * sin(phi)) + n * z);
}

float3 SurfaceEmission(Material material, Hit hit, float3 emission, float3 fromNormal, float3 direction, float t)
{
    if (!hit.front && (material.flags & MATERIAL_DOUBLE_SIDED) == 0u) { return 0.0; }
    if ((material.flags & MATERIAL_EMITTER) == 0u) { return emission; }
    float g = max(dot(fromNormal, direction), 0.0) * abs(dot(hit.geometricNormal, direction)) / max(t * t, 1e-6);
    return emission * saturate(1.0 - PI / max(hit.area * g, 1e-12));
}

Bounce TraceRadiance(RaytracingAccelerationStructure tlas, Texture2D<uint4> gbuffer, float3 origin, float3 direction, float3 fromNormal, float3 viewer, bool reorder, inout uint rng)
{
    Bounce first = (Bounce)0;
    float3 radiance = 0.0, throughput = 1.0;
    [loop] for (uint segment = 0; segment < PATH_SEGMENTS; segment++)
    {
        RayDesc ray;
        ray.Origin = origin;
        ray.Direction = direction;
        ray.TMin = 0.0;
        ray.TMax = frame.maxDistance;
        Payload payload = (Payload)0;
        HitObj hitObject = TraceHit(tlas, RAY_FLAG_NONE, MASK_VISIBLE, ray, payload);
        if (reorder && segment == 0)
        {
            uint hint = 0;
            if (hitObject.IsHit())
            {
                Instance i = LoadInstance(hitObject.GetInstanceID());
                hint = ((i.materialBase + hitObject.GetGeometryIndex()) & 0x3ff) | (i.lightmap >= 0 ? 0x400u : 0u) | 0x800u;
            }
            ReorderHit(hitObject, hint, 12);
        }
        if (!hitObject.IsHit())
        {
            radiance += throughput * SampleAmbient(direction);
            if (segment == 0) { first.terminal = true; }
            break;
        }
        float t = hitObject.GetRayTCurrent();
        Hit hit = ResolveHit(hitObject, 0.35);
        Material material = LoadMaterial(hit.materialIndex);
        Instance instance = LoadInstance(hit.instanceIndex);
        float4 baseColor = material.baseColor * SampleTexture(material.baseTexture, hit.uv0, material.baseST, hit.lod, 512.0);
        float3 albedo = saturate(baseColor.rgb) * (1.0 - material.metallic * 0.75);
        float3 emission = MaterialEmission(material, hit.uv0, hit.lod, 512.0, baseColor.rgb) * frame.emissionScale;
        float3 hitOrigin = hit.position + hit.geometricNormal * frame.normalBias;
        if (segment == 0)
        {
            first.hit = true;
            first.position = hitOrigin;
            first.normal = hit.normal;
            first.geometric = hit.geometricNormal;
        }
        radiance += throughput * SurfaceEmission(material, hit, emission, fromNormal, direction, t);
        bool trusted = t > CacheCellSize(hit.position, viewer) * 1.5 || segment >= 2;
        float3 screen;
        if ((frame.flags & FLAG_SCREEN_REUSE) != 0 && ScreenRadiance(gbuffer, hit.position, hit.normal, screen))
        {
            radiance += throughput * max(0.0, screen - emission);
            if (instance.lightmap < 0 && trusted) { radiance += throughput * albedo * CacheLookup(hit.position, hit.geometricNormal, viewer, Random(rng)).rgb; }
            if (segment == 0) { first.terminal = true; }
            break;
        }
        if ((material.flags & MATERIAL_UNLIT) != 0)
        {
            radiance += throughput * baseColor.rgb;
            if (segment == 0) { first.terminal = true; }
            break;
        }
        if (instance.lightmap >= 0)
        {
            Texture2D<float4> lm = ResourceDescriptorHeap[HEAP_TEXTURES + instance.lightmap];
            float3 baked = lm.SampleLevel(linearClamp, hit.uv1 * instance.lightmapST.xy + instance.lightmapST.zw, 0).rgb;
            radiance += throughput * albedo * (baked + SampleLights(tlas, hitOrigin, hit.normal, true, rng).value);
            if (segment == 0) { first.terminal = true; }
            break;
        }
        float coverage;
        radiance += throughput * albedo * HitLighting(tlas, hitOrigin, hit.position, hit.normal, hit.geometricNormal, viewer, trusted, rng, coverage);
        if (coverage >= 1.0) { break; }
        throughput *= albedo * (1.0 - coverage);
        if (segment >= 2)
        {
            float survive = min(max(throughput.r, max(throughput.g, throughput.b)), 0.95);
            if (Random(rng) >= survive) { break; }
            throughput /= survive;
        }
        if (all(throughput < 0.01)) { break; }
        origin = hitOrigin;
        fromNormal = hit.normal;
        direction = CosineSample(hit.normal, float2(Random(rng), Random(rng)));
        if (dot(direction, hit.geometricNormal) <= 0.0) { direction = normalize(direction - 2.0 * dot(direction, hit.geometricNormal) * hit.geometricNormal); }
    }
    first.radiance = all(isfinite(radiance)) ? max(radiance, 0.0) : 0.0;
    return first;
}

float3 ClampFirefly(float3 v)
{
    if (!all(isfinite(v))) { return 0.0; }
    float l = Luminance(v);
    return frame.fireflyClamp > 0.0 && l > frame.fireflyClamp ? v * (frame.fireflyClamp / l) : v;
}

void GatherRay(RaytracingAccelerationStructure tlas, Texture2D<uint4> gbuffer, float3 position, float3 normal, float3 plane, float3 viewer, uint i, uint rays, float2 rotation, bool reorder, inout uint rng, inout SH sh, out Bounce b)
{
    uint columns = rays >= 16 ? 8u : rays;
    uint rows = max(rays / columns, 1u);
    float2 u = frac((float2(i % columns, i / columns) + float2(Random(rng), Random(rng))) / float2(columns, rows) + rotation);
    float3 direction = CosineSample(normal, u);
    if (dot(direction, plane) <= 0.0) { direction = normalize(direction - 2.0 * dot(direction, plane) * plane); }
    b = TraceRadiance(tlas, gbuffer, position, direction, normal, viewer, reorder, rng);
    float3 sample = ClampFirefly(b.radiance);
    ShAdd(sh, direction, sample * (PI / (max(dot(direction, normal), 0.2) * rays)));
    sh.mean += sample / rays;
}

void GatherFarLight(RaytracingAccelerationStructure tlas, float3 position, float3 normal, float3 viewer, inout uint rng, inout SH sh)
{
    if ((frame.flags & FLAG_BAKED_HYBRID) != 0) { return; }
    LightCandidate light = SampleLights(tlas, position, normal, false, rng, 2u, CacheCellSize(position, viewer) * 3.0);
    if (any(light.value > 0.0))
    {
        float3 v = ClampFirefly(light.value);
        ShAdd(sh, light.direction, v * (PI / max(dot(normal, light.direction), 0.25)));
        sh.mean += v;
    }
}

float3 FarLight(RaytracingAccelerationStructure tlas, float3 position, float3 normal, float nearRadius, inout uint rng)
{
    if ((frame.flags & FLAG_BAKED_HYBRID) != 0 || frame.emitterCount == 0) { return 0.0; }
    return ClampFirefly(SampleLights(tlas, position, normal, false, rng, 2u, nearRadius).value);
}

SH GatherSH(RaytracingAccelerationStructure tlas, Texture2D<uint4> gbuffer, float3 position, float3 normal, float3 plane, float3 viewer, uint rays, float2 rotation, bool reorder, inout uint rng, out Bounce firstBounce)
{
    SH sh = ShZero();
    firstBounce = (Bounce)0;
    for (uint i = 0; i < rays; i++)
    {
        Bounce b;
        GatherRay(tlas, gbuffer, position, normal, plane, viewer, i, rays, rotation, reorder, rng, sh, b);
        if (i == 0) { firstBounce = b; }
    }
    GatherFarLight(tlas, position, normal, viewer, rng, sh);
    return sh;
}

bool CacheSettled(float3 p, float3 n, float3 viewer, float rnd)
{
    CacheFrame f = CacheLocate(p, n, viewer, rnd);
    int index = CacheFind(CacheKey(int3(floor(p / f.size)), f.level, f.axis));
    if (index < 0) { return false; }
    RWByteAddressBuffer resolved = ResourceDescriptorHeap[HEAP_CACHE_RESOLVED];
    float samples;
    CacheLoadSH(resolved, uint(index) * CACHE_STRIDE, samples);
    return samples >= CACHE_SETTLED;
}

#include "probe.hlsli"

LightCandidate EvaluateStored(uint4 id, float3 position, float3 normal, float nearRadius)
{
    LightCandidate c = (LightCandidate)0;
    if (id.x >= frame.instanceCount) { return c; }
    Instance instance = LoadInstance(id.x);
    Mesh mesh = LoadMesh(instance.mesh);
    if ((LoadMaterial(instance.materialBase + id.y).flags & MATERIAL_EMITTER) == 0 || id.y >= mesh.submeshCount || id.z * 3u >= LoadSubmesh(mesh.submeshOffset + id.y).indexCount) { return c; }
    float area;
    c = EvaluateEmitterPoint(instance, id.y, id.z, f16tof32(id.w), f16tof32(id.w >> 16), position, normal, 1u, nearRadius, area);
    c.id.x = id.x;
    return c;
}

#ifdef REFERENCE
void ReferenceRayGen(uint mode, uint2 pixel)
{
    if (mode != 1u || pixel.x >= frame.width * frame.viewCount || pixel.y >= frame.height) { return; }
    Texture2D<uint4> gbuffer = ResourceDescriptorHeap[HEAP_GBUFFER + frame.gbufferIndex];
    RaytracingAccelerationStructure tlas = LOAD_TLAS();
    RWTexture2D<float4> output = ResourceDescriptorHeap[HEAP_RADIANCE_UAV];
    uint viewIndex;
    uint2 local = ViewPixel(pixel, viewIndex);
    Surface surface = LoadSurface(gbuffer, pixel, viewIndex);
    if (!surface.valid) { output[pixel] = float4(0.0, 0.0, 0.0, 1.0); return; }
    float3 viewer = frame.views[viewIndex].position.xyz;
    float3 geometric = PrimaryGeometric(gbuffer, pixel, local, viewIndex, surface);
    float3 toViewer = normalize(viewer - surface.position);
    float3 start = surface.position + (geometric + toViewer) * frame.normalBias * (1.0 + surface.distance * 0.01);
    PrimarySurface(tlas, viewer, surface, geometric);
    uint rng = Hash(pixel.x + pixel.y * 65536u) ^ Hash(frame.frameIndex * 7919u + 91u);
    uint paths = max(frame.samples, 1u);
    float3 total = 0.0;
    for (uint p = 0; p < paths; p++)
    {
        float3 origin = start, normal = surface.normal, plane = geometric, throughput = 1.0, radiance = 0.0;
        [loop] for (uint segment = 0; segment < 24; segment++)
        {
            float3 direction = CosineSample(normal, float2(Random(rng), Random(rng)));
            if (dot(direction, plane) <= 0.0) { direction = normalize(direction - 2.0 * dot(direction, plane) * plane); }
            RayDesc ray;
            ray.Origin = origin;
            ray.Direction = direction;
            ray.TMin = 0.0;
            ray.TMax = frame.maxDistance;
            Payload payload = (Payload)0;
            HitObj h = TraceHit(tlas, RAY_FLAG_NONE, MASK_VISIBLE, ray, payload);
            if (!h.IsHit()) { radiance += throughput * SampleAmbient(direction); break; }
            Hit hit = ResolveHit(h, 0.35);
            Material material = LoadMaterial(hit.materialIndex);
            float4 baseColor = material.baseColor * SampleTexture(material.baseTexture, hit.uv0, material.baseST, hit.lod, 512.0);
            float3 albedo = saturate(baseColor.rgb) * (1.0 - material.metallic * 0.75);
            if (hit.front || (material.flags & MATERIAL_DOUBLE_SIDED) != 0u) { radiance += throughput * MaterialEmission(material, hit.uv0, hit.lod, 512.0, baseColor.rgb) * frame.emissionScale; }
            if ((material.flags & MATERIAL_UNLIT) != 0) { radiance += throughput * baseColor.rgb; break; }
            origin = hit.position + hit.geometricNormal * frame.normalBias;
            for (uint l = 0; l < frame.lightCount; l++)
            {
                LightCandidate c = PunctualCandidate(origin, hit.normal, l);
                if (any(c.value > 0.0) && !(c.shadow && Occluded(tlas, origin, c.direction, max(c.distance * 0.999 - frame.normalBias * 2.0, 0.0)))) { radiance += throughput * albedo * c.value; }
            }
            throughput *= albedo;
            if (segment >= 3u)
            {
                float survive = clamp(max(throughput.r, max(throughput.g, throughput.b)), 0.05, 0.95);
                if (Random(rng) >= survive) { break; }
                throughput /= survive;
            }
            normal = hit.normal;
            plane = hit.geometricNormal;
        }
        if (all(isfinite(radiance))) { total += radiance; }
    }
    output[pixel] = float4(total / paths, 1.0);
}
#endif

[shader("raygeneration")]
void RayGen()
{
    uint mode = pass.a;
    uint2 index = DispatchRaysIndex().xy;
#ifdef REFERENCE
    ReferenceRayGen(mode, index);
    return;
#endif
    if (mode == 0u && index.y >= pass.b)
    {
        uint k = (index.y - pass.b) * DispatchRaysDimensions().x + index.x;
        if (k >= 4096u) { return; }
        mode = 3u;
        index = uint2(k % 64u, k / 64u);
    }
    Texture2D<uint4> gbuffer = ResourceDescriptorHeap[HEAP_GBUFFER + frame.gbufferIndex];
    RaytracingAccelerationStructure tlas = LOAD_TLAS();
    bool reorder = (frame.flags & FLAG_REORDER) != 0;

    if (mode == 4)
    {
        uint2 pixel = index;
        uint stride = frame.width * frame.viewCount;
        if (pixel.x >= stride || pixel.y >= frame.height) { return; }
        uint viewIndex;
        uint2 local = ViewPixel(pixel, viewIndex);
        RWByteAddressBuffer scratch = ResourceDescriptorHeap[frame.contextBase + CTX_RESERVOIR_SCRATCH];
        RWByteAddressBuffer history = ResourceDescriptorHeap[frame.contextBase + CTX_RESERVOIRS + frame.historyWrite];
        uint o = (pixel.y * stride + pixel.x) * RESERVOIR_SCRATCH_STRIDE;
        uint ho = (pixel.y * stride + pixel.x) * RESERVOIR_STRIDE;
        uint4 id = scratch.Load4(o);
        float4 meta = asfloat(scratch.Load4(o + 16u));
        if (!(meta.y > 0.0) || frame.emitterCount == 0) { history.Store4(ho, id); history.Store2(ho + 16u, uint2(0u, 0u)); return; }
        float4 at = asfloat(scratch.Load4(o + 32u));
        float3 origin = at.xyz, normal = UnpackNormal(asuint(at.w));
        float nearRadius = meta.z;
        float tolerance = max(0.03, distance(origin, frame.views[viewIndex].position.xyz) * 0.02);
        uint rng = Hash(pixel.x * 7919u + pixel.y * 6271u) ^ Hash(frame.frameIndex * 104729u + 7u);
        LightCandidate chosen = (LightCandidate)0;
        float chosenTarget = 0.0, wsum = 0.0, count = meta.y;
        float4 partAt[SPATIAL_NEIGHBOURS];
        float partRadius[SPATIAL_NEIGHBOURS], partM[SPATIAL_NEIGHBOURS];
        uint parts = 0;
        if (meta.x > 0.0)
        {
            LightCandidate own = EvaluateStored(id, origin, normal, nearRadius);
            float t = Luminance(own.value);
            float w = t * meta.x * meta.y;
            if (w > 0.0 && isfinite(w)) { wsum = w; chosen = own; chosenTarget = t; }
        }
        [loop] for (uint k = 0; k < SPATIAL_NEIGHBOURS; k++)
        {
            float radius = SPATIAL_RADIUS * sqrt(Random(rng)), angle = 2.0 * PI * Random(rng);
            int2 q = int2(local) + int2(round(radius * cos(angle)), round(radius * sin(angle)));
            if (any(q < 0) || q.x >= int(frame.width) || q.y >= int(frame.height) || all(q == int2(local))) { continue; }
            uint no = (uint(q.y) * stride + uint(q.x) + viewIndex * frame.width) * RESERVOIR_SCRATCH_STRIDE;
            float4 neighbourMeta = asfloat(scratch.Load4(no + 16u));
            if (!(neighbourMeta.y > 0.0)) { continue; }
            float4 neighbourAt = asfloat(scratch.Load4(no + 32u));
            if (dot(UnpackNormal(asuint(neighbourAt.w)), normal) < 0.9 || abs(dot(neighbourAt.xyz - origin, normal)) > tolerance) { continue; }
            count += neighbourMeta.y;
            partAt[parts] = neighbourAt;
            partRadius[parts] = neighbourMeta.z;
            partM[parts] = neighbourMeta.y;
            parts++;
            if (!(neighbourMeta.x > 0.0)) { continue; }
            LightCandidate c = EvaluateStored(scratch.Load4(no), origin, normal, nearRadius);
            float t = Luminance(c.value);
            float w = t * neighbourMeta.x * neighbourMeta.y;
            if (w > 0.0 && isfinite(w))
            {
                wsum += w;
                if (Random(rng) * wsum < w) { chosen = c; chosenTarget = t; }
            }
        }
        float z = meta.y;
        if (chosenTarget > 0.0)
        {
            [loop] for (uint k = 0; k < parts; k++)
            {
                if (Luminance(EvaluateStored(chosen.id, partAt[k].xyz, UnpackNormal(asuint(partAt[k].w)), partRadius[k]).value) > 0.0) { z += partM[k]; }
            }
        }
        float W = chosenTarget > 0.0 && z > 0.0 ? wsum / (z * chosenTarget) : 0.0;
        if (W > 0.0 && chosen.shadow && Occluded(tlas, origin, chosen.direction, max(chosen.distance * 0.999 - frame.normalBias * 2.0, 0.0))) { W = 0.0; }
        if (W > 0.0 && isfinite(W))
        {
            RWTexture2D<float4> output = ResourceDescriptorHeap[HEAP_RADIANCE_UAV];
            float4 value = output[pixel];
            value.rgb += ClampFirefly(chosen.value * W);
            output[pixel] = value;
        }
        history.Store4(ho, chosen.id);
        history.Store2(ho + 16u, asuint(float2(isfinite(W) ? W : 0.0, min(chosenTarget > 0.0 ? z : count, RESERVOIR_HISTORY * RESERVOIR_INITIAL))));
        return;
    }

    if (mode == 3)
    {
        float3 viewer = frame.views[0].position.xyz;
        uint rng = Hash(index.x * 7919u + index.y * 104729u) ^ Hash(frame.frameIndex * 15485863u + 5u);
        float z = 1.0 - 2.0 * Random(rng), phi = 2.0 * PI * Random(rng);
        float r = sqrt(max(0.0, 1.0 - z * z));
        RayDesc ray;
        ray.Origin = viewer;
        ray.Direction = float3(r * cos(phi), r * sin(phi), z);
        ray.TMin = 0.05;
        ray.TMax = frame.maxDistance;
        Payload payload = (Payload)0;
        HitObj h = TraceHit(tlas, RAY_FLAG_NONE, MASK_VISIBLE, ray, payload);
        if (!h.IsHit()) { return; }
        Hit hit = ResolveHit(h, 0.35);
        if ((frame.flags & FLAG_BAKED_HYBRID) != 0 && LoadInstance(hit.instanceIndex).lightmap >= 0) { return; }
        float3 position = hit.position + hit.geometricNormal * frame.normalBias;
        float levelRandom = Random(rng);
        if (CacheSettled(position, hit.geometricNormal, viewer, levelRandom) && (Hash(index.x + index.y * 64u + frame.frameIndex) & 15u) != 0u) { return; }
        Bounce ignored;
        float2 rotation = float2(Random(rng), Random(rng));
        SH sh = GatherSH(tlas, gbuffer, position, hit.normal, hit.geometricNormal, viewer, 2u, rotation, reorder, rng, ignored);
        CacheAccumulate(position, hit.geometricNormal, viewer, sh, levelRandom, float2(Random(rng), Random(rng)));
        return;
    }

    if (mode == 2)
    {
        uint ray = index.x % PROBE_RAYS;
        uint2 tile = uint2(index.x / PROBE_RAYS, index.y);
        uint tilesPerView = (frame.width + PROBE_TILE - 1u) / PROBE_TILE;
        uint viewIndex = min(tile.x / tilesPerView, frame.viewCount - 1u);
        uint tx = tile.x - viewIndex * tilesPerView;
        RWByteAddressBuffer probesOut = ResourceDescriptorHeap[frame.contextBase + CTX_PROBES + frame.historyWrite];
        uint probe = ProbeIndex(tile);
        uint2 local = 0, pixel = 0;
        Surface surface = (Surface)0;
        [loop] for (uint attempt = 0; attempt < 4 && !surface.valid; attempt++)
        {
            uint h = Hash(tile.x * 7919u + tile.y * 104729u + frame.frameIndex * 1299709u + attempt * 15485863u);
            uint2 offset = attempt == 0 ? uint2(PROBE_TILE / 2u, PROBE_TILE / 2u) : uint2(h % PROBE_TILE, (h >> 8) % PROBE_TILE);
            local = min(uint2(tx * PROBE_TILE, tile.y * PROBE_TILE) + offset, uint2(frame.width - 1u, frame.height - 1u));
            pixel = local + uint2(viewIndex * frame.width, 0);
            surface = LoadSurface(gbuffer, pixel, viewIndex);
        }
        if (!surface.valid) { if (ray == 0u) { StoreProbe(probesOut, probe, 0.0, 0.0, 0.0, 0.0, ShZero()); } return; }
        float3 viewer = frame.views[viewIndex].position.xyz;
        float3 geometric = PrimaryGeometric(gbuffer, pixel, local, viewIndex, surface);
        float3 toViewer = normalize(viewer - surface.position);
        float3 origin = surface.position + (geometric + toViewer) * frame.normalBias * (1.0 + surface.distance * 0.01);
        uint rng = Hash(probe * 9781u) ^ Hash(frame.frameIndex * 6271u + 3u);
        float2 rotation = float2(Random(rng), Random(rng));
        rng = Hash(rng ^ ((ray + 1u) * 0x9e3779b9u));
        SH sh = ShZero();
        Bounce ignored;
        GatherRay(tlas, gbuffer, origin, surface.normal, geometric, viewer, ray, PROBE_RAYS, rotation, reorder, rng, sh, ignored);
        if (ray == 0u)
        {
            GatherFarLight(tlas, origin, surface.normal, viewer, rng, sh);
            probesOut.Store4(probe * PROBE_STRIDE, asuint(float4(surface.position, 1.0)));
            probesOut.Store4(probe * PROBE_STRIDE + 16, asuint(float4(surface.normal, 0.0)));
        }
        RWByteAddressBuffer partials = ResourceDescriptorHeap[frame.contextBase + CTX_PROBE_RAYS];
        CacheStoreSH(partials, (probe * PROBE_RAYS + ray) * CACHE_STRIDE, sh, 0.0);
        return;
    }

    uint2 pixel = index;
    if (mode == 0)
    {
        uint h = Hash(index.x * 73856093u ^ index.y * 19349663u ^ frame.frameIndex * 83492791u);
        pixel = index * 2u + uint2(h & 1u, (h >> 1) & 1u);
        if (pixel.x >= frame.width * frame.viewCount || pixel.y >= frame.height) { return; }
    }
    uint viewIndex;
    uint2 local = ViewPixel(pixel, viewIndex);
    RWTexture2D<float4> output = ResourceDescriptorHeap[HEAP_RADIANCE_UAV];
    Surface surface = LoadSurface(gbuffer, pixel, viewIndex);
    if (!surface.valid)
    {
        if (mode == 1)
        {
            output[pixel] = float4(0.0, 0.0, 0.0, 1.0);
            RWByteAddressBuffer reservoirs = ResourceDescriptorHeap[frame.contextBase + CTX_RESERVOIR_SCRATCH];
            reservoirs.Store2((pixel.y * frame.width * frame.viewCount + pixel.x) * RESERVOIR_SCRATCH_STRIDE + 16u, uint2(0u, 0u));
        }
        return;
    }
    float3 viewer = frame.views[viewIndex].position.xyz;
    float3 geometric = PrimaryGeometric(gbuffer, pixel, local, viewIndex, surface);
    uint rng = Hash(pixel.x + pixel.y * 65536u) ^ Hash(frame.frameIndex * 7919u + (mode == 0 ? 17u : 91u));
    float3 toViewer = normalize(viewer - surface.position);
    float bias = frame.normalBias * (1.0 + surface.distance * 0.01);
    float3 origin = surface.position + (geometric + toViewer) * bias;
    bool avatarPixel = false;
    if (mode != 0) { avatarPixel = PrimarySurface(tlas, viewer, surface, geometric); }
    else if (!ReusePrimary(pixel, surface, geometric, origin)) { PrimarySurface(tlas, viewer, surface, geometric); }

    bool baked = (frame.flags & FLAG_BAKED_HYBRID) != 0 && surface.receive < 0.5;
    if (mode == 0)
    {
        if (baked) { return; }
        float3 position = origin, normal = surface.normal, plane = geometric;
        uint vertices = clamp(frame.bounces & 0xffu, 1u, 4u);
        for (uint v = 0; v < vertices; v++)
        {
            float levelRandom = Random(rng);
            if (CacheSettled(position, plane, viewer, levelRandom) && (Hash(pixel.x * 31u + pixel.y * 17u + frame.frameIndex) & 15u) != 0u) { break; }
            Bounce next;
            float2 rotation = v == 0 ? BlueNoise2(index, 4u, frame.frameIndex) : float2(Random(rng), Random(rng));
            SH sh = GatherSH(tlas, gbuffer, position, normal, plane, viewer, v == 0 ? max(frame.samples, 2u) : 2u, rotation, reorder, rng, next);
            CacheAccumulate(position, plane, viewer, sh, levelRandom, float2(Random(rng), Random(rng)));
            if (!next.hit || next.terminal) { break; }
            position = next.position;
            normal = next.normal;
            plane = next.geometric;
        }
        return;
    }

    float3 emitted = 0.0;
    uint avatarRays = frame.bounces >> 8;
    bool direct = avatarPixel && avatarRays > 0u && !baked;
    float nearRadius = baked || direct ? 1e9 : CacheCellSize(surface.position, viewer) * 3.0;
    uint part = direct ? 0u : 1u;
    {
        RWByteAddressBuffer reservoirs = ResourceDescriptorHeap[frame.contextBase + CTX_RESERVOIR_SCRATCH];
        LightCandidate chosen = (LightCandidate)0;
        float chosenTarget = 0.0, wsum = 0.0, count = 0.0;
        if (frame.emitterCount > 0)
        {
            GridCell g = LoadGridCell(origin);
            if (GridNoEmitters(g, part)) { count += RESERVOIR_INITIAL; }
            else [loop] for (uint i = 0; i < RESERVOIR_INITIAL; i++)
            {
                LightCandidate c = EmitterCandidate(g, origin, surface.normal, rng, part, nearRadius);
                count += 1.0;
                float t = Luminance(c.value);
                if (t <= 0.0 || c.pdf <= 0.0) { continue; }
                float w = t / c.pdf;
                wsum += w;
                if (Random(rng) * wsum < w) { chosen = c; chosenTarget = t; }
            }
            float2 previousPixel;
            bool reproject = (frame.flags & FLAG_RESET_HISTORY) == 0 && ((frame.flags & FLAG_MOTION) != 0 ? true : ProjectToPixel(frame.views[viewIndex].previousViewProjection, surface.position, previousPixel));
            if ((frame.flags & FLAG_MOTION) != 0) { previousPixel = float2(local) + 0.5 - surface.motion; }
            if (reproject && all(previousPixel >= 0.0) && previousPixel.x < float(frame.width) && previousPixel.y < float(frame.height))
            {
                uint2 q = uint2(previousPixel) + uint2(viewIndex * frame.width, 0);
                RWTexture2D<float4> previousGeometry = ResourceDescriptorHeap[HEAP_GEOMETRY_UAV + frame.historyRead];
                float4 g = previousGeometry[q];
                if (!all(g == 0.0) && abs(dot(g.xyz - surface.position, surface.normal)) < max(0.03, surface.distance * 0.02) && dot(UnpackNormal(asuint(g.w)), surface.normal) > 0.8)
                {
                    RWByteAddressBuffer previous = ResourceDescriptorHeap[frame.contextBase + CTX_RESERVOIRS + frame.historyRead];
                    uint o = (q.y * frame.width * frame.viewCount + q.x) * RESERVOIR_STRIDE;
                    uint4 id = previous.Load4(o);
                    float2 wm = asfloat(previous.Load2(o + 16u));
                    if (wm.y > 0.0)
                    {
                        count += min(wm.y, RESERVOIR_HISTORY * RESERVOIR_INITIAL);
                        if (wm.x > 0.0 && isfinite(wm.x) && id.x < frame.instanceCount)
                        {
                            LightCandidate p = EvaluateStored(id, origin, surface.normal, nearRadius);
                            float t = Luminance(p.value);
                            float w = t * wm.x * min(wm.y, RESERVOIR_HISTORY * RESERVOIR_INITIAL);
                            if (w > 0.0 && isfinite(w))
                            {
                                wsum += w;
                                if (Random(rng) * wsum < w) { chosen = p; chosenTarget = t; }
                            }
                        }
                    }
                }
            }
        }
        float W = chosenTarget > 0.0 && count > 0.0 ? wsum / (count * chosenTarget) : 0.0;
        uint o = (pixel.y * frame.width * frame.viewCount + pixel.x) * RESERVOIR_SCRATCH_STRIDE;
        reservoirs.Store4(o, chosen.id);
        reservoirs.Store4(o + 16u, asuint(float4(isfinite(W) ? W : 0.0, count, nearRadius, 0.0)));
        reservoirs.Store4(o + 32u, asuint(float4(origin, asfloat(PackNormal(surface.normal)))));
    }

    if (baked)
    {
        float3 added = 0.0;
        float blocked = 0.0;
        const uint dynamicRays = 4;
        for (uint r = 0; r < dynamicRays; r++)
        {
            float3 direction = CosineSample(surface.normal, float2(Random(rng), Random(rng)));
            if (dot(direction, geometric) <= 0.0) { direction = normalize(direction - 2.0 * dot(direction, geometric) * geometric); }
            RayDesc ray;
            ray.Origin = origin;
            ray.Direction = direction;
            ray.TMin = 0.0;
            ray.TMax = min(frame.maxDistance, 6.0);
            Payload payload = (Payload)0;
            HitObj h = TraceHit(tlas, RAY_FLAG_NONE, MASK_DYNAMIC, ray, payload);
            if (!h.IsHit()) { continue; }
            Hit hit = ResolveHit(h, 0.35);
            Material material = LoadMaterial(hit.materialIndex);
            float4 baseColor = material.baseColor * SampleTexture(material.baseTexture, hit.uv0, material.baseST, hit.lod, 512.0);
            float3 albedo = saturate(baseColor.rgb) * (1.0 - material.metallic * 0.75);
            float3 own = 0.0;
            if ((material.flags & MATERIAL_EMITTER) == 0 && (hit.front || (material.flags & MATERIAL_DOUBLE_SIDED) != 0u))
            {
                own = MaterialEmission(material, hit.uv0, hit.lod, 512.0, baseColor.rgb) * frame.emissionScale;
            }
            float3 hitOrigin = hit.position + hit.geometricNormal * frame.normalBias;
            float coverage;
            float3 lit = HitLighting(tlas, hitOrigin, hit.position, hit.normal, hit.geometricNormal, viewer, true, rng, coverage);
            added += ClampFirefly(own + albedo * lit);
            blocked += saturate(1.0 - h.GetRayTCurrent() / 6.0);
        }
        added /= dynamicRays;
        blocked /= dynamicRays;
        float3 bakedIrradiance = surface.radiance / max(surface.albedo, 0.05);
        float3 result = emitted + added - bakedIrradiance * blocked * 0.5;
        float3 positive = max(result, 0.0);
        float lost = Luminance(max(-result, 0.0)) / max(Luminance(bakedIrradiance), 1e-3);
        if (!all(isfinite(positive))) { positive = 0.0; }
        output[pixel] = float4(positive, saturate(1.0 - lost));
        return;
    }

    float4 cached = direct ? float4(0.0, 0.0, 0.0, 1.0) : CacheLookup(surface.position, geometric, viewer, BlueNoise1(pixel, 0u, frame.frameIndex));
    float3 irradiance = cached.rgb;
    if (cached.a < 0.5)
    {
        uint fresh = clamp(frame.samples / 2u, 1u, 8u);
        float3 traced = 0.0;
        for (uint ri = 0; ri < fresh; ri++)
        {
            float3 direction = CosineSample(surface.normal, BlueNoise2(pixel, 1u, frame.frameIndex * fresh + ri));
            if (dot(direction, geometric) <= 0.0) { direction = normalize(direction - 2.0 * dot(direction, geometric) * geometric); }
            Bounce b = TraceRadiance(tlas, gbuffer, origin, direction, surface.normal, viewer, false, rng);
            traced += ClampFirefly(b.radiance);
        }
        irradiance = lerp(traced / fresh + FarLight(tlas, origin, surface.normal, nearRadius, rng), cached.rgb, saturate(cached.a * 2.0));
    }
    if (direct)
    {
        float3 traced = 0.0;
        for (uint ar = 0; ar < avatarRays; ar++)
        {
            float3 direction = CosineSample(surface.normal, BlueNoise2(pixel, 2u, frame.frameIndex * avatarRays + ar));
            if (dot(direction, geometric) <= 0.0) { direction = normalize(direction - 2.0 * dot(direction, geometric) * geometric); }
            Bounce b = TraceRadiance(tlas, gbuffer, origin, direction, surface.normal, viewer, false, rng);
            traced += ClampFirefly(b.radiance);
        }
        irradiance = traced / avatarRays;
    }

    if ((frame.flags & FLAG_PROBES) != 0 && !direct)
    {
        RWByteAddressBuffer probes = ResourceDescriptorHeap[frame.contextBase + CTX_PROBES + frame.historyWrite];
        uint tilesPerView = (frame.width + PROBE_TILE - 1u) / PROBE_TILE, tilesY = (frame.height + PROBE_TILE - 1u) / PROBE_TILE;
        float2 f = (float2(local) + 0.5) / float(PROBE_TILE) - 0.5;
        int2 t0 = int2(floor(f));
        float2 fr = f - float2(t0);
        float3 probeSum = 0.0;
        float probeWeight = 0.0;
        float planeScale = max(0.03, surface.distance * 0.02);
        [unroll] for (uint k = 0; k < 4; k++)
        {
            int2 t = clamp(t0 + int2(k & 1u, k >> 1), int2(0, 0), int2(tilesPerView - 1, tilesY - 1));
            float w = ((k & 1u) ? fr.x : 1.0 - fr.x) * ((k >> 1) ? fr.y : 1.0 - fr.y);
            uint o = ProbeIndex(uint2(t.x + viewIndex * tilesPerView, t.y)) * PROBE_STRIDE;
            float4 pp = asfloat(probes.Load4(o)), pn = asfloat(probes.Load4(o + 16));
            if (pp.w <= 0.5) { continue; }
            float plane = abs(dot(pp.xyz - surface.position, geometric));
            float facing = saturate(dot(pn.xyz, surface.normal));
            w *= exp(-plane / planeScale) * facing * facing * saturate((pn.w - 2.0) / 6.0);
            if (w <= 1e-4) { continue; }
            float ignored;
            probeSum += ShIrradiance(CacheLoadSH(probes, o + 32, ignored), surface.normal, pn.xyz) * w;
            probeWeight += w;
        }
        if (probeWeight > 1e-4) { irradiance = lerp(irradiance, probeSum / probeWeight, saturate(probeWeight * 2.0)); }
    }

    float grazing = saturate(dot(geometric, toViewer) * 6.0);
    float occlusion = 0.0;
    uint aoRays = 2;
    if (frame.aoRadius > 0.0)
    {
        for (uint s = 0; s < aoRays; s++)
        {
            float3 direction = CosineSample(surface.normal, BlueNoise2(pixel, 3u, frame.frameIndex * aoRays + s));
            if (dot(direction, geometric) <= 0.0) { direction = normalize(direction - 2.0 * dot(direction, geometric) * geometric); }
            RayDesc ray;
            ray.Origin = origin;
            ray.Direction = direction;
            ray.TMin = bias * 4.0;
            ray.TMax = frame.aoRadius;
            Payload payload = (Payload)0;
            HitObj h = TraceHit(tlas, RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_SKIP_CLOSEST_HIT_SHADER | RAY_FLAG_FORCE_OPAQUE, MASK_VISIBLE, ray, payload);
            if (h.IsHit()) { float o = 1.0 - h.GetRayTCurrent() / frame.aoRadius; occlusion += o * o; }
        }
        occlusion /= aoRays;
    }
    occlusion *= grazing;
    float visibility = 1.0 - occlusion;
    irradiance = irradiance * (0.5 + 0.5 * visibility) + emitted;
    if (!all(isfinite(irradiance))) { irradiance = 0.0; }
    output[pixel] = float4(irradiance, saturate(1.0 - frame.aoIntensity * occlusion));
}
