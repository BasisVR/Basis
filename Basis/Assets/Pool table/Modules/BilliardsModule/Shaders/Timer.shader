Shader "metaphira/URP_Timer"
{
    Properties
    {
        _Color("Color", Color) = (1,1,1,1)
        _MainTex("Albedo (RGB)", 2D) = "white" {}
        _Glossiness("Smoothness", Range(0,1)) = 0.5
        _Metallic("Metallic", Range(0,1)) = 0.0
        _TimeFrac("Time", Range(0,1)) = 0.0
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }
        LOD 200

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 positionWS  : TEXCOORD0;
                float3 normalWS    : TEXCOORD1;
                float2 uv          : TEXCOORD2;
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float _Glossiness;
                float _Metallic;
                float _TimeFrac;
            CBUFFER_END

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                VertexPositionInputs posInputs = GetVertexPositionInputs(IN.positionOS.xyz);
                VertexNormalInputs normalInputs = GetVertexNormalInputs(IN.normalOS);

                OUT.positionHCS = posInputs.positionCS;
                OUT.positionWS = posInputs.positionWS;
                OUT.normalWS = normalInputs.normalWS;
                OUT.uv = IN.uv;

                return OUT;
            }

            float4 frag(Varyings IN) : SV_Target
            {
                float4 albedoSample = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv);

                // Timer-based color modulation
                float4 colourTerm = saturate((albedoSample.r - _TimeFrac) * 12.0);
                float3 baseColor = lerp(_Color.rgb * colourTerm.r, float3(1.0, 1.0, 1.0), albedoSample.g);

                // Lighting
                float3 normalWS = normalize(IN.normalWS);
                float3 viewDirWS = normalize(_WorldSpaceCameraPos - IN.positionWS);

                // Main directional light
                Light mainLight = GetMainLight();

                float NdotL = saturate(dot(normalWS, mainLight.direction));
                float3 diffuse = baseColor * mainLight.color.rgb * NdotL;

                // Specular reflection (simple approximation)
                float3 halfwayDir = normalize(mainLight.direction + viewDirWS);
                float NdotH = saturate(dot(normalWS, halfwayDir));
                float3 specular = pow(NdotH, 32.0) * _Glossiness;

                float3 finalColor = diffuse + specular;

                // Emission
                float3 emission = colourTerm.r * (1.0 - albedoSample.g) * _Color.rgb;

                return float4(finalColor + emission, albedoSample.a);
            }

            ENDHLSL
        }
    }

    FallBack "Hidden/InternalErrorShader"
}
