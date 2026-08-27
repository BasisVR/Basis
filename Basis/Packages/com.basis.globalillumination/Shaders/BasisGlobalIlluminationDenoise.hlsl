#ifndef BASIS_GLOBAL_ILLUMINATION_DENOISE_INCLUDED
#define BASIS_GLOBAL_ILLUMINATION_DENOISE_INCLUDED

#include "./BasisGlobalIlluminationCommon.hlsl"

float4 _BasisGIBlurAxis;

struct BasisGITemporalOutput
{
    float4 indirect : SV_Target0;
    float4 stats : SV_Target1;
};

float4 BasisGILoadIndirect(float2 uv)
{
    return SAMPLE_TEXTURE2D_X_LOD(_BasisGIIndirect, sampler_PointClamp, UnityStereoTransformScreenSpaceTex(saturate(uv)), 0);
}

/// Depth in red, frames accumulated in green, and the running mean and variance of the accumulated
/// luminance in blue and alpha.
float4 BasisGILoadHistoryStats(float2 uv)
{
    return SAMPLE_TEXTURE2D_X_LOD(_BasisGIHistoryStats, sampler_PointClamp, UnityStereoTransformScreenSpaceTex(saturate(uv)), 0);
}

float4 BasisGILoadHistory(float2 uv)
{
    return SAMPLE_TEXTURE2D_X_LOD(_BasisGIHistory, sampler_BasisGIHistory, UnityStereoTransformScreenSpaceTex(saturate(uv)), 0);
}

BasisGITemporalOutput BasisGITemporal(float2 uv)
{
    BasisGITemporalOutput output;

    float4 current = BasisGILoadIndirect(uv);
    float rawDepth = BasisGISampleRawDepth(uv);
    float eyeDepth = BasisGILinearEyeDepth(rawDepth);
    float luminance = Luminance(max(0.0, current.rgb));

    output.indirect = current;
    output.stats = float4(eyeDepth, 1.0, luminance, 0.0);

    if (BasisGIIsSky(rawDepth) || _BasisGIHistoryValid < 0.5) { return output; }

    float3 worldPosition = BasisGIWorldPosition(uv, rawDepth);
    float4 previousClip = mul(BasisGIPreviousViewProjection(), float4(worldPosition, 1.0));
    if (previousClip.w <= BASISGI_EPSILON) { return output; }

    float2 previousUv = previousClip.xy / previousClip.w * 0.5 + 0.5;
#if UNITY_UV_STARTS_AT_TOP
    previousUv.y = 1.0 - previousUv.y;
#endif
    if (any(previousUv < 0.0) || any(previousUv > 1.0)) { return output; }

    float4 historyStats = BasisGILoadHistoryStats(previousUv);
    float relativeDelta = abs(historyStats.r - eyeDepth) / max(eyeDepth, BASISGI_EPSILON);
    if (historyStats.r <= 0.0 || relativeDelta > BASISGI_DEPTH_REJECTION) { return output; }

    float4 history = BasisGILoadHistory(previousUv);

#if defined(_BASISGI_NEIGHBOURHOOD_CLAMP)
    // Variance clipping rather than a min/max box. At one or two rays per pixel the neighbourhood's extremes
    // are themselves noise, so clamping to them feeds that noise back into the history every frame and the
    // accumulation never settles. Mean plus a couple of standard deviations rejects real ghosting while
    // leaving a noisy but unbiased history alone.
    float4 moment1 = current;
    float4 moment2 = current * current;
    float2 texel = _BasisGITracedTexelSize.xy;
    UNITY_UNROLL
    for (int y = -1; y <= 1; y++)
    {
        UNITY_UNROLL
        for (int x = -1; x <= 1; x++)
        {
            if (x == 0 && y == 0) { continue; }
            float4 neighbour = BasisGILoadIndirect(uv + float2(x, y) * texel);
            moment1 += neighbour;
            moment2 += neighbour * neighbour;
        }
    }
    float4 mean = moment1 * (1.0 / BASISGI_TEMPORAL_NEIGHBOURS);
    float4 deviation = sqrt(max(0.0, moment2 * (1.0 / BASISGI_TEMPORAL_NEIGHBOURS) - mean * mean));

    // The box is never allowed to be narrower than what a run of misses could plausibly have hidden.
    // Colour is bounded per sample by the firefly ceiling and obscurance by its own intensity, so the
    // floor is written in each channel's own units rather than as one number that would be wrong for
    // both, and it tightens on its own as the ray budget rises.
    float sampleCount = BASISGI_TEMPORAL_NEIGHBOURS * max(1.0, BASISGI_RAY_COUNT);
    float rare = BASISGI_TEMPORAL_CLIP_RARE / sampleCount;
    float4 ceiling = float4(BASISGI_FIREFLY_CLAMP.xxx, max(BASISGI_OBSCURANCE, BASISGI_EPSILON));
    float4 halfWidth = max(deviation * BASISGI_TEMPORAL_CLIP_SIGMA, rare * ceiling);
    history = clamp(history, mean - halfWidth, mean + halfWidth);
#endif

    // How many frames this pixel has been accumulating for. A freshly disoccluded pixel starts at one and
    // takes the whole of this frame, a settled one keeps a long tail, and a shaky reprojection decays the
    // count rather than throwing the history away outright. The response slider is the floor: it decides
    // where accumulation stops, and it is only reached once there is enough history to stop at.
    float rejection = saturate(relativeDelta / max(BASISGI_DEPTH_REJECTION, BASISGI_EPSILON));
    float frames = min(historyStats.g * (1.0 - rejection) + 1.0, BASISGI_TEMPORAL_MAX_FRAMES);
    float response = max(rcp(max(frames, 1.0)), BASISGI_TEMPORAL_RESPONSE);

    output.indirect = lerp(history, current, response);

    // The mean and the variance ride the same blend as the colour, so what they describe is what the
    // accumulation is actually holding rather than how noisy one frame was. That is the number the
    // spatial filter needs: whether this pixel has settled, not how far one sample landed.
    //
    // Carried as mean and variance rather than as the first two moments. Recovering a variance from
    // moments means subtracting two numbers that are nearly equal once a pixel settles, and in a half
    // float target almost nothing survives that subtraction - a settled pixel reads a variance floor of
    // pure quantisation noise, the spatial filter believes it is still unresolved, and it smears the
    // image it was supposed to be leaving alone. The incremental form never forms that difference.
    float luminanceDelta = luminance - historyStats.b;
    float luminanceIncrement = response * luminanceDelta;
    float accumulatedMean = historyStats.b + luminanceIncrement;
    float accumulatedVariance = (1.0 - response) * (max(0.0, historyStats.a) + luminanceDelta * luminanceIncrement);
    output.stats = float4(eyeDepth, frames, accumulatedMean, accumulatedVariance);
    return output;
}

