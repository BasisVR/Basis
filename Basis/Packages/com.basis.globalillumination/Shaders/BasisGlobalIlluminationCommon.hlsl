#ifndef BASIS_GLOBAL_ILLUMINATION_COMMON_INCLUDED
#define BASIS_GLOBAL_ILLUMINATION_COMMON_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
#include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/ImageBasedLighting.hlsl"

#define BASISGI_MAX_EMITTERS 48
#define BASISGI_EPSILON 1e-5
// How long a still pixel is allowed to keep accumulating, and how far a history sample may sit from the
// neighbourhood mean before it is pulled back in.
#define BASISGI_TEMPORAL_MAX_FRAMES 64.0
#define BASISGI_TEMPORAL_CLIP_SIGMA 1.5
// Zero bright samples out of N is not evidence that the true mean is zero. At one or two rays a pixel
// misses a small bright source far more often than it finds it, so a neighbourhood of misses has no
// spread at all and a clip box built from that spread alone closes onto zero - erasing exactly the
// light the accumulation had spent a second finding. Three over N is the standard bound on how often
// such a hit could have been missed, and it is the width the box is never allowed to fall below.
#define BASISGI_TEMPORAL_CLIP_RARE 3.0
#define BASISGI_TEMPORAL_NEIGHBOURS 9.0
// How wide the spatial filter's luminance gate is, in standard deviations of what the pixel has
// actually been accumulating, and how many frames of history count as settled. Below that a pixel is
// treated as unresolved and the gate opens wide enough to let a neighbour's bright sample through,
// which is the only way a gather this sparse ever gets reconstructed.
#define BASISGI_BLUR_LUMINANCE 4.0
#define BASISGI_BLUR_CONVERGED 12.0
// A settled surface still has a little sampling noise on it, and a gate with no floor closes hard
// enough on one to stop filtering altogether.
#define BASISGI_BLUR_LUMINANCE_FLOOR 0.02
// How far off a pixel's own surface plane a neighbour may sit before it stops being the same surface,
// as a fraction of the distance to it, with a floor for anything close to the camera.
#define BASISGI_BLUR_PLANE 0.03
#define BASISGI_BLUR_PLANE_FLOOR 0.02

TEXTURE2D_X(_BasisGISceneColor);
SAMPLER(sampler_BasisGISceneColor);
TEXTURE2D_X(_BasisGIIndirect);
SAMPLER(sampler_BasisGIIndirect);
TEXTURE2D_X(_BasisGIHistory);
SAMPLER(sampler_BasisGIHistory);
TEXTURE2D_X(_BasisGIHistoryStats);
TEXTURE2D_X(_BasisGIStats);
TEXTURE2D_X(_BasisGINormals);
TEXTURECUBE(_BasisGISkyCube);
SAMPLER(sampler_BasisGISkyCube);

float4 _BasisGIParams0;
float4 _BasisGIParams1;
float4 _BasisGIParams2;
float4 _BasisGIParams3;
float4 _BasisGITint;
float4 _BasisGITracedTexelSize;
float4 _BasisGISourceTexelSize;
/// Mip to read in x, and how much of it to let through in y - zero when the fallback is off or
/// there is no environment bound at all.
float4 _BasisGISky;
float4 _BasisGISkyDecode;
float4x4 _BasisGIPrevViewProjection[2];
float _BasisGIHistoryValid;
float _BasisGIStatsValid;
int _BasisGIDebugView;
int _BasisGIEmitterCount;
float4 _BasisGIEmitterSpheres[BASISGI_MAX_EMITTERS];
float4 _BasisGIEmitterRadiance[BASISGI_MAX_EMITTERS];

#define BASISGI_INTENSITY          _BasisGIParams0.x
#define BASISGI_SATURATION         _BasisGIParams0.y
#define BASISGI_OBSCURANCE         _BasisGIParams0.z
#define BASISGI_OBSCURANCE_RADIUS  _BasisGIParams0.w
#define BASISGI_MAX_RAY_LENGTH     _BasisGIParams1.x
#define BASISGI_THICKNESS          _BasisGIParams1.y
#define BASISGI_JITTER             _BasisGIParams1.z
#define BASISGI_FADE_DISTANCE      _BasisGIParams1.w
#define BASISGI_RAY_COUNT          _BasisGIParams2.x
#define BASISGI_RAY_STEPS          _BasisGIParams2.y
#define BASISGI_FIREFLY_CLAMP      _BasisGIParams2.z
#define BASISGI_FRAME_INDEX        _BasisGIParams3.x
#define BASISGI_TEMPORAL_RESPONSE  _BasisGIParams3.y
#define BASISGI_DEPTH_REJECTION    _BasisGIParams3.z
#define BASISGI_EMITTER_INTENSITY  _BasisGIParams3.w

float4x4 BasisGIPreviousViewProjection()
{
#if defined(UNITY_STEREO_INSTANCING_ENABLED) || defined(UNITY_STEREO_MULTIVIEW_ENABLED)
    return _BasisGIPrevViewProjection[unity_StereoEyeIndex];
#else
    return _BasisGIPrevViewProjection[0];
#endif
}

