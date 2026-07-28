// Instanced unlit sphere for BasisGizmoManager. Replicates the old GizmoMaterial
// look (additive, ZTest Always, queue 4000, double-sided) with per-instance color
// so thousands of sphere gizmos render as a handful of DrawMeshInstanced calls.
Shader "Basis/GizmoSphereInstanced"
{
    Properties
    {
        _Color ("Color", Color) = (1, 1, 1, 1)
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Overlay" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Pass
        {
            Name "GizmoSphere"
            Blend One One
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half4 color : COLOR0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            UNITY_INSTANCING_BUFFER_START(GizmoProps)
                UNITY_DEFINE_INSTANCED_PROP(float4, _Color)
            UNITY_INSTANCING_BUFFER_END(GizmoProps)

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.color = half4(UNITY_ACCESS_INSTANCED_PROP(GizmoProps, _Color));
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                return half4(input.color.rgb * input.color.a, input.color.a);
            }
            ENDHLSL
        }
    }
}
