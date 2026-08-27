Shader "Hidden/Basis/GlobalIllumination"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }

        ZTest Always
        ZWrite Off
        Cull Off

        Pass
        {
            Name "BasisGITrace"
            Blend Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.5
            #pragma multi_compile_local_fragment _ _BASISGI_NORMALS_TEXTURE
            #pragma multi_compile_local_fragment _ _BASISGI_FALLBACK_SKY _BASISGI_FALLBACK_PROBE
            #pragma multi_compile_local_fragment _ _BASISGI_EMITTERS
            #pragma multi_compile_local_fragment _ _BASISGI_EMITTER_OCCLUSION
            #pragma multi_compile_local_fragment _ _BASISGI_RAY_REUSE
            #pragma multi_compile_local_fragment _ _BASISGI_HIT_NORMAL

            #include "./BasisGlobalIlluminationTrace.hlsl"

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                return BasisGITrace(input.texcoord, input.positionCS.xy);
            }
            ENDHLSL
        }

        Pass
        {
            Name "BasisGITemporal"
            Blend Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.5
            #pragma multi_compile_local_fragment _ _BASISGI_NEIGHBOURHOOD_CLAMP

            #include "./BasisGlobalIlluminationDenoise.hlsl"

            BasisGITemporalOutput Frag(Varyings input)
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                return BasisGITemporal(input.texcoord);
            }
            ENDHLSL
        }

        Pass
        {
            Name "BasisGIBlur"
            Blend Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.5

            #include "./BasisGlobalIlluminationDenoise.hlsl"

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                return BasisGIBilateralBlur(input.texcoord);
            }
            ENDHLSL
        }

        Pass
        {
            Name "BasisGIComposite"
            Blend DstColor Zero, Zero One

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.5
            #pragma multi_compile_local_fragment _ _BASISGI_BILATERAL_UPSAMPLE

            #include "./BasisGlobalIlluminationComposite.hlsl"

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                return BasisGIComposite(input.texcoord);
            }
            ENDHLSL
        }

        Pass
        {
            Name "BasisGIDebug"
            Blend Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.5
            #pragma multi_compile_local_fragment _ _BASISGI_NORMALS_TEXTURE
            #pragma multi_compile_local_fragment _ _BASISGI_BILATERAL_UPSAMPLE

            #include "./BasisGlobalIlluminationComposite.hlsl"

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                return BasisGIDebug(input.texcoord);
            }
            ENDHLSL
        }

        Pass
        {
            Name "BasisGICopyColor"
            Blend Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.5

            #include "./BasisGlobalIlluminationCommon.hlsl"

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                return float4(SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, UnityStereoTransformScreenSpaceTex(input.texcoord), 0).rgb, 1.0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
