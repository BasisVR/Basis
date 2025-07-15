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
      Tags { "RenderType" = "Opaque" }
      LOD 200

      Pass
      {
         CGPROGRAM

         #pragma vertex vert
         #pragma fragment frag
         // make fog work
         #pragma multi_compile_fog

         #include "UnityCG.cginc"

         struct appdata
         {
            float4 vertex : POSITION;
            float2 uv : TEXCOORD0;
         };

         struct v2f
         {
            float2 uv : TEXCOORD0;
            UNITY_FOG_COORDS(1)
            float4 vertex : SV_POSITION;
         };

         static const float4 BLACK = float4(0, 0, 0, 0);
         static const float4 WHITE = float4(1, 1, 1, 1);

         static const float OFFSET = 0.03125;

         sampler2D _EightBallTex;
         sampler2D _NineBallTex;
         sampler2D _FourBallTex;
         int _GameMode;

         int _SolidsMode;
         int _LeftScore;
         int _RightScore;

         // color of each ball as it appears on the scoreboard, left to right
         float4 _Colors[15]; /* = {
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
         };*/

         v2f vert(appdata v)
         {
            v2f o;
            o.vertex = UnityObjectToClipPos(v.vertex);
            o.uv = v.uv;
            return o;
         }

         fixed4 frag(v2f i) : SV_Target
         {
            fixed4 base;
            float leftEnd;
            float rightEnd;
            switch (_GameMode)
            {
               case 0:
               {
                  base = tex2D(_EightBallTex, i.uv);
                  leftEnd = _LeftScore * 0.0625;
                  rightEnd = _RightScore * 0.0625;
                  break;
               }
               case 1:
               {
                  base = tex2D(_NineBallTex, i.uv);
                  break;
               }
               //2 and 3 are the same
               case 2:
               {
                  base = tex2D(_FourBallTex, i.uv);
                  leftEnd = _LeftScore * 0.04681905;
                  rightEnd = _RightScore * 0.04681905;
                  break;
               }
               case 3:
               {
                  base = tex2D(_FourBallTex, i.uv);
                  leftEnd = _LeftScore * 0.04681905;
                  rightEnd = _RightScore * 0.04681905;
                  break;
               }
               default:
               {
                  return float4(0,0,0,0);
               }
            }

            fixed4 leftComponent = BLACK;
            float leftStart = i.uv.x - OFFSET;
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

            fixed4 rightComponent = BLACK;
            float rightStart = 1.0 - i.uv.x - OFFSET;
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

            fixed4 white = base.rrrr;
            fixed4 color = leftComponent + rightComponent;

            return white + color;
         }

         ENDCG
      }
   }
}
