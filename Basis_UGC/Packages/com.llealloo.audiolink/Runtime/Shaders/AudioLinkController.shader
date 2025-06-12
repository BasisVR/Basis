Shader "AudioLink/Internal/AudioLinkController"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
        _MainTex ("Albedo (RGB)", 2D) = "white" {}
        _Metallic ("Metallic", 2D) = "white" {}
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }

        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows
        #pragma target 3.0

        sampler2D _MainTex;
        sampler2D _Metallic;
        float4 _Color;

        struct Input
        {
            float2 uv_MainTex;
        };

        void surf (Input IN, inout SurfaceOutputStandard o)
        {
            float4 c = tex2D (_MainTex, IN.uv_MainTex) * _Color;
            o.Albedo = c.rgb;
            float4 b = tex2D(_Metallic, IN.uv_MainTex);
            o.Metallic = b.r;
            o.Smoothness = b.a;
            o.Occlusion = b.g;
            o.Alpha = c.a;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