float BasisGILinearEyeDepth(float rawDepth)
{
    float perspective = LinearEyeDepth(rawDepth, _ZBufferParams);
#if UNITY_REVERSED_Z
    float orthographic = lerp(_ProjectionParams.z, _ProjectionParams.y, rawDepth);
#else
    float orthographic = lerp(_ProjectionParams.y, _ProjectionParams.z, rawDepth);
#endif
    return lerp(perspective, orthographic, unity_OrthoParams.w);
}

float BasisGISampleRawDepth(float2 uv)
{
    return SAMPLE_TEXTURE2D_X_LOD(_CameraDepthTexture, sampler_PointClamp, UnityStereoTransformScreenSpaceTex(uv), 0).r;
}

float BasisGISampleEyeDepth(float2 uv)
{
    return BasisGILinearEyeDepth(BasisGISampleRawDepth(uv));
}

bool BasisGIIsSky(float rawDepth)
{
#if UNITY_REVERSED_Z
    return rawDepth <= 0.0;
#else
    return rawDepth >= 1.0;
#endif
}

float3 BasisGIWorldPosition(float2 uv, float rawDepth)
{
    return ComputeWorldSpacePosition(uv, rawDepth, UNITY_MATRIX_I_VP);
}

float3 BasisGIViewPosition(float2 uv, float rawDepth)
{
    float3 world = BasisGIWorldPosition(uv, rawDepth);
    return TransformWorldToView(world);
}

float4 BasisGIWorldToScreen(float3 worldPosition)
{
    float4 clip = mul(GetWorldToHClipMatrix(), float4(worldPosition, 1.0));
    float3 ndc = clip.xyz / max(clip.w, BASISGI_EPSILON);
    float2 uv = ndc.xy * 0.5 + 0.5;
#if UNITY_UV_STARTS_AT_TOP
    uv.y = 1.0 - uv.y;
#endif
    return float4(uv, ndc.z, clip.w);
}

float3 BasisGIReconstructNormal(float2 uv, float3 viewPosition, float rawDepth)
{
#if defined(_BASISGI_NORMALS_TEXTURE)
    float3 packed = SAMPLE_TEXTURE2D_X_LOD(_BasisGINormals, sampler_PointClamp, UnityStereoTransformScreenSpaceTex(uv), 0).xyz;
    return normalize(packed * 2.0 - 1.0);
#else
    float2 texel = _BasisGISourceTexelSize.xy;
    float2 uvRight = uv + float2(texel.x, 0.0);
    float2 uvLeft = uv - float2(texel.x, 0.0);
    float2 uvUp = uv + float2(0.0, texel.y);
    float2 uvDown = uv - float2(0.0, texel.y);

    float depthRight = BasisGISampleRawDepth(uvRight);
    float depthLeft = BasisGISampleRawDepth(uvLeft);
    float depthUp = BasisGISampleRawDepth(uvUp);
    float depthDown = BasisGISampleRawDepth(uvDown);

    float eye = BasisGILinearEyeDepth(rawDepth);
    float diffRight = abs(BasisGILinearEyeDepth(depthRight) - eye);
    float diffLeft = abs(BasisGILinearEyeDepth(depthLeft) - eye);
    float diffUp = abs(BasisGILinearEyeDepth(depthUp) - eye);
    float diffDown = abs(BasisGILinearEyeDepth(depthDown) - eye);

    float3 horizontal = diffRight < diffLeft
        ? BasisGIViewPosition(uvRight, depthRight) - viewPosition
        : viewPosition - BasisGIViewPosition(uvLeft, depthLeft);
    float3 vertical = diffUp < diffDown
        ? BasisGIViewPosition(uvUp, depthUp) - viewPosition
        : viewPosition - BasisGIViewPosition(uvDown, depthDown);

    float3 viewNormal = normalize(cross(horizontal, vertical));
    float3 worldNormal = mul((float3x3)UNITY_MATRIX_I_V, viewNormal);
    return normalize(worldNormal);
#endif
}

float BasisGIInterleavedGradientNoise(float2 pixel, float frame)
{
    pixel += frame * float2(47.0, 17.0) * 0.695;
    return frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
}

float2 BasisGIHammersley(uint index, uint count)
{
    uint bits = index;
    bits = (bits << 16u) | (bits >> 16u);
    bits = ((bits & 0x55555555u) << 1u) | ((bits & 0xAAAAAAAAu) >> 1u);
    bits = ((bits & 0x33333333u) << 2u) | ((bits & 0xCCCCCCCCu) >> 2u);
    bits = ((bits & 0x0F0F0F0Fu) << 4u) | ((bits & 0xF0F0F0F0u) >> 4u);
    bits = ((bits & 0x00FF00FFu) << 8u) | ((bits & 0xFF00FF00u) >> 8u);
    return float2(float(index) / max(1.0, float(count)), float(bits) * 2.3283064365386963e-10);
}

