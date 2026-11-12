Shader "Hidden/Basis/GlobalIlluminationSurface"
{
    Properties
    {
        _BaseMap("", 2D) = "red" {}
        _MainTex("", 2D) = "white" {}
        _BaseColor("", Color) = (-1, -1, -1, -1)
        _Color("", Color) = (1, 1, 1, 1)
        _Metallic("", Float) = 0
        _Cutoff("", Float) = -1
        _Surface("", Float) = 0
        _SrcBlend("", Float) = 1
        _DstBlend("", Float) = 0
        _AlphaClip("", Float) = 0
        _AlphaToMask("", Float) = 0
        _AlphaToCoverage("", Float) = 0
        _Mode("", Float) = -1
        _EnableUDIMDiscardOptions("", Float) = 0
        _UDIMDiscardCompile("", Float) = 0
        _UDIMDiscardUV("", Float) = 0
        _UDIMDiscardRow0_0("", Float) = 0
        _UDIMDiscardRow0_1("", Float) = 0
        _UDIMDiscardRow0_2("", Float) = 0
        _UDIMDiscardRow0_3("", Float) = 0
        _UDIMDiscardRow1_0("", Float) = 0
        _UDIMDiscardRow1_1("", Float) = 0
        _UDIMDiscardRow1_2("", Float) = 0
        _UDIMDiscardRow1_3("", Float) = 0
        _UDIMDiscardRow2_0("", Float) = 0
        _UDIMDiscardRow2_1("", Float) = 0
        _UDIMDiscardRow2_2("", Float) = 0
        _UDIMDiscardRow2_3("", Float) = 0
        _UDIMDiscardRow3_0("", Float) = 0
        _UDIMDiscardRow3_1("", Float) = 0
        _UDIMDiscardRow3_2("", Float) = 0
        _UDIMDiscardRow3_3("", Float) = 0
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
