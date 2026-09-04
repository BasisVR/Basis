#ifndef BASIS_GLOBAL_ILLUMINATION_RT_KERNEL_INCLUDED
#define BASIS_GLOBAL_ILLUMINATION_RT_KERNEL_INCLUDED

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
#include "Packages/com.unity.render-pipelines.core/Runtime/UnifiedRayTracing/Common.hlsl"
#include "Packages/com.unity.render-pipelines.core/Runtime/UnifiedRayTracing/TraceRayAndQueryHit.hlsl"
#include "Packages/com.basis.globalillumination/Shaders/BasisGlobalIlluminationRTCommon.hlsl"

UNIFIED_RT_DECLARE_ACCEL_STRUCT(_BasisGIRtAccel);

Texture2DArray<float4> _BasisGIRtPositionTex;
Texture2DArray<float4> _BasisGIRtNormalTex;
RWTexture2DArray<float4> _BasisGIRtResultTex;
RWTexture2DArray<float4> _BasisGIRtSpecularTex;

StructuredBuffer<BasisGIRtInstance> _BasisGIRtInstances;
StructuredBuffer<BasisGIRtLight> _BasisGIRtLights;
StructuredBuffer<uint> _BasisGIRtIndices;
StructuredBuffer<uint> _BasisGIRtNormals;

TextureCube<float4> _BasisGIRtSkyCube;
SamplerState sampler_BasisGIRtSkyCube;

float4 _BasisGIRtReference;
float4 _BasisGIRtSize;
float4 _BasisGIRtTrace;
float4 _BasisGIRtBias;
float4 _BasisGIRtOptions;
float4 _BasisGIRtSky;
float4 _BasisGIRtSkyDecode;
float4 _BasisGIRtSpecular;
int _BasisGIRtRayCount;
// Which halves of the structure this trace may hit. The structure can be shared with ambient
// occlusion, which wants its own answer, so the ray narrows it rather than the contents.
int _BasisGIRtTraceMask;
int _BasisGIRtBounces;
int _BasisGIRtLightCount;
int _BasisGIRtLightSamples;
int _BasisGIRtViewCount;
int _BasisGIRtFrameIndex;
// Which of the two gathers this dispatch is being asked for. They share the prepass, the acceleration
// structure, the light list and the sky, so running both costs one dispatch rather than two - but either
// can be off, because ray traced reflections are usable with screen space diffuse and vice versa.
int _BasisGIRtDiffuseEnabled;
int _BasisGIRtSpecularEnabled;

#define BASISGI_RT_RAY_LENGTH        _BasisGIRtTrace.x
#define BASISGI_RT_OBSCURANCE_RADIUS _BasisGIRtTrace.y
#define BASISGI_RT_OBSCURANCE        _BasisGIRtTrace.z
#define BASISGI_RT_FADE_DISTANCE     _BasisGIRtTrace.w
#define BASISGI_RT_NORMAL_BIAS       _BasisGIRtBias.x
#define BASISGI_RT_DISTANCE_BIAS     _BasisGIRtBias.y
#define BASISGI_RT_EMISSION          _BasisGIRtBias.z
#define BASISGI_RT_LIGHT_INTENSITY   _BasisGIRtBias.w
#define BASISGI_RT_FIREFLY_CLAMP     _BasisGIRtOptions.x
#define BASISGI_RT_BOUNCE_THRESHOLD  _BasisGIRtOptions.y
#define BASISGI_RT_SHADOW_RAYS       _BasisGIRtOptions.z
#define BASISGI_RT_SPEC_RAY_LENGTH   _BasisGIRtSpecular.x
#define BASISGI_RT_SPEC_INTENSITY    _BasisGIRtSpecular.y
#define BASISGI_RT_SPEC_FADE         _BasisGIRtSpecular.z
#define BASISGI_RT_SPEC_BOUNCES      _BasisGIRtSpecular.w

float3 BasisGIRtDecodeHDR(float4 encoded, float4 decode)
{
    float alpha = max(decode.w * (encoded.a - 1.0) + 1.0, 0.0);
    return (decode.x * PositivePow(alpha, decode.y)) * encoded.rgb;
}

float3 BasisGIRtSampleSky(float3 direction)
{
    if (_BasisGIRtSky.y <= 0.0) { return float3(0.0, 0.0, 0.0); }
    float4 encoded = _BasisGIRtSkyCube.SampleLevel(sampler_BasisGIRtSkyCube, direction, _BasisGIRtSky.x);
    return max(0.0, BasisGIRtDecodeHDR(encoded, _BasisGIRtSkyDecode)) * _BasisGIRtSky.y;
}

