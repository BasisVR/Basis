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
int _BasisGIRtRayCount;
int _BasisGIRtBounces;
int _BasisGIRtLightCount;
int _BasisGIRtLightSamples;
int _BasisGIRtViewCount;
int _BasisGIRtFrameIndex;

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
/// </summary>
float3 BasisGIRtDirectLighting(UnifiedRT::DispatchInfo dispatchInfo, UnifiedRT::RayTracingAccelStruct accelStruct,
    float3 positionWS, float3 normalWS, inout uint seed)
{
    int count = min(_BasisGIRtLightCount, BASISGI_RT_MAX_LIGHTS);
    if (count <= 0) { return float3(0.0, 0.0, 0.0); }

    int samples = clamp(_BasisGIRtLightSamples, 1, BASISGI_RT_MAX_LIGHT_SAMPLES);
    float3 total = float3(0.0, 0.0, 0.0);

    UNITY_LOOP
    for (int sampleIndex = 0; sampleIndex < samples; sampleIndex++)
    {
        float weightSum = 0.0;
        float chosenWeight = 0.0;
        float3 chosenRadiance = float3(0.0, 0.0, 0.0);
        float3 chosenDirection = float3(0.0, 1.0, 0.0);
        float chosenDistance = 0.0;
        float chosenShadow = 0.0;

        UNITY_LOOP
        for (int index = 0; index < count; index++)
        {
            BasisGIRtLight light = _BasisGIRtLights[index];

            float3 toLight;
            float attenuation;
            float distanceToLight;

            if (light.direction.w < 0.5)
            {
                toLight = -normalize(light.direction.xyz);
                attenuation = 1.0;
                distanceToLight = BASISGI_RT_RAY_LENGTH * 4.0;
            }
            else
            {
                float3 delta = light.position.xyz - positionWS;
                float radius = max(light.spot.w, 0.0);
                float distanceSquared = max(dot(delta, delta), max(1e-4, radius * radius));
                distanceToLight = sqrt(distanceSquared);
                if (distanceToLight > light.position.w) { continue; }

                toLight = delta * rcp(max(distanceToLight, 1e-4));
                attenuation = BasisGIRtDistanceAttenuation(distanceSquared, light.spot.z);
                if (light.direction.w > 1.5) { attenuation *= BasisGIRtSpotAttenuation(light, toLight); }
            }

            float contribution = saturate(dot(normalWS, toLight)) * attenuation;
            if (contribution <= 1e-4) { continue; }

            float3 radiance = light.color.rgb * contribution;
            float weight = max(radiance.r, max(radiance.g, radiance.b));
            if (weight <= 0.0) { continue; }

            // A reservoir: each light replaces the one held with probability equal to its share of the
            // weight seen so far, which leaves the holder drawn exactly in proportion to its own weight
            // after one pass and needs no second look at the list.
            weightSum += weight;
            seed = BasisGIRtHash(seed + 0x9e3779b9u);
            if (BasisGIRtUnitFloat(seed) * weightSum <= weight)
            {
                chosenWeight = weight;
                chosenRadiance = radiance;
                chosenDirection = toLight;
                chosenDistance = distanceToLight;
                chosenShadow = light.color.w;
            }
        }

        if (chosenWeight <= 0.0) { continue; }

        float visibility = 1.0;
        if (BASISGI_RT_SHADOW_RAYS > 0.5 && chosenShadow > 0.5)
        {
            UnifiedRT::Ray shadowRay;
            shadowRay.origin = OffsetRayOrigin(positionWS, normalWS, BASISGI_RT_NORMAL_BIAS);
            shadowRay.direction = chosenDirection;
            shadowRay.tMin = 0.0;
            shadowRay.tMax = max(0.0, chosenDistance - BASISGI_RT_NORMAL_BIAS * 2.0);
            visibility = UnifiedRT::TraceRayAnyHit(dispatchInfo, accelStruct, 0xffffffff, shadowRay, 0) ? 0.0 : 1.0;
        }

        total += chosenRadiance * ((weightSum / chosenWeight) * visibility);
    }

    return total * (BASISGI_RT_LIGHT_INTENSITY / (float)samples);
}

void RayGenExecute(UnifiedRT::DispatchInfo dispatchInfo)
{
    uint3 id = dispatchInfo.dispatchThreadID;
    if (id.x >= (uint)_BasisGIRtSize.x || id.y >= (uint)_BasisGIRtSize.y || id.z >= (uint)_BasisGIRtViewCount)
    {
        return;
    }

    float4 packed = _BasisGIRtPositionTex.Load(int4(id.xy, id.z, 0));
    if (packed.w < 0.5)
    {
        _BasisGIRtResultTex[id] = float4(0.0, 0.0, 0.0, 1.0);
        return;
    }

    float viewDistance = length(packed.xyz);
    float fade = saturate(1.0 - viewDistance / max(1.0, BASISGI_RT_FADE_DISTANCE));
    if (fade <= 0.0)
    {
        _BasisGIRtResultTex[id] = float4(0.0, 0.0, 0.0, 1.0);
        return;
    }

    float3 positionWS = packed.xyz + _BasisGIRtReference.xyz;
    float3 normalWS = BasisGIRtDecodeNormal(_BasisGIRtNormalTex.Load(int4(id.xy, id.z, 0)).xy);

    uint seed = BasisGIRtHash(id.x * 1973u + id.y * 9277u + id.z * 26699u + (uint)_BasisGIRtFrameIndex * 6151u);

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
    float originBias = BASISGI_RT_NORMAL_BIAS + BASISGI_RT_DISTANCE_BIAS * viewDistance;

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
            UnifiedRT::Ray ray;
            ray.origin = origin;
            ray.tMin = 0.0;
            ray.direction = direction;
            ray.tMax = BASISGI_RT_RAY_LENGTH;

            UnifiedRT::Hit hit = UnifiedRT::TraceRayClosestHit(dispatchInfo, accelStruct, 0xffffffff, ray, 0);
            if (!hit.IsValid())
            {
                radiance += throughput * BasisGIRtSampleSky(direction);
                break;
            }

            if (bounce == 0)
            {
                occlusion += 1.0 - saturate(hit.hitDistance / max(BASISGI_RT_OBSCURANCE_RADIUS, BASISGI_RT_EPSILON));
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
