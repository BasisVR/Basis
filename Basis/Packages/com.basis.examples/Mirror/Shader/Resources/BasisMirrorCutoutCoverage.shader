Shader "Hidden/Basis/MirrorCutoutCoverage"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        ZWrite Off
        ZTest Greater
        Cull Off
        ColorMask A

        Pass
        {
            Name "Coverage"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

            float4 Vert(uint vertexID : SV_VertexID) : SV_POSITION
            {
                return GetFullScreenTriangleVertexPosition(vertexID, UNITY_RAW_FAR_CLIP_VALUE);
            }

            half4 Frag() : SV_Target
            {
                return half4(0, 0, 0, 1);
            }
            ENDHLSL
        }
    }
}
