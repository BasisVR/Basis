Shader "Basis/UI/PerformanceBar"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "PreviewType"="Plane" }
        Cull Off
        Lighting Off
        ZWrite Off
        ZTest Always
        Fog { Mode Off }

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            #define BASIS_PERFBAR_SEGMENTS 9

            sampler2D _MainTex;
            float4 _Segments[BASIS_PERFBAR_SEGMENTS];
            float4 _Colors[BASIS_PERFBAR_SEGMENTS];
            float _FillFraction;
            float _OverBudget;
            float _Gap;
            float4 _Background;

            fixed4 frag(v2f_img i) : SV_Target
            {
                float fill = saturate(_FillFraction);
                if (i.uv.x >= fill) return _Background;

                float local = fill > 0.0001 ? i.uv.x / fill : 0.0;
                float halfGap = _Gap * 0.5;
                fixed4 color = _Background;
                for (int s = 0; s < BASIS_PERFBAR_SEGMENTS; s++)
                {
                    float4 seg = _Segments[s];
                    if (local >= seg.x + halfGap && local < seg.y - halfGap) { color = _Colors[s]; break; }
                }

                float edge = fwidth(i.uv.x) * 1.5 + 1e-5;
                float glow = smoothstep(fill, fill - edge, i.uv.x);
                color.rgb += glow * 0.08;

                if (_OverBudget > 0.5)
                {
                    float pulse = 0.5 + 0.5 * sin(_Time.y * 6.0);
                    color.rgb = lerp(color.rgb, fixed3(1, 0.2, 0.2), pulse * 0.35);
                }

                return color;
            }
            ENDHLSL
        }
    }
    Fallback Off
}
