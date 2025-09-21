/// NOTE: This shader is designed to work with Premultiplied Alpha enabled.
/// It multiplies the RGB channel by Alpha internally, so ensure that Premultiplied Alpha remains enabled in the project settings.
/// Premultiplied Alpha can be enabled by setting OVRManager.eyeFovPremultipliedAlphaModeEnabled to true.

Shader "Oculus/SelectivePassthrough"
{
    Properties
    {
        _MainTex("Texture", 2D) = "white" {}
        _Inflation("Inflation", float) = 0
        _InvertedAlpha("Inverted Alpha", float) = 1

        [Header(DepthTest)]
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest("ZTest", Float) = 4 //"LessEqual"
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" }
        LOD 100

        Pass
        {
            ZWrite Off
            ZTest[_ZTest]
            BlendOp Add
            Blend Zero SrcAlpha

            CGPROGRAM
            // Upgrade NOTE: excluded shader from DX11; has structs without semantics (struct v2f members center)
            //#pragma exclude_renderers d3d11
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                float3 normal : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float _Inflation;
            float _InvertedAlpha;

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.vertex = UnityObjectToClipPos(v.vertex + v.normal * _Inflation);
                float4 origin = mul(unity_ObjectToWorld, float4(0.0, 0.0, 0.0, 1.0));
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target {
                fixed4 col = tex2D(_MainTex, i.uv);
              float alpha = lerp(col.r, 1 - col.r, _InvertedAlpha);
                return float4(0, 0, 0, alpha);
            }
            ENDCG
        }
    }
}
