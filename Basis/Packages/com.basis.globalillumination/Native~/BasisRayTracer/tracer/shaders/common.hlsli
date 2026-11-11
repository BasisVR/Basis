#ifndef BASIS_RT_COMMON
#define BASIS_RT_COMMON

#define HEAP_TLAS 0
#define HEAP_VERTICES 1
#define HEAP_INDICES 2
#define HEAP_MESHES 3
#define HEAP_SUBMESHES 4
#define HEAP_VERTICES_UAV 5
#define HEAP_INDICES_UAV 6
#define HEAP_UPLOAD 7
#define HEAP_INSTANCES 16
#define HEAP_MATERIALS 24
#define HEAP_LIGHTS 32
#define HEAP_EMITTERS 40
#define HEAP_GBUFFER (frame.contextBase + 0)
#define HEAP_RADIANCE_UAV (frame.contextBase + 2)
#define HEAP_HISTORY_UAV (frame.contextBase + 3)
#define HEAP_MOMENTS_UAV (frame.contextBase + 5)
#define HEAP_GEOMETRY_UAV (frame.contextBase + 7)
#define HEAP_FILTER_UAV (frame.contextBase + 9)
#define HEAP_COMPACT_UAV (frame.contextBase + 17)
#define HEAP_TEXTURES 1024
#define HEAP_TEXTURE_UAVS (1024 + 4096)
#define MAX_TEXTURES 4096

#define FLAG_RESET_HISTORY 1
#define FLAG_SCREEN_REUSE 2
#define FLAG_SHADOWS 4
#define FLAG_DENOISE 8
#define FLAG_REORDER 32
#define FLAG_CACHE_RESET 64
#define FLAG_PROBES 128
#define FLAG_MOTION 512
#define FLAG_BAKED_HYBRID 256
#define FLAG_WORLD_SPACE 1024
#define MASK_DYNAMIC 2
#define MASK_VISIBLE 0xFB
#define HEAP_DIRTY 56
#define HEAP_RING 9
#define HEAP_BOUNDS 54

#define SUBMESH_OPAQUE 1
#define SUBMESH_ALPHA_TEST 2
#define SUBMESH_BLEND 4

#define ALPHA_OPAQUE 0
#define ALPHA_CUTOUT 1
#define ALPHA_BLEND 2
#define MATERIAL_DOUBLE_SIDED 1
#define MATERIAL_UNLIT 2
#define MATERIAL_EMITTER 8
#define INSTANCE_AVATAR 1
#define INSTANCE_EMITTER 4
#define INSTANCE_DYNAMIC 8

#define LIGHT_DIRECTIONAL 0
#define LIGHT_POINT 1
#define LIGHT_SPOT 2

#define PI 3.14159265

struct View
{
    float4x4 view;
    float4x4 projection;
    float4x4 inverseViewProjection;
    float4x4 previousViewProjection;
    float4 position;
};

struct Frame
{
    uint frameLow, frameHigh, instanceCount, materialCount, lightCount, commandCount, uploadTexels, flags;
    uint width, height, viewCount, samples, bounces;
    float maxDistance, aoRadius, emissionScale, skyIntensity, time, fireflyClamp, temporalResponse, lightIntensity, normalBias;
    uint emitterCount;
    float aoIntensity;
    uint context, parity;
    float cacheCellSize;
    uint dirtyCount;
    float4 ambient[7];
    View views[2];
    uint slot, gbufferIndex, historyRead, historyWrite;
    float deltaTime;
    uint debugView, frameIndex, contextBase;
};

struct Instance
{
    float4 row0, row1, row2;
    uint mesh, materialBase, mask, flags;
    float4 lightmapST;
    int lightmap;
    uint previous, pad0, pad1;
};

struct Material
{
    float4 baseColor;
    float4 emission;
    float4 baseST;
    float4 emissionST;
    int baseTexture, emissionTexture;
    uint alphaMode;
    float cutoff, metallic;
    uint emission2;
    uint flags;
    float emissionBase;
};

struct Light
{
    float4 position;
    float4 direction;
    float4 color;
    float4 spot;
};

struct Emitter
{
    uint instance, geometry, triangleCount;
    float cdf;
};