/// The shading normal at a hit. Meshes that could not be read back carry no normals, and those fall back to
/// a view facing normal so they still bounce and still occlude instead of dropping out of the trace.
float3 BasisGIRtHitNormal(BasisGIRtInstance instance, UnifiedRT::Hit hit, float3 direction)
{
    float3 normal = -direction;

    UNITY_BRANCH
    if ((instance.geometry.z & BASISGI_RT_FLAG_HAS_NORMALS) != 0u)
    {
        uint triangleStart = hit.primitiveIndex * 3u;
        if (triangleStart + 3u <= instance.geometry.w)
        {
            uint indexBase = instance.geometry.x + triangleStart;
            uint vertexBase = instance.geometry.y;
            float3 n0 = BasisGIRtUnpackNormal(_BasisGIRtNormals[_BasisGIRtIndices[indexBase] + vertexBase]);
            float3 n1 = BasisGIRtUnpackNormal(_BasisGIRtNormals[_BasisGIRtIndices[indexBase + 1u] + vertexBase]);
            float3 n2 = BasisGIRtUnpackNormal(_BasisGIRtNormals[_BasisGIRtIndices[indexBase + 2u] + vertexBase]);

            float2 barycentrics = hit.uvBarycentrics;
            float3 objectNormal = n0 * (1.0 - barycentrics.x - barycentrics.y) + n1 * barycentrics.x + n2 * barycentrics.y;
            normal = BasisGIRtInstanceNormal(instance, objectNormal);
        }
    }

    return dot(normal, direction) > 0.0 ? -normal : normal;
}

/// How many capsule walls one ray may step out of before it gives up and takes the hit. Four covers the
/// worst real overlap - hips, spine, chest and an arm crossing the body - without letting a degenerate
/// case spin.
#define BASISGI_RT_MAX_PROXY_ESCAPES 4u
/// Far enough past the wall not to land back on it through floating point, small enough not to skip
/// anything real standing immediately behind it.
#define BASISGI_RT_PROXY_ESCAPE_EPSILON 0.01
/// How far from its origin a ray still counts a capsule as ITS OWN body rather than somebody else's.
///
/// The back face test below answers "did I start inside this", which is only the same question as "is this
/// me" when the capsule encloses the surface. It does not: the fit is deliberately INSCRIBED, so a thigh
/// capsule is about eight centimetres across inside a fourteen centimetre thigh and the rendered skin sits
/// OUTSIDE its own proxy. A ray leaving that skin at a grazing angle re-enters its own capsule FRONT face,
/// which reads as another body standing in the way - dark bands down the legs of every avatar, worst around
/// the hips, where each thigh capsule is half buried in the pelvis one and the skin is outside them both.
///
/// Resizing cannot settle it. Inscribed leaves the surface outside and self-hitting; circumscribed would put
/// it inside where the back face rule works, but a skirt or a coat then inflates a limb until it swallows the
/// other leg. Shape cannot express the difference between my leg and yours, so distance stands in for it: a
/// capsule axis is one limb thickness from the skin it belongs to and much further from anyone else's.
///
/// Twenty centimetres because the self hit window is short. A ray angled shallowly off the skin MISSES its
/// own capsule - it is a thin rod, not a shell - so the strike only happens on rays leaving steeply, and the
/// range works out at three to four centimetres for a thigh, two to three for a shin and seven to ten for a
/// torso, which is the fat case. Twice the worst of those leaves room for avatars scaled above human size.
///
/// The cost is person-to-person contact shading closer than this: two avatars touching stop occluding each
/// other THROUGH THEIR PROXIES, while the room still shadows them both normally. That is the one number to
/// move if bodies in contact start looking detached.
#define BASISGI_RT_PROXY_SELF_REACH 0.2

