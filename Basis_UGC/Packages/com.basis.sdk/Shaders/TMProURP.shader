Shader "Basis/TMPro URP"
{
    Properties
    {
        _FaceColor          ("Face Color", Color) = (1,1,1,1)
        _FaceDilate			("Face Dilate", Range(-1,1)) = 0

        _OutlineColor	    ("Outline Color", Color) = (0,0,0,1)
        _OutlineWidth		("Outline Thickness", Range(0,1)) = 0
        _OutlineSoftness	("Outline Softness", Range(0,1)) = 0

        _UnderlayColor	    ("Border Color", Color) = (0,0,0,.5)
        _UnderlayOffsetX 	("Border OffsetX", Range(-1,1)) = 0
        _UnderlayOffsetY 	("Border OffsetY", Range(-1,1)) = 0
        _UnderlayDilate		("Border Dilate", Range(-1,1)) = 0
        _UnderlaySoftness 	("Border Softness", Range(0,1)) = 0

        _WeightNormal		("Weight Normal", float) = 0
        _WeightBold			("Weight Bold", float) = .5

        _ShaderFlags		("Flags", float) = 0
        _ScaleRatioA		("Scale RatioA", float) = 1
        _ScaleRatioB		("Scale RatioB", float) = 1
        _ScaleRatioC		("Scale RatioC", float) = 1

        _MainTex			("Font Atlas", 2D) = "white" {}
        _TextureWidth		("Texture Width", float) = 512
        _TextureHeight		("Texture Height", float) = 512
        _GradientScale		("Gradient Scale", float) = 5
        _ScaleX				("Scale X", float) = 1
        _ScaleY				("Scale Y", float) = 1
        _PerspectiveFilter	("Perspective Correction", Range(0, 1)) = 0.875
        _Sharpness			("Sharpness", Range(-1,1)) = 0

        _VertexOffsetX		("Vertex OffsetX", float) = 0
        _VertexOffsetY		("Vertex OffsetY", float) = 0

        _ClipRect			("Clip Rect", vector) = (-32767, -32767, 32767, 32767)
        _MaskSoftnessX		("Mask SoftnessX", float) = 0
        _MaskSoftnessY		("Mask SoftnessY", float) = 0

        _StencilComp		("Stencil Comparison", Float) = 8
        _Stencil			("Stencil ID", Float) = 0
        _StencilOp			("Stencil Operation", Float) = 0
        _StencilWriteMask	("Stencil Write Mask", Float) = 255
        _StencilReadMask	("Stencil Read Mask", Float) = 255

        _CullMode			("Cull Mode", Float) = 0
    }
    SubShader
    {
        Tags {
            "RenderPipeline"    = "UniversalPipeline"
            "RenderType"        = "Transparent"
            "Queue"             = "Transparent"
        }

        LOD 100

        Stencil
        {
            Ref[_Stencil]
            Comp[_StencilComp]
            Pass [_StencilOp]
            ReadMask[_StencilReadMask]
            WriteMask[_StencilWriteMask]
        }

        Cull[_CullMode]
        ZWrite Off

        ZTest[unity_GUIZTestMode]
        Blend One OneMinusSrcAlpha

        Pass
        {
            Name "Forward"
            Tags {"Lightmode" = "UniversalForward"}

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #pragma multi_compile_fog

            #pragma shader_feature __ OUTLINE_ON
            #pragma shader_feature __ UNDERLAY_ON UNDERLAY_INNER

            #pragma multi_compile __ UNITY_UI_CLIP_RECT
            #pragma multi_compile __ UNITY_UI_ALPHACLIP

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct vertex
            {
                float3 position : POSITION;
                float3 normal : NORMAL;
                float4 color  : COLOR;
                float4 uv0 : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct interpolator
            {
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
    
                float4 positionCS : SV_POSITION;
                float4 wPos_fogFactor : TEXCOORD1;
                float4 texcoord0 : TEXCOORD0;
                float4 mask : TEXCOORD3;
                float4 faceColor : COLOR0;
                nointerpolation float4 outlineColor : COLOR1;
                nointerpolation float4 param : TEXCOORD2;
                 
    			#if (UNDERLAY_ON | UNDERLAY_INNER)
			    float4	texcoord1		: TEXCOORD4;			// Texture UV, alpha, reserved
			    float2	underlayParam	: TEXCOORD5;			// Scale(x), Bias(y)
			    #endif
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;

                float4 _FaceColor;
                float4 _OutlineColor;
                float4 _UnderlayColor;
                float4 _ClipRect;

                float  _FaceDilate;                
         
                float _OutlineWidth;
                float _OutlineSoftness;
  
                float _UnderlayOffsetX;
                float _UnderlayOffsetY;
                float _UnderlayDilate;
                float _UnderlaySoftness;
                
                float _WeightNormal;
                float _WeightBold;	
                
                float _ShaderFlags;	
                float _ScaleRatioA;		
                float _ScaleRatioB;		
                float _ScaleRatioC;		
                		
                float _TextureWidth;
                float _TextureHeight;
                float _GradientScale;	
                float _ScaleX;
                float _ScaleY;
                float _PerspectiveFilter;
                float _Sharpness;
                
                float _VertexOffsetX;	
                float _VertexOffsetY;
                
                float _MaskSoftnessX;
                float _MaskSoftnessY;
            CBUFFER_END

            float _UIMaskSoftnessX;
            float _UIMaskSoftnessY;
            int _UIVertexColorAlwaysGammaSpace;

            interpolator vert(vertex v)
            {
                interpolator o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

			    v.position.x += _VertexOffsetX;
			    v.position.y += _VertexOffsetY;

                o.wPos_fogFactor.xyz = TransformObjectToWorld(v.position.xyz);
                o.positionCS = TransformWorldToHClip(o.wPos_fogFactor.xyz);
                float3 viewDir = normalize(o.wPos_fogFactor.xyz - _WorldSpaceCameraPos);

                half clipZ_0Far = UNITY_Z_0_FAR_FROM_CLIPSPACE(o.positionCS.z); // normalize the clipspace z-coordinate to 1 near 0 far for platforms that have a different range of clip coordinates
                o.wPos_fogFactor.w = unity_FogParams.x * clipZ_0Far;

                float bold = step(v.uv0.w, 0);

			    float2 pixelSize = o.positionCS.w;
			    pixelSize /= float2(_ScaleX, _ScaleY) * abs(mul((float2x2)GetViewToHClipMatrix(), _ScaledScreenParams.xy));

			    float scale = rsqrt(dot(pixelSize, pixelSize));
			    scale *= abs(v.uv0.w) * _GradientScale * (_Sharpness + 1);

			    [branch] if(UNITY_MATRIX_P[3][3] == 0)
                {
                    scale = lerp(
                        abs(scale) * (1 - _PerspectiveFilter),
                        scale,
                        abs(
                            dot( TransformObjectToWorldNormal(v.normal.xyz), viewDir )
                        )
                    );
                }

			    float weight = lerp(_WeightNormal, _WeightBold, bold) / 4.0;
			    weight = (weight + _FaceDilate) * _ScaleRatioA * 0.5;

			    float layerScale = scale;

			    scale /= 1 + (_OutlineSoftness * _ScaleRatioA * scale);
			    float bias = (0.5 - weight) * scale - 0.5;
			    float outline = _OutlineWidth * _ScaleRatioA * 0.5 * scale;

                // TODO: Quest will probably be gamma space. Final blit is prohibitively expensive vs rendering to the backbuffer. 
#ifdef UNITY_COLORSPACE_GAMMA
                if (_UIVertexColorAlwaysGammaSpace)
                {
                    input.color.rgb = SRGBToLinear(input.color.rgb);
                }
#endif

                float opacity = v.color.a;
			    #if (UNDERLAY_ON | UNDERLAY_INNER)
			    opacity = 1.0;
			    #endif

			    half4 faceColor = half4(v.color.rgb, opacity) * _FaceColor;
			    faceColor.rgb *= faceColor.a;

			    half4 outlineColor = _OutlineColor;
			    outlineColor.a *= opacity;
			    outlineColor.rgb *= outlineColor.a;
			    outlineColor = lerp(faceColor, outlineColor, sqrt(min(1.0, (outline * 2))));

			    #if (UNDERLAY_ON | UNDERLAY_INNER)
			    layerScale /= 1 + ((_UnderlaySoftness * _ScaleRatioC) * layerScale);
			    float layerBias = (.5 - weight) * layerScale - .5 - ((_UnderlayDilate * _ScaleRatioC) * .5 * layerScale);

			    float x = -(_UnderlayOffsetX * _ScaleRatioC) * _GradientScale / _TextureWidth;
			    float y = -(_UnderlayOffsetY * _ScaleRatioC) * _GradientScale / _TextureHeight;
			    float2 layerOffset = float2(x, y);
			    #endif

			    // Generate UV for the Masking Texture
			    float4 clampedRect = clamp(_ClipRect, -2e10, 2e10);
			    float2 maskUV = (v.position.xy - clampedRect.xy) / (clampedRect.zw - clampedRect.xy);

			    // Populate structure for pixel shader
			    o.faceColor = faceColor;
			    o.outlineColor = outlineColor;
			    o.texcoord0 = float4(v.uv0.x, v.uv0.y, maskUV.x, maskUV.y);
			    o.param = half4(scale, bias - outline, bias + outline, bias);

			    const half2 maskSoftness = half2(max(_UIMaskSoftnessX, _MaskSoftnessX), max(_UIMaskSoftnessY, _MaskSoftnessY));
			    o.mask = half4(v.position.xy * 2 - clampedRect.xy - clampedRect.zw, 0.25 / (0.25 * maskSoftness + pixelSize.xy));
			    #if (UNDERLAY_ON || UNDERLAY_INNER)
			    o.texcoord1 = float4(v.uv0 + layerOffset, v.color.a, 0);
			    o.underlayParam = half2(layerScale, layerBias);
			    #endif


                return o;
            }

            half4 frag(interpolator input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                UNITY_SETUP_INSTANCE_ID(input);

                half d = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.texcoord0.xy).a * input.param.x;
                half4 c = input.faceColor * saturate(d - input.param.w);

			    #ifdef OUTLINE_ON
			    c = lerp(input.outlineColor, input.faceColor, saturate(d - input.param.z));
			    c *= saturate(d - input.param.y);
			    #endif

			    #if UNDERLAY_ON
			    d = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.texcoord1.xy).a * input.underlayParam.x;
			    c += float4(_UnderlayColor.rgb * _UnderlayColor.a, _UnderlayColor.a) * saturate(d - input.underlayParam.y) * (1 - c.a);
			    #endif

			    #if UNDERLAY_INNER
			    half sd = saturate(d - input.param.z);
			    d = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.texcoord1.xy).a * input.underlayParam.x;
			    c += float4(_UnderlayColor.rgb * _UnderlayColor.a, _UnderlayColor.a) * (1 - saturate(d - input.underlayParam.y)) * sd * (1 - c.a);
			    #endif

			    // Alternative implementation to UnityGet2DClipping with support for softness.
			    #if UNITY_UI_CLIP_RECT
			    half2 m = saturate((_ClipRect.zw - _ClipRect.xy - abs(input.mask.xy)) * input.mask.zw);
			    c *= m.x * m.y;
			    #endif

			    #if (UNDERLAY_ON | UNDERLAY_INNER)
			    c *= input.texcoord1.z;
			    #endif

			    #if UNITY_UI_ALPHACLIP
			    clip(c.a - 0.001);
			    #endif
    
                // apply fog
                //#if defined(FOG_LINEAR) || defined(FOG_EXP) || defined(FOG_EXP2)
                //float fogIntensity = ComputeFogIntensity(input.wPos_fogFactor.w);
                ////fogIntensity *= c.a;
                //c = lerp(float4(unity_FogColor.rgb, c.a), c, fogIntensity);
                //#endif
    
                return c;
            }
            ENDHLSL
        }
    }
    CustomEditor "TMPro.EditorUtilities.TMP_SDFShaderGUI"
}
