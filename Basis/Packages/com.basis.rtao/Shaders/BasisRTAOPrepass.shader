Shader "Hidden/Basis/RTAO/Prepass"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            Name "BasisRTAOPrepass"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5
            #pragma editor_sync_compilation

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.basis.rtao/Shaders/BasisRTAOCommon.hlsl"

            float4 _BasisRtaoReference;
            float4 _BasisRtaoFullSize;
            float4 _BasisRtaoComposite;
            int _BasisRtaoScale;

            #define COMPOSITE_FADE_END _BasisRtaoComposite.w

            struct FragOutput
            {
                float4 position : SV_Target0;
                float4 normal : SV_Target1;
            };

            float LoadDeviceDepth(int2 coord)
            {
                int2 clamped = clamp(coord, int2(0, 0), int2(_BasisRtaoFullSize.xy) - 1);
                return LOAD_TEXTURE2D_X(_CameraDepthTexture, clamped).r;
            }

            float3 WorldFromCoord(int2 coord, float deviceDepth)
            {
                float2 positionNDC = (float2(coord) + 0.5) * _BasisRtaoFullSize.zw;
                return ComputeWorldSpacePosition(positionNDC, deviceDepth, UNITY_MATRIX_I_VP);
            }

            bool IsSky(float deviceDepth)
            {
                #if UNITY_REVERSED_Z
                    return deviceDepth <= 0.0;
                #else
                    return deviceDepth >= 1.0;
                #endif
            }

            /// Builds one tangent from the two neighbours along a step.
            ///
            /// The obvious way to do this is to pick whichever side is nearer in depth, so the tangent never
            /// straddles a silhouette. That pick is a hard branch on a continuous quantity, and on a flat
            /// surface the two sides are equal to within the depth buffer's precision, so which one wins is
            /// decided by rounding - and it changes at whatever distance the rounding changes. The result is
            /// a hard line across a surface that has no edge anywhere on it.
            ///
            /// So blend instead of choosing. Each side is weighted by how much it looks like a step rather
            /// than a slope: on a flat surface both weights are equal and both sides agree, so the answer is
            /// the same either way and there is nothing left to flip. At a real silhouette the far side's
            /// weight collapses and this becomes the pick it used to be, but it gets there continuously.
            float3 TangentAlong(int2 bestCoord, int2 step, float3 positionWS, float centerLinear)
            {
                float depthForward = LoadDeviceDepth(bestCoord + step);
                float depthBackward = LoadDeviceDepth(bestCoord - step);

                bool skyForward = IsSky(depthForward);
                bool skyBackward = IsSky(depthBackward);

                // Sky is not a surface at a distance, so its reconstructed position is meaningless and would
                // poison the blend with an enormous vector. Zero it before it is multiplied by anything.
                float3 forward = skyForward ? float3(0.0, 0.0, 0.0)
                    : WorldFromCoord(bestCoord + step, depthForward) - positionWS;
                float3 backward = skyBackward ? float3(0.0, 0.0, 0.0)
                    : positionWS - WorldFromCoord(bestCoord - step, depthBackward);

                // Proportional to distance, with no floor under it. A max() here would be a kink, and a kink
                // at a fixed distance is exactly the artifact this function exists to avoid.
                float tolerance = 0.01 * centerLinear;

                float weightForward = skyForward ? 0.0
                    : tolerance / (tolerance + abs(LinearEyeDepth(depthForward, _ZBufferParams) - centerLinear));
                float weightBackward = skyBackward ? 0.0
                    : tolerance / (tolerance + abs(LinearEyeDepth(depthBackward, _ZBufferParams) - centerLinear));

                float sumWeight = weightForward + weightBackward;
                if (sumWeight < 1e-8)
                    return float3(0.0, 0.0, 0.0);

                return (forward * weightForward + backward * weightBackward) / sumWeight;
            }

            FragOutput Frag(Varyings input)
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                int2 baseCoord = int2(input.positionCS.xy) * _BasisRtaoScale;
                int2 bestCoord = baseCoord;
                float bestDepth = LoadDeviceDepth(baseCoord);

                UNITY_BRANCH
                if (_BasisRtaoScale > 1)
                {
                    float bestLinear = IsSky(bestDepth) ? 1e30 : LinearEyeDepth(bestDepth, _ZBufferParams);
                    UNITY_UNROLL
                    for (int t = 1; t < 4; ++t)
                    {
                        int2 coord = baseCoord + int2(t & 1, t >> 1);
                        float depth = LoadDeviceDepth(coord);
                        float linearDepth = IsSky(depth) ? 1e30 : LinearEyeDepth(depth, _ZBufferParams);
                        if (linearDepth < bestLinear)
                        {
                            bestLinear = linearDepth;
                            bestDepth = depth;
                            bestCoord = coord;
                        }
                    }
                }

                // Past the composite's fade end its output is exactly one whatever was traced, so marking
                // those pixels as having no geometry skips the trace, the accumulation and the whole blur
                // cascade for them rather than resolving an occlusion that is then multiplied by zero. The
                // block picked the nearest of its four depths, so a block straddling the boundary keeps
                // whichever part of it can still darken.
                float centerLinear = IsSky(bestDepth) ? 0.0 : LinearEyeDepth(bestDepth, _ZBufferParams);

                FragOutput output;
                if (IsSky(bestDepth) || (COMPOSITE_FADE_END > 0.0 && centerLinear >= COMPOSITE_FADE_END))
                {
                    output.position = float4(0.0, 0.0, 0.0, 0.0);
                    output.normal = float4(0.0, 0.0, 0.0, 0.0);
                    return output;
                }

                float3 positionWS = WorldFromCoord(bestCoord, bestDepth);
                float3 viewVector = _BasisRtaoReference.xyz - positionWS;

                float3 tangentX = TangentAlong(bestCoord, int2(1, 0), positionWS, centerLinear);
                float3 tangentY = TangentAlong(bestCoord, int2(0, 1), positionWS, centerLinear);

                // Normalised before the cross, so the only thing left to test is the angle between the two
                // tangents. Crossing them raw makes the result scale with the fourth power of how much world
                // a pixel covers, so a fixed floor under it is a distance test wearing a degeneracy test's
                // clothes: past about a millimetre of world per pixel every surface falls through to the view
                // vector at once, and a flat wall up close reads as a sphere.
                float lengthXSq = dot(tangentX, tangentX);
                float lengthYSq = dot(tangentY, tangentY);

                float3 normalWS;
                if (lengthXSq < 1e-30 || lengthYSq < 1e-30)
                {
                    normalWS = normalize(viewVector);
                }
                else
                {
                    normalWS = cross(tangentX * rsqrt(lengthXSq), tangentY * rsqrt(lengthYSq));
                    float lengthSq = dot(normalWS, normalWS);
                    normalWS = lengthSq < 1e-8 ? normalize(viewVector) : normalWS * rsqrt(lengthSq);
                }

                // A cross product of two tangents has an arbitrary sign, so point it back at the camera.
                if (dot(normalWS, viewVector) < 0.0)
                    normalWS = -normalWS;

                output.position = float4(positionWS - _BasisRtaoReference.xyz, 1.0);
                output.normal = float4(BasisRtaoEncodeNormal(normalWS), 0.0, 0.0);
                return output;
            }
            ENDHLSL
        }
    }

    Fallback Off
}
