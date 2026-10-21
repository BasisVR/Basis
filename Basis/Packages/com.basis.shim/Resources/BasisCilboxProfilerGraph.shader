Shader "Basis/UI/CilboxProfilerGraph"
{
    Properties
    {
        _MainTex ("Frame History", 2D) = "black" {}
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

            #define BASIS_CILBOX_GRAPH_ROWS 9

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            float4 _Colors[BASIS_CILBOX_GRAPH_ROWS];
            float4 _Background;
            float4 _Grid;
            float _Head;
            float _Scale;

            fixed4 frag(v2f_img i) : SV_Target
            {
                float frames = _MainTex_TexelSize.z;
                float column = min(floor(i.uv.x * frames), frames - 1.0);
                float u = (fmod(_Head + 1.0 + column, frames) + 0.5) * _MainTex_TexelSize.x;
                float level = i.uv.y * _Scale;
                fixed4 color = _Background;
                float stacked = 0.0;
                [unroll]
                for (int row = 0; row < BASIS_CILBOX_GRAPH_ROWS; row++)
                {
                    float ms = tex2Dlod(_MainTex, float4(u, (row + 0.5) * _MainTex_TexelSize.y, 0.0, 0.0)).r;
                    float top = stacked + ms;
                    if (ms > 0.0 && level >= stacked && level < top) color = _Colors[row];
                    stacked = top;
                }

                float quarter = i.uv.y * 4.0;
                float mark = 1.0 - saturate(abs(quarter - round(quarter)) / max(fwidth(quarter), 1e-5));
                color.rgb = lerp(color.rgb, _Grid.rgb, mark * _Grid.a);
                return color;
            }
            ENDHLSL
        }
    }
    Fallback Off
}
