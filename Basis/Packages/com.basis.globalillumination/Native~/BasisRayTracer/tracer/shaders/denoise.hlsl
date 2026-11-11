#include "common.hlsli"

#define DENOISE_RESERVOIR_SCRATCH 16

#ifndef HISTORY_BORROW_REACH
#define HISTORY_BORROW_REACH 0.5
#endif
#ifndef HISTORY_BORROW_LENGTH
#define HISTORY_BORROW_LENGTH 3.0
#endif
#ifndef HISTORY_FRESH_CEILING
#define HISTORY_FRESH_CEILING 4.0
#endif

float4 PackGeometry(Surface s) { return float4(s.position, asfloat(PackNormal(s.normal))); }

uint4 PackCompact(float3 offset, float3 n)
{
    n /= abs(n.x) + abs(n.y) + abs(n.z);
    float2 o = n.z >= 0.0 ? n.xy : OctWrap(n.xy);
    uint2 q = uint2(round(saturate(o * 0.5 + 0.5) * 254.0)) + 1u;
    return uint4(f32tof16(clamp(offset, -60000.0, 60000.0)), q.x | (q.y << 8));
}

float3 CompactNormal(uint p)
{
    float2 o = (float2(p & 0xffu, p >> 8) - 1.0) / 127.0 - 1.0;
    float3 n = float3(o, 1.0 - abs(o.x) - abs(o.y));
    float t = saturate(-n.z);
    n.xy += select(n.xy >= 0.0, -t, t);
    return normalize(n);
}

float4 Half(float4 v, uint seed)
{
    float4 a = abs(v);
    uint4 h = f32tof16(a);
    float4 low = f16tof32(h);
    h -= uint4(low > a);
    low = f16tof32(h);
    float4 high = f16tof32(h + 1u);
    uint r = Hash(seed);
    float4 u = float4(r & 0xffu, (r >> 8) & 0xffu, (r >> 16) & 0xffu, r >> 24) * (1.0 / 256.0) + (0.5 / 256.0);
    float4 q = select((a - low) > u * (high - low), high, low);
    return select(v < 0.0, -q, q);
}

