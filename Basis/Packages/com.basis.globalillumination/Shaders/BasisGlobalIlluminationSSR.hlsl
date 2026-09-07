#ifndef BASIS_GLOBAL_ILLUMINATION_SSR_INCLUDED
#define BASIS_GLOBAL_ILLUMINATION_SSR_INCLUDED

// The screen space half of the reflections: the same mirror ray the ray traced kernel casts, walked
// through the depth buffer with the same hierarchical march the diffuse gather uses, instead of through a
// BVH. It exists because the ray traced backend is the only reflection path there was, and the default
// shipping mode is Screen Space - so the players who most needed a cheap reflection were exactly the ones
// who got none.
//
// The one thing screen space cannot do that the kernel can is see the current frame: this trace runs
// before the opaque draws, because the opaque shaders are what consume its result, and at that point the
// only colour in existence is the previous frame's - reprojected through the stored view projection, the
// exact matrix and flip convention the temporal filter already reprojects with. A hit lands on the
// previous frame's image of the same surface; whatever moved in between is one frame stale, and the
// specular accumulation's fast response is what absorbs that.
//
// Output contract is the ray traced kernel's, exactly, because everything downstream - resolve, temporal,
// blur, upsample, and BasisSampleTracedReflection in the lit shaders - is shared: rgb is the reflected
// radiance already scaled by intensity, alpha is confidence, and both are faded together by distance. A
// pixel with nothing worth saying writes zero confidence and the lit shader keeps its reflection probe.

#include "./BasisGlobalIlluminationTrace.hlsl"

TEXTURE2D_X(_BasisGISpecularPriorColor);
/// x: specular intensity, applied here so the value published is final - the shared stages apply no look
/// controls to a reflection. y: whether the prior colour target holds a frame recent enough to read; zero
/// until the capture pass has run once, after a resize, and after a camera gap long enough that
/// reprojecting across it would be a lie.
float4 _BasisGISSRParams;

#define BASISGI_SSR_INTENSITY   _BasisGISSRParams.x
#define BASISGI_SSR_PRIOR_VALID _BasisGISSRParams.y

/// How far along the mirror direction "the sky" is taken to sit, in metres. Far enough that the
/// parallax between two consecutive camera positions is a fraction of a texel - which is what makes
/// reprojecting it as an ordinary point sound - and small enough that the projection keeps full float
/// precision. Nothing scene-sized compares against it.
#define BASISGI_SSR_SKY_RANGE 4096.0

/// Where the fade on rays aimed back at the eye begins and completes, as the cosine of the angle
/// between the mirror ray and the direction to the camera. What such a ray reflects is behind the
/// camera, which no screen space method can see: the march either misses into a fallback that claims
/// sky where there is a room, or worse, crosses unrelated geometry on its way toward the near plane.
/// Fading those rays' answers hands the pixel back to the reflection probe smoothly instead - the same
/// judgment Unity's post processing stack ships as its vignette/fade pair. A mirror facing the camera
/// head-on is entirely this case, and Basis has real mirrors for that job.
#define BASISGI_SSR_FACING_FADE_START 0.55
#define BASISGI_SSR_FACING_FADE_END   0.9

/// The coarse-cell ceiling for the mirror ray's walk. The diffuse gather's ceiling is sized for bounce
/// rays a few metres long; a reflection ray carries sixty-plus metres and legitimately crosses the whole
/// screen, and under the short ceiling every reflection ended on the same exhaustion line partway across.
/// Cells beyond a hit or a screen exit are never walked, so this bounds the pathological ray, not the
/// typical one - and the fine-step budget inside the walk is unchanged, which is where the real per-pixel
/// cost lives.
#define BASISGI_SSR_MAX_CELLS 128

/// <summary>
/// Confidence in a hit found near the edge of the screen. A hit there is real this frame and gone the
/// moment the camera turns a degree further, and a reflection that pops between traced and probe at the
/// screen edge is far more visible than one that fades a little early. The last few percent of the screen
/// ramp to zero; everywhere else this is one.
/// </summary>
float BasisGISSREdgeFade(float2 uv)
{
    float2 margin = min(uv, 1.0 - uv);
    return saturate(min(margin.x, margin.y) * 12.5);
}

