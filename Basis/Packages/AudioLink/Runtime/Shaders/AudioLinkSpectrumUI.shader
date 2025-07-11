Shader "AudioLink/Internal/AudioLinkSpectrumUI"
{
    Properties
    {
        _SpectrumGain("Spectrum Gain", Float) = 1
        _SeparatorColor("Seperator Color", Color) = (0.5, 0.5, 0, 1)
        _SpectrumFixedColor("Spectrum Fixed color", Color) = (0.9, 0.9, 0.9, 1)
        _BaseColor("Base Color", Color) = (0, 0, 0, 0)
        _UnderSpectrumColor("Under-Spectrum Color", Color) = (1, 1, 1, 0.1)

        _SpectrumVertOffset( "Spectrum Vertical OFfset", Float ) = 0.0
        _SpectrumThickness("Spectrum Thickness", Float) = 0.01

        _SegmentThickness("Segment Thickness", Float) = 0.01
        _ThresholdThickness("Threshold Bar Thickness", Float) = 0.01
        _ThresholdDottedLine("Threshold Dotted Line Width", Float) = 0.001

        _Band0Color("Band 0 Color", Color) = (0.5 , 0.5, 0, 1)
        _Band1Color("Band 1 Color", Color) = (0.5 , 0.5, 0, 1)
        _Band2Color("Band 2 Color", Color) = (0.5 , 0.5, 0, 1)
        _Band3Color("Band 3 Color", Color) = (0.5 , 0.5, 0, 1)
        _BandDelayPulse("Band Delay Pulse", Float) = 0.1
        _BandDelayPulseOpacity("Band Delay Pulse", Float) = 0.5

        [Header(Crossover)]
        _X0("X0", Range(0.0, 0.168)) = 0.0
        _X1("X1", Range(0.242, 0.387)) = 0.25
        _X2("X2", Range(0.461, 0.628)) = 0.5
        _X3("X3", Range(0.704, 0.953)) = 0.75
        _Threshold0("Threshold 0", Range(0.0, 1.0)) = 0.45
        _Threshold1("Threshold 1", Range(0.0, 1.0)) = 0.45
        _Threshold2("Threshold 2", Range(0.0, 1.0)) = 0.45
        _Threshold3("Threshold 3", Range(0.0, 1.0)) = 0.45
        
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog

            #include "UnityCG.cginc"
            #include "AudioLink.cginc"

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
                UNITY_FOG_COORDS(1)
                UNITY_VERTEX_OUTPUT_STEREO
            };

            uniform float _X0;
            uniform float _X1;
            uniform float _X2;
            uniform float _X3;
            uniform float _Threshold0;
            uniform float _Threshold1;
            uniform float _Threshold2;
            uniform float _Threshold3;
            
            float _SpectrumGain;
            float _SpectrumColorMix;
            float4 _SeparatorColor;
            float _SpectrumThickness;
            float4 _SpectrumFixedColor;
            float4 _BaseColor;
            float4 _UnderSpectrumColor;
            float _SpectrumVertOffset;
            float _SegmentThickness;
            float _ThresholdThickness;
            float _ThresholdDottedLine;
            float4 _Band0Color;
            float4 _Band1Color;
            float4 _Band2Color;
            float4 _Band3Color;
            float _BandDelayPulse;
            float _BandDelayPulseOpacity;

            v2f vert (appdata v)
            {
                v2f o;

                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                UNITY_TRANSFER_FOG(o,o.vertex);
                return o;
            }
            
            float4 forcefilt(sampler2D sample, float4 texelsize, float2 uv)
            {
                float4 A = tex2D(sample, uv);
                float4 B = tex2D(sample, uv + float2(texelsize.x, 0));
                float4 C = tex2D(sample, uv + float2(0, texelsize.y));
                float4 D = tex2D(sample, uv + float2(texelsize.x, texelsize.y));
                float2 conv = frac(uv*texelsize.zw);
                //return float4(uv, 0., 1.);
                return lerp(
                    lerp(A, B, conv.x),
                    lerp(C, D, conv.x),
                    conv.y);
            }
            
            fixed4 frag(v2f IN) : SV_Target
            {
                float2 iuv = IN.uv;
                float audioBands[4] = {_X0, _X1, _X2, _X3};
                float audioThresholds[4] = {_Threshold0, _Threshold1, _Threshold2, _Threshold3};
                float4 intensity = 0;
                uint totalBins = AUDIOLINK_EXPBINS * AUDIOLINK_EXPOCT;
                uint noteno = AudioLinkRemap(iuv.x, 0., 1., AUDIOLINK_4BAND_FREQFLOOR * totalBins, AUDIOLINK_4BAND_FREQCEILING * totalBins);
                float notenof = AudioLinkRemap(iuv.x, 0., 1., AUDIOLINK_4BAND_FREQFLOOR * totalBins, AUDIOLINK_4BAND_FREQCEILING * totalBins);

                {
                    float4 spectrum_value_lower  =  AudioLinkData(float2(fmod(noteno, 128), (noteno/128)+4.0));
                    float4 spectrum_value_higher =  AudioLinkData(float2(fmod(noteno+1, 128), ((noteno+1)/128)+4.0));
                    intensity = lerp(spectrum_value_lower, spectrum_value_higher, frac(notenof) )* _SpectrumGain;
                }
            
                float4 c = _BaseColor;

                // Band segments
                float4 segment = 0.;
                for (int i=0; i<4; i++)
                {
                    segment += saturate(_SegmentThickness - abs(iuv.x - audioBands[i])) * 1000.;
                }

                // Band threshold lines
                float4 threshold = 0;
                float minHeight = 0.186;
                float maxHeight = 0.875;
                int band = 0;
                for (int j=1; j<4; j++)
                {
                    band += (iuv.x > audioBands[j]);
                }
                for (int k=0; k<4; k++)
                {
                    threshold += (band == k) * saturate(_ThresholdThickness - abs(iuv.y - lerp(minHeight, maxHeight, audioThresholds[k]))) * 1000.;
                }
                threshold = saturate(threshold) * (1. - round((iuv.x % _ThresholdDottedLine) / _ThresholdDottedLine));
                threshold *= (iuv.x > _X0);

                // Colored areas
                float4 bandColor = 0;
                bandColor += (band == 0) * _Band0Color;
                bandColor += (band == 1) * _Band1Color;
                bandColor += (band == 2) * _Band2Color;
                bandColor += (band == 3) * _Band3Color;
                bandColor *= (iuv.x > _X0);
                float bandIntensity = AudioLinkData(float2(0., (float)band));
                
                // Under-spectrum first
                float rval = clamp(_SpectrumThickness - iuv.y + intensity.g + _SpectrumVertOffset, 0., 1.);
                rval = min( 1., 1000*rval );
                c = lerp(c, _UnderSpectrumColor, rval * _UnderSpectrumColor.a);
                
                // Spectrum-Line second
                rval = max(_SpectrumThickness - abs(intensity.g - iuv.y + _SpectrumVertOffset), 0.);
                rval = min(1., 1000*rval);
                rval *= (iuv.x > _X0);
                c = lerp(c, _SpectrumFixedColor, rval * bandIntensity);

                // Overlay blending mode
                float4 a = c;
                float4 b = bandColor;
                // Cheap grayscale conversion
                if (0.2 * a.r + 0.7 * a.g + 0.1 * a.b < 0.5)
                {
                    c = 2. * a * b;
                } else {
                    c = 1. - 2. * (1. - a) * (1. - b);
                }
                
                float4 finalColor = (segment + threshold) * _SeparatorColor + c;
                UNITY_APPLY_FOG(IN.fogCoord, finalColor);
                return finalColor;
            }
            ENDCG
        }
    }
}
