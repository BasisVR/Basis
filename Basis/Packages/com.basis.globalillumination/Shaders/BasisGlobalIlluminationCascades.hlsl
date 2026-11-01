#ifndef BASIS_GLOBAL_ILLUMINATION_CASCADES_INCLUDED
#define BASIS_GLOBAL_ILLUMINATION_CASCADES_INCLUDED

#include "./BasisGlobalIlluminationTrace.hlsl"
#include "./BasisGlobalIlluminationWorld.hlsl"

#define BASISGI_RC_DIRECTIONS0 4
#define BASISGI_RC_LEVELS 4
#define BASISGI_RC_BELOW 0.0

TEXTURE2D_X(_BasisGIRCColor1);
TEXTURE2D_X(_BasisGIRCColor2);
TEXTURE2D_X(_BasisGIRCColor3);
TEXTURE2D_X(_BasisGIRCDepth1);
TEXTURE2D_X(_BasisGIRCDepth2);
TEXTURE2D_X(_BasisGIRCDepth3);
TEXTURE2D_X(_BasisGIRCSourceColor);
TEXTURE2D_X(_BasisGIRCSourceDepth);
TEXTURE2D_X(_BasisGIRCProbes);
TEXTURE2D_X(_BasisGIRCUpperProbes);
TEXTURE2D_X(_BasisGIRCUpper);
TEXTURE2D_X(_BasisGIRCSH0);
TEXTURE2D_X(_BasisGIRCSH1);
TEXTURE2D_X(_BasisGIRCSH2);
TEXTURE2D_X(_BasisGIRCSH3);

float4 _BasisGIRCLevels[BASISGI_RC_LEVELS];
float _BasisGIRCSolidAngle[BASISGI_RC_DIRECTIONS0 * BASISGI_RC_DIRECTIONS0];
float4 _BasisGIRCCascade;
float4 _BasisGIRCInterval;
float4 _BasisGIRCGrid;
float4 _BasisGIRCJitter;
float4x4 _BasisGIRCRotation;

float2 BasisGIRCSnapToDepthTexel(float2 uv)
{
    float2 size = _BasisGISourceTexelSize.zw;
    return (floor(max(saturate(uv) * size - 0.001, 0.0)) + 0.5) / size;
}

struct BasisGIRCProbe
{
    bool valid;
    float3 position;
    float3 normal;
};

float BasisGIRCPackNormal(float3 normal)
{
    float2 oct = saturate(PackNormalOctQuadEncode(normal) * 0.5 + 0.5);
    uint2 quantised = (uint2)round(oct * 4095.0);
    return 1.0 + (float)(quantised.x + quantised.y * 4096u);
}

float3 BasisGIRCUnpackNormal(float packed)
{
    uint value = (uint)packed - 1u;
    float2 oct = float2(value & 4095u, value >> 12) / 4095.0;
    return UnpackNormalOctQuadEncode(oct * 2.0 - 1.0);
}

BasisGIRCProbe BasisGIRCDecodeProbe(float4 data)
{
    BasisGIRCProbe probe;
    probe.valid = data.w > 0.5;
    probe.position = data.xyz;
    probe.normal = probe.valid ? BasisGIRCUnpackNormal(data.w) : float3(0.0, 0.0, 1.0);
    return probe;
}

float3 BasisGIRCDirection(int2 texel, float resolution)
{
    float2 oct = ((float2)texel + 0.5) / resolution * 2.0 - 1.0;
    return mul((float3x3)_BasisGIRCRotation, UnpackNormalOctQuadEncode(oct));
}

