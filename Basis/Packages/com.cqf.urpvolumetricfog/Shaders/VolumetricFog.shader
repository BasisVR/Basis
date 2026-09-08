Shader "Hidden/VolumetricFog"
{
    Properties
    {
        [HideInInspector] _VFSrcBlend ("Src Blend", Float) = 1
        [HideInInspector] _VFDstBlend ("Dst Blend", Float) = 0
        [HideInInspector] _VFSrcBlendAlpha ("Src Blend Alpha", Float) = 1
        [HideInInspector] _VFDstBlendAlpha ("Dst Blend Alpha", Float) = 0
    }

    SubShader
    {
        Tags
        { 
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "VolumetricFogRender"

            ZTest Always
            ZWrite Off
            Cull Off
            Blend [_VFSrcBlend] [_VFDstBlend], [_VFSrcBlendAlpha] [_VFDstBlendAlpha]

            HLSLPROGRAM

            #include "./VolumetricFog.hlsl"

           // #pragma enable_d3d11_debug_symbols
            // URP realtime additional (point/spot) lights are intentionally not supported: their
            // Forward+ cluster light loop (_CLUSTER_LIGHT_LOOP + _ADDITIONAL_LIGHT_SHADOWS) overflows
            // the D3D11 FXC register allocator on build (issue #25). The fog is lit by the main light
            // + APV + optional LTCGI instead, none of which need that loop.
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
#if UNITY_VERSION >= 202310
            #pragma multi_compile_fragment _ PROBE_VOLUMES_L1 PROBE_VOLUMES_L2
#endif

            #pragma multi_compile_local_fragment _ _MAIN_LIGHT_CONTRIBUTION_DISABLED
            #pragma multi_compile_local_fragment _ _APV_CONTRIBUTION_ENABLED
            // When set (alongside _APV_CONTRIBUTION_ENABLED), APV is read from a pre-baked world-space
            // 3D texture (one trilinear tap) instead of being evaluated live; needs no PROBE_VOLUMES data.
            #pragma multi_compile_local_fragment _ _APV_BAKED

            #pragma vertex Vert
            #pragma fragment Frag

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                return VolumetricFog(input.texcoord, input.positionCS.xy);
            }

            ENDHLSL
        }

        Pass
        {
            Name "VolumetricFogHorizontalBlur"
            
            ZTest Always
            ZWrite Off
            Cull Off
            Blend Off

            HLSLPROGRAM

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "./DepthAwareGaussianBlur.hlsl"

            #pragma vertex Vert
            #pragma fragment Frag

#if UNITY_VERSION < 202320
            float4 _BlitTexture_TexelSize;
#endif

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                return DepthAwareGaussianBlur(input.texcoord, float2(1.0, 0.0), _BlitTexture, sampler_LinearClamp, _BlitTexture_TexelSize.xy);
            }

            ENDHLSL
        }

        Pass
        {
            Name "VolumetricFogVerticalBlur"
            
            ZTest Always
            ZWrite Off
            Cull Off
            Blend Off

            HLSLPROGRAM

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "./DepthAwareGaussianBlur.hlsl"

            #pragma vertex Vert
            #pragma fragment Frag

#if UNITY_VERSION < 202320
            float4 _BlitTexture_TexelSize;
#endif

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                return DepthAwareGaussianBlur(input.texcoord, float2(0.0, 1.0), _BlitTexture, sampler_LinearClamp, _BlitTexture_TexelSize.xy);
            }

            ENDHLSL
        }

        Pass
        {
            Name "VolumetricFogUpsampleComposition"

            ZTest Always
            ZWrite Off
            Cull Off
            // Drawn straight onto the (possibly multisampled) camera color: dst * transmittance + fog.
            Blend One SrcAlpha, Zero One

            HLSLPROGRAM

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "./DepthAwareUpsample.hlsl"

            #pragma target 4.5

            #pragma vertex Vert
            #pragma fragment Frag

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                return DepthAwareUpsample(input.texcoord, _BlitTexture);
            }

            ENDHLSL
        }

        Pass
        {
            Name "VolumetricFogComposition"

            ZTest Always
            ZWrite Off
            Cull Off
            Blend One SrcAlpha, Zero One

            HLSLPROGRAM

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            #pragma vertex Vert
            #pragma fragment Frag

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                return LOAD_TEXTURE2D_X(_BlitTexture, uint2(input.positionCS.xy));
            }

            ENDHLSL
        }

        Pass
        {
            Name "VolumetricFogTemporal"

            ZTest Always
            ZWrite Off
            Cull Off
            Blend Off

            HLSLPROGRAM

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "./VolumetricFogTemporal.hlsl"

            #pragma vertex Vert
            #pragma fragment Frag

            TemporalOutput Frag(Varyings input)
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                return VolumetricFogTemporalResolve(input.texcoord, input.positionCS.xy);
            }

            ENDHLSL
        }

        Pass
        {
            Name "VolumetricFogFroxelApply"

            ZTest Always
            ZWrite Off
            Cull Off
            Blend One SrcAlpha, Zero One

            HLSLPROGRAM

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/GlobalSamplers.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "./VolumetricFogFroxel.hlsl"

            #pragma vertex Vert
            #pragma fragment Frag

            TEXTURE3D(_VFFroxelVolume);
            float4x4 _VFFroxelViewProj;
            float _VFFroxelShared;

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float rawDepth = SampleSceneDepth(input.texcoord);
                float viewDepth = LinearEyeDepth(rawDepth, _ZBufferParams);
                float2 fogUV = input.texcoord;
                uint viewIndex = unity_StereoEyeIndex;

                UNITY_BRANCH
                if (_VFFroxelShared > 0.5)
                {
#if !UNITY_REVERSED_Z
                    rawDepth = lerp(UNITY_NEAR_CLIP_VALUE, 1.0, rawDepth);
#endif
                    float3 posWS = ComputeWorldSpacePosition(input.texcoord, rawDepth, UNITY_MATRIX_I_VP);
                    float4 fogClip = mul(_VFFroxelViewProj, float4(posWS, 1.0));
                    fogUV = ComputeNormalizedDeviceCoordinates(posWS, _VFFroxelViewProj);
                    viewDepth = fogClip.w;
                    viewIndex = 0u;
                }

                return SAMPLE_TEXTURE3D_LOD(_VFFroxelVolume, sampler_LinearClamp, FroxelIntegratedUVW(fogUV, viewDepth, viewIndex), 0.0);
            }

            ENDHLSL
        }
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
        }

        UsePass "Hidden/VolumetricFog/VOLUMETRICFOGRENDER"

        UsePass "Hidden/VolumetricFog/VOLUMETRICFOGHORIZONTALBLUR"

        UsePass "Hidden/VolumetricFog/VOLUMETRICFOGVERTICALBLUR"

        Pass
        {
            Name "VolumetricFogUpsampleComposition"

            ZTest Always
            ZWrite Off
            Cull Off
            Blend One SrcAlpha, Zero One

            HLSLPROGRAM

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "./DepthAwareUpsample.hlsl"

            #pragma vertex Vert
            #pragma fragment Frag

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                return DepthAwareUpsample(input.texcoord, _BlitTexture);
            }

            ENDHLSL
        }

        UsePass "Hidden/VolumetricFog/VOLUMETRICFOGCOMPOSITION"

        UsePass "Hidden/VolumetricFog/VOLUMETRICFOGTEMPORAL"

        UsePass "Hidden/VolumetricFog/VOLUMETRICFOGFROXELAPPLY"
    }

    Fallback Off
}
