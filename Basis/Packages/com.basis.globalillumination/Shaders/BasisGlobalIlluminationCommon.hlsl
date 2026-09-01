#ifndef BASIS_GLOBAL_ILLUMINATION_COMMON_INCLUDED
#define BASIS_GLOBAL_ILLUMINATION_COMMON_INCLUDED

// The depth helpers live in their own header so the lightmap mask pass - which draws geometry and
// therefore cannot include Blit.hlsl - can share them. See BasisGlobalIlluminationDepth.hlsl.
#include "./BasisGlobalIlluminationDepth.hlsl"
#include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/ImageBasedLighting.hlsl"

#define BASISGI_MAX_EMITTERS 48
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
/// URP's motion vectors: a FORWARD vector in screen UV space, current minus previous, with the platform's
/// v flip and the NDC to UV halving already folded in by CalcNdcMotionVectorFromCsPositions. URP draws a
/// fullscreen camera motion quad before any per object motion, so every pixel carries a usable vector -
/// geometry with no motion pass of its own reads the camera's own motion, which is exactly what the
/// previous view-projection would have produced for it.
TEXTURE2D_X(_BasisGIMotion);
/// A coarse summary of the depth buffer for the hierarchical march: one texel per block of traced texels,
/// holding the CLOSEST eye depth in that block in r and the FURTHEST in g. Sky contributes to neither -
/// it writes r at the sky sentinel and leaves g at zero, so a block of pure sky is skipped by both tests.
TEXTURE2D_X(_BasisGICoarseDepth);
/// (1/width, 1/height, width, height) of the coarse buffer the march reads.
float4 _BasisGICoarseTexelSize;
/// The same pair again, folded one more time: one texel per block of COARSE texels, so one texel per
/// BASISGI_COARSE_FAR_BLOCK traced texels. A minimum of minima is still a minimum and a maximum of maxima
/// is still a maximum, so a ray dismissed against this is dismissed against every coarse cell underneath
/// it - which is the whole point, because clearing it costs one tap instead of eight.
TEXTURE2D_X(_BasisGICoarseFarDepth);
/// (1/width, 1/height, width, height) of the far buffer above.
float4 _BasisGICoarseFarTexelSize;
/// How many TRACED texels one far texel spans, and the flag for whether the far level exists at all: zero
/// when it was not built, which is every path that does not run the hierarchical march.
float _BasisGICoarseFarBlock;
/// The same pair of numbers as _BasisGICoarseDepth, one texel per TRACED texel rather than per block of
/// them, and already linearised: the closest real surface under this texel in r and the furthest in g.
/// This is what the march walks. Sky contributes to neither, so an empty texel reads the sky sentinel in
/// r and zero in g - which is what lets every consumer drop its sky test, because a ray can never be in
/// front of the sentinel.
TEXTURE2D_X(_BasisGITracedDepth);
/// Whether the buffer above was built for this camera. The ray traced mode does not need it.
float _BasisGITracedDepthValid;
/// How far beyond its surface each pixel's reflected image sits, in metres, written by the screen space
/// reflection trace at traced resolution - the sky sentinel where the answer was not a found surface. The
/// specular temporal filter reprojects by the virtual point this describes rather than by the surface,
/// because a reflection moves with the parallax of the thing reflected, not of the mirror it sits on.
TEXTURE2D_X(_BasisGISpecHitDistance);
/// Whether the buffer above was written this frame. Zero for the diffuse accumulation and for the ray
/// traced reflection backend, which reprojects by the surface as it always has.
float _BasisGISpecHitDistanceValid;
/// One where the frontmost surface is NOT lightmapped - avatars, props, anything dynamic - and zero where
/// it is, at traced resolution. Cleared to one and only ever written down, so every way the mask pass can
/// fail to draw leaves the effect exactly as it was without the mask. The first version of this pass had
/// the opposite polarity and every failure read as "global illumination is gone"; see the pass in
/// BasisGlobalIlluminationPass.RecordLightmapMask.
TEXTURE2D_X(_BasisGILightmapMask);
SAMPLER(sampler_BasisGILightmapMask);
/// x: how much bounce a lightmapped surface still receives (its own bounce is already in its lightmap).
/// One disables the whole path.
float4 _BasisGILightmapParams;
/// x: how many source texels one destination texel folds, per side, while building.
/// yz: the source texture's size, for clamping those taps.
/// w: how many TRACED texels one finished coarse texel spans, which is the march's cell size.
float4 _BasisGICoarseParams;

#define BASISGI_COARSE_SPAN        _BasisGICoarseParams.x
#define BASISGI_COARSE_SOURCE_SIZE _BasisGICoarseParams.yz
#define BASISGI_COARSE_BLOCK       _BasisGICoarseParams.w
/// Half's largest finite value. A block with nothing in it has to read as further away than any ray can
/// reach, and this is the largest number the R16 target can actually hold.
#define BASISGI_SKY_DEPTH          65504.0
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

/// <summary>
/// The closest and the furthest real surface under a traced texel, in linear eye depth.
///
/// This exists because the march runs at traced resolution and was reading FULL resolution depth on every
/// step, every binary refine step and every emitter shadow step - a texture four times the size it needed
/// at Half, point sampled at a stride of about a traced texel, so three quarters of every cache line it
/// pulled in was for texels it would never look at. It then linearised each tap by hand.
///
/// Two channels rather than one because a traced texel spanning a silhouette holds two surfaces and a
/// single number has to lie about one of them. Testing the crossing against the closest and the thickness
/// against the furthest is the same test the coarse cells already run one level up, and at Full resolution,
/// where the two channels are equal, it reduces to exactly the arithmetic it replaced.
/// </summary>
float2 BasisGISampleTracedDepth(float2 uv)
{
    return SAMPLE_TEXTURE2D_X_LOD(_BasisGITracedDepth, sampler_PointClamp, UnityStereoTransformScreenSpaceTex(uv), 0).rg;
}

