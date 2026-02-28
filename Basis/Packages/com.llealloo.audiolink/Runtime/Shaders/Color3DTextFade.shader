// Upgrade NOTE: replaced 'mul(UNITY_MATRIX_MVP,*)' with 'UnityObjectToClipPos(*)'

// http://forum.unity3d.com/threads/3d-text-that-takes-the-depth-buffer-into-account.9931/
// slightly modified so that it has a color parameter,
// start with white sprites and you can color them
// if having trouble making font sprite sets http://answers.unity3d.com/answers/1105527/view.html

Shader "GUI/Color3DTextFade"
{
    Properties
    {
        _MainTex ("Font Texture", 2D) = "white" {}
        _FadeNear ("Fade Near", float) = 2.0
        _FadeSharpness ("Fade Range", float ) = 1
        _FadeCull ("Fade Cull", float) = 4.0
        [HDR]_Colorize ("Colorize", Color) = (1,1,1,1)
    }
    SubShader
    {
        // shadow caster rendering pass, implemented manually
        // using macros from UnityCG.cginc
        Pass
        {
            Tags
            {
                "LightMode"="ShadowCaster"
            }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_shadowcaster
            #include "UnityCG.cginc"
            #pragma multi_compile_instancing

            struct v2f
            {
                V2F_SHADOW_CASTER;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                TRANSFER_SHADOW_CASTER_NORMALOFFSET(o)
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                SHADOW_CASTER_FRAGMENT(i)
            }
            ENDCG
        }

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
            float _FadeCull;
            float _FadeNear, _FadeSharpness;


            struct v2f
            {
                float4 pos : SV_POSITION;
                fixed4 color : COLOR;
                float2 uv : TEXCOORD0;
                float3 camrelpos : TEXCOORD1;
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
                o.camrelpos = _WorldSpaceCameraPos - mul(unity_ObjectToWorld, v.vertex);
                if (length(o.camrelpos) > _FadeCull)
                {
                    o.pos = 0;
                    o.color = 0;
                    o.uv = 0;
                    o.camrelpos = 0;
                }
                return o;
            }

            fixed4 frag(v2f o) : COLOR
            {
                // this gives us text or not based on alpha, apparently
                o.color.a *= tex2D(_MainTex, o.uv).a
                    //;
                    * pow(saturate(1 + (_FadeNear - length(o.camrelpos))), 2);
                o.color *= _Colorize;
                return o.color;
            }
            ENDCG
        }

        // DepthOnly pass
        Pass
        {
            Name "DepthOnly"
            Tags
            {
                "LightMode" = "DepthOnly"
            }

            ZWrite On
            ColorMask 0
            Cull Back

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            float _FadeCull;

            struct appdata
            {
                float4 vertex : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float3 camrelpos : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.camrelpos = _WorldSpaceCameraPos - mul(unity_ObjectToWorld, v.vertex);

                // Match the same culling logic from the main pass
                if (length(o.camrelpos) > _FadeCull)
                {
                    o.vertex = 0;
                    o.camrelpos = 0;
                }

                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                return 0;
            }
            ENDCG
        }

        // DepthNormals pass
        Pass
        {
            Name "DepthNormals"
            Tags
            {
                "LightMode" = "DepthNormals"
            }

            ZWrite On
            Cull Back

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            float _FadeCull;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float3 normal : TEXCOORD0;
                float3 camrelpos : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.normal = UnityObjectToWorldNormal(v.normal);
                o.camrelpos = _WorldSpaceCameraPos - mul(unity_ObjectToWorld, v.vertex);

                // Match the same culling logic from the main pass
                if (length(o.camrelpos) > _FadeCull)
                {
                    o.vertex = 0;
                    o.normal = 0;
                    o.camrelpos = 0;
                }

                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float3 normalWS = normalize(i.normal);
                return float4(normalWS * 0.5 + 0.5, 1);
            }
            ENDCG
        }

    }
}