struct Mesh
{
    uint vertexOffset, indexOffset, submeshOffset, submeshCount;
};

struct Submesh
{
    uint indexStart, indexCount, flags, pad;
};

[[vk::binding(0, 0)]] ConstantBuffer<Frame> frame : register(b0);

struct PassConstants
{
    uint a, b, c, d, e, f, g, h;
};
[[vk::push_constant]] ConstantBuffer<PassConstants> pass : register(b1);

[[vk::binding(1, 0)]] SamplerState linearWrap : register(s0);
[[vk::binding(2, 0)]] SamplerState linearClamp : register(s1);
[[vk::binding(3, 0)]] SamplerState pointClamp : register(s2);

uint Hash(uint x)
{
    x ^= x >> 16; x *= 0x7feb352d; x ^= x >> 15; x *= 0x846ca68b; x ^= x >> 16;
    return x;
}

float Random(inout uint state)
{
    state = state * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    return ((word >> 22u) ^ word) * (1.0 / 4294967296.0);
}

#define HEAP_BLUE_NOISE 10

uint2 BlueNoiseBits(uint2 pixel, uint dimension)
{
    ByteAddressBuffer b = ResourceDescriptorHeap[HEAP_BLUE_NOISE];
    uint2 q = (pixel + uint2(dimension * 37u, dimension * 71u)) & 127u;
    uint v = b.Load((q.y * 128u + q.x) * 4u);
    return uint2(v << 16, v & 0xffff0000u);
}

float2 BlueNoise2(uint2 pixel, uint dimension, uint index)
{
    uint2 v = BlueNoiseBits(pixel, dimension) + index * uint2(3242174889u, 2447445413u);
    return float2(v >> 8) * (1.0 / 16777216.0);
}

float BlueNoise1(uint2 pixel, uint dimension, uint index)
{
    return float((BlueNoiseBits(pixel, dimension).x + index * 2654435769u) >> 8) * (1.0 / 16777216.0);
}

float2 OctWrap(float2 v) { return (1.0 - abs(v.yx)) * select(v.xy >= 0.0, 1.0, -1.0); }

uint PackNormal(float3 n)
{
    n /= abs(n.x) + abs(n.y) + abs(n.z);
    float2 o = n.z >= 0.0 ? n.xy : OctWrap(n.xy);
    int2 q = int2(round(clamp(o, -1.0, 1.0) * 32767.0));
    return (uint(q.x) & 0xffff) | (uint(q.y) << 16);
}

float3 UnpackNormal(uint p)
{
    float2 o = float2(int(p << 16) >> 16, int(p) >> 16) / 32767.0;
    float3 n = float3(o, 1.0 - abs(o.x) - abs(o.y));
    float t = saturate(-n.z);
    n.xy += select(n.xy >= 0.0, -t, t);
    return normalize(n);
}

float3 UnpackRGB9E5(uint p)
{
    float scale = exp2(float(int(p >> 27) - 15 - 9));
    return float3(p & 0x1ff, (p >> 9) & 0x1ff, (p >> 18) & 0x1ff) * scale;
}

float4 UnpackRGBA8(uint p) { return float4(p & 0xff, (p >> 8) & 0xff, (p >> 16) & 0xff, p >> 24) / 255.0; }

float3 Luminance3(float3 c) { return dot(c, float3(0.2126, 0.7152, 0.0722)).xxx; }
float Luminance(float3 c) { return dot(c, float3(0.2126, 0.7152, 0.0722)); }

float3 SampleAmbient(float3 n)
{
    float4 v = float4(n, 1.0);
    float3 l1 = float3(dot(frame.ambient[0], v), dot(frame.ambient[1], v), dot(frame.ambient[2], v));
    float4 q = n.xyzz * n.yzzx;
    float3 l2 = float3(dot(frame.ambient[3], q), dot(frame.ambient[4], q), dot(frame.ambient[5], q));
    float c = n.x * n.x - n.y * n.y;
    return max(0.0, l1 + l2 + frame.ambient[6].rgb * c) * frame.skyIntensity;
}

