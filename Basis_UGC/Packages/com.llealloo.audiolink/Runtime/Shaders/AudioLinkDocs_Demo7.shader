Shader "AudioLink/Examples/Demo7"
{
    Properties
    {
        _Background("Background", Color) = (0, 0, 0, 1)
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
            
            //Based on https://www.shadertoy.com/view/Mss3Wf by PauloFalcao
            
            float GenFractal( float2 uv, float rotqty )
            {
                // Generate a 2x2 rotation matrix.  We can apply this in
                // subsequent steps.
                float2 cs;
                sincos( rotqty, cs.x, cs.y );
                float2x2 rotmat = float2x2( cs.x, -cs.y, cs.y, cs.x );


                const int maxIterations = 6;
                float circleSize = 2.0/(3.0*pow(2.0,float(maxIterations)));
            
                uv = mul( rotmat, uv*.9 );
                //uv *= cs.x * 0.5 + 1.5;
                
                //mirror, rotate and scale 6 times...
                float s= 0.3;
                for( int i=0; i < maxIterations; i++ )
                {
                    uv = abs( uv ) - s;
                    uv = mul( rotmat, uv );
                    s = s/2.1;
                }

                float intensity = length(uv) / circleSize;
                return 1.-intensity*.5;
            }
            
            float4 frag (v2f i) : SV_Target
            {
                uint2 quadrant = i.uv * 2;
                int quadrant_id = quadrant.x + quadrant.y * 2;

                int mode = 0;

                float time = AudioLinkDecodeDataAsUInt( ALPASS_CHRONOTENSITY +
                    uint2( mode, quadrant_id ) ) % 628318;

                float2 localuv = i.uv * 4 - quadrant * 2 - 1;

                float colout = GenFractal( localuv, time/100000. );
                
                colout *= max( 0, AudioLinkData( ALPASS_AUDIOLINK + uint2( 0, quadrant_id ) ) - .1 );

                return float4( colout.xxx, 1.);
            }
            ENDCG
        }
    }
}