float2 BasisGIRCLoadDepth(int level, float2 uv)
{
    int2 texel = int2(uv * _BasisGITracedTexelSize.zw) >> level;
    texel = clamp(texel, int2(0, 0), int2(_BasisGIRCLevels[level].xy) - 1);
    UNITY_BRANCH
    if (level == 0) { return LOAD_TEXTURE2D_X(_BasisGITracedDepth, texel).rg; }
    UNITY_BRANCH
    if (level == 1) { return LOAD_TEXTURE2D_X(_BasisGIRCDepth1, texel).rg; }
    UNITY_BRANCH
    if (level == 2) { return LOAD_TEXTURE2D_X(_BasisGIRCDepth2, texel).rg; }
    return LOAD_TEXTURE2D_X(_BasisGIRCDepth3, texel).rg;
}

float3 BasisGIRCSampleColor(int level, float2 uv)
{
    float2 coverage = _BasisGITracedTexelSize.zw / (exp2((float)level) * _BasisGIRCLevels[level].xy);
    float2 levelUv = saturate(uv) * coverage;
    UNITY_BRANCH
    if (level == 0) { return SAMPLE_TEXTURE2D_X_LOD(_BasisGISceneColor, sampler_LinearClamp, levelUv, 0).rgb; }
    UNITY_BRANCH
    if (level == 1) { return SAMPLE_TEXTURE2D_X_LOD(_BasisGIRCColor1, sampler_LinearClamp, levelUv, 0).rgb; }
    UNITY_BRANCH
    if (level == 2) { return SAMPLE_TEXTURE2D_X_LOD(_BasisGIRCColor2, sampler_LinearClamp, levelUv, 0).rgb; }
    return SAMPLE_TEXTURE2D_X_LOD(_BasisGIRCColor3, sampler_LinearClamp, levelUv, 0).rgb;
}

struct BasisGIRCReduceOutput
{
    float4 color : SV_Target0;
    float2 depth : SV_Target1;
};

BasisGIRCReduceOutput BasisGIRCReduce(float2 positionCS)
{
    if (BasisGISkippedEye())
    {
        BasisGIRCReduceOutput skipped;
        skipped.color = float4(0.0, 0.0, 0.0, 1.0);
        skipped.depth = float2(0.0, 0.0);
        return skipped;
    }
    int2 base = int2(positionCS) * 2;
    int2 limit = int2(_BasisGIRCJitter.zw) - 1;
    float nearest = BASISGI_SKY_DEPTH;
    float furthest = 0.0;
    float3 sum = float3(0.0, 0.0, 0.0);
    float count = 0.0;

    UNITY_UNROLL
    for (int index = 0; index < 4; index++)
    {
        int2 tap = min(base + int2(index & 1, index >> 1), limit);
        float2 depth = LOAD_TEXTURE2D_X(_BasisGIRCSourceDepth, tap).rg;
        nearest = min(nearest, depth.r);
        furthest = max(furthest, depth.g);
        if (depth.g > 0.0)
        {
            sum += LOAD_TEXTURE2D_X(_BasisGIRCSourceColor, tap).rgb;
            count += 1.0;
        }
    }

    BasisGIRCReduceOutput output;
    output.color = float4(count > 0.0 ? sum / count : float3(0.0, 0.0, 0.0), 1.0);
    output.depth = float2(nearest, furthest);
    return output;
}

float4 BasisGIRCPlaceProbe(float2 positionCS)
{
    if (BasisGISkippedEye()) { return float4(0.0, 0.0, 0.0, 0.0); }
    int2 probe = int2(positionCS);
    if (any(probe >= int2(_BasisGIRCGrid.xy))) { return float4(0.0, 0.0, 0.0, 0.0); }

    float spacing = _BasisGIRCCascade.y;
    float2 size = _BasisGITracedTexelSize.zw;
    float2 centre = ((float2)probe + 0.5) * spacing + _BasisGIRCJitter.xy;

    float2 uv = float2(0.0, 0.0);
    float rawDepth = 0.0;
    bool found = false;

    UNITY_LOOP
    for (int candidate = 0; candidate < 5 && !found; candidate++)
    {
        float2 offset = candidate == 0 ? float2(0.0, 0.0) : (float2(candidate & 1, (candidate - 1) >> 1) * 0.5 - 0.25) * spacing;
        uv = BasisGIRCSnapToDepthTexel(clamp(centre + offset, 0.5, size - 0.5) / size);
        rawDepth = BasisGISampleRawDepth(uv);
        found = !BasisGIIsSky(rawDepth);
    }
    if (!found) { return float4(0.0, 0.0, 0.0, 0.0); }

    float3 viewPosition = BasisGIViewPosition(uv, rawDepth);
    float3 normalWS = BasisGIReconstructNormal(uv, viewPosition, rawDepth);
    return float4(BasisGIWorldPosition(uv, rawDepth), BasisGIRCPackNormal(normalWS));
}

