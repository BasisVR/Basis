#ifndef BASIS_GLOBAL_ILLUMINATION_SURFACE_INCLUDED
#define BASIS_GLOBAL_ILLUMINATION_SURFACE_INCLUDED

#include "./BasisGlobalIlluminationDepth.hlsl"

TEXTURE2D(_BaseMap);
SAMPLER(sampler_BaseMap);
TEXTURE2D(_MainTex);
SAMPLER(sampler_MainTex);

CBUFFER_START(UnityPerMaterial)
float4 _BaseMap_ST;
float4 _MainTex_ST;
float4 _BaseColor;
float4 _Color;
float _Metallic;
float _Cutoff;
float _Surface;
float _SrcBlend;
float _DstBlend;
float _AlphaClip;
float _AlphaToMask;
float _AlphaToCoverage;
float _Mode;
float _EnableUDIMDiscardOptions;
float _UDIMDiscardCompile;
float _UDIMDiscardUV;
float _UDIMDiscardRow0_0;
float _UDIMDiscardRow0_1;
float _UDIMDiscardRow0_2;
float _UDIMDiscardRow0_3;
float _UDIMDiscardRow1_0;
float _UDIMDiscardRow1_1;
float _UDIMDiscardRow1_2;
float _UDIMDiscardRow1_3;
float _UDIMDiscardRow2_0;
float _UDIMDiscardRow2_1;
float _UDIMDiscardRow2_2;
float _UDIMDiscardRow2_3;
float _UDIMDiscardRow3_0;
float _UDIMDiscardRow3_1;
float _UDIMDiscardRow3_2;
float _UDIMDiscardRow3_3;
CBUFFER_END

float4 _BasisGISurfaceTexelSize;

struct SurfaceAttributes
{
    float4 positionOS : POSITION;
    float2 uv : TEXCOORD0;
    float2 uv1 : TEXCOORD1;
    float2 uv2 : TEXCOORD2;
    float2 uv3 : TEXCOORD3;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct SurfaceVaryings
{
    float4 positionCS : SV_POSITION;
    float2 uv : TEXCOORD0;
    float2 tile : TEXCOORD1;
    UNITY_VERTEX_OUTPUT_STEREO
};

SurfaceVaryings SurfaceVert(SurfaceAttributes input)
{
    SurfaceVaryings output;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
    output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
    output.uv = input.uv;
    float channel = round(_UDIMDiscardUV);
    output.tile = channel == 1.0 ? input.uv1 : channel == 2.0 ? input.uv2 : channel == 3.0 ? input.uv3 : input.uv;
    return output;
}

bool SurfaceTileDiscarded(float2 uv)
{
    if (_EnableUDIMDiscardOptions < 0.5 && _UDIMDiscardCompile < 0.5) { return false; }
    float2 tile = floor(uv);
    if (any(tile < 0.0) || any(tile >= 4.0)) { return false; }
    float4 column = float4(tile.x == 0.0, tile.x == 1.0, tile.x == 2.0, tile.x == 3.0);
    float4 row = float4(tile.y == 0.0, tile.y == 1.0, tile.y == 2.0, tile.y == 3.0);
    float hidden = row.x * dot(float4(_UDIMDiscardRow0_0, _UDIMDiscardRow0_1, _UDIMDiscardRow0_2, _UDIMDiscardRow0_3), column)
        + row.y * dot(float4(_UDIMDiscardRow1_0, _UDIMDiscardRow1_1, _UDIMDiscardRow1_2, _UDIMDiscardRow1_3), column)
        + row.z * dot(float4(_UDIMDiscardRow2_0, _UDIMDiscardRow2_1, _UDIMDiscardRow2_2, _UDIMDiscardRow2_3), column)
        + row.w * dot(float4(_UDIMDiscardRow3_0, _UDIMDiscardRow3_1, _UDIMDiscardRow3_2, _UDIMDiscardRow3_3), column);
    return hidden > 0.001;
}

float4 SurfaceFrag(SurfaceVaryings input) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    float2 screenUv = input.positionCS.xy * _BasisGISurfaceTexelSize.xy;
    float sceneEye = BasisGISampleEyeDepth(screenUv);
    float fragmentEye = BasisGILinearEyeDepth(input.positionCS.z);
    float mismatch = abs(fragmentEye - sceneEye) / (sceneEye * 0.002 + 0.003 + fwidth(fragmentEye));
    if (mismatch > 1.0) { discard; }
    if (SurfaceTileDiscarded(input.tile)) { discard; }

    float src = round(_SrcBlend), dst = round(_DstBlend);
    if (dst == 1.0 || src == 2.0 || dst == 3.0) { discard; }
    bool blend = _Surface > 0.5 || dst != 0.0 || src == 4.0 || src == 7.0 || src == 8.0;
    bool clip = _AlphaClip > 0.5 || _AlphaToMask > 0.5 || _AlphaToCoverage > 0.5 || (abs(_Mode - 1.0) < 0.1 && _Cutoff >= 0.0);

    float4 texel = float4(1.0, 1.0, 1.0, 1.0);
    bool mainPresent = any(_MainTex_ST.xy != 0.0);
    bool basePresent = any(_BaseMap_ST.xy != 0.0);
    if (basePresent)
    {
        float4 baseTexel = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv * _BaseMap_ST.xy + _BaseMap_ST.zw);
        bool unassigned = baseTexel.r == 1.0 && baseTexel.g == 0.0 && baseTexel.b == 0.0;
        if (!unassigned) { texel = baseTexel; }
        else if (mainPresent) { texel = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv * _MainTex_ST.xy + _MainTex_ST.zw); }
    }
    else if (mainPresent)
    {
        texel = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv * _MainTex_ST.xy + _MainTex_ST.zw);
    }
    float4 tint = _BaseColor.r >= 0.0 ? _BaseColor : _Color;
    float alpha = texel.a * tint.a;
    if (clip && alpha < (_Cutoff >= 0.0 ? _Cutoff : 0.5)) { discard; }
    if (blend && alpha < 0.9) { discard; }
    return float4(saturate(texel.rgb * tint.rgb) * (1.0 - saturate(_Metallic)), 0.0);
}

#endif
