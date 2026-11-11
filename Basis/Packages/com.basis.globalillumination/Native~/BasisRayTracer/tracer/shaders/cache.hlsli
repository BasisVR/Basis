#ifndef BASIS_RT_CACHE
#define BASIS_RT_CACHE

#include "common.hlsli"

#define HEAP_CACHE_KEYS 48
#define HEAP_CACHE_LIST 49
#define HEAP_CACHE_ARGS 55
#define CACHE_LIST_HEADER 16u
#define HEAP_CACHE_ACCUM 50
#define HEAP_CACHE_RESOLVED 51
#define HEAP_CACHE_TOUCHED 52
#define HEAP_CACHE_POSITIONS 53
#define CACHE_SIZE (1u << 20)
#define CACHE_MASK (CACHE_SIZE - 1u)
#define CACHE_PROBES 12
#define CACHE_TOMBSTONE 0xffffffffu
#define CACHE_FIXED 512.0
#define CACHE_SETTLED 256.0
#define CACHE_STRIDE 64u
#define SH_Y0 0.282095
#define SH_Y1 0.488603

struct SH
{
    float3 c0, cx, cy, cz, mean;
};

SH ShZero() { SH s; s.c0 = 0.0; s.cx = 0.0; s.cy = 0.0; s.cz = 0.0; s.mean = 0.0; return s; }

void ShAdd(inout SH s, float3 direction, float3 value)
{
    s.c0 += value * SH_Y0;
    s.cx += value * (SH_Y1 * direction.x);
    s.cy += value * (SH_Y1 * direction.y);
    s.cz += value * (SH_Y1 * direction.z);
}

SH ShScale(SH s, float k) { s.c0 *= k; s.cx *= k; s.cy *= k; s.cz *= k; s.mean *= k; return s; }

SH ShLerp(SH a, SH b, float t) { SH s; s.c0 = lerp(a.c0, b.c0, t); s.cx = lerp(a.cx, b.cx, t); s.cy = lerp(a.cy, b.cy, t); s.cz = lerp(a.cz, b.cz, t); s.mean = lerp(a.mean, b.mean, t); return s; }

float3 ShRaw(SH s, float3 n) { return max(s.c0 * SH_Y0 + (2.0 / 3.0) * SH_Y1 * (s.cx * n.x + s.cy * n.y + s.cz * n.z), s.c0 * SH_Y0 * 0.25); }

float3 ShIrradiance(SH s, float3 n, float3 n0)
{
    float reference = Luminance(ShRaw(s, n0));
    float ratio = reference > 1e-5 ? clamp(Luminance(ShRaw(s, n)) / reference, 0.25, 4.0) : 1.0;
    return max(s.mean, 0.0) * ratio;
}

float3 AxisNormal(uint axis) { float3 n = 0.0; n[axis >> 1] = (axis & 1u) ? -1.0 : 1.0; return n; }

struct CacheCell
{
    uint a, b;
};

struct CacheFrame
{
    int3 base;
    float size;
    uint level, axis;
    float3 fraction;
};

uint CacheAxis(float3 n)
{
    float3 m = abs(n);
    uint axis = m.x > m.y ? (m.x > m.z ? 0u : 2u) : (m.y > m.z ? 1u : 2u);
    return axis * 2u + (n[axis] < 0.0 ? 1u : 0u);
}

float CacheCellSize(float3 p, float3 viewer)
{
    float levelF = log2(1.0 + length(p - viewer) / max(frame.cacheCellSize * 40.0, 0.5));
    return max(frame.cacheCellSize, 0.02) * exp2(levelF);
}

CacheFrame CacheLocate(float3 p, float3 n, float3 viewer, float rnd, float levelBias = 0.0)
{
    CacheFrame f;
    float distance = length(p - viewer);
    float levelF = log2(1.0 + distance / max(frame.cacheCellSize * 40.0, 0.5)) + levelBias;
    f.level = min(uint(levelF) + (rnd < frac(levelF) ? 1u : 0u), 12u);
    f.size = max(frame.cacheCellSize, 0.02) * exp2(float(f.level));
    float3 scaled = p / f.size;
    f.base = int3(floor(scaled - 0.5));
    f.fraction = scaled - 0.5 - float3(f.base);
    f.axis = CacheAxis(n);
    return f;
}