/// <summary>
/// One ray, stepping out of any proxy capsule it began inside of.
///
/// Avatars are traced as capsules on their bones, but the depth buffer - and so the point every ray starts
/// from - is the avatar's REAL surface. The spine bone sits towards the back of a torso, so a capsule of
/// 0.115 x body height swallows the chest: a ray leaving the visible chest starts inside that capsule and
/// hits it at almost zero distance, which reads as "this surface is completely enclosed". What you see is a
/// hard edged dark patch in the shape of the capsule, and no ray start offset small enough to keep contact
/// darkening honest is large enough to escape it - it takes about a third of a metre, which would lift the
/// darkening off every corner in the world.
///
/// A ray that starts inside a closed shape leaves it through a BACK face, so that is the entire test. No
/// distance to tune, and world geometry is left alone because only proxies are checked - a back face on a
/// leaf card or a double sided wall is still real occlusion and is still counted.
///
/// EVERY trace against this structure has to come through here. It is declared above the first of them
/// rather than next to the gather that happens to be the largest, because the one trace that skipped it -
/// the light visibility ray, which was an any-hit and so could not tell which instance had stopped it -
/// painted exactly the patch described above onto every lit avatar.
/// </summary>
UnifiedRT::Hit BasisGIRtTraceEscapingProxies(UnifiedRT::DispatchInfo dispatchInfo, UnifiedRT::RayTracingAccelStruct accelStruct,
    inout float3 origin, float3 direction, float tMax, uint mask)
{
    UnifiedRT::Hit hit = UnifiedRT::Hit::Invalid();

    // Distance from where the ray really started, which is what the self test needs - hitDistance alone is
    // measured from the last wall stepped past and would let a chain of escapes creep arbitrarily far.
    float travelled = 0.0;

    UNITY_LOOP
    for (uint attempt = 0u; attempt < BASISGI_RT_MAX_PROXY_ESCAPES; attempt++)
    {
        UnifiedRT::Ray ray;
        ray.origin = origin;
        ray.tMin = 0.0;
        ray.direction = direction;
        ray.tMax = tMax;

        hit = UnifiedRT::TraceRayClosestHit(dispatchInfo, accelStruct, mask, ray, 0);
        if (!hit.IsValid()) { return hit; }

        // Mine on either of two counts: I began inside it (back face), or it is near enough to have been the
        // capsule under my own skin (front face within reach). Anything else is a body in the way.
        //
        // Tested before the instance is looked up, because a front face further off than the self reach
        // occludes whatever it is made of and the proxy flag cannot change that answer. Written this way for
        // the reader rather than for the compiler: dxc sinks the buffer load past the early out on its own,
        // and the compute backend's DXIL is byte identical either way. It costs nothing and does not depend
        // on the optimizer noticing.
        if (hit.isFrontFace && (travelled + hit.hitDistance) >= BASISGI_RT_PROXY_SELF_REACH) { return hit; }

        BasisGIRtInstance instance = _BasisGIRtInstances[hit.instanceID];
        if ((instance.geometry.z & BASISGI_RT_FLAG_PROXY) == 0u) { return hit; }

        // Step just past the wall and try again. tMax comes down with the origin so the ray can never
        // reach further in total than it was allowed to.
        float advance = hit.hitDistance + BASISGI_RT_PROXY_ESCAPE_EPSILON;
        origin += direction * advance;
        travelled += advance;
        tMax -= advance;
        if (tMax <= 0.0) { return UnifiedRT::Hit::Invalid(); }
    }

    return hit;
}

