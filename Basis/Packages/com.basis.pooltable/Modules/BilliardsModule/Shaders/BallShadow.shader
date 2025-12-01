Shader "metaphira/Ball Shadow"
{
    Properties
    {
        _MainTex("Texture", 2D) = "white" {}
        _Floor("Surface Height (World Space)", Float) = 0.0
        _Scale("Ball Scale", Float) = 1.0
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "DisableBatching" = "true" }
        ZWrite Off
        Cull Off

        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ UNITY_SINGLE_PASS_STEREO

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
                float intensity : TEXCOORD1;
                float4 vertex : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            float _Floor;
            float _Scale;
            static const float BALL_RADIUS = 0.03f;

            v2f vert(appdata v)
            {
                UNITY_SETUP_INSTANCE_ID(v);
                v2f o;
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                float ballRadius = BALL_RADIUS * _Scale;
                float ballOriginY = _Floor + ballRadius;

                float3 shadowOrigin = mul(unity_ObjectToWorld, float4(0, 0, 0, 1)).xyz;
                float intensity = 1.0 - saturate(abs(shadowOrigin.y - ballOriginY) / ballRadius);

                float3 worldPos = float3(v.vertex.x * _Scale + shadowOrigin.x, _Floor, v.vertex.z * _Scale + shadowOrigin.z);
                float4 worldPos4 = float4(worldPos, 1.0);

                // Stereo projection manually (Built-in RP)
                float4 clipPos = mul(UNITY_MATRIX_VP, worldPos4);
                o.vertex = clipPos;

                o.uv = v.uv;
                o.intensity = intensity;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 col = tex2D(_MainTex, i.uv);
                col.a *= i.intensity;
                return col;
            }
            ENDCG
        }
    }
}