struct BasisGIRCSegment
{
    float3 radiance;
    float occlusion;
    float transmittance;
    bool leftScreen;
    float2 exitUv;
    float3 endWS;
};

BasisGIRCSegment BasisGIRCMarch(float3 originWS, float3 directionWS, float start, float end, float noise, float travelled)
{
    BasisGIRCSegment segment;
    segment.radiance = float3(0.0, 0.0, 0.0);
    segment.occlusion = 0.0;
    segment.transmittance = 1.0;
    segment.leftScreen = false;
    segment.exitUv = float2(0.5, 0.5);
    segment.endWS = originWS;

    float4 first = BasisGIWorldToScreen(originWS + directionWS * start);
    if (first.w <= _ProjectionParams.y)
    {
        segment.leftScreen = true;
        return segment;
    }
    if (any(first.xy < 0.0) || any(first.xy > 1.0))
    {
        segment.leftScreen = true;
        segment.exitUv = saturate(first.xy);
        return segment;
    }

    float4 last = BasisGIWorldToScreen(originWS + directionWS * end);
    if (last.w <= _ProjectionParams.y)
    {
        float along = saturate((first.w - _ProjectionParams.y) / max(BASISGI_EPSILON, first.w - last.w)) * 0.98;
        end = start + (end - start) * along;
        last = BasisGIWorldToScreen(originWS + directionWS * end);
        segment.leftScreen = true;
        if (last.w <= _ProjectionParams.y) { return segment; }
    }

    float2 size = _BasisGITracedTexelSize.zw;
    float pixels = length((last.xy - first.xy) * size);
    int ceiling = (int)_BasisGIRCInterval.z;
    int level = (int)clamp(floor(log2(max(pixels / (float)ceiling, 1.0))), 0.0, (float)(BASISGI_RC_LEVELS - 1));
    int steps = clamp((int)ceil(pixels / exp2((float)level)), 1, ceiling);

    float invFirst = 1.0 / first.w;
    float invLast = 1.0 / last.w;
    float radius = max(BASISGI_OBSCURANCE_RADIUS, BASISGI_EPSILON);
    float verified = -1.0;

    UNITY_LOOP
    for (int step = 0; step < steps; step++)
    {
        float along = ((float)step + noise) / (float)steps;
        float2 uv = lerp(first.xy, last.xy, along);
        float invEye = lerp(invFirst, invLast, along);
        if (any(uv < 0.0) || any(uv > 1.0))
        {
            segment.leftScreen = true;
            segment.exitUv = saturate(uv);
            break;
        }

        float rayEye = 1.0 / max(BASISGI_EPSILON, invEye);
        float2 depth = BasisGIRCLoadDepth(level, uv);
        float distanceAlong = (along * invLast / max(BASISGI_EPSILON, invEye));
        if (depth.g <= 0.0) { verified = distanceAlong; continue; }

        float thickness = BasisGIThicknessAt(depth.r);
        if (rayEye <= depth.r) { verified = distanceAlong; continue; }
        if (rayEye > depth.g + thickness)
        {
            if (!BasisGIWorldValid()) { continue; }
            segment.leftScreen = true;
            segment.exitUv = saturate(uv);
            break;
        }
        float span = depth.g - depth.r;
        float coverage = rayEye > depth.g ? 1.0 : (span > thickness * 2.0 ? 0.5 : 0.0);
        if (coverage <= 0.0) { continue; }

        float world = travelled + start + (end - start) * (along * invLast / max(BASISGI_EPSILON, invEye));
        float weight = segment.transmittance * coverage;
        segment.radiance += weight * BasisGIClampFirefly(BasisGIRCSampleColor(level, uv));
        segment.occlusion += weight * (1.0 - saturate(world / radius));
        segment.transmittance *= 1.0 - coverage;
        if (segment.transmittance < 0.02)
        {
            segment.transmittance = 0.0;
            break;
        }
    }

    if (verified >= 0.0) { segment.endWS = originWS + directionWS * (start + (end - start) * verified); }
    return segment;
}

