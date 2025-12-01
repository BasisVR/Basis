Shader "metaphira/TableSurface (Glass)"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
        _EmissionColor( "Emission Color", Color ) = (1,1,1,1)
        _MainTex ("Albedo (RGB)", 2D) = "white" {}
        _MetalSmooth ("MetalSmooth", 2D) = "white" {}
        _EmissionMap( "EmissionMap", 2D ) = "black" {}
        _TimerPct("Timer Percentage", Range(0, 1)) = 1
    }
    SubShader
    {
        Tags {"Queue" = "Transparent" "RenderType"="Transparent" }
        LOD 200

        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        CGPROGRAM
        // Physically based Standard lighting model, and enable shadows on all light types
        #pragma surface surf Standard fullforwardshadows vertex:vert alpha

        // Use shader model 3.0 target, to get nicer looking lighting
        #pragma target 3.0

        sampler2D _MainTex;
        sampler2D _EmissionMap;
        sampler2D _MetalSmooth;

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


        fixed4 _Color;
        fixed4 _EmissionColor;
        float _TimerPct;

        // Add instancing support for this shader. You need to check 'Enable Instancing' on materials that use the shader.
        // See https://docs.unity3d.com/Manual/GPUInstancing.html for more information about instancing.
        // #pragma instancing_options assumeuniformscaling
        UNITY_INSTANCING_BUFFER_START(Props)
            // put more per-instance properties here
        UNITY_INSTANCING_BUFFER_END(Props)

        static const float M_PI = 3.14159265358979323846264338327950288;

        void surf (Input IN, inout SurfaceOutputStandard o)
        {
            fixed4 c = tex2D( _MainTex, IN.uv_MainTex ) * _Color;
            fixed4 e = tex2D( _EmissionMap, IN.uv_MainTex );
            fixed4 g = tex2D( _MetalSmooth, IN.uv_MainTex );
            o.Albedo = c.rgb;
            o.Metallic = g.r;
            o.Smoothness = g.a;
            o.Alpha = c.a * _Color.a;

            float timer_pct = clamp(_TimerPct, 0, 1);
            // add a small fudge factor so that the light connects
            float surf_angle_pct = (M_PI + atan2(IN.modelPos.x, IN.modelPos.z)) / (2 * M_PI) / 1.04 + (1 - 1 / 1.04);
            float angle_cl = clamp((surf_angle_pct - timer_pct) * 40.0, 0, 1.5);
            o.Emission = e * _EmissionColor * angle_cl;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
