// Upgrade NOTE: replaced 'mul(UNITY_MATRIX_MVP,*)' with 'UnityObjectToClipPos(*)'

// http://forum.unity3d.com/threads/3d-text-that-takes-the-depth-buffer-into-account.9931/
// slightly modified so that it has a color parameter,
// start with white sprites and you can color them
// if having trouble making font sprite sets http://answers.unity3d.com/answers/1105527/view.html

Shader "GUI/Color3DText"
{
    Properties
    {
        _MainTex ("Font Texture", 2D) = "white" {}

        [HDR]_Colorize ("Colorize", Color) = (1,1,1,1)
    }
    SubShader
    {
        Tags
        {
            "RenderType"="Transparent" "Queue"="Transparent"
        }

        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;

            fixed4 _Colorize;

            struct v2f
            {
                float4 pos : SV_POSITION;
                fixed4 color : COLOR;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            struct appdata
            {
                float4 vertex : POSITION;
                fixed4 color : COLOR;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            float4 _MainTex_ST;

            v2f vert(appdata v)
            {
                v2f o;

                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                o.uv = TRANSFORM_TEX(v.texcoord, _MainTex);
                return o;
            }

            fixed4 frag(v2f o) : COLOR
            {
                // this gives us text or not based on alpha, apparently
                o.color.a *= tex2D(_MainTex, o.uv).a;

                o.color *= _Colorize;

                return o.color;
            }
            ENDCG
        }
    }
}