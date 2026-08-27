#ifndef BASIS_GLOBAL_ILLUMINATION_TRACE_INCLUDED
#define BASIS_GLOBAL_ILLUMINATION_TRACE_INCLUDED

#include "./BasisGlobalIlluminationCommon.hlsl"

#define BASISGI_REFINE_STEPS 4
#define BASISGI_EMITTER_SHADOW_STEPS 8

struct BasisGIHit
{
    bool valid;
    float2 uv;
    float distance;
};

float BasisGIThicknessAt(float eyeDepth)
{
    return BASISGI_THICKNESS * (1.0 + eyeDepth * 0.05);
}

BasisGIHit BasisGIMarch(float3 originWS, float3 directionWS, float rayLength, float noise, out bool leftScreen)
{
    BasisGIHit hit;
    hit.valid = false;
    hit.uv = float2(0.0, 0.0);
    hit.distance = rayLength;
    leftScreen = false;

    float4 startScreen = BasisGIWorldToScreen(originWS);
    float4 endScreen = BasisGIWorldToScreen(originWS + directionWS * rayLength);

    if (startScreen.w <= BASISGI_EPSILON) { return hit; }
    if (endScreen.w <= BASISGI_EPSILON)
    {
        float shortened = rayLength * saturate((startScreen.w - _ProjectionParams.y) / max(BASISGI_EPSILON, startScreen.w - endScreen.w)) * 0.98;
        rayLength = max(BASISGI_EPSILON, shortened);
        endScreen = BasisGIWorldToScreen(originWS + directionWS * rayLength);
        if (endScreen.w <= BASISGI_EPSILON) { return hit; }
    }

    float invStartW = 1.0 / startScreen.w;
    float invEndW = 1.0 / endScreen.w;
    int steps = (int)BASISGI_RAY_STEPS;
    float stepSize = 1.0 / (float)steps;
    float jitter = lerp(0.5, noise, BASISGI_JITTER);

    float previousT = 0.0;

    UNITY_LOOP
    for (int step = 1; step <= steps; step++)
    {
        float t = saturate(((float)step - jitter) * stepSize);
        float2 uv = lerp(startScreen.xy, endScreen.xy, t);

        if (any(uv < 0.0) || any(uv > 1.0))
        {
            leftScreen = true;
            hit.uv = saturate(uv);
            hit.distance = t * rayLength;
            return hit;
        }

        float rayEye = 1.0 / max(BASISGI_EPSILON, lerp(invStartW, invEndW, t));
        float rawDepth = BasisGISampleRawDepth(uv);
        if (BasisGIIsSky(rawDepth)) { previousT = t; continue; }

        float sceneEye = BasisGILinearEyeDepth(rawDepth);
        float delta = rayEye - sceneEye;

        if (delta > 0.0 && delta < BasisGIThicknessAt(sceneEye))
        {
            float low = previousT;
            float high = t;
            UNITY_UNROLL
            for (int refine = 0; refine < BASISGI_REFINE_STEPS; refine++)
            {
                float mid = (low + high) * 0.5;
                float2 midUv = lerp(startScreen.xy, endScreen.xy, mid);
                float midEye = 1.0 / max(BASISGI_EPSILON, lerp(invStartW, invEndW, mid));
                float midRaw = BasisGISampleRawDepth(midUv);
                float midScene = BasisGILinearEyeDepth(midRaw);
                bool inside = !BasisGIIsSky(midRaw) && (midEye - midScene) > 0.0;
                low = inside ? low : mid;
                high = inside ? mid : high;
            }
            hit.valid = true;
            hit.uv = lerp(startScreen.xy, endScreen.xy, high);
            hit.distance = high * rayLength;
            return hit;
        }

        previousT = t;
    }

    return hit;
}