/// <summary>Whether a traced depth texel saw any real surface at all.</summary>
bool BasisGITracedIsSky(float2 depths)
{
    return depths.g <= 0.0;
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

    // Pick the side FIRST, then reconstruct it. A ternary between two BasisGIViewPosition calls looks
    // like a choice but is not one: HLSL evaluates both arms, so four positions were being unprojected
    // to answer a question about two. Each one is an inverse view projection and a divide followed by a
    // world to view, and this runs for every shaded pixel and again for every ray hit under _BASISGI_HIT_NORMAL.
    bool useRight = diffRight < diffLeft;
    bool useUp = diffUp < diffDown;
    float3 horizontalPosition = BasisGIViewPosition(useRight ? uvRight : uvLeft, useRight ? depthRight : depthLeft);
    float3 verticalPosition = BasisGIViewPosition(useUp ? uvUp : uvDown, useUp ? depthUp : depthDown);
    float3 horizontal = useRight ? horizontalPosition - viewPosition : viewPosition - horizontalPosition;
    float3 vertical = useUp ? verticalPosition - viewPosition : viewPosition - verticalPosition;

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
    // Already unit: radius^2 + (1 - sample.x) is exactly one, and the basis is orthonormal, so the product
    // is a rotation of a unit vector. The normalize was a dot, an rsqrt and three multiplies per ray spent
    // renormalising something that was never off the sphere.
    return mul(local, basis);
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
/// Eye depth in x - zero where the pixel was sky - frames accumulated in y, and the variance of the
/// luminance the accumulation is holding in z. Zero frames means the temporal filter did not run, and the
/// spatial filter then has to treat every pixel as unresolved rather than trusting statistics that were
/// never gathered.
///
/// The depth rides here because every consumer of the statistics is already fetching this texel and every
/// one of them also wants the neighbour's depth. Reading it out of the full resolution depth texture
/// instead costs a second fetch per tap, at a stride that grows with the a-trous level and so falls out of
/// cache exactly when the filter is widest.
/// </summary>
float3 BasisGIStats(float2 uv)
{
    if (_BasisGIStatsValid < 0.5) { return float3(0.0, 0.0, 0.0); }
    float4 stats = SAMPLE_TEXTURE2D_X_LOD(_BasisGIStats, sampler_PointClamp, UnityStereoTransformScreenSpaceTex(saturate(uv)), 0);
    return float3(stats.r, stats.g, max(0.0, stats.a));
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

/// <summary>
/// The centre pixel's surface plane, in the only form the filters ever evaluate it in: how far a neighbour
/// sits off it, from that neighbour's uv offset and eye depth alone.
///
/// Under perspective every point a pixel can hold lies on one ray, p = camera + V(uv) * eyeDepth, and V
/// carries no depth term at all - so dot(normal, p) is (a*u + b*v + c) * eyeDepth, affine in uv, with the
/// same two gradients everywhere on screen. ddx/ddy of V recovers them exactly, and because V is
/// depth independent the pair survives a silhouette that would make a derivative of the world position
/// meaningless. What is left per tap is a multiply-add and a subtract, where the direct form spends an
/// inverse view projection and a divide on every neighbour.
///
/// Orthographic cameras do not fit p = camera + V * eyeDepth, and with no statistics there is no depth to
/// read, so both fall back to the direct form rather than being approximated.
/// </summary>
struct BasisGIPlaneBasis
{
    float3 gradient;
    float centre, scale;
    /// Whether the affine form describes this camera at all - it does not fit an orthographic one.
    bool usable;
    /// ...and additionally whether the statistics texture that carries the neighbours' depth exists yet.
    /// A consumer reading depth from the depth texture only needs the first.
    bool statsUsable;
};

BasisGIPlaneBasis BasisGIBuildPlaneBasis(float3 centrePosition, float3 centreNormal, float centreEye, float2 texelSize)
{
    float3 viewRay = (centrePosition - GetCameraPositionWS()) / max(centreEye, BASISGI_EPSILON);
    float3 rayDdx = ddx(viewRay), rayDdy = ddy(viewRay);
    float atCentre = dot(centreNormal, viewRay);

    BasisGIPlaneBasis basis;
    basis.gradient = float3(dot(centreNormal, rayDdx) / max(texelSize.x, BASISGI_EPSILON),
                            dot(centreNormal, rayDdy) / max(texelSize.y, BASISGI_EPSILON),
                            atCentre);
    basis.centre = atCentre * centreEye;
    basis.scale = BasisGIPlaneTolerance(centreEye);
    basis.usable = unity_OrthoParams.w < 0.5;
    basis.statsUsable = basis.usable && _BasisGIStatsValid >= 0.5;
    return basis;
}

/// <summary>How far the neighbour at uvOffset from the centre, holding eyeDepth, sits off the centre's plane.</summary>
float BasisGIPlaneDistance(BasisGIPlaneBasis basis, float2 uvOffset, float eyeDepth)
{
    return abs((basis.gradient.z + dot(basis.gradient.xy, uvOffset)) * eyeDepth - basis.centre);
}

float BasisGIDistanceFade(float eyeDepth)
{
    return saturate(1.0 - eyeDepth / max(1.0, BASISGI_FADE_DISTANCE));
}

#endif
