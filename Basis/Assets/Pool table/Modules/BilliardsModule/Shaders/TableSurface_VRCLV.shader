Shader "metaphira/TableSurface_VRCLV"
{
   Properties
   {
      _EmissionColor ("Emission Color", Color) = (1,1,1,1)
      _Color ("Tint Color", Color) = (1,1,1,1)

      _MainTex ("Albedo (RGB), TintMap(A)", 2D) = "white" {}
      _EmissionMap ("Emission Mask", 2D) = "black" {}
      _Metalic ("Metallic(R)/Smoothness(A)", 2D) = "white" {}
      [Toggle(DETAIL_CLOTH)]_UseDetailCloth ("Use Cloth Detail Texture", Range(0,1)) = 0
      _DetailCloth ("Cloth Detail", 2D) = "white" {}
      _ClothHue("Cloth Hue", Range(0, 1)) = 0
      _ClothSaturation("Cloth Saturation", Range(0, 3)) = 1
      _DetailClothBrightness("Detail Brightness", Range(0, 2)) = 1
      _DetailClothMask ("Cloth Detail Mask", 2D) = "white" {}
      _MaskStrengthCloth("Mask Strength Cloth", Range(0, 1)) = 1
      [Toggle(DETAIL_OTHER)]_UseDetailOther ("Use Non-Cloth Detail Texture", Range(0,1)) = 0
      _DetailOther ("Other Detail", 2D) = "white" {}
      _DetailOtherBrightness("Other Detail Brightness", Range(0, 2)) = 1
      _MaskStrengthOther("Mask Strength Other", Range(0, 1)) = 1

      _TimerPct("Timer Percentage", Range(0, 1)) = 1

      [Header(VRC Light Volumes)]
      [Toggle(INTEGRATE_VRCLV)]_IntegrateVRCLV("Integrate VRC Light Volumes", Int) = 1
   }
   SubShader
   {
      Tags { "RenderType"="Opaque" }
      LOD 200

      CGPROGRAM

      #pragma surface surf StandardVRCLV fullforwardshadows vertex:vert
      #pragma target 4.5
      #pragma shader_feature DETAIL_CLOTH
      #pragma shader_feature DETAIL_OTHER
      #pragma shader_feature_local INTEGRATE_VRCLV

      #include "UnityPBSLighting.cginc"

      sampler2D _MainTex;
      sampler2D _EmissionMap;
      sampler2D _Metalic;
      sampler2D _DetailCloth;
      sampler2D _DetailOther;
      sampler2D _DetailClothMask;
      sampler2D _TimerMap;
      fixed4 _DetailCloth_ST;
      fixed4 _DetailOther_ST;

      fixed4 _EmissionColor;
      fixed3 _Color;

      float _ClothHue;
      float _ClothSaturation;
      float _MaskStrengthCloth;
      float _MaskStrengthOther;
      float _DetailClothBrightness;
      float _DetailOtherBrightness;
      float _TimerPct;

      struct Input
      {
         float2 uv_MainTex;
         float3 modelPos;
      };

      half4 LightingStandardVRCLV(SurfaceOutputStandard s, half3 viewDir, UnityGI gi)
      {
         return LightingStandard(s, viewDir, gi);
      }

      void LightingStandardVRCLV_GI(SurfaceOutputStandard s, UnityGIInput data, inout UnityGI gi)
      {
         // Removed LightVolumes.cginc logic
         LightingStandard_GI(s, data, gi);
      }

      void vert (inout appdata_full v, out Input o) 
      {
         UNITY_INITIALIZE_OUTPUT(Input,o);
         o.modelPos = v.vertex.xyz;
      }

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
            +4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
            -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
            -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s
         );
      }

      float3 hueShift(float3 color, float shift)
      {
         float3 oklab = linear_srgb_to_oklab(max(color, 0.0000000001));
         float hue = atan2(oklab.z, oklab.y);
         hue += shift * M_PI * 2;

         float chroma = length(oklab.yz);
         oklab.y = cos(hue) * chroma;
         oklab.z = sin(hue) * chroma;

         return oklab_to_linear_srgb(oklab);
      }

      float3 Unity_Saturation_float(float3 In, float Saturation)
      {
         float luma = dot(In, float3(0.2126729, 0.7151522, 0.0721750));
         return luma.xxx + Saturation.xxx * (In - luma.xxx);
      }

      void surf(Input IN, inout SurfaceOutputStandard o)
      {
         fixed4 sample_diffuse = tex2D(_MainTex, IN.uv_MainTex);
         fixed4 sample_emission = tex2D(_EmissionMap, IN.uv_MainTex);
         fixed4 sample_metalic = tex2D(_Metalic, IN.uv_MainTex);

   #if defined(DETAIL_CLOTH)
         fixed4 sample_detail = tex2D(_DetailCloth, IN.uv_MainTex * _DetailCloth_ST.xy + _DetailCloth_ST.wz) * unity_ColorSpaceDouble * _DetailClothBrightness;
         float sample_detailclothmask = tex2D(_DetailClothMask, IN.uv_MainTex).r;
         fixed3 final = lerp(sample_diffuse.rgb, _Color * sample_diffuse.rgb * 2.0, pow(sample_diffuse.a, 0.1));
         final = lerp(final, final * sample_detail, sample_detailclothmask * _MaskStrengthCloth);
         float3 cloth = final * sample_detailclothmask;
         float3 other = final * (1 - sample_detailclothmask);
         cloth = hueShift(cloth, _ClothHue);
         cloth = Unity_Saturation_float(cloth, _ClothSaturation);
         final = cloth + other;
   #if defined(DETAIL_OTHER)
         fixed4 sample_detailother = tex2D(_DetailOther, IN.uv_MainTex * _DetailOther_ST.xy + _DetailOther_ST.wz) * unity_ColorSpaceDouble * _DetailOtherBrightness;
         final = lerp(final, final * sample_detailother, (1 - sample_detailclothmask) * _MaskStrengthOther);
   #endif
   #else
         fixed3 final = lerp(sample_diffuse.rgb, _Color * sample_diffuse.rgb * 2.0, pow(sample_diffuse.a, 0.1));
   #endif

         o.Albedo = final;
         o.Metallic = sample_metalic.r;
         o.Smoothness = sample_metalic.a;
         o.Alpha = 1.0;

         float timer_pct = clamp(_TimerPct, 0, 1);
         float surf_angle_pct = (M_PI + atan2(IN.modelPos.x, IN.modelPos.z)) / (2 * M_PI) / 1.04 + (1 - 1 / 1.04);
         float angle_cl = clamp((surf_angle_pct - timer_pct) * 40.0, 0, 1.5);
         o.Emission = sample_emission * _EmissionColor * angle_cl;
      }

      ENDCG
   }
   FallBack "Diffuse"
}