float3x3 BasisGIOrthonormalBasis(float3 normal)
{
    float sign = normal.z >= 0.0 ? 1.0 : -1.0;
    float a = -1.0 / (sign + normal.z);
    float b = normal.x * normal.y * a;
    float3 tangent = float3(1.0 + sign * normal.x * normal.x * a, sign * b, -sign * normal.x);
    float3 bitangent = float3(b, sign + normal.y * normal.y * a, -normal.y);
    return float3x3(tangent, bitangent, normal);
}

float3 BasisGICosineDirection(float2 sample, float3x3 basis)
{
    float radius = sqrt(sample.x);
    float angle = TWO_PI * sample.y;
    float3 local = float3(radius * cos(angle), radius * sin(angle), sqrt(max(0.0, 1.0 - sample.x)));
    return normalize(mul(local, basis));
}

float3 BasisGIClampFirefly(float3 radiance)
{
    float peak = max(radiance.r, max(radiance.g, radiance.b));
    float scale = peak > BASISGI_FIREFLY_CLAMP ? BASISGI_FIREFLY_CLAMP / max(peak, BASISGI_EPSILON) : 1.0;
    return radiance * scale;
}

float3 BasisGISampleSceneColor(float2 uv)
{
    return SAMPLE_TEXTURE2D_X_LOD(_BasisGISceneColor, sampler_BasisGISceneColor, UnityStereoTransformScreenSpaceTex(saturate(uv)), 0).rgb;
}

/// <summary>
/// What a ray that hit nothing is worth.
///
/// The environment is bound explicitly rather than read from unity_SpecCube0. That slot is filled per
/// renderer by the reflection probe system, and a fullscreen pass has no renderer, so what it holds is
/// whatever the last object drawn happened to leave there - in practice nothing. Every ray that missed was
/// returning black, which made both fallback settings do nothing at all and cost the screen space gather
/// its entire ambient term. Binding it by hand also means both modes read the same cubemap at the same
/// mip, so switching mode does not change what an unoccluded ray is worth.
/// </summary>
float3 BasisGIFallbackRadiance(float3 direction)
{
#if defined(_BASISGI_FALLBACK_PROBE) || defined(_BASISGI_FALLBACK_SKY)
    if (_BasisGISky.y <= 0.0) { return float3(0.0, 0.0, 0.0); }
    float4 encoded = SAMPLE_TEXTURECUBE_LOD(_BasisGISkyCube, sampler_BasisGISkyCube, direction, _BasisGISky.x);
    return max(0.0, DecodeHDREnvironment(encoded, _BasisGISkyDecode)) * _BasisGISky.y;
#else
    return float3(0.0, 0.0, 0.0);
#endif
}

/// <summary>
/// Frames accumulated in x, and the variance of the luminance the accumulation is holding in y.
/// Zero frames means the temporal filter did not run, and the spatial filter then has to treat every
/// pixel as unresolved rather than trusting statistics that were never gathered.
/// </summary>
float2 BasisGIAccumulation(float2 uv)
{
    if (_BasisGIStatsValid < 0.5) { return float2(0.0, 0.0); }
    float4 stats = SAMPLE_TEXTURE2D_X_LOD(_BasisGIStats, sampler_PointClamp, UnityStereoTransformScreenSpaceTex(saturate(uv)), 0);
    return float2(stats.g, max(0.0, stats.a));
}

/// <summary>
/// The surface plane at this pixel, taken from the screen space derivatives of the reconstructed world
/// position. In a fullscreen pass the quad's own finite difference is already the tangent pair, so this
/// costs nothing, where reconstructing a normal from four more depth taps costs four more depth taps.
/// Across a depth discontinuity the derivatives are meaningless, and it degrades to a normal facing the
/// camera - which turns the plane test back into the plain depth test it replaced.
/// </summary>
float3 BasisGIPlaneNormal(float3 worldPosition)
{
    float3 normal = cross(ddy(worldPosition), ddx(worldPosition));
    float lengthSquared = dot(normal, normal);
    if (lengthSquared < 1e-12)
    {
        float3 toCamera = GetCameraPositionWS() - worldPosition;
        float toCameraLengthSquared = dot(toCamera, toCamera);
        return toCameraLengthSquared < 1e-12 ? float3(0.0, 0.0, 1.0) : toCamera * rsqrt(toCameraLengthSquared);
    }
    return normal * rsqrt(lengthSquared);
}

/// <summary>How far off a pixel's own plane a neighbour may sit and still be the same surface.</summary>
float BasisGIPlaneTolerance(float eyeDepth)
{
    return max(BASISGI_BLUR_PLANE_FLOOR, eyeDepth * BASISGI_BLUR_PLANE);
}

float BasisGIDistanceFade(float eyeDepth)
{
    return saturate(1.0 - eyeDepth / max(1.0, BASISGI_FADE_DISTANCE));
}

#endif
