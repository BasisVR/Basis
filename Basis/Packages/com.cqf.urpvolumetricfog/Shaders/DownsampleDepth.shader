Shader "Hidden/DownsampleDepth"
{
    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "DownsampleDepth"

            ZTest Always
            ZWrite Off
            Cull Off
            Blend Off
            ColorMask R

            HLSLPROGRAM

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            
            #pragma target 4.5
            #pragma editor_sync_compilation

            #pragma vertex Vert
            #pragma fragment Frag

            int _DownsampleDepthFactor;

            float Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                uint factor = max(2u, (uint)_DownsampleDepthFactor);
                uint2 sourceOrigin = uint2(input.positionCS.xy) * factor;
                float2 sourceTexelSize = _CameraDepthTexture_TexelSize.xy;
                float minDepth = 1.0;
                float maxDepth = 0.0;

                UNITY_LOOP
                for (uint y = 0; y < factor; y += 2)
                {
                    UNITY_LOOP
                    for (uint x = 0; x < factor; x += 2)
                    {
                        float2 gatherUv = float2(sourceOrigin + uint2(x + 1, y + 1)) * sourceTexelSize;
                        float4 depths = GATHER_RED_TEXTURE2D_X(_CameraDepthTexture, sampler_CameraDepthTexture, gatherUv);
                        minDepth = min(minDepth, Min3(depths.x, depths.y, min(depths.z, depths.w)));
                        maxDepth = max(maxDepth, Max3(depths.x, depths.y, max(depths.z, depths.w)));
                    }
                }

                return (uint(input.positionCS.x + input.positionCS.y) & 1) > 0 ? minDepth : maxDepth;
            }

            ENDHLSL
        }

        Pass
        {
            Name "DownsampleDepthFromExternal"

            ZTest Always
            ZWrite Off
            Cull Off
            Blend Off
            ColorMask R

            HLSLPROGRAM

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "./DownsampleDepthFromExternal.hlsl"

            #pragma editor_sync_compilation

            #pragma vertex Vert
            #pragma fragment Frag

            ENDHLSL
        }
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "DownsampleDepth"

            ZTest Always
            ZWrite Off
            Cull Off
            Blend Off
            ColorMask R

            HLSLPROGRAM

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            
            #pragma editor_sync_compilation

            #pragma vertex Vert
            #pragma fragment Frag

            int _DownsampleDepthFactor;

            float Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                uint factor = max(2u, (uint)_DownsampleDepthFactor);
                uint2 sourceOrigin = uint2(input.positionCS.xy) * factor;
                float minDepth = 1.0;
                float maxDepth = 0.0;

                UNITY_LOOP
                for (uint y = 0; y < factor; ++y)
                {
                    UNITY_LOOP
                    for (uint x = 0; x < factor; ++x)
                    {
                        float depth = LoadSceneDepth(sourceOrigin + uint2(x, y));
                        minDepth = min(minDepth, depth);
                        maxDepth = max(maxDepth, depth);
                    }
                }

                return (uint(input.positionCS.x + input.positionCS.y) & 1) > 0 ? minDepth : maxDepth;
            }

            ENDHLSL
        }

        Pass
        {
            Name "DownsampleDepthFromExternal"

            ZTest Always
            ZWrite Off
            Cull Off
            Blend Off
            ColorMask R

            HLSLPROGRAM

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "./DownsampleDepthFromExternal.hlsl"

            #pragma editor_sync_compilation

            #pragma vertex Vert
            #pragma fragment Frag

            ENDHLSL
        }
    }

    Fallback Off
}