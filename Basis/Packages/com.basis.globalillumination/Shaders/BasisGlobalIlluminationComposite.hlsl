#ifndef BASIS_GLOBAL_ILLUMINATION_COMPOSITE_INCLUDED
#define BASIS_GLOBAL_ILLUMINATION_COMPOSITE_INCLUDED

#include "./BasisGlobalIlluminationCommon.hlsl"

float3 BasisGIUpsampleNormal(float2 uv)
{
#if defined(_BASISGI_NORMALS_TEXTURE)
    float3 packed = SAMPLE_TEXTURE2D_X_LOD(_BasisGINormals, sampler_PointClamp, UnityStereoTransformScreenSpaceTex(uv), 0).xyz;
    return dot(packed, packed) > 1e-6 ? normalize(packed) : float3(0.0, 0.0, 0.0);
#else
    return float3(0.0, 0.0, 0.0);
#endif
}

float BasisGIUpsampleFacing(float3 shading, float2 tapUv)
{
#if defined(_BASISGI_NORMALS_TEXTURE)
    float facing = saturate(dot(shading, BasisGIUpsampleNormal(tapUv)));
    facing *= facing; facing *= facing; facing *= facing;
    return dot(shading, shading) > 0.5 ? 0.05 + 0.95 * facing : 1.0;
#else
    return 1.0;
#endif
}

float BasisGIUpsampleWeight(BasisGIPlaneBasis basis, float2 uv, float2 sampleUv, float2 tapUv, float3 centrePosition, float3 centreNormal)
{
    UNITY_BRANCH
    if (basis.statsUsable)
    {
        float tapEye = BasisGIStats(sampleUv).x;
        return tapEye > 0.0 ? exp(-BasisGIPlaneDistance(basis, tapUv - uv, tapEye) / basis.scale) : 0.0;
    }
    float sampleRaw = BasisGISampleRawDepth(tapUv);
    float plane = abs(dot(centreNormal, BasisGIWorldPosition(tapUv, sampleRaw) - centrePosition));
    return BasisGIIsSky(sampleRaw) ? 0.0 : exp(-plane / basis.scale);
}

float2 BasisGIUpsampleTapUv(float2 coord, float2 texel)
{
    if (_BasisGIUpsampleParams.x < 0.5) { return (coord + 0.5) * texel; }
    return (floor((coord + 0.5 + _BasisGIUpsampleParams.zw) * _BasisGISourceTexelSize.zw * texel) + 0.5) * _BasisGISourceTexelSize.xy;
}