#define BASISGI_COARSE_MAX_CELLS 28
// How finely a cell that could hold a hit is walked. Driven by the Ray Steps setting rather than fixed, so
// the quality ladder still means something here: without this the slider would be entirely inert whenever
// the hierarchical march is on, because nothing else in this path reads it.
#define BASISGI_FINE_STEPS (int)clamp(BASISGI_RAY_STEPS * 0.5, 4.0, 16.0)

/// <summary>
/// Where the ray leaves the coarse cell it is currently inside, as a distance along the whole ray.
///
/// The nudge past the boundary is what keeps the walk moving: land exactly on a cell edge and floor()
/// is free to name the cell just left, and the loop then tests the same cell until it runs out of
/// iterations.
/// </summary>
float BasisGICellExit(float2 origin, float2 direction, float2 position, float cellSize)
{
    float2 cellMin = floor(position / cellSize) * cellSize;
    float2 boundary = cellMin + float2(direction.x >= 0.0 ? cellSize : 0.0, direction.y >= 0.0 ? cellSize : 0.0);
    float2 safe = float2(abs(direction.x) < 1e-6 ? 1e-6 : direction.x, abs(direction.y) < 1e-6 ? 1e-6 : direction.y);
    float2 toBoundary = (boundary - origin) / safe;
    // A component travelling away from its boundary hands back a distance already behind us; it must not
    // win the minimum.
    toBoundary = float2(
        safe.x * (boundary.x - position.x) < 0.0 ? 1e30 : toBoundary.x,
        safe.y * (boundary.y - position.y) < 0.0 ? 1e30 : toBoundary.y);
    return min(toBoundary.x, toBoundary.y) + 1e-4;
}

float2 BasisGILoadCoarse(float2 tracedPosition)
{
    int2 cell = int2(tracedPosition / max(1.0, BASISGI_COARSE_BLOCK));
    cell = clamp(cell, int2(0, 0), int2(_BasisGICoarseTexelSize.zw) - 1);
    return LOAD_TEXTURE2D_X(_BasisGICoarseDepth, cell).rg;
}

/// <summary>
/// Walks the fine depth buffer across one coarse cell the summary could not rule out, one texel or so at
/// a time, and returns the first surface the ray actually crosses.
///
/// This is the same crossing test and the same binary refine the plain march uses. The difference is only
/// where it is spent: over a fraction of the ray rather than the whole of it, so the stride is about a
/// texel instead of tens of them, and there is nothing thin enough to fall between two steps.
/// </summary>
BasisGIHit BasisGIFineSegment(float2 origin, float2 direction, float2 size, float entryT, float exitT,
    float invStartW, float invEndW, float rayLength, float noise, inout bool inFront)
{
    BasisGIHit hit;
    hit.valid = false;
    hit.uv = float2(0.0, 0.0);
    hit.distance = rayLength;

    float span = exitT - entryT;
    float previousT = entryT;

    UNITY_LOOP
    for (int step = 1; step <= BASISGI_FINE_STEPS; step++)
    {
        float t = entryT + span * (((float)step - noise) / (float)BASISGI_FINE_STEPS);
        float2 position = origin + direction * t;
        float2 uv = position / size;
        if (any(uv < 0.0) || any(uv > 1.0)) { return hit; }

        float rayEye = 1.0 / max(BASISGI_EPSILON, lerp(invStartW, invEndW, t));
        float rawDepth = BasisGISampleRawDepth(uv);
        if (BasisGIIsSky(rawDepth)) { previousT = t; inFront = true; continue; }

        float sceneEye = BasisGILinearEyeDepth(rawDepth);
        float delta = rayEye - sceneEye;
        bool crossed = inFront && delta > 0.0;
        inFront = delta <= 0.0;

        // A CROSSING, not a proximity. Being within the thickness of a surface is not evidence of having
        // met it - the ray may have been behind that surface all along and merely passed close to its back.
        // The plain march gets this for free by stepping outwards from a point known to be in front; a walk
        // that resumes part way along the ray knows no such thing, so the state is carried rather than
        // rebuilt per cell.
        //
        // Honesty about what this bought: nothing measurable. It was written to explain a 15% excess that
        // turned out not to be an excess at all - the reading that prompted it compared this march against
        // the RAY TRACED gather, which shades a hit by relighting it rather than by reading the colour
        // already there, so the two were never comparable in absolute terms. Against a converged run of
        // this same estimator the march was right all along. Kept because a crossing test is the correct
        // formulation and it costs a bool, not because it fixed anything.
        if (crossed && delta < BasisGIThicknessAt(sceneEye))
        {
            float low = previousT;
            float high = t;
            UNITY_UNROLL
            for (int refine = 0; refine < BASISGI_REFINE_STEPS; refine++)
            {
                float mid = (low + high) * 0.5;
                float2 midUv = (origin + direction * mid) / size;
                float midEye = 1.0 / max(BASISGI_EPSILON, lerp(invStartW, invEndW, mid));
                float midRaw = BasisGISampleRawDepth(midUv);
                bool inside = !BasisGIIsSky(midRaw) && (midEye - BasisGILinearEyeDepth(midRaw)) > 0.0;
                low = inside ? low : mid;
                high = inside ? mid : high;
            }
            hit.valid = true;
            hit.uv = (origin + direction * high) / size;
            hit.distance = high * rayLength;
            return hit;
        }

        previousT = t;
    }

    return hit;
}

