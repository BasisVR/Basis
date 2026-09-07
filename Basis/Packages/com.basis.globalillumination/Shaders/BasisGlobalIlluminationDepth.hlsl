#ifndef BASIS_GLOBAL_ILLUMINATION_DEPTH_INCLUDED
#define BASIS_GLOBAL_ILLUMINATION_DEPTH_INCLUDED

// The depth helpers alone, importable by a pass that DRAWS GEOMETRY. BasisGlobalIlluminationCommon.hlsl
// includes Blit.hlsl for the fullscreen passes, and Blit.hlsl cannot be included by a geometry pass: it
// defines its own Vert, colliding with the vertex entry such a pass must declare, and under
// DOTS_INSTANCING_ON its include order leaves EntityLighting.hlsl calling SAMPLE_TEXTURE2D_ARRAY without
// its slice argument, which fails to compile - and a BatchRendererGroup then skips every draw. Both were
// hit live by the first lightmap mask pass (2026-08-29); this split is the prescribed fix.

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/GlobalSamplers.hlsl"

#define BASISGI_EPSILON 1e-5

/// <summary>
/// Eye depth from a raw depth sample.
///
/// The orthographic form is behind a branch rather than lerped in. Both are cheap on their own, but this
/// is the single most repeated line in the effect - every march step, every refine step, every filter tap -
/// and the lerp form pays for the reciprocal AND the far/near interpolation on every one of them. The
/// branch is on a uniform, so it is perfectly coherent across the whole draw and costs nothing to take.
/// </summary>
float BasisGILinearEyeDepth(float rawDepth)
{
    UNITY_BRANCH
    if (unity_OrthoParams.w < 0.5) { return LinearEyeDepth(rawDepth, _ZBufferParams); }
#if UNITY_REVERSED_Z
    return lerp(_ProjectionParams.z, _ProjectionParams.y, rawDepth);
#else
    return lerp(_ProjectionParams.y, _ProjectionParams.z, rawDepth);
#endif
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

#endif