CacheCell CacheKey(int3 q, uint level, uint axis)
{
    CacheCell c;
    uint h = Hash(level * 8u + axis + 0x51ed27u);
    h = Hash(h ^ asuint(q.x));
    h = Hash(h ^ asuint(q.y) * 0x85ebca6bu);
    h = Hash(h ^ asuint(q.z) * 0xc2b2ae35u);
    c.a = clamp(h, 1u, 0xfffffffeu);
    c.b = Hash(h ^ 0x9e3779b9u ^ asuint(q.x) * 31u ^ asuint(q.z) * 17u) | 1u;
    return c;
}

int CacheFind(CacheCell c)
{
    RWByteAddressBuffer keys = ResourceDescriptorHeap[HEAP_CACHE_KEYS];
    uint index = c.a & CACHE_MASK;
    [loop] for (uint i = 0; i < CACHE_PROBES; i++)
    {
        uint2 k = keys.Load2(index * 8);
        if (k.x == 0) { return -1; }
        if (k.x == c.a && k.y == c.b) { return int(index); }
        index = (index + 1u) & CACHE_MASK;
    }
    return -1;
}

bool CacheClaim(RWByteAddressBuffer keys, uint index, uint expected, CacheCell c, float3 centre, float size)
{
    uint original;
    keys.InterlockedCompareExchange(index * 8, expected, c.a, original);
    if (original == expected)
    {
        keys.Store(index * 8 + 4, c.b);
        RWByteAddressBuffer positions = ResourceDescriptorHeap[HEAP_CACHE_POSITIONS];
        positions.Store4(index * 16, asuint(float4(centre, size)));
        return true;
    }
    if (original != c.a) { return false; }
    uint check = keys.Load(index * 8 + 4);
    return check == c.b || check == 0u;
}

int CacheInsert(CacheCell c, float3 centre, float size)
{
    RWByteAddressBuffer keys = ResourceDescriptorHeap[HEAP_CACHE_KEYS];
    uint index = c.a & CACHE_MASK;
    int reuse = -1;
    [loop] for (uint i = 0; i < CACHE_PROBES; i++)
    {
        uint original = keys.Load(index * 8);
        if (original == c.a)
        {
            uint check = keys.Load(index * 8 + 4);
            if (check == c.b || check == 0u) { return int(index); }
        }
        else if (original == CACHE_TOMBSTONE) { if (reuse < 0) { reuse = int(index); } }
        else if (original == 0u)
        {
            if (reuse >= 0 && CacheClaim(keys, uint(reuse), CACHE_TOMBSTONE, c, centre, size)) { return reuse; }
            reuse = -1;
            if (CacheClaim(keys, index, 0u, c, centre, size)) { return int(index); }
        }
        index = (index + 1u) & CACHE_MASK;
    }
    if (reuse >= 0 && CacheClaim(keys, uint(reuse), CACHE_TOMBSTONE, c, centre, size)) { return reuse; }
    return -1;
}

int3 Fixed(float3 v, float3 dither) { return int3(floor(clamp(v * CACHE_FIXED, -2.0e9, 2.0e9) + dither)); }

void AddFixed(RWByteAddressBuffer b, uint offset, int3 q)
{
    uint ignored;
    b.InterlockedAdd(offset + 0, asuint(q.x), ignored);
    b.InterlockedAdd(offset + 4, asuint(q.y), ignored);
    b.InterlockedAdd(offset + 8, asuint(q.z), ignored);
}

bool ShFinite(SH s) { return all(isfinite(s.c0)) && all(isfinite(s.cx)) && all(isfinite(s.cy)) && all(isfinite(s.cz)) && all(isfinite(s.mean)); }