/// <summary>
/// The hierarchical march: skip whole blocks of the screen using a coarse summary of the depth buffer,
/// and only look at real texels where that summary admits a hit is possible.
///
/// A block is dismissed on either of two grounds, and each is a statement about the whole block rather
/// than about any texel in it. If the ray is still in front of the CLOSEST thing in the block for the
/// whole of its passage, it cannot have hit anything inside. If it is already behind the FURTHEST thing
/// by more than the thickness the crossing test would have accepted, it has passed clean through and out
/// the far side. Everything else is a maybe, and a maybe is answered by looking properly.
/// </summary>
BasisGIHit BasisGIMarchHierarchical(float3 originWS, float3 directionWS, float rayLength, float noise, out bool leftScreen)
{
    BasisGIHit hit;
    hit.valid = false;
    hit.uv = float2(0.0, 0.0);
    hit.distance = rayLength;
    leftScreen = false;

    float4 startScreen = BasisGIWorldToScreen(originWS);
    float4 endScreen = BasisGIWorldToScreen(originWS + directionWS * rayLength);
    if (startScreen.w <= BASISGI_EPSILON) { return hit; }
    if (endScreen.w <= BASISGI_EPSILON)
    {
        float shortened = rayLength * saturate((startScreen.w - _ProjectionParams.y) / max(BASISGI_EPSILON, startScreen.w - endScreen.w)) * 0.98;
        rayLength = max(BASISGI_EPSILON, shortened);
        endScreen = BasisGIWorldToScreen(originWS + directionWS * rayLength);
        if (endScreen.w <= BASISGI_EPSILON) { return hit; }
    }

    float2 size = _BasisGITracedTexelSize.zw;
    float2 origin = startScreen.xy * size;
    float2 direction = endScreen.xy * size - origin;

    float invStartW = 1.0 / startScreen.w;
    float invEndW = 1.0 / endScreen.w;
    float cellSize = max(1.0, BASISGI_COARSE_BLOCK);

    // Start a fraction of a cell in, jittered per pixel, so the ray steps past its own surface and no two
    // neighbours agree on where their first cell boundary falls.
    float t = lerp(0.25, 1.0, noise) / max(1.0, length(direction));

    // Whether the ray is currently in front of the depth buffer, carried across the WHOLE walk rather than
    // rebuilt inside each cell. This is what makes the test downstream a crossing test; see BasisGIFineSegment.
    // The ray leaves a surface along its own normal, so it starts in front of it.
    bool inFront = true;

    // A ceiling on the fine walking one ray may pay for, in steps, across every cell it passes through.
    //
    // Coarse taps are one apiece and self-limiting, but the fine walks are not: a ray threading a lot of
    // depth complexity can fail to rule out cell after cell, and without a ceiling its cost is bounded only
    // by the cell count times the steps in a cell. Typical rays never approach this - most of a room is
    // empty space the coarse test crosses in a single tap each - but a frame is only as fast as its worst
    // pixel, and in a headset one slow frame is a dropped one. Spending the budget nearest the origin is
    // the right way to run out: that is where the bounce is brightest and where the plain march was worst.
    //
    // Sized as a backstop for the pathological ray, NOT as a routine limiter, and the difference is not
    // subtle. Measured 2026-08-27 against a converged march: a budget of one Ray Steps - two fine segments
    // - took the contact probe from 1% under converged to 5% under and the open floor from 0.1% to 9%,
    // which is most of the way back to the march it replaced. Four times that leaves both readings where
    // they were and still bounds the worst ray. If this ever needs tightening for frame time, measure the
    // quality cost at the same time; it is steep.
    int fineBudget = (int)max(BASISGI_RAY_STEPS * 4.0, 32.0);

    UNITY_LOOP
    for (int cell = 0; cell < BASISGI_COARSE_MAX_CELLS; cell++)
    {
        if (t >= 1.0) { break; }

        float2 position = origin + direction * t;
        if (any(position < 0.0) || any(position >= size))
        {
            leftScreen = true;
            hit.uv = saturate(position / size);
            hit.distance = t * rayLength;
            return hit;
        }

        float exitT = min(1.0, BasisGICellExit(origin, direction, position, cellSize));
        float entryEye = 1.0 / max(BASISGI_EPSILON, lerp(invStartW, invEndW, t));
        float exitEye = 1.0 / max(BASISGI_EPSILON, lerp(invStartW, invEndW, exitT));
        // A cosine hemisphere ray can be aimed back towards the viewer, so which end of the segment is
        // deepest is not known in advance - both bounds have to come off the pair.
        float deepest = max(entryEye, exitEye);
        float shallowest = min(entryEye, exitEye);

        float2 coarse = BasisGILoadCoarse(position);
        bool inFrontOfEverything = deepest <= coarse.r;
        bool behindEverything = shallowest > coarse.g + BasisGIThicknessAt(coarse.g);

        if (inFrontOfEverything)
        {
            // Skipped because the ray stays nearer than anything in the block, so it leaves the block still
            // in front. Skipping a cell must update the carried state exactly as walking it would have.
            inFront = true;
        }
        else if (behindEverything)
        {
            inFront = false;
        }
        else if (fineBudget > 0)
        {
            BasisGIHit fine = BasisGIFineSegment(origin, direction, size, t, exitT, invStartW, invEndW, rayLength, noise, inFront);
            if (fine.valid) { return fine; }
            fineBudget -= BASISGI_FINE_STEPS;
        }
        else
        {
            // Out of budget. Keep crossing cells rather than ending the ray here: a ray that runs out still
            // has to be able to leave the screen and take the off screen fallback with it, and ending it
            // early throws that away as well as the hit.
            inFront = false;
        }

        t = exitT;
    }

    return hit;
}

