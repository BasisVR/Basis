// Unity built-in shader source. Copyright (c) 2016 Unity Technologies. MIT license (see license.txt)

Shader "Basis/UI/Main"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)

        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255

        _ColorMask ("Color Mask", Float) = 15

        [Toggle(UNITY_UI_ALPHACLIP)] _UseUIAlphaClip ("Use Alpha Clip", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue"="Overlay"
            "IgnoreProjector"="True"
            "RenderType"="Transparent"
            "PreviewType"="Plane"
            "CanUseSpriteAtlas"="True"
        }

        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }

        Cull Off
        Lighting Off
        Fog { Mode Off }
        ZWrite Off
        ZTest Always // always render on top of everything //[unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]
        

        Pass
        {
            Name "Default"
        CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0

            #include "UnityCG.cginc"
            #include "UnityUI.cginc"

            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #pragma multi_compile_local _ UNITY_UI_ALPHACLIP

            struct appdata_t
            {
                float4 vertex   : POSITION;
                float4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex   : SV_POSITION;
                fixed4 color    : COLOR;
                float2 texcoord  : TEXCOORD0;
                float4 worldPosition : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            fixed4 _Color;
            fixed4 _TextureSampleAdd;
            float4 _ClipRect;
            float4 _MainTex_ST;

            v2f vert(appdata_t v)
            {
                v2f OUT;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                OUT.worldPosition = v.vertex;
                OUT.vertex = UnityObjectToClipPos(OUT.worldPosition);

                OUT.texcoord = TRANSFORM_TEX(v.texcoord, _MainTex);

                OUT.color = v.color * _Color;
                return OUT;
            }

            fixed4 frag(v2f IN) : SV_Target
            {
                half4 color = (tex2D(_MainTex, IN.texcoord) + _TextureSampleAdd) * IN.color;

                #ifdef UNITY_UI_CLIP_RECT
                color.a *= UnityGet2DClipping(IN.worldPosition.xy, _ClipRect);
                #endif

                #ifdef UNITY_UI_ALPHACLIP
                clip (color.a - 0.001);
                #endif

                return color;
            }
            
        ENDCG
        }
    }
}

// Shader "Basis/UI/Main"
// {
//     Properties
//     {
//         [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
//         _Color ("Tint", Color) = (1,1,1,1)

//         _StencilComp ("Stencil Comparison", Float) = 8
//         _Stencil ("Stencil ID", Float) = 0
//         _StencilOp ("Stencil Operation", Float) = 0
//         _StencilWriteMask ("Stencil Write Mask", Float) = 255
//         _StencilReadMask ("Stencil Read Mask", Float) = 255

//         _ColorMask ("Color Mask", Float) = 15

//         [Toggle(UNITY_UI_ALPHACLIP)] _UseUIAlphaClip ("Use Alpha Clip", Float) = 0
//     }

//     SubShader
//     {
//         Tags
//         {
//             "IgnoreProjector"="True"
//             "RenderType"="Transparent"
//             "PreviewType"="Plane"
//             "CanUseSpriteAtlas"="True"
//         }

//         // =========================================================
//         // PASS 1 — DEPTH ONLY (Opaque queue)
//         // =========================================================
//         Pass
//         {
//             Tags { "Queue"="Geometry+10" }

//             Cull Off
//             ZWrite On
//             ZTest LEqual
//             ColorMask 0
//             Blend Off

//             Stencil
//             {
//                 Ref [_Stencil]
//                 Comp [_StencilComp]
//                 Pass [_StencilOp]
//                 ReadMask [_StencilReadMask]
//                 WriteMask [_StencilWriteMask]
//             }

//             HLSLPROGRAM
//             #pragma vertex vertDepth
//             #pragma fragment fragDepth
//             #include "UnityCG.cginc"

//             struct appdata_t
//             {
//                 float4 vertex : POSITION;
//             };

//             struct v2f
//             {
//                 float4 pos : SV_POSITION;
//             };

//             v2f vertDepth(appdata_t v)
//             {
//                 v2f o;
//                 o.pos = UnityObjectToClipPos(v.vertex);
//                 return o;
//             }

//             float4 fragDepth(v2f i) : SV_Target
//             {
//                 return 0;
//             }

//             ENDHLSL
//         }

//         // =========================================================
//         // PASS 2 — ORIGINAL OVERLAY PASS
//         // =========================================================
//         Pass
//         {
//             Name "Default"
//             Tags { "Queue"="Overlay" }

//             Cull Off
//             Lighting Off
//             Fog { Mode Off }
//             ZWrite Off
//             ZTest Always
//             Blend SrcAlpha OneMinusSrcAlpha
//             ColorMask [_ColorMask]

//             Stencil
//             {
//                 Ref [_Stencil]
//                 Comp [_StencilComp]
//                 Pass [_StencilOp]
//                 ReadMask [_StencilReadMask]
//                 WriteMask [_StencilWriteMask]
//             }

//             CGPROGRAM
//             #pragma vertex vert
//             #pragma fragment frag
//             #pragma target 2.0
//             #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
//             #pragma multi_compile_local _ UNITY_UI_ALPHACLIP

//             #include "UnityCG.cginc"
//             #include "UnityUI.cginc"

//             struct appdata_t
//             {
//                 float4 vertex   : POSITION;
//                 float4 color    : COLOR;
//                 float2 texcoord : TEXCOORD0;
//                 UNITY_VERTEX_INPUT_INSTANCE_ID
//             };

//             struct v2f
//             {
//                 float4 vertex   : SV_POSITION;
//                 fixed4 color    : COLOR;
//                 float2 texcoord  : TEXCOORD0;
//                 float4 worldPosition : TEXCOORD1;
//                 UNITY_VERTEX_OUTPUT_STEREO
//             };

//             sampler2D _MainTex;
//             fixed4 _Color;
//             fixed4 _TextureSampleAdd;
//             float4 _ClipRect;
//             float4 _MainTex_ST;

//             v2f vert(appdata_t v)
//             {
//                 v2f OUT;
//                 UNITY_SETUP_INSTANCE_ID(v);
//                 UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
//                 OUT.worldPosition = v.vertex;
//                 OUT.vertex = UnityObjectToClipPos(OUT.worldPosition);

//                 OUT.texcoord = TRANSFORM_TEX(v.texcoord, _MainTex);

//                 OUT.color = v.color * _Color;
//                 return OUT;
//             }

//             fixed4 frag(v2f IN) : SV_Target
//             {
//                 half4 color = (tex2D(_MainTex, IN.texcoord) + _TextureSampleAdd) * IN.color;

//                 #ifdef UNITY_UI_CLIP_RECT
//                 color.a *= UnityGet2DClipping(IN.worldPosition.xy, _ClipRect);
//                 #endif

//                 #ifdef UNITY_UI_ALPHACLIP
//                 clip (color.a - 0.001);
//                 #endif

//                 return color;
//             }
            
//             ENDCG
//         }
//     }
// }