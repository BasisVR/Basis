Shader "metaphira/TableSurface"
{
   Properties
   {
      _EmissionColor ("Emission Color", Color) = (1, 1, 1, 1)
      [MainColor] _Color ("Tint Color", Color) = (1, 1, 1, 1)

      [MainTexture] _MainTex ("Albedo (RGB), TintMap(A)", 2D) = "white" {}
      _EmissionMap ("Emission Mask", 2D) = "black" {}
      _Metalic ("Metallic(R)/Smoothness(A)", 2D) = "white" {}
      [Toggle(DETAIL_CLOTH)]_UseDetailCloth ("Use Cloth Detail Texture", Range(0, 1)) = 0
      _DetailCloth ("Cloth Detail", 2D) = "white" {}
      _ClothHue("Cloth Hue", Range(0, 1)) = 0
      _ClothSaturation("Cloth Saturation", Range(0, 3)) = 1
      _DetailClothBrightness("Detail Brightness", Range(0, 2)) = 1
      _DetailClothMask ("Cloth Detail Mask", 2D) = "white" {}
      _MaskStrengthCloth("Mask Strength Cloth", Range(0, 1)) = 1
      [Toggle(DETAIL_OTHER)]_UseDetailOther ("Use Non-Cloth Detail Texture", Range(0, 1)) = 0
      _DetailOther ("Other Detail", 2D) = "white" {}
      _DetailOtherBrightness("Other Detail Brightness", Range(0, 2)) = 1
      _MaskStrengthOther("Mask Strength Other", Range(0, 1)) = 1

      _TimerPct("Timer Percentage", Range(0, 1)) = 1
   }
   SubShader
   {
      Name "Forward"
      Tags
      {
         "RenderPipeline" = "UniversalPipeline"
         "RenderType" = "Opaque"
         "Queue" = "Geometry"
         "LightMode" = "UniversalForward"
      }

      HLSLINCLUDE
      #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

      CBUFFER_START(UnityPerMaterial)
      float4 _EmissionColor;
      float3 _Color;

      float _ClothHue;
      float _ClothSaturation;
      float _MaskStrengthCloth;
      float _MaskStrengthOther;
      float _DetailClothBrightness;
      float _DetailOtherBrightness;
      float _TimerPct;
      CBUFFER_END
      ENDHLSL

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

         #pragma shader_feature DETAIL_CLOTH
         #pragma shader_feature DETAIL_OTHER

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

            float4 positionOS : TEXCOORD11; // Original position in object space

            float4 positionCS : SV_POSITION;
            UNITY_VERTEX_INPUT_INSTANCE_ID
            UNITY_VERTEX_OUTPUT_STEREO
         };

         TEXTURE2D(_MainTex);
         TEXTURE2D(_EmissionMap);
         TEXTURE2D(_Metalic);
         TEXTURE2D(_DetailCloth);
         TEXTURE2D(_DetailOther);
         TEXTURE2D(_DetailClothMask);
         TEXTURE2D(_TimerMap);

         SamplerState sampler_linear_repeat;

         float4 _DetailCloth_ST;
         float4 _DetailOther_ST;
         float4 _MainTex_ST;

         #ifdef UNITY_COLORSPACE_GAMMA
         #define unity_ColorSpaceGrey float4(0.5, 0.5, 0.5, 0.5)
         #define unity_ColorSpaceDouble float4(2.0, 2.0, 2.0, 2.0)
         #define unity_ColorSpaceDielectricSpec half4(0.220916301, 0.220916301, 0.220916301, 1.0 - 0.220916301)
         #define unity_ColorSpaceLuminance half4(0.22, 0.707, 0.071, 0.0) // Legacy: alpha is set to 0.0 to specify gamma mode
         #else // Linear values
         #define unity_ColorSpaceGrey float4(0.214041144, 0.214041144, 0.214041144, 0.5)
         #define unity_ColorSpaceDouble float4(4.59479380, 4.59479380, 4.59479380, 2.0)
         #define unity_ColorSpaceDielectricSpec half4(0.04, 0.04, 0.04, 1.0 - 0.04) // standard dielectric reflectivity coef at incident angle (= 4%)
         #define unity_ColorSpaceLuminance half4(0.0396819152, 0.458021790, 0.00609653955, 1.0) // Legacy: alpha is set to 1.0 to specify linear mode
         #endif

         static const float M_PI = 3.14159265358979323846264338327950288;

         float3 linear_srgb_to_oklab(float3 c)
         {
            float l = 0.4122214708 * c.x + 0.5363325363 * c.y + 0.0514459929 * c.z;
            float m = 0.2119034982 * c.x + 0.6806995451 * c.y + 0.1073969566 * c.z;
            float s = 0.0883024619 * c.x + 0.2817188376 * c.y + 0.6299787005 * c.z;

            float l_ = pow(l, 1.0 / 3.0);
            float m_ = pow(m, 1.0 / 3.0);
            float s_ = pow(s, 1.0 / 3.0);

            return float3(
            0.2104542553 * l_ + 0.7936177850 * m_ - 0.0040720468 * s_,
            1.9779984951 * l_ - 2.4285922050 * m_ + 0.4505937099 * s_,
            0.0259040371 * l_ + 0.7827717662 * m_ - 0.8086757660 * s_
            );
         }

         float3 oklab_to_linear_srgb(float3 c)
         {
            float l_ = c.x + 0.3963377774 * c.y + 0.2158037573 * c.z;
            float m_ = c.x - 0.1055613458 * c.y - 0.0638541728 * c.z;
            float s_ = c.x - 0.0894841775 * c.y - 1.2914855480 * c.z;

            float l = l_ * l_ * l_;
            float m = m_ * m_ * m_;
            float s = s_ * s_ * s_;

            return float3(
            + 4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
            - 1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
            - 0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s
            );
         }

         float3 hueShift(float3 color, float shift)
         {
            float3 oklab = linear_srgb_to_oklab(max(color, 0.0000000001));
            float hue = atan2(oklab.z, oklab.y);
            hue += shift * M_PI * 2; // Add the hue shift

            float chroma = length(oklab.yz);
            oklab.y = cos(hue) * chroma;
            oklab.z = sin(hue) * chroma;

            return oklab_to_linear_srgb(oklab);
         }

         float3 Unity_Saturation_float(float3 In, float Saturation)
         {
            float luma = dot(In, float3(0.2126729, 0.7151522, 0.0721750));
            float3 Out = luma.xxx + Saturation.xxx * (In - luma.xxx);
            return Out;
         }

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

         inline void InitializeStandardLitSurfaceData(Varyings IN, out SurfaceData outSurfaceData)
         {

            float4 sample_diffuse = SAMPLE_TEXTURE2D(_MainTex, sampler_linear_repeat, IN.uv);
            float4 sample_emission = SAMPLE_TEXTURE2D(_EmissionMap, sampler_linear_repeat, IN.uv);
            float4 sample_metalic = SAMPLE_TEXTURE2D(_Metalic, sampler_linear_repeat, IN.uv);
            #if defined(DETAIL_CLOTH)
            float4 sample_detail = SAMPLE_TEXTURE2D (_DetailCloth, sampler_linear_repeat, IN.uv * _DetailCloth_ST.xy + _DetailCloth_ST.wz) * unity_ColorSpaceDouble * _DetailClothBrightness;
            float sample_detailclothmask = SAMPLE_TEXTURE2D(_DetailClothMask, sampler_linear_repeat, IN.uv).r;
            float3 final = lerp(sample_diffuse.rgb, _Color * sample_diffuse.rgb * 2.0, pow(sample_diffuse.a, 0.1));
            final = lerp(final, final * sample_detail, sample_detailclothmask * _MaskStrengthCloth);
            float3 cloth = final * sample_detailclothmask;
            float3 other = final * (1 - sample_detailclothmask);
            cloth = hueShift(cloth, _ClothHue);
            cloth = Unity_Saturation_float(cloth, _ClothSaturation);
            final = cloth + other;
            #if defined(DETAIL_OTHER)
            float4 sample_detailother = SAMPLE_TEXTURE2D(_DetailOther, sampler_linear_repeat, IN.uv * _DetailOther_ST.xy + _DetailOther_ST.wz) * unity_ColorSpaceDouble * _DetailOtherBrightness;
            final = lerp(final, final * sample_detailother, (1 - sample_detailclothmask) * _MaskStrengthOther);
            #endif
            #else
            float3 final = lerp(sample_diffuse.rgb, _Color * sample_diffuse.rgb * 2.0, pow(sample_diffuse.a, 0.1));
            #endif

            float timer_pct = clamp(_TimerPct, 0, 1);
            // add a small fudge factor so that the light connects
            float surf_angle_pct = (M_PI + atan2(IN.positionOS.x, IN.positionOS.z)) / (2 * M_PI) / 1.04 + (1 - 1 / 1.04);
            float angle_cl = clamp((surf_angle_pct - timer_pct) * 40.0, 0, 1.5);

            outSurfaceData.alpha = 1;

            outSurfaceData.albedo = final;

            outSurfaceData.metallic = sample_metalic.r;
            outSurfaceData.specular = half3(0.0, 0.0, 0.0);

            outSurfaceData.smoothness = sample_metalic.a;
            outSurfaceData.normalTS = float3(0.0, 0.0, 1.0);
            outSurfaceData.occlusion = 1.0;
            outSurfaceData.emission = sample_emission * _EmissionColor * angle_cl;

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

            output.uv = TRANSFORM_TEX(input.texcoord, _MainTex);

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
            output.positionOS = input.positionOS;

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
            InitializeStandardLitSurfaceData(input, surfaceData);

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
            outColor = color;

            #ifdef _WRITE_RENDERING_LAYERS
            outRenderingLayers = EncodeMeshRenderingLayer();
            #endif
         }

         ENDHLSL
      }

      Pass {
         Name "ShadowCaster"
         Tags { "LightMode" = "ShadowCaster" }

         ZWrite On
         ZTest LEqual

         HLSLPROGRAM
         #pragma vertex ShadowPassVertex
         #pragma fragment ShadowPassFragment

         // GPU Instancing
         #pragma multi_compile_instancing
         //#pragma multi_compile _ DOTS_INSTANCING_ON

         #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
         #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"
         #include "Packages/com.unity.render-pipelines.universal/Shaders/ShadowCasterPass.hlsl"

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
