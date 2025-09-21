Shader "URP/UI/Prerendered Opaque"
{
    Properties
    {
        _MainTex("Texture", 2D) = "white" {}
        _Color("Color", Color) = (1,1,1,1)
        _AlphaToMask("AlphaToMask", Int) = 0
    }
    SubShader
    {
        PackageRequirements
        {
            "com.unity.render-pipelines.universal": "12.1" // 2021.3+
        }
        Tags {"Queue"="Geometry" "IgnoreProjector"="True" "RenderType"="Opaque"}

        Pass
        {
            ZWrite On
            Blend One Zero, Zero Zero
            AlphaToMask [_AlphaToMask]


            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ WITH_CLIP
            #pragma multi_compile _ EXPENSIVE
            #pragma multi_compile _ ALPHA_TO_MASK

            #define ALPHA_SQUARED 0
            #define ALPHA_BLEND 0
            #include "UIPrerendered.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "MotionVectors"
            Tags{ "LightMode" = "MotionVectors" "RenderPipeline" = "UniversalPipeline"}

            ZWrite On

            HLSLPROGRAM
            #pragma vertex mv_vert
            #pragma fragment mv_frag
            #pragma multi_compile _ WITH_CLIP
            #pragma multi_compile _ EXPENSIVE
            #pragma multi_compile _ ALPHA_TO_MASK

            #define ALPHA_SQUARED 0
            #define MOTION_VECTORS 1
            #include "UIPrerendered.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "XRMotionVectors"
            Tags{ "LightMode" = "XRMotionVectors" "RenderPipeline" = "UniversalPipeline"}

            ZWrite On

            HLSLPROGRAM
            #pragma vertex mv_vert
            #pragma fragment mv_frag
            #pragma multi_compile _ WITH_CLIP
            #pragma multi_compile _ EXPENSIVE
            #pragma multi_compile _ ALPHA_TO_MASK

            #define ALPHA_SQUARED 0
            #define MOTION_VECTORS 1
            #include "UIPrerendered.hlsl"
            ENDHLSL
        }
    }

    FallBack "UI/Prerendered Opaque"
}
