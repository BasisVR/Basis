Shader "metaphira/Ball Shadow"
{
   Properties
   {
      _MainTex("Texture", 2D) = "white" {}
      _Floor("Surface Height (World Space)", Float) = 0.0
      _Scale("Ball Scale", Float) = 1.0
   }

   SubShader
   {
      Tags { "Queue" = "AlphaTest" "DisableBatching" = "true" }

      ZWrite Off
      Cull Off

      Pass
      {
         Blend SrcAlpha OneMinusSrcAlpha

         CGPROGRAM
         #pragma vertex vert
         #pragma fragment frag

         #include "UnityCG.cginc"

         struct appdata
         {
            float4 vertex : POSITION;
            float2 uv : TEXCOORD0;
         };

         struct v2f
         {
            float3 uv : TEXCOORD0;
            float4 vertex : SV_POSITION;
         };

         static const float BALL_RADIUS = 0.03f;

         sampler2D _MainTex;
         float _Floor;
         float _Scale;

         v2f vert(appdata v)
         {
            float ballRadius = BALL_RADIUS * _Scale;
            float ballOriginY = _Floor + ballRadius;
            float3 shadowOrigin = mul(unity_ObjectToWorld, float4(0.0, 0.0, 0.0, 1.0));

            float intensity = 1.0 - clamp(abs(shadowOrigin.y - ballOriginY), 0.0, ballRadius) / ballRadius;

            v2f o;
            o.vertex = UnityWorldToClipPos(float3(v.vertex.x * _Scale + shadowOrigin.x, _Floor, v.vertex.z * _Scale + shadowOrigin.z));
            o.uv = float3(v.uv, intensity);
            return o;
         }

         fixed4 frag(v2f i) : SV_Target
         {
            return tex2D(_MainTex, i.uv.xy) * float4(1.0, 1.0, 1.0, i.uv.z);
         }
         ENDCG
      }
   }
}
