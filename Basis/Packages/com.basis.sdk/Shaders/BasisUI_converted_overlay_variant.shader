Shader "Basis/UI/Background"
{
    Properties
    {
        _BaseTex("Base Layer", 2D) = "white" {}
        _AccentTex1("Accent Layer 1", 2D) = "white" {}
        _AccentTex2("Accent Layer 2", 2D) = "white" {}
        _AccentTex3("Accent Layer 3", 2D) = "white" {}
        _BlendFactor("Accent Amount", Range(0, 1)) = 0.2
        _AccentSoftness("Accent Softness", Range(0.25, 4)) = 1.75
        _AccentFeather("Accent Feather", Range(0, 1)) = 1
        _OffsetMultiples("Parallax Multiples", Vector, 4) = (0.1, 0.2, 0.3, 0)
        _MaxDistance("Parallax Max Distance", Float) = 3
        _GradientStrength("Brand Gradient", Range(0, 1)) = 1
        _GradientCycle("Brand Gradient Seconds", Range(2, 60)) = 15
        _TimeScale("Time Scale", Range(0, 4)) = 1
        _SheenStrength("Sheen", Range(0, 1)) = 0.135
        _CursorGlowColor("Cursor Glow Color", Color) = (0.62, 0.22, 0.34, 1)
        _CursorGlow("Cursor Glow", Range(0, 2)) = 0.18
        _CursorGlowRadius("Cursor Glow Radius", Range(0.02, 2)) = 0.3
        _Vignette("Vignette", Range(0, 1)) = 0.4
        _Exposure("Exposure", Range(0.25, 3)) = 0.84
        _Grain("Grain", Range(0, 16)) = 3
        _GrainScale("Grain Scale", Range(64, 4096)) = 900
        [Enum(UnityEngine.Rendering.CullMode)] _CullMode("Cull Mode", Int) = 0
        [HideInInspector][NoScaleOffset]_MainTex("MainTex", 2D) = "white" {}
        [HideInInspector]_StencilComp("Stencil Comparison", Float) = 8
        [HideInInspector]_Stencil("Stencil ID", Float) = 0
        [HideInInspector]_StencilOp("Stencil Operation", Float) = 0
        [HideInInspector]_StencilWriteMask("Stencil Write Mask", Float) = 255
        [HideInInspector]_StencilReadMask("Stencil Read Mask", Float) = 255
        [HideInInspector]_ColorMask("ColorMask", Float) = 15
        [HideInInspector]_ClipRect("ClipRect", Vector, 4) = (0, 0, 0, 0)
        [HideInInspector]_UIMaskSoftnessX("UIMaskSoftnessX", Float) = 1
        [HideInInspector]_UIMaskSoftnessY("UIMaskSoftnessY", Float) = 1
        [HideInInspector][NoScaleOffset]unity_Lightmaps("unity_Lightmaps", 2DArray) = "" {}
        [HideInInspector][NoScaleOffset]unity_LightmapsInd("unity_LightmapsInd", 2DArray) = "" {}
        [HideInInspector][NoScaleOffset]unity_ShadowMasks("unity_ShadowMasks", 2DArray) = "" {}
    }
    SubShader
    {
        Tags
        {
            "RenderPipeline"="UniversalPipeline"
            "Queue"="Overlay"
            "IgnoreProjector"="True"
            "RenderType"="Transparent"
            "PreviewType"="Plane"
            "CanUseSpriteAtlas"="True"

            "ShaderGraphShader"="true"
            "ShaderGraphTargetId"="UniversalCanvasSubTarget"
        }
        Pass
        {
            Name "Default"
            Tags
            {
                // LightMode: <None>
            }

            // Render State
            Stencil
            {
                Ref [_Stencil]
                Comp [_StencilComp]
                Pass [_StencilOp]
                ReadMask [_StencilReadMask]
                WriteMask [_StencilWriteMask]
            }

            Cull [_CullMode]
            Lighting Off
            Fog { Mode Off }
            ZWrite Off
            ZTest Always
            Blend SrcAlpha OneMinusSrcAlpha
            ColorMask [_ColorMask]

            // Debug
            // <None>

            // --------------------------------------------------
            // Pass

            HLSLPROGRAM

            // Pragmas
            #pragma target 3.0
        #pragma vertex vert
        #pragma fragment frag

            // Keywords
            #pragma multi_compile_local _ UNITY_UI_ALPHACLIP
        #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            // GraphKeywords: <None>

            #define CANVAS_SHADERGRAPH

            // Defines
           #define _SURFACE_TYPE_TRANSPARENT 1
           #define ATTRIBUTES_NEED_NORMAL
           #define ATTRIBUTES_NEED_TEXCOORD0
           #define ATTRIBUTES_NEED_TEXCOORD1
           #define ATTRIBUTES_NEED_COLOR
           #define ATTRIBUTES_NEED_VERTEXID
           #define ATTRIBUTES_NEED_INSTANCEID
           #define VARYINGS_NEED_POSITION_WS
           #define VARYINGS_NEED_NORMAL_WS
           #define VARYINGS_NEED_TEXCOORD0
           #define VARYINGS_NEED_TEXCOORD1
           #define VARYINGS_NEED_COLOR

        #define REQUIRE_DEPTH_TEXTURE
        #define REQUIRE_NORMAL_TEXTURE

           #define SHADERPASS SHADERPASS_CUSTOM_UI

           #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Texture.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/TextureStack.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include_with_pragmas "Packages/com.unity.render-pipelines.core/ShaderLibrary/FoveatedRenderingKeywords.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/FoveatedRendering.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Input.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/DebugMipmapStreamingMacros.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/UnityInstancing.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ShaderGraphFunctions.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/SpaceTransforms.hlsl"
        #include "Packages/com.unity.shadergraph/ShaderGraphLibrary/Functions.hlsl"

            // --------------------------------------------------
            // Structs and Packing


            struct Attributes
        {
             float3 positionOS : POSITION;
             float3 normalOS : NORMAL;
             float4 color : COLOR;
             float4 uv0 : TEXCOORD0;
             float4 uv1 : TEXCOORD1;
            #if UNITY_ANY_INSTANCING_ENABLED || defined(ATTRIBUTES_NEED_INSTANCEID)
             uint instanceID : INSTANCEID_SEMANTIC;
            #endif
             uint vertexID : VERTEXID_SEMANTIC;
        };
        struct SurfaceDescriptionInputs
        {
             float4 uv0;
             float3 TimeParameters;
             float3 WorldSpacePosition;
        };
        struct Varyings
        {
             float4 positionCS : SV_POSITION;
             float3 positionWS;
             float3 normalWS;
             float4 texCoord0;
             float4 texCoord1;
             float4 color;
            #if UNITY_ANY_INSTANCING_ENABLED || defined(VARYINGS_NEED_INSTANCEID)
             uint instanceID : CUSTOM_INSTANCE_ID;
            #endif
            #if (defined(UNITY_STEREO_MULTIVIEW_ENABLED)) || (defined(UNITY_STEREO_INSTANCING_ENABLED) && (defined(SHADER_API_GLES3) || defined(SHADER_API_GLCORE)))
             uint stereoTargetEyeIndexAsBlendIdx0 : BLENDINDICES0;
            #endif
            #if (defined(UNITY_STEREO_INSTANCING_ENABLED))
             uint stereoTargetEyeIndexAsRTArrayIdx : SV_RenderTargetArrayIndex;
            #endif
        };
        struct VertexDescriptionInputs
        {
        };
        struct PackedVaryings
        {
             float4 positionCS : SV_POSITION;
             float4 texCoord0 : INTERP0;
             float4 texCoord1 : INTERP1;
             float4 color : INTERP2;
             float3 positionWS : INTERP3;
             float3 normalWS : INTERP4;
            #if UNITY_ANY_INSTANCING_ENABLED || defined(VARYINGS_NEED_INSTANCEID)
             uint instanceID : CUSTOM_INSTANCE_ID;
            #endif
            #if (defined(UNITY_STEREO_MULTIVIEW_ENABLED)) || (defined(UNITY_STEREO_INSTANCING_ENABLED) && (defined(SHADER_API_GLES3) || defined(SHADER_API_GLCORE)))
             uint stereoTargetEyeIndexAsBlendIdx0 : BLENDINDICES0;
            #endif
            #if (defined(UNITY_STEREO_INSTANCING_ENABLED))
             uint stereoTargetEyeIndexAsRTArrayIdx : SV_RenderTargetArrayIndex;
            #endif
        };

            PackedVaryings PackVaryings (Varyings input)
        {
            PackedVaryings output;
            ZERO_INITIALIZE(PackedVaryings, output);
            output.positionCS = input.positionCS;
            output.texCoord0.xyzw = input.texCoord0;
            output.texCoord1.xyzw = input.texCoord1;
            output.color.xyzw = input.color;
            output.positionWS.xyz = input.positionWS;
            output.normalWS.xyz = input.normalWS;
            #if UNITY_ANY_INSTANCING_ENABLED || defined(VARYINGS_NEED_INSTANCEID)
            output.instanceID = input.instanceID;
            #endif
            #if (defined(UNITY_STEREO_MULTIVIEW_ENABLED)) || (defined(UNITY_STEREO_INSTANCING_ENABLED) && (defined(SHADER_API_GLES3) || defined(SHADER_API_GLCORE)))
            output.stereoTargetEyeIndexAsBlendIdx0 = input.stereoTargetEyeIndexAsBlendIdx0;
            #endif
            #if (defined(UNITY_STEREO_INSTANCING_ENABLED))
            output.stereoTargetEyeIndexAsRTArrayIdx = input.stereoTargetEyeIndexAsRTArrayIdx;
            #endif
            return output;
        }

        Varyings UnpackVaryings (PackedVaryings input)
        {
            Varyings output;
            output.positionCS = input.positionCS;
            output.texCoord0 = input.texCoord0.xyzw;
            output.texCoord1 = input.texCoord1.xyzw;
            output.color = input.color.xyzw;
            output.positionWS = input.positionWS.xyz;
            output.normalWS = input.normalWS.xyz;
            #if UNITY_ANY_INSTANCING_ENABLED || defined(VARYINGS_NEED_INSTANCEID)
            output.instanceID = input.instanceID;
            #endif
            #if (defined(UNITY_STEREO_MULTIVIEW_ENABLED)) || (defined(UNITY_STEREO_INSTANCING_ENABLED) && (defined(SHADER_API_GLES3) || defined(SHADER_API_GLCORE)))
            output.stereoTargetEyeIndexAsBlendIdx0 = input.stereoTargetEyeIndexAsBlendIdx0;
            #endif
            #if (defined(UNITY_STEREO_INSTANCING_ENABLED))
            output.stereoTargetEyeIndexAsRTArrayIdx = input.stereoTargetEyeIndexAsRTArrayIdx;
            #endif
            return output;
        }


            // -- Property used by ScenePickingPass
            #ifdef SCENEPICKINGPASS
            float4 _SelectionID;
            #endif

            // -- Properties used by SceneSelectionPass
            #ifdef SCENESELECTIONPASS
            int _ObjectId;
            int _PassValue;
            #endif

            //UGUI has no keyword for when a renderer has "bloom", so its nessecary to hardcore it here, like all the base UI shaders.
            half4 _TextureSampleAdd;

            // --------------------------------------------------
            // Graph

            // Graph Properties
            CBUFFER_START(UnityPerMaterial)
        float4 _BaseTex_TexelSize;
        float4 _BaseTex_ST;
        float4 _AccentTex1_TexelSize;
        float4 _AccentTex1_ST;
        float4 _AccentTex2_TexelSize;
        float4 _AccentTex2_ST;
        float4 _AccentTex3_TexelSize;
        float4 _AccentTex3_ST;
        float _BlendFactor;
        float _AccentSoftness;
        float _AccentFeather;
        float4 _OffsetMultiples;
        float _MaxDistance;
        float _GradientStrength;
        float _GradientCycle;
        float _TimeScale;
        float _SheenStrength;
        float4 _CursorGlowColor;
        float _CursorGlow;
        float _CursorGlowRadius;
        float _Vignette;
        float _Exposure;
        float _Grain;
        float _GrainScale;
        float4 _MainTex_TexelSize;
        float _Stencil;
        float _StencilOp;
        float _StencilWriteMask;
        float _StencilReadMask;
        float _ColorMask;
        float4 _ClipRect;
        float _UIMaskSoftnessX;
        float _UIMaskSoftnessY;
        UNITY_TEXTURE_STREAMING_DEBUG_VARS;
        CBUFFER_END


        // Object and Global properties
        SAMPLER(SamplerState_Linear_Repeat);
        TEXTURE2D(_BaseTex);
        SAMPLER(sampler_BaseTex);
        TEXTURE2D(_AccentTex1);
        SAMPLER(sampler_AccentTex1);
        TEXTURE2D(_AccentTex2);
        SAMPLER(sampler_AccentTex2);
        TEXTURE2D(_AccentTex3);
        SAMPLER(sampler_AccentTex3);
        float3 _CursorPos;
        TEXTURE2D(_MainTex);
        SAMPLER(sampler_MainTex);

            // Graph Includes
            // GraphIncludes: <None>

            // Graph Functions

        float3 BasisBrandRamp(float t)
        {
            const float3 kBrandRed = float3(0.86660, 0.00296, 0.03434);
            const float3 kBrandPurple = float3(0.29723, 0.02899, 0.82836);
            const float3 kBrandBlue = float3(0.03987, 0.22744, 0.92457);
            const float3 kBrandPink = float3(0.90784, 0.04614, 0.11154);

            float s = saturate(t) * 3.0;
            float3 c = lerp(kBrandRed, kBrandPurple, saturate(s));
            c = lerp(c, kBrandBlue, saturate(s - 1.0));
            c = lerp(c, kBrandPink, saturate(s - 2.0));
            return IsGammaSpace() ? LinearToSRGB(c) : c;
        }

        float BasisTriangle(float x)
        {
            return 1.0 - abs(1.0 - 2.0 * frac(x * 0.5));
        }

        float3 BasisScreen(float3 Base, float3 Blend)
        {
            return 1.0 - (1.0 - Base) * (1.0 - saturate(Blend));
        }

        float BasisGradientNoise(float2 Position)
        {
            return frac(52.9829189 * frac(dot(Position, float2(0.06711056, 0.00583715))));
        }

        float BasisFeather(float2 TextureUV, float Amount)
        {
            float radial = saturate(1.0 - length(TextureUV - 0.5) * 2.0);
            radial = radial * radial * (3.0 - 2.0 * radial);
            return lerp(1.0, radial, Amount);
        }

            /* WARNING: $splice Could not find named fragment 'CustomInterpolatorPreVertex' */

            // Graph Vertex
            // GraphVertex: <None>

            /* WARNING: $splice Could not find named fragment 'CustomInterpolatorPreSurface' */

            // Graph Pixel
            struct SurfaceDescription
        {
            float3 BaseColor;
            float Alpha;
            float3 Emission;
        };

        SurfaceDescription SurfaceDescriptionFunction(SurfaceDescriptionInputs IN)
        {
            SurfaceDescription surface = (SurfaceDescription)0;

            float2 uv = IN.uv0.xy;
            float time = IN.TimeParameters.x * _TimeScale;

            UnityTexture2D baseTex = UnityBuildTexture2DStruct(_BaseTex);
            UnityTexture2D accentTex1 = UnityBuildTexture2DStruct(_AccentTex1);
            UnityTexture2D accentTex2 = UnityBuildTexture2DStruct(_AccentTex2);
            UnityTexture2D accentTex3 = UnityBuildTexture2DStruct(_AccentTex3);

            float3 cursorViewSpace = TransformWorldToView(_CursorPos);
            float2 parallax = clamp(cursorViewSpace, -_MaxDistance.xxx, _MaxDistance.xxx).xy;

            float3 bed = SAMPLE_TEXTURE2D(baseTex.tex, baseTex.samplerstate, baseTex.GetTransformedUV(uv)).rgb;

            float sweep = 0.5 - 0.5 * cos(time * TWO_PI / max(_GradientCycle, 0.01));
            float axis = saturate((uv.x + uv.y) * 0.5);
            float rampK = saturate(axis * 0.7 + sweep * 0.3);

            float2 accentUV1 = uv + parallax * float2(_OffsetMultiples.x, -_OffsetMultiples.x);
            float2 accentUV2 = uv + parallax * float2(_OffsetMultiples.y, -_OffsetMultiples.y);
            float2 accentUV3 = uv + parallax * float2(_OffsetMultiples.z, -_OffsetMultiples.z);

            float2 texUV1 = accentTex1.GetTransformedUV(accentUV1);
            float2 texUV2 = accentTex2.GetTransformedUV(accentUV2);
            float2 texUV3 = accentTex3.GetTransformedUV(accentUV3);

            float4 accent1 = SAMPLE_TEXTURE2D(accentTex1.tex, accentTex1.samplerstate, texUV1);
            float4 accent2 = SAMPLE_TEXTURE2D(accentTex2.tex, accentTex2.samplerstate, texUV2);
            float4 accent3 = SAMPLE_TEXTURE2D(accentTex3.tex, accentTex3.samplerstate, texUV3);

            float mask1 = pow(saturate(accent1.a) * BasisFeather(texUV1, _AccentFeather), _AccentSoftness) * _BlendFactor;
            float mask2 = pow(saturate(accent2.a) * BasisFeather(texUV2, _AccentFeather), _AccentSoftness) * _BlendFactor;
            float mask3 = pow(saturate(accent3.a) * BasisFeather(texUV3, _AccentFeather), _AccentSoftness) * _BlendFactor;

            float3 tint1 = lerp(accent1.rgb, BasisBrandRamp(BasisTriangle(rampK + 0.15)), _GradientStrength);
            float3 tint2 = lerp(accent2.rgb, BasisBrandRamp(BasisTriangle(rampK + 0.55)), _GradientStrength);
            float3 tint3 = lerp(accent3.rgb, BasisBrandRamp(BasisTriangle(rampK + 0.95)), _GradientStrength);

            float3 color = bed;
            color = BasisScreen(color, tint1 * mask1);
            color = BasisScreen(color, tint2 * mask2);
            color = BasisScreen(color, tint3 * mask3);

            float band = saturate(1.0 - abs(axis - sweep) * 3.2);
            band = band * band * (3.0 - 2.0 * band);
            color = BasisScreen(color, BasisBrandRamp(rampK) * (band * _SheenStrength));

            float cursorFalloff = saturate(1.0 - distance(IN.WorldSpacePosition, _CursorPos) / max(_CursorGlowRadius, 0.001));
            cursorFalloff = cursorFalloff * cursorFalloff * cursorFalloff;
            color = BasisScreen(color, _CursorGlowColor.rgb * (cursorFalloff * _CursorGlow));

            float2 centered = uv - 0.5;
            float vignette = 1.0 - _Vignette * smoothstep(0.12, 0.75, length(centered) * 1.41421356);
            color *= vignette * _Exposure;

            float3 encoded = sqrt(max(color, 0.0));
            encoded += (BasisGradientNoise(uv * _GrainScale) - 0.5) * (_Grain / 255.0);
            color = encoded * encoded;

            surface.BaseColor = color;
            surface.Alpha = 1.0;
            surface.Emission = float3(0.0, 0.0, 0.0);
            return surface;
        }

            // --------------------------------------------------
            // Build Graph Inputs

            SurfaceDescriptionInputs BuildSurfaceDescriptionInputs(Varyings input)
        {
            SurfaceDescriptionInputs output;
            ZERO_INITIALIZE(SurfaceDescriptionInputs, output);

            /* WARNING: $splice Could not find named fragment 'CustomInterpolatorCopyToSDI' */






            #if UNITY_UV_STARTS_AT_TOP
            #else
            #endif



        #if defined(UNITY_UIE_INCLUDED)
            output.uv0 =                                        float4(input.texCoord0.x, input.texCoord0.y, 0, 0);
        #else
            output.uv0 =                                        input.texCoord0;
        #endif




        #if UNITY_ANY_INSTANCING_ENABLED
        #else // TODO: XR support for procedural instancing because in this case UNITY_ANY_INSTANCING_ENABLED is not defined and instanceID is incorrect.
        #endif
            output.WorldSpacePosition =                         input.positionWS;
            output.TimeParameters =                             _TimeParameters.xyz; // This is mainly for LW as HD overwrite this value
        #if defined(SHADER_STAGE_FRAGMENT) && defined(VARYINGS_NEED_CULLFACE)
        #define BUILD_SURFACE_DESCRIPTION_INPUTS_OUTPUT_FACESIGN                output.FaceSign =                                   IS_FRONT_VFACE(input.cullFace, true, false);
        #else
        #define BUILD_SURFACE_DESCRIPTION_INPUTS_OUTPUT_FACESIGN
        #endif
        #undef BUILD_SURFACE_DESCRIPTION_INPUTS_OUTPUT_FACESIGN

            return output;
        }

            // --------------------------------------------------
            // Main

            #include "Packages/com.unity.render-pipelines.universal/Editor/ShaderGraph/Includes/CanvasPass.hlsl"

            ENDHLSL
        }
    }
    CustomEditor "UnityEditor.ShaderGraph.GenericShaderGraphMaterialGUI"
    FallBack "Hidden/Shader Graph/FallbackError"
}
