// URP — Procedural world-space circle ring on a quad (hard edges, optimized)
Shader "Custom/WorldSpaceRingOnQuad_Hard"
{
    Properties
    {
        _Color("Color", Color) = (0.5, 0.9, 1, 0.9)
        _Radius("Ring Radius (meters)", Float) = 0.5
        _Thickness("Ring Thickness (meters)", Float) = 0.05
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest("ZTest", Float) = 4 // LEqual
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        Cull Off
        ZWrite Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        ENDHLSL

        Pass
        {
            Name "Ring"
            ZTest [_ZTest]
            Blend SrcAlpha OneMinusSrcAlpha     // use "Blend One One" for additive

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS
            #pragma multi_compile _ STEREO_MULTIVIEW_ON STEREO_INSTANCING_ON

            struct appdata
            {
                float3 vertex : POSITION;
                float2 uv     : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 posCS   : SV_POSITION;
                half2  uv      : TEXCOORD0;
                half2  wsPerUV : TEXCOORD1; // meters per +U/+V
                UNITY_VERTEX_OUTPUT_STEREO
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                half   _Radius;
                half   _Thickness;
                half   _ZTest;
            CBUFFER_END

            // Fetch object->world 3x3 (column-major in Unity)
            float3x3 GetObjToWorld3x3()
            {
                float4x4 M = GetObjectToWorldMatrix();
                return float3x3(
                    M._m00_m10_m20,
                    M._m01_m11_m21,
                    M._m02_m12_m22
                );
            }

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                float3 posWS = TransformObjectToWorld(v.vertex);
                o.posCS = TransformWorldToHClip(posWS);
                o.uv = v.uv;

                // World meters represented by a +1 step in local U/V (Unity built-in Quad is 1x1)
                float3x3 o2w = GetObjToWorld3x3();
                half metersPerU = (half)length(o2w[0]); // local +X
                half metersPerV = (half)length(o2w[1]); // local +Y
                o.wsPerUV = half2(metersPerU, metersPerV);

                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                // Center UV and convert to world meters (handles any scaling)
                half2 uvC    = i.uv - half2(0.5h, 0.5h);
                half2 offsWS = uvC * i.wsPerUV;

                // Work in squared distance to avoid sqrt
                half r2 = dot(offsWS, offsWS);

                half R     = _Radius;
                half halfT = _Thickness * 0.5h;

                half rMin  = max(0.0h, R - halfT);
                half rMax  = R + halfT;

                half rMin2 = rMin * rMin;
                half rMax2 = rMax * rMax;

                // Positive when inside the band; negative outside
                // Keep if both (r2 - rMin2) >= 0 and (rMax2 - r2) >= 0
                // The min() is negative when outside either side.
                half bandTest = min(r2 - rMin2, rMax2 - r2);

                // Hard edge: discard fragments outside the band (no soft fade)
                clip(bandTest);

                // Straight alpha (premul not needed without soft edge)
                half4 col = (half4)_Color;
                return col;
            }
            ENDHLSL
        }
    }
}
