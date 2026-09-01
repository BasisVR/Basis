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

/// startScreen is handed in rather than computed here. Every ray a pixel casts leaves the SAME point, so
/// projecting that point is per pixel work that was being repeated per ray - a whole world to clip matrix
/// multiply and a divide, times the ray count, for a value that never changed between them.
BasisGIHit BasisGIMarch(float4 startScreen, float3 originWS, float3 directionWS, float rayLength, float noise, out bool leftScreen)
{
    BasisGIHit hit;
    hit.valid = false;
    hit.uv = float2(0.0, 0.0);
    hit.distance = rayLength;
    leftScreen = false;

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
        // The same crossing-and-thickness test this always was, against a number reduced once up front
        // rather than fetched from a four times larger texture and linearised again on every step.
        // No sky test: sky carries the sentinel in r, and nothing can be in front of that.
        float2 sceneDepth = BasisGISampleTracedDepth(uv);

        if (rayEye > sceneDepth.r && rayEye < sceneDepth.r + BasisGIThicknessAt(sceneDepth.r))
        {
            float low = previousT;
            float high = t;
            UNITY_UNROLL
            for (int refine = 0; refine < BASISGI_REFINE_STEPS; refine++)
            {
                float mid = (low + high) * 0.5;
                float2 midUv = lerp(startScreen.xy, endScreen.xy, mid);
                float midEye = 1.0 / max(BASISGI_EPSILON, lerp(invStartW, invEndW, mid));
                bool inside = midEye > BasisGISampleTracedDepth(midUv).r;
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

float2 BasisGILoadCoarseFar(float2 tracedPosition)
{
    int2 cell = int2(tracedPosition / max(1.0, _BasisGICoarseFarBlock));
    cell = clamp(cell, int2(0, 0), int2(_BasisGICoarseFarTexelSize.zw) - 1);
    return LOAD_TEXTURE2D_X(_BasisGICoarseFarDepth, cell).rg;
}

/// <summary>
/// Walks the fine depth buffer across one coarse cell the summary could not rule out, one texel or so at
/// a time, and returns the first surface the ray actually crosses.
///
/// This is the same crossing test and the same binary refine the plain march uses. The difference is only
/// where it is spent: over a fraction of the ray rather than the whole of it, so the stride is about a
/// texel instead of tens of them, and there is nothing thin enough to fall between two steps.
///
/// bracketFloor is the last position along the ray already tested against the depth buffer, carried by
/// the caller ACROSS cells, and the sampling here lands its final step exactly on exitT. Between the two,
/// a cell boundary stops being special: without them, each cell restarted its refine bracket at its own
/// entry and left a jitter-sized sliver before it unsampled, so a crossing falling in that sliver either
/// snapped its refined hit to the boundary - shearing the reflected image - or overshot the thickness
/// test and was dropped. Both drew a line at EVERY cell boundary, evenly spaced across every reflective
/// surface, at exactly the coarse block period.
/// </summary>
BasisGIHit BasisGIFineSegment(float2 origin, float2 direction, float2 size, float entryT, float exitT,
    float invStartW, float invEndW, float rayLength, float noise, inout bool inFront, inout float bracketFloor)
{
    BasisGIHit hit;
    hit.valid = false;
    hit.uv = float2(0.0, 0.0);
    hit.distance = rayLength;

    float span = exitT - entryT;
    float previousT = bracketFloor;

    UNITY_LOOP
    for (int step = 1; step <= BASISGI_FINE_STEPS; step++)
    {
        // The denominator keeps the jitter while pinning the last step to exitT itself, so the walk hands
        // the next cell a bracket that really ends where that cell begins.
        float t = entryT + span * (((float)step - noise) / ((float)BASISGI_FINE_STEPS - noise));
        float2 position = origin + direction * t;
        float2 uv = position / size;
        if (any(uv < 0.0) || any(uv > 1.0)) { return hit; }

        float rayEye = 1.0 / max(BASISGI_EPSILON, lerp(invStartW, invEndW, t));
        // Sky needs no branch of its own here either: the sentinel leaves the ray in front, which is
        // exactly the state the explicit sky case used to set by hand before continuing.
        //
        // The pair is read as an INTERVAL: in front means before the nearest thing the texel holds,
        // a crossing means past the furthest, and a ray BETWEEN the two is interleaved with the span -
        // a grazing ray over its own floor, a silhouette's spread - which is not a crossing and not a
        // reason to change the carried state. Answering the ambiguous band from a representative was a
        // per-texel coin flip that landed in whole rows, and the evenly spaced lines it drew across
        // every reflective surface vanished outright at Full resolution, where the flip has no room to
        // exist. The diffuse pyramid keeps its two channels equal, so for it every test below is
        // bit-for-bit the arithmetic it always ran.
        float2 sceneDepth = BasisGISampleTracedDepth(uv);
        float thickness = BasisGIThicknessAt(sceneDepth.g);
        bool crossed = inFront && rayEye > sceneDepth.g;
        if (rayEye <= sceneDepth.r) { inFront = true; }
        else if (rayEye > sceneDepth.g + thickness) { inFront = false; }

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
        if (crossed && rayEye < sceneDepth.g + thickness)
        {
            float low = previousT;
            float high = t;
            UNITY_UNROLL
            for (int refine = 0; refine < BASISGI_REFINE_STEPS; refine++)
            {
                float mid = (low + high) * 0.5;
                float2 midUv = (origin + direction * mid) / size;
                float midEye = 1.0 / max(BASISGI_EPSILON, lerp(invStartW, invEndW, mid));
                bool inside = midEye > BasisGISampleTracedDepth(midUv).r;
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

    bracketFloor = previousT;
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
/// startInFront is what the caller can honestly claim about the ray's first sample. A cosine ray leaves
/// its surface steeply enough that it is in front of the depth buffer by its first test, and saying so
/// lets a genuinely adjacent surface be hit at once. A mirror ray reflected off a floor seen at an angle
/// hugs that floor for its first texels, and at a reduced trace resolution the representative depth under
/// those texels can sit BEHIND the ray - an armed crossing state then declares a hit on the surface the
/// ray just left, and every reflection becomes a reflection of its own floor. Passing false starts the
/// state unarmed: nothing counts as a crossing until the ray has actually been observed in front once.
///
/// maxCells bounds the coarse walk, and it is a parameter because the two callers need different reach:
/// the diffuse gather's bounce rays are a few metres and its ceiling was sized for them, but a mirror ray
/// carries sixty-plus metres and can genuinely need to cross the whole screen - under the short ceiling it
/// died a quarter of the way across, and every reflection ended on the same "reflections stop here" line.
///
/// observed is how much of the ray was actually tested against the depth buffer before the answer, as a
/// fraction: one for a walk that ran to its end, left the screen having looked at everything on the way,
/// or hit; less when the cell ceiling or the fine budget ran out first. A miss with observed below one is
/// not evidence of sky - it is a ray the walk stopped looking at - and the reflection trace fades such an
/// answer out by exactly this number rather than asserting a fallback along a hard budget boundary.
BasisGIHit BasisGIMarchHierarchicalCore(bool startInFront, int maxCells, float4 startScreen, float3 originWS, float3 directionWS, float rayLength, float noise, out bool leftScreen, out float observed)
{
    BasisGIHit hit;
    hit.valid = false;
    hit.uv = float2(0.0, 0.0);
    hit.distance = rayLength;
    leftScreen = false;
    observed = 1.0;

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
    bool inFront = startInFront;

    // The last position along the ray already tested - or ruled out wholesale by a coarse skip - carried
    // across cells so a crossing detected by a cell's first sample refines into the previous cell's tail
    // instead of snapping to the boundary between them. See BasisGIFineSegment for the line this drew.
    float bracketFloor = 0.0;

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
    for (int cell = 0; cell < maxCells; cell++)
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

        // Ask the far level first. It answers for a whole block of coarse cells at once, so a ray crossing
        // empty room clears eight cells per tap instead of one, and the cell ceiling stops being a limit on
        // REACH and becomes a limit on complexity actually met. This is what the reflection ray needed: its
        // ceiling was raised to 128 cells because a mirror ray has to cross the screen, and every one of
        // those cells was a tap whether or not anything was in it.
        //
        // Both dismissals below are the same two statements the coarse test makes, asked of a bigger block.
        // The fold guarantees they still hold: the far texel's r is the minimum of the minima underneath it
        // and its g the maximum of the maxima, so a ray in front of r is in front of every coarse cell in
        // the block, and one behind g is behind all of them. Nothing here can dismiss a cell the coarse
        // level would have kept - it can only reach the same answer sooner.
        if (_BasisGICoarseFarBlock >= 1.0)
        {
            float farExitT = min(1.0, BasisGICellExit(origin, direction, position, _BasisGICoarseFarBlock));
            float farEntryEye = 1.0 / max(BASISGI_EPSILON, lerp(invStartW, invEndW, t));
            float farExitEye = 1.0 / max(BASISGI_EPSILON, lerp(invStartW, invEndW, farExitT));
            float farDeepest = max(farEntryEye, farExitEye);
            float farShallowest = min(farEntryEye, farExitEye);

            // Not named "far": that is a legacy reserved identifier in the D3D headers some HLSL
            // toolchains still pull in, and it fails to compile on the ones that do.
            float2 farCoarse = BasisGILoadCoarseFar(position);

            // The carried state updates exactly as the coarse skips below do, and for the same reason: a
            // block crossed entirely in front holds no crossing, so the refine floor may advance past all
            // of it. observed is untouched - this is looking, not assuming.
            if (farDeepest <= farCoarse.r)
            {
                inFront = true;
                bracketFloor = farExitT;
                t = farExitT;
                continue;
            }
            if (farShallowest > farCoarse.g + BasisGIThicknessAt(farCoarse.g))
            {
                inFront = false;
                bracketFloor = farExitT;
                t = farExitT;
                continue;
            }
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
            // in front. Skipping a cell must update the carried state exactly as walking it would have -
            // and that includes the refine floor: a cell the ray crossed entirely in front of provably
            // holds no crossing, so the floor may advance past it.
            inFront = true;
            bracketFloor = exitT;
        }
        else if (behindEverything)
        {
            inFront = false;
            bracketFloor = exitT;
        }
        else if (fineBudget > 0)
        {
            BasisGIHit fine = BasisGIFineSegment(origin, direction, size, t, exitT, invStartW, invEndW, rayLength, noise, inFront, bracketFloor);
            if (fine.valid) { return fine; }
            fineBudget -= BASISGI_FINE_STEPS;
        }
        else
        {
            // Out of budget. Keep crossing cells rather than ending the ray here: a ray that runs out still
            // has to be able to leave the screen and take the off screen fallback with it, and ending it
            // early throws that away as well as the hit. From here on the walk is assuming rather than
            // looking, and the observed fraction has to say so.
            inFront = false;
            bracketFloor = exitT;
            observed = min(observed, t);
        }

        t = exitT;
    }

    // Ran out of cells with ray still to walk: everything past t was never looked at, and a miss reported
    // from here must not carry the confidence of one that genuinely searched its whole path.
    if (t < 1.0) { observed = min(observed, t); }

    return hit;
}

/// <summary>The march at the diffuse gather's own ceiling, exactly as it always ran - the bounce rays it
/// walks are a few metres and never needed more.</summary>
BasisGIHit BasisGIMarchHierarchicalFrom(bool startInFront, float4 startScreen, float3 originWS, float3 directionWS, float rayLength, float noise, out bool leftScreen)
{
    float observed;
    return BasisGIMarchHierarchicalCore(startInFront, BASISGI_COARSE_MAX_CELLS, startScreen, originWS, directionWS, rayLength, noise, leftScreen, observed);
}

BasisGIHit BasisGIMarchHierarchical(float4 startScreen, float3 originWS, float3 directionWS, float rayLength, float noise, out bool leftScreen)
{
    // The cosine gather's original contract, unchanged: its ray leaves the surface along a lobe around the
    // normal, so it genuinely starts in front - and saying so is what lets contact geometry be hit at once.
    return BasisGIMarchHierarchicalFrom(true, startScreen, originWS, directionWS, rayLength, noise, leftScreen);
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

        float2 sceneDepth = BasisGISampleTracedDepth(screen.xy);

        // clip.w IS the eye depth under a perspective projection, and the projection a few lines up
        // already produced it - so the world to view matrix multiply spent here per shadow step, per
        // emitter, per pixel was recomputing a number this function was already holding.
        float sampleEye;
        UNITY_BRANCH
        if (unity_OrthoParams.w < 0.5) { sampleEye = screen.w; }
        else { sampleEye = -TransformWorldToView(samplePosition).z; }

        // A tap that found an occluder is evidence; taps that could not be taken are not evidence of the
        // opposite, so one hit shadows the emitter outright rather than being diluted by them.
        if (sampleEye > sceneDepth.r && sampleEye < sceneDepth.r + BasisGIThicknessAt(sceneDepth.r) * 4.0) { return 0.0; }
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
    // Every ray leaves this one point, so it is projected once here rather than once per ray in the march.
    float4 startScreen = BasisGIWorldToScreen(originWS);

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
        BasisGIHit hit = BasisGIMarchHierarchical(startScreen, originWS, direction, BASISGI_MAX_RAY_LENGTH, noise, leftScreen);
#else
        BasisGIHit hit = BasisGIMarch(startScreen, originWS, direction, BASISGI_MAX_RAY_LENGTH, noise, leftScreen);
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
