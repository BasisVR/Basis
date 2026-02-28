Shader "AudioLink/Surface/AudioReactiveSurface"
{
    Properties
    {
        [KeywordEnum(Opaque,Cutout,Transparent)] _Surface("Surface Type", Int) = 0
        [Enum(UnityEngine.Rendering.CullMode)] _Cull("Cull", Float) = 2
        [Spacer(5f)]
        _MainTex("Albedo", 2D) = "white" {}
        _Color("Color", Color) = (0.4980392,0.4980392,0.4980392,1)
        _Metallic("Metallic", Range( 0 , 1)) = 0
        _Smoothness("Smoothness", Range( 0 , 1)) = 0.5
        _BumpMap("Normal Map", 2D) = "bump" {}
        _BumpScale("Normal Scale", Float) = 1
        _EmissionMap("Emission Map", 2D) = "white" {}
        [HDR]_EmissionColor("Emission Color", Color) = (0,0,0,1)
        _Emission("Emission Scale", Float) = 1

        [Header(Audio Section)]
        [IntRange]_Band("Band", Range( 0 , 3)) = 0
        _Delay("Delay", Range( 0 , 1)) = 0

        [Header(Pulse Across UVs)]
        _Pulse("Pulse", Range( 0 , 1)) = 0
        _AudioHueShift("Audio Hue Shift", Float) = 0
        _PulseRotation("Pulse Rotation", Range( 0 , 360)) = 0

        [Header(Transparency)]
        _Cutoff("Cutout Threshold", Range(0.0, 1.0)) = 0.5

        // Managed via shader gui
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend("Src Blend", Float) = 1
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend("Dst Blend", Float) = 0
        [Enum(Off,0,On,1)] _ZWrite("ZWrite", Float) = 1
        [Enum(UnityEngine.Rendering.ShadowCastingMode)] _CastShadows("Cast Shadows", Float) = 1

    }

    SubShader
    {
        Tags
        {
            "RenderType"="Opaque"
            "Queue"="Geometry"
            "RenderPipeline"="UniversalPipeline"
        }

        Blend [_SrcBlend] [_DstBlend]
        ZWrite [_ZWrite]
        Cull [_Cull]

        HLSLINCLUDE
        float3 HSVToRGB(float3 c)
        {
            float4 K = float4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
            float3 p = abs(frac(c.xxx + K.xyz) * 6.0 - K.www);
            return c.z * lerp(K.xxx, saturate(p - K.xxx), c.y);
        }

        float3 RGBToHSV(float3 c)
        {
            float4 K = float4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
            float4 p = lerp(float4(c.bg, K.wz), float4(c.gb, K.xy), step(c.b, c.g));
            float4 q = lerp(float4(p.xyw, c.r), float4(c.r, p.yzx), step(p.x, c.r));
            float d = q.x - min(q.w, q.y);
            float e = 1.0e-10;
            return float3(abs(q.z + (q.w - q.y) / (6.0 * d + e)), d / (q.x + e), q.x);
        }
        ENDHLSL

        Pass
        {
            Name "UniversalForward"
            Tags
            {
                "LightMode"="UniversalForward"
            }

            HLSLPROGRAM
            #pragma shader_feature_local _UNIVERSAL_RENDER_PIPELINE
            #pragma shader_feature_local _SURFACE_OPAQUE _SURFACE_CUTOUT _SURFACE_TRANSPARENT
            #pragma multi_compile_instancing
            #pragma multi_compile __ LOD_FADE_CROSSFADE
            #pragma multi_compile_fog
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile _ _SHADOWS_SOFT
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED

            #if defined(_UNIVERSAL_RENDER_PIPELINE)
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/SpaceTransforms.hlsl"
            #include "Packages/com.llealloo.audiolink/Runtime/Shaders/AudioLink.cginc"
            #endif

            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 tangentOS : TANGENT;
                float3 normalOS : NORMAL;
                float2 texcoord : TEXCOORD0;
                float2 lightmapUV : TEXCOORD1;
                #if defined(_UNIVERSAL_RENDER_PIPELINE)
                UNITY_VERTEX_INPUT_INSTANCE_ID
                #endif
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                #if _UNIVERSAL_RENDER_PIPELINE
                DECLARE_LIGHTMAP_OR_SH(lightmapUV, vertexSH, 1);
                #endif
                float3 positionWS : TEXCOORD2;
                float3 normalWS : TEXCOORD3;
                float3 tangentWS : TEXCOORD4;
                float3 bitangentWS : TEXCOORD5;
                float4 shadowCoord : TEXCOORD6;
                float fogCoord : TEXCOORD7;
                #if defined(_UNIVERSAL_RENDER_PIPELINE)
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
                #endif
            };

            #if defined(_UNIVERSAL_RENDER_PIPELINE)
            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            TEXTURE2D(_BumpMap);
            SAMPLER(sampler_BumpMap);
            TEXTURE2D(_EmissionMap);
            SAMPLER(sampler_EmissionMap);

            UNITY_INSTANCING_BUFFER_START(UnityPerMaterial)
                UNITY_DEFINE_INSTANCED_PROP(float4, _MainTex_ST)
                UNITY_DEFINE_INSTANCED_PROP(float4, _Color)
                UNITY_DEFINE_INSTANCED_PROP(float4, _BumpMap_ST)
                UNITY_DEFINE_INSTANCED_PROP(float, _BumpScale)
                UNITY_DEFINE_INSTANCED_PROP(float4, _EmissionMap_ST)
                UNITY_DEFINE_INSTANCED_PROP(float4, _EmissionColor)
                UNITY_DEFINE_INSTANCED_PROP(float, _Metallic)
                UNITY_DEFINE_INSTANCED_PROP(float, _Smoothness)
                UNITY_DEFINE_INSTANCED_PROP(float, _AudioHueShift)
                UNITY_DEFINE_INSTANCED_PROP(float, _Band)
                UNITY_DEFINE_INSTANCED_PROP(float, _PulseRotation)
                UNITY_DEFINE_INSTANCED_PROP(float, _Pulse)
                UNITY_DEFINE_INSTANCED_PROP(float, _Delay)
                UNITY_DEFINE_INSTANCED_PROP(float, _Emission)
                UNITY_DEFINE_INSTANCED_PROP(float, _Cutoff)
            UNITY_INSTANCING_BUFFER_END(UnityPerMaterial)
            #endif

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                #if defined(_UNIVERSAL_RENDER_PIPELINE)
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normalInput = GetVertexNormalInputs(input.normalOS, input.tangentOS);

                output.positionCS = vertexInput.positionCS;
                output.positionWS = vertexInput.positionWS;
                output.uv = TRANSFORM_TEX(input.texcoord, _MainTex);

                output.normalWS = normalInput.normalWS;
                output.tangentWS = normalInput.tangentWS;
                output.bitangentWS = normalInput.bitangentWS;

                OUTPUT_LIGHTMAP_UV(input.lightmapUV, unity_LightmapST, output.lightmapUV);
                OUTPUT_SH(output.normalWS.xyz, output.vertexSH);

                output.shadowCoord = GetShadowCoord(vertexInput);
                output.fogCoord = ComputeFogFactor(output.positionCS.z);
                #endif
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                #if defined(_UNIVERSAL_RENDER_PIPELINE)
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                #ifdef LOD_FADE_CROSSFADE
                LODDitheringTransition(input.positionCS.xyz, unity_LODFade.x);
                #endif

                // Sample textures
                float4 albedoAlpha = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);
                float4 baseColor = UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _Color);

                // AudioLink calculations
                float2 texCoord = input.uv;
                float3 hsvColor = RGBToHSV((albedoAlpha * baseColor).rgb);

                // Alpha testing
                float finalAlpha = 1;
                #ifndef _SURFACE_OPAQUE
                finalAlpha = albedoAlpha.a * baseColor.a;
                float cutoff = UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _Cutoff);
                clip(finalAlpha - cutoff);
                #endif

                float hueShift = lerp(0, UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _AudioHueShift), AudioLinkIsAvailable());
                float band = UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _Band);
                float pulseRotation = UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _PulseRotation);
                float pulse = UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _Pulse);
                float delay = UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _Delay);

                // Rotation calculation
                float2 centeredUV = texCoord - 0.5;
                float rotRad = radians(pulseRotation);
                float cosRot = cos(rotRad);
                float sinRot = sin(rotRad);
                float normalizer = 1.0 / (abs(cosRot) + abs(sinRot));

                float2 rotatedUV = float2(
                    (centeredUV.x * cosRot * normalizer + centeredUV.y * sinRot * normalizer) + 0.5,
                    (centeredUV.y * cosRot * normalizer - centeredUV.x * sinRot * normalizer) + 0.5
                );

                float finalDelay = ((delay + (rotatedUV.x * pulse - 0.0) * (1.0 - delay) / (1.0 - 0.0)) % 1.0) * 127;
                float amplitude = lerp(1, AudioLinkLerp(ALPASS_AUDIOLINK + float2(finalDelay, (int)band)).r, AudioLinkIsAvailable());

                float3 finalAlbedo = HSVToRGB(float3(hsvColor.x + (hueShift * amplitude), hsvColor.y, hsvColor.z));

                // Normal mapping
                float4 bumpMapST = UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _BumpMap_ST);
                float2 bumpUV = texCoord * bumpMapST.xy + bumpMapST.zw;
                float4 normalMap = SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, bumpUV);
                float bumpScale = UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _BumpScale);
                float3 normalTS = UnpackNormalScale(normalMap, bumpScale);

                // Transform normal to world space
                float3 normalWS = TransformTangentToWorld(normalTS, half3x3(input.tangentWS, input.bitangentWS, input.normalWS));
                normalWS = normalize(normalWS);

                // Emission
                float4 emissionMapST = UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _EmissionMap_ST);
                float2 emissionUV = texCoord * emissionMapST.xy + emissionMapST.zw;
                float4 emissionColor = UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _EmissionColor);
                float emissionStrength = UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _Emission);

                float3 emissionHSV = RGBToHSV((SAMPLE_TEXTURE2D(_EmissionMap, sampler_EmissionMap, emissionUV) * emissionColor * amplitude).rgb);
                float3 finalEmission = HSVToRGB(float3(emissionHSV.x + (hueShift * amplitude), emissionHSV.y, emissionHSV.z));

                // Surface data
                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.normalWS = normalWS;
                inputData.viewDirectionWS = normalize(_WorldSpaceCameraPos - input.positionWS);
                inputData.shadowCoord = input.shadowCoord;
                inputData.fogCoord = input.fogCoord;
                inputData.vertexLighting = half3(0, 0, 0);
                inputData.bakedGI = SAMPLE_GI(input.lightmapUV, input.vertexSH, inputData.normalWS);

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = finalAlbedo;
                surfaceData.metallic = UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _Metallic);
                surfaceData.specular = half3(0.0h, 0.0h, 0.0h);
                surfaceData.smoothness = UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _Smoothness);
                surfaceData.normalTS = normalTS;
                surfaceData.emission = finalEmission * emissionStrength;
                surfaceData.occlusion = 1.0;
                surfaceData.alpha = 1;

                half4 color = UniversalFragmentPBR(inputData, surfaceData);
                color.rgb = MixFog(color.rgb, inputData.fogCoord);
                color.a = finalAlpha;

                return color;
                #else
                return 0;
                #endif
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags
            {
                "LightMode"="ShadowCaster"
            }
            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma shader_feature_local _UNIVERSAL_RENDER_PIPELINE
            #pragma shader_feature_local _SURFACE_OPAQUE _SURFACE_CUTOUT _SURFACE_TRANSPARENT
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #if defined(_UNIVERSAL_RENDER_PIPELINE)
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            #endif

            #if defined(_UNIVERSAL_RENDER_PIPELINE)
            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            TEXTURE2D(_BumpMap);
            SAMPLER(sampler_BumpMap);
            TEXTURE2D(_EmissionMap);
            SAMPLER(sampler_EmissionMap);

            UNITY_INSTANCING_BUFFER_START(UnityPerMaterial)
                UNITY_DEFINE_INSTANCED_PROP(float4, _MainTex_ST)
                UNITY_DEFINE_INSTANCED_PROP(float4, _Color)
                UNITY_DEFINE_INSTANCED_PROP(float4, _BumpMap_ST)
                UNITY_DEFINE_INSTANCED_PROP(float, _BumpScale)
                UNITY_DEFINE_INSTANCED_PROP(float4, _EmissionMap_ST)
                UNITY_DEFINE_INSTANCED_PROP(float4, _EmissionColor)
                UNITY_DEFINE_INSTANCED_PROP(float, _Metallic)
                UNITY_DEFINE_INSTANCED_PROP(float, _Smoothness)
                UNITY_DEFINE_INSTANCED_PROP(float, _AudioHueShift)
                UNITY_DEFINE_INSTANCED_PROP(float, _Band)
                UNITY_DEFINE_INSTANCED_PROP(float, _PulseRotation)
                UNITY_DEFINE_INSTANCED_PROP(float, _Pulse)
                UNITY_DEFINE_INSTANCED_PROP(float, _Delay)
                UNITY_DEFINE_INSTANCED_PROP(float, _Emission)
                UNITY_DEFINE_INSTANCED_PROP(float, _Cutoff)
            UNITY_INSTANCING_BUFFER_END(UnityPerMaterial)
            #endif

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                #if defined(_UNIVERSAL_RENDER_PIPELINE)
                UNITY_VERTEX_INPUT_INSTANCE_ID
                #endif
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                #if defined(_UNIVERSAL_RENDER_PIPELINE)
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
                #endif
            };

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                #if defined(_UNIVERSAL_RENDER_PIPELINE)
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);

                output.positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, _MainLightPosition.xyz));
                output.uv = input.uv;
                #if UNITY_REVERSED_Z
                output.positionCS.z = min(output.positionCS.z, output.positionCS.w * UNITY_NEAR_CLIP_VALUE);
                #else
                output.positionCS.z = max(output.positionCS.z, output.positionCS.w * UNITY_NEAR_CLIP_VALUE);
                #endif

                #endif
                return output;
            }

            half4 frag(Varyings input) : SV_TARGET
            {
                #if defined(_UNIVERSAL_RENDER_PIPELINE)
                #ifndef _SURFACE_OPAQUE
                // Sample main texture for alpha
                float4 albedoAlpha = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);
                float4 baseColor = UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _Color);
                float finalAlpha = albedoAlpha.a * baseColor.a;
                float cutoff = UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _Cutoff);
                clip(finalAlpha - cutoff);
                #endif
                #endif
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags
            {
                "LightMode"="DepthOnly"
            }
            ZWrite On
            ColorMask 0

            HLSLPROGRAM
            #pragma shader_feature_local _UNIVERSAL_RENDER_PIPELINE
            #pragma shader_feature_local _SURFACE_OPAQUE _SURFACE_CUTOUT _SURFACE_TRANSPARENT
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #if defined(_UNIVERSAL_RENDER_PIPELINE)
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #endif

            #if defined(_UNIVERSAL_RENDER_PIPELINE)
            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            TEXTURE2D(_BumpMap);
            SAMPLER(sampler_BumpMap);
            TEXTURE2D(_EmissionMap);
            SAMPLER(sampler_EmissionMap);

            UNITY_INSTANCING_BUFFER_START(UnityPerMaterial)
                UNITY_DEFINE_INSTANCED_PROP(float4, _MainTex_ST)
                UNITY_DEFINE_INSTANCED_PROP(float4, _Color)
                UNITY_DEFINE_INSTANCED_PROP(float4, _BumpMap_ST)
                UNITY_DEFINE_INSTANCED_PROP(float, _BumpScale)
                UNITY_DEFINE_INSTANCED_PROP(float4, _EmissionMap_ST)
                UNITY_DEFINE_INSTANCED_PROP(float4, _EmissionColor)
                UNITY_DEFINE_INSTANCED_PROP(float, _Metallic)
                UNITY_DEFINE_INSTANCED_PROP(float, _Smoothness)
                UNITY_DEFINE_INSTANCED_PROP(float, _AudioHueShift)
                UNITY_DEFINE_INSTANCED_PROP(float, _Band)
                UNITY_DEFINE_INSTANCED_PROP(float, _PulseRotation)
                UNITY_DEFINE_INSTANCED_PROP(float, _Pulse)
                UNITY_DEFINE_INSTANCED_PROP(float, _Delay)
                UNITY_DEFINE_INSTANCED_PROP(float, _Emission)
                UNITY_DEFINE_INSTANCED_PROP(float, _Cutoff)
            UNITY_INSTANCING_BUFFER_END(UnityPerMaterial)
            #endif


            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                #if defined(_UNIVERSAL_RENDER_PIPELINE)
                UNITY_VERTEX_INPUT_INSTANCE_ID
                #endif
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                #if defined(_UNIVERSAL_RENDER_PIPELINE)
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
                #endif
            };

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                #if defined(_UNIVERSAL_RENDER_PIPELINE)
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                #endif
                return output;
            }

            half4 frag(Varyings input) : SV_TARGET
            {
                #if defined(_UNIVERSAL_RENDER_PIPELINE)
                #ifndef _SURFACE_OPAQUE
                float4 albedoAlpha = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);
                float4 baseColor = UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _Color);
                float finalAlpha = albedoAlpha.a * baseColor.a;
                float cutoff = UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _Cutoff);
                clip(finalAlpha - cutoff);
                #endif
                #endif
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags
            {
                "LightMode"="DepthNormals"
            }
            ZWrite On

            HLSLPROGRAM
            #pragma shader_feature_local _UNIVERSAL_RENDER_PIPELINE
            #pragma shader_feature_local _SURFACE_OPAQUE _SURFACE_CUTOUT _SURFACE_TRANSPARENT
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #if defined(_UNIVERSAL_RENDER_PIPELINE)
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #endif

            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 tangentOS : TANGENT;
                float3 normalOS : NORMAL;
                float2 texcoord : TEXCOORD0;
                #if defined(_UNIVERSAL_RENDER_PIPELINE)
                UNITY_VERTEX_INPUT_INSTANCE_ID
                #endif
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 tangentWS : TEXCOORD2;
                float3 bitangentWS : TEXCOORD3;
                #if defined(_UNIVERSAL_RENDER_PIPELINE)
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
                #endif
            };


            #if defined(_UNIVERSAL_RENDER_PIPELINE)
            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            TEXTURE2D(_BumpMap);
            SAMPLER(sampler_BumpMap);
            TEXTURE2D(_EmissionMap);
            SAMPLER(sampler_EmissionMap);

            UNITY_INSTANCING_BUFFER_START(UnityPerMaterial)
                UNITY_DEFINE_INSTANCED_PROP(float4, _MainTex_ST)
                UNITY_DEFINE_INSTANCED_PROP(float4, _Color)
                UNITY_DEFINE_INSTANCED_PROP(float4, _BumpMap_ST)
                UNITY_DEFINE_INSTANCED_PROP(float, _BumpScale)
                UNITY_DEFINE_INSTANCED_PROP(float4, _EmissionMap_ST)
                UNITY_DEFINE_INSTANCED_PROP(float4, _EmissionColor)
                UNITY_DEFINE_INSTANCED_PROP(float, _Metallic)
                UNITY_DEFINE_INSTANCED_PROP(float, _Smoothness)
                UNITY_DEFINE_INSTANCED_PROP(float, _AudioHueShift)
                UNITY_DEFINE_INSTANCED_PROP(float, _Band)
                UNITY_DEFINE_INSTANCED_PROP(float, _PulseRotation)
                UNITY_DEFINE_INSTANCED_PROP(float, _Pulse)
                UNITY_DEFINE_INSTANCED_PROP(float, _Delay)
                UNITY_DEFINE_INSTANCED_PROP(float, _Emission)
                UNITY_DEFINE_INSTANCED_PROP(float, _Cutoff)
            UNITY_INSTANCING_BUFFER_END(UnityPerMaterial)
            #endif

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                #if defined(_UNIVERSAL_RENDER_PIPELINE)
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normalInput = GetVertexNormalInputs(input.normalOS, input.tangentOS);

                output.positionCS = vertexInput.positionCS;
                output.uv = input.texcoord;
                output.normalWS = normalInput.normalWS;
                output.tangentWS = normalInput.tangentWS;
                output.bitangentWS = normalInput.bitangentWS;
                #endif
                return output;
            }

            half4 frag(Varyings input) : SV_TARGET
            {
                #if defined(_UNIVERSAL_RENDER_PIPELINE)
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                #ifndef _SURFACE_OPAQUE
                float4 albedoAlpha = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);
                float4 baseColor = UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _Color);
                float finalAlpha = albedoAlpha.a * baseColor.a;
                float cutoff = UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _Cutoff);
                clip(finalAlpha - cutoff);
                #endif

                float4 bumpMapST = UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _BumpMap_ST);
                float2 bumpUV = input.uv * bumpMapST.xy + bumpMapST.zw;
                float bumpScale = UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _BumpScale);

                float4 normalMap = SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, bumpUV);
                float3 normalTS = UnpackNormalScale(normalMap, bumpScale);

                // Transform normal from tangent space to world space
                float3 normalWS = TransformTangentToWorld(normalTS, half3x3(input.tangentWS, input.bitangentWS, input.normalWS));
                normalWS = NormalizeNormalPerPixel(normalWS);

                return half4(normalWS, 0.0);
                #else
                return 0;
                #endif
            }
            ENDHLSL
        }


        Pass
        {
            Name "Meta"
            Tags
            {
                "LightMode"="Meta"
            }
            Cull Off

            HLSLPROGRAM
            #pragma shader_feature_local _UNIVERSAL_RENDER_PIPELINE
            #pragma shader_feature_local _SURFACE_OPAQUE _SURFACE_CUTOUT _SURFACE_TRANSPARENT
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #if defined(_UNIVERSAL_RENDER_PIPELINE)
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/MetaInput.hlsl"
            #include "Packages/com.llealloo.audiolink/Runtime/Shaders/AudioLink.cginc"
            #endif

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 texcoord : TEXCOORD0;
                float2 texcoord1 : TEXCOORD1;
                float2 texcoord2 : TEXCOORD2;
                #if defined(_UNIVERSAL_RENDER_PIPELINE)
                UNITY_VERTEX_INPUT_INSTANCE_ID
                #endif
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                #if defined(_UNIVERSAL_RENDER_PIPELINE)
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
                #endif
            };

            #if defined(_UNIVERSAL_RENDER_PIPELINE)
            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            TEXTURE2D(_EmissionMap);
            SAMPLER(sampler_EmissionMap);

            UNITY_INSTANCING_BUFFER_START(UnityPerMaterial)
                UNITY_DEFINE_INSTANCED_PROP(float4, _MainTex_ST)
                UNITY_DEFINE_INSTANCED_PROP(float4, _Color)
                UNITY_DEFINE_INSTANCED_PROP(float4, _EmissionMap_ST)
                UNITY_DEFINE_INSTANCED_PROP(float4, _EmissionColor)
                UNITY_DEFINE_INSTANCED_PROP(float, _AudioHueShift)
                UNITY_DEFINE_INSTANCED_PROP(float, _Band)
                UNITY_DEFINE_INSTANCED_PROP(float, _PulseRotation)
                UNITY_DEFINE_INSTANCED_PROP(float, _Pulse)
                UNITY_DEFINE_INSTANCED_PROP(float, _Delay)
                UNITY_DEFINE_INSTANCED_PROP(float, _Emission)
                UNITY_DEFINE_INSTANCED_PROP(float, _Cutoff)
            UNITY_INSTANCING_BUFFER_END(UnityPerMaterial)
            #endif

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                #if defined(_UNIVERSAL_RENDER_PIPELINE)
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                output.positionCS = MetaVertexPosition(input.positionOS, input.texcoord1, input.texcoord2, unity_LightmapST, unity_DynamicLightmapST);
                output.uv = TRANSFORM_TEX(input.texcoord, _MainTex);
                #endif
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                #if defined(_UNIVERSAL_RENDER_PIPELINE)
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                // AudioLink calculations (same as forward pass)
                float2 texCoord = input.uv;
                float4 albedoAlpha = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, texCoord);
                float4 baseColor = UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _Color);

                #ifndef _SURFACE_OPAQUE
                float finalAlpha = albedoAlpha.a * baseColor.a;
                float cutoff = UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _Cutoff);
                clip(finalAlpha - cutoff);
                #endif

                float3 hsvColor = RGBToHSV((albedoAlpha * baseColor).rgb);

                float hueShift = lerp(0, UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _AudioHueShift), AudioLinkIsAvailable());
                float band = UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _Band);
                float pulseRotation = UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _PulseRotation);
                float pulse = UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _Pulse);
                float delay = UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _Delay);

                // Rotation calculation
                float2 centeredUV = texCoord - 0.5;
                float rotRad = radians(pulseRotation);
                float cosRot = cos(rotRad);
                float sinRot = sin(rotRad);
                float normalizer = 1.0 / (abs(cosRot) + abs(sinRot));

                float2 rotatedUV = float2(
                    (centeredUV.x * cosRot * normalizer + centeredUV.y * sinRot * normalizer) + 0.5,
                    (centeredUV.y * cosRot * normalizer - centeredUV.x * sinRot * normalizer) + 0.5
                );

                float finalDelay = ((delay + (rotatedUV.x * pulse - 0.0) * (1.0 - delay) / (1.0 - 0.0)) % 1.0) * 128.0;
                float amplitude = lerp(1, AudioLinkLerp(ALPASS_AUDIOLINK + float2(finalDelay, (int)band)).r, AudioLinkIsAvailable());

                float3 finalAlbedo = HSVToRGB(float3(hsvColor.x + (hueShift * amplitude), hsvColor.y, hsvColor.z));

                // Emission
                float4 emissionMapST = UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _EmissionMap_ST);
                float2 emissionUV = texCoord * emissionMapST.xy + emissionMapST.zw;
                float4 emissionColor = UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _EmissionColor);
                float emissionStrength = UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _Emission);

                float3 emissionHSV = RGBToHSV((SAMPLE_TEXTURE2D(_EmissionMap, sampler_EmissionMap, emissionUV) * emissionColor * amplitude).rgb);
                float3 finalEmission = HSVToRGB(float3(emissionHSV.x + (hueShift * amplitude), emissionHSV.y, emissionHSV.z));

                MetaInput metaInput;
                metaInput.Albedo = finalAlbedo;
                metaInput.Emission = finalEmission * emissionStrength;

                return MetaFragment(metaInput);
                #else
                return 0;
                #endif
            }
            ENDHLSL
        }
    }


    SubShader
    {
        Tags
        {
            "RenderType"="Opaque" "Queue"="Geometry"
        }

        Blend [_SrcBlend] [_DstBlend]
        ZWrite [_ZWrite]
        Cull [_Cull]

        CGINCLUDE
        float3 HSVToRGB(float3 c)
        {
            float4 K = float4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
            float3 p = abs(frac(c.xxx + K.xyz) * 6.0 - K.www);
            return c.z * lerp(K.xxx, saturate(p - K.xxx), c.y);
        }

        float3 RGBToHSV(float3 c)
        {
            float4 K = float4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
            float4 p = lerp(float4(c.bg, K.wz), float4(c.gb, K.xy), step(c.b, c.g));
            float4 q = lerp(float4(p.xyw, c.r), float4(c.r, p.yzx), step(p.x, c.r));
            float d = q.x - min(q.w, q.y);
            float e = 1.0e-10;
            return float3(abs(q.z + (q.w - q.y) / (6.0 * d + e)), d / (q.x + e), q.x);
        }
        ENDCG

        Pass
        {
            Tags
            {
                "LightMode"="ForwardBase"
            }


            CGPROGRAM
            #pragma shader_feature_local _SURFACE_OPAQUE _SURFACE_CUTOUT _SURFACE_TRANSPARENT
            #pragma multi_compile_instancing
            #pragma multi_compile __ LOD_FADE_CROSSFADE
            #pragma multi_compile_fog
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fwdbase

            #include "UnityCG.cginc"
            #include "Lighting.cginc"
            #include "UnityPBSLighting.cginc"
            #include "AutoLight.cginc"
            #include "Packages/com.llealloo.audiolink/Runtime/Shaders/AudioLink.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float4 tangent : TANGENT;
                float3 normal : NORMAL;
                float4 texcoord1 : TEXCOORD1;
                float4 texcoord2 : TEXCOORD2;
                float4 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                #if defined(LIGHTMAP_ON) || (!defined(LIGHTMAP_ON) && SHADER_TARGET >= 30)
                float4 lmap : TEXCOORD0;
                #endif
                #if !defined(LIGHTMAP_ON) && UNITY_SHOULD_SAMPLE_SH
                half3 sh : TEXCOORD1;
                #endif
                UNITY_SHADOW_COORDS(2)
                UNITY_FOG_COORDS(3)
                float4 tSpace0 : TEXCOORD4;
                float4 tSpace1 : TEXCOORD5;
                float4 tSpace2 : TEXCOORD6;
                float4 uv : TEXCOORD7;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            // Uniforms
            uniform sampler2D _MainTex;
            uniform sampler2D _BumpMap;
            uniform float _BumpScale;
            uniform sampler2D _EmissionMap;
            uniform float _Metallic;
            uniform float _Smoothness;

            UNITY_INSTANCING_BUFFER_START(UnityPerMaterial)
                UNITY_DEFINE_INSTANCED_PROP(float4, _Color)
                UNITY_DEFINE_INSTANCED_PROP(float4, _BumpMap_ST)
                UNITY_DEFINE_INSTANCED_PROP(float4, _EmissionMap_ST)
                UNITY_DEFINE_INSTANCED_PROP(float4, _EmissionColor)
                UNITY_DEFINE_INSTANCED_PROP(float, _AudioHueShift)
                UNITY_DEFINE_INSTANCED_PROP(float, _Band)
                UNITY_DEFINE_INSTANCED_PROP(float, _PulseRotation)
                UNITY_DEFINE_INSTANCED_PROP(float, _Pulse)
                UNITY_DEFINE_INSTANCED_PROP(float, _Delay)
                UNITY_DEFINE_INSTANCED_PROP(float, _Emission)
                UNITY_DEFINE_INSTANCED_PROP(float, _Cutoff)
            UNITY_INSTANCING_BUFFER_END(UnityPerMaterial)


            v2f vert(appdata v)
            {
                UNITY_SETUP_INSTANCE_ID(v);
                v2f o = (v2f)0;
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                o.uv.xy = v.uv.xy;
                o.uv.zw = 0;

                o.pos = UnityObjectToClipPos(v.vertex);
                float3 worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                fixed3 worldNormal = UnityObjectToWorldNormal(v.normal);
                fixed3 worldTangent = UnityObjectToWorldDir(v.tangent.xyz);
                fixed tangentSign = v.tangent.w * unity_WorldTransformParams.w;
                fixed3 worldBinormal = cross(worldNormal, worldTangent) * tangentSign;

                o.tSpace0 = float4(worldTangent.x, worldBinormal.x, worldNormal.x, worldPos.x);
                o.tSpace1 = float4(worldTangent.y, worldBinormal.y, worldNormal.y, worldPos.y);
                o.tSpace2 = float4(worldTangent.z, worldBinormal.z, worldNormal.z, worldPos.z);

                #ifdef DYNAMICLIGHTMAP_ON
                o.lmap.zw = v.texcoord2.xy * unity_DynamicLightmapST.xy + unity_DynamicLightmapST.zw;
                #endif
                #ifdef LIGHTMAP_ON
                o.lmap.xy = v.texcoord1.xy * unity_LightmapST.xy + unity_LightmapST.zw;
                #endif

                #ifndef LIGHTMAP_ON
                #if UNITY_SHOULD_SAMPLE_SH && !UNITY_SAMPLE_FULL_SH_PER_PIXEL
                o.sh = 0;
                #ifdef VERTEXLIGHT_ON
                o.sh += Shade4PointLights(
                    unity_4LightPosX0, unity_4LightPosY0, unity_4LightPosZ0,
                    unity_LightColor[0].rgb, unity_LightColor[1].rgb, unity_LightColor[2].rgb, unity_LightColor[3].rgb,
                    unity_4LightAtten0, worldPos, worldNormal);
                #endif
                o.sh = ShadeSHPerVertex(worldNormal, o.sh);
                #endif
                #endif

                UNITY_TRANSFER_SHADOW(o, v.texcoord1.xy);
                UNITY_TRANSFER_FOG(o, o.pos);

                return o;
            }

            fixed4 frag(v2f IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);

                #ifdef LOD_FADE_CROSSFADE
                UNITY_APPLY_DITHER_CROSSFADE(IN.pos.xy);
                #endif

                float2 texCoord = IN.uv;
                float4 mainTex = tex2D(_MainTex, texCoord);
                float4 baseColor = UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _Color);

                float finalAlpha = 1;
                #ifndef _SURFACE_OPAQUE
                finalAlpha = mainTex.a * baseColor.a;
                float cutoff = UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _Cutoff);
                clip(finalAlpha - cutoff);
                #endif

                SurfaceOutputStandard o = (SurfaceOutputStandard)0;

                float3 worldPos = float3(IN.tSpace0.w, IN.tSpace1.w, IN.tSpace2.w);
                float3 worldViewDir = normalize(UnityWorldSpaceViewDir(worldPos));

                UNITY_LIGHT_ATTENUATION(atten, IN, worldPos)

                float3 hsvColor = RGBToHSV((mainTex * baseColor).rgb);

                float hueShift = lerp(0, UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _AudioHueShift), AudioLinkIsAvailable());
                float band = UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _Band);
                float pulseRotation = UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _PulseRotation);
                float pulse = UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _Pulse);
                float delay = UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _Delay);

                // Rotation calculation
                float2 centeredUV = texCoord - 0.5;
                float rotRad = radians(pulseRotation);
                float cosRot = cos(rotRad);
                float sinRot = sin(rotRad);
                float normalizer = 1.0 / (abs(cosRot) + abs(sinRot));

                float2 rotatedUV = float2(
                    (centeredUV.x * cosRot * normalizer + centeredUV.y * sinRot * normalizer) + 0.5,
                    (centeredUV.y * cosRot * normalizer - centeredUV.x * sinRot * normalizer) + 0.5
                );

                float finalDelay = ((delay + (rotatedUV.x * pulse - 0.0) * (1.0 - delay) / (1.0 - 0.0)) % 1.0) * 128.0;
                float amplitude = lerp(1, AudioLinkLerp(ALPASS_AUDIOLINK + float2(finalDelay, (int)band)).r, AudioLinkIsAvailable());

                float3 finalAlbedo = HSVToRGB(float3(hsvColor.x + (hueShift * amplitude), hsvColor.y, hsvColor.z));

                // Normal mapping
                float4 bumpMapST = UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _BumpMap_ST);
                float2 bumpUV = texCoord * bumpMapST.xy + bumpMapST.zw;

                // Emission
                float4 emissionMapST = UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _EmissionMap_ST);
                float2 emissionUV = texCoord * emissionMapST.xy + emissionMapST.zw;
                float4 emissionColor = UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _EmissionColor);
                float emissionStrength = UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _Emission);

                float3 emissionHSV = RGBToHSV((tex2D(_EmissionMap, emissionUV) * emissionColor * amplitude).rgb);
                float3 finalEmission = HSVToRGB(float3(emissionHSV.x + (hueShift * amplitude), emissionHSV.y, emissionHSV.z));

                // Set surface properties
                o.Albedo = finalAlbedo;
                o.Normal = UnpackNormal(tex2D(_BumpMap, bumpUV)) * _BumpScale;
                o.Emission = finalEmission * emissionStrength;
                o.Metallic = _Metallic;
                o.Smoothness = _Smoothness;
                o.Alpha = finalAlpha;

                // Lighting calculations
                fixed3 lightDir = _WorldSpaceLightPos0.xyz;

                float3 worldN;
                worldN.x = dot(IN.tSpace0.xyz, o.Normal);
                worldN.y = dot(IN.tSpace1.xyz, o.Normal);
                worldN.z = dot(IN.tSpace2.xyz, o.Normal);
                worldN = normalize(worldN);
                o.Normal = worldN;

                UnityGI gi;
                UNITY_INITIALIZE_OUTPUT(UnityGI, gi);
                gi.light.color = _LightColor0.rgb;
                gi.light.dir = lightDir;

                UnityGIInput giInput;
                UNITY_INITIALIZE_OUTPUT(UnityGIInput, giInput);
                giInput.light = gi.light;
                giInput.worldPos = worldPos;
                giInput.worldViewDir = worldViewDir;
                giInput.atten = atten;

                #if defined(LIGHTMAP_ON) || defined(DYNAMICLIGHTMAP_ON)
                giInput.lightmapUV = IN.lmap;
                #endif

                #if UNITY_SHOULD_SAMPLE_SH && !UNITY_SAMPLE_FULL_SH_PER_PIXEL
                giInput.ambient = IN.sh;
                #endif

                giInput.probeHDR[0] = unity_SpecCube0_HDR;
                giInput.probeHDR[1] = unity_SpecCube1_HDR;

                LightingStandard_GI(o, giInput, gi);
                fixed4 c = LightingStandard(o, worldViewDir, gi);
                c.rgb += o.Emission;

                UNITY_APPLY_FOG(IN.fogCoord, c);
                return c;
            }
            ENDCG
        }

        Pass
        {
            Name "Meta"
            Tags
            {
                "LightMode"="Meta"
            }
            Cull Off

            CGPROGRAM
            #pragma shader_feature_local _SURFACE_OPAQUE _SURFACE_CUTOUT _SURFACE_TRANSPARENT
            #pragma multi_compile_instancing
            #pragma multi_compile __ LOD_FADE_CROSSFADE
            #pragma vertex vert
            #pragma fragment frag
            #pragma shader_feature EDITOR_VISUALIZATION

            #include "UnityCG.cginc"
            #include "UnityMetaPass.cginc"
            #include "Packages/com.llealloo.audiolink/Runtime/Shaders/AudioLink.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float4 texcoord1 : TEXCOORD1;
                float4 texcoord2 : TEXCOORD2;
                float4 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                #ifdef EDITOR_VISUALIZATION
                float2 vizUV : TEXCOORD0;
                float4 lightCoord : TEXCOORD1;
                #endif
                float4 uv : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            uniform sampler2D _MainTex;
            uniform sampler2D _EmissionMap;

            UNITY_INSTANCING_BUFFER_START(UnityPerMaterial)
                UNITY_DEFINE_INSTANCED_PROP(float4, _Color)
                UNITY_DEFINE_INSTANCED_PROP(float4, _EmissionMap_ST)
                UNITY_DEFINE_INSTANCED_PROP(float4, _EmissionColor)
                UNITY_DEFINE_INSTANCED_PROP(float, _AudioHueShift)
                UNITY_DEFINE_INSTANCED_PROP(float, _Band)
                UNITY_DEFINE_INSTANCED_PROP(float, _PulseRotation)
                UNITY_DEFINE_INSTANCED_PROP(float, _Pulse)
                UNITY_DEFINE_INSTANCED_PROP(float, _Delay)
                UNITY_DEFINE_INSTANCED_PROP(float, _Emission)
                UNITY_DEFINE_INSTANCED_PROP(float, _Cutoff)
            UNITY_INSTANCING_BUFFER_END(UnityPerMaterial)

            v2f vert(appdata v)
            {
                UNITY_SETUP_INSTANCE_ID(v);
                v2f o;
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                o.uv.xy = v.uv.xy;
                o.uv.zw = 0;

                #ifdef EDITOR_VISUALIZATION
                o.vizUV = 0;
                o.lightCoord = 0;
                if (unity_VisualizationMode == EDITORVIZ_TEXTURE)
                    o.vizUV = UnityMetaVizUV(unity_EditorViz_UVIndex, v.uv.xy, v.texcoord1.xy, v.texcoord2.xy, unity_EditorViz_Texture_ST);
                else if (unity_VisualizationMode == EDITORVIZ_SHOWLIGHTMASK)
                {
                    o.vizUV = v.texcoord1.xy * unity_LightmapST.xy + unity_LightmapST.zw;
                    o.lightCoord = mul(unity_EditorViz_WorldToLight, mul(unity_ObjectToWorld, float4(v.vertex.xyz, 1)));
                }
                #endif

                o.pos = UnityMetaVertexPosition(v.vertex, v.texcoord1.xy, v.texcoord2.xy, unity_LightmapST, unity_DynamicLightmapST);

                return o;
            }

            fixed4 frag(v2f IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);

                #ifdef LOD_FADE_CROSSFADE
                UNITY_APPLY_DITHER_CROSSFADE(IN.pos.xy);
                #endif


                // Sample main texture and perform alpha test
                float2 texCoord = IN.uv;
                float4 mainTex = tex2D(_MainTex, texCoord);

                #ifndef _SURFACE_OPAQUE
                float4 color = UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _Color);
                float finalAlpha = mainTex.a * color.a;
                float cutoff = UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _Cutoff);
                clip(finalAlpha - cutoff);
                #endif

                // AudioLink calculations
                float3 hsvColor = RGBToHSV((mainTex * _Color).rgb);

                float hueShift = lerp(0, UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _AudioHueShift), AudioLinkIsAvailable());
                float band = UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _Band);
                float pulseRotation = UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _PulseRotation);
                float pulse = UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _Pulse);
                float delay = UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _Delay);

                // Rotation calculation
                float2 centeredUV = texCoord - 0.5;
                float rotRad = radians(pulseRotation);
                float cosRot = cos(rotRad);
                float sinRot = sin(rotRad);
                float normalizer = 1.0 / (abs(cosRot) + abs(sinRot));

                float2 rotatedUV = float2(
                    (centeredUV.x * cosRot * normalizer + centeredUV.y * sinRot * normalizer) + 0.5,
                    (centeredUV.y * cosRot * normalizer - centeredUV.x * sinRot * normalizer) + 0.5
                );

                float finalDelay = ((delay + (rotatedUV.x * pulse - 0.0) * (1.0 - delay) / (1.0 - 0.0)) % 1.0) * 128.0;
                float amplitude = lerp(1, AudioLinkLerp(ALPASS_AUDIOLINK + float2(finalDelay, (int)band)).r, AudioLinkIsAvailable());

                float3 finalAlbedo = HSVToRGB(float3(hsvColor.x + (hueShift * amplitude), hsvColor.y, hsvColor.z));

                // Emission
                float4 emissionMapST = UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _EmissionMap_ST);
                float2 emissionUV = texCoord * emissionMapST.xy + emissionMapST.zw;
                float4 emissionColor = UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _EmissionColor);
                float emissionStrength = UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _Emission);

                float3 emissionHSV = RGBToHSV((tex2D(_EmissionMap, emissionUV) * emissionColor * amplitude).rgb);
                float3 finalEmission = HSVToRGB(float3(emissionHSV.x + (hueShift * amplitude), emissionHSV.y, emissionHSV.z));

                // Meta pass output
                UnityMetaInput metaIN;
                UNITY_INITIALIZE_OUTPUT(UnityMetaInput, metaIN);
                metaIN.Albedo = finalAlbedo;
                metaIN.Emission = finalEmission * emissionStrength;

                #ifdef EDITOR_VISUALIZATION
                metaIN.VizUV = IN.vizUV;
                metaIN.LightCoord = IN.lightCoord;
                #endif

                return UnityMetaFragment(metaIN);
            }
            ENDCG
        }

        Pass
        {
            Name "ShadowCaster"
            Tags
            {
                "LightMode"="ShadowCaster"
            }
            ZWrite On
            ZTest LEqual

            CGPROGRAM
            #pragma shader_feature_local _SURFACE_OPAQUE _SURFACE_CUTOUT _SURFACE_TRANSPARENT
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_shadowcaster
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                V2F_SHADOW_CASTER;
                float2 uv : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            uniform sampler2D _MainTex;
            uniform sampler2D _EmissionMap;

            UNITY_INSTANCING_BUFFER_START(UnityPerMaterial)
                UNITY_DEFINE_INSTANCED_PROP(float4, _Color)
                UNITY_DEFINE_INSTANCED_PROP(float4, _MainTex_ST)
                UNITY_DEFINE_INSTANCED_PROP(float4, _EmissionMap_ST)
                UNITY_DEFINE_INSTANCED_PROP(float4, _EmissionColor)
                UNITY_DEFINE_INSTANCED_PROP(float, _AudioHueShift)
                UNITY_DEFINE_INSTANCED_PROP(float, _Band)
                UNITY_DEFINE_INSTANCED_PROP(float, _PulseRotation)
                UNITY_DEFINE_INSTANCED_PROP(float, _Pulse)
                UNITY_DEFINE_INSTANCED_PROP(float, _Delay)
                UNITY_DEFINE_INSTANCED_PROP(float, _Emission)
                UNITY_DEFINE_INSTANCED_PROP(float, _Cutoff)
            UNITY_INSTANCING_BUFFER_END(UnityPerMaterial)

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                float4 st = UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _MainTex_ST);
                o.uv = v.texcoord * st.xy + st.zw;
                TRANSFER_SHADOW_CASTER_NORMALOFFSET(o)
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);

                #ifndef _SURFACE_OPAQUE
                float4 mainTex = tex2D(_MainTex, i.uv);
                float4 color = UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _Color);
                float finalAlpha = mainTex.a * color.a;
                float cutoff = UNITY_ACCESS_INSTANCED_PROP(UnityPerMaterial, _Cutoff);
                clip(finalAlpha - cutoff);
                #endif

                SHADOW_CASTER_FRAGMENT(i)
            }
            ENDCG
        }
    }

    CustomEditor "AudioLink.Editor.Shaders.AudioReactiveSurfaceGUI"
}