float3 CosineSample(float3 n, float2 u)
{
    float3 t = abs(n.z) < 0.999 ? normalize(cross(n, float3(0, 0, 1))) : normalize(cross(n, float3(1, 0, 0)));
    float3 b = cross(n, t);
    float r = sqrt(u.x), phi = 2.0 * PI * u.y;
    return normalize(t * (r * cos(phi)) + b * (r * sin(phi)) + n * sqrt(max(0.0, 1.0 - u.x)));
}

uint2 ViewPixel(uint2 pixel, out uint viewIndex)
{
    viewIndex = pixel.x / frame.width;
    return uint2(pixel.x - viewIndex * frame.width, pixel.y);
}

bool ProjectToPixel(float4x4 viewProjection, float3 world, out float2 pixel)
{
    float4 clip = mul(viewProjection, float4(world, 1.0));
    pixel = 0;
    if (clip.w <= 1e-4) { return false; }
    float2 ndc = clip.xy / clip.w;
    pixel = (ndc * 0.5 + 0.5) * float2(frame.width, frame.height);
    return all(ndc > -1.0) && all(ndc < 1.0);
}

bool PreviousPixel(float3 position, float2 motion, uint2 local, uint viewIndex, out float2 pixel, out bool inside)
{
    pixel = 0.0;
    inside = false;
    if ((frame.flags & FLAG_RESET_HISTORY) != 0) { return false; }
    if ((frame.flags & FLAG_MOTION) != 0) { pixel = float2(local) + 0.5 - motion; }
    else
    {
        float4 clip = mul(frame.views[viewIndex].previousViewProjection, float4(position, 1.0));
        if (clip.w <= 1e-4) { return false; }
        pixel = (clip.xy / clip.w * 0.5 + 0.5) * float2(frame.width, frame.height);
    }
    if (!all(isfinite(pixel))) { return false; }
    inside = all(pixel > 0.0) && pixel.x < float(frame.width) && pixel.y < float(frame.height);
    return true;
}

float HistoryTolerance(float3 position, float range, float2 previousPixel, uint viewIndex)
{
    float tolerance = max(0.03, range * 0.02);
    if ((frame.flags & FLAG_MOTION) == 0) { return tolerance; }
    float4 clip = mul(frame.views[viewIndex].previousViewProjection, float4(position, 1.0));
    if (clip.w <= 1e-4) { return tolerance; }
    float2 still = (clip.xy / clip.w * 0.5 + 0.5) * float2(frame.width, frame.height);
    float slip = max(length(previousPixel - still) - 1.0, 0.0) * range * 2.0 / (abs(frame.views[viewIndex].projection[1][1]) * float(frame.height));
    return tolerance + 2.0 * slip;
}

struct Surface
{
    float3 position;
    float distance;
    float3 normal;
    float3 albedo;
    float3 radiance;
    float receive;
    float2 motion;
    bool valid;
};

uint4 Load128(Texture2D<uint4> t, uint2 logical)
{
    uint4 lo = t.Load(int3(logical.x, logical.y * 2, 0));
    uint4 hi = t.Load(int3(logical.x, logical.y * 2 + 1, 0));
    return uint4(lo.x | (lo.y << 16), lo.z | (lo.w << 16), hi.x | (hi.y << 16), hi.z | (hi.w << 16));
}

Surface LoadSurface(Texture2D<uint4> gbuffer, uint2 pixel, uint viewIndex)
{
    uint4 a = Load128(gbuffer, uint2(pixel.x * 2, pixel.y));
    uint4 b = Load128(gbuffer, uint2(pixel.x * 2 + 1, pixel.y));
    Surface s;
    float3 relative = asfloat(a.xyz);
    s.distance = length(relative);
    s.valid = s.distance > 0.0 && all(isfinite(relative));
    s.position = frame.views[viewIndex].position.xyz + relative;
    s.normal = UnpackNormal(a.w);
    s.albedo = UnpackRGBA8(b.x).rgb;
    s.radiance = UnpackRGB9E5(b.y);
    s.motion = f16tof32(uint2(b.w, b.w >> 16));
    s.receive = b.z == 0u ? 1.0 : asfloat(b.z);
    return s;
}

#endif
