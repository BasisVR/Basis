Shader "metaphira/Timer"
{
    Properties
    {
        [MainColor] _Color ("Color", Color) = (1, 1, 1, 1)
        [MainTexture] _BaseMap ("Albedo (RGB)", 2D) = "white" {}
        _Glossiness ("Smoothness", Range(0, 1)) = 0.5
        _Metallic ("Metallic", Range(0, 1)) = 0.0
        _TimeFrac("Time", Range(0, 1)) = 0.0
    }
    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "UniversalMaterialType" = "Lit"
        }
        LOD 300

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
        half _Glossiness;
        half _Metallic;
        float4 _Color;
        float _TimeFrac;
        CBUFFER_END
        ENDHLSL

        // CGPROGRAM
        // #pragma surface surf Standard fullforwardshadows
        // #pragma target 3.0

        // sampler2D _BaseMap;

        // struct Input
        // {
            // float2 uv_BaseMap;
        // };

        // half _Glossiness;
        // half _Metallic;
        // float4 _Color;
        // float _TimeFrac;

        // void surf (Input IN, inout SurfaceOutputStandard o)
        // {
            // float4 c = tex2D (_BaseMap, IN.uv_BaseMap);

            // float4 colourterm = clamp((c.r - _TimeFrac) * 12.0, 0.0, 1.0);

            // o.Albedo = lerp(_Color * colourterm, float4(1.0, 1.0, 1.0, 1.0), c.g);

            // o.Metallic = _Metallic;
            // o.Smoothness = _Glossiness;
            // o.Alpha = c.a;
            // o.Emission = colourterm * (1.0 - c.g) * _Color;
        // }
        // ENDCG

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex LitPassVertex
            #pragma fragment MyLitPassFragment

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ EVALUATE_SH_MIXED EVALUATE_SH_VERTEX
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_ATLAS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _SCREEN_SPACE_IRRADIANCE
            #pragma multi_compile_fragment _ _DBUFFER_MRT1 _DBUFFER_MRT2 _DBUFFER_MRT3
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP

            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ProbeVolumeVariants.hlsl"

            #if defined(_PARALLAXMAP)
            #define REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR
            #endif

            #if (defined(_NORMALMAP) || (defined(_PARALLAXMAP) && !defined(REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR))) || defined(_DETAIL)
            #define REQUIRES_WORLD_SPACE_TANGENT_INTERPOLATOR
            #endif

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 tangentOS : TANGENT;
                float2 texcoord : TEXCOORD0;
                float2 staticLightmapUV : TEXCOORD1;
                float2 dynamicLightmapUV : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float2 uv : TEXCOORD0;

                #if defined(REQUIRES_WORLD_SPACE_POS_INTERPOLATOR)
                float3 positionWS : TEXCOORD1;
                #endif

                float3 normalWS : TEXCOORD2;
                #if defined(REQUIRES_WORLD_SPACE_TANGENT_INTERPOLATOR)
                half4 tangentWS : TEXCOORD3; // xyz : tangent, w : sign
                #endif

                #ifdef _ADDITIONAL_LIGHTS_VERTEX
                half4 fogFactorAndVertexLight : TEXCOORD5; // x : fogFactor, yzw : vertex light
                #else
                half fogFactor : TEXCOORD5;
                #endif

                #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                float4 shadowCoord : TEXCOORD6;
                #endif

                #if defined(REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR)
                half3 viewDirTS : TEXCOORD7;
                #endif

                DECLARE_LIGHTMAP_OR_SH(staticLightmapUV, vertexSH, 8);
                #ifdef DYNAMICLIGHTMAP_ON
                float2 dynamicLightmapUV : TEXCOORD9; // Dynamic lightmap UVs
                #endif

                #ifdef USE_APV_PROBE_OCCLUSION
                float4 probeOcclusion : TEXCOORD10;
                #endif

                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            float4 _BaseMap_ST;

            half4 SampleAlbedoAlpha(float2 uv, TEXTURE2D_PARAM(albedoAlphaMap, sampler_albedoAlphaMap))
            {
                return half4(SAMPLE_TEXTURE2D(albedoAlphaMap, sampler_albedoAlphaMap, uv));
            }

            void InitializeInputData(Varyings input, half3 normalTS, out InputData inputData)
            {
                inputData = (InputData)0;

                #if defined(REQUIRES_WORLD_SPACE_POS_INTERPOLATOR)
                inputData.positionWS = input.positionWS;
                #endif

                #if defined(DEBUG_DISPLAY)
                inputData.positionCS = input.positionCS;
                #endif

                half3 viewDirWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                #if defined(_NORMALMAP) || defined(_DETAIL)
                float sgn = input.tangentWS.w; // should be either + 1 or - 1
                float3 bitangent = sgn * cross(input.normalWS.xyz, input.tangentWS.xyz);
                half3x3 tangentToWorld = half3x3(input.tangentWS.xyz, bitangent.xyz, input.normalWS.xyz);

                #if defined(_NORMALMAP)
                inputData.tangentToWorld = tangentToWorld;
                #endif
                inputData.normalWS = TransformTangentToWorld(normalTS, tangentToWorld);
                #else
                inputData.normalWS = input.normalWS;
                #endif

                inputData.normalWS = NormalizeNormalPerPixel(inputData.normalWS);
                inputData.viewDirectionWS = viewDirWS;

                #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                inputData.shadowCoord = input.shadowCoord;
                #elif defined(MAIN_LIGHT_CALCULATE_SHADOWS)
                inputData.shadowCoord = TransformWorldToShadowCoord(inputData.positionWS);
                #else
                inputData.shadowCoord = float4(0, 0, 0, 0);
                #endif
                #ifdef _ADDITIONAL_LIGHTS_VERTEX
                inputData.fogCoord = InitializeInputDataFog(float4(input.positionWS, 1.0), input.fogFactorAndVertexLight.x);
                inputData.vertexLighting = input.fogFactorAndVertexLight.yzw;
                #else
                inputData.fogCoord = InitializeInputDataFog(float4(input.positionWS, 1.0), input.fogFactor);
                #endif

                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);

                #if defined(DEBUG_DISPLAY)
                #if defined(DYNAMICLIGHTMAP_ON)
                inputData.dynamicLightmapUV = input.dynamicLightmapUV;
                #endif
                #if defined(LIGHTMAP_ON)
                inputData.staticLightmapUV = input.staticLightmapUV;
                #else
                inputData.vertexSH = input.vertexSH;
                #endif
                #if defined(USE_APV_PROBE_OCCLUSION)
                inputData.probeOcclusion = input.probeOcclusion;
                #endif
                #endif
            }

            void InitializeBakedGIData(Varyings input, inout InputData inputData)
            {
                #if defined(_SCREEN_SPACE_IRRADIANCE)
                inputData.bakedGI = SAMPLE_GI(_ScreenSpaceIrradiance, input.positionCS.xy);
                #elif defined(DYNAMICLIGHTMAP_ON)
                inputData.bakedGI = SAMPLE_GI(input.staticLightmapUV, input.dynamicLightmapUV, input.vertexSH, inputData.normalWS);
                inputData.shadowMask = SAMPLE_SHADOWMASK(input.staticLightmapUV);
                #elif !defined(LIGHTMAP_ON) && (defined(PROBE_VOLUMES_L1) || defined(PROBE_VOLUMES_L2))
                inputData.bakedGI = SAMPLE_GI(input.vertexSH,
                GetAbsolutePositionWS(inputData.positionWS),
                inputData.normalWS,
                inputData.viewDirectionWS,
                input.positionCS.xy,
                input.probeOcclusion,
                inputData.shadowMask);
                #else
                inputData.bakedGI = SAMPLE_GI(input.staticLightmapUV, input.vertexSH, inputData.normalWS);
                inputData.shadowMask = SAMPLE_SHADOWMASK(input.staticLightmapUV);
                #endif
            }

            inline void InitializeStandardLitSurfaceData(float2 uv, out SurfaceData outSurfaceData)
            {
                half4 albedoAlpha = SampleAlbedoAlpha(uv, TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap));
                outSurfaceData.alpha = saturate(1 - albedoAlpha.g);

                float4 colourterm = clamp((albedoAlpha.r - _TimeFrac) * 12.0, 0.0, 1.0);
                outSurfaceData.albedo = lerp(_Color * colourterm, float4(1.0, 1.0, 1.0, 1.0), albedoAlpha.g);
                outSurfaceData.albedo = AlphaModulate(outSurfaceData.albedo, outSurfaceData.alpha);

                outSurfaceData.metallic = _Metallic;
                outSurfaceData.specular = half3(0.0, 0.0, 0.0);

                outSurfaceData.smoothness = _Glossiness;
                outSurfaceData.normalTS = float3(0.0, 0.0, 1.0);
                outSurfaceData.occlusion = 1.0;
                outSurfaceData.emission = colourterm * (1.0 - albedoAlpha.g) * _Color;

                outSurfaceData.clearCoatMask = half(0.0);
                outSurfaceData.clearCoatSmoothness = half(0.0);
            }

            // Used in Standard (Physically Based) shader
            Varyings LitPassVertex(Attributes input)
            {
                Varyings output = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);

                // normalWS and tangentWS already normalize.
                // this is required to avoid skewing the direction during interpolation
                // also required for per - vertex lighting and SH evaluation
                VertexNormalInputs normalInput = GetVertexNormalInputs(input.normalOS, input.tangentOS);

                half3 vertexLight = VertexLighting(vertexInput.positionWS, normalInput.normalWS);

                half fogFactor = 0;
                #if !defined(_FOG_FRAGMENT)
                fogFactor = ComputeFogFactor(vertexInput.positionCS.z);
                #endif

                output.uv = TRANSFORM_TEX(input.texcoord, _BaseMap);

                // already normalized from normal transform to WS.
                output.normalWS = normalInput.normalWS;
                #if defined(REQUIRES_WORLD_SPACE_TANGENT_INTERPOLATOR) || defined(REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR)
                real sign = input.tangentOS.w * GetOddNegativeScale();
                half4 tangentWS = half4(normalInput.tangentWS.xyz, sign);
                #endif
                #if defined(REQUIRES_WORLD_SPACE_TANGENT_INTERPOLATOR)
                output.tangentWS = tangentWS;
                #endif

                #if defined(REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR)
                half3 viewDirWS = GetWorldSpaceNormalizeViewDir(vertexInput.positionWS);
                half3 viewDirTS = GetViewDirectionTangentSpace(tangentWS, output.normalWS, viewDirWS);
                output.viewDirTS = viewDirTS;
                #endif

                OUTPUT_LIGHTMAP_UV(input.staticLightmapUV, unity_LightmapST, output.staticLightmapUV);
                #ifdef DYNAMICLIGHTMAP_ON
                output.dynamicLightmapUV = input.dynamicLightmapUV.xy * unity_DynamicLightmapST.xy + unity_DynamicLightmapST.zw;
                #endif
                OUTPUT_SH4(vertexInput.positionWS, output.normalWS.xyz, GetWorldSpaceNormalizeViewDir(vertexInput.positionWS), output.vertexSH, output.probeOcclusion);
                #ifdef _ADDITIONAL_LIGHTS_VERTEX
                output.fogFactorAndVertexLight = half4(fogFactor, vertexLight);
                #else
                output.fogFactor = fogFactor;
                #endif

                #if defined(REQUIRES_WORLD_SPACE_POS_INTERPOLATOR)
                output.positionWS = vertexInput.positionWS;
                #endif

                #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                output.shadowCoord = GetShadowCoord(vertexInput);
                #endif

                output.positionCS = vertexInput.positionCS;

                return output;
            }

            // Custom URP LitForwardPass
            void MyLitPassFragment(
            Varyings input
            , out half4 outColor : SV_Target0
            #ifdef _WRITE_RENDERING_LAYERS
            , out uint outRenderingLayers : SV_Target1
            #endif
            )
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                #if defined(_PARALLAXMAP)
                #if defined(REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR)
                half3 viewDirTS = input.viewDirTS;
                #else
                half3 viewDirWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                half3 viewDirTS = GetViewDirectionTangentSpace(input.tangentWS, input.normalWS, viewDirWS);
                #endif
                ApplyPerPixelDisplacement(viewDirTS, input.uv);
                #endif

                SurfaceData surfaceData;
                InitializeStandardLitSurfaceData(input.uv, surfaceData);

                #ifdef LOD_FADE_CROSSFADE
                LODFadeCrossFade(input.positionCS);
                #endif

                InputData inputData;
                InitializeInputData(input, surfaceData.normalTS, inputData);
                SETUP_DEBUG_TEXTURE_DATA(inputData, UNDO_TRANSFORM_TEX(input.uv, _BaseMap));

                #if defined(_DBUFFER)
                ApplyDecalToSurfaceData(input.positionCS, surfaceData, inputData);
                #endif

                InitializeBakedGIData(input, inputData);

                half4 color = UniversalFragmentPBR(inputData, surfaceData);
                color.rgb = MixFog(color.rgb, inputData.fogCoord);
                if (color.a < 0.9) discard;
                outColor = color;

                #ifdef _WRITE_RENDERING_LAYERS
                outRenderingLayers = EncodeMeshRenderingLayer();
                #endif
            }

            ENDHLSL
        }

        Pass {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ColorMask 0
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex DepthOnlyVertex
            #pragma fragment DepthOnlyFragment

            // GPU Instancing
            #pragma multi_compile_instancing
            //#pragma multi_compile _ DOTS_INSTANCING_ON

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/DepthOnlyPass.hlsl"

            ENDHLSL
        }

        Pass {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex DepthNormalsVertex
            #pragma fragment DepthNormalsFragment

            // GPU Instancing
            #pragma multi_compile_instancing
            //#pragma multi_compile _ DOTS_INSTANCING_ON

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/DepthNormalsPass.hlsl"

            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
