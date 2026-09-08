#ifndef VOLUMETRIC_FOG_INCLUDED
#define VOLUMETRIC_FOG_INCLUDED

#include "./VolumetricFogCommon.hlsl"
#include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
#include "./DeclareDownsampledDepthTexture.hlsl"

// Computes the ray origin, direction, and returns the reconstructed world position for orthographic projection.
float3 ComputeOrthographicParams(float2 uv, float depth, out float3 ro, out float3 rd)
{
    float4x4 viewMatrix = UNITY_MATRIX_V;
    float2 ndc = uv * 2.0 - 1.0;

    rd = normalize(-viewMatrix[2].xyz);
    float3 rightOffset = normalize(viewMatrix[0].xyz) * (ndc.x * unity_OrthoParams.x);
    float3 upOffset = normalize(viewMatrix[1].xyz) * (ndc.y * unity_OrthoParams.y);
    float3 fwdOffset = rd * depth;

    float3 posWs = GetCameraPositionWS() + fwdOffset + rightOffset + upOffset;
    ro = posWs - fwdOffset;

    return posWs;
}

// Calculates the initial raymarching parameters.
void CalculateRaymarchingParams(float2 uv, out float3 ro, out float3 rd, out float iniOffsetToNearPlane, out float offsetLength, out float3 rdPhase)
{
    float depth = SampleDownsampledSceneDepth(uv);
    float3 posWS;

    UNITY_BRANCH
    if (unity_OrthoParams.w <= 0)
    {
        ro = GetCameraPositionWS();
#if !UNITY_REVERSED_Z
        depth = lerp(UNITY_NEAR_CLIP_VALUE, 1.0, depth);
#endif
        posWS = ComputeWorldSpacePosition(uv, depth, UNITY_MATRIX_I_VP);
        float3 offset = posWS - ro;
        offsetLength = length(offset);
        rd = offset / offsetLength;
        rdPhase = rd;

        // In perspective, ray direction should vary in length depending on which fragment we are at.
        float3 camFwd = normalize(-UNITY_MATRIX_V[2].xyz);
        float cos = dot(camFwd, rd);
        float fragElongation = 1.0 / cos;
        iniOffsetToNearPlane = fragElongation * _ProjectionParams.y;
    }
    else
    {
        depth = LinearEyeDepthOrthographic(depth);
        posWS = ComputeOrthographicParams(uv, depth, ro, rd);
        offsetLength = depth;

        // Fake the ray direction that will be used to calculate the phase, so we can still use anisotropy in orthographic mode.
        rdPhase = normalize(posWS - GetCameraPositionWS());
        iniOffsetToNearPlane = _ProjectionParams.y;
    }
}

// Per-pixel raymarch start jitter from a blue-noise texture (better-distributed than interleaved
// gradient noise, so banding is less visible at the same step count).
float GetRaymarchJitter(float2 positionCS)
{
    float2 uv = positionCS * _BlueNoiseParams.xy + _BlueNoiseParams.zw;
    return SAMPLE_TEXTURE2D_LOD(_BlueNoiseTexture, sampler_PointRepeat, uv, 0.0).r;
}

// Distance along the ray (from the band entry) at which the integral of the normalized height density
// min(a - b * s, 1) reaches target, or marchLength when it never does. The density is full below the base
// height and falls linearly to zero at the maximum height, so the integral is piecewise linear/quadratic.
float SolveNormalizedOpticalDepth(float target, float marchLength, float a, float b)
{
    UNITY_BRANCH
    if (abs(b) < 1e-6)
        return min(marchLength, target / max(min(a, 1.0), 1e-6));

    float sc = clamp((a - 1.0) / b, 0.0, marchLength);
    float t;

    UNITY_BRANCH
    if (b > 0.0)
    {
        UNITY_BRANCH
        if (target <= sc)
            return target;

        float c = target - sc + a * sc - 0.5 * b * sc * sc;
        float disc = a * a - 2.0 * b * c;
        UNITY_BRANCH
        if (disc < 0.0)
            return marchLength;

        t = (a - sqrt(disc)) / b;
    }
    else
    {
        t = (a - sqrt(a * a - 2.0 * b * target)) / b;
        UNITY_BRANCH
        if (t > sc)
            t = sc + target - (a * sc - 0.5 * b * sc * sc);
    }

    return min(t, marchLength);
}

