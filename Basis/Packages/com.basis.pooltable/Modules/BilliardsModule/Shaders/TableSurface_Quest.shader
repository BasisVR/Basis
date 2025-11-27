Shader "metaphira/TableSurface (Quest)"
{
   Properties
   {
      _EmissionColor ("Emission Colour", Color) = (1,1,1,1)
      _Color ("Tint Colour", Color) = (1,1,1,1)

      _MainTex ("Albedo (RGB)", 2D) = "white" {}
      _EmissionMap ("Emission mask", 2D) = "black" {}

      _TimerPct("Timer Percentage", Range(0, 1)) = 1
   }
   SubShader
   {
      Tags { "RenderType"="Opaque" }
      LOD 200

      CGPROGRAM

      #pragma surface surf Lambert noforwardadd vertex:vert

      sampler2D _MainTex;
      sampler2D _EmissionMap;
      sampler2D _TimerMap;

      fixed4 _EmissionColor;
      fixed3 _Color;

      float _TimerPct;

      struct Input
      {
         float2 uv_MainTex;
         float3 modelPos;
      };

      void vert(inout appdata_full v, out Input o)
      {
         UNITY_INITIALIZE_OUTPUT(Input, o);
         o.modelPos = v.vertex.xyz;
      }

      static const float M_PI = 3.14159265358979323846264338327950288;

      void surf (Input IN, inout SurfaceOutput o)
      {
         fixed4 sample_diffuse = tex2D (_MainTex, IN.uv_MainTex);
         fixed4 sample_emission = tex2D( _EmissionMap, IN.uv_MainTex );

         o.Albedo = lerp( sample_diffuse.rgb, _Color * sample_diffuse.rgb * 2.0, pow(sample_diffuse.a,0.1) );

         float timer_pct = clamp(_TimerPct, 0, 1);
         // add a small fudge factor so that the light connects
         float surf_angle_pct = (M_PI + atan2(IN.modelPos.x, IN.modelPos.z)) / (2 * M_PI) / 1.04 + (1 - 1 / 1.04);
         float angle_cl = clamp((surf_angle_pct - timer_pct) * 40.0, 0, 1.5);
         o.Emission = sample_emission * _EmissionColor * angle_cl;
      }

      ENDCG
   }
   FallBack "Diffuse"
}