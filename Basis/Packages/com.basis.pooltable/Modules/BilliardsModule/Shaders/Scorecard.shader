Shader "metaphira/Scorecard"
{
   Properties
   {
      _EightBallTex("Eight Ball", 2D) = "White" {}
      _NineBallTex("Nine Ball", 2D) = "White" {}
      _FourBallTex("Four Ball", 2D) = "White" {}
      [KeywordEnum(EightBall, NineBall, FourBall, FourBallKR, Snooker6Red)] _GameMode("Gamemode", Int) = 0
      _LeftScore("Left Score", Int) = 0
      _RightScore("Right Score", Int) = 0
      [KeywordEnum(Both, Left, Right)] _SolidsMode("Solids", Int) = 0
   }
   SubShader
   {
      Tags
      {
         "RenderPipeline" = "UniversalPipeline"
         "RenderType" = "Opaque"
         "Queue" = "Geometry"
      }
      LOD 300

      HLSLINCLUDE
      #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

      CBUFFER_START(UnityPerMaterial)
      int _GameMode;
      int _SolidsMode;
      int _LeftScore;
      int _RightScore;
      CBUFFER_END
      ENDHLSL

      Pass
      {
         Name "Forward"
         Tags { "LightMode" = "UniversalForward" }

         HLSLPROGRAM
         #pragma vertex vert
         #pragma fragment frag

         // GPU Instancing
         #pragma multi_compile_instancing

         struct Attributes
         {
            float4 positionOS : POSITION;
            float2 uv : TEXCOORD0;
            UNITY_VERTEX_INPUT_INSTANCE_ID
         };

         struct Varyings
         {
            float4 positionCS : SV_POSITION;
            float2 uv : TEXCOORD0;
            UNITY_VERTEX_INPUT_INSTANCE_ID
            UNITY_VERTEX_OUTPUT_STEREO
         };

         static const float4 BLACK = float4(0, 0, 0, 0);
         static const float4 WHITE = float4(1, 1, 1, 1);

         static const float OFFSET = 0.03125;

         TEXTURE2D(_EightBallTex);
         TEXTURE2D(_NineBallTex);
         TEXTURE2D(_FourBallTex);

         SamplerState sampler_linear_clamp;

         // color of each ball as it appears on the scoreboard, left to right
         static float4 _Colors[15] = {
            float4(255, 210, 0, 255) / 255, // yellow
            float4(0, 118, 227, 255) / 255, // blue
            float4(190, 13, 18, 255) / 255, // red
            float4(174, 82, 200, 255) / 255, // purple
            float4(255, 108, 0, 255) / 255, // orange
            float4(115, 229, 22, 255) / 255, // green
            float4(135, 48, 61, 255) / 255, // maroon

            float4(0, 0, 0, 255) / 255, // black

            float4(135, 48, 61, 255) / 255, // maroon
            float4(115, 229, 22, 255) / 255, // green
            float4(255, 108, 0, 255) / 255, // orange
            float4(174, 82, 200, 255) / 255, // purple
            float4(190, 13, 18, 255) / 255, // red
            float4(0, 118, 227, 255) / 255, // blue
            float4(255, 210, 0, 255) / 255 // yellow
         };

         Varyings vert(Attributes IN)
         {
            Varyings o;
            UNITY_SETUP_INSTANCE_ID(IN);
            UNITY_TRANSFER_INSTANCE_ID(IN, o);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

            o.positionCS = TransformObjectToHClip(IN.positionOS);
            o.uv = IN.uv;

            return o;
         }

         float4 frag(Varyings IN) : SV_Target
         {
            UNITY_SETUP_INSTANCE_ID(IN);
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);
            float4 base = BLACK;
            float leftEnd = 0;
            float rightEnd = 0;
            switch (_GameMode)
            {
               case 0 :
               {
                  base = SAMPLE_TEXTURE2D(_EightBallTex, sampler_linear_clamp, IN.uv);
                  leftEnd = _LeftScore * 0.0625;
                  rightEnd = _RightScore * 0.0625;
                  break;
               }
               case 1 :
               {
                  base = SAMPLE_TEXTURE2D(_NineBallTex, sampler_linear_clamp, IN.uv);
                  break;
               }
               //2 and 3 are the same
               case 2 :
               {
                  base = SAMPLE_TEXTURE2D(_FourBallTex, sampler_linear_clamp, IN.uv);
                  leftEnd = _LeftScore * 0.04681905;
                  rightEnd = _RightScore * 0.04681905;
                  break;
               }
               case 3 :
               {
                  base = SAMPLE_TEXTURE2D(_FourBallTex, sampler_linear_clamp, IN.uv);
                  leftEnd = _LeftScore * 0.04681905;
                  rightEnd = _RightScore * 0.04681905;
                  break;
               }
               default :
               {
                  return float4(0, 0, 0, 0);
               }
            }

            float4 leftComponent = BLACK;
            float leftStart = IN.uv.x - OFFSET;
            if (leftStart < leftEnd)
            {
               int index = _GameMode == 0 ? leftStart / 0.0625 : 0;
               if (_SolidsMode == 2 && round(base.b) && index != 7)
               {
                  // show stripe on left unless 8 ball
                  leftComponent = WHITE;
               }
               else
               {
                  // show color on left
                  leftComponent = _Colors[index] * round(length(base.gb));
               }
            }

            float4 rightComponent = BLACK;
            float rightStart = 1.0 - IN.uv.x - OFFSET;
            if (rightStart < rightEnd)
            {
               int index = _GameMode == 0 ? 15 - rightStart / 0.0625 : 1;
               if (_SolidsMode == 1 && round(base.b) && index != 7)
               {
                  // show stripe on right unless 8 ball
                  rightComponent = WHITE;
               }
               else
               {
                  // show color on right
                  rightComponent = _Colors[index] * round(length(base.gb));
               }
            }

            float4 white = base.rrrr;
            float4 color = leftComponent + rightComponent;

            return saturate(white + color);
         }

         ENDHLSL
      }

      Pass {
         Name "ShadowCaster"
         Tags { "LightMode" = "ShadowCaster" }

         ZWrite On
         ZTest LEqual

         HLSLPROGRAM
         #pragma vertex ShadowPassVertex
         #pragma fragment ShadowPassFragment

         // GPU Instancing
         #pragma multi_compile_instancing
         //#pragma multi_compile _ DOTS_INSTANCING_ON

         #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
         #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"
         #include "Packages/com.unity.render-pipelines.universal/Shaders/ShadowCasterPass.hlsl"

         ENDHLSL
      }

      Pass {
         Name "DepthOnly"
         Tags { "LightMode" = "DepthOnly" }

         ColorMask 0
         ZWrite On
         ZTest LEqual

         HLSLPROGRAM
         #pragma vertex DepthOnlyVertex
         #pragma fragment DepthOnlyFragment

         // GPU Instancing
         #pragma multi_compile_instancing
         //#pragma multi_compile _ DOTS_INSTANCING_ON

         #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
         #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"
         #include "Packages/com.unity.render-pipelines.universal/Shaders/DepthOnlyPass.hlsl"

         ENDHLSL
      }

      Pass {
         Name "DepthNormals"
         Tags { "LightMode" = "DepthNormals" }

         ZWrite On
         ZTest LEqual

         HLSLPROGRAM
         #pragma vertex DepthNormalsVertex
         #pragma fragment DepthNormalsFragment

         // GPU Instancing
         #pragma multi_compile_instancing
         //#pragma multi_compile _ DOTS_INSTANCING_ON

         #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
         #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"
         #include "Packages/com.unity.render-pipelines.universal/Shaders/DepthNormalsPass.hlsl"

         ENDHLSL
      }
   }
}