[numthreads(8, 8, 1)]
void Temporal(uint3 id : SV_DispatchThreadID)
{
    uint2 pixel = id.xy;
    if (pixel.x >= frame.width * frame.viewCount || pixel.y >= frame.height) { return; }
    uint viewIndex;
    uint2 local = ViewPixel(pixel, viewIndex);
    Texture2D<uint4> gbuffer = ResourceDescriptorHeap[HEAP_GBUFFER + frame.gbufferIndex];
    RWTexture2D<float4> radiance = ResourceDescriptorHeap[HEAP_RADIANCE_UAV];
    RWTexture2D<float4> history = ResourceDescriptorHeap[HEAP_HISTORY_UAV + frame.historyRead];
    RWTexture2D<float4> moments = ResourceDescriptorHeap[HEAP_MOMENTS_UAV + frame.historyRead];
    RWTexture2D<float4> geometry = ResourceDescriptorHeap[HEAP_GEOMETRY_UAV + frame.historyRead];
    RWTexture2D<float4> historyOut = ResourceDescriptorHeap[pass.a];
    RWTexture2D<float4> momentsOut = ResourceDescriptorHeap[HEAP_MOMENTS_UAV + frame.historyWrite];
    RWTexture2D<float4> geometryOut = ResourceDescriptorHeap[HEAP_GEOMETRY_UAV + frame.historyWrite];
    RWTexture2D<uint4> compactOut = ResourceDescriptorHeap[HEAP_COMPACT_UAV];

    Surface s = LoadSurface(gbuffer, pixel, viewIndex);
    if (s.valid)
    {
        RWByteAddressBuffer reservoirs = ResourceDescriptorHeap[frame.contextBase + DENOISE_RESERVOIR_SCRATCH];
        uint packed = reservoirs.Load((pixel.y * frame.width * frame.viewCount + pixel.x) * 64u + 44u);
        float3 shading = UnpackNormal(packed);
        if (packed != 0u && dot(shading, s.normal) > 0.2) { s.normal = shading; }
    }
    float4 current = radiance[pixel];
    if (!all(isfinite(current))) { current = float4(0.0, 0.0, 0.0, 1.0); }
    geometryOut[pixel] = s.valid ? PackGeometry(s) : float4(0.0, 0.0, 0.0, 0.0);
    compactOut[pixel] = s.valid ? PackCompact(s.position - frame.views[viewIndex].position.xyz, s.normal) : uint4(0u, 0u, 0u, 0u);
    if (!s.valid)
    {
        historyOut[pixel] = float4(0.0, 0.0, 0.0, 1.0);
        momentsOut[pixel] = 0.0;
        return;
    }

    float l = Luminance(current.rgb);
    float4 previous = 0.0;
    float4 previousMoments = 0.0;
    float weightSum = 0.0;
    float2 p;
    bool onScreen, borrowed = false;
    if (PreviousPixel(s.position, s.motion, local, viewIndex, p, onScreen))
    {
        if (onScreen)
        {
            float2 coord = p - 0.5;
            int2 base = int2(floor(coord));
            float2 f = coord - base;
            float tolerance = HistoryTolerance(s.position, s.distance, p, viewIndex);
            [unroll] for (uint k = 0; k < 4; k++)
            {
                int2 o = int2(k & 1, k >> 1);
                int2 tap = base + o;
                if (any(tap < 0) || tap.x >= int(frame.width) || tap.y >= int(frame.height)) { continue; }
                tap.x += viewIndex * frame.width;
                float4 g = geometry[tap];
                if (all(g == 0.0)) { continue; }
                float plane = abs(dot(g.xyz - s.position, s.normal));
                float facing = dot(UnpackNormal(asuint(g.w)), s.normal);
                if (plane > tolerance || facing < 0.8) { continue; }
                float w = (o.x ? f.x : 1.0 - f.x) * (o.y ? f.y : 1.0 - f.y);
                previous += history[tap] * w;
                previousMoments += moments[tap] * w;
                weightSum += w;
            }
            if (weightSum <= 1e-3)
            {
                float2 back = float2(local) + 0.5 - p;
                float span = length(back);
                [loop] for (uint n = 0; n < 40; n++)
                {
                    if (n >= 24 && span < 1.0) { break; }
                    float radius = n < 8 ? 2.0 : n < 16 ? 5.0 : n < 24 ? 10.0 : span * float(n - 23u) * 0.125;
                    float angle = float(n & 7u) * 0.7853982 + (n < 8 ? 0.0 : n < 16 ? 0.3926991 : 0.0);
                    int2 tap = int2(floor(p + (n < 24 ? radius * float2(cos(angle), sin(angle)) : back * (float(n - 23u) * 0.125))));
                    if (any(tap < 0) || tap.x >= int(frame.width) || tap.y >= int(frame.height)) { continue; }
                    tap.x += viewIndex * frame.width;
                    float4 g = geometry[tap];
                    if (all(g == 0.0)) { continue; }
                    float plane = abs(dot(g.xyz - s.position, s.normal));
                    if (plane > tolerance || dot(UnpackNormal(asuint(g.w)), s.normal) < 0.8) { continue; }
                    float w = 1.0 / (max(radius, 1.0) * (1.0 + 4.0 * plane / tolerance));
                    previous += history[tap] * w;
                    previousMoments += moments[tap] * w;
                    weightSum += w;
                    if (n == 7 || n == 15 || n >= 23) { if (weightSum > 1e-3) { break; } }
                }
                borrowed = weightSum > 1e-3;
            }
        }
        else
        {
            int2 edge = int2(clamp(p, 0.5, float2(frame.width, frame.height) - 0.5));
            if (length(float2(edge) + 0.5 - p) / float(frame.height) < HISTORY_BORROW_REACH)
            {
                float tolerance = HistoryTolerance(s.position, s.distance, p, viewIndex);
                int2 along = p.y <= 0.0 || p.y >= float(frame.height) ? int2(1, 0) : int2(0, 1);
                [unroll] for (int k = -2; k <= 2; k++)
                {
                    int2 tap = edge + along * (k * 3);
                    if (any(tap < 0) || tap.x >= int(frame.width) || tap.y >= int(frame.height)) { continue; }
                    tap.x += viewIndex * frame.width;
                    float4 g = geometry[tap];
                    if (all(g == 0.0) || abs(dot(g.xyz - s.position, s.normal)) >= tolerance || dot(UnpackNormal(asuint(g.w)), s.normal) < 0.8) { continue; }
                    float w = 1.0 / (1.0 + abs(float(k)));
                    previous += history[tap] * w;
                    previousMoments += moments[tap] * w;
                    weightSum += w;
                }
                borrowed = weightSum > 1e-3;
            }
        }
    }

    float3 nSum = 0.0, nSq = 0.0;
    float nCount = 0.0;
    [unroll] for (int y = -1; y <= 1; y++)
    {
        [unroll] for (int x = -1; x <= 1; x++)
        {
            int2 q = int2(local) + int2(x, y);
            if (any(q < 0) || q.x >= int(frame.width) || q.y >= int(frame.height)) { continue; }
            float4 c = (x == 0 && y == 0) ? current : radiance[uint2(q.x + viewIndex * frame.width, q.y)];
            if (!all(isfinite(c)) || (all(c.rgb == 0.0) && c.a == 1.0 && (x != 0 || y != 0))) { continue; }
            nSum += c.rgb; nSq += c.rgb * c.rgb; nCount += 1.0;
        }
    }
    float3 nMean = nSum / max(nCount, 1.0);
    float3 nSigma = sqrt(max(nSq / max(nCount, 1.0) - nMean * nMean, 0.0));
    float nLum = Luminance(nMean), nLumSigma = Luminance(nSigma);

    float length = 0.0, drift = 1.0;
    if (weightSum > 1e-3)
    {
        previous /= weightSum;
        previousMoments /= weightSum;
        length = borrowed ? min(previousMoments.z, HISTORY_BORROW_LENGTH) : previousMoments.z;
        float spread = sqrt(max(previousMoments.y - previousMoments.x * previousMoments.x, 0.0));
        float ceiling = previousMoments.x + 4.0 * spread + 0.25 * previousMoments.x + 0.02;
        if (length >= 4.0 && l > ceiling && l > 2.0 * nLum) { current.rgb *= ceiling / l; l = ceiling; }
        float noise = (2.0 * spread + nLumSigma) / sqrt(max(nCount, 1.0));
        float scale = max(max(previousMoments.x, nLum), 0.01);
        float gradient = saturate((abs(nLum - previousMoments.x) - noise - 0.05 * scale - 0.005) / scale);
        length = lerp(length, 1.0, gradient);
        float around = nCount > 1.5 ? (nLum * nCount - l) / (nCount - 1.0) : previousMoments.x;
        drift = saturate(abs(around - previousMoments.x) / (noise + 0.02 * scale + 0.002) - 1.0);
    }
    if (length < 4.0 && nCount > 1.5)
    {
        float others = (nLum * nCount - l) / (nCount - 1.0);
        float ceiling = HISTORY_FRESH_CEILING * others + 0.02;
        if (l > ceiling) { current.rgb *= ceiling / l; l = ceiling; }
    }
    length = min(length + 1.0, 64.0);
    float timeAlpha = 1.0 - exp(-max(frame.deltaTime, 0.001) * 90.0 * frame.temporalResponse);
    float alpha = max(1.0 / length, timeAlpha * max(drift, 0.25));
    float2 m = lerp(previousMoments.xy, float2(l, l * l), weightSum > 1e-3 ? alpha : 1.0);
    float4 blended = weightSum > 1e-3 ? lerp(previous, current, alpha) : current;
    float variance = max(m.y - m.x * m.x, 0.0);
    if (length < 4.0) { variance = max(variance, nLumSigma * nLumSigma * (4.0 / length)); }
#ifdef REFERENCE
    blended = radiance[pixel];
#endif
    historyOut[pixel] = all(isfinite(blended)) ? Half(min(blended, 60000.0), pixel.x + pixel.y * 8192u + frame.frameIndex * 0x9e3779b9u) : blended;
    momentsOut[pixel] = float4(m, length, variance);
}

