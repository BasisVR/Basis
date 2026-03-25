Shader "Basis/UI/Background"
{
    Properties
    {
        _BaseTex("_BaseTex", 2D) = "white" {}
        _AccentTex1("_AccentTex1", 2D) = "white" {}
        _AccentTex2("_AccentTex2", 2D) = "white" {}
        _AccentTex3("_AccentTex3", 2D) = "white" {}
        _BlendFactor("_BlendFactor", Float) = 0.2
        _OffsetMultiples("_OffsetMultiples", Vector, 4) = (0.1, 0.2, 0.3, 0)
        _MaxDistance("_MaxDistance", Float) = 3
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
            
            Cull Off
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
            #pragma target 2.0
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
        float4 _OffsetMultiples;
        float _MaxDistance;
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
            
        void Unity_TilingAndOffset_float(float2 UV, float2 Tiling, float2 Offset, out float2 Out)
        {
            Out = UV * Tiling + Offset;
        }
        
        void Unity_Subtract_float3(float3 A, float3 B, out float3 Out)
        {
            Out = A - B;
        }
        
        void Unity_Multiply_float_float(float A, float B, out float Out)
        {
            Out = A * B;
        }
        
        void Unity_Clamp_float3(float3 In, float3 Min, float3 Max, out float3 Out)
        {
            Out = clamp(In, Min, Max);
        }
        
        void Unity_Combine_float(float R, float G, float B, float A, out float4 RGBA, out float3 RGB, out float2 RG)
        {
            RGBA = float4(R, G, B, A);
            RGB = float3(R, G, B);
            RG = float2(R, G);
        }
        
        void Unity_Multiply_float3_float3(float3 A, float3 B, out float3 Out)
        {
            Out = A * B;
        }
        
        void Unity_Multiply_float4_float4(float4 A, float4 B, out float4 Out)
        {
            Out = A * B;
        }
        
        void Unity_Lerp_float4(float4 A, float4 B, float4 T, out float4 Out)
        {
            Out = lerp(A, B, T);
        }
        
        void Unity_Maximum_float(float A, float B, out float Out)
        {
            Out = max(A, B);
        }
        
        void Unity_Saturate_float(float In, out float Out)
        {
            Out = saturate(In);
        }
        
        void Unity_Blend_Difference_float4(float4 Base, float4 Blend, out float4 Out, float Opacity)
        {
            Out = abs(Blend - Base);
            Out = lerp(Base, Out, Opacity);
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
            UnityTexture2D _Property_82e4a93b04984a4ab7a2574f68101212_Out_0_Texture2D = UnityBuildTexture2DStruct(_BaseTex);
            float2 _TilingAndOffset_e9d29fb247264308b7d6be0371a24fd8_Out_3_Vector2;
            Unity_TilingAndOffset_float(IN.uv0.xy, float2 (1, 1), float2 (0, 0), _TilingAndOffset_e9d29fb247264308b7d6be0371a24fd8_Out_3_Vector2);
            float4 _SampleTexture2D_c7119f54d8a045928e67bdfee81e3ee1_RGBA_0_Vector4 = SAMPLE_TEXTURE2D(_Property_82e4a93b04984a4ab7a2574f68101212_Out_0_Texture2D.tex, _Property_82e4a93b04984a4ab7a2574f68101212_Out_0_Texture2D.samplerstate, _Property_82e4a93b04984a4ab7a2574f68101212_Out_0_Texture2D.GetTransformedUV(_TilingAndOffset_e9d29fb247264308b7d6be0371a24fd8_Out_3_Vector2) );
            float _SampleTexture2D_c7119f54d8a045928e67bdfee81e3ee1_R_4_Float = _SampleTexture2D_c7119f54d8a045928e67bdfee81e3ee1_RGBA_0_Vector4.r;
            float _SampleTexture2D_c7119f54d8a045928e67bdfee81e3ee1_G_5_Float = _SampleTexture2D_c7119f54d8a045928e67bdfee81e3ee1_RGBA_0_Vector4.g;
            float _SampleTexture2D_c7119f54d8a045928e67bdfee81e3ee1_B_6_Float = _SampleTexture2D_c7119f54d8a045928e67bdfee81e3ee1_RGBA_0_Vector4.b;
            float _SampleTexture2D_c7119f54d8a045928e67bdfee81e3ee1_A_7_Float = _SampleTexture2D_c7119f54d8a045928e67bdfee81e3ee1_RGBA_0_Vector4.a;
            UnityTexture2D _Property_b8d98ff0a5b34054a8891f613078d80b_Out_0_Texture2D = UnityBuildTexture2DStruct(_AccentTex1);
            float3 _Property_86984395b88e4073ba8e79170675ead5_Out_0_Vector3 = _CursorPos;
            float3 _Transform_c90606a6bc38480c95db94d5cbdbe60c_Out_1_Vector3;
            _Transform_c90606a6bc38480c95db94d5cbdbe60c_Out_1_Vector3 = TransformWorldToView(_Property_86984395b88e4073ba8e79170675ead5_Out_0_Vector3.xyz);
            float3 _Subtract_9b008a36686c4ad098cf338de8e60530_Out_2_Vector3;
            Unity_Subtract_float3(_Transform_c90606a6bc38480c95db94d5cbdbe60c_Out_1_Vector3, float3(0, 0, 0), _Subtract_9b008a36686c4ad098cf338de8e60530_Out_2_Vector3);
            float _Property_b06e0d762783485d86750144ca408f9a_Out_0_Float = _MaxDistance;
            float _Multiply_dc59bf22285d4551ac847cc96442394b_Out_2_Float;
            Unity_Multiply_float_float(_Property_b06e0d762783485d86750144ca408f9a_Out_0_Float, -1, _Multiply_dc59bf22285d4551ac847cc96442394b_Out_2_Float);
            float3 _Clamp_f30e5a7296ec47e3bc49d2e34161fd9d_Out_3_Vector3;
            Unity_Clamp_float3(_Subtract_9b008a36686c4ad098cf338de8e60530_Out_2_Vector3, (_Multiply_dc59bf22285d4551ac847cc96442394b_Out_2_Float.xxx), (_Property_b06e0d762783485d86750144ca408f9a_Out_0_Float.xxx), _Clamp_f30e5a7296ec47e3bc49d2e34161fd9d_Out_3_Vector3);
            float4 _Property_43118c028de845259215e88e0b9a7e7e_Out_0_Vector4 = _OffsetMultiples;
            float _Split_dcae35647c474bbca4b4e1c29e4b6c1d_R_1_Float = _Property_43118c028de845259215e88e0b9a7e7e_Out_0_Vector4[0];
            float _Split_dcae35647c474bbca4b4e1c29e4b6c1d_G_2_Float = _Property_43118c028de845259215e88e0b9a7e7e_Out_0_Vector4[1];
            float _Split_dcae35647c474bbca4b4e1c29e4b6c1d_B_3_Float = _Property_43118c028de845259215e88e0b9a7e7e_Out_0_Vector4[2];
            float _Split_dcae35647c474bbca4b4e1c29e4b6c1d_A_4_Float = _Property_43118c028de845259215e88e0b9a7e7e_Out_0_Vector4[3];
            float _Multiply_f38282908064447b875273485e6bf18c_Out_2_Float;
            Unity_Multiply_float_float(_Split_dcae35647c474bbca4b4e1c29e4b6c1d_R_1_Float, -1, _Multiply_f38282908064447b875273485e6bf18c_Out_2_Float);
            float4 _Combine_f3da732965ef4728a76c5e98e5bc3573_RGBA_4_Vector4;
            float3 _Combine_f3da732965ef4728a76c5e98e5bc3573_RGB_5_Vector3;
            float2 _Combine_f3da732965ef4728a76c5e98e5bc3573_RG_6_Vector2;
            Unity_Combine_float(_Split_dcae35647c474bbca4b4e1c29e4b6c1d_R_1_Float, _Multiply_f38282908064447b875273485e6bf18c_Out_2_Float, _Split_dcae35647c474bbca4b4e1c29e4b6c1d_R_1_Float, float(0), _Combine_f3da732965ef4728a76c5e98e5bc3573_RGBA_4_Vector4, _Combine_f3da732965ef4728a76c5e98e5bc3573_RGB_5_Vector3, _Combine_f3da732965ef4728a76c5e98e5bc3573_RG_6_Vector2);
            float3 _Multiply_19d749c48b674f53ad3a11d7a9ff841a_Out_2_Vector3;
            Unity_Multiply_float3_float3(_Clamp_f30e5a7296ec47e3bc49d2e34161fd9d_Out_3_Vector3, _Combine_f3da732965ef4728a76c5e98e5bc3573_RGB_5_Vector3, _Multiply_19d749c48b674f53ad3a11d7a9ff841a_Out_2_Vector3);
            float2 _TilingAndOffset_869b6409175a4fe0bf5e32f8a82466cb_Out_3_Vector2;
            Unity_TilingAndOffset_float(IN.uv0.xy, float2 (1, 1), (_Multiply_19d749c48b674f53ad3a11d7a9ff841a_Out_2_Vector3.xy), _TilingAndOffset_869b6409175a4fe0bf5e32f8a82466cb_Out_3_Vector2);
            float4 _SampleTexture2D_fa8f450e1e5e4f77a9df7e3ad155e380_RGBA_0_Vector4 = SAMPLE_TEXTURE2D(_Property_b8d98ff0a5b34054a8891f613078d80b_Out_0_Texture2D.tex, _Property_b8d98ff0a5b34054a8891f613078d80b_Out_0_Texture2D.samplerstate, _Property_b8d98ff0a5b34054a8891f613078d80b_Out_0_Texture2D.GetTransformedUV(_TilingAndOffset_869b6409175a4fe0bf5e32f8a82466cb_Out_3_Vector2) );
            float _SampleTexture2D_fa8f450e1e5e4f77a9df7e3ad155e380_R_4_Float = _SampleTexture2D_fa8f450e1e5e4f77a9df7e3ad155e380_RGBA_0_Vector4.r;
            float _SampleTexture2D_fa8f450e1e5e4f77a9df7e3ad155e380_G_5_Float = _SampleTexture2D_fa8f450e1e5e4f77a9df7e3ad155e380_RGBA_0_Vector4.g;
            float _SampleTexture2D_fa8f450e1e5e4f77a9df7e3ad155e380_B_6_Float = _SampleTexture2D_fa8f450e1e5e4f77a9df7e3ad155e380_RGBA_0_Vector4.b;
            float _SampleTexture2D_fa8f450e1e5e4f77a9df7e3ad155e380_A_7_Float = _SampleTexture2D_fa8f450e1e5e4f77a9df7e3ad155e380_RGBA_0_Vector4.a;
            float4 _Multiply_7284efbbc23d4e2c82a60d9f35de334e_Out_2_Vector4;
            Unity_Multiply_float4_float4(_SampleTexture2D_fa8f450e1e5e4f77a9df7e3ad155e380_RGBA_0_Vector4, (_SampleTexture2D_fa8f450e1e5e4f77a9df7e3ad155e380_A_7_Float.xxxx), _Multiply_7284efbbc23d4e2c82a60d9f35de334e_Out_2_Vector4);
            UnityTexture2D _Property_32b14b9642e643d1813653321abd21e7_Out_0_Texture2D = UnityBuildTexture2DStruct(_AccentTex2);
            float _Multiply_5e0c3188ceb44046b5a1fe9def46f4c0_Out_2_Float;
            Unity_Multiply_float_float(_Split_dcae35647c474bbca4b4e1c29e4b6c1d_G_2_Float, -1, _Multiply_5e0c3188ceb44046b5a1fe9def46f4c0_Out_2_Float);
            float4 _Combine_af5e7f2400394ce0a6be0a58037424c3_RGBA_4_Vector4;
            float3 _Combine_af5e7f2400394ce0a6be0a58037424c3_RGB_5_Vector3;
            float2 _Combine_af5e7f2400394ce0a6be0a58037424c3_RG_6_Vector2;
            Unity_Combine_float(_Split_dcae35647c474bbca4b4e1c29e4b6c1d_G_2_Float, _Multiply_5e0c3188ceb44046b5a1fe9def46f4c0_Out_2_Float, _Split_dcae35647c474bbca4b4e1c29e4b6c1d_G_2_Float, float(0), _Combine_af5e7f2400394ce0a6be0a58037424c3_RGBA_4_Vector4, _Combine_af5e7f2400394ce0a6be0a58037424c3_RGB_5_Vector3, _Combine_af5e7f2400394ce0a6be0a58037424c3_RG_6_Vector2);
            float3 _Multiply_3156ac1d91de47b4a3c26de17c9c3777_Out_2_Vector3;
            Unity_Multiply_float3_float3(_Clamp_f30e5a7296ec47e3bc49d2e34161fd9d_Out_3_Vector3, _Combine_af5e7f2400394ce0a6be0a58037424c3_RGB_5_Vector3, _Multiply_3156ac1d91de47b4a3c26de17c9c3777_Out_2_Vector3);
            float2 _TilingAndOffset_3560955b265a4efd8aa4a60a971da714_Out_3_Vector2;
            Unity_TilingAndOffset_float(IN.uv0.xy, float2 (1, 1), (_Multiply_3156ac1d91de47b4a3c26de17c9c3777_Out_2_Vector3.xy), _TilingAndOffset_3560955b265a4efd8aa4a60a971da714_Out_3_Vector2);
            float4 _SampleTexture2D_591f1d7c50264437a64d364e1ad744f4_RGBA_0_Vector4 = SAMPLE_TEXTURE2D(_Property_32b14b9642e643d1813653321abd21e7_Out_0_Texture2D.tex, _Property_32b14b9642e643d1813653321abd21e7_Out_0_Texture2D.samplerstate, _Property_32b14b9642e643d1813653321abd21e7_Out_0_Texture2D.GetTransformedUV(_TilingAndOffset_3560955b265a4efd8aa4a60a971da714_Out_3_Vector2) );
            float _SampleTexture2D_591f1d7c50264437a64d364e1ad744f4_R_4_Float = _SampleTexture2D_591f1d7c50264437a64d364e1ad744f4_RGBA_0_Vector4.r;
            float _SampleTexture2D_591f1d7c50264437a64d364e1ad744f4_G_5_Float = _SampleTexture2D_591f1d7c50264437a64d364e1ad744f4_RGBA_0_Vector4.g;
            float _SampleTexture2D_591f1d7c50264437a64d364e1ad744f4_B_6_Float = _SampleTexture2D_591f1d7c50264437a64d364e1ad744f4_RGBA_0_Vector4.b;
            float _SampleTexture2D_591f1d7c50264437a64d364e1ad744f4_A_7_Float = _SampleTexture2D_591f1d7c50264437a64d364e1ad744f4_RGBA_0_Vector4.a;
            float4 _Multiply_1901c214026a47e4acdeabe4e10c4603_Out_2_Vector4;
            Unity_Multiply_float4_float4(_SampleTexture2D_591f1d7c50264437a64d364e1ad744f4_RGBA_0_Vector4, (_SampleTexture2D_591f1d7c50264437a64d364e1ad744f4_A_7_Float.xxxx), _Multiply_1901c214026a47e4acdeabe4e10c4603_Out_2_Vector4);
            float4 _Lerp_9aa8090e1c0d4a708d2526798e0545cb_Out_3_Vector4;
            Unity_Lerp_float4(_Multiply_7284efbbc23d4e2c82a60d9f35de334e_Out_2_Vector4, _Multiply_1901c214026a47e4acdeabe4e10c4603_Out_2_Vector4, (_SampleTexture2D_591f1d7c50264437a64d364e1ad744f4_A_7_Float.xxxx), _Lerp_9aa8090e1c0d4a708d2526798e0545cb_Out_3_Vector4);
            float _Split_a7d77afc6e7c44b590f14d78d6cbccef_R_1_Float = _Lerp_9aa8090e1c0d4a708d2526798e0545cb_Out_3_Vector4[0];
            float _Split_a7d77afc6e7c44b590f14d78d6cbccef_G_2_Float = _Lerp_9aa8090e1c0d4a708d2526798e0545cb_Out_3_Vector4[1];
            float _Split_a7d77afc6e7c44b590f14d78d6cbccef_B_3_Float = _Lerp_9aa8090e1c0d4a708d2526798e0545cb_Out_3_Vector4[2];
            float _Split_a7d77afc6e7c44b590f14d78d6cbccef_A_4_Float = _Lerp_9aa8090e1c0d4a708d2526798e0545cb_Out_3_Vector4[3];
            float _Maximum_ad5d3d088df04836b25c6ac4fa25b00d_Out_2_Float;
            Unity_Maximum_float(_SampleTexture2D_fa8f450e1e5e4f77a9df7e3ad155e380_A_7_Float, _SampleTexture2D_591f1d7c50264437a64d364e1ad744f4_A_7_Float, _Maximum_ad5d3d088df04836b25c6ac4fa25b00d_Out_2_Float);
            float _Saturate_bc454cb64cd3462e87c471a6cbf6df4e_Out_1_Float;
            Unity_Saturate_float(_Maximum_ad5d3d088df04836b25c6ac4fa25b00d_Out_2_Float, _Saturate_bc454cb64cd3462e87c471a6cbf6df4e_Out_1_Float);
            float4 _Vector4_ca458987d08d4135b1f7f89db40fa483_Out_0_Vector4 = float4(_Split_a7d77afc6e7c44b590f14d78d6cbccef_R_1_Float, _Split_a7d77afc6e7c44b590f14d78d6cbccef_G_2_Float, _Split_a7d77afc6e7c44b590f14d78d6cbccef_B_3_Float, _Saturate_bc454cb64cd3462e87c471a6cbf6df4e_Out_1_Float);
            UnityTexture2D _Property_52c4b975f39b4c589f3707f156b6bb06_Out_0_Texture2D = UnityBuildTexture2DStruct(_AccentTex3);
            float _Multiply_859eb13fd355492eaad7192294e82cb7_Out_2_Float;
            Unity_Multiply_float_float(_Split_dcae35647c474bbca4b4e1c29e4b6c1d_B_3_Float, -1, _Multiply_859eb13fd355492eaad7192294e82cb7_Out_2_Float);
            float4 _Combine_7f4a8942e3c54db8a17de5bcccdf8700_RGBA_4_Vector4;
            float3 _Combine_7f4a8942e3c54db8a17de5bcccdf8700_RGB_5_Vector3;
            float2 _Combine_7f4a8942e3c54db8a17de5bcccdf8700_RG_6_Vector2;
            Unity_Combine_float(_Split_dcae35647c474bbca4b4e1c29e4b6c1d_B_3_Float, _Multiply_859eb13fd355492eaad7192294e82cb7_Out_2_Float, _Split_dcae35647c474bbca4b4e1c29e4b6c1d_B_3_Float, float(0), _Combine_7f4a8942e3c54db8a17de5bcccdf8700_RGBA_4_Vector4, _Combine_7f4a8942e3c54db8a17de5bcccdf8700_RGB_5_Vector3, _Combine_7f4a8942e3c54db8a17de5bcccdf8700_RG_6_Vector2);
            float3 _Multiply_d193150d66824537858011bb4e19e9ea_Out_2_Vector3;
            Unity_Multiply_float3_float3(_Clamp_f30e5a7296ec47e3bc49d2e34161fd9d_Out_3_Vector3, _Combine_7f4a8942e3c54db8a17de5bcccdf8700_RGB_5_Vector3, _Multiply_d193150d66824537858011bb4e19e9ea_Out_2_Vector3);
            float2 _TilingAndOffset_0fbcc28f67db4e15b425f8bf87df2581_Out_3_Vector2;
            Unity_TilingAndOffset_float(IN.uv0.xy, float2 (1, 1), (_Multiply_d193150d66824537858011bb4e19e9ea_Out_2_Vector3.xy), _TilingAndOffset_0fbcc28f67db4e15b425f8bf87df2581_Out_3_Vector2);
            float4 _SampleTexture2D_e459a941bc234613a7a22e845521d79c_RGBA_0_Vector4 = SAMPLE_TEXTURE2D(_Property_52c4b975f39b4c589f3707f156b6bb06_Out_0_Texture2D.tex, _Property_52c4b975f39b4c589f3707f156b6bb06_Out_0_Texture2D.samplerstate, _Property_52c4b975f39b4c589f3707f156b6bb06_Out_0_Texture2D.GetTransformedUV(_TilingAndOffset_0fbcc28f67db4e15b425f8bf87df2581_Out_3_Vector2) );
            float _SampleTexture2D_e459a941bc234613a7a22e845521d79c_R_4_Float = _SampleTexture2D_e459a941bc234613a7a22e845521d79c_RGBA_0_Vector4.r;
            float _SampleTexture2D_e459a941bc234613a7a22e845521d79c_G_5_Float = _SampleTexture2D_e459a941bc234613a7a22e845521d79c_RGBA_0_Vector4.g;
            float _SampleTexture2D_e459a941bc234613a7a22e845521d79c_B_6_Float = _SampleTexture2D_e459a941bc234613a7a22e845521d79c_RGBA_0_Vector4.b;
            float _SampleTexture2D_e459a941bc234613a7a22e845521d79c_A_7_Float = _SampleTexture2D_e459a941bc234613a7a22e845521d79c_RGBA_0_Vector4.a;
            float4 _Multiply_cf9acb3451f042b9b1839c5f505b8d78_Out_2_Vector4;
            Unity_Multiply_float4_float4(_SampleTexture2D_e459a941bc234613a7a22e845521d79c_RGBA_0_Vector4, (_SampleTexture2D_e459a941bc234613a7a22e845521d79c_A_7_Float.xxxx), _Multiply_cf9acb3451f042b9b1839c5f505b8d78_Out_2_Vector4);
            float4 _Lerp_6eef3cfc16064571a71fffbe881dd936_Out_3_Vector4;
            Unity_Lerp_float4(_Vector4_ca458987d08d4135b1f7f89db40fa483_Out_0_Vector4, _Multiply_cf9acb3451f042b9b1839c5f505b8d78_Out_2_Vector4, (_SampleTexture2D_e459a941bc234613a7a22e845521d79c_A_7_Float.xxxx), _Lerp_6eef3cfc16064571a71fffbe881dd936_Out_3_Vector4);
            float _Split_56c5f5e363d341f5802fafe64a95a9a8_R_1_Float = _Lerp_6eef3cfc16064571a71fffbe881dd936_Out_3_Vector4[0];
            float _Split_56c5f5e363d341f5802fafe64a95a9a8_G_2_Float = _Lerp_6eef3cfc16064571a71fffbe881dd936_Out_3_Vector4[1];
            float _Split_56c5f5e363d341f5802fafe64a95a9a8_B_3_Float = _Lerp_6eef3cfc16064571a71fffbe881dd936_Out_3_Vector4[2];
            float _Split_56c5f5e363d341f5802fafe64a95a9a8_A_4_Float = _Lerp_6eef3cfc16064571a71fffbe881dd936_Out_3_Vector4[3];
            float _Maximum_9f97e56d689c4009a15a7cc5742e9313_Out_2_Float;
            Unity_Maximum_float(_SampleTexture2D_e459a941bc234613a7a22e845521d79c_A_7_Float, _Saturate_bc454cb64cd3462e87c471a6cbf6df4e_Out_1_Float, _Maximum_9f97e56d689c4009a15a7cc5742e9313_Out_2_Float);
            float _Saturate_16d35af48cb0496e9a39a0cf9c358d17_Out_1_Float;
            Unity_Saturate_float(_Maximum_9f97e56d689c4009a15a7cc5742e9313_Out_2_Float, _Saturate_16d35af48cb0496e9a39a0cf9c358d17_Out_1_Float);
            float4 _Vector4_c4e7d6ee52454afcbe5fea79e8e0b659_Out_0_Vector4 = float4(_Split_56c5f5e363d341f5802fafe64a95a9a8_R_1_Float, _Split_56c5f5e363d341f5802fafe64a95a9a8_G_2_Float, _Split_56c5f5e363d341f5802fafe64a95a9a8_B_3_Float, _Saturate_16d35af48cb0496e9a39a0cf9c358d17_Out_1_Float);
            float _Property_51fa2b65fe5f484688c3a5b27b7dca7c_Out_0_Float = _BlendFactor;
            float _Multiply_3eadde9df2574050b00fac3d783d6033_Out_2_Float;
            Unity_Multiply_float_float(_Saturate_16d35af48cb0496e9a39a0cf9c358d17_Out_1_Float, _Property_51fa2b65fe5f484688c3a5b27b7dca7c_Out_0_Float, _Multiply_3eadde9df2574050b00fac3d783d6033_Out_2_Float);
            float4 _Lerp_5b7996ecf5894d2d954ae1217b06d51c_Out_3_Vector4;
            Unity_Lerp_float4(_SampleTexture2D_c7119f54d8a045928e67bdfee81e3ee1_RGBA_0_Vector4, _Vector4_c4e7d6ee52454afcbe5fea79e8e0b659_Out_0_Vector4, (_Multiply_3eadde9df2574050b00fac3d783d6033_Out_2_Float.xxxx), _Lerp_5b7996ecf5894d2d954ae1217b06d51c_Out_3_Vector4);
            float4 Color_0dc5f24e6eb4455096f23a32ab8b326f = IsGammaSpace() ? LinearToSRGB(float4(0.08669343, 0.03644536, 0.1981132, 1)) : float4(0.08669343, 0.03644536, 0.1981132, 1);
            float4 Color_31b5fe77e4ac49849a94c8fdbc310115 = IsGammaSpace() ? LinearToSRGB(float4(0.2264151, 0.02029191, 0.2130304, 1)) : float4(0.2264151, 0.02029191, 0.2130304, 1);
            float _Float_8c90062864ac406587bae5a65094b9bf_Out_0_Float = float(0.4);
            float _Multiply_3316979e02c44f8cb01d76f9ebe62662_Out_2_Float;
            Unity_Multiply_float_float(IN.TimeParameters.y, _Float_8c90062864ac406587bae5a65094b9bf_Out_0_Float, _Multiply_3316979e02c44f8cb01d76f9ebe62662_Out_2_Float);
            float _Float_65914f2e9cd54847bc9b11a57d54d4d6_Out_0_Float = float(0.5);
            float _Multiply_34ef115fbac34823ad5aedd5ad5f37d0_Out_2_Float;
            Unity_Multiply_float_float(_Multiply_3316979e02c44f8cb01d76f9ebe62662_Out_2_Float, _Float_65914f2e9cd54847bc9b11a57d54d4d6_Out_0_Float, _Multiply_34ef115fbac34823ad5aedd5ad5f37d0_Out_2_Float);
            float4 _Lerp_b6c91e456ade40f5b53af29df5d598b5_Out_3_Vector4;
            Unity_Lerp_float4(Color_0dc5f24e6eb4455096f23a32ab8b326f, Color_31b5fe77e4ac49849a94c8fdbc310115, (_Multiply_34ef115fbac34823ad5aedd5ad5f37d0_Out_2_Float.xxxx), _Lerp_b6c91e456ade40f5b53af29df5d598b5_Out_3_Vector4);
            float4 _Blend_679fb4310ffd486e84eb5c01518b0268_Out_2_Vector4;
            Unity_Blend_Difference_float4(_Lerp_5b7996ecf5894d2d954ae1217b06d51c_Out_3_Vector4, _Lerp_b6c91e456ade40f5b53af29df5d598b5_Out_3_Vector4, _Blend_679fb4310ffd486e84eb5c01518b0268_Out_2_Vector4, float(1));
            surface.BaseColor = (_Blend_679fb4310ffd486e84eb5c01518b0268_Out_2_Vector4.xyz);
            surface.Alpha = float(1);
            surface.Emission = float3(0, 0, 0);
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
