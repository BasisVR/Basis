#ifndef BASIS_GLOBAL_ILLUMINATION_LIGHTMAP_MASK_INCLUDED
#define BASIS_GLOBAL_ILLUMINATION_LIGHTMAP_MASK_INCLUDED

// The lightmap receive mask's entry points. In their own header rather than inline in the pass so the
// offline compile check builds the exact code the pass ships - including the DOTS_INSTANCING_ON variant
// that decides whether the BatchRendererGroup draws this pass at all, which is precisely the variant a
// batchmode import will not exercise. Includes the depth-only header on purpose: Blit.hlsl cannot sit in
// a geometry pass (see BasisGlobalIlluminationDepth.hlsl).

#include "./BasisGlobalIlluminationDepth.hlsl"

float4 _BasisGITracedTexelSize;
/// Negative in production. At zero or above, every fragment this pass keeps writes THIS value instead of
/// the LIGHTMAP_ON split - which is what lets a test prove the draw, the frontmost test, the sample
/// alignment and the composite's receive arithmetic end to end in an environment where a runtime-assigned
/// lightmapIndex may not drive the keyword at all. See BasisGlobalIlluminationPass.LightmapMaskForcedValue.
float _BasisGILightmapMaskForce;

struct MaskAttributes
{
    float4 positionOS : POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct MaskVaryings
{
    float4 positionCS : SV_POSITION;
    UNITY_VERTEX_OUTPUT_STEREO
};

MaskVaryings MaskVert(MaskAttributes input)
{
    MaskVaryings output;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
    output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
    return output;
}

float MaskFrag(MaskVaryings input) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    // There is no depth attachment at traced resolution to test against, so the frontmost test is done by
    // hand against the camera depth. One sided on purpose: a fragment clearly BEHIND what the depth buffer
    // recorded is someone else's pixel and says nothing; a fragment at or in front of it speaks. The slack
    // covers the traced texel holding a different full resolution texel than the one sampled here.
    float2 uv = input.positionCS.xy * _BasisGITracedTexelSize.xy;
    float sceneEye = BasisGISampleEyeDepth(uv);
    float fragmentEye = BasisGILinearEyeDepth(input.positionCS.z);
    if (fragmentEye > sceneEye * 1.02 + 0.02) { discard; }
    if (_BasisGILightmapMaskForce >= 0.0) { return saturate(_BasisGILightmapMaskForce); }
#if defined(LIGHTMAP_ON)
    return 0.0;
#else
    return 1.0;
#endif
}

#endif
