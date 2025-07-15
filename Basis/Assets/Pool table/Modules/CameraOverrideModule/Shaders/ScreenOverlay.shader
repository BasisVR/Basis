Shader "metaphira/ScreenOverlay"
{
    Properties
    {
        _MainTex("Texture", 2D) = "white" {}
        [HideInInspector] _ActivationRange("Activation Range", Vector) = (0, 0, 0, 0)
        _FillColor("Fill Color", Color) = (0, 0, 0, 1)
        [MaterialToggle] _AlwaysActive("Always Active", Float) = 0
    }

    SubShader
    {
        Tags { "Queue" = "Transparent+2000" }
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
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
                float4 vertex : SV_POSITION;

                float2 uv : TEXCOORD0;
            };

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            float4 _ActivationRange;
            float4 _FillColor;
            float _AlwaysActive;

            float map(float value, float min1, float max1, float min2, float max2) {
                return min2 + (value - min1) * (max2 - min2) / (max1 - min1);
            }

            v2f vert(appdata v)
            {
                v2f o;
#if !UNITY_SINGLE_PASS_STEREO
                if (_AlwaysActive == 1 || distance(_WorldSpaceCameraPos, _ActivationRange.xyz) < _ActivationRange.w)
                {
                    o.vertex = float4(v.vertex.x * 2, v.vertex.y * 2, UNITY_NEAR_CLIP_VALUE, 1.0);
                    o.uv = v.uv;
                    if (_ProjectionParams.x < 0)
                        o.uv.y = 1 - o.uv.y;
                    return o;
                }
#endif
                o.vertex = float4(0, 0, 0, 0);
                o.uv = v.uv;
                return o; // discard?
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float screenRatio = _ScreenParams.x / _ScreenParams.y;
                float desiredRatio = _MainTex_TexelSize.z / _MainTex_TexelSize.w;

                if (screenRatio < desiredRatio) {
                    float ratio = screenRatio / desiredRatio;
                    if (abs((i.uv.y - 0.5) * 2) > ratio) return _FillColor;

                    return tex2D(_MainTex, float2(i.uv.x, map(i.uv.y - 0.5, -ratio, ratio, -1, 1) + 0.5));
                } else if (screenRatio > desiredRatio) {
                    float ratio = desiredRatio / screenRatio;
                    if (abs((i.uv.x - 0.5) * 2) > ratio) return _FillColor;

                    return tex2D(_MainTex, float2(map(i.uv.x - 0.5, -ratio, ratio, -1, 1) + 0.5, i.uv.y));
                } else {
                    return tex2D(_MainTex, i.uv);
                }
            }
            ENDCG
       }
   }
}