/// <summary>
/// How much of an emitter this point can see, tested against the depth buffer.
///
/// The path is walked in world space and each point is projected on its own, rather than interpolated
/// between two projected endpoints. An emitter that has passed behind the camera has no projection to
/// interpolate towards, and the old form gave up on the whole segment the moment that happened - so a
/// wall stopped casting its shadow at the instant the light behind it left the view, and the room
/// brightened for no reason a player could see. Walking it in world space keeps every tap that is still
/// on screen, which includes the ones nearest the surface being shaded, where an occluder usually is.
///
/// The walk is also dithered by the pixel's own noise. A single undithered set of tap positions makes
/// the whole shadow edge flip on the same frame; offsetting it per pixel turns that into something the
/// blur and the temporal filter average into a soft edge.
/// </summary>
float BasisGIEmitterVisibility(float3 originWS, float3 emitterWS, float noise)
{
#if defined(_BASISGI_EMITTER_OCCLUSION)
    float3 toEmitter = emitterWS - originWS;
    if (dot(toEmitter, toEmitter) <= BASISGI_EPSILON) { return 1.0; }

    UNITY_UNROLL
    for (int step = 1; step < BASISGI_EMITTER_SHADOW_STEPS; step++)
    {
        float t = ((float)step - 0.5 + noise) / (float)BASISGI_EMITTER_SHADOW_STEPS;
        float3 samplePosition = originWS + toEmitter * t;

        float4 screen = BasisGIWorldToScreen(samplePosition);
        if (screen.w <= BASISGI_EPSILON) { continue; }
        if (any(screen.xy < 0.0) || any(screen.xy > 1.0)) { continue; }

        float rawDepth = BasisGISampleRawDepth(screen.xy);
        if (BasisGIIsSky(rawDepth)) { continue; }

        float sceneEye = BasisGILinearEyeDepth(rawDepth);
        float sampleEye = -TransformWorldToView(samplePosition).z;
        float delta = sampleEye - sceneEye;

        // A tap that found an occluder is evidence; taps that could not be taken are not evidence of the
        // opposite, so one hit shadows the emitter outright rather than being diluted by them.
        if (delta > 0.0 && delta < BasisGIThicknessAt(sceneEye) * 4.0) { return 0.0; }
    }
    return 1.0;
#else
    return 1.0;
#endif
}