float3 BasisGIRCBeyondScreen(float3 directionWS, BasisGIRCSegment segment)
{
    float3 radiance = BasisGIFallbackRadiance(directionWS);
#if defined(_BASISGI_RAY_REUSE)
    if (segment.leftScreen) { radiance = lerp(radiance, BasisGIClampFirefly(BasisGISampleSceneColor(segment.exitUv)), 0.5); }
#endif
    float4 world = BasisGIWorldRadiance(segment.endWS, directionWS);
    return lerp(radiance, BasisGIClampFirefly(world.rgb), world.a);
}

float BasisGIRCTexelWorld(float eyeDepth)
{
    return 2.0 * eyeDepth / max(BASISGI_EPSILON, abs(GetViewToHClipMatrix()._m11) * _BasisGITracedTexelSize.w);
}

float2 BasisGIRCIntervalAt(float cascade, float eyeDepth)
{
    float floorLength = _BasisGIRCInterval.x;
    float penumbra = _BasisGIRCInterval.y * BasisGIRCTexelWorld(eyeDepth);
    float first = clamp(penumbra, floorLength, floorLength * 4.0);
    float scale = exp2(2.0 * cascade);
    float start = first * (scale - 1.0) / 3.0;
    return float2(start, start + first * scale);
}

struct BasisGIRCUpper
{
    bool valid;
    BasisGIRCProbe probe;
    float weight;
    float corner;
    float4 value;
};

BasisGIRCUpper BasisGIRCLoadUpper(int index, BasisGIRCProbe probe, float probeEye, float2 probeCentre, float3 directionWS, int2 directionTexel, float resolution)
{
    BasisGIRCUpper upper;
    float upperSpacing = _BasisGIRCCascade.y * 2.0;
    float upperResolution = resolution * 2.0;
    int2 upperGrid = int2(_BasisGIRCGrid.zw);
    float2 grid = (probeCentre - _BasisGIRCJitter.xy) / upperSpacing - 0.5;
    int2 cell = (int2)floor(grid);
    float2 blend = grid - (float2)cell;
    int2 offset = int2(index & 1, index >> 1);
    int2 coordinate = clamp(cell + offset, int2(0, 0), upperGrid - 1);
    upper.probe = BasisGIRCDecodeProbe(LOAD_TEXTURE2D_X(_BasisGIRCUpperProbes, coordinate));
    upper.valid = upper.probe.valid;

    float2 bilinear = lerp(1.0 - blend, blend, (float2)offset);
    upper.corner = max(bilinear.x * bilinear.y, 1e-4);
    float2 childCentre = (float2)directionTexel * 2.0 + 1.0;
    float2 atlasUv = ((float2)coordinate * upperResolution + childCentre) / _BasisGIRCJitter.zw;
    upper.value = SAMPLE_TEXTURE2D_X_LOD(_BasisGIRCUpper, sampler_LinearClamp, atlasUv, 0);

    float tolerance = max(0.05, probeEye * 0.05);
    float plane = abs(dot(probe.normal, upper.probe.position - probe.position)) / tolerance;
    upper.weight = upper.corner / (1.0 + plane * plane * plane * plane);
    if (dot(directionWS, upper.probe.normal) < BASISGI_RC_BELOW) { upper.weight *= 0.02; }
    return upper;
}

