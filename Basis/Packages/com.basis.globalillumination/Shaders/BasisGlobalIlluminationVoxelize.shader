Shader "Hidden/Basis/GlobalIlluminationVoxelize"
{
    Properties
    {
        [HDR] _EmissionColor("", Color) = (0, 0, 0, 0)
        _EmissionMap("", 2D) = "white" {}
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "BasisGIVoxelize"
            Cull Off
            ZTest Always
            ZWrite Off
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex VoxelVert
            #pragma fragment VoxelFrag
            #pragma target 4.5
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            #include "./BasisGlobalIlluminationVoxelize.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "BasisGIVoxelEmission"
            Cull Off
            ZTest Always
            ZWrite Off
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex VoxelEmissionVert
            #pragma fragment VoxelEmissionFrag
            #pragma target 4.5
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            #define BASISGI_VOXEL_EMISSION
            #include "./BasisGlobalIlluminationVoxelize.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "BasisGIVoxelizeGeometry"
            Cull Off
            ZTest Always
            ZWrite Off
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex VoxelVert
            #pragma geometry VoxelGeom
            #pragma fragment VoxelFrag
            #pragma target 4.5
            #pragma require geometry
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            #define BASISGI_VOXEL_GEOMETRY
            #include "./BasisGlobalIlluminationVoxelize.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "BasisGIVoxelEmissionGeometry"
            Cull Off
            ZTest Always
            ZWrite Off
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex VoxelEmissionVert
            #pragma geometry VoxelEmissionGeom
            #pragma fragment VoxelEmissionFrag
            #pragma target 4.5
            #pragma require geometry
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            #define BASISGI_VOXEL_EMISSION
            #define BASISGI_VOXEL_GEOMETRY
            #include "./BasisGlobalIlluminationVoxelize.hlsl"
            ENDHLSL
        }
    }

    Fallback Off
}
