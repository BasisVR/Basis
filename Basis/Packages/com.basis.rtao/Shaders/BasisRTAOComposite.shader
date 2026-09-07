Shader "Hidden/Basis/RTAO/Composite"
{
    HLSLINCLUDE
        #pragma target 4.5
        #pragma editor_sync_compilation

        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
        #include "Packages/com.basis.rtao/Shaders/BasisRTAOCommon.hlsl"

        // Not TextureXR.hlsl: including it ahead of URP's Core.hlsl redefines the whole TEXTURE2D_X family
        // and macro expands unity_StereoEyeIndex, which then breaks its own declaration in UnityInput.hlsl.
        #if defined(UNITY_STEREO_INSTANCING_ENABLED) || defined(UNITY_STEREO_MULTIVIEW_ENABLED)
            #define BASIS_RTAO_SLICE unity_StereoEyeIndex
        #else
            #define BASIS_RTAO_SLICE 0
        #endif

        TEXTURE2D_ARRAY(_BasisRtaoAOTex);
        TEXTURE2D_ARRAY(_BasisRtaoDepthTex);

        float4 _BasisRtaoAOSize;
        float4 _BasisRtaoComposite;
        int _BasisRtaoScale;

        #define COMPOSITE_INTENSITY _BasisRtaoComposite.x
        #define COMPOSITE_POWER _BasisRtaoComposite.y
        #define COMPOSITE_FADE_START _BasisRtaoComposite.z
        #define COMPOSITE_FADE_END _BasisRtaoComposite.w

        bool IsSky(float deviceDepth)
        {
            #if UNITY_REVERSED_Z
                return deviceDepth <= 0.0;
            #else
                return deviceDepth >= 1.0;
            #endif
        }

        float ResolveVisibility(float2 positionSS, out float linearDepth)
        {
            linearDepth = 0.0;

            int2 fullCoord = int2(positionSS);
            float deviceDepth = LOAD_TEXTURE2D_X(_CameraDepthTexture, fullCoord).r;
            if (IsSky(deviceDepth))
                return 1.0;

            linearDepth = LinearEyeDepth(deviceDepth, _ZBufferParams);

            // ShapeVisibility multiplies everything past the fade end by zero and the prepass stopped
            // tracing there for the same reason, so there is nothing here to gather.
            if (COMPOSITE_FADE_END > 0.0 && linearDepth >= COMPOSITE_FADE_END)
                return 1.0;

            if (_BasisRtaoScale <= 1)
                return LOAD_TEXTURE2D_ARRAY(_BasisRtaoAOTex, fullCoord, BASIS_RTAO_SLICE).x;

            float2 coord = (float2(fullCoord) + 0.5) / float(_BasisRtaoScale) - 0.5;
            int2 baseCoord = int2(floor(coord));
            float2 frac2 = coord - float2(baseCoord);
            float weights[4] =
            {
                (1.0 - frac2.x) * (1.0 - frac2.y),
                frac2.x * (1.0 - frac2.y),
                (1.0 - frac2.x) * frac2.y,
                frac2.x * frac2.y
            };

            float sumWeight = 0.0;
            float sumVisibility = 0.0;
            float tolerance = max(0.02, 0.05 * linearDepth);

            // View depth, not the distance between two world positions. A full resolution pixel is
            // laterally offset from its trace resolution parents by construction, so weighting on the
            // whole offset docks a tap for being where it was always going to be, and picks the
            // nearest tap rather than the one on this surface. It also costs a world space
            // reconstruction and four square roots per pixel at full resolution, for a discriminator
            // that answers the question worse. Depth agreement is the question.
            UNITY_UNROLL
            for (int t = 0; t < 4; ++t)
            {
                int2 tapCoord = clamp(baseCoord + int2(t & 1, t >> 1), int2(0, 0), int2(_BasisRtaoAOSize.xy) - 1);
                float tapDepth = LOAD_TEXTURE2D_ARRAY(_BasisRtaoDepthTex, tapCoord, BASIS_RTAO_SLICE).x;
                if (tapDepth <= 0.0)
                    continue;

                float weight = weights[t] / (1.0 + abs(tapDepth - linearDepth) / tolerance);
                sumWeight += weight;
                sumVisibility += weight * LOAD_TEXTURE2D_ARRAY(_BasisRtaoAOTex, tapCoord, BASIS_RTAO_SLICE).x;
            }

            if (sumWeight < 1e-5)
                return LOAD_TEXTURE2D_ARRAY(_BasisRtaoAOTex, clamp(baseCoord, int2(0, 0), int2(_BasisRtaoAOSize.xy) - 1), BASIS_RTAO_SLICE).x;

            return sumVisibility / sumWeight;
        }

        float ShapeVisibility(float visibility, float linearDepth)
        {
            float occlusion = saturate((1.0 - visibility) * COMPOSITE_INTENSITY);
            occlusion = pow(occlusion, COMPOSITE_POWER);
            float fade = 1.0 - saturate((linearDepth - COMPOSITE_FADE_START) / max(1e-3, COMPOSITE_FADE_END - COMPOSITE_FADE_START));
            return saturate(1.0 - occlusion * fade);
        }
    ENDHLSL

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            Name "BasisRTAOComposite"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float linearDepth;
                float visibility = ResolveVisibility(input.positionCS.xy, linearDepth);
                return half4(ShapeVisibility(visibility, linearDepth), 0.0, 0.0, 1.0);
            }
            ENDHLSL
        }

        // Matches URP's own SSAO "After Opaque" mode: Blend One SrcAlpha with the occlusion in alpha resolves
        // to cameraColor *= visibility. It multiplies the finished image instead of feeding the lighting, so
        // it lands on every opaque surface whatever its shader does, and is not clamped by a material's own
        // occlusion map. Cheaper to reason about, less physically honest - it dims direct light and specular
        // that already have shadows of their own.
        Pass
        {
            Name "BasisRTAOAfterOpaque"

            ZTest Always
            ZWrite Off
            Cull Off
            Blend One SrcAlpha, Zero One
            BlendOp Add, Add

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            TEXTURE2D_X(_BasisRtaoResolvedAfterOpaqueTex);

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                // The composite already resolved and shaped this at full resolution, so there is nothing to
                // recompute; the blend does the multiply.
                half visibility = LOAD_TEXTURE2D_X(_BasisRtaoResolvedAfterOpaqueTex, int2(input.positionCS.xy)).r;
                return half4(0.0, 0.0, 0.0, visibility);
            }
            ENDHLSL
        }

        Pass
        {
            Name "BasisRTAODebugView"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            // The composited result is a TEXTURE2D_X - one texture outside VR - while every earlier stage
            // is a real array with a slice per eye. Declaring one of them as the other reads nothing at all,
            // so both are declared and the pass says which it bound.
            TEXTURE2D_X(_BasisRtaoDebugResolvedTex);
            TEXTURE2D_ARRAY(_BasisRtaoDebugStageTex);
            int _BasisRtaoDebugInterpretation;
            int _BasisRtaoDebugStageScale;
            int _BasisRtaoDebugFromStageArray;

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                // Every stage but the composited one lives at trace resolution, so step the coordinate down
                // to it. Point sampling on purpose: a bilinear tap would smooth over the very steps this
                // view exists to find.
                int2 coord = int2(input.positionCS.xy) / max(1, _BasisRtaoDebugStageScale);

                float4 packed;
                UNITY_BRANCH
                if (_BasisRtaoDebugFromStageArray != 0)
                    packed = LOAD_TEXTURE2D_ARRAY(_BasisRtaoDebugStageTex, coord, BASIS_RTAO_SLICE);
                else
                    packed = LOAD_TEXTURE2D_X(_BasisRtaoDebugResolvedTex, coord);

                if (_BasisRtaoDebugInterpretation == 1)
                {
                    // A repeating one metre gradient. Position is what the denoiser and the upscale both
                    // compare against, and a break in this ramp is a break in both of them.
                    if (packed.w < 0.5)
                        return half4(0.0, 0.0, 0.0, 1.0);
                    return half4(frac(packed.xyz), 1.0);
                }

                if (_BasisRtaoDebugInterpretation == 2)
                    return half4(BasisRtaoDecodeNormal(packed.xy) * 0.5 + 0.5, 1.0);

                half visibility = packed.r;
                return half4(visibility, visibility, visibility, 1.0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