[numthreads(8, 8, 1)]
void Wavelet(uint3 id : SV_DispatchThreadID)
{
    uint2 pixel = id.xy;
    if (pixel.x >= frame.width * frame.viewCount || pixel.y >= frame.height) { return; }
    uint viewIndex;
    uint2 local = ViewPixel(pixel, viewIndex);
    RWTexture2D<float4> source = ResourceDescriptorHeap[pass.a];
    RWTexture2D<float4> moments = ResourceDescriptorHeap[HEAP_MOMENTS_UAV + frame.historyWrite];
    RWTexture2D<uint4> geometry = ResourceDescriptorHeap[HEAP_COMPACT_UAV];
    RWTexture2D<float4> target = ResourceDescriptorHeap[pass.b];
    int step = int(pass.c);

    uint4 g = geometry[pixel];
    float4 centre = source[pixel];
    if (g.w == 0u) { target[pixel] = centre; return; }
    float3 position = f16tof32(g.xyz);
    float3 normal = CompactNormal(g.w);
    float4 mo = moments[pixel];
    float variance = mo.w / float(1u << min(pass.d * 2u, 6u));
    if (pass.e != 0u && pass.d == 0u)
    {
        float s1 = 0.0, s2 = 0.0, n = 0.0;
        float near = max(0.02, length(position) * 0.015);
        [unroll] for (int sy = -1; sy <= 1; sy++)
        {
            [unroll] for (int sx = -1; sx <= 1; sx++)
            {
                int2 q = int2(local) + int2(sx, sy);
                if (any(q < 0) || q.x >= int(frame.width) || q.y >= int(frame.height)) { continue; }
                int2 t = q + int2(viewIndex * frame.width, 0);
                uint4 tg = geometry[t];
                if (tg.w == 0u || abs(dot(f16tof32(tg.xyz) - position, normal)) > near || dot(normal, CompactNormal(tg.w)) < 0.9) { continue; }
                float l = Luminance(source[t].rgb);
                s1 += l; s2 += l * l; n += 1.0;
            }
        }
        if (n > 1.5) { float m = s1 / n; variance = max(variance, max(s2 / n - m * m, 0.0)); }
    }
    float lc = Luminance(centre.rgb);
    float sigmaL = 4.0 * sqrt(variance) + 1e-3;
    float distance = length(position);
    float planeScale = max(0.02, distance * 0.015) * step;
    float inversePlane = 1.0 / planeScale, inverseSigma = 1.0 / sigmaL;

    float4 sum = centre;
    float weightSum = 1.0;
    float support = 0.0, around = -1.0;
    static const float kernel[3] = { 1.0, 2.0 / 3.0, 1.0 / 6.0 };
    [unroll] for (int y = -2; y <= 2; y++)
    {
        [unroll] for (int x = -2; x <= 2; x++)
        {
            if (x == 0 && y == 0) { continue; }
            int2 local2 = int2(local) + int2(x, y) * step;
            if (any(local2 < 0) || local2.x >= int(frame.width) || local2.y >= int(frame.height)) { continue; }
            int2 tap = local2 + int2(viewIndex * frame.width, 0);
            uint4 tg = geometry[tap];
            if (tg.w == 0u) { continue; }
            float4 c = source[tap];
            float wn = saturate(dot(normal, CompactNormal(tg.w)));
            wn *= wn; wn *= wn; wn *= wn; wn *= wn; wn *= wn;
            float plane = abs(dot(f16tof32(tg.xyz) - position, normal)) * inversePlane;
            float tl = Luminance(c.rgb);
            float k = kernel[abs(x)] * kernel[abs(y)];
            float w = k * wn * exp(-plane - abs(tl - lc) * inverseSigma);
            sum += c * w;
            weightSum += w;
            float same = plane < 2.0 ? k * wn : 0.0;
            support += same;
            if (abs(x) <= 1 && abs(y) <= 1 && same < 0.5 * k) { around = max(around, tl); }
        }
    }
    float4 result = sum / weightSum;
    if (pass.d == 0u && support < 1.5 && around >= 0.0)
    {
        float l = Luminance(result.rgb);
        if (l > around) { result.rgb *= around / l; }
    }
    target[pixel] = result;
}
