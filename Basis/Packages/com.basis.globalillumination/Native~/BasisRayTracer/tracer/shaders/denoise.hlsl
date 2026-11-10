#include "common.hlsli"

#define DENOISE_RESERVOIR_SCRATCH 16

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
    if ((frame.flags & FLAG_RESET_HISTORY) == 0)
    {
        float2 p = float2(local) + 0.5 - s.motion;
        bool onScreen = (frame.flags & FLAG_MOTION) != 0 ? all(p > 0.0) && p.x < float(frame.width) && p.y < float(frame.height) : ProjectToPixel(frame.views[viewIndex].previousViewProjection, s.position, p);
        if (onScreen)
        {
            float2 coord = p - 0.5;
            int2 base = int2(floor(coord));
            float2 f = coord - base;
            float tolerance = max(0.03, s.distance * 0.02);
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

    float length = 0.0;
    if (weightSum > 1e-3)
    {
        previous /= weightSum;
        previousMoments /= weightSum;
        length = previousMoments.z;
        float spread = sqrt(max(previousMoments.y - previousMoments.x * previousMoments.x, 0.0));
        float ceiling = previousMoments.x + 4.0 * spread + 0.25 * previousMoments.x + 0.02;
        if (length >= 4.0 && l > ceiling && l > 2.0 * nLum) { current.rgb *= ceiling / l; l = ceiling; }
        float noise = (2.0 * spread + nLumSigma) / sqrt(max(nCount, 1.0));
        float scale = max(max(previousMoments.x, nLum), 0.01);
        float gradient = saturate((abs(nLum - previousMoments.x) - noise - 0.05 * scale - 0.005) / scale);
        length = lerp(length, 1.0, gradient);
    }
    length = min(length + 1.0, 64.0);
    float timeAlpha = 1.0 - exp(-max(frame.deltaTime, 0.001) * 90.0 * frame.temporalResponse);
    float alpha = max(1.0 / length, timeAlpha);
    float2 m = lerp(previousMoments.xy, float2(l, l * l), weightSum > 1e-3 ? alpha : 1.0);
    float4 blended = weightSum > 1e-3 ? lerp(previous, current, alpha) : current;
    float variance = max(m.y - m.x * m.x, 0.0);
    if (length < 4.0) { variance = max(variance, nLumSigma * nLumSigma * (4.0 / length)); }
    historyOut[pixel] = blended;
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
    float lc = Luminance(centre.rgb);
    float sigmaL = 4.0 * sqrt(variance) + 1e-3;
    float distance = length(position);
    float planeScale = max(0.02, distance * 0.015) * step;
    float inversePlane = 1.0 / planeScale, inverseSigma = 1.0 / sigmaL;

    float4 sum = centre;
    float weightSum = 1.0;
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
            float w = kernel[abs(x)] * kernel[abs(y)] * wn * exp(-abs(dot(f16tof32(tg.xyz) - position, normal)) * inversePlane - abs(Luminance(c.rgb) - lc) * inverseSigma);
            sum += c * w;
            weightSum += w;
        }
    }
    target[pixel] = sum / weightSum;
}
