#ifndef BASIS_GLOBAL_ILLUMINATION_COMPOSITE_INCLUDED
#define BASIS_GLOBAL_ILLUMINATION_COMPOSITE_INCLUDED

#include "./BasisGlobalIlluminationCommon.hlsl"

/// <summary>
/// Half resolution back up to full, weighted so a traced texel only contributes to a pixel on the same
/// surface. The plane test is the same one the spatial filter uses, and it is what stops the bounce from
/// halo-ing across a silhouette where a depth difference alone would have accepted the wrong texel.
/// </summary>
float4 BasisGIUpsample(float2 uv, float centreRaw, float centreEye, bool isSky)
{
#if defined(_BASISGI_BILATERAL_UPSAMPLE)
    float3 centrePosition = BasisGIWorldPosition(uv, centreRaw);
    float3 centreNormal = BasisGIPlaneNormal(centrePosition);
    // Built next to the normal for the same reason it is: both are screen space derivatives, and both are
    // only meaningful where the whole quad agreed to take them.
    BasisGIPlaneBasis basis = BasisGIBuildPlaneBasis(centrePosition, centreNormal, centreEye, _BasisGISourceTexelSize.xy);
    if (isSky) { return SAMPLE_TEXTURE2D_X_LOD(_BasisGIIndirect, sampler_BasisGIIndirect, UnityStereoTransformScreenSpaceTex(uv), 0); }

    float2 texel = _BasisGITracedTexelSize.xy;
    float2 tracedCoord = uv * _BasisGITracedTexelSize.zw - 0.5;
    float2 baseCoord = floor(tracedCoord);
    float2 fraction = tracedCoord - baseCoord;

    float4 total = float4(0.0, 0.0, 0.0, 0.0);
    float weightSum = 0.0;

    UNITY_UNROLL
    for (int y = 0; y < 2; y++)
    {
        UNITY_UNROLL
        for (int x = 0; x < 2; x++)
        {
            float2 sampleUv = (baseCoord + float2(x, y) + 0.5) * texel;
            float bilinear = (x == 0 ? 1.0 - fraction.x : fraction.x) * (y == 0 ? 1.0 - fraction.y : fraction.y);

            // This runs at full resolution, so the four taps below are the most repeated work in the whole
            // effect. Taking the parent's depth from the statistics it already wrote costs one traced
            // resolution fetch where the depth texture costs a full resolution one, and the plane test then
            // needs no inverse view projection per tap at all.
            float depthWeight;

            UNITY_BRANCH
            if (basis.usable)
            {
                float tapEye = BasisGIStats(sampleUv).x;
                depthWeight = tapEye > 0.0 ? exp(-BasisGIPlaneDistance(basis, sampleUv - uv, tapEye) / basis.scale) : 0.0;
            }
            else
            {
                float sampleRaw = BasisGISampleRawDepth(sampleUv);
                float plane = abs(dot(centreNormal, BasisGIWorldPosition(sampleUv, sampleRaw) - centrePosition));
                depthWeight = BasisGIIsSky(sampleRaw) ? 0.0 : exp(-plane / basis.scale);
            }
            float weight = bilinear * depthWeight + 1e-4;
            total += SAMPLE_TEXTURE2D_X_LOD(_BasisGIIndirect, sampler_PointClamp, UnityStereoTransformScreenSpaceTex(saturate(sampleUv)), 0) * weight;
            weightSum += weight;
        }
    }

    return total / max(weightSum, BASISGI_EPSILON);
#else
    return SAMPLE_TEXTURE2D_X_LOD(_BasisGIIndirect, sampler_BasisGIIndirect, UnityStereoTransformScreenSpaceTex(uv), 0);
#endif
}

void BasisGIResolve(float2 uv, out float3 indirect, out float obscurance, out float rawDepth)
{
    rawDepth = BasisGISampleRawDepth(uv);
    bool isSky = BasisGIIsSky(rawDepth);
    float centreEye = BasisGILinearEyeDepth(rawDepth);

    float4 traced = BasisGIUpsample(uv, rawDepth, centreEye, isSky);
    indirect = max(0.0, traced.rgb);
    obscurance = saturate(traced.a);

    if (isSky)
    {
        indirect = float3(0.0, 0.0, 0.0);
        obscurance = 1.0;
        return;
    }

    float luminance = Luminance(indirect);
    indirect = lerp(luminance.xxx, indirect, BASISGI_SATURATION);
    indirect = max(0.0, indirect) * _BasisGITint.rgb * BASISGI_INTENSITY;
}

/// <summary>
/// Reflections brought back to full resolution, so the lit shaders that sample this by screen UV are not
/// fetching a half resolution texel across a silhouette. It is the same plane weighted upsample the bounce
/// uses, and it matters more here: what leaks across an edge is a recognisable image rather than a colour.
///
/// Alpha is confidence, not obscurance. The upsample carries it through the same weights the radiance rides,
/// so a pixel whose only usable neighbours were rejected comes out with no confidence and the lit shader
/// keeps its reflection probe. Sky pixels never had a surface to reflect from and say so outright.
///
/// None of the diffuse look controls apply. Intensity is already in the traced value, and tint or saturation
/// on a reflection would stop it matching the thing being reflected.
/// </summary>
float4 BasisGISpecularResolve(float2 uv)
{
    float rawDepth = BasisGISampleRawDepth(uv);
    bool isSky = BasisGIIsSky(rawDepth);

    // Sky is handed to the upsample and discarded after it rather than short circuiting in front of it. The
    // upsample opens on two screen space derivatives, and a quad where some lanes returned early takes them
    // across whatever those lanes left behind - so every pixel along a sky silhouette gets its plane weights
    // from a gradient that was never computed. The upsample returns immediately for sky anyway.
    float4 traced = BasisGIUpsample(uv, rawDepth, BasisGILinearEyeDepth(rawDepth), isSky);
    if (isSky) { return float4(0.0, 0.0, 0.0, 0.0); }
    return float4(max(0.0, traced.rgb), saturate(traced.a));
}

float4 BasisGIComposite(float2 uv)
{
    float3 indirect;
    float obscurance, rawDepth;
    BasisGIResolve(uv, indirect, obscurance, rawDepth);
    return float4(obscurance + indirect, 1.0);
}

float3 BasisGIHeat(float value)
{
    float clamped = saturate(value);
    return saturate(float3(clamped * 3.0, clamped * 3.0 - 1.0, clamped * 3.0 - 2.0));
}

float4 BasisGIDebug(float2 uv)
{
    float3 indirect;
    float obscurance, rawDepth;
    BasisGIResolve(uv, indirect, obscurance, rawDepth);

    if (_BasisGIDebugView == 1) { return float4(indirect, 1.0); }
    if (_BasisGIDebugView == 2) { return float4(obscurance.xxx, 1.0); }
    if (_BasisGIDebugView == 3)
    {
        if (BasisGIIsSky(rawDepth)) { return float4(0.0, 0.0, 0.0, 1.0); }
        float3 viewPosition = BasisGIViewPosition(uv, rawDepth);
        float3 normalWS = BasisGIReconstructNormal(uv, viewPosition, rawDepth);
        return float4(normalWS * 0.5 + 0.5, 1.0);
    }
    if (_BasisGIDebugView == 4) { return float4(BasisGIHeat(Luminance(indirect)), 1.0); }
    return float4(0.18 * (obscurance + indirect), 1.0);
}

#endif
