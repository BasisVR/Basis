Shader "metaphira/TableSurface_URP"
{
    Properties
    {
        _EmissionColor ("Emission Color", Color) = (1,1,1,1)
        _Color ("Tint Color", Color) = (1,1,1,1)

        _MainTex ("Albedo (RGB), TintMap(A)", 2D) = "white" {}
        _EmissionMap ("Emission Mask", 2D) = "black" {}
        _Metalic ("Metallic(R)/Smoothness(A)", 2D) = "white" {}

        [Toggle(DETAIL_CLOTH)] _UseDetailCloth ("Use Cloth Detail Texture", Float) = 0
        _DetailCloth ("Cloth Detail", 2D) = "white" {}
        _ClothHue ("Cloth Hue", Range(0, 1)) = 0
        _ClothSaturation ("Cloth Saturation", Range(0, 3)) = 1
        _DetailClothBrightness ("Detail Brightness", Range(0, 2)) = 1
        _DetailClothMask ("Cloth Detail Mask", 2D) = "white" {}
        _MaskStrengthCloth ("Mask Strength Cloth", Range(0, 1)) = 1

        [Toggle(DETAIL_OTHER)] _UseDetailOther ("Use Non-Cloth Detail Texture", Float) = 0
        _DetailOther ("Other Detail", 2D) = "white" {}
        _DetailOtherBrightness ("Other Detail Brightness", Range(0, 2)) = 1
        _MaskStrengthOther ("Mask Strength Other", Range(0, 1)) = 1

        _TimerPct ("Timer Percentage", Range(0, 1)) = 1
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalRenderPipeline" }
        LOD 200

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ DETAIL_CLOTH
            #pragma multi_compile _ DETAIL_OTHER

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ShaderVariablesFunctions.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 worldPos : TEXCOORD1;
                float3 normalWS : TEXCOORD2;
            };

            // Declare textures and samplers
            TEXTURE2D(_MainTex);            SAMPLER(sampler_MainTex);
            TEXTURE2D(_EmissionMap);        SAMPLER(sampler_EmissionMap);
            TEXTURE2D(_Metalic);            SAMPLER(sampler_Metalic);
            TEXTURE2D(_DetailCloth);        SAMPLER(sampler_DetailCloth);
            TEXTURE2D(_DetailClothMask);    SAMPLER(sampler_DetailClothMask);
            TEXTURE2D(_DetailOther);        SAMPLER(sampler_DetailOther);

            float4 _MainTex_ST;
            float4 _DetailCloth_ST;
            float4 _DetailOther_ST;

            float4 _Color;
            float4 _EmissionColor;
            float _ClothHue;
            float _ClothSaturation;
            float _MaskStrengthCloth;
            float _MaskStrengthOther;
            float _DetailClothBrightness;
            float _DetailOtherBrightness;
            float _TimerPct;

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = TRANSFORM_TEX(IN.uv, _MainTex);
                OUT.worldPos = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);
                return OUT;
            }

            float3 linear_srgb_to_oklab(float3 c)
            {
                float l = 0.4122214708 * c.r + 0.5363325363 * c.g + 0.0514459929 * c.b;
                float m = 0.2119034982 * c.r + 0.6806995451 * c.g + 0.1073969566 * c.b;
                float s = 0.0883024619 * c.r + 0.2817188376 * c.g + 0.6299787005 * c.b;

                l = pow(l, 1.0 / 3.0);
                m = pow(m, 1.0 / 3.0);
                s = pow(s, 1.0 / 3.0);

                return float3(
                    0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
                    1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
                    0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s
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
                    +4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
                    -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
                    -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s
                );
            }

            float3 hueShift(float3 color, float shift)
            {
                float3 oklab = linear_srgb_to_oklab(color);
                float hue = atan2(oklab.z, oklab.y);
                hue += shift * 6.2831853;

                float chroma = length(oklab.yz);
                oklab.y = cos(hue) * chroma;
                oklab.z = sin(hue) * chroma;

                return oklab_to_linear_srgb(oklab);
            }

            float3 AdjustSaturation(float3 color, float saturation)
            {
                float luminance = dot(color, float3(0.2126, 0.7152, 0.0722));
                return lerp(float3(luminance, luminance, luminance), color, saturation);
            }

            float4 frag(Varyings IN) : SV_Target
            {
                float2 uv = IN.uv;
                float3 worldPos = IN.worldPos;

                float4 baseCol = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv);
                float4 emissionMap = SAMPLE_TEXTURE2D(_EmissionMap, sampler_EmissionMap, uv);
                float4 metal = SAMPLE_TEXTURE2D(_Metalic, sampler_Metalic, uv);

                float3 finalColor = baseCol.rgb;

                #if defined(DETAIL_CLOTH)
                {
                    float2 uvDetail = TRANSFORM_TEX(uv, _DetailCloth);
                    float4 detail = SAMPLE_TEXTURE2D(_DetailCloth, sampler_DetailCloth, uvDetail);
                    float maskCloth = SAMPLE_TEXTURE2D(_DetailClothMask, sampler_DetailClothMask, uv).r;

                    finalColor = lerp(finalColor, _Color.rgb * finalColor * 2.0, pow(baseCol.a, 0.1));
                    finalColor = lerp(finalColor, finalColor * detail.rgb * _DetailClothBrightness, maskCloth * _MaskStrengthCloth);

                    float3 cloth = finalColor * maskCloth;
                    float3 other = finalColor * (1.0 - maskCloth);

                    cloth = hueShift(cloth, _ClothHue);
                    cloth = AdjustSaturation(cloth, _ClothSaturation);
                    finalColor = cloth + other;
                }
                #else
                {
                    finalColor = lerp(finalColor, _Color.rgb * finalColor * 2.0, pow(baseCol.a, 0.1));
                }
                #endif

                #if defined(DETAIL_OTHER)
                {
                    float2 uvOther = TRANSFORM_TEX(uv, _DetailOther);
                    float4 detailOther = SAMPLE_TEXTURE2D(_DetailOther, sampler_DetailOther, uvOther);
                    float maskCloth = SAMPLE_TEXTURE2D(_DetailClothMask, sampler_DetailClothMask, uv).r;
                    finalColor = lerp(finalColor, finalColor * detailOther.rgb * _DetailOtherBrightness, (1.0 - maskCloth) * _MaskStrengthOther);
                }
                #endif

                float angle = (PI + atan2(worldPos.x, worldPos.z)) / (2.0 * PI) / 1.04 + (1 - 1 / 1.04);
                float lightFactor = saturate((angle - _TimerPct) * 40.0);
                float3 emission = emissionMap.rgb * _EmissionColor.rgb * lightFactor;

                return float4(finalColor + emission, 1.0);
            }

            ENDHLSL
        }
    }

    FallBack "Hidden/InternalErrorShader"
}
