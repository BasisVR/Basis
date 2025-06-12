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
        _FadeCull ("Fade Cull", float) = 3.0
        _FadeSharpness ("Fade Range", float ) = 1
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

            /*
                          #pragma geometry geom
            
                        struct v2g {
                            float4 pos : SV_POSITION;
                            fixed4 color : COLOR;
                            float2 uv : TEXCOORD0;
                            float3 camrelpos : TEXCOORD1;
                        };
                        // I tried doing an approach with a geometry shader, I didn't like how it looked.
            
                        [maxvertexcount(3)]            
                        void geom(triangle v2g p[3], inout TriangleStream<g2f> triStream, uint id : SV_PrimitiveID)
                        {
                            float3 dists = float3( length( p[0].camrelpos ), length( p[1].camrelpos ), length( p[2].camrelpos ) );
                            float avgdist = (dists.x+dists.y+dists.z)/3;
                            if( max( max( dists.x, dists.y ), dists.z ) >= _FadeCull )
                            {
                                return;
                            }
                            float strength = pow( saturate( 1 + (_FadeNear - avgdist) ), 1.5 );
                            float4 center = (p[0].pos+p[1].pos+p[2].pos)/3;
                            float4 v0 = p[0].pos-center;
                            float4 v1 = p[1].pos-center;
                            float4 v2 = p[2].pos-center;
                            v0 *= strength;
                            v1 *= strength;
                            v2 *= strength;
                            p[0].pos = UnityObjectToClipPos( v0+center );
                            p[1].pos = UnityObjectToClipPos( v1+center );
                            p[2].pos = UnityObjectToClipPos( v2+center );
                            triStream.Append( p[0] );
                            triStream.Append( p[1] );
                            triStream.Append( p[2] );
                        }
                   */
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
    }
}