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
            #pragma multi_compile_local_fragment _ _BASISGI_HIERARCHICAL_MARCH

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
            #pragma multi_compile_local_fragment _ _BASISGI_MOTION_VECTORS

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

        // Pass 6, appended rather than inserted: the pass indices above are constants on
        // BasisGlobalIlluminationPass and reordering them silently repoints every stage.
        Pass
        {
            Name "BasisGISpecularUpsample"
            Blend Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.5
            #pragma multi_compile_local_fragment _ _BASISGI_BILATERAL_UPSAMPLE

            #include "./BasisGlobalIlluminationComposite.hlsl"

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                return BasisGISpecularResolve(input.texcoord);
            }
            ENDHLSL
        }

        Pass
        {
            Name "BasisGICoarseSeed"
            Blend Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.5

            #include "./BasisGlobalIlluminationCommon.hlsl"

            /// Reduces the full resolution depth buffer to the first level of the coarse summary, keeping
            /// the closest and furthest real surface in each block. Sky is skipped rather than clamped: a
            /// far plane would read as an ordinary distant surface and stop the march skipping open space.
            float2 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                int span = clamp((int)BASISGI_COARSE_SPAN, 1, 8);
                int2 base = int2(input.positionCS.xy) * span;
                int2 limit = int2(BASISGI_COARSE_SOURCE_SIZE) - 1;

                float closest = BASISGI_SKY_DEPTH;
                float furthest = 0.0;

                UNITY_LOOP
                for (int y = 0; y < span; y++)
                {
                    UNITY_LOOP
                    for (int x = 0; x < span; x++)
                    {
                        float raw = LOAD_TEXTURE2D_X(_CameraDepthTexture, min(base + int2(x, y), limit)).r;
                        if (BasisGIIsSky(raw)) { continue; }
                        float eye = BasisGILinearEyeDepth(raw);
                        closest = min(closest, eye);
                        furthest = max(furthest, eye);
                    }
                }

                return float2(closest, furthest);
            }
            ENDHLSL
        }

        Pass
        {
            Name "BasisGICoarseReduce"
            Blend Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.5

            #include "./BasisGlobalIlluminationCommon.hlsl"

            /// Folds one level of the coarse summary into the next. Closest stays a minimum and furthest
            /// stays a maximum, so both remain true of every texel underneath however many times it folds.
            float2 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                int span = clamp((int)BASISGI_COARSE_SPAN, 1, 8);
                int2 base = int2(input.positionCS.xy) * span;
                int2 limit = int2(BASISGI_COARSE_SOURCE_SIZE) - 1;

                float closest = BASISGI_SKY_DEPTH;
                float furthest = 0.0;

                UNITY_LOOP
                for (int y = 0; y < span; y++)
                {
                    UNITY_LOOP
                    for (int x = 0; x < span; x++)
                    {
                        float2 tap = LOAD_TEXTURE2D_X(_BasisGICoarseDepth, min(base + int2(x, y), limit)).rg;
                        closest = min(closest, tap.r);
                        furthest = max(furthest, tap.g);
                    }
                }

                return float2(closest, furthest);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
