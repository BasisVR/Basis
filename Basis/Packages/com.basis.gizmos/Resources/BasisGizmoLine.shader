// Camera-facing ribbon line for BasisGizmoManager. Every line gizmo lives in one
// dynamic mesh; each vertex carries its segment's endpoints and a side sign, and
// the width expansion happens here so the CPU never rebuilds geometry per line.
// Blend/depth state mirrors the LineRenderer + Sprites/Default setup it replaces.
Shader "Basis/GizmoLine"
{
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Pass
        {
            Name "GizmoLine"
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float3 positionOS : POSITION;   // world-space anchor endpoint of this vertex
                float3 otherEnd   : TEXCOORD0;  // world-space opposite endpoint of the segment
                float2 sideWidth  : TEXCOORD1;  // x = -1/+1 ribbon side, y = half width in meters
                half4 color       : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half4 color : COLOR0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float3 anchor = input.positionOS;
                float3 seg = input.otherEnd - anchor;
                float segLen = length(seg);
                float3 segDir = segLen > 1e-5 ? seg / segLen : float3(0, 0, 1);
                float3 toView = _WorldSpaceCameraPos.xyz - anchor;
                float3 offset = cross(segDir, toView);
                float offLen = length(offset);
                offset = offLen > 1e-5
                    ? offset / offLen
                    : normalize(cross(segDir, float3(0.01, 1, 0.01)));

                float3 positionWS = anchor + offset * (input.sideWidth.x * input.sideWidth.y);
                output.positionCS = TransformWorldToHClip(positionWS);
                output.color = input.color;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                return input.color;
            }
            ENDHLSL
        }
    }
}
