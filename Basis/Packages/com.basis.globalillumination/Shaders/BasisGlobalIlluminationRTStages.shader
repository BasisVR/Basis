Shader "Hidden/Basis/GlobalIlluminationRT"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }

        ZTest Always
        ZWrite Off
        Cull Off

        Pass
        {
            Name "BasisGIRTPrepass"
            Blend Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "./BasisGlobalIlluminationRTCommon.hlsl"

            float4 _BasisGIRtReference;
            float4 _BasisGIRtFullSize;
            int _BasisGIRtScale;

            struct FragOutput
            {
                float4 position : SV_Target0;
                float4 normal : SV_Target1;
            };

            float LoadDeviceDepth(int2 coord)
            {
                int2 clamped = clamp(coord, int2(0, 0), int2(_BasisGIRtFullSize.xy) - 1);
                return LOAD_TEXTURE2D_X(_CameraDepthTexture, clamped).r;
            }

            float3 WorldFromCoord(int2 coord, float deviceDepth)
            {
                float2 positionNDC = (float2(coord) + 0.5) * _BasisGIRtFullSize.zw;
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

            FragOutput Frag(Varyings input)
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                int2 baseCoord = int2(input.positionCS.xy) * _BasisGIRtScale;
                int2 bestCoord = baseCoord;
                float bestDepth = LoadDeviceDepth(baseCoord);

                UNITY_BRANCH
                if (_BasisGIRtScale > 1)
                {
                    float bestLinear = IsSky(bestDepth) ? 1e30 : LinearEyeDepth(bestDepth, _ZBufferParams);
                    UNITY_UNROLL
                    for (int tap = 1; tap < 4; ++tap)
                    {
                        int2 coord = baseCoord + int2(tap & 1, tap >> 1);
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

                FragOutput output;
                if (IsSky(bestDepth))
                {
                    output.position = float4(0.0, 0.0, 0.0, 0.0);
                    output.normal = float4(0.0, 0.0, 0.0, 0.0);
                    return output;
                }

                float3 positionWS = WorldFromCoord(bestCoord, bestDepth);
                float centreLinear = LinearEyeDepth(bestDepth, _ZBufferParams);

                float depthRight = LoadDeviceDepth(bestCoord + int2(1, 0));
                float depthLeft = LoadDeviceDepth(bestCoord + int2(-1, 0));
                float depthUp = LoadDeviceDepth(bestCoord + int2(0, 1));
                float depthDown = LoadDeviceDepth(bestCoord + int2(0, -1));

                float linearRight = LinearEyeDepth(depthRight, _ZBufferParams);
                float linearLeft = LinearEyeDepth(depthLeft, _ZBufferParams);
                float linearUp = LinearEyeDepth(depthUp, _ZBufferParams);
                float linearDown = LinearEyeDepth(depthDown, _ZBufferParams);

                float3 tangentX = abs(linearRight - centreLinear) < abs(linearLeft - centreLinear)
                    ? WorldFromCoord(bestCoord + int2(1, 0), depthRight) - positionWS
                    : positionWS - WorldFromCoord(bestCoord + int2(-1, 0), depthLeft);
                float3 tangentY = abs(linearUp - centreLinear) < abs(linearDown - centreLinear)
                    ? WorldFromCoord(bestCoord + int2(0, 1), depthUp) - positionWS
                    : positionWS - WorldFromCoord(bestCoord + int2(0, -1), depthDown);

                float3 normalWS = cross(tangentX, tangentY);
                float lengthSquared = dot(normalWS, normalWS);
                float3 viewVector = _BasisGIRtReference.xyz - positionWS;
                normalWS = lengthSquared < 1e-12 ? normalize(viewVector) : normalWS * rsqrt(lengthSquared);
                if (dot(normalWS, viewVector) < 0.0) { normalWS = -normalWS; }

                output.position = float4(positionWS - _BasisGIRtReference.xyz, 1.0);
                output.normal = float4(BasisGIRtEncodeNormal(normalWS), 0.0, 0.0);
                return output;
            }
            ENDHLSL
        }

        Pass
        {
            Name "BasisGIRTResolve"
            Blend Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            Texture2DArray<float4> _BasisGIRtResolveSource;

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                int slice = 0;
                #if defined(UNITY_STEREO_INSTANCING_ENABLED) || defined(UNITY_STEREO_MULTIVIEW_ENABLED)
                    slice = (int)unity_StereoEyeIndex;
                #endif
                return _BasisGIRtResolveSource.Load(int4(int2(input.positionCS.xy), slice, 0));
            }
            ENDHLSL
        }
    }

    Fallback Off
}
