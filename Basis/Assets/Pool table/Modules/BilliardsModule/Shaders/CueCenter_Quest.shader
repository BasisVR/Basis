Shader "metaphira/CueCenter (Quest)"
{
    Properties
    {
        _ReColor("Main Color", Color) = (1,1,1,1)
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

        fixed4 _ReColor;

        void surf (Input IN, inout SurfaceOutput o)
        {
            o.Albedo = fixed4(1,1,1,1);
            o.Emission = _ReColor;
        }

        ENDCG
    }
}