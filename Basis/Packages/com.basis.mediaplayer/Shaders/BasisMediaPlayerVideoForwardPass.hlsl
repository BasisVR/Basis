// Basis media-player video screen forward pass.
//
// A verbatim copy of URP's UnlitForwardPass.hlsl (com.unity.render-pipelines.universal
// /Shaders) so the screen matches URP/Unlit exactly on desktop and in VR. The ONLY
// change is the letterbox branch in the fragment (marked "Basis letterbox"):
// BasisVideoMaterialOutput fits a non-16:9 source by baking the fit into _BaseMap_ST
// with scale > 1, which pushes uv outside [0,1] over the bar region; URP would
// clamp-smear the edge texel there, so we paint it black to letterbox on the fixed
// screen instead.
#ifndef BASIS_MEDIAPLAYER_VIDEO_FORWARD_PASS_INCLUDED
#define BASIS_MEDIAPLAYER_VIDEO_FORWARD_PASS_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/Shaders/UnlitInput.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Unlit.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#if defined(LOD_FADE_CROSSFADE)
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/LODCrossFade.hlsl"
#endif

struct Attributes
{
    float4 positionOS : POSITION;
    float2 uv : TEXCOORD0;

    #if defined(DEBUG_DISPLAY)
    float3 normalOS : NORMAL;
    float4 tangentOS : TANGENT;
    #endif

    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    float2 uv : TEXCOORD0;
    float fogCoord : TEXCOORD1;
    float4 positionCS : SV_POSITION;

    #if defined(DEBUG_DISPLAY)
    float3 positionWS : TEXCOORD2;
    float3 normalWS : TEXCOORD3;
    float3 viewDirWS : TEXCOORD4;
    #endif

    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

void InitializeInputData(Varyings input, out InputData inputData)
{
    inputData = (InputData)0;

    #if defined(DEBUG_DISPLAY)
    inputData.positionWS = input.positionWS;
    inputData.positionCS = input.positionCS;
    inputData.normalWS = input.normalWS;
    inputData.viewDirectionWS = input.viewDirWS;
    #else
    inputData.positionWS = float3(0, 0, 0);
    inputData.normalWS = half3(0, 0, 1);
    inputData.viewDirectionWS = half3(0, 0, 1);
    #endif
    inputData.shadowCoord = 0;
    inputData.fogCoord = 0;
    inputData.vertexLighting = half3(0, 0, 0);
    inputData.bakedGI = half3(0, 0, 0);
    inputData.normalizedScreenSpaceUV = 0;
    inputData.shadowMask = half4(1, 1, 1, 1);
}

Varyings UnlitPassVertex(Attributes input)
{
    Varyings output = (Varyings)0;

    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);

    output.positionCS = vertexInput.positionCS;
    output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
    #if defined(_FOG_FRAGMENT)
    output.fogCoord = vertexInput.positionVS.z;
    #else
    output.fogCoord = ComputeFogFactor(vertexInput.positionCS.z);
    #endif

    #if defined(DEBUG_DISPLAY)
    // normalWS and tangentWS already normalize.
    // this is required to avoid skewing the direction during interpolation
    // also required for per-vertex lighting and SH evaluation
    VertexNormalInputs normalInput = GetVertexNormalInputs(input.normalOS, input.tangentOS);
    half3 viewDirWS = GetWorldSpaceViewDir(vertexInput.positionWS);

    // already normalized from normal transform to WS.
    output.positionWS = vertexInput.positionWS;
    output.normalWS = normalInput.normalWS;
    output.viewDirWS = viewDirWS;
    #endif

    return output;
}

void UnlitPassFragment(
    Varyings input
    , out half4 outColor : SV_Target0
#ifdef _WRITE_RENDERING_LAYERS
    , out uint outRenderingLayers : SV_Target1
#endif
)
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    half2 uv = input.uv;

    // --- Basis letterbox (the only change from URP UnlitForwardPass) ---
    // The aspect fit samples outside the source over the bar region; render those
    // pixels black instead of the clamped edge texel. UNITY_SETUP_INSTANCE_ID above
    // has already run, so EncodeMeshRenderingLayer() is valid here.
    if (any(uv < 0.0h) || any(uv > 1.0h))
    {
        outColor = half4(0.0h, 0.0h, 0.0h, 1.0h);
#ifdef _WRITE_RENDERING_LAYERS
        outRenderingLayers = EncodeMeshRenderingLayer();
#endif
        return;
    }
    // --- end Basis letterbox ---

    half4 texColor = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv);
    half3 color = texColor.rgb * _BaseColor.rgb;
    half alpha = texColor.a * _BaseColor.a;

    alpha = AlphaDiscard(alpha, _Cutoff);
    color = AlphaModulate(color, alpha);

#ifdef LOD_FADE_CROSSFADE
    LODFadeCrossFade(input.positionCS);
#endif

    InputData inputData;
    InitializeInputData(input, inputData);
    SETUP_DEBUG_TEXTURE_DATA(inputData, UNDO_TRANSFORM_TEX(input.uv, _BaseMap));

#ifdef _DBUFFER
    ApplyDecalToBaseColor(input.positionCS, color);
#endif

    half4 finalColor = UniversalFragmentUnlit(inputData, color, alpha);

#if defined(_SCREEN_SPACE_OCCLUSION) && !defined(_SURFACE_TYPE_TRANSPARENT)
    float2 normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
    AmbientOcclusionFactor aoFactor = GetScreenSpaceAmbientOcclusion(normalizedScreenSpaceUV);
    finalColor.rgb *= aoFactor.directAmbientOcclusion;
#endif

    half fogFactor = 0;
#if defined(_FOG_FRAGMENT)
    bool anyFogEnabled = false;
    
    #if defined(FOG_LINEAR_KEYWORD_DECLARED)
    if (FOG_LINEAR)
        anyFogEnabled = true;
    #endif
    
    #if defined(FOG_EXP_KEYWORD_DECLARED)
    if (FOG_EXP)
        anyFogEnabled = true;
    #endif
    
    #if defined(FOG_EXP2_KEYWORD_DECLARED)
    if (FOG_EXP2)
        anyFogEnabled = true;
    #endif
    
    if (anyFogEnabled)
    {
        float viewZ = -input.fogCoord;
        float nearToFarZ = max(viewZ - _ProjectionParams.y, 0);
        fogFactor = ComputeFogFactorZ0ToFar(nearToFarZ);
    }
#else // #if defined(_FOG_FRAGMENT)
    fogFactor = input.fogCoord;
#endif // #if defined(_FOG_FRAGMENT)
    finalColor.rgb = MixFog(finalColor.rgb, fogFactor);
    finalColor.a = OutputAlpha(finalColor.a, IsSurfaceTypeTransparent());

    outColor = finalColor;

#ifdef _WRITE_RENDERING_LAYERS
    outRenderingLayers = EncodeMeshRenderingLayer();
#endif
}

#endif