// A missed ray's answer depends on which fallback the volume asked for, and the two modes now mean what
// they say. Sky claims the bound environment with full confidence - the explicit "reflections read the
// sky" opt-in. Reflection Probe - the default - reports NO DATA instead: the lit shader consuming this
// texture is already holding the surface's own reflection probes, local and box projected and blended
// per object, which a fullscreen pass cannot see and a global environment guess should not override.
// Zero confidence hands the pixel to them, and every partial fade in the trace - screen edges, rays
// aimed at the eye, budget coverage, slivers - blends toward them by the same alpha.

/// <summary>
/// The previous frame's colour at a world point, through the stored view projection - the same matrix and
/// flip convention the temporal filter reprojects with. False when the point was not on the previous
/// frame's screen at all, which the caller answers with a fallback rather than a clamped-edge smear.
/// </summary>
bool BasisGISSRSamplePrior(float3 worldPoint, inout float3 colour)
{
    float4 previousClip = mul(BasisGIPreviousViewProjection(), float4(worldPoint, 1.0));
    if (previousClip.w <= BASISGI_EPSILON) { return false; }

    float2 previousUv = previousClip.xy / previousClip.w * 0.5 + 0.5;
#if UNITY_UV_STARTS_AT_TOP
    previousUv.y = 1.0 - previousUv.y;
#endif
    if (any(previousUv < 0.0) || any(previousUv > 1.0)) { return false; }

    colour = SAMPLE_TEXTURE2D_X_LOD(_BasisGISpecularPriorColor, sampler_LinearClamp, UnityStereoTransformScreenSpaceTex(previousUv), 0).rgb;
    return true;
}

/// <summary>
/// The plain march, carrying the crossing state the hierarchical walk carries - as a CROSSING test,
/// not the shared BasisGIMarch's band membership, which would read a hugged origin surface as a hit on
/// every texel. Armed from the start: a mirror ray is geometrically above the surface it just left, and
/// the false self-crossings a misread representative can still produce are caught by the caller's
/// same-plane veto and walked past by its continuation, where an unarmed gate would instead silently
/// eat real crossings on the rows the same misreads flip the other way.
/// </summary>
BasisGIHit BasisGISSRMarch(float4 startScreen, float3 originWS, float3 directionWS, float rayLength, float noise, out bool leftScreen)
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
    bool inFront = true;

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
        // The interval semantics of the fine segment, exactly - see BasisGIFineSegment for why the
        // ambiguous band between a texel's nearest and furthest is carried rather than guessed.
        float2 sceneDepth = BasisGISampleTracedDepth(uv);
        float thickness = BasisGIThicknessAt(sceneDepth.g);
        bool crossed = inFront && rayEye > sceneDepth.g;
        if (rayEye <= sceneDepth.r) { inFront = true; }
        else if (rayEye > sceneDepth.g + thickness) { inFront = false; }

        if (crossed && rayEye < sceneDepth.g + thickness)
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

