Shader "Hidden/Basis/GlobalIlluminationSurface"
{
    Properties
    {
        _BaseMap("", 2D) = "red" {}
        _MainTex("", 2D) = "white" {}
        _BaseColor("", Color) = (-1, -1, -1, -1)
        _Color("", Color) = (1, 1, 1, 1)
        _Metallic("", Float) = 0
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "BasisGISurface"
            Cull Off
            ZTest LEqual
            ZWrite On

            HLSLPROGRAM
            #pragma vertex SurfaceVert
            #pragma fragment SurfaceFrag
            #pragma target 3.5
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            #include "./BasisGlobalIlluminationSurface.hlsl"
            ENDHLSL
        }
    }

    Fallback Off
}
