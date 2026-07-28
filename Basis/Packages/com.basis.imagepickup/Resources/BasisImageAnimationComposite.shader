Shader "Hidden/Basis/ImageAnimationComposite"
{
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

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D(_BasisImageAnimSourceTex);
        SAMPLER(sampler_BasisImageAnimSourceTex);
        float4 _BasisImageAnimSourceUvRect;
        float4 _BasisImageAnimClearColor;

        struct Attributes
        {
            uint vertexID : SV_VertexID;
        };

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float2 uv : TEXCOORD0;
        };

        Varyings Vert(Attributes input)
        {
            Varyings output;
            output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
            output.uv = GetFullScreenTriangleTexCoord(input.vertexID);
            return output;
        }

        float4 SamplePremultiplied(Varyings input) : SV_Target
        {
            float2 uv = _BasisImageAnimSourceUvRect.xy
                + input.uv * _BasisImageAnimSourceUvRect.zw;
            float4 color = SAMPLE_TEXTURE2D(
                _BasisImageAnimSourceTex,
                sampler_BasisImageAnimSourceTex,
                uv);
            color.rgb *= color.a;
            return color;
        }

        float4 SampleAlreadyPremultiplied(Varyings input) : SV_Target
        {
            float2 uv = _BasisImageAnimSourceUvRect.xy
                + input.uv * _BasisImageAnimSourceUvRect.zw;
            return SAMPLE_TEXTURE2D(
                _BasisImageAnimSourceTex,
                sampler_BasisImageAnimSourceTex,
                uv);
        }

        float4 ClearPremultiplied(Varyings input) : SV_Target
        {
            float4 color = _BasisImageAnimClearColor;
            color.rgb *= color.a;
            return color;
        }
        ENDHLSL

        Pass
        {
            Name "Source"
            Blend One Zero

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment SamplePremultiplied
            ENDHLSL
        }

        Pass
        {
            Name "Over"
            Blend One OneMinusSrcAlpha, One OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment SamplePremultiplied
            ENDHLSL
        }

        Pass
        {
            Name "Clear"
            Blend One Zero

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment ClearPremultiplied
            ENDHLSL
        }

        Pass
        {
            Name "CopyPremultiplied"
            Blend One Zero

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment SampleAlreadyPremultiplied
            ENDHLSL
        }
    }

    Fallback Off
}