float3 BasisGIEmitters(float3 originWS, float3 normalWS, float noise)
{
#if defined(_BASISGI_EMITTERS)
    float3 total = float3(0.0, 0.0, 0.0);
    int count = min(_BasisGIEmitterCount, BASISGI_MAX_EMITTERS);

    UNITY_LOOP
    for (int index = 0; index < count; index++)
    {
        float4 sphere = _BasisGIEmitterSpheres[index];
        float4 radiance = _BasisGIEmitterRadiance[index];
        float3 toEmitter = sphere.xyz - originWS;
        float distanceSquared = dot(toEmitter, toEmitter);
        float range = radiance.w;
        if (distanceSquared >= range * range) { continue; }

        float distance = sqrt(max(distanceSquared, BASISGI_EPSILON));
        float3 direction = toEmitter / distance;
        float cosine = saturate(dot(normalWS, direction));
        if (cosine <= 0.0) { continue; }

        float radius = max(sphere.w, BASISGI_EPSILON);
        float solidAngle = (radius * radius) / max(distanceSquared, radius * radius);
        float attenuation = saturate(1.0 - distance / range);
        attenuation *= attenuation;

        float contribution = cosine * solidAngle * attenuation;
        if (contribution <= BASISGI_EPSILON) { continue; }

        // Each emitter walks the path from a different offset, so two of them never share a shadow edge.
        float offset = frac(noise + (float)index * 0.6180339887);
        total += radiance.rgb * contribution * BasisGIEmitterVisibility(originWS, sphere.xyz, offset);
    }
    return total * BASISGI_EMITTER_INTENSITY * INV_PI;
#else
    return float3(0.0, 0.0, 0.0);
#endif
}