float4 BasisGISSRTrace(float2 uv, float2 positionSS, out float hitDistance)
{
    // The distance the temporal filter reprojects this pixel's reflection by, see BasisGITemporal: the sky
    // sentinel means "at infinity", which reprojects as pure rotation and is the honest default for every
    // answer that is not a found surface - misses, fallbacks, and pixels with nothing to say.
    hitDistance = BASISGI_SKY_DEPTH;

    // Snapped to the CENTRE of the nearest full resolution texel before anything reads through it. At a
    // reduced trace resolution the traced pixel centre lands exactly on the corner BETWEEN four full
    // resolution depth texels - (i + 0.5) / traced is (2i + 1) / full, a texel edge - and a point sample
    // there is decided by fixed function sub-texel rounding on a knife edge. The surface the ray "leaves"
    // then belongs to an arbitrary one of four neighbours, the whole ray's depth parameterisation rides
    // on that choice, and the rounding flips in quasi-periodic blocks of the coordinate's float pattern:
    // evenly spaced lines across every reflective surface, immune to every change of march logic because
    // they are decided before the march begins, and absent at Full resolution where centres align. Found
    // by elimination: the lines survived the crossing gate, the coarse skips, the budget, the jitter and
    // the depth representation all being removed, and died the moment the origin stopped sitting on the
    // knife edge.
    uv = (floor(uv * _BasisGISourceTexelSize.zw) + 0.5) * _BasisGISourceTexelSize.xy;

    float rawDepth = BasisGISampleRawDepth(uv);
    if (BasisGIIsSky(rawDepth)) { return float4(0.0, 0.0, 0.0, 0.0); }

    // BASISGI_FADE_DISTANCE holds the SPECULAR fade distance here - the reflection pass fills the shared
    // constants with its own reach and fade, see SpecularPass.FillConstants.
    float eyeDepth = BasisGILinearEyeDepth(rawDepth);
    float fade = BasisGIDistanceFade(eyeDepth);
    if (fade <= 0.0) { return float4(0.0, 0.0, 0.0, 0.0); }

    float3 viewPosition = BasisGIViewPosition(uv, rawDepth);
    float3 worldPosition = BasisGIWorldPosition(uv, rawDepth);
    float3 normalWS = BasisGIReconstructNormal(uv, viewPosition, rawDepth);

    float3 viewDirection = normalize(worldPosition - GetCameraPositionWS());
    float3 direction = reflect(viewDirection, normalWS);
    // A reflection pointing into the surface means the reconstructed normal disagrees with the depth it
    // came from, which is what a silhouette pixel looks like. Same rule as the kernel: no data, keep the
    // probe.
    if (dot(direction, normalWS) <= 0.0) { return float4(0.0, 0.0, 0.0, 0.0); }

    // How much this ray aims back at the eye. Applied to the MISS answers only: a genuine hit is on
    // screen by definition and stands on its own evidence, but a miss on a camera-facing ray must not
    // claim the sky for content that is really the room behind the viewer.
    float facingFade = 1.0 - smoothstep(BASISGI_SSR_FACING_FADE_START, BASISGI_SSR_FACING_FADE_END,
        saturate(dot(direction, -viewDirection)));

    float normalBias = 0.01 + eyeDepth * 0.002;
    float3 originWS = worldPosition + normalWS * normalBias;
    // Frozen at phase zero, deliberately not the frame-walked noise the diffuse gather marches with. The
    // jitter exists to decorrelate neighbouring rays' step phase, and the spatial pattern alone does that;
    // animating it buys a stochastic estimator nothing here because the mirror ray is deterministic - the
    // only thing the animation added was a per-frame sparkle on every band edge that the reflection
    // accumulation's short tail cannot settle. A still camera now produces a bit-identical trace.
    float noise = BasisGIInterleavedGradientNoise(positionSS, 0.0);
    float4 startScreen = BasisGIWorldToScreen(originWS);

    // BASISGI_MAX_RAY_LENGTH is the SPECULAR ray length in this pass's constants: a reflection carries
    // much further than a bounce, and the mirror ray gets the reach the kernel's does.
    //
    // ARMED from the start, which is a reversal worth its history. The unarmed start existed so a
    // grazing ray hugging its own floor could not "cross" a misread representative texel and reflect the
    // floor in itself - but the same misreads flip the OTHER way row by row, and a ray that never gets
    // observed in front arrives at its real target with the gate still shut and misses it SILENTLY. That
    // silent miss alternated with the armed rows at exactly the traced grid's beat: evenly spaced lines.
    // The self-hit the gate guarded against is now caught by the same-plane veto below and RECOVERED by
    // the continuation - a loud, correctable failure instead of a quiet one - so the gate's remaining
    // contribution was only its misses.
    // How much of the ray the march actually tested before answering. A miss with less than the whole
    // ray observed is not evidence about what the unwalked stretch held, and every confidence it feeds
    // scales down by it - the blend-out that replaces a hard "reflections stop here" budget line.
    float observed = 1.0;

    float3 radiance = float3(0.0, 0.0, 0.0);
    float confidence = 0.0;
    bool answered = false;

    // The march, with up to TWO continuations. A crossing rejected below is a misreading of the
    // REFLECTOR or of a silhouette, not a reflection - and simply stopping there turned every rejected
    // pixel into a sky answer while its neighbour, whose ray happened not to meet the same texel step,
    // reached the wall behind. That alternation beats at the traced grid's period, which is exactly the
    // "evenly spaced lines across the reflective surface" it shipped as. Restarting from just past the
    // rejected surface reaches what the false hit was standing in front of; two silhouettes on one ray -
    // a box top, then the wall top behind it - is common enough to allow, a third rejection gives up and
    // takes the miss path honestly.
    float3 marchOrigin = originWS;
    float4 marchStart = startScreen;
    float covered = 0.0;

    UNITY_LOOP
    for (int attempt = 0; attempt < 3; attempt++)
    {
        float remaining = BASISGI_MAX_RAY_LENGTH - covered;
        if (remaining <= BASISGI_EPSILON) { break; }

        bool segmentLeftScreen = false;
        float segmentObserved = 1.0;
#if defined(_BASISGI_HIERARCHICAL_MARCH)
        BasisGIHit hit = BasisGIMarchHierarchicalCore(true, BASISGI_SSR_MAX_CELLS, marchStart, marchOrigin, direction, remaining, noise, segmentLeftScreen, segmentObserved);
#else
        BasisGIHit hit = BasisGISSRMarch(marchStart, marchOrigin, direction, remaining, noise, segmentLeftScreen);
#endif
        observed = (covered + segmentObserved * remaining) / BASISGI_MAX_RAY_LENGTH;

        if (!hit.valid || BASISGI_SSR_PRIOR_VALID <= 0.5) { break; }

        // The SURFACE at the hit uv, from the depth buffer, and deliberately not marchOrigin + direction *
        // hit.distance. The march parameterises the ray by its SCREEN progress, and hit.distance is that
        // fraction times the world length - a number that is only a world distance where the two
        // parameterisations agree, which under perspective they very much do not: a hit halfway across the
        // screen of a 64 metre ray leaving a nearby floor is a few metres away in the world, and the ray
        // point lands tens of metres past it, in the sky. Every reprojection then sampled sky, and the
        // whole backend read as "hits happen, reflections never arrive". The depth fetch is one tap per
        // pixel, once, for the point the march actually found.
        float hitRaw = BasisGISampleRawDepth(hit.uv);

        // Four rejections, each a false hit the march cannot rule out at a reduced trace resolution.
        // Their shared shape: at half resolution a silhouette rasterises as a staircase of whole texel
        // rows, and whether a ray meets one of its steps flips coherently row by row - which the mirror
        // geometry then magnifies into evenly spaced hit/miss bands across every reflective surface.
        // Every rejection CONTINUES the march past the misreading rather than giving up, because what the
        // false surface stood in front of is exactly what the reflection should show.
        //
        // PHANTOM: the refined uv landed on a sky texel - there is no surface here at all, only the far
        // side of a silhouette step.
        bool phantom = BasisGIIsSky(hitRaw);
        float3 hitPosition = float3(0.0, 0.0, 0.0);
        float hitRange = 0.0;
        bool backface = false;
        bool samePlane = false;
        float sliverConfidence = 1.0;
        if (!phantom)
        {
            hitPosition = BasisGIWorldPosition(hit.uv, hitRaw);
            hitRange = distance(hitPosition, originWS);
            float3 hitNormal = BasisGIReconstructNormal(hit.uv, BasisGIViewPosition(hit.uv, hitRaw), hitRaw);

            // BACKFACE: a reflection can only land on a surface that faces the ray. A crossing declared
            // where the surface faces WITH the ray is the representative depth misreading a skim.
            backface = dot(hitNormal, direction) > 0.08;
            // SAME PLANE: a flat mirror cannot reflect its own plane - geometrically impossible, not
            // merely unlikely. The normal agreement test keeps a wall meeting this floor - coplanar at
            // the corner but perpendicular in normal - out of the rejection.
            samePlane = abs(dot(hitPosition - worldPosition, normalWS)) < 0.02 + hitRange * 0.01
                && dot(hitNormal, normalWS) > 0.9;

            // SLIVER, graded rather than vetoed: how far the found surface extends past the hit along
            // the march, probed one and two traced texels on. A broad face keeps full confidence; the
            // one-or-two texel top edge of something - which at this resolution the depth buffer cannot
            // decide row by row whether a ray clears - blends toward the fallback instead. The rows it
            // would otherwise draw are the dashes the alias prints when a thin reflection is asserted at
            // full strength on some rows and not at all on their neighbours.
            float2 towardExit = hit.uv - marchStart.xy;
            float towardLength = length(towardExit);
            if (towardLength > 1e-5)
            {
                float2 stepUv = towardExit / towardLength * _BasisGITracedTexelSize.xy;
                float hitEye = BasisGILinearEyeDepth(hitRaw);
                float band = 4.0 * BasisGIThicknessAt(hitEye);
                float extentOne = BasisGISampleTracedDepth(hit.uv + stepUv).r - hitEye < band ? 1.0 : 0.0;
                float extentTwo = BasisGISampleTracedDepth(hit.uv + stepUv * 2.0).r - hitEye < band ? 1.0 : 0.0;
                sliverConfidence = (1.0 + extentOne + extentTwo) / 3.0;
            }
        }

        if (phantom || backface || samePlane)
        {
            // Resume just past the misreading. The restart distance comes from the march's own screen
            // fraction, converted to a world distance perspective-correctly - the same s = t*w0 /
            // ((1-t)*w1 + t*w0) relation whose neglect once broke the reprojection. The euclidean range
            // of the found SURFACE is the wrong number here: a rejected skim's surface point lies far
            // along the floor, and restarting at its projection teleports the march past the very wall
            // the reflection needed - which was a second family of evenly spaced dropout rows.
            float4 segmentEnd = BasisGIWorldToScreen(marchOrigin + direction * remaining);
            float tScreen = saturate(hit.distance / max(remaining, BASISGI_EPSILON));
            float w0 = marchStart.w;
            float w1 = max(segmentEnd.w, BASISGI_EPSILON);
            float along = remaining * tScreen * w0 / max(BASISGI_EPSILON, (1.0 - tScreen) * w1 + tScreen * w0);
            // A rejected SELF crossing needs more than a token step: the restart must put the ray far
            // enough along to have CLIMBED clear of its own plane's misread band, or the next attempt
            // rejects again a few centimetres on and the ray dies within arm's reach. The climb rate is
            // the ray's elevation off the plane, and the height to clear is the same-plane epsilon.
            float advance = samePlane
                ? (0.03 + 0.02 * (covered + along)) / max(0.05, dot(direction, normalWS))
                : max(0.05, (covered + along) * 0.01);
            covered += along + advance;
            marchOrigin = originWS + direction * covered;
            marchStart = BasisGIWorldToScreen(marchOrigin);
            if (marchStart.w <= BASISGI_EPSILON) { break; }
            continue;
        }

        if (BasisGISSRSamplePrior(hitPosition, radiance))
        {
            confidence = BasisGISSREdgeFade(hit.uv) * sliverConfidence;
            hitDistance = hitRange;
            answered = true;
        }
        break;
    }

    // A ray that found no surface may still be looking at sky the frame actually drew. The environment
    // cubemap the kernel falls back to is the baked reflection environment, which a world is free to have
    // never baked - black - while its skybox renders fine; the captured colour holds that skybox as
    // rendered. So before settling for the cubemap, project the direction to its vanishing point: if the
    // screen shows sky there, the reflection should show the same sky. Deliberately independent of the
    // fallback setting - this is a real answer read off the screen, not a stand-in.
    if (!answered && BASISGI_SSR_PRIOR_VALID > 0.5)
    {
        float3 skyPoint = originWS + direction * BASISGI_SSR_SKY_RANGE;
        float4 skyScreen = BasisGIWorldToScreen(skyPoint);
        if (skyScreen.w > BASISGI_EPSILON && all(skyScreen.xy >= 0.0) && all(skyScreen.xy <= 1.0)
            && BasisGIIsSky(BasisGISampleRawDepth(skyScreen.xy))
            && BasisGISSRSamplePrior(skyPoint, radiance))
        {
            confidence = BasisGISSREdgeFade(skyScreen.xy) * facingFade * observed;
            answered = true;
        }
    }

    // Three ways to arrive here: the ray missed everything on screen, it left the screen entirely, or it
    // hit something the previous frame never saw. Under the Sky fallback the bound environment answers
    // for all three; under everything else this is NO DATA, and the lit shader keeps the reflection
    // probes it already sampled for this surface - see the fallback note at the top of this file.
    if (!answered)
    {
#if defined(_BASISGI_FALLBACK_SKY)
        radiance = BasisGIFallbackRadiance(direction);
        confidence = (_BasisGISky.y > 0.0 ? 1.0 : 0.0) * facingFade * observed;
#else
        radiance = float3(0.0, 0.0, 0.0);
        confidence = 0.0;
#endif
    }

    radiance = BasisGIClampFirefly(radiance) * BASISGI_SSR_INTENSITY;
    return float4(radiance * fade, confidence * fade);
}

#endif
