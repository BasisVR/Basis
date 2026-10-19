Shader "Hidden/Basis/Camera/PassCompose"
{
    Properties
    {
        _MainTex ("Picture", 2D) = "black" {}
        _BasisPassDepthTex ("Depth", 2D) = "black" {}
        _BasisPassNormalsTex ("Normals", 2D) = "black" {}
        _BasisPassMaskTex ("Mask", 2D) = "black" {}
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Opaque"
        }

        Cull Off
        ZWrite Off
        ZTest Always
        Blend Off

        Pass
        {
            Name "BasisCameraPassCompose"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma editor_sync_compilation

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/GlobalSamplers.hlsl"

            TEXTURE2D(_MainTex);
            TEXTURE2D(_BasisPassDepthTex);
            TEXTURE2D(_BasisPassNormalsTex);
            TEXTURE2D(_BasisPassMaskTex);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                return output;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                float2 cell = step(0.5, input.uv);
                float2 uv = input.uv * 2.0 - cell;
                float3 colour;
                if (cell.y > 0.5)
                {
                    colour = cell.x < 0.5 ? SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_LinearClamp, uv, 0).rgb : SAMPLE_TEXTURE2D_LOD(_BasisPassDepthTex, sampler_LinearClamp, uv, 0).rgb;
                }
                else
                {
                    colour = cell.x < 0.5 ? SAMPLE_TEXTURE2D_LOD(_BasisPassNormalsTex, sampler_LinearClamp, uv, 0).rgb : SAMPLE_TEXTURE2D_LOD(_BasisPassMaskTex, sampler_LinearClamp, uv, 0).rgb;
                }
                return float4(colour, 1.0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