// Calculates the volumetric fog. Returns the color in the RGB channels and transmittance in alpha.
float4 VolumetricFog(float2 uv, float2 positionCS)
{
    float3 ro;
    float3 rd;
    float iniOffsetToNearPlane;
    float offsetLength;
    float3 rdPhase;

    CalculateRaymarchingParams(uv, ro, rd, iniOffsetToNearPlane, offsetLength, rdPhase);

    // We treat the space between the camera and the near plane as if no fog existed there. It removes a
    // lot of noise in closed environments with darkening attenuation and certain near plane / fov / density
    // combinations, and looks much better when the near plane is above the minimum allowed.
    float3 roNearPlane = ro + rd * iniOffsetToNearPlane;
    float marchEnd = min(offsetLength, _Distance) - iniOffsetToNearPlane;

    // Empty-space skipping: density is only non-zero inside the world-Y band [_GroundHeight, _MaximumHeight].
    // Clip the march to where the ray actually crosses that band so no steps are wasted above or below it.
    float tEnter = 0.0;
    float tExit = marchEnd;

    UNITY_BRANCH
    if (abs(rd.y) > 1e-6)
    {
        float invRdy = 1.0 / rd.y;
        float tBandA = (_MaximumHeight - roNearPlane.y) * invRdy;
        float tBandB = (_GroundHeight - roNearPlane.y) * invRdy;
        tEnter = max(tEnter, min(tBandA, tBandB));
        tExit = min(tExit, max(tBandA, tBandB));
    }
    else if (roNearPlane.y > _MaximumHeight || roNearPlane.y < _GroundHeight)
    {
        tExit = tEnter; // Horizontal ray entirely outside the fog band.
    }

    UNITY_BRANCH
    if (tExit <= tEnter)
        return float4(0.0, 0.0, 0.0, 1.0);

    float invHeightRange = rcp(max(_MaximumHeight - _BaseHeight, 1e-4));
    bool analyticOpticalDepth = _VFAnalyticOpticalDepth > 0.5;
    float odA = (_MaximumHeight - (roNearPlane.y + rd.y * tEnter)) * invHeightRange;
    float odB = rd.y * invHeightRange;

    UNITY_BRANCH
    if (analyticOpticalDepth)
    {
        float extinction = _Absortion * _Density;
        UNITY_BRANCH
        if (extinction > 1e-6)
            tExit = tEnter + SolveNormalizedOpticalDepth(-log(VF_MIN_TRANSMITTANCE) / extinction, tExit - tEnter, odA, odB);
    }

    float marchLength = tExit - tEnter;

    // Cap the iteration count to the actual (slab-clipped) march length so the sample density (steps
    // per world unit) stays constant instead of always spending all _MaxSteps. Short crossings then
    // use far fewer iterations - and shadow samples - for the same quality.
    float fogSpan = max(1e-4, _Distance - iniOffsetToNearPlane);
    int stepCount = (int)clamp(ceil((float)_MaxSteps * (marchLength / fogSpan)), 1.0, (float)_MaxSteps);

    // Geometric step distribution over [tEnter, tExit]: ds grows by 'growth' each step so the far/near
    // step length ratio is VF_STEP_GROWTH_RATIO, while the lengths still sum exactly to marchLength.
    float growth = pow(VF_STEP_GROWTH_RATIO, 1.0 / (float)stepCount);
    float ds = marchLength * (growth - 1.0) / (VF_STEP_GROWTH_RATIO - 1.0);
    float jitterFrac = GetRaymarchJitter(positionCS);

    // Hoist the per-ray main-light terms out of the loop; only shadow, cookie and density vary per step.
    real3 mainLightConst = real3(0.0, 0.0, 0.0);
    bool sampleMainLight = false;
#if !_MAIN_LIGHT_CONTRIBUTION_DISABLED
    Light mainLight = GetMainLight();
    real phaseMainLight = CornetteShanksPhaseFunction(_MainLightAnisotropy, dot(rdPhase, mainLight.direction));
    mainLightConst = (mainLight.color * _Tint) * (phaseMainLight * _MainLightScattering);
    // When the main light can't contribute (black sun, zero scattering/tint), its per-step shadow sample is wasted work.
    sampleMainLight = any(mainLightConst > 0.0);
#endif
    bool sunTrims = _VFSunTrims > 0.5;
    half cascadeIndex = half(0.0);
    float3 cascadeCenter = float3(0.0, 0.0, 0.0);
    float cascadeRadius2 = -1.0;
    real cachedShadow = real(1.0);

    float minDensity = _Density * VF_MIN_DENSITY_FRACTION;
    float3 volumetricFogColor = float3(0.0, 0.0, 0.0);
    float transmittance = 1.0;
    float t = tEnter;

    UNITY_LOOP
    for (int i = 0; i < stepCount; ++i)
    {
        float tSample = t + ds * jitterFrac;
        float3 currPosWS = roNearPlane + rd * tSample;
        float density = GetFogDensity(currPosWS.y, invHeightRange);

        UNITY_BRANCH
        if (density > minDensity)
        {
            float transmittanceDensity = density;
            UNITY_BRANCH
            if (analyticOpticalDepth)
                transmittanceDensity = _Density * saturate(odA - odB * (t - tEnter + 0.5 * ds));
            transmittance *= exp(-ds * _Absortion * transmittanceDensity);

            real3 apvColor = real3(0.0, 0.0, 0.0);
#if _APV_CONTRIBUTION_ENABLED
            apvColor = EvaluateWeightedAPV(uv * _ScreenSize.xy, currPosWS) * density;
#endif
            real3 mainLightColor = real3(0.0, 0.0, 0.0);
            UNITY_BRANCH
            if (sampleMainLight)
            {
#if !_MAIN_LIGHT_CONTRIBUTION_DISABLED
                UNITY_BRANCH
                if (!sunTrims || (i & 1) == 0)
                {
                    float4 shadowCoord;
                    UNITY_BRANCH
                    if (sunTrims)
                        shadowCoord = VolumetricShadowCoord(currPosWS, true, cascadeIndex, cascadeCenter, cascadeRadius2);
                    else
                        shadowCoord = TransformWorldToShadowCoord(currPosWS);
                    cachedShadow = VolumetricMainLightRealtimeShadow(shadowCoord);
                }
                mainLightColor = mainLightConst * (cachedShadow * density);
    #if _LIGHT_COOKIES
                mainLightColor *= SampleMainLightCookie(currPosWS);
    #endif
#endif
            }

            real3 ltcgiColor = GetStepLTCGIColor(currPosWS, -rd, density);

            real3 stepColor = apvColor + mainLightColor + ltcgiColor;
            volumetricFogColor += (stepColor * (transmittance * ds));

            // Early-out once the medium is effectively opaque: further steps contribute ~0 and, since
            // transmittance is already near zero, the composite occludes the scene as dense fog should.
            UNITY_BRANCH
            if (transmittance < VF_MIN_TRANSMITTANCE)
                break;
        }

        t += ds;
        ds *= growth;
    }

    return float4(volumetricFogColor, transmittance);
}

#endif