float4 BasisGIRCMergeUpper(BasisGIRCProbe probe, float probeEye, float2 probeCentre, float3 directionWS, int2 directionTexel, float resolution)
{
    float4 weighted = float4(0.0, 0.0, 0.0, 0.0);
    float4 plain = float4(0.0, 0.0, 0.0, 0.0);
    float weightSum = 0.0, plainSum = 0.0;

    UNITY_UNROLL
    for (int index = 0; index < 4; index++)
    {
        BasisGIRCUpper upper = BasisGIRCLoadUpper(index, probe, probeEye, probeCentre, directionWS, directionTexel, resolution);
        if (!upper.valid) { continue; }
        weighted += upper.value * upper.weight;
        weightSum += upper.weight;
        plain += upper.value * upper.corner;
        plainSum += upper.corner;
    }

    if (weightSum > 1e-5) { return weighted / weightSum; }
    if (plainSum > 0.0) { return plain / plainSum; }
    return float4(BasisGIFallbackRadiance(directionWS), 0.0);
}

bool BasisGIRCMergeFixed(BasisGIRCProbe probe, float probeEye, float2 probeCentre, float3 directionWS, int2 directionTexel, float resolution,
    float3 origin, float start, float noise, out float4 merged)
{
    float4 weighted = float4(0.0, 0.0, 0.0, 0.0);
    float weightSum = 0.0;
    float3 from = origin + directionWS * start;

    UNITY_LOOP
    for (int index = 0; index < 4; index++)
    {
        BasisGIRCUpper upper = BasisGIRCLoadUpper(index, probe, probeEye, probeCentre, directionWS, directionTexel, resolution);
        if (!upper.valid || upper.weight < 1e-4) { continue; }

        float upperEye = -TransformWorldToView(upper.probe.position).z;
        float3 upperOrigin = upper.probe.position + upper.probe.normal * (0.01 + upperEye * 0.002);
        float3 target = upperOrigin + directionWS * BasisGIRCIntervalAt(_BasisGIRCCascade.x + 1.0, upperEye).x;
        float3 delta = target - from;
        float span = sqrt(dot(delta, delta));

        float4 value = upper.value;
        if (span > 1e-4)
        {
            BasisGIRCSegment segment = BasisGIRCMarch(from, delta / span, 0.0, span, noise, start);
            value = float4(segment.radiance, segment.occlusion);
            if (segment.transmittance > 0.0)
            {
                float4 beyond = segment.leftScreen ? float4(BasisGIRCBeyondScreen(directionWS, segment), 0.0) : upper.value;
                value += beyond * segment.transmittance;
            }
        }
        weighted += value * upper.weight;
        weightSum += upper.weight;
    }

    merged = weightSum > 1e-5 ? weighted / weightSum : float4(0.0, 0.0, 0.0, 0.0);
    return weightSum > 1e-5;
}

