#ifndef BASIS_RTAO_KERNEL_INCLUDED
#define BASIS_RTAO_KERNEL_INCLUDED

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
#include "Packages/com.unity.render-pipelines.core/Runtime/UnifiedRayTracing/Common.hlsl"
#include "Packages/com.unity.render-pipelines.core/Runtime/UnifiedRayTracing/TraceRayAndQueryHit.hlsl"
#include "Packages/com.basis.rtao/Shaders/BasisRTAOCommon.hlsl"

UNIFIED_RT_DECLARE_ACCEL_STRUCT(_BasisRtaoAccel);

Texture2DArray<float4> _BasisRtaoPositionTex;
Texture2DArray<float4> _BasisRtaoNormalTex;
RWTexture2DArray<float2> _BasisRtaoResultTex;

float4 _BasisRtaoReference;
float4 _BasisRtaoTrace;
float4 _BasisRtaoBias;
float4 _BasisRtaoSize;
int _BasisRtaoRayCount;
int _BasisRtaoViewCount;
int _BasisRtaoFrameIndex;
int _BasisRtaoStereoCoherent;
// Which halves of the structure this trace may hit. The structure can be shared with global
// illumination, which wants its own answer, so the ray is what narrows it rather than the contents.
int _BasisRtaoTraceMask;

/// How many capsule walls one ray may step out of before it gives up and counts the hit. Four covers a foot
/// inside a shin capsule inside a hips capsule with another limb crossing it, without letting a degenerate
/// case spin.
#define BASIS_RTAO_MAX_PROXY_ESCAPES 4u
/// Far enough past the wall not to land back on it through floating point, small enough not to skip a body
/// standing immediately behind it.
#define BASIS_RTAO_PROXY_ESCAPE_EPSILON 0.005
/// How far from its origin a ray still counts a capsule as ITS OWN body. Must match
/// BASISGI_RT_PROXY_SELF_REACH - the two tracers stand on the same capsules and disagreeing about which of
/// them belongs to the surface being shaded would light an avatar one way and shade it another.
///
/// The back face test alone answers "did I start inside this", which is not the same question as "is this
/// me": the proxy fit is INSCRIBED, so the rendered skin sits OUTSIDE its own capsule and a ray leaving it
/// steeply re-enters it FRONT face, reading as another body in the way. That is the dark banding down
/// avatars' legs. See the global illumination constant for where twenty centimetres comes from.
#define BASIS_RTAO_PROXY_SELF_REACH 0.2

/// <summary>
/// One ray against the avatar proxy capsules ALONE, stepping out of any it began inside of.
///
/// Avatars are in the structure as capsules on their bones, but the point every ray starts from comes from
/// the depth buffer - the avatar's real rendered surface. The two do not coincide, so a surface sitting
/// inside its own proxy fires every ray into the inside of that proxy and reads as fully enclosed. On feet
/// it is at its worst: the shin capsule ends at the ankle BONE, which is buried inside the shoe, so the
/// visible shoe around the ankle sits inside the capsule and comes out as a hard black disc.
///
/// A ray that starts inside a closed shape leaves it through a BACK face, so that is the whole test.
///
/// This is only sound because the mask restricts the trace to proxies: every hit here IS a capsule, so a
/// back face can only mean "started inside a body". Global illumination reaches the same conclusion by
/// reading a proxy flag off the instance it hit - it has an instance buffer and this does not, and running
/// the capsules as their own trace is what replaces it. Do NOT widen this mask: a back face on a double
/// sided wall or a leaf card is real occlusion, and stepping through those would delete most of the effect.
/// </summary>
bool BasisRtaoTraceProxies(UnifiedRT::DispatchInfo dispatchInfo, UnifiedRT::RayTracingAccelStruct accelStruct,
    float3 origin, float3 direction, float tMax, uint mask, out float hitDistance)
{
    hitDistance = 0.0;
    // Distance from the ORIGINAL origin, so a hit found after stepping is still reported at its true range -
    // the falloff and the mean hit distance the denoiser reads are both scaled by it.
    float travelled = 0.0;

    UNITY_LOOP
    for (uint attempt = 0u; attempt < BASIS_RTAO_MAX_PROXY_ESCAPES; attempt++)
    {
        UnifiedRT::Ray ray;
        ray.origin = origin;
        ray.tMin = 0.0;
        ray.direction = direction;
        ray.tMax = tMax;

        UnifiedRT::Hit hit = UnifiedRT::TraceRayClosestHit(dispatchInfo, accelStruct, mask, ray, 0);
        if (!hit.IsValid()) { return false; }
        // Every hit in here is a capsule by construction, so the only question is whose. A back face means
        // the ray began inside one; a front face close enough to have been the capsule under this very
        // surface is the same body seen from outside, which the inscribed fit makes the common case.
        float range = travelled + hit.hitDistance;
        if (hit.isFrontFace && range >= BASIS_RTAO_PROXY_SELF_REACH)
        {
            // Far enough to be somebody else standing in the way, and they still occlude.
            hitDistance = range;
            return true;
        }

        float advance = hit.hitDistance + BASIS_RTAO_PROXY_ESCAPE_EPSILON;
        origin += direction * advance;
        travelled += advance;
        tMax -= advance;
        if (tMax <= 0.0) { return false; }
    }

    return false;
}

