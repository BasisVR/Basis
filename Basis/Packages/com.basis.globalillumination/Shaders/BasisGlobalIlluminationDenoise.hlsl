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

/// <summary>
/// Where the surface under this pixel was on screen last frame, and whether it was on screen at all.
///
/// The matrix form walks the CURRENT world position back through the PREVIOUS view-projection. That is
/// exact for a world which stood still, and wrong for everything in it that did not: it carries the
/// camera's motion and nothing else. Two consequences, and the second is the expensive one.
///
/// A pixel now on a moving surface reprojects to whatever was behind that surface last frame, so its
/// history is rejected on depth every single frame and the accumulation never starts - which is why, in
/// a room of avatars, the avatars are the noisiest thing in it. And where the surface moved roughly along
/// its own plane, the depth it lands on matches, nothing rejects anything, and the history that gets
/// blended in belongs to a different part of the surface. That one is a smear rather than noise.
///
/// Motion vectors carry the camera's motion and the object's together, which is what makes this strictly
/// better rather than a trade: where a renderer has no motion pass of its own URP has already written the
/// camera's motion into those texels, so the fallback is the matrix result, arrived at by other means.
/// </summary>
bool BasisGIReproject(float2 uv, float3 worldPosition, out float2 previousUv)
{
#if defined(_BASISGI_MOTION_VECTORS)
    // Forward vector, current minus previous, already in UV space with the v flip folded in - so the
    // previous position is this pixel minus what the texel holds, and there is no second flip to apply.
    float2 motion = SAMPLE_TEXTURE2D_X_LOD(_BasisGIMotion, sampler_PointClamp, UnityStereoTransformScreenSpaceTex(uv), 0).xy;
    previousUv = uv - motion;
#else
    float4 previousClip = mul(BasisGIPreviousViewProjection(), float4(worldPosition, 1.0));
    if (previousClip.w <= BASISGI_EPSILON) { previousUv = uv; return false; }

    previousUv = previousClip.xy / previousClip.w * 0.5 + 0.5;
    #if UNITY_UV_STARTS_AT_TOP
    previousUv.y = 1.0 - previousUv.y;
    #endif
#endif

    return all(previousUv >= 0.0) && all(previousUv <= 1.0);
}

struct BasisGINeighbourhood
{
    float4 mean;
    float4 deviation;
    /// <summary>How many samples are really behind the mean, once the gate has had its say.</summary>
    float samples;
};

/// <summary>
/// The three by three of the frame that has just arrived, reduced to a mean, a spread, and how much
/// evidence is actually behind them. Two jobs out of one set of taps.
///
/// The mean is the frame the accumulation is handed. Neighbouring pixels rotate their ray sets
/// independently, so nine of them are close to nine independent estimates of the same patch of surface,
/// and averaging them costs one pass where buying the same variance in rays would cost nine times the
/// trace. Doing it here rather than after the blend is the whole point: what gets remembered is then the
/// clean estimate, and every later frame compounds on that instead of on one or two rays that the
/// spatial pass has to tidy up again on the way out.
///
/// The spread is what the clip box downstream is built from, and it has to describe the same taps that
/// produced the mean - a box centred on one estimate and sized by a differently weighted one is not a
/// box around anything.
///
/// Taps are weighted by how far they sit off the centre pixel's own plane, which is what keeps a corner
/// or a silhouette from averaging two surfaces into one. Sky is not evidence about a surface and is
/// dropped outright. The count returned is the effective one, (sum w)^2 / sum w^2: where the gate threw
/// most of the neighbourhood away the pixel is still carrying nearly one tap's worth of noise, and
/// everything downstream that reasons about how many samples it has needs to be told that rather than
/// assuming nine.
/// </summary>
BasisGINeighbourhood BasisGIGather(float2 uv, float3 centrePosition, float3 centreNormal, float centreEye)
{
    float2 texel = _BasisGITracedTexelSize.xy;
    float planeScale = BasisGIPlaneTolerance(centreEye);

    float4 centre = BasisGILoadIndirect(uv);
    float4 weighted = centre;
    float4 weightedSquares = centre * centre;
    float weightSum = 1.0;
    float weightSquaredSum = 1.0;

    UNITY_UNROLL
    for (int y = -1; y <= 1; y++)
    {
        UNITY_UNROLL
        for (int x = -1; x <= 1; x++)
        {
            if (x == 0 && y == 0) { continue; }

            float2 sampleUv = uv + float2(x, y) * texel;
            float sampleRaw = BasisGISampleRawDepth(sampleUv);
            if (BasisGIIsSky(sampleRaw)) { continue; }

            float plane = abs(dot(centreNormal, BasisGIWorldPosition(sampleUv, sampleRaw) - centrePosition));
            float weight = exp(-plane / planeScale);

            float4 neighbour = BasisGILoadIndirect(sampleUv);
            weighted += neighbour * weight;
            weightedSquares += neighbour * neighbour * weight;
            weightSum += weight;
            weightSquaredSum += weight * weight;
        }
    }

    BasisGINeighbourhood hood;
    float rcpWeight = rcp(max(weightSum, BASISGI_EPSILON));
    hood.mean = weighted * rcpWeight;
    hood.deviation = sqrt(max(0.0, weightedSquares * rcpWeight - hood.mean * hood.mean));
    hood.samples = weightSum * weightSum * rcp(max(weightSquaredSum, BASISGI_EPSILON));
    return hood;
}