float4 BasisGIRCCascade(float2 positionCS)
{
    if (BasisGISkippedEye()) { return float4(0.0, 0.0, 0.0, 0.0); }
    int2 texel = int2(positionCS);
    float resolution = _BasisGIRCCascade.z;
    int2 probeIndex = texel / (int)resolution;
    int2 directionTexel = texel - probeIndex * (int)resolution;
    if (any(probeIndex >= int2(_BasisGIRCGrid.xy))) { return float4(0.0, 0.0, 0.0, 0.0); }

    BasisGIRCProbe probe = BasisGIRCDecodeProbe(LOAD_TEXTURE2D_X(_BasisGIRCProbes, probeIndex));
    if (!probe.valid) { return float4(0.0, 0.0, 0.0, 0.0); }

    float3 direction = BasisGIRCDirection(directionTexel, resolution);
    if (dot(direction, probe.normal) < BASISGI_RC_BELOW) { return float4(0.0, 0.0, 0.0, 0.0); }

    float eye = -TransformWorldToView(probe.position).z;
    float bias = 0.01 + eye * 0.002;
    float3 origin = probe.position + probe.normal * bias;
    float noise = lerp(0.5, BasisGIInterleavedGradientNoise((float2)texel, _BasisGIRCInterval.w), BASISGI_JITTER);
    float2 interval = BasisGIRCIntervalAt(_BasisGIRCCascade.x, eye);
    bool hasUpper = _BasisGIRCCascade.w > 0.5;
    float2 probeCentre = ((float2)probeIndex + 0.5) * _BasisGIRCCascade.y + _BasisGIRCJitter.xy;

#if defined(_BASISGI_RC_MERGE_FIX)
    UNITY_BRANCH
    if (hasUpper)
    {
        float4 merged;
        if (BasisGIRCMergeFixed(probe, eye, probeCentre, direction, directionTexel, resolution, origin, interval.x, noise, merged)) { return merged; }
    }
#endif

    float end = interval.y;
    if (hasUpper) { end += min(interval.y - interval.x, 1.41421356 * _BasisGIRCCascade.y * 2.0 * BasisGIRCTexelWorld(eye)); }
    BasisGIRCSegment segment = BasisGIRCMarch(origin, direction, interval.x, end, noise, 0.0);

    float4 result = float4(segment.radiance, segment.occlusion);
    if (segment.transmittance <= 0.0) { return result; }

    float4 beyond;
    UNITY_BRANCH
    if (segment.leftScreen || !hasUpper) { beyond = float4(BasisGIRCBeyondScreen(direction, segment), 0.0); }
    else { beyond = BasisGIRCMergeUpper(probe, eye, probeCentre, direction, directionTexel, resolution); }
    return result + beyond * segment.transmittance;
}

struct BasisGIRCHarmonics
{
    float4 exact : SV_Target0;
    float4 bandX : SV_Target1;
    float4 bandY : SV_Target2;
    float4 bandZ : SV_Target3;
};

BasisGIRCHarmonics BasisGIRCResolve(float2 positionCS)
{
    BasisGIRCHarmonics output;
    output.exact = float4(0.0, 0.0, 0.0, 0.0);
    output.bandX = float4(0.0, 0.0, 0.0, 0.0);
    output.bandY = float4(0.0, 0.0, 0.0, 0.0);
    output.bandZ = float4(0.0, 0.0, 0.0, 0.0);
    if (BasisGISkippedEye()) { return output; }

    int2 probe = int2(positionCS);
    if (any(probe >= int2(_BasisGIRCGrid.xy))) { return output; }
    BasisGIRCProbe probeData = BasisGIRCDecodeProbe(LOAD_TEXTURE2D_X(_BasisGIRCProbes, probe));
    if (!probeData.valid) { return output; }

    float cosineSum = 0.0;
    UNITY_UNROLL
    for (int index = 0; index < BASISGI_RC_DIRECTIONS0 * BASISGI_RC_DIRECTIONS0; index++)
    {
        int2 directionTexel = int2(index % BASISGI_RC_DIRECTIONS0, index / BASISGI_RC_DIRECTIONS0);
        float4 value = LOAD_TEXTURE2D_X(_BasisGIRCUpper, probe * BASISGI_RC_DIRECTIONS0 + directionTexel);
        float3 direction = BasisGIRCDirection(directionTexel, BASISGI_RC_DIRECTIONS0);
        float solidAngle = _BasisGIRCSolidAngle[index];
        float4 weighted = value * solidAngle;
        output.bandX += weighted * direction.x;
        output.bandY += weighted * direction.y;
        output.bandZ += weighted * direction.z;
        float cosine = max(0.0, dot(direction, probeData.normal)) * solidAngle;
        output.exact += value * cosine;
        cosineSum += cosine;
    }

    output.exact = cosineSum > 0.0 ? output.exact / cosineSum : float4(0.0, 0.0, 0.0, 0.0);
    output.bandX *= 1.0 / (2.0 * PI);
    output.bandY *= 1.0 / (2.0 * PI);
    output.bandZ *= 1.0 / (2.0 * PI);
    return output;
}

