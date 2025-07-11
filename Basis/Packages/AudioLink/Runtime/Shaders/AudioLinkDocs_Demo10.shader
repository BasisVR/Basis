Shader "AudioLink/Examples/Demo10"
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

            const static float lut[240] = AUDIOLINK_LUT;

            float DFTSample(float bin)
            {
                float mag = AudioLinkLerpMultiline( ALPASS_DFT + float2( bin, 0 ) ).r;
                return mag / (lut[min(bin, 239)] * AUDIOLINK_TREBLE_CORRECTION + 1);
            }

            float DFTPhase(float bin)
            {
                return AudioLinkLerpMultiline( ALPASS_DFT + float2( bin, 0 ) ).a;
            }

            float WaveformSample(float sampleIndex, float period)
            {
                if (sampleIndex < 0) {
                    sampleIndex += period * ceil((0 - sampleIndex) / period);
                }
                if (sampleIndex >= AUDIOLINK_SAMPLEDATA24) {
                    sampleIndex -= period * ceil((sampleIndex - AUDIOLINK_SAMPLEDATA24 + 1) / period);
                }
                return AudioLinkLerpMultiline( ALPASS_WAVEFORM + float2( sampleIndex, 0 ) ).r;
            }


            fixed4 frag (v2f i) : SV_Target
            {

                float maxBin = 0;
                float maxValue = 0;
                for ( int bin = 0 ; bin < AUDIOLINK_ETOTALBINS; bin++)
                {
                    float curValue = DFTSample(bin);
                    if ( curValue > maxValue )
                    {
                        maxValue = curValue;
                        maxBin = bin;
                    }
                }

                float period = 24000 / ( pow( 2, maxBin / AUDIOLINK_EXPBINS ) * AUDIOLINK_BOTTOM_FREQUENCY );
                float phase = DFTPhase(maxBin);
                float angle = phase / UNITY_TWO_PI;
                float centerSampleIndex = AUDIOLINK_SAMPHIST * 0.5 -
                    ( angle + round( ( AUDIOLINK_SAMPHIST - AUDIOLINK_SAMPLEDATA24 ) * 0.5 / period ) ) * period;

                float sampleIndex = lerp( centerSampleIndex - 500, centerSampleIndex + 500, i.uv.x );
                float sample = WaveformSample( sampleIndex, period );

                return clamp( 1 - 10 * abs( sample - i.uv.y* 2. + 1 ), 0, 1 );
            }
            ENDCG
        }
    }
}