void CacheAccumulate(float3 p, float3 n, float3 viewer, SH value, float rnd, float2 jitter, float levelBias = 0.0)
{
    if (!ShFinite(value)) { return; }
    CacheFrame f = CacheLocate(p, n, viewer, rnd, levelBias);
    uint axis = f.axis >> 1;
    float3 offset = 0.0;
    offset[axis == 0 ? 1u : 0u] = (jitter.x - 0.5) * 0.5 * f.size;
    offset[axis == 2 ? 1u : 2u] = (jitter.y - 0.5) * 0.5 * f.size;
    int3 q = int3(floor((p + offset) / f.size));
    int index = CacheInsert(CacheKey(q, f.level, f.axis), (float3(q) + 0.5) * f.size, f.size);
    if (index < 0) { return; }
    uint seed = Hash(asuint(jitter.x) ^ (asuint(jitter.y) * 0x9e3779b9u) ^ asuint(rnd));
    float3 dither = float3(Random(seed), Random(seed), Random(seed));
    int3 r = Fixed(float3(value.c0.r, value.cx.r, value.cy.r), dither), g = Fixed(float3(value.c0.g, value.cx.g, value.cy.g), dither.yzx), b = Fixed(float3(value.c0.b, value.cx.b, value.cy.b), dither.zxy);
    int3 z = Fixed(value.cz, dither), m = Fixed(value.mean, dither.zyx);
    RWByteAddressBuffer accum = ResourceDescriptorHeap[HEAP_CACHE_ACCUM];
    uint o = uint(index) * CACHE_STRIDE;
    AddFixed(accum, o + 0, r);
    AddFixed(accum, o + 12, g);
    AddFixed(accum, o + 24, b);
    AddFixed(accum, o + 36, z);
    uint previous;
    accum.InterlockedAdd(o + 48, 1u, previous);
    if (previous == 0u)
    {
        RWByteAddressBuffer list = ResourceDescriptorHeap[HEAP_CACHE_LIST];
        uint at;
        list.InterlockedAdd(0, 1u, at);
        if (at < CACHE_SIZE) { list.Store(CACHE_LIST_HEADER + at * 4, uint(index)); }
    }
    AddFixed(accum, o + 52, m);
}

SH CacheLoadAccum(RWByteAddressBuffer b, uint o, out uint count)
{
    int4 a = asint(b.Load4(o)), c = asint(b.Load4(o + 16)), d = asint(b.Load4(o + 32));
    count = b.Load(o + 48);
    SH s;
    s.c0 = float3(a.x, a.w, c.z);
    s.cx = float3(a.y, c.x, c.w);
    s.cy = float3(a.z, c.y, d.x);
    s.cz = float3(d.y, d.z, d.w);
    int3 m = asint(b.Load3(o + 52));
    s.mean = float3(m);
    return ShScale(s, 1.0 / CACHE_FIXED);
}

SH CacheLoadSH(RWByteAddressBuffer b, uint o, out float samples)
{
    float4 a = asfloat(b.Load4(o)), c = asfloat(b.Load4(o + 16)), d = asfloat(b.Load4(o + 32)), e = asfloat(b.Load4(o + 48));
    SH s;
    s.c0 = float3(a.x, a.w, c.z);
    s.cx = float3(a.y, c.x, c.w);
    s.cy = float3(a.z, c.y, d.x);
    s.cz = float3(d.y, d.z, d.w);
    s.mean = e.yzw;
    samples = e.x;
    return s;
}

void CacheStoreSH(RWByteAddressBuffer b, uint o, SH s, float samples)
{
    b.Store4(o, asuint(float4(s.c0.r, s.cx.r, s.cy.r, s.c0.g)));
    b.Store4(o + 16, asuint(float4(s.cx.g, s.cy.g, s.c0.b, s.cx.b)));
    b.Store4(o + 32, asuint(float4(s.cy.b, s.cz.r, s.cz.g, s.cz.b)));
    b.Store4(o + 48, asuint(float4(samples, s.mean)));
}

