Shader "metaphira/Transparent Orb"
{
    Properties
    {
        [HDR] _Color("Main Color", Color) = (1, 1, 1, 1)
        _FresnelBias("Fresnel Bias", Float) = 0
        _FresnelScale("Fresnel Scale", Float) = 1
        _FresnelPower("Fresnel Power", Float) = 1
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline"="UniversalPipeline"
            "RenderType"="Transparent"
            "Queue"="Transparent+13"
        }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
        float4 _Color;
        float _FresnelBias;
        float _FresnelScale;
        float _FresnelPower;
        CBUFFER_END
        ENDHLSL

        Pass
        {
            Tags { "LightMode" = "UniversalForward" }
            Blend One One
            ZWrite Off

            HLSLPROGRAM

            #pragma vertex vert
            #pragma fragment frag
            // GPU Instancing
            #pragma multi_compile_instancing

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normal : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float fresnel : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes IN)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(IN.positionOS);
                float3 positionWS = TransformObjectToWorld(IN.positionOS).xyz;
                float3 i = normalize(mul((float3x3)unity_WorldToObject, GetWorldSpaceViewDir(positionWS)));
                o.fresnel = _FresnelBias + _FresnelScale * pow(1 + dot(i, IN.normal), _FresnelPower);
                return o;
            }

            float4 frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                return lerp(float4(0.0, 0.0, 0.0, 0.0), _Color, saturate(1 - IN.fresnel));
            }

            ENDHLSL
        }
    }
}