void RayGenExecute(UnifiedRT::DispatchInfo dispatchInfo)
{
    uint3 id = dispatchInfo.dispatchThreadID;
    if (id.x >= (uint)_BasisRtaoSize.x || id.y >= (uint)_BasisRtaoSize.y || id.z >= (uint)_BasisRtaoViewCount)
        return;

    float4 packed = _BasisRtaoPositionTex.Load(int4(id.xy, id.z, 0));
    if (packed.w < 0.5)
    {
        _BasisRtaoResultTex[id] = float2(1.0, 1.0);
        return;
    }

    float3 positionWS = packed.xyz + _BasisRtaoReference.xyz;
    float3 normalWS = BasisRtaoDecodeNormal(_BasisRtaoNormalTex.Load(int4(id.xy, id.z, 0)).xy);

    float viewDistance = length(packed.xyz);

    float2 jitter = BasisRtaoSampleJitter(positionWS, viewDistance, _BasisRtaoBias.z, id, (uint)_BasisRtaoFrameIndex, _BasisRtaoStereoCoherent != 0);

    // The distance term exists to clear the half float precision of the stored position, which is about
    // d/2048, so it has to grow with distance. But it must never grow into the search itself: at a 10 cm
    // radius an uncapped bias starts the ray a full radius above the surface by ~60 m out, and the occlusion
    // simply stops at a line. Cap it so three quarters of the radius always survives.
    float originBias = min(_BasisRtaoBias.x + _BasisRtaoBias.y * viewDistance, _BasisRtaoTrace.y * 0.25);

    UnifiedRT::RayTracingAccelStruct accelStruct = UNIFIED_RT_GET_ACCEL_STRUCT(_BasisRtaoAccel);

    UnifiedRT::Ray ray;
    ray.origin = OffsetRayOrigin(positionWS, normalWS, originBias);
    ray.tMin = 0.0;
    ray.tMax = _BasisRtaoTrace.y;

    uint rayCount = (uint)max(1, _BasisRtaoRayCount);
    float visibility = 0.0;
    float distanceSum = 0.0;

    // One tangent frame for the whole fan of rays; see BasisRtaoCosineHemisphere.
    float3 tangent, bitangent;
    BasisRtaoOrthonormalBasis(normalWS, tangent, bitangent);

    // Real geometry and body proxies are traced separately, because only the second may be stepped out of.
    // See BasisRtaoTraceProxies.
    uint traceMask = (uint)_BasisRtaoTraceMask;
    uint solidMask = traceMask & ~BASIS_RTAO_CATEGORY_AVATAR_PROXY;
    uint proxyMask = traceMask & BASIS_RTAO_CATEGORY_AVATAR_PROXY;

    if (_BasisRtaoTrace.z > 0.0)
    {
        for (uint i = 0; i < rayCount; ++i)
        {
            ray.direction = BasisRtaoCosineHemisphere(BasisRtaoHammersley(i, rayCount, jitter), normalWS, tangent, bitangent);

            // The nearer of the two answers wins, so a body in front of a wall still shades as a body.
            float best = _BasisRtaoTrace.y * 2.0;
            bool anyHit = false;
            if (solidMask != 0u)
            {
                UnifiedRT::Hit solid = UnifiedRT::TraceRayClosestHit(dispatchInfo, accelStruct, solidMask, ray, 0);
                if (solid.IsValid()) { best = solid.hitDistance; anyHit = true; }
            }
            if (proxyMask != 0u)
            {
                float proxyDistance;
                // The capsule walk is bounded by the answer real geometry already gave, because only the
                // NEARER of the two survives the min below: a body found behind the wall the ray already
                // stopped at cannot change this ray's occlusion, and every metre traversed looking for one
                // is spent for nothing. The falloff branch is where this matters - the any-hit branch below
                // never had a distance to bound with. Shortening the reach can only turn a hit that would
                // have lost the min into a miss, and a miss there leaves best exactly where it was.
                float proxyReach = anyHit ? min(ray.tMax, best) : ray.tMax;
                if (BasisRtaoTraceProxies(dispatchInfo, accelStruct, ray.origin, ray.direction, proxyReach, proxyMask, proxyDistance))
                {
                    best = anyHit ? min(best, proxyDistance) : proxyDistance;
                    anyHit = true;
                }
            }

            if (anyHit)
            {
                float normalized = saturate(best / _BasisRtaoTrace.y);
                visibility += pow(normalized, _BasisRtaoTrace.z);
                distanceSum += normalized;
            }
            else
            {
                visibility += 1.0;
                distanceSum += 1.0;
            }
        }
    }
    else
    {
        for (uint i = 0; i < rayCount; ++i)
        {
            ray.direction = BasisRtaoCosineHemisphere(BasisRtaoHammersley(i, rayCount, jitter), normalWS, tangent, bitangent);

            // Real geometry keeps the cheap any-hit; only the capsules pay for a closest-hit walk, and only
            // when the first trace found nothing to stop the ray anyway.
            bool occluded = solidMask != 0u
                && UnifiedRT::TraceRayAnyHit(dispatchInfo, accelStruct, solidMask, ray, 0);
            if (!occluded && proxyMask != 0u)
            {
                float proxyDistance;
                occluded = BasisRtaoTraceProxies(dispatchInfo, accelStruct, ray.origin, ray.direction, ray.tMax, proxyMask, proxyDistance);
            }
            visibility += occluded ? 0.0 : 1.0;
            distanceSum += occluded ? 0.0 : 1.0;
        }
    }

    float rcpCount = 1.0 / float(rayCount);
    _BasisRtaoResultTex[id] = float2(saturate(visibility * rcpCount), saturate(distanceSum * rcpCount));
}

#endif