float4 CacheLookup(float3 p, float3 n, float3 viewer, float rnd)
{
    CacheFrame f = CacheLocate(p, n, viewer, rnd);
    RWByteAddressBuffer resolved = ResourceDescriptorHeap[HEAP_CACHE_RESOLVED];
    float3 sum = 0.0;
    float weight = 0.0, coverage = 0.0;
    [loop] for (uint k = 0; k < 8; k++)
    {
        int3 o = int3(k & 1u, (k >> 1) & 1u, k >> 2);
        float3 t = select(o != 0, f.fraction, 1.0 - f.fraction);
        float w = t.x * t.y * t.z;
        if (w <= 1e-4) { continue; }
        int index = CacheFind(CacheKey(f.base + o, f.level, f.axis));
        if (index < 0) { continue; }
        float samples;
        SH s = CacheLoadSH(resolved, uint(index) * CACHE_STRIDE, samples);
        if (samples <= 0.0) { continue; }
        float trust = saturate(samples / 16.0);
        sum += ShIrradiance(s, n, AxisNormal(f.axis)) * w * trust;
        weight += w * trust;
        coverage += w;
    }
    return float4(weight > 1e-5 ? sum / weight : 0.0, saturate(weight / max(coverage, 1e-5)) * saturate(coverage * 2.0));
}

float4 CacheLookupLevel(float3 p, float3 geometric, float3 shading, uint level)
{
    float size = max(frame.cacheCellSize, 0.02) * exp2(float(level));
    float3 scaled = p / size;
    int3 base = int3(floor(scaled - 0.5));
    float3 fraction = scaled - 0.5 - float3(base);
    uint axis = CacheAxis(geometric);
    RWByteAddressBuffer resolved = ResourceDescriptorHeap[HEAP_CACHE_RESOLVED];
    float3 sum = 0.0;
    float weight = 0.0;
    [loop] for (uint k = 0; k < 8; k++)
    {
        int3 o = int3(k & 1u, (k >> 1) & 1u, k >> 2);
        float3 t = select(o != 0, fraction, 1.0 - fraction);
        float w = t.x * t.y * t.z;
        if (w <= 1e-4) { continue; }
        int index = CacheFind(CacheKey(base + o, level, axis));
        if (index < 0) { continue; }
        float samples;
        SH s = CacheLoadSH(resolved, uint(index) * CACHE_STRIDE, samples);
        if (samples <= 0.0) { continue; }
        w *= saturate(samples / 32.0);
        sum += ShIrradiance(s, shading, AxisNormal(axis)) * w;
        weight += w;
    }
    return float4(weight > 1e-5 ? sum / weight : 0.0, saturate(weight));
}

float3 CacheViewer(float3 viewer)
{
    return (frame.flags & FLAG_WORLD_SPACE) != 0 && frame.viewCount > 1u ? (frame.views[0].position.xyz + frame.views[1].position.xyz) * 0.5 : viewer;
}

float4 CacheLookupSmooth(float3 p, float3 geometric, float3 shading, float3 viewer, float levelBias)
{
    float3 toViewer = viewer - p;
    float facing = abs(dot(geometric, toViewer)) * rsqrt(max(dot(toViewer, toViewer), 1e-8));
    levelBias += clamp(log2(1.0 / max(facing, 0.25)) - 0.5, 0.0, 1.5);
    float levelF = min(log2(1.0 + length(p - viewer) / max(frame.cacheCellSize * 40.0, 0.5)) + levelBias, 12.0);
    uint level = min(uint(levelF), 11u);
    float t = levelF - float(level);
    float4 fine = CacheLookupLevel(p, geometric, shading, level);
    float4 coarse = CacheLookupLevel(p, geometric, shading, level + 1u);
    float wf = (1.0 - t) * fine.a, wc = (t + (1.0 - t) * (1.0 - fine.a)) * coarse.a;
    float w = wf + wc;
    return float4(w > 1e-5 ? (fine.rgb * wf + coarse.rgb * wc) / w : 0.0, saturate(max(fine.a, coarse.a)));
}

#endif
