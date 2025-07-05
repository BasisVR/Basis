// Shader written by DomNomNom. MIT License.
Shader "AudioLink/Examples/Demo8"
{
    Properties
    {
        _AudioLinkBand("AudioLink Band", Int) = 0
    }
    SubShader
    {
        // Allow users to make this effect transparent.
        Tags {"Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent"}

        Blend SrcAlpha OneMinusSrcAlpha

        LOD 100

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            #include "Packages/com.llealloo.audiolink/Runtime/Shaders/AudioLink.cginc"
            #include "Packages/com.llealloo.audiolink/Runtime/Shaders/SmoothPixelFont.cginc"


            uniform uint _AudioLinkBand;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };


            v2f vert (appdata v)
            {
                v2f o;

                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float getBandAngle(uint column)
            {
                // Implement the bottom row
                // Note: It's not performance optimal to do branching in shaders but it's quicker to develop this example.
                float v = AudioLinkData(uint2(0, _AudioLinkBand)).r;
                if (column == 0) return max(v-0.4, 0) * 2;
                if (column == 1) return v * 2 - 1;
                if (column == 2) return v > 0.4? 0 : 1;
                if (column == 3) return v > 0.4? -1 : 1;
                return 0;
            }

            float getCellAngle(uint2 grid_index)
            {
                // For the top and middle row, sample from chronotensity.
                uint2 offset = uint2(2 * grid_index.x + (grid_index.y - 1), _AudioLinkBand);
                float chronotensityAngle = (AudioLinkDecodeDataAsUInt(ALPASS_CHRONOTENSITY + offset) % 628319) / 100000.0;
                return grid_index.y == 0 ? getBandAngle(grid_index.x) : chronotensityAngle;
            }

            float4 frag (v2f i) : SV_Target
            {
                float2 grid_dimensions = float2(4,4);
                float2 uv = i.uv * grid_dimensions;
                uint2 grid_index = floor(uv);
				
				// Add labels (This part by CNL)
				if( grid_index.y == 3 )
				{
					uv *= float2( 5./4.,1 );
					float2 luv = uv;
					luv.x = 1.-uv.x;
					uint chars[] = { 'B', 'A', 'N', 'D', '0' };
					int chno = floor(uv.x);
					int ch = chars[chno];
					if( chno == 4 ) 
					{
						ch += _AudioLinkBand;
					}
					float4 c = PrintChar( ch, frac(luv)*float2(4,6), 10, 0.0 );
					return c;
				}
                float2 pos = frac(uv)*2 - 1; // position relative to the circle center.
                float angle = getCellAngle(grid_index);
                float2 direction;
                sincos(angle, direction.x, direction.y);
                return float4(
                    0.7 * lerp(
                        // If pos is aligned with direction, be white.
                        // Otherwise choose a background color based on the grid_index.
                        float3(grid_index / grid_dimensions, 0),
                        float3(1,1,1),
                        smoothstep(.995, .996, dot(direction, normalize(pos)))
                    ),
                    smoothstep(.91, .90, length(pos))  // circular cutout
                );

            }
            ENDCG
        }
    }
}
