Shader "AudioLink/Internal/ThemeColorGrid"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"
            #include "AudioLink.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;

            v2f vert (appdata v)
            {
                v2f o;

                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                uint2 themeColorLocation = (i.uv.y > .5) ?
                    ((i.uv.x < .5)? ALPASS_THEME_COLOR0 : ALPASS_THEME_COLOR1) :
                    ((i.uv.x < .5)? ALPASS_THEME_COLOR2 : ALPASS_THEME_COLOR3);
                fixed3 themeColor = AudioLinkData(themeColorLocation).rgb;
                fixed4 col = tex2D(_MainTex, i.uv);
                themeColor = lerp(themeColor, col.rgb, col.a *.25);
                return fixed4(themeColor, 1);
            }
            ENDCG
        }
    }
}