/// <summary>
/// Half resolution back up to full, weighted so a traced texel only contributes to a pixel on the same
/// surface. The plane test is the same one the spatial filter uses, and it is what stops the bounce from
/// halo-ing across a silhouette where a depth difference alone would have accepted the wrong texel.
/// </summary>
float4 BasisGIUpsample(float2 uv, float centreRaw, float centreEye, bool isSky, out float4 low, out float4 high)
{
    low = high = float4(0.0, 0.0, 0.0, 1.0);
    UNITY_BRANCH
    if (_BasisGIUpsampleParams.y > 0.5) { return SAMPLE_TEXTURE2D_X_LOD(_BasisGIIndirect, sampler_PointClamp, UnityStereoTransformScreenSpaceTex(uv), 0); }
#if defined(_BASISGI_BILATERAL_UPSAMPLE)
    float3 centrePosition = BasisGIWorldPosition(uv, centreRaw);
    float3 centreNormal = BasisGIPlaneNormal(centrePosition);
    // Built next to the normal for the same reason it is: both are screen space derivatives, and both are
    // only meaningful where the whole quad agreed to take them.
    BasisGIPlaneBasis basis = BasisGIBuildPlaneBasis(centrePosition, centreNormal, centreEye, _BasisGISourceTexelSize.xy);
    if (isSky) { return SAMPLE_TEXTURE2D_X_LOD(_BasisGIIndirect, sampler_BasisGIIndirect, UnityStereoTransformScreenSpaceTex(uv), 0); }

    float2 texel = _BasisGITracedTexelSize.xy;
    float2 tracedCoord = uv * _BasisGITracedTexelSize.zw - 0.5 - _BasisGIUpsampleParams.zw;
    float2 baseCoord = floor(tracedCoord);
    float2 fraction = tracedCoord - baseCoord;
    float3 shading = BasisGIUpsampleNormal(uv);

    float4 total = float4(0.0, 0.0, 0.0, 0.0);
    float weightSum = 0.0;
    float bestDepthWeight = 0.0;
    low = float4(1e6, 1e6, 1e6, 1e6);
    high = float4(-1e6, -1e6, -1e6, -1e6);

    // This runs at full resolution, so the four taps below are the most repeated work in the whole
    // effect. Taking the parent's depth from the statistics it already wrote costs one traced
    // resolution fetch where the depth texture costs a full resolution one, and the plane test then
    // needs no inverse view projection per tap at all.
    UNITY_UNROLL
    for (int y = 0; y < 2; y++)
    {
        UNITY_UNROLL
        for (int x = 0; x < 2; x++)
        {
            float2 coord = baseCoord + float2(x, y);
            float2 sampleUv = (coord + 0.5) * texel;
            float bilinear = (x == 0 ? 1.0 - fraction.x : fraction.x) * (y == 0 ? 1.0 - fraction.y : fraction.y);
            float2 tapUv = BasisGIUpsampleTapUv(coord, texel);
            float depthWeight = BasisGIUpsampleWeight(basis, uv, sampleUv, tapUv, centrePosition, centreNormal);
            bestDepthWeight = max(bestDepthWeight, depthWeight);
            float weight = bilinear * depthWeight * BasisGIUpsampleFacing(shading, tapUv) + 1e-4;
            float4 tap = SAMPLE_TEXTURE2D_X_LOD(_BasisGIIndirect, sampler_PointClamp, UnityStereoTransformScreenSpaceTex(saturate(sampleUv)), 0);
            if (depthWeight > 0.1) { low = min(low, tap); high = max(high, tap); }
            total += tap * weight;
            weightSum += weight;
        }
    }

    // A silhouette pixel whose four nearest texels all sit on another surface would otherwise take their
    // bilinear average, which is the blocky edge a low trace resolution shows. The ring around them almost
    // always holds a texel from this pixel's own surface, and only edge pixels pay for looking.
    UNITY_BRANCH
    if (bestDepthWeight < 0.25)
    {
        UNITY_LOOP
        for (int ry = -1; ry <= 2; ry++)
        {
            UNITY_LOOP
            for (int rx = -1; rx <= 2; rx++)
            {
                if (rx >= 0 && rx <= 1 && ry >= 0 && ry <= 1) { continue; }
                float2 coord = baseCoord + float2(rx, ry);
                float2 sampleUv = (coord + 0.5) * texel;
                float2 d = coord + 0.5 - (tracedCoord + 0.5);
                float2 tapUv = BasisGIUpsampleTapUv(coord, texel);
                float depthWeight = BasisGIUpsampleWeight(basis, uv, sampleUv, tapUv, centrePosition, centreNormal);
                float weight = depthWeight * depthWeight * exp(-0.5 * dot(d, d)) * BasisGIUpsampleFacing(shading, tapUv);
                total += SAMPLE_TEXTURE2D_X_LOD(_BasisGIIndirect, sampler_PointClamp, UnityStereoTransformScreenSpaceTex(saturate(sampleUv)), 0) * weight;
                weightSum += weight;
            }
        }
    }

    float4 result = total / max(weightSum, BASISGI_EPSILON);
    if (low.a > high.a) { low = high = result; }
    return result;
#else
    low = high = SAMPLE_TEXTURE2D_X_LOD(_BasisGIIndirect, sampler_BasisGIIndirect, UnityStereoTransformScreenSpaceTex(uv), 0);
    return low;
#endif
}

float4 BasisGIUpsample(float2 uv, float centreRaw, float centreEye, bool isSky)
{
    float4 low, high;
    return BasisGIUpsample(uv, centreRaw, centreEye, isSky, low, high);
}

#define BASISGI_UPSCALE_FRESH 0.7
#define BASISGI_UPSCALE_REFRESH 0.12
#define BASISGI_UPSCALE_SLACK 0.5

