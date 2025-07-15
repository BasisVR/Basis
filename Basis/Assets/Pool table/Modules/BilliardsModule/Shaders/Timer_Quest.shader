Shader "metaphira/Timer (Quest)"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
        _MainTex ("Albedo (RGB)", 2D) = "white" {}
        _TimeFrac( "Time", Float ) = 0.0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 200

        CGPROGRAM
        #pragma surface surf Lambert noforwardadd

        sampler2D _MainTex;

        struct Input
        {
            float2 uv_MainTex;
        };

        fixed4 _Color;
        float _TimeFrac;

        void surf (Input IN, inout SurfaceOutput o)
        {
            fixed4 c = tex2D (_MainTex, IN.uv_MainTex);
            float4 colourterm = clamp( (c.r - _TimeFrac) * 12.0, 0.0, 1.0 );

            o.Albedo = lerp( _Color * colourterm, fixed4(1.0,1.0,1.0,1.0), c.g );
            o.Emission = colourterm * (1.0-c.g) * _Color;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