/// <summary>
/// What the real lights put on a hit, estimated by resampled importance sampling - the idea ReSTIR and
/// RTXDI are built on.
///
/// Weighing a light is arithmetic; shadow-raying one is not. Shadow-raying every light at every hit of
/// every bounce is what forced the light budget down to a dozen, and a budget that small is itself a
/// source of flicker: a light drops out of it as the player walks and takes all of its contribution with
/// it. So every light is weighed by what it would contribute unshadowed, a few are drawn in proportion to
/// those weights, and only those pay for a ray. Each survivor is scaled by how likely it was to be drawn,
/// which leaves the estimate unbiased - the expected value is still the sum over every light - and makes
/// the cost of a room with sixty lights the cost of a room with one.
///
/// Multiple light samples are multiple INDEPENDENT reservoirs, not multiple passes over the list. A
/// light's weigh - toLight, attenuation, distance, contribution, radiance - depends on the hit and the
/// light, never on which reservoir is drawing, so it is computed once per light and fed to every reservoir
/// the same way; only the accept/reject draw differs per reservoir, off its own decorrelated seed stream.
/// weightSum is exactly the same running total for every reservoir for the same reason - it sums the same
/// per-light weights - so it is one shared scalar, not one per reservoir. Reservoir state is sized by
/// BASISGI_RT_MAX_LIGHT_SAMPLES (4), never by the light count (64): the array holds one slot per sample,
/// not one per light, which is what keeps this cheap instead of trading the redundant weighing for a
/// register-pressure blowup.
/// </summary>
float3 BasisGIRtDirectLighting(UnifiedRT::DispatchInfo dispatchInfo, UnifiedRT::RayTracingAccelStruct accelStruct,
    float3 positionWS, float3 normalWS, inout uint seed)
{
    int count = min(_BasisGIRtLightCount, BASISGI_RT_MAX_LIGHTS);
    if (count <= 0) { return float3(0.0, 0.0, 0.0); }

    int samples = clamp(_BasisGIRtLightSamples, 1, BASISGI_RT_MAX_LIGHT_SAMPLES);

    float chosenWeight[BASISGI_RT_MAX_LIGHT_SAMPLES];
    float3 chosenRadiance[BASISGI_RT_MAX_LIGHT_SAMPLES];
    float3 chosenDirection[BASISGI_RT_MAX_LIGHT_SAMPLES];
    float chosenDistance[BASISGI_RT_MAX_LIGHT_SAMPLES];
    float chosenShadow[BASISGI_RT_MAX_LIGHT_SAMPLES];
    uint reservoirSeed[BASISGI_RT_MAX_LIGHT_SAMPLES];

    // These six arrays are indexed dynamically, which makes them indexable temps rather than registers.
    // Unrolling the three loops over BASISGI_RT_MAX_LIGHT_SAMPLES to make every index a constant DOES
    // clear that on the DXIL backends - all twenty allocas go - but it is a 2.1x instruction-count
    // regression through fxc (141 -> 296 slots on an isolated probe, at both /O1 and /O3), fxc keeps the
    // six indexable temps anyway, and its optimiser gives up with "did not converge". d3d11 compiles this
    // kernel's compute fallback through fxc, so the unrolled form is not worth having. Measured
    // 2026-09-03; do not re-land it without ISA level numbers from both backends.
    UNITY_LOOP
    for (int reservoirIndex = 0; reservoirIndex < samples; reservoirIndex++)
    {
        chosenWeight[reservoirIndex] = 0.0;
        chosenRadiance[reservoirIndex] = float3(0.0, 0.0, 0.0);
        chosenDirection[reservoirIndex] = float3(0.0, 1.0, 0.0);
        chosenDistance[reservoirIndex] = 0.0;
        chosenShadow[reservoirIndex] = 0.0;
        // Each reservoir's own stream, decorrelated from the others by starting at a different point in
        // the hash space. Reusing one draw across every reservoir would make them all accept or reject the
        // same light at the same time - every reservoir converging on the same choice - which collapses
        // the noise reduction lightSamples is supposed to buy back down to one sample's worth of variance.
        reservoirSeed[reservoirIndex] = BasisGIRtHash(seed + (uint)reservoirIndex * 0x85ebca6bu);
    }

    float weightSum = 0.0;

    UNITY_LOOP
    for (int index = 0; index < count; index++)
    {
        BasisGIRtLight light = _BasisGIRtLights[index];

        float3 toLight;
        float attenuation, distanceToLight, cosine;

        if (light.direction.w < 0.5)
        {
            // Already unit length: BasisGlobalIlluminationRayLights normalises the forward vector on the
            // way into the buffer, so the direction a light is pointing is normalised once per light per
            // frame on the CPU rather than once per light per hit here.
            toLight = -light.direction.xyz;
            cosine = dot(normalWS, toLight);
            if (cosine <= 0.0) { continue; }
            attenuation = 1.0;
            distanceToLight = BASISGI_RT_RAY_LENGTH * 4.0;
        }
        else
        {
            float3 delta = light.position.xyz - positionWS;
            // The hemisphere test decided before any of the falloff is paid for. toLight is delta scaled
            // by a positive reciprocal, so this dot product already carries the sign of the cosine the
            // contribution below tests, and a light behind the surface contributes exactly nothing to it
            // however bright or close it is. In a room, roughly half the list is behind any given hit.
            float facing = dot(normalWS, delta);
            if (facing <= 0.0) { continue; }

            float radius = max(light.spot.w, 0.0);
            float distanceSquared = max(dot(delta, delta), max(1e-4, radius * radius));
            // Range rejected on the square. sqrt is monotonic over non-negatives, so this is the test the
            // sqrt form ran, minus the sqrt on every light the test throws away. The range is clamped
            // because squaring loses the sign a negative one would have carried into the comparison.
            float range = max(light.position.w, 0.0);
            if (distanceSquared > range * range) { continue; }

            distanceToLight = sqrt(distanceSquared);
            float rcpDistance = rcp(max(distanceToLight, 1e-4));
            toLight = delta * rcpDistance;
            cosine = facing * rcpDistance;
            attenuation = BasisGIRtDistanceAttenuation(distanceSquared, light.spot.z);
            if (light.direction.w > 1.5) { attenuation *= BasisGIRtSpotAttenuation(light, toLight); }
        }

        float contribution = saturate(cosine) * attenuation;
        if (contribution <= 1e-4) { continue; }

        float3 radiance = light.color.rgb * contribution;
        float weight = max(radiance.r, max(radiance.g, radiance.b));
        if (weight <= 0.0) { continue; }

        // A reservoir: each light replaces the one held with probability equal to its share of the
        // weight seen so far, which leaves the holder drawn exactly in proportion to its own weight
        // after one pass and needs no second look at the list. Run once per reservoir here instead of
        // once per sample-pass over the whole light list - the weigh above already happened once for
        // every reservoir sharing it.
        weightSum += weight;

        UNITY_LOOP
        for (int sampleIndex = 0; sampleIndex < samples; sampleIndex++)
        {
            reservoirSeed[sampleIndex] = BasisGIRtHash(reservoirSeed[sampleIndex] + 0x9e3779b9u);
            if (BasisGIRtUnitFloat(reservoirSeed[sampleIndex]) * weightSum <= weight)
            {
                chosenWeight[sampleIndex] = weight;
                chosenRadiance[sampleIndex] = radiance;
                chosenDirection[sampleIndex] = toLight;
                chosenDistance[sampleIndex] = distanceToLight;
                chosenShadow[sampleIndex] = light.color.w;
            }
        }
    }

    float3 total = float3(0.0, 0.0, 0.0);

    UNITY_LOOP
    for (int resultIndex = 0; resultIndex < samples; resultIndex++)
    {
        // Read once into locals rather than re-indexing the arrays at each of the five uses below: the
        // slot cannot change inside the iteration, and every one of those indexed reads is an indexable
        // temp load.
        float slotWeight = chosenWeight[resultIndex];
        if (slotWeight <= 0.0) { continue; }
        float slotDistance = chosenDistance[resultIndex], slotShadow = chosenShadow[resultIndex];
        float3 slotRadiance = chosenRadiance[resultIndex], slotDirection = chosenDirection[resultIndex];

        float visibility = 1.0;
        if (BASISGI_RT_SHADOW_RAYS > 0.5 && slotShadow > 0.5)
        {
            // A point on an avatar's visible chest sits INSIDE that avatar's own torso capsule, so a ray
            // towards a light leaves through the capsule wall and stops there - every lit avatar wearing the
            // shape of its own proxy as a hard edged black patch. Stepping out of that is what the escape
            // below is for.
            //
            // But the room is not a capsule, and the room is nearly every shadow ray. So the two are traced
            // separately: real geometry keeps the cheap ANY-HIT it always had, and only the handful of
            // proxy capsules pay for a closest-hit walk - and only when the room did not already stop the
            // ray. Routing every shadow ray through the walk, as this briefly did, multiplies the cost of
            // the most numerous trace in the frame by up to four, and reflections feel it most because they
            // shade a hit this way at every bounce.
            //
            // Inside the proxy-only trace every hit IS a capsule, so a back face there can only mean the ray
            // started inside a body. A capsule met FRONT face on is somebody else in the way and shadows.
            float3 shadowOrigin = OffsetRayOrigin(positionWS, normalWS, BASISGI_RT_NORMAL_BIAS);
            float shadowReach = max(0.0, slotDistance - BASISGI_RT_NORMAL_BIAS * 2.0);

            uint shadowMask = (uint)_BasisGIRtTraceMask;
            uint solidMask = shadowMask & ~BASISGI_RT_CATEGORY_AVATAR_PROXY;
            uint proxyMask = shadowMask & BASISGI_RT_CATEGORY_AVATAR_PROXY;

            UnifiedRT::Ray shadowRay;
            shadowRay.origin = shadowOrigin;
            shadowRay.tMin = 0.0;
            shadowRay.direction = slotDirection;
            shadowRay.tMax = shadowReach;

            bool blocked = solidMask != 0u
                && UnifiedRT::TraceRayAnyHit(dispatchInfo, accelStruct, solidMask, shadowRay, 0);
            if (!blocked && proxyMask != 0u)
            {
                float3 walk = shadowOrigin;
                blocked = BasisGIRtTraceEscapingProxies(dispatchInfo, accelStruct, walk, slotDirection, shadowReach, proxyMask).IsValid();
            }
            visibility = blocked ? 0.0 : 1.0;
        }

        total += slotRadiance * ((weightSum / slotWeight) * visibility);
    }

    return total * (BASISGI_RT_LIGHT_INTENSITY / (float)samples);
}