float4 BasisGIUpscale(float2 uv)
{
    float rawDepth = BasisGISampleRawDepth(uv);
    bool isSky = BasisGIIsSky(rawDepth);
    float4 low, high;
    float4 current = BasisGIUpsample(uv, rawDepth, BasisGILinearEyeDepth(rawDepth), isSky, low, high);
    if (isSky) { return float4(0.0, 0.0, 0.0, 1.0); }
    float2 previousUv = uv - SAMPLE_TEXTURE2D_X_LOD(_BasisGIMotion, sampler_PointClamp, UnityStereoTransformScreenSpaceTex(uv), 0).xy;
    if (_BasisGIHistoryValid < 0.5 || any(previousUv <= 0.0) || any(previousUv >= 1.0)) { return current; }
    float4 history = SAMPLE_TEXTURE2D_X_LOD(_BasisGIHistory, sampler_LinearClamp, UnityStereoTransformScreenSpaceTex(previousUv), 0);
    float4 slack = (high - low) * BASISGI_UPSCALE_SLACK + 0.005;
    float4 bounded = clamp(history, low - slack, high + slack);
    float3 strayed = abs(history.rgb - bounded.rgb) / (slack.rgb + 0.02 * max(bounded.rgb, 0.0));
    float stale = saturate(max(strayed.r, max(strayed.g, strayed.b)));
    history = bounded;
    float2 nearest = floor(uv * _BasisGITracedTexelSize.zw - _BasisGIUpsampleParams.zw);
    float2 sampled = floor((nearest + 0.5 + _BasisGIUpsampleParams.zw) * _BasisGISourceTexelSize.zw * _BasisGITracedTexelSize.xy);
    bool exact = all(abs(sampled - floor(uv * _BasisGISourceTexelSize.zw)) < 0.5);
    float cells = max(1.0, _BasisGISourceTexelSize.z * _BasisGISourceTexelSize.w * _BasisGITracedTexelSize.x * _BasisGITracedTexelSize.y);
    return lerp(history, current, max(exact ? BASISGI_UPSCALE_FRESH : BASISGI_UPSCALE_REFRESH / cells, stale));
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

#if defined(_BASISGI_LIGHTMAP_MASK)
    // A lightmapped surface already holds its own bounce and its own baked occlusion, so the composite
    // multiplying more of both onto it is double counting - the room reads blown out and crushed at once.
    // The mask is one on dynamic surfaces and zero on lightmapped ones; the receive floor is what a
    // lightmapped surface keeps, because the bounce an avatar throws onto a wall is real light no bake
    // ever saw. The gather itself is untouched - lightmapped pixels still LIGHT the room, they just stop
    // being relit by it.
    float mask = SAMPLE_TEXTURE2D_X_LOD(_BasisGILightmapMask, sampler_BasisGILightmapMask, UnityStereoTransformScreenSpaceTex(uv), 0).r;
    float receive = lerp(_BasisGILightmapParams.x, 1.0, mask);
    indirect *= receive;
    obscurance = lerp(1.0, obscurance, receive);
#endif

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

float4 BasisGICompositeSurface(float2 uv)
{
    float3 indirect;
    float obscurance, rawDepth;
    BasisGIResolve(uv, indirect, obscurance, rawDepth);
    float4 surface = SAMPLE_TEXTURE2D_X_LOD(_BasisGISurface, sampler_PointClamp, UnityStereoTransformScreenSpaceTex(uv), 0);
    return float4(indirect * surface.rgb * (1.0 - surface.a), lerp(obscurance, 1.0, surface.a));
}

float4 BasisGICompositeUnknownSurface(float2 uv)
{
    float3 indirect;
    float obscurance, rawDepth;
    BasisGIResolve(uv, indirect, obscurance, rawDepth);
    float unknown = SAMPLE_TEXTURE2D_X_LOD(_BasisGISurface, sampler_PointClamp, UnityStereoTransformScreenSpaceTex(uv), 0).a;
    return float4(lerp(float3(1.0, 1.0, 1.0), obscurance + indirect, unknown), 1.0);
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
    if (_BasisGIDebugView == 6)
    {
        if (_BasisGISurfaceValid < 0.5) { return float4(1.0, 0.0, 1.0, 1.0); }
        return SAMPLE_TEXTURE2D_X_LOD(_BasisGISurface, sampler_PointClamp, UnityStereoTransformScreenSpaceTex(uv), 0);
    }
    return float4(0.18 * (obscurance + indirect), 1.0);
}

#endif
