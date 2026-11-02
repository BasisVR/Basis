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
CBUFFER_END

float4 _BasisGISurfaceTexelSize;

struct SurfaceAttributes
{
    float4 positionOS : POSITION;
    float2 uv : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct SurfaceVaryings
{
    float4 positionCS : SV_POSITION;
    float2 uv : TEXCOORD0;
    UNITY_VERTEX_OUTPUT_STEREO
};

SurfaceVaryings SurfaceVert(SurfaceAttributes input)
{
    SurfaceVaryings output;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
    output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
    output.uv = input.uv;
    return output;
}

float4 SurfaceFrag(SurfaceVaryings input, out float closeness : SV_Depth) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    float2 screenUv = input.positionCS.xy * _BasisGISurfaceTexelSize.xy;
    float sceneEye = BasisGISampleEyeDepth(screenUv);
    float fragmentEye = BasisGILinearEyeDepth(input.positionCS.z);
    float mismatch = abs(fragmentEye - sceneEye) / (sceneEye * 0.002 + 0.003);
    if (mismatch > 1.0) { discard; }
#if UNITY_REVERSED_Z
    closeness = 1.0 - mismatch;
#else
    closeness = mismatch;
#endif

    float3 texel = float3(1.0, 1.0, 1.0);
    bool mainPresent = any(_MainTex_ST.xy != 0.0);
    bool basePresent = any(_BaseMap_ST.xy != 0.0);
    if (basePresent)
    {
        float3 baseTexel = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv * _BaseMap_ST.xy + _BaseMap_ST.zw).rgb;
        bool unassigned = baseTexel.r == 1.0 && baseTexel.g == 0.0 && baseTexel.b == 0.0;
        if (!unassigned) { texel = baseTexel; }
        else if (mainPresent) { texel = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv * _MainTex_ST.xy + _MainTex_ST.zw).rgb; }
    }
    else if (mainPresent)
    {
        texel = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv * _MainTex_ST.xy + _MainTex_ST.zw).rgb;
    }
    float3 tint = _BaseColor.a > 0.0 ? _BaseColor.rgb : (_Color.a > 0.0 ? _Color.rgb : float3(1.0, 1.0, 1.0));
    return float4(saturate(texel * tint) * (1.0 - saturate(_Metallic)), 0.0);
}

#endif