/// <summary>
/// The mirror reflection at a surface: one deterministic ray along the reflection vector, shaded at the hit
/// with the same lights and emissive materials the diffuse gather uses.
///
/// There is no cosine lobe to sample here, so unlike the diffuse gather this is not a Monte Carlo estimate -
/// the direction is exact, and the only noise left is whatever the hit's own light resampling introduces.
/// That is what makes a reflection usable at one ray per pixel where diffuse needs several.
///
/// It cannot know the roughness of the surface it starts from. There is no GBuffer here, by design, because
/// avatar shaders do not write one - so it always traces the mirror direction and lets the lit shader decide
/// how much of that to keep from the roughness it does know. See BasisSampleTracedReflection in the forked
/// URP's GlobalIllumination.hlsl.
/// </summary>
float4 BasisGIRtTraceSpecular(UnifiedRT::DispatchInfo dispatchInfo, UnifiedRT::RayTracingAccelStruct accelStruct,
    float3 positionWS, float3 normalWS, float originBias, float fade, uint seed)
{
    float3 viewDirection = normalize(positionWS - _BasisGIRtReference.xyz);
    float3 direction = reflect(viewDirection, normalWS);

    // A reflection pointing into the surface means the reconstructed normal disagrees with the depth it was
    // reconstructed from, which is what a silhouette pixel looks like. Nothing behind the surface is worth
    // tracing, so it reports no data and the shader keeps the reflection probe it already had.
    if (dot(direction, normalWS) <= 0.0) { return float4(0.0, 0.0, 0.0, 0.0); }

    float3 origin = OffsetRayOrigin(positionWS, normalWS, originBias);
    float3 throughput = float3(1.0, 1.0, 1.0);
    float3 radiance = float3(0.0, 0.0, 0.0);
    float confidence = 0.0;
    uint bounces = (uint)clamp((int)BASISGI_RT_SPEC_BOUNCES, 1, 4);

    UNITY_LOOP
    for (uint bounce = 0; bounce < bounces; bounce++)
    {
        // The mirror ray gets its own reach, because a reflection carries much further than a bounce does:
        // the far wall of a room is a bounce nobody can see and a reflection everybody can.
        float reach = bounce == 0 ? BASISGI_RT_SPEC_RAY_LENGTH : BASISGI_RT_RAY_LENGTH;

        // A reflection off a body starts inside that body's own capsule exactly as the diffuse gather does,
        // and a mirror ray that hits it returns the inside of the avatar rather than the room.
        UnifiedRT::Hit hit = BasisGIRtTraceEscapingProxies(dispatchInfo, accelStruct, origin, direction, reach, (uint)_BasisGIRtTraceMask);
        if (!hit.IsValid())
        {
            // A miss reads the sky as LIGHTING either way, but whether a primary miss may CLAIM it with
            // confidence is the fallback setting's call, carried in _BasisGIRtSky.z: the Sky fallback
            // asks for exactly that, and under Reflection Probe the pixel reports no data instead - the
            // lit shader is already holding this surface's own probes, local and box projected, which
            // this pass cannot see and a global environment claim should not override. The same rule the
            // screen space backend answers with, so a miss is worth the same thing either side of a mode
            // switch.
            radiance += throughput * BasisGIRtSampleSky(direction);
            if (bounce == 0) { confidence = (_BasisGIRtSky.y > 0.0 && _BasisGIRtSky.z > 0.5) ? 1.0 : 0.0; }
            break;
        }

        if (bounce == 0) { confidence = 1.0; }

        BasisGIRtInstance instance = _BasisGIRtInstances[hit.instanceID];
        float3 hitPosition = origin + direction * hit.hitDistance;
        float3 hitNormal = BasisGIRtHitNormal(instance, hit, direction);

        float3 contribution = instance.emission.rgb * BASISGI_RT_EMISSION;
        contribution += instance.albedo.rgb * BasisGIRtDirectLighting(dispatchInfo, accelStruct, hitPosition, hitNormal, seed);
        if (bounce > 0) { contribution = BasisGIRtClampFirefly(contribution, BASISGI_RT_FIREFLY_CLAMP); }
        radiance += throughput * contribution;

        throughput *= instance.albedo.rgb;
        if (max(throughput.r, max(throughput.g, throughput.b)) < BASISGI_RT_BOUNCE_THRESHOLD) { break; }

        // Past the mirror hit there is no roughness to sample a second lobe from - the instance buffer
        // carries albedo and emission and nothing else - so the continuation is diffuse. That is what keeps
        // the reflection of an unlit corner from being black rather than making it a second mirror.
        seed = BasisGIRtHash(seed + bounce * 2654435761u);
        float2 next = float2(BasisGIRtUnitFloat(seed), BasisGIRtUnitFloat(BasisGIRtHash(seed ^ 0x85ebca6bu)));
        direction = BasisGIRtCosineHemisphere(next, hitNormal);
        origin = OffsetRayOrigin(hitPosition, hitNormal, originBias);
    }

    radiance = BasisGIRtClampFirefly(radiance, BASISGI_RT_FIREFLY_CLAMP) * BASISGI_RT_SPEC_INTENSITY;

    // Confidence fades with the same distance the radiance does, so a surface leaving the traced range hands
    // itself back to the reflection probe over a few metres instead of switching in one frame.
    return float4(radiance * fade, confidence * fade);
}