/// <summary>
/// One level of the a-trous cascade: a separable kernel at the stride the caller asked for, gated on three
/// things at once.
///
/// The plane distance is what keeps a widening stride from crossing a crease. Two surfaces meeting at a
/// corner sit at almost the same depth and a depth difference cannot tell them apart, while one surface
/// seen at a glancing angle spans a large depth over a few pixels and a depth difference rejects it from
/// itself. Measuring how far a neighbour sits off the centre pixel's own plane does neither.
///
/// The luminance gate is what decides how much detail survives, and it is opened by how unresolved the
/// pair is rather than being fixed. A pixel with no history behind it lets everything through, which is
/// the only way a bright sample that one ray in forty found ever reaches the pixels around it; a settled
/// pixel narrows the gate to a few standard deviations of its own accumulated swing and keeps its detail.
///
/// It is deliberately decided by the pair rather than by the centre alone, which makes it symmetric: the
/// weight between two pixels is the same whichever of them is being filtered. A gate that only consulted
/// the centre would let a noisy pixel take energy from its settled neighbours while they refused to take
/// any back, and a sparse bounce drains away into that one-way valve a few percent per pass.
/// </summary>
float4 BasisGIBilateralBlur(float2 uv)
{
    float centreRaw = BasisGISampleRawDepth(uv);
    float3 centrePosition = BasisGIWorldPosition(uv, centreRaw);
    // Taken before any branch: screen space derivatives are only meaningful where the whole quad agrees.
    float3 centreNormal = BasisGIPlaneNormal(centrePosition);
    float4 centre = BasisGILoadIndirect(uv);

    if (BasisGIIsSky(centreRaw)) { return centre; }

    float2 axis = _BasisGIBlurAxis.xy;
    float taps = _BasisGIBlurAxis.z;
    if (taps <= 0.0) { return centre; }

    float centreEye = BasisGILinearEyeDepth(centreRaw);
    float centreLuminance = Luminance(max(0.0, centre.rgb));
    float planeScale = BasisGIPlaneTolerance(centreEye);

    float2 centreAccumulation = BasisGIAccumulation(uv);
    float unresolved = BASISGI_FIREFLY_CLAMP / max(1.0, BASISGI_RAY_COUNT);

    float4 total = centre;
    float weightSum = 1.0;
    int count = (int)taps;

    UNITY_LOOP
    for (int offset = 1; offset <= count; offset++)
    {
        float spatial = exp(-0.5 * ((float)offset * (float)offset) / max(BASISGI_EPSILON, taps * taps * 0.25));

        UNITY_UNROLL
        for (int side = 0; side < 2; side++)
        {
            float2 sampleUv = uv + axis * (float)offset * (side == 0 ? 1.0 : -1.0);
            if (any(sampleUv < 0.0) || any(sampleUv > 1.0)) { continue; }

            float sampleRaw = BasisGISampleRawDepth(sampleUv);
            if (BasisGIIsSky(sampleRaw)) { continue; }

            float plane = abs(dot(centreNormal, BasisGIWorldPosition(sampleUv, sampleRaw) - centrePosition));
            float planeWeight = exp(-plane / planeScale);

            float2 tapAccumulation = BasisGIAccumulation(sampleUv);
            float convergence = saturate(min(centreAccumulation.x, tapAccumulation.x) / BASISGI_BLUR_CONVERGED);
            float deviation = sqrt(max(centreAccumulation.y, tapAccumulation.y));
            float luminanceScale = BASISGI_BLUR_LUMINANCE * lerp(unresolved, deviation, convergence) + BASISGI_BLUR_LUMINANCE_FLOOR;

            float4 sampleValue = BasisGILoadIndirect(sampleUv);
            float luminanceWeight = exp(-abs(Luminance(max(0.0, sampleValue.rgb)) - centreLuminance) / luminanceScale);

            float weight = spatial * planeWeight * luminanceWeight;
            total += sampleValue * weight;
            weightSum += weight;
        }
    }

    return total / max(weightSum, BASISGI_EPSILON);
}

#endif
