Shader "Hidden/Klak/Syphon/Blit"
{
    Properties
    {
        _MainTex("", 2D) = "white" {}
    }

    CGINCLUDE

    #include "UnityCG.cginc"

    sampler2D _MainTex;
    float _KeepAlpha;
    float _VFlip;

    half4 frag_simple(v2f_img i) : SV_Target
    {
        float2 uv = i.uv;
        uv.y = lerp(uv.y, 1 - uv.y, _VFlip);
        half4 c = tex2D(_MainTex, uv);
        return half4(c.rgb, lerp(1, c.a, _KeepAlpha));
    }

    ENDCG

    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag_simple
            ENDCG
        }
    }
}
