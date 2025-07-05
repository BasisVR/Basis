Shader "AudioLink/Debug/AudioLinkDebug"
{
    Properties
    {
        _SpectrumGain ("Spectrum Gain", Float) = 1.
        _SampleGain ("Sample Gain", Float) = 1.
        _SeparatorColor ("Seperator Color", Color) = (.5,.5,0.,1.)

        _SpectrumColorMix ("Spectrum Color Mix", Range(0, 1)) = 0

        

        _SampleColorL ("Left Waveform", Color) = (.5, .5, .9, 1.)
        _SampleColorR ("Right Waveform", Color) = (.9, .5, .5, 1.)
        _SampleColorC ("Center Waveform", Color) = (.0, .0, .0, .0)
        _SpectrumFixedColor ("Spectrum Fixed color", Color) = (.9, .9, .9,1.)
        _SpectrumFixedColorForSlow ("Spectrum Fixed color for slow", Color) = (.9, .9, .9,1.)
        _BaseColor ("Base Color", Color) = (0, 0, 0, 0)
        _UnderSpectrumColor ("Under-Spectrum Color", Color) = (1, 1, 1, .1)

        _SampleVertOffset( "Sample Vertical OFfset", Float ) = 0.0
        _SpectrumVertOffset( "Spectrum Vertical OFfset", Float ) = 0.0
        _SampleThickness ("Sample Thickness", Float) = .02
        _SpectrumThickness ("Spectrum Thickness", Float) = .01
        
        _WaveformZoom ("Waveform Zoom", Float) = 2.0
        
        _VUOpacity( "VU Opacity", Float) = 0.5
        
        [ToggleUI] _ShowVUInMain("Show VU In Main", Float) = 0
        [ToggleUI] _EnableColorChord("Show ColorChord", Float) = 0
    }
    SubShader
    {
        Tags
        {
            "RenderType"="Opaque"
        }
        LOD 100

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog

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
                UNITY_FOG_COORDS(1)
                UNITY_VERTEX_OUTPUT_STEREO
            };
            
            float _SpectrumGain;
            float _SampleGain;
            float _SpectrumColorMix;
            float4 _SeparatorColor;
            float _SampleThickness;
            float _SpectrumThickness;
        
            float _SampleVertOffset;
            float4 _SampleColorL;
            float4 _SampleColorR;
            float4 _SampleColorC;
            float4 _SpectrumFixedColor;
            float4 _SpectrumFixedColorForSlow;
            float4 _BaseColor;
            float4 _UnderSpectrumColor;
            
            float _SpectrumVertOffset;
            
            float _VUOpacity;
            float _ShowVUInMain;
            float _WaveformZoom;
            
            float _EnableColorChord;
            
            v2f vert (appdata v)
            {
                v2f o;

                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv * float2(1.25, 1.15);
                UNITY_TRANSFER_FOG(o,o.vertex);
                return o;
            }
            
            fixed4 frag (v2f i) : SV_Target
            {
                float2 iuv = i.uv;

                float4 spectrum_value = 0;

                uint noteno = iuv.x * AUDIOLINK_EXPBINS * AUDIOLINK_EXPOCT;
                float notenof = iuv.x * AUDIOLINK_EXPBINS * AUDIOLINK_EXPOCT;
                uint readno = noteno % AUDIOLINK_EXPBINS;
                float readnof = fmod(notenof, AUDIOLINK_EXPBINS);
                int reado = (noteno / AUDIOLINK_EXPBINS);
                float readof = notenof / AUDIOLINK_EXPBINS;

                spectrum_value = AudioLinkLerpMultiline(ALPASS_DFT + float2(notenof, 0)) * _SpectrumGain;

                spectrum_value.x *= 1.; // Quick, unfiltered spectrum.
                spectrum_value.y *= 1.; // Slower, filtered spectrum

                float4 coloro = _BaseColor;


                //Output any debug notes
                if (_EnableColorChord > 0.5)
                {
                    #define MAXNOTES 10
                    #define PASS_SIX_OFFSET    int2(12,22) //Pass 6: ColorChord Notes Note: This is reserved to 32,16.

                    int selnote = (int)(iuv.x * 10);
                    float4 NoteSummary = AudioLinkData(ALPASS_CCINTERNAL);
                    float4 Note = AudioLinkData(ALPASS_CCINTERNAL + uint2(selnote+1,0));

                    float intensity = clamp(Note.z * .01, 0, 1);
                    if (abs(iuv.y - intensity) < 0.05 && intensity > 0)
                    {
                        return float4(AudioLinkCCtoRGB(Note.x, 1.0, AUDIOLINK_ROOTNOTE), 1.);
                    }

                    if (iuv.y > 1)
                    {
                        #define PASS_EIGHT_OFFSET    int2(0,24)
                        //Output Linear
                        return AudioLinkData(PASS_EIGHT_OFFSET + uint2( iuv.x * 128, 0 ));
                    }
                }

                if (iuv.x < 1.)
                {
                    //The first-note-segmenters
                    float3 vertical_bars = max(0., 1.3 - length(readnof - 1.3));
                    coloro += float4(vertical_bars * _SeparatorColor, 1.);

                    //Waveform
                    // Get whole waveform would be / 1.
                    float sinpull = (AUDIOLINK_EXPBINS * AUDIOLINK_EXPOCT - 1 - notenof) / _WaveformZoom;
                    //2. zooms into the first half.
                    sinpull = clamp(sinpull, 0.5, 2045.5); //Prevent overflows.
                    float4 sinewaveval = AudioLinkLerpMultiline(ALPASS_WAVEFORM + float2(sinpull, 0)) * _SampleGain;

                    //If line has more significant slope, roll it extra wide.
                    float ddd = 1. + length(float2(ddx(sinewaveval.x), ddy(sinewaveval.y))) * 20;
                    float sinewavevalC = sinewaveval.x;
                    float sinewavevalL = sinewaveval.x + sinewaveval.a;
                    float sinewavevalR = sinewaveval.x - sinewaveval.a;
                    coloro += _SampleColorR * max(
                        100. * ((_SampleThickness * ddd) - abs(sinewavevalR - iuv.y * 2. + 1. + _SampleVertOffset)),
                        0.);
                    coloro += _SampleColorL * max(
                        100. * ((_SampleThickness * ddd) - abs(sinewavevalL - iuv.y * 2. + 1. + _SampleVertOffset)),
                        0.);
                    coloro += _SampleColorC * max(
                        100. * ((_SampleThickness * ddd) - abs(sinewavevalC - iuv.y * 2. + 1. + _SampleVertOffset)),
                        0.);

                    //Under-spectrum first
                    float rval = clamp(_SpectrumThickness - iuv.y + spectrum_value.z + _SpectrumVertOffset, 0., 1.);
                    rval = min(1., 1000 * rval);
                    coloro = lerp(coloro, _UnderSpectrumColor, rval * _UnderSpectrumColor.a);

                    //Spectrum-Line second
                    rval = max(_SpectrumThickness - abs(spectrum_value.z - iuv.y + _SpectrumVertOffset), 0.);
                    rval = min(1., 1000 * rval);
                    coloro = lerp(coloro, fixed4(lerp(AudioLinkCCtoRGB(noteno, 1.0, AUDIOLINK_ROOTNOTE),
                                                      _SpectrumFixedColor, _SpectrumColorMix), 1.0), rval);

                    //Other Spectrum-Line second
                    rval = max(_SpectrumThickness - abs(spectrum_value.x - iuv.y + _SpectrumVertOffset), 0.);
                    rval = min(1., 1000 * rval);
                    coloro = lerp(coloro, fixed4(lerp(AudioLinkCCtoRGB(noteno, 1.0, AUDIOLINK_ROOTNOTE),
                                                      _SpectrumFixedColorForSlow, _SpectrumColorMix), 1.0), rval);
                }

                //Potentially draw 
                if (_ShowVUInMain > 0.5 && iuv.x > 1 - 1 / 8. && iuv.x < 1. && iuv.y > 0.5)
                {
                    iuv.x = (((iuv.x * 8.) - 7) + 1.);
                    iuv.y = (iuv.y * 2.) - 1.;
                }

                if (iuv.x >= 1 && iuv.x < 2.)
                {
                    float UVy = iuv.y;
                    float UVx = iuv.x - 1.;


                    float Marker = 0.;
                    float Value = 0.;
                    float4 Marker4 = AudioLinkData(ALPASS_GENERALVU + int2( 9, 0 ));
                    float4 Value4 = AudioLinkData(ALPASS_GENERALVU + int2( 8, 0 ));
                    float whichVUMeter = UVx * 16;
                    if (whichVUMeter <= 2)
                    {
                        //P-P
                        if (whichVUMeter <= 1)
                        {
                            Marker = Marker4.x;
                            Value = Value4.x;
                        }
                        else
                        {
                            Marker = Marker4.y;
                            Value = Value4.y;
                        }
                    }
                    else
                    {
                        //RMS
                        if (whichVUMeter <= 3)
                        {
                            Marker = Marker4.z;
                            Value = Value4.z;
                        }
                        else
                        {
                            Marker = Marker4.w;
                            Value = Value4.w;
                        }
                    }
                    if (glsl_mod(whichVUMeter, 1.0) < 0.1)
                    {
                        Marker = 0;
                        Value = 0;
                    }

                    Marker = log(Marker) * 10.;
                    Value = log(Value) * 10.;

                    float4 VUColor = 0.;

                    int c = floor(UVy * 20);
                    float cp = glsl_mod(UVy * 20, 1.);

                    float guard_separator = 0.02;
                    float gsx = guard_separator * (.8 - 100. * length(float2(ddx(UVx), ddy(UVx)))) * 1.;
                    float gsy = guard_separator * (.8 - 100. * length(float2(ddx(UVy), ddy(UVy)))) * 1.;

                    if (UVx > 0.50 + gsx)
                    {
                        if (c > 18)
                            VUColor = float4(1., 0., 0., 1.);
                        else if (c > 15)
                            VUColor = float4(0.8, 0.8, 0., 1.);
                        else
                            VUColor = float4(0., 1., 0., 1.);
                    }
                    else if (UVx <= 0.50 - gsx)
                    {
                        if (c > 15)
                            VUColor = float4(1., 0., 0., 1.);
                        else if (c > 12)
                            VUColor = float4(0.8, 0.8, 0., 1.);
                        else
                            VUColor = float4(0., 1., 0., 1.);
                    }

                    float thisdb = (-1 + UVy) * 30;

                    float VUColorspectrum_valuesity = 0.;

                    //Historical Peak
                    if (abs(thisdb - Marker) < 0.2)
                    {
                        VUColorspectrum_valuesity = 1.;
                    }
                    else
                    {
                        if (cp > gsy * 20.)
                        {
                            if (thisdb < Value)
                            {
                                VUColorspectrum_valuesity = 0.4;
                            }
                        }
                        else
                        {
                            VUColorspectrum_valuesity = 0.02;
                        }
                    }
                    VUColor *= VUColorspectrum_valuesity;

                    coloro = lerp(VUColor, coloro, _VUOpacity);
                }

                // apply fog
                UNITY_APPLY_FOG(i.fogCoord, coloro);

                return coloro;

                //Graph-based spectrogram.
            }
            ENDCG
        }
    }
}