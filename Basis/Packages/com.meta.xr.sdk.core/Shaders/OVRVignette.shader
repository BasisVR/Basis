Shader "Oculus/OVRVignette"
{
    Properties
    {
        _Color("Color", Color) = (0,0,0,0)
        [Enum(UnityEngine.Rendering.BlendMode)]_BlendSrc ("Blend Source", Int) = 1
        [Enum(UnityEngine.Rendering.BlendMode)]_BlendDst ("Blend Destination", Int) = 0
        [Toggle]_ZWrite ("Z Write", Int) = 0
        _StencilRef ("Stencil Ref", Int) = 0
        [Enum(UnityEngine.Rendering.StencilOp)]_StencilOp ("Stencil Op", Int) = 0
        _ColorMask ("Color Mask", Int) = 15
    }
    SubShader
    {
        Tags { "IgnoreProjector" = "True" }

        Pass
        {
            Blend [_BlendSrc] [_BlendDst]
            ZTest Always
            ZWrite [_ZWrite]
            Cull Off
            ColorMask [_ColorMask]

            Stencil
            {
                Ref [_StencilRef]
                Pass [_StencilOp]
            }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ QUADRATIC_FALLOFF
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
                float4 vertex : SV_POSITION;
                half4 color : COLOR;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float4 _ScaleAndOffset0[2];
            float4 _ScaleAndOffset1[2];
            float4 _Color;

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                float4 scaleAndOffset = lerp(_ScaleAndOffset0[unity_StereoEyeIndex], _ScaleAndOffset1[unity_StereoEyeIndex], v.uv.x);

                o.vertex = float4(scaleAndOffset.zw + v.vertex.xy * scaleAndOffset.xy, UNITY_NEAR_CLIP_VALUE, 1);

                o.color.rgb = _Color.rgb;
                o.color.a = v.uv.y;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
#if QUADRATIC_FALLOFF
                i.color.a *= i.color.a;
#endif
                return i.color;
            }
            ENDCG
        }
    }
}