float4 BasisGITrace(float2 uv, float2 positionSS)
{
    float rawDepth = BasisGISampleRawDepth(uv);
    if (BasisGIIsSky(rawDepth)) { return float4(0.0, 0.0, 0.0, 1.0); }

    float eyeDepth = BasisGILinearEyeDepth(rawDepth);
    float fade = BasisGIDistanceFade(eyeDepth);
    if (fade <= 0.0) { return float4(0.0, 0.0, 0.0, 1.0); }

    float3 viewPosition = BasisGIViewPosition(uv, rawDepth);
    float3 worldPosition = BasisGIWorldPosition(uv, rawDepth);
    float3 normalWS = BasisGIReconstructNormal(uv, viewPosition, rawDepth);

    float normalBias = 0.01 + eyeDepth * 0.002;
    float3 originWS = worldPosition + normalWS * normalBias;

    float noise = BasisGIInterleavedGradientNoise(positionSS, BASISGI_FRAME_INDEX);
    float3x3 basis = BasisGIOrthonormalBasis(normalWS);

    int rayCount = (int)BASISGI_RAY_COUNT;
    float3 radianceSum = float3(0.0, 0.0, 0.0);
    float occlusionSum = 0.0;

    UNITY_LOOP
    for (int ray = 0; ray < rayCount; ray++)
    {
        // DO NOT "FIX" THIS. Both axes of the rotation come off the one gradient, so every pixel's offsets
        // sit on a single line of the unit square instead of filling it. That is a degenerate two
        // dimensional sample, it is meant to be here, and it has been measured twice:
        //
        //     second axis from an R2 lattice   raw trace noise 0.00221 -> 0.00315   (+43%)
        //     second axis from an integer hash                 0.00221 -> 0.00379   (+71%)
        //
        // Both "repairs" make this gather NOISIER. The same scalar sets the march's step offset a few lines
        // down, so one gradient carries the pixel's whole sampling state - and what the spatial filter
        // downstream needs is not independence between the axes but error that varies smoothly between
        // neighbours, so that averaging them cancels it. A second independent axis destroys exactly that.
        //
        // The ray traced kernel makes the opposite choice for the opposite reason and it is not an
        // inconsistency: its jitter is only a rotation, nothing else reads it, so a second axis costs it
        // nothing there and buys 23%.
        float2 sample = BasisGIHammersley((uint)ray, (uint)rayCount);
        sample.y = frac(sample.y + noise);
        sample.x = frac(sample.x + noise * 0.618034);

        float3 direction = BasisGICosineDirection(sample, basis);
        bool leftScreen;
#if defined(_BASISGI_HIERARCHICAL_MARCH)
        BasisGIHit hit = BasisGIMarchHierarchical(originWS, direction, BASISGI_MAX_RAY_LENGTH, noise, leftScreen);
#else
        BasisGIHit hit = BasisGIMarch(originWS, direction, BASISGI_MAX_RAY_LENGTH, noise, leftScreen);
#endif

        float3 radiance;
        if (hit.valid)
        {
            radiance = BasisGISampleSceneColor(hit.uv);
#if defined(_BASISGI_HIT_NORMAL)
            float hitRaw = BasisGISampleRawDepth(hit.uv);
            float3 hitView = BasisGIViewPosition(hit.uv, hitRaw);
            float3 hitNormal = BasisGIReconstructNormal(hit.uv, hitView, hitRaw);
            radiance *= saturate(-dot(direction, hitNormal));
#endif
            occlusionSum += 1.0 - saturate(hit.distance / max(BASISGI_OBSCURANCE_RADIUS, BASISGI_EPSILON));
        }
        else
        {
            radiance = BasisGIFallbackRadiance(direction);
#if defined(_BASISGI_RAY_REUSE)
            if (leftScreen) { radiance = lerp(radiance, BasisGISampleSceneColor(hit.uv), 0.5); }
#endif
        }

        radianceSum += BasisGIClampFirefly(radiance);
    }

    float3 indirect = radianceSum / max(1.0, (float)rayCount);
    indirect += BasisGIEmitters(originWS, normalWS, noise);

    float obscurance = 1.0 - saturate(occlusionSum / max(1.0, (float)rayCount)) * BASISGI_OBSCURANCE;

    return float4(indirect * fade, lerp(1.0, obscurance, fade));
}

#endif
