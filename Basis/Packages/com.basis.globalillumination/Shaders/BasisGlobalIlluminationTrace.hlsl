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
        BasisGIHit hit = BasisGIMarch(originWS, direction, BASISGI_MAX_RAY_LENGTH, noise, leftScreen);

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
