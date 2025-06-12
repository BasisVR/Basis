Shader "AudioLink/Examples/Demo5"
{
    Properties
    {
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            #include "Packages/com.llealloo.audiolink/Runtime/Shaders/AudioLink.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
				UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float3 uvw : TEXCOORD0;
				float3 normal : TEXCOORD8;
				float3 opos : TEXCOORD9;
                float4 vertex : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert (appdata v)
            {
                v2f o;

                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
            	
                float3 vp = v.vertex;

				// Pull out the ordinal value
				int whichzone = floor(v.uv.x-1);
				
				//Only affect it if the v.uv.x was greater than or equal to 1.0
				if( whichzone >= 0 )
				{
					float alpressure = AudioLinkData( ALPASS_AUDIOLINK + int2( 0, whichzone ) ).x;
					vp.x -= alpressure * .5;
				}

				o.opos = vp;
                o.uvw = float3( frac( v.uv ), whichzone + 0.5 );                
                o.vertex = UnityObjectToClipPos(vp);
				o.normal = UnityObjectToWorldNormal( v.normal );
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
				float radius = length( i.uvw.xy - 0.5 ) * 30;
				float3 color = 0;
				if( i.uvw.z >= 0 )
				{
					// If a speaker, color it with a random ColorChord light.
					color = AudioLinkLerp( ALPASS_AUDIOLINK + float2( radius, i.uvw.z ) ).rgb * 10. + 0.5;
					
					//Adjust the coloring on the speaker by the normal
					color *= (dot(i.normal.xyz,float3(1,1,-1)))*.2;
					
					color *= AudioLinkData( ALPASS_CCLIGHTS + int2( i.uvw.z, 0) ).rgb;
				}
				else
				{
					// If the box, use the normal to color it.
					color = abs(i.normal.xyz)*.01+.02;
				}
				
                return float4( color ,1. );
            }
            ENDCG
        }
    }
}