void RayGenExecute(UnifiedRT::DispatchInfo dispatchInfo)
{
    uint3 id = dispatchInfo.dispatchThreadID;
    if (id.x >= (uint)_BasisGIRtSize.x || id.y >= (uint)_BasisGIRtSize.y || id.z >= (uint)_BasisGIRtViewCount)
    {
        return;
    }

    // The two gathers share this preamble, the acceleration structure, the light list and the sky, which is
    // why they are one dispatch and not two. Either can be the only one running: ray traced reflections are
    // worth having over screen space diffuse, and ray traced diffuse is worth having without reflections.
    bool wantsDiffuse = _BasisGIRtDiffuseEnabled != 0;
    bool wantsSpecular = _BasisGIRtSpecularEnabled != 0;

    float4 packed = _BasisGIRtPositionTex.Load(int4(id.xy, id.z, 0));
    float viewDistance = length(packed.xyz);
    float fade = saturate(1.0 - viewDistance / max(1.0, BASISGI_RT_FADE_DISTANCE));
    float specularFade = saturate(1.0 - viewDistance / max(1.0, BASISGI_RT_SPEC_FADE));

    if (packed.w < 0.5)
    {
        if (wantsDiffuse) { _BasisGIRtResultTex[id] = float4(0.0, 0.0, 0.0, 1.0); }
        if (wantsSpecular) { _BasisGIRtSpecularTex[id] = float4(0.0, 0.0, 0.0, 0.0); }
        return;
    }

    float3 positionWS = packed.xyz + _BasisGIRtReference.xyz;
    float3 normalWS = BasisGIRtDecodeNormal(_BasisGIRtNormalTex.Load(int4(id.xy, id.z, 0)).xy);

    uint seed = BasisGIRtHash(id.x * 1973u + id.y * 9277u + id.z * 26699u + (uint)_BasisGIRtFrameIndex * 6151u);
    float originBias = BASISGI_RT_NORMAL_BIAS + BASISGI_RT_DISTANCE_BIAS * viewDistance;

    UNITY_BRANCH
    if (wantsSpecular)
    {
        UnifiedRT::RayTracingAccelStruct specularAccel = UNIFIED_RT_GET_ACCEL_STRUCT(_BasisGIRtAccel);
        _BasisGIRtSpecularTex[id] = specularFade > 0.0
            ? BasisGIRtTraceSpecular(dispatchInfo, specularAccel, positionWS, normalWS, originBias, specularFade, BasisGIRtHash(seed ^ 0x1b873593u))
            : float4(0.0, 0.0, 0.0, 0.0);
    }

    if (!wantsDiffuse) { return; }

    if (fade <= 0.0)
    {
        _BasisGIRtResultTex[id] = float4(0.0, 0.0, 0.0, 1.0);
        return;
    }

    // The per-pixel rotation of the ray set, walked by the R2 low-discrepancy sequence each frame so it is
    // temporally even for the accumulation.
    //
    // Its two axes have to be independent of each other. Deriving the second from the first - as
    // multiplying the gradient by the golden ratio does - puts every pixel's offsets on a single line of
    // the unit square, so the pixel draws its rays from a one parameter family, and so does each of the
    // neighbours the filter would average it with. Interleaved gradient noise cannot supply the second
    // either, because every value it can produce is the same one dot product.
    //
    // The second axis is an R2 lattice: a different linear form of the pixel, so it does not collapse onto
    // the gradient, and still low discrepancy across the screen. Substituting the pixel's hash here is the
    // obvious move and is measurably worse - white noise is independent per pixel but no longer spread
    // evenly between neighbours, and even spread is precisely what the spatial filter needs in order to
    // cancel it.
    float gradient = BasisGIRtInterleavedGradientNoise(float2(id.xy), float(_BasisGIRtFrameIndex));
    float lattice = frac(dot(float2(id.xy), float2(0.7548776662, 0.5698402909)));
    float2 jitter = frac(float2(gradient, lattice) + float(_BasisGIRtFrameIndex) * float2(0.7548776662, 0.5698402909));

    UnifiedRT::RayTracingAccelStruct accelStruct = UNIFIED_RT_GET_ACCEL_STRUCT(_BasisGIRtAccel);
    uint rayCount = (uint)max(1, _BasisGIRtRayCount);
    uint bounces = (uint)clamp(_BasisGIRtBounces, 1, 4);

    float3 gathered = float3(0.0, 0.0, 0.0);
    float occlusion = 0.0;

    UNITY_LOOP
    for (uint rayIndex = 0; rayIndex < rayCount; rayIndex++)
    {
        float3 direction = BasisGIRtCosineHemisphere(BasisGIRtHammersley(rayIndex, rayCount, jitter), normalWS);
        float3 origin = OffsetRayOrigin(positionWS, normalWS, originBias);
        float3 throughput = float3(1.0, 1.0, 1.0);
        float3 radiance = float3(0.0, 0.0, 0.0);
        uint pathSeed = BasisGIRtHash(seed + rayIndex * 7919u);

        UNITY_LOOP
        for (uint bounce = 0; bounce < bounces; bounce++)
        {
            float3 startedAt = origin;
            UnifiedRT::Hit hit = BasisGIRtTraceEscapingProxies(dispatchInfo, accelStruct, origin, direction, BASISGI_RT_RAY_LENGTH, (uint)_BasisGIRtTraceMask);
            if (!hit.IsValid())
            {
                radiance += throughput * BasisGIRtSampleSky(direction);
                break;
            }

            if (bounce == 0)
            {
                // Measured from where the ray actually started, not from where it resumed, so stepping out
                // of a capsule cannot make the thing beyond it read as closer than it is.
                float travelled = distance(origin, startedAt) + hit.hitDistance;
                occlusion += 1.0 - saturate(travelled / max(BASISGI_RT_OBSCURANCE_RADIUS, BASISGI_RT_EPSILON));
            }

            BasisGIRtInstance instance = _BasisGIRtInstances[hit.instanceID];
            float3 hitPosition = origin + direction * hit.hitDistance;
            float3 hitNormal = BasisGIRtHitNormal(instance, hit, direction);

            float3 contribution = instance.emission.rgb * BASISGI_RT_EMISSION;
            contribution += instance.albedo.rgb * BasisGIRtDirectLighting(dispatchInfo, accelStruct, hitPosition, hitNormal, pathSeed);

            // A second bounce that lands on something bright is the classic firefly: one pixel in a hundred
            // gets a value the filter then smears over its neighbours. Clamping the later bounces costs a
            // little energy and buys a great deal of stability.
            if (bounce > 0) { contribution = BasisGIRtClampFirefly(contribution, BASISGI_RT_FIREFLY_CLAMP); }
            radiance += throughput * contribution;

            throughput *= instance.albedo.rgb;
            if (max(throughput.r, max(throughput.g, throughput.b)) < BASISGI_RT_BOUNCE_THRESHOLD) { break; }

            pathSeed = BasisGIRtHash(pathSeed + bounce * 2654435761u);
            float2 next = float2(BasisGIRtUnitFloat(pathSeed), BasisGIRtUnitFloat(BasisGIRtHash(pathSeed ^ 0x85ebca6bu)));
            direction = BasisGIRtCosineHemisphere(next, hitNormal);
            origin = OffsetRayOrigin(hitPosition, hitNormal, originBias);
        }

        gathered += BasisGIRtClampFirefly(radiance, BASISGI_RT_FIREFLY_CLAMP);
    }

    float rcpCount = rcp(float(rayCount));
    float3 indirect = gathered * rcpCount;
    float obscurance = 1.0 - saturate(occlusion * rcpCount) * BASISGI_RT_OBSCURANCE;
    _BasisGIRtResultTex[id] = float4(indirect * fade, lerp(1.0, obscurance, fade));
}

#endif