float4 BasisGIRCGather(float2 traceUv, float2 positionSS)
{
    if (BasisGISkippedEye()) { return float4(0.0, 0.0, 0.0, 1.0); }
    float2 uv = BasisGIRCSnapToDepthTexel(traceUv);
    float rawDepth = BasisGISampleRawDepth(uv);
    if (BasisGIIsSky(rawDepth)) { return float4(0.0, 0.0, 0.0, 1.0); }

    float eyeDepth = BasisGILinearEyeDepth(rawDepth);
    float fade = BasisGIDistanceFade(eyeDepth);
    if (fade <= 0.0) { return float4(0.0, 0.0, 0.0, 1.0); }

    float3 viewPosition = BasisGIViewPosition(uv, rawDepth);
    float3 worldPosition = BasisGIWorldPosition(uv, rawDepth);
    float3 normalWS = BasisGIReconstructNormal(uv, viewPosition, rawDepth);

    float spacing = _BasisGIRCCascade.y;
    int2 probeGrid = int2(_BasisGIRCGrid.xy);
    float2 grid = (uv * _BasisGITracedTexelSize.zw - _BasisGIRCJitter.xy) / spacing - 0.5;
    int2 cell = (int2)floor(grid);
    float2 blend = grid - (float2)cell;
    float tolerance = BasisGIPlaneTolerance(eyeDepth);

    float4 weighted = float4(0.0, 0.0, 0.0, 0.0);
    float4 plain = float4(0.0, 0.0, 0.0, 0.0);
    float weightSum = 0.0, plainSum = 0.0;

    UNITY_UNROLL
    for (int index = 0; index < 4; index++)
    {
        int2 offset = int2(index & 1, index >> 1);
        int2 probeIndex = clamp(cell + offset, int2(0, 0), probeGrid - 1);
        BasisGIRCProbe probe = BasisGIRCDecodeProbe(LOAD_TEXTURE2D_X(_BasisGIRCProbes, probeIndex));
        if (!probe.valid) { continue; }

        float3 bend = normalWS - probe.normal;
        float4 value = LOAD_TEXTURE2D_X(_BasisGIRCSH0, probeIndex)
            + LOAD_TEXTURE2D_X(_BasisGIRCSH1, probeIndex) * bend.x
            + LOAD_TEXTURE2D_X(_BasisGIRCSH2, probeIndex) * bend.y
            + LOAD_TEXTURE2D_X(_BasisGIRCSH3, probeIndex) * bend.z;

        float2 bilinear = lerp(1.0 - blend, blend, (float2)offset);
        float corner = max(bilinear.x * bilinear.y, 1e-4);
        float plane = abs(dot(normalWS, probe.position - worldPosition)) / tolerance;
        float facing = saturate(dot(normalWS, probe.normal)) + 0.05;
        float weight = corner * facing * facing / (1.0 + plane * plane * plane * plane);

        weighted += value * weight;
        weightSum += weight;
        plain += value * corner;
        plainSum += corner;
    }

    float4 resolved = weightSum > 1e-5 ? weighted / weightSum : (plainSum > 0.0 ? plain / plainSum : float4(0.0, 0.0, 0.0, 0.0));

    float normalBias = 0.01 + eyeDepth * 0.002;
    float noise = BasisGIInterleavedGradientNoise(positionSS, BASISGI_FRAME_INDEX);
    float3 indirect = max(0.0, resolved.rgb) + BasisGIEmitters(worldPosition + normalWS * normalBias, normalWS, noise);
    float obscurance = 1.0 - saturate(resolved.a) * BASISGI_OBSCURANCE;
    return float4(indirect * fade, lerp(1.0, obscurance, fade));
}

#endif
