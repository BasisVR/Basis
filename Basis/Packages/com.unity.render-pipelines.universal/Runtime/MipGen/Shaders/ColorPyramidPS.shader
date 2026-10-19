Shader "Hidden/ColorPyramidPS"
{
    SubShader
    {
        Tags{ "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            ZWrite Off ZTest Always Blend Off Cull Off
            ColorMask RGBA

            HLSLPROGRAM
                #pragma editor_sync_compilation
                #pragma target 2.0
                #pragma only_renderers d3d11 playstation xboxone xboxseries vulkan metal switch switch2 glcore gles3 webgpu
                #pragma vertex Vert
                #pragma fragment Frag
                #include_with_pragmas "ColorPyramidPS.hlsl"
            ENDHLSL
        }
    }
    Fallback Off
}
