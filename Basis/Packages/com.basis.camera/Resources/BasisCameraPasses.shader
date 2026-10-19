Shader "Hidden/Basis/Camera/Passes"
{
    Properties
    {
        _BasisPassDepthRange ("Depth Range", Float) = 10
        _BasisPassData ("Data", Float) = 0
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

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/GlobalSamplers.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

        float _BasisPassDepthRange;
        float _BasisPassData;
        float4 _BasisPassTargetSize;
        float4x4 _BasisPassWorldToView;

        float BasisPassEyeDepth(float rawDepth)
        {
        #if UNITY_REVERSED_Z
            float orthographic = lerp(_ProjectionParams.z, _ProjectionParams.y, rawDepth);
        #else
            float orthographic = lerp(_ProjectionParams.y, _ProjectionParams.z, rawDepth);
        #endif
            return unity_OrthoParams.w < 0.5 ? LinearEyeDepth(rawDepth, _ZBufferParams) : orthographic;
        }

        float BasisPassRawDepth(float2 uv)
        {
            return SAMPLE_TEXTURE2D_X_LOD(_CameraDepthTexture, sampler_PointClamp, UnityStereoTransformScreenSpaceTex(uv), 0).r;
        }

        bool BasisPassIsSky(float rawDepth)
        {
        #if UNITY_REVERSED_Z
            return rawDepth <= 0.0;
        #else
            return rawDepth >= 1.0;
        #endif
        }

        float4 BasisPassDisplay(float3 encoded)
        {
            return float4(SRGBToLinear(saturate(encoded)), 1.0);
        }

        float3 BasisPassHue(float hue)
        {
            float3 rgb = saturate(abs(frac(hue + float3(0.0, 2.0 / 3.0, 1.0 / 3.0)) * 6.0 - 3.0) - 1.0);
            return rgb;
        }
        ENDHLSL

        Pass
        {
            Name "BasisCameraPassDepth"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.5
            #pragma editor_sync_compilation

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float rawDepth = BasisPassRawDepth(input.texcoord);
                float eye = BasisPassEyeDepth(rawDepth);
                if (_BasisPassData > 0.5) { return float4(eye, eye, eye, 1.0); }
                float closeness = BasisPassIsSky(rawDepth) ? 0.0 : 1.0 - saturate(eye / max(_BasisPassDepthRange, 1e-4));
                return BasisPassDisplay(closeness.xxx);
            }
            ENDHLSL
        }

        Pass
        {
            Name "BasisCameraPassNormals"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.5
            #pragma editor_sync_compilation
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareNormalsTexture.hlsl"

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float rawDepth = BasisPassRawDepth(input.texcoord);
                float3 normalWS = SampleSceneNormals(input.texcoord);
                float length2 = dot(normalWS, normalWS);
                if (BasisPassIsSky(rawDepth) || length2 < 1e-6) { return float4(0.0, 0.0, 0.0, 1.0); }
                float3 normalVS = normalize(mul((float3x3)_BasisPassWorldToView, normalWS * rsqrt(length2)));
                if (_BasisPassData > 0.5) { return float4(normalVS, 1.0); }
                return BasisPassDisplay(normalVS * 0.5 + 0.5);
            }
            ENDHLSL
        }

        Pass
        {
            Name "BasisCameraPassMotion"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.5
            #pragma editor_sync_compilation

            TEXTURE2D_X(_MotionVectorTexture);

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 velocity = SAMPLE_TEXTURE2D_X_LOD(_MotionVectorTexture, sampler_PointClamp, UnityStereoTransformScreenSpaceTex(input.texcoord), 0).xy;
                float2 pixels = velocity * _BasisPassTargetSize.xy;
                if (_BasisPassData > 0.5) { return float4(pixels, 0.0, 1.0); }
                float speed = saturate(length(pixels) / max(_BasisPassTargetSize.x * 0.025, 1.0));
                float hue = atan2(pixels.y, pixels.x) * (0.5 * INV_PI) + 0.5;
                return BasisPassDisplay(lerp(float3(1.0, 1.0, 1.0), BasisPassHue(hue), speed));
            }
            ENDHLSL
        }

        Pass
        {
            Name "BasisCameraPassMaskResolve"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.5
            #pragma editor_sync_compilation

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float coverage = saturate(SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, input.texcoord, 0).r);
                if (_BasisPassData > 0.5) { return float4(coverage, coverage, coverage, 1.0); }
                return BasisPassDisplay(coverage.xxx);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
