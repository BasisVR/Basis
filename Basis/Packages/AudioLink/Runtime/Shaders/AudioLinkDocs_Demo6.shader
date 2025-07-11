
Shader "AudioLink/Examples/Demo6"
{
    Properties
    {
		_Logo ("Logo", 2D) = "" {}
		_Background("Background", Color) = (0, 0, 0, 1)
    }
    SubShader
    {
		// Allow users to make this effect transparent.
		Tags {"Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent"}
		
		Blend SrcAlpha OneMinusSrcAlpha 

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"
            #include "Packages/com.llealloo.audiolink/Runtime/Shaders/AudioLink.cginc"


			uniform float4 _Background;
            sampler2D _Logo;
            float4 _Logo_ST;
            float4 _Logo_TexelSize;
			
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
			

            // Utility function to check if a point lies in the unit square.
            float inUnit( float2 px )
            {
                //0 is minimum, 1 is maximum to check
                float2 tmp = step( 0, px ) - step( 1, px );
                return tmp.x * tmp.y;
            }
            
            
            float2 hash12(float2 n){ return frac( sin(dot(n, 4.1414)) *
				float2( 43758.5453, 38442.558 ) ); }

            fixed4 frag (v2f i) : SV_Target
            {
                // 23 and 31 LCM of 713 cycles for same corner bounce.
                const float2 collisiondiv = float2( 23, 31 );

                // Make the default size of the logo take up .2 of the overall object,
                // but let the user scale the size of their logo using the texture
                // repeat sliders.
                float2 logoSize = .2*_Logo_ST.xy;
                
                // Calculate the remaining area that the logo can bounce around.
                float2 remainder = 1. - logoSize;

                // Retrieve the instance time.
                float instanceTime = AudioLinkDecodeDataAsSeconds( ALPASS_GENERALVU_NETWORK_TIME );

                // Calculate the total progress made along X and Y irrespective of
                // the total number of bounces made.  But then compute where the
                // logo would have ended up after that long period of time.
                float2 logoUV = i.uv.xy / logoSize;
                float2 xyprogress = instanceTime * 1/collisiondiv;
                int totalbounces = floor( xyprogress * 2. ).x + floor( xyprogress * 2. ).y;
                float2 xyoffset = abs( frac( xyprogress ) * 2. - 1. );

                // Update the logo position with that location.
                logoUV -= (remainder*xyoffset)/logoSize;

                // Read that pixel.
                float4 logoTexel =  tex2D( _Logo, logoUV );
                
                // Change the color any time it would have hit a corner.
                float2 hash = hash12( totalbounces );
                
                // Abuse the colorchord hue function here to randomly color the logo.
                logoTexel.rgb *= AudioLinkHSVtoRGB( float3( hash.x, hash.y*0.5 + 0.5, 1. ) );

                // If we are looking for the logo where the logo is not
                // zero it out.
                logoTexel *= inUnit( logoUV );

                // Alpha blend the logo onto the background.
                float3 color = lerp( _Background.rgb, logoTexel.rgb, logoTexel.a ); 
                return clamp( float4( color, _Background.a + logoTexel.a ), 0, 1 );
            }
            ENDCG
        }
    }
}