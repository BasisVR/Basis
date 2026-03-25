Shader "harry_t/cliptable"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Dims ("Dimentions", Vector) = (0,0,0,0)
        _Colour ("Colour", Color ) = (1,1,1,1)
    }
    SubShader
    {
        Tags { "Queue" = "Transparent+12" }
        
        ZWrite Off
        Cull Off

        Pass
        {
            Blend One One

            CGPROGRAM

            #pragma vertex vert
            #pragma fragment frag

            #pragma multi_compile _ UNITY_SINGLE_PASS_STEREO
            #pragma multi_compile_instancing

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
                float4 worldpos: TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float2 _Dims;
            float4x4 _BaseTransform;
            fixed4 _Colour;

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.worldpos = mul( _BaseTransform, mul(unity_ObjectToWorld, v.vertex) );
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                
                fixed4 col = tex2D(_MainTex, i.uv);
                
                if( abs(i.worldpos.x) > _Dims.x || abs(i.worldpos.z) > _Dims.y )
                    discard;

                return col * _Colour;
            }

            ENDCG
        }
    }
}
