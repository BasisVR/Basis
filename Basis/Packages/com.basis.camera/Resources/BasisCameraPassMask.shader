Shader "Hidden/Basis/Camera/PassMask"
{
    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Opaque"
        }

        Pass
        {
            Name "BasisCameraPassMask"
            Blend Off
            ColorMask R
            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma vertex MaskVert
            #pragma fragment MaskFrag
            #pragma target 3.5
            #pragma editor_sync_compilation
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/GlobalSamplers.hlsl"

            float4 _BasisPassMaskTexel;

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

            float MaskEyeDepth(float rawDepth)
            {
            #if UNITY_REVERSED_Z
                float orthographic = lerp(_ProjectionParams.z, _ProjectionParams.y, rawDepth);
            #else
                float orthographic = lerp(_ProjectionParams.y, _ProjectionParams.z, rawDepth);
            #endif
                return unity_OrthoParams.w < 0.5 ? LinearEyeDepth(rawDepth, _ZBufferParams) : orthographic;
            }

            float MaskSceneEye(float2 uv)
            {
                return MaskEyeDepth(SAMPLE_TEXTURE2D_X_LOD(_CameraDepthTexture, sampler_PointClamp, UnityStereoTransformScreenSpaceTex(uv), 0).r);
            }

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
                float2 uv = input.positionCS.xy * _BasisPassMaskTexel.xy;
                float fragmentEye = MaskEyeDepth(input.positionCS.z);
                float tolerance = fragmentEye * 0.01 + 0.005;
                if (fragmentEye > MaskSceneEye(uv) + tolerance) { discard; }

                float nearest = abs(MaskSceneEye(uv) - fragmentEye);
                UNITY_UNROLL
                for (int y = -1; y <= 1; y++)
                {
                    UNITY_UNROLL
                    for (int x = -1; x <= 1; x++)
                    {
                        nearest = min(nearest, abs(MaskSceneEye(uv + float2(x, y) * _BasisPassMaskTexel.xy) - fragmentEye));
                    }
                }
                if (nearest > tolerance) { discard; }
                return 1.0;
            }
            ENDHLSL
        }
    }

    Fallback Off
}