BasisGITemporalOutput BasisGITemporal(float2 uv)
{
    BasisGITemporalOutput output;

    float rawDepth = BasisGISampleRawDepth(uv);
    float eyeDepth = BasisGILinearEyeDepth(rawDepth);
    float3 worldPosition = BasisGIWorldPosition(uv, rawDepth);
    // Taken before any early out: screen space derivatives are only meaningful where the whole quad agrees
    // on whether to take them.
    float3 planeNormal = BasisGIPlaneNormal(worldPosition);

    // The reconstruction has to happen up here, above every early out, because the pixels that need it
    // most are the ones that take those exits - a pixel with no history behind it is handed straight to
    // the spatial pass carrying its raw one or two rays, and that is exactly the case a moving camera
    // spends most of its frame in.
    bool isSky = BasisGIIsSky(rawDepth);
    BasisGINeighbourhood hood = BasisGIGather(uv, worldPosition, planeNormal, eyeDepth);
    float4 current = isSky ? BasisGILoadIndirect(uv) : hood.mean;
    float luminance = Luminance(max(0.0, current.rgb));

    output.indirect = current;
    // Sky is written as a zero depth rather than as the far plane. Everything downstream reads its
    // neighbours' depth out of this channel, and a far plane reads as a perfectly ordinary distant surface -
    // the spatial filter would accept sky as a neighbour, and this pass's own reprojection would accept a
    // distant surface's history from a texel that only ever held sky. Zero is already the "no history here"
    // value the reprojection tests for, so it says both at once.
    output.stats = float4(isSky ? 0.0 : eyeDepth, 1.0, luminance, 0.0);

    if (isSky || _BasisGIHistoryValid < 0.5) { return output; }

    float2 previousUv;
    if (!BasisGIReproject(uv, worldPosition, previousUv)) { return output; }

    float4 historyStats = BasisGILoadHistoryStats(previousUv);
    float relativeDelta = abs(historyStats.r - eyeDepth) / max(eyeDepth, BASISGI_EPSILON);
    if (historyStats.r <= 0.0 || relativeDelta > BASISGI_DEPTH_REJECTION) { return output; }

    float4 history = BasisGILoadHistory(previousUv);

#if defined(_BASISGI_NEIGHBOURHOOD_CLAMP)
    // Variance clipping rather than a min/max box. At one or two rays per pixel the neighbourhood's extremes
    // are themselves noise, so clamping to them feeds that noise back into the history every frame and the
    // accumulation never settles. Mean plus a couple of standard deviations rejects real ghosting while
    // leaving a noisy but unbiased history alone. Both numbers come from the taps the reconstruction above
    // already paid for, so the box is centred on exactly the frame being blended in rather than on a
    // second, differently weighted estimate of it.
    float4 mean = hood.mean;
    float4 deviation = hood.deviation;

    // The box is never allowed to be narrower than what a run of misses could plausibly have hidden.
    // Colour is bounded per sample by the firefly ceiling and obscurance by its own intensity, so the
    // floor is written in each channel's own units rather than as one number that would be wrong for
    // both, and it tightens on its own as the ray budget rises. What it tightens against is how many
    // samples are really behind the mean rather than how many taps were offered: where the plane gate
    // threw most of the neighbourhood away - a corner, a silhouette - the pixel is still carrying close
    // to one tap's worth of noise, and the floor has to stay as wide as that admits.
    float sampleCount = max(1.0, hood.samples) * max(1.0, BASISGI_RAY_COUNT);
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
    float centreEye = BasisGILinearEyeDepth(centreRaw);
    float3 centrePosition = BasisGIWorldPosition(uv, centreRaw);
    // Taken before any branch: screen space derivatives are only meaningful where the whole quad agrees.
    float3 centreNormal = BasisGIPlaneNormal(centrePosition);
    BasisGIPlaneBasis basis = BasisGIBuildPlaneBasis(centrePosition, centreNormal, centreEye, _BasisGITracedTexelSize.xy);
    float4 centre = BasisGILoadIndirect(uv);

    if (BasisGIIsSky(centreRaw)) { return centre; }

    float2 axis = _BasisGIBlurAxis.xy;
    float taps = _BasisGIBlurAxis.z;
    if (taps <= 0.0) { return centre; }

    float centreLuminance = Luminance(max(0.0, centre.rgb));
    float3 centreStats = BasisGIStats(uv);
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
            float2 uvOffset = axis * ((float)offset * (side == 0 ? 1.0 : -1.0));
            float2 sampleUv = uv + uvOffset;
            if (any(sampleUv < 0.0) || any(sampleUv > 1.0)) { continue; }

            // One fetch, three answers: the tap's depth for the plane test, and the two accumulation
            // numbers the luminance gate is opened by. The depth used to come from a second fetch into the
            // full resolution depth texture, at a stride that doubles every a-trous level, and the position
            // it fed cost an inverse view projection per tap on top.
            float3 tapStats = BasisGIStats(sampleUv);
            float plane;
            bool onSurface;

            UNITY_BRANCH
            if (basis.usable)
            {
                onSurface = tapStats.x > 0.0;
                plane = BasisGIPlaneDistance(basis, uvOffset, tapStats.x);
            }
            else
            {
                float sampleRaw = BasisGISampleRawDepth(sampleUv);
                onSurface = !BasisGIIsSky(sampleRaw);
                plane = abs(dot(centreNormal, BasisGIWorldPosition(sampleUv, sampleRaw) - centrePosition));
            }

            if (!onSurface) { continue; }
            float planeWeight = exp(-plane / basis.scale);

            float convergence = saturate(min(centreStats.y, tapStats.y) / BASISGI_BLUR_CONVERGED);
            float deviation = sqrt(max(centreStats.z, tapStats.z));
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
