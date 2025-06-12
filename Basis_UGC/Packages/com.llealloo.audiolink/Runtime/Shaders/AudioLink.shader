Shader "AudioLink/Internal/AudioLink"
{
    Properties
    {
        _Gain("Gain", Range(0, 2)) = 1.0
        _FadeLength("Fade Length", Range(0 , 1)) = 0.8
        _FadeExpFalloff("Fade Exp Falloff", Range(0 , 1)) = 0.3
        _Bass("Bass", Range(0 , 4)) = 1.0
        _Treble("Treble", Range(0 , 4)) = 1.0
        _X0("X0", Range(0.0, 0.168)) = 0.25
        _X1("X1", Range(0.242, 0.387)) = 0.25
        _X2("X2", Range(0.461, 0.628)) = 0.5
        _X3("X3", Range(0.704, 0.953)) = 0.75
        _Threshold0("Threshold 0", Range(0.0, 1.0)) = 0.45
        _Threshold1("Threshold 1", Range(0.0, 1.0)) = 0.45
        _Threshold2("Threshold 2", Range(0.0, 1.0)) = 0.45
        _Threshold3("Threshold 3", Range(0.0, 1.0)) = 0.45
        [ToggleUI] _Autogain("Enable Autogain", float) = 1
        _AutogainDerate ("Autogain Derate", Range(.001, .5)) = 0.1
        _SourceVolume("Audio Source Volume", float) = 1
        _SourceDistance("Distance to Source", float) = 1
        _SourceSpatialBlend("Spatial Blend", float) = 0 //0-1 = 2D -> 3D curve
        _SourcePosition ("Source Position", Vector) = (0,0,0,0)
        
        _ThemeColorMode( "Theme Color Mode", int ) = 0
        _CustomThemeColor0 ("Theme Color 0", Color ) = (1.0,1.0,0.0,1.0)
        _CustomThemeColor1 ("Theme Color 1", Color ) = (0.0,0.0,1.0,1.0)
        _CustomThemeColor2 ("Theme Color 2", Color ) = (1.0,0.0,0.0,1.0)
        _CustomThemeColor3 ("Theme Color 3", Color ) = (0.0,1.0,0.0,1.0)

        [Enum(None,0,Playing,1,Paused,2,Stopped,3,Loading,4,Streaming,5,Error,6)] _MediaPlaying ("Media Playing", Float) = 0
        [Enum(None,0,Loop,1,Loop One,2,Random,3,Random Loop,4)] _MediaLoop ("Media Loop", Float) = 0
        _MediaVolume ("Media Volume", Range(0, 1)) = 0
        _MediaTime ("Media Time (Progress %)", Range(0, 1)) = 0
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest Always

        Pass
        {
            CGINCLUDE
            //On Quest UNITY_UV_STARTS_AT_TOP is false but actual uv behaves as it's true breaking entire audio texture,same issue occur in editor on OpenGL ES mode
            //So SHADER_API_GLES3 is included to fix texture on Quest, in case of same issue occuring on PC entire conditioning here need to be removed.
            #if UNITY_UV_STARTS_AT_TOP || SHADER_API_GLES3 || SHADER_API_GLCORE
                #define AUDIO_LINK_ALPHA_START(BASECOORDY) \
                float2 guv = IN.globalTexcoord.xy; \
                uint2 coordinateGlobal = round(guv * _SelfTexture2D_TexelSize.zw - 0.5); \
                uint2 coordinateLocal = uint2(coordinateGlobal.x - BASECOORDY.x, coordinateGlobal.y - BASECOORDY.y);
            #else
                #define AUDIO_LINK_ALPHA_START(BASECOORDY) \
                float2 guv = IN.globalTexcoord.xy; \
                guv.y = 1.-guv.y; \
                uint2 coordinateGlobal = round(guv * _SelfTexture2D_TexelSize.zw - 0.5); \
                uint2 coordinateLocal = uint2(coordinateGlobal.x - BASECOORDY.x, coordinateGlobal.y - BASECOORDY.y);
            #endif

            #pragma target 4.0
            #pragma vertex CustomRenderTextureVertexShader
            #pragma fragment frag

            // This changes _SelfTexture2D in 'UnityCustomRenderTexture.cginc' to Texture2D instead of sampler2D
            // Thanks Lyuma!
            #define _SelfTexture2D _JunkTexture
            #include "UnityCustomRenderTexture.cginc"
            #undef _SelfTexture2D
            Texture2D<float4>   _SelfTexture2D;

            #include "UnityCG.cginc"
            #include "AudioLink.cginc"
            uniform half4 _SelfTexture2D_TexelSize;

            // AudioLink 4 Band
            uniform float _FadeLength;
            uniform float _FadeExpFalloff;
            uniform float _Gain;
            uniform float _Bass;
            uniform float _Treble;
            uniform float _X0;
            uniform float _X1;
            uniform float _X2;
            uniform float _X3;
            uniform float _Threshold0;
            uniform float _Threshold1;
            uniform float _Threshold2;
            uniform float _Threshold3;
            uniform float _SourceVolume;
            uniform float _SourceDistance;
            uniform float _SourceSpatialBlend;
            uniform float4 _SourcePosition;
            uniform uint _ThemeColorMode;
            uniform float4 _CustomThemeColor0;
            uniform float4 _CustomThemeColor1;
            uniform float4 _CustomThemeColor2;
            uniform float4 _CustomThemeColor3;

            // Global strings
            uniform float4 _StringLocalPlayer[8];
            uniform float4 _StringMasterPlayer[8];
            uniform float4 _StringCustom1[8];
            uniform float4 _StringCustom2[8];

            // Extra Properties
            uniform float _Autogain;
            uniform float _AutogainDerate;

            // Set by Udon
            uniform float4 _AdvancedTimeProps0;
            uniform float4 _AdvancedTimeProps1;
            uniform float4 _VersionNumberAndFPSProperty;
            uniform float4 _PlayerCountAndData;

            //Set by Udon for states
            uniform float _MediaPlaying;
            uniform float _MediaLoop;
            uniform float _MediaVolume;
            uniform float _MediaTime;

            //Raw audio data.
            cbuffer LeftSampleBuffer {
                float _Samples0L[1023];
                float _Samples1L[1023];
                float _Samples2L[1023];
                float _Samples3L[1023];
            };
            cbuffer RightSampleBuffer {
                float _Samples0R[1023];
                float _Samples1R[1023];
                float _Samples2R[1023];
                float _Samples3R[1023];
            };

            // These may become uniforms set by the controller, keep them named like this for now
            const static float _LogAttenuation = 0.68;
            const static float _ContrastSlope = 0.63;
            const static float _ContrastOffset = 0.62;

            const static float _WaveInClampValue = 2.0;

            // Encodes a uint so it can be read-out by AudioLinkDecodeDataAsUInt().
            inline float4 AudioLinkEncodeUInt(uint val)
            {
                return float4(
                    (float)((val) & 0x3ff),
                    (float)((val >> 10) & 0x3ff),
                    (float)((val >> 20) & 0x3ff),
                    (float)((val >> 30) & 0x3ff)
                );
            }
            ENDCG

            Name "Pass1AudioDFT"
            CGPROGRAM
            const static float lut[240] = {0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0,
                0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0,
                0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.001, 0.002, 0.003, 0.004, 0.005, 0.006, 0.008, 0.01,
                0.012, 0.014, 0.017, 0.02, 0.022, 0.025, 0.029, 0.032, 0.036, 0.04, 0.044, 0.048, 0.053, 0.057, 0.062, 0.067, 0.072, 0.078, 0.083, 0.089,
                0.095, 0.101, 0.107, 0.114, 0.121, 0.128, 0.135, 0.142, 0.149, 0.157, 0.164, 0.172, 0.18, 0.188, 0.196, 0.205, 0.213, 0.222, 0.23, 0.239,
                0.248, 0.257, 0.266, 0.276, 0.285, 0.294, 0.304, 0.313, 0.323, 0.333, 0.342, 0.352, 0.362, 0.372, 0.381, 0.391, 0.401, 0.411, 0.421, 0.431,
                0.441, 0.451, 0.46, 0.47, 0.48, 0.49, 0.499, 0.509, 0.519, 0.528, 0.538, 0.547, 0.556, 0.565, 0.575, 0.584, 0.593, 0.601, 0.61, 0.619,
                0.627, 0.636, 0.644, 0.652, 0.66, 0.668, 0.676, 0.684, 0.691, 0.699, 0.706, 0.713, 0.72, 0.727, 0.734, 0.741, 0.747, 0.754, 0.76, 0.766,
                0.772, 0.778, 0.784, 0.79, 0.795, 0.801, 0.806, 0.811, 0.816, 0.821, 0.826, 0.831, 0.835, 0.84, 0.844, 0.848, 0.853, 0.857, 0.861, 0.864,
                0.868, 0.872, 0.875, 0.879, 0.882, 0.885, 0.888, 0.891, 0.894, 0.897, 0.899, 0.902, 0.904, 0.906, 0.909, 0.911, 0.913, 0.914, 0.916, 0.918,
                0.919, 0.921, 0.922, 0.924, 0.925, 0.926, 0.927, 0.928, 0.928, 0.929, 0.929, 0.93, 0.93, 0.93, 0.931, 0.931, 0.93, 0.93, 0.93, 0.93,
                0.929, 0.929, 0.928, 0.927, 0.926, 0.925, 0.924, 0.923, 0.922, 0.92, 0.919, 0.917, 0.915, 0.913, 0.911, 0.909, 0.907, 0.905, 0.903, 0.9};

            float4 frag (v2f_customrendertexture IN) : SV_Target
            {
                AUDIO_LINK_ALPHA_START(ALPASS_DFT)

                int note = coordinateLocal.y * AUDIOLINK_WIDTH + coordinateLocal.x;
                float4 last = AudioLinkGetSelfPixelData(coordinateGlobal);
                float2 amplitude = 0.;
                float phase = 0;
                float phaseDelta = pow(2, (note)/((float)AUDIOLINK_EXPBINS));
                phaseDelta = ((phaseDelta * AUDIOLINK_BOTTOM_FREQUENCY) / AUDIOLINK_SPS) * UNITY_TWO_PI * 2.; // 2 here because we're at 24kSPS
                phase = -phaseDelta * AUDIOLINK_SAMPHIST/2;     // Align phase so 0 phase is center of window.

                // DFT Window
                float halfWindowSize = AUDIOLINK_DFT_Q / (phaseDelta / UNITY_TWO_PI);
                int windowRange = floor(halfWindowSize) + 1;
                float totalWindow = 0;

                // For ??? reason, this is faster than doing a clever indexing which only searches the space that will be used.
                uint idx;
                for(idx = 0; idx < AUDIOLINK_SAMPHIST / 2; idx++)
                {
                    // XXX TODO: Try better windows, this is just a triangle.
                    float window = max(0, halfWindowSize - abs(idx - (AUDIOLINK_SAMPHIST / 2 - halfWindowSize)));
                    float af = AudioLinkGetSelfPixelData(ALPASS_WAVEFORM + uint2(idx % AUDIOLINK_WIDTH, idx / AUDIOLINK_WIDTH)).x;

                    // Sin and cosine components to convolve.
                    float2 sinCos; sincos(phase, sinCos.x, sinCos.y);

                    // Step through, one sample at a time, multiplying the sin and cos values by the incoming signal.
                    amplitude += sinCos * af * window;
                    totalWindow += window;
                    phase += phaseDelta;
                }
                float mag = (length(amplitude) / totalWindow) * AUDIOLINK_BASE_AMPLITUDE * _Gain;

                // Treble compensation
                mag *= (lut[min(note, 239)] * AUDIOLINK_TREBLE_CORRECTION + 1);

                // Filtered output, also use FadeLength to lerp delay coefficient min/max for added smoothing effect
                float magFilt = lerp(mag, last.z, lerp(AUDIOLINK_DELAY_COEFFICIENT_MIN, AUDIOLINK_DELAY_COEFFICIENT_MAX, _FadeLength));

                // Filtered EQ'd output, used by AudioLink 4 Band
                float freqNormalized = note / float(AUDIOLINK_EXPOCT * AUDIOLINK_EXPBINS);
                float magEQ = magFilt * (((1.0 - freqNormalized) * _Bass) + (freqNormalized * _Treble));

                // Red:   Spectrum power, served straight up
                // Green: Filtered power EQ'd, used by AudioLink 4 Band
                // Blue:  Filtered spectrum
                return float4(mag, magEQ, magFilt, 1);
            }
            ENDCG
        }

        Pass
        {
            Name "Pass2WaveformData"
            CGPROGRAM

            float ReadLeft( int sample )
            {
                if( sample < 1023 )
                    return _Samples0L[sample];
                else if( sample < 2046 )
                    return _Samples1L[sample-1023];
                else if( sample < 3069 )
                    return _Samples2L[sample-2046];
                else if( sample < 4092 )
                    return _Samples3L[sample-3069];
                else
                    return 0.;
            }
            float ReadRight( int sample )
            {
                if( sample < 1023 )
                    return _Samples0R[sample];
                else if( sample < 2046 )
                    return _Samples1R[sample-1023];
                else if( sample < 3069 )
                    return _Samples2R[sample-2046];
                else if( sample < 4092 )
                    return _Samples3R[sample-3069];
                else
                    return 0.;
            }

            float4 frag (v2f_customrendertexture IN) : SV_Target
            {
                AUDIO_LINK_ALPHA_START(ALPASS_WAVEFORM)
                int frame = coordinateLocal.x + coordinateLocal.y * AUDIOLINK_WIDTH;

                float incomingGain = 1;
                // Scales the gain by the audio source component Volume to prevent data changing when changing the volume.
                // Clamped to 0.001 to prevent division by 0 because that will make it 'splode and we don't want that now do we?
                incomingGain *= 1/clamp(_SourceVolume, 0.001, 1);
                if(_Autogain)
                {
                    float4 lastAutoGain = AudioLinkGetSelfPixelData(ALPASS_GENERALVU + int2(11, 0));

                    // Divide by the running volume.
                    incomingGain *= 1. / (lastAutoGain.x + _AutogainDerate);
                }

                // Downsampled to 24k and 12k samples per second by averaging, limiting frame to prevent overflow
                float4 ret = 0; // [ downsampled 24k mono, native 48k mono, down sampled to 12k mono, difference between left and right at 24k]
                if( frame < 2046 )
                {
                    ret.x = (
                            ReadLeft(frame * 2 + 0) + ReadRight(frame * 2 + 0) +
                            ReadLeft(frame * 2 + 1) + ReadRight(frame * 2 + 1) ) / 4.;
                }
                if( frame < 4092 )
                {
                    ret.y = ( ReadLeft(frame) + ReadRight(frame) ) / 2.;
                }
                if( frame < 1023 )
                {
                    ret.z = (
                            ReadLeft(frame * 4 + 0) + ReadRight(frame * 4 + 0) +
                            ReadLeft(frame * 4 + 1) + ReadRight(frame * 4 + 1) +
                            ReadLeft(frame * 4 + 2) + ReadRight(frame * 4 + 2) +
                            ReadLeft(frame * 4 + 3) + ReadRight(frame * 4 + 3) ) / 8.;
                }
                if( frame < 2046 )
                {
                    ret.w = (
                            ReadLeft(frame * 2 + 0) - ReadRight(frame * 2 + 0) +
                            ReadLeft(frame * 2 + 1) - ReadRight(frame * 2 + 1) ) / 4.;
                }

                return clamp( ret * incomingGain, -_WaveInClampValue, _WaveInClampValue );
            }
            ENDCG
        }

        Pass
        {
            Name "Pass3AudioLink4Band"
            CGPROGRAM

            float4 frag (v2f_customrendertexture IN) : SV_Target
            {
                AUDIO_LINK_ALPHA_START(ALPASS_AUDIOLINK)

                float audioBands[4] = {_X0, _X1, _X2, _X3};
                float audioThresholds[4] = {_Threshold0, _Threshold1, _Threshold2, _Threshold3};

                int band = min(coordinateLocal.y, 3);
                int delay = coordinateLocal.x;
                if (delay == 0)
                {
                    // Get average of samples in the band
                    float total = 0.;
                    uint totalBins = AUDIOLINK_EXPBINS * AUDIOLINK_EXPOCT;
                    uint binStart = AudioLinkRemap(audioBands[band], 0., 1., AUDIOLINK_4BAND_FREQFLOOR * totalBins, AUDIOLINK_4BAND_FREQCEILING * totalBins);
                    uint binEnd = (band != 3) ? AudioLinkRemap(audioBands[band + 1], 0., 1., AUDIOLINK_4BAND_FREQFLOOR * totalBins, AUDIOLINK_4BAND_FREQCEILING * totalBins) : AUDIOLINK_4BAND_FREQCEILING * totalBins;
                    float threshold = audioThresholds[band];
                    for (uint i=binStart; i<binEnd; i++)
                    {
                        int2 spectrumCoord = int2(i % AUDIOLINK_WIDTH, i / AUDIOLINK_WIDTH);
                        float rawMagnitude = AudioLinkGetSelfPixelData(ALPASS_DFT + spectrumCoord).y;
                        total += rawMagnitude;
                    }
                    float magnitude = total / (binEnd - binStart);

                    // Log attenuation
                    magnitude = saturate(magnitude * (log(1.1) / (log(1.1 + pow(_LogAttenuation, 4) * (1.0 - magnitude))))) / pow(threshold, 2);

                    // Contrast
                    magnitude = saturate(magnitude * tan(1.57 * _ContrastSlope) + magnitude + _ContrastOffset * tan(1.57 * _ContrastSlope) - tan(1.57 * _ContrastSlope));

                    // Fade
                    float lastMagnitude = AudioLinkGetSelfPixelData(ALPASS_AUDIOLINK + int2(0, band)).y;
                    lastMagnitude -= -1.0 * pow(_FadeLength-1.0, 3);                                                                            // Inverse cubic remap
                    lastMagnitude = saturate(lastMagnitude * (1.0 + (pow(lastMagnitude - 1.0, 4.0) * _FadeExpFalloff) - _FadeExpFalloff));     // Exp falloff

                    magnitude = max(lastMagnitude, magnitude);

                    return float4(magnitude, magnitude, magnitude, 1.);

                    // If part of the delay
                    } 
                    else 
                    {
                    // Slide pixels (coordinateLocal.x > 0)
                    float4 lastvalTiming = AudioLinkGetSelfPixelData(ALPASS_GENERALVU + int2(4, 1)); // Timing for 4-band, move at 90 Hz.
                    lastvalTiming.x += unity_DeltaTime.x * AUDIOLINK_4BAND_TARGET_RATE;
                    uint framesToRoll = floor( lastvalTiming.x );

                    if( framesToRoll == 0 )
                    {
                        return AudioLinkGetSelfPixelData(ALPASS_AUDIOLINK + int2(coordinateLocal.x, coordinateLocal.y));
                    }
                    else // 1 or more.
                    {
                        if( coordinateLocal.x > framesToRoll )
                        {
                            // For the rest of the line, move by the appropriate speed
                            return AudioLinkGetSelfPixelData(ALPASS_AUDIOLINK + int2(coordinateLocal.x - framesToRoll, coordinateLocal.y));
                        }
                        else
                        {
                            // For the first part, extrapolate the cells.
                            float last = AudioLinkGetSelfPixelData(ALPASS_AUDIOLINK + int2(0, coordinateLocal.y));
                            float next = AudioLinkGetSelfPixelData(ALPASS_AUDIOLINK + int2(1, coordinateLocal.y));
                            float lprev = (coordinateLocal.x - 1) / (float)framesToRoll;
                            return lerp( last, next, lprev );
                        }
                    }
                }
            }
            ENDCG
        }

        Pass
        {
            Name "Pass5-VU-Meter-And-Other-Info"
            CGPROGRAM
            // The structure of the output is:
            // RED CHANNEL: Peak Amplitude
            // GREEN CHANNEL: RMS Amplitude.
            // BLUE CHANNEL: RESERVED.

            float4 frag (v2f_customrendertexture IN) : SV_Target
            {
                AUDIO_LINK_ALPHA_START(ALPASS_GENERALVU)

                float2 total = 0;
                float2 peak = 0;

                // Only VU over 1024 24kSPS samples
                uint i;
                for( i = 0; i < 1024; i++ )
                {
                    float4 audioFrame = AudioLinkGetSelfPixelData(ALPASS_WAVEFORM + uint2(i % AUDIOLINK_WIDTH, i / AUDIOLINK_WIDTH));
                    float2 leftright = audioFrame.x + float2( audioFrame.a, -audioFrame.a );
                    total += leftright * leftright;
                    peak = max(peak, abs(leftright));
                }

                float2 RMS = sqrt(total / i);
                
                float4 markerValue = AudioLinkGetSelfPixelData(ALPASS_GENERALVU + int2(9, 0));
                float4 markerTimes = AudioLinkGetSelfPixelData(ALPASS_GENERALVU + int2(10, 0));
                float4 lastAutogain = AudioLinkGetSelfPixelData(ALPASS_GENERALVU + int2(11, 0));

                markerTimes.xyzw += unity_DeltaTime.xxxx;
                //markerTimes = (markerTimes>1.) ? float4(-1, -1, -1, -1) : markerTimes;
                float4 RMSPeak = float4( RMS.x, peak.x, RMS.y, peak.y );
                #if 0
                    if(markerValue.x < RMSPeak.x || markerTimes.x > 1. )
                    {
                        markerValue.x = RMSPeak.x;
                        markerTimes.x = 0;
                    }
                    if(markerValue.y < RMSPeak.y || markerTimes.y > 1. )
                    {
                        markerValue.y = RMSPeak.y;
                        markerTimes.y = 0;
                    }
                    if(markerValue.z < RMSPeak.z || markerTimes.z > 1. )
                    {
                        markerValue.z = RMSPeak.z;
                        markerTimes.z = 0;
                    }
                    if(markerValue.w < RMSPeak.w || markerTimes.w > 1.)
                    {
                        markerValue.w = RMSPeak.a;
                        markerTimes.w = 0;
                    }
                #endif
                bool4 peakout = (markerValue < RMSPeak || markerTimes > float4(1.,1.,1.,1.) );
                markerTimes = peakout?0:markerTimes;
                markerValue = peakout?RMSPeak:markerValue;

                if( coordinateLocal.y == 0 )
                {
                    if(coordinateLocal.x >= 8)
                    {
                        if(coordinateLocal.x == 8)
                        {
                            // First pixel: Current value.
                            return RMSPeak;
                        }
                        else if(coordinateLocal.x == 9)
                        {
                            // Second pixel: Limit Output
                            return markerValue;
                        }
                        else if(coordinateLocal.x == 10)
                        {
                            // Second pixel: Limit time
                            return markerTimes;
                        }
                        else if(coordinateLocal.x == 11)
                        {
                            // Third pixel: Auto Gain / Volume Monitor for ColorChord

                            // Compensate for the fact that we've already gain'd our samples.
                            float deratePeak = peak * (lastAutogain.x + _AutogainDerate);

                            if(deratePeak > lastAutogain.x)
                            {
                                lastAutogain.x = lerp(deratePeak, lastAutogain.x, .5); //Make attack quick
                            }
                            else
                            {
                                lastAutogain.x = lerp(deratePeak, lastAutogain.x, .995); //Make decay long.
                            }

                            lastAutogain.y = lerp(peak, lastAutogain.y, 0.95);
                            return lastAutogain;
                        }
                    }
                    else
                    {
                        if(coordinateLocal.x == 0)
                        {
                            // Pixel 0 = Version
                            return _VersionNumberAndFPSProperty;
                        }
                        else if(coordinateLocal.x == 1)
                        {
                            // Pixel 1 = Frame Count, if we did not repeat, this would stop counting after ~51 hours.
                            // So, instead we just reset it every 24 hours.  Note this is after 24 hours in an instance.
                            // Note: This is also used to measure FPS.

                            float4 lastVal = AudioLinkGetSelfPixelData(ALPASS_GENERALVU + int2(1, 0));
                            float frameCount = lastVal.x;
                            float frameCountFPS = lastVal.y;
                            float frameCountLastFPS = lastVal.z;
                            float lastTimeFPS = lastVal.a;
                            frameCount++;
                            if(frameCount >= 7776000) //~24 hours.
                            frameCount = 0;
                            frameCountFPS++;

                            // See if we've been reset.
                            if(lastTimeFPS > _Time.y)
                            {
                                lastTimeFPS = 0;
                            }

                            // After one second, take the running FPS and present it as the now FPS.
                            if(_Time.y > lastTimeFPS + 1)
                            {
                                frameCountLastFPS = frameCountFPS;
                                frameCountFPS = 0;
                                lastTimeFPS = _Time.y;
                            }
                            return float4(frameCount, frameCountFPS, frameCountLastFPS, lastTimeFPS);
                        }
                        else if(coordinateLocal.x == 2)
                        {
                            // Output of this is daytime, in milliseconds
                            // This is done a little awkwardly as to prevent any overflows.
                            uint dtms = _AdvancedTimeProps0.x * 1000;
                            uint dtms2 = _AdvancedTimeProps0.y * 1000 + (dtms >> 10);
                            // Specialized implementation, similar to AudioLinkEncodeUInt
                            return float4(
                            (float)(dtms & 0x3ff),
                            (float)((dtms2) & 0x3ff),
                            (float)((dtms2 >> 10) & 0x3ff),
                            (float)((dtms2 >> 20) & 0x3ff)
                            );
                        }
                        else if(coordinateLocal.x == 3)
                        {
                            // Current time of day, in local time.
                            // Generally this will not exceed 90 million milliseconds. (25 hours)
                            int ftpa = _AdvancedTimeProps0.z * 1000.;
                            // Specialized implementation, similar to AudioLinkEncodeUInt
                            return float4(ftpa & 0x3ff, (ftpa >> 10) & 0x3ff, (ftpa >> 20) & 0x3ff, 0 );
                        }
                        else if(coordinateLocal.x == 4)
                        {
                            // Time sync'd off of Networking.GetServerTimeInMilliseconds()
                            float fractional = _AdvancedTimeProps1.x;
                            float major = _AdvancedTimeProps1.y;
                            if( major < 0 )
                            major = 65536 + major;
                            uint currentNetworkTimeMS = ((uint)fractional) | (((uint)major)<<16);
                            return AudioLinkEncodeUInt(currentNetworkTimeMS);
                        }
                        else if(coordinateLocal.x == 5)
                        {
                            //Provide Media Volume, Time, and State set by Udon
                            return float4(_MediaVolume, _MediaTime, _MediaPlaying, _MediaLoop);
                        }
                        else if(coordinateLocal.x == 6)
                        {
                            //.x = Player Count
                            //.y = IsMaster
                            //.z = IsInstanceOwner
                            return float4( _PlayerCountAndData );
                        }
                        else if(coordinateLocal.x == 7)
                        {
                            //General Debug Register
                            //Use this for whatever.
                            return float4( _AdvancedTimeProps0.w, unity_DeltaTime.x, markerTimes.y, 1 );
                        }
                    }
                }
                else
                {
                    //Second Row y = 1
                    if( coordinateLocal.x < 4 )
                    {
                        if( _ThemeColorMode == 1 )
                        {
                            if( coordinateLocal.x == 0 ) return _CustomThemeColor0;
                            if( coordinateLocal.x == 1 ) return _CustomThemeColor1;
                            if( coordinateLocal.x == 2 ) return _CustomThemeColor2;
                            if( coordinateLocal.x == 3 ) return _CustomThemeColor3;
                        }
                        else
                        {
                            return AudioLinkGetSelfPixelData(ALPASS_CCCOLORS+uint2(coordinateLocal.x,0));
                        }
                    }
                    else if( coordinateLocal.x == 4 )
                    {
                        // Computation for history timing.
                        float4 lastval = AudioLinkGetSelfPixelData(ALPASS_GENERALVU + int2(4, 1)); // Timing for 4-band, move at 90 Hz.
                        lastval.x += unity_DeltaTime.x * AUDIOLINK_4BAND_TARGET_RATE;
                        // This looks like a frac() but I want to make sure the math gets done the same here
                        // to prevent any possible mismatch between here and the use of finding the int.
                        int framesToRoll = floor( lastval.x );
                        lastval.x -= framesToRoll;
                        return lastval;
                    }
                    else if( coordinateLocal.x == 5 )
                    {
                        // UTC Day number
                        return AudioLinkEncodeUInt(_AdvancedTimeProps1.z);
                    }
                    else if( coordinateLocal.x == 6 )
                    {
                        return AudioLinkEncodeUInt(_AdvancedTimeProps1.w * 1000);
                    }
                    else if( coordinateLocal.x == 7)
                    {
                        // Audio Source Position
                        return _SourcePosition;
                    }
                }

                // Reserved
                return 0;
            }
            ENDCG
        }

        Pass
        {
            Name "Pass6ColorChord-Notes"
            CGPROGRAM

            float NoteWrap(float note1, float note2)
            {
                float diff = note2 - note1;
                diff = glsl_mod(diff, AUDIOLINK_EXPBINS);
                if(diff > AUDIOLINK_EXPBINS / 2)
                return diff - AUDIOLINK_EXPBINS;
                else
                return diff;
            }

            float4 frag (v2f_customrendertexture IN) : SV_Target
            {
                AUDIO_LINK_ALPHA_START(ALPASS_CCINTERNAL)

                float vuAmplitude = AudioLinkGetSelfPixelData(ALPASS_GENERALVU + int2(8, 0)).y * _Gain;
                float noteMinimum = 0.00 + 0.1 * vuAmplitude;

                //Note structure:
                // .x = Note frequency (0...AUDIOLINK_ETOTALBINS, but floating point)
                // .y = The incoming intensity.
                // .z = Lagged intensity.         ---> This is what decides if a note is going to disappear.
                // .w = Quicker lagged intensity.

                //NoteB Structure
                // .x = Note Number  ::: NOTE if .y < 0 this is the index of where this note _went_ or what note it was joined to.
                // .y = Time this note has existed.
                // .z = Sorted-by-frequency position. (With note 0 being the 0th note)

                //Summary:
                // .x = Total number of notes.
                // .y .z .w = sum of note's yzw.

                //SummaryB:
                // .x = Latest note number.
                // .y = AUDIOLINK_ROOTNOTE
                // .z = number of populated notes.

                float4 notes[COLORCHORD_MAX_NOTES];
                float4 notesB[COLORCHORD_MAX_NOTES];

                uint i;
                for(i = 0; i < COLORCHORD_MAX_NOTES; i++)
                {
                    notes[i] = AudioLinkGetSelfPixelData(ALPASS_CCINTERNAL + uint2(i + 1, 0)) * float4(1, 0, 1, 1);
                    notesB[i] = AudioLinkGetSelfPixelData(ALPASS_CCINTERNAL + uint2(i + 1, 1));
                }

                float4 noteSummary = AudioLinkGetSelfPixelData(ALPASS_CCINTERNAL);
                float4 noteSummaryB = AudioLinkGetSelfPixelData(ALPASS_CCINTERNAL + int2(0, 1));
                float lastAmplitude = AudioLinkGetSelfPixelData(ALPASS_DFT + uint2(AUDIOLINK_EXPBINS, 0)).z;
                float thisAmplitude = AudioLinkGetSelfPixelData(ALPASS_DFT + uint2(1 + AUDIOLINK_EXPBINS, 0)).z;

                for(i = AUDIOLINK_EXPBINS + 2; i < COLORCHORD_EMAXBIN; i++)
                {
                    float nextAmplitude = AudioLinkGetSelfPixelData(ALPASS_DFT + uint2(i % AUDIOLINK_WIDTH, i / AUDIOLINK_WIDTH)).z;
                    if(thisAmplitude > lastAmplitude && thisAmplitude > nextAmplitude && thisAmplitude > noteMinimum)
                    {
                        // Find actual peak by looking ahead and behind.
                        float diffA = thisAmplitude - nextAmplitude;
                        float diffB = thisAmplitude - lastAmplitude;
                        float noteFreq = glsl_mod(i - 1, AUDIOLINK_EXPBINS);
                        if(diffA < diffB)
                        {
                            // Behind
                            noteFreq -= 1. - diffA / diffB; //Ratio must be between 0 .. 0.5
                        }
                        else
                        {
                            // Ahead
                            noteFreq += 1. - diffB / diffA;
                        }

                        uint j;
                        int closestNote = -1;
                        int freeNote = -1;
                        float closestNoteDistance = COLORCHORD_NOTE_CLOSEST;

                        // Search notes to see what the closest note to this peak is.
                        // also look for any empty notes.
                        for(j = 0; j < COLORCHORD_MAX_NOTES; j++)
                        {
                            float dist = abs(NoteWrap(notes[j].x, noteFreq));
                            if(notes[j].z <= 0)
                            {
                                if(freeNote == -1)
                                freeNote = j;
                            }
                            else if(dist < closestNoteDistance)
                            {
                                closestNoteDistance = dist;
                                closestNote = j;
                            }
                        }

                        float thisIntensity = thisAmplitude * COLORCHORD_NEW_NOTE_GAIN;

                        if(closestNote != -1)
                        {
                            // Note to combine peak to has been found, roll note in.
                            float4 n = notes[closestNote];
                            float drag = NoteWrap(n.x, noteFreq) * 0.05;

                            float mn = max(n.y, thisAmplitude * COLORCHORD_NEW_NOTE_GAIN)
                            // Technically the above is incorrect without the below, additional notes found should contribute.
                            // But I'm finding it looks better w/o it.  Well, the 0.3 is arbitrary.  But, it isn't right to
                            // only take max.
                            + thisAmplitude * COLORCHORD_NEW_NOTE_GAIN * 0.3;

                            notes[closestNote] = float4(n.x + drag, mn, n.z, n.w);
                        }
                        else if(freeNote != -1)
                        {

                            int jc = 0;
                            int ji = 0;
                            // uuuggghhhh Ok, so this's is probably just me being paranoid
                            // but I really wanted to make sure all note IDs are unique
                            // in case another tool would care about the uniqueness.
                            [loop]
                            for(ji = 0; ji < COLORCHORD_MAX_NOTES && jc != COLORCHORD_MAX_NOTES; ji++)
                            {
                                noteSummaryB.x = noteSummaryB.x + 1;
                                if(noteSummaryB.x > 1023) noteSummaryB.x = 0;
                                [loop]
                                for(jc = 0; jc < COLORCHORD_MAX_NOTES; jc++)
                                {
                                    if(notesB[jc].x == noteSummaryB.x)
                                    break;
                                }
                            }

                            // Couldn't find note.  Create a new note.
                            notes[freeNote]  = float4(noteFreq, thisIntensity, thisIntensity, thisIntensity);
                            notesB[freeNote] = float4(noteSummaryB.x, unity_DeltaTime.x, 0, 0);
                        }
                        else
                        {
                            // Whelp, the note fell off the wagon.  Oh well!
                        }
                    }
                    lastAmplitude = thisAmplitude;
                    thisAmplitude = nextAmplitude;
                }

                float4 newNoteSummary = 0.;
                float4 newNoteSummaryB = noteSummaryB;
                newNoteSummaryB.y = AUDIOLINK_ROOTNOTE;

                [loop]
                for(i = 0; i < COLORCHORD_MAX_NOTES; i++)
                {
                    uint j;
                    float4 n1 = notes[i];
                    float4 n1B = notesB[i];

                    [loop]
                    for(j = 0; j < COLORCHORD_MAX_NOTES; j++)
                    {
                        // 🤮 Shader compiler can't do triangular loops.
                        // We don't want to iterate over a cube just compare ith and jth note once.

                        float4 n2 = notes[j];

                        if(n2.z > 0 && j > i && n1.z > 0)
                        {
                            // Potentially combine notes
                            float dist = abs(NoteWrap(n1.x, n2.x));
                            if(dist < COLORCHORD_NOTE_CLOSEST)
                            {
                                //Found combination of notes.  Nil out second.
                                float drag = NoteWrap(n1.x, n2.x) * 0.5;//n1.z/(n2.z+n1.y);
                                n1 = float4(n1.x + drag, n1.y + thisAmplitude, n1.z, n1.w);

                                //n1B unchanged.

                                notes[j] = 0;
                                notesB[j] = float4(i, -1, 0, 0);
                            }
                        }
                    }

                    #if 0
                        //Old values, framerate-invariant, assumed 60 FPS medium.
                        #define COLORCHORD_IIR_DECAY_1          0.90
                        #define COLORCHORD_IIR_DECAY_2          0.85
                        #define COLORCHORD_CONSTANT_DECAY_1     0.01
                        #define COLORCHORD_CONSTANT_DECAY_2     0.0
                    #else
                        // Calculated from above values using: 0.9 = pow( x, .016666 ), or new_component = x ^ 60
                        float COLORCHORD_IIR_DECAY_1 = pow( 0.0018, unity_DeltaTime.x );
                        float COLORCHORD_IIR_DECAY_2 = pow( 5.822e-5, unity_DeltaTime.x );
                        float COLORCHORD_CONSTANT_DECAY_1 = (0.01*60)*unity_DeltaTime.x;
                        float COLORCHORD_CONSTANT_DECAY_2 = (0.0*60)*unity_DeltaTime.x;
                    #endif
                    // Filter n1.z from n1.y.
                    if(n1.z >= 0)
                    {
                        // Make sure we're wrapped correctly.
                        n1.x = glsl_mod(n1.x, AUDIOLINK_EXPBINS);

                        // Apply filtering
                        n1.z = lerp(n1.y, n1.z, COLORCHORD_IIR_DECAY_1) - COLORCHORD_CONSTANT_DECAY_1; //Make decay slow.
                        n1.w = lerp(n1.y, n1.w, COLORCHORD_IIR_DECAY_2) - COLORCHORD_CONSTANT_DECAY_2; //Make decay slow.

                        n1B.y += unity_DeltaTime.x;


                        if(n1.z < noteMinimum)
                        {
                            n1 = -1;
                            n1B = 0;
                        }
                        //XXX TODO: Do uniformity calculation on n1 for n1.w.
                    }

                    if(n1.z >= 0)
                    {
                        // Compute Y to create a "unified" value.  This is good for understanding
                        // the ratio of how "important" this note is.
                        n1.y = pow(max(n1.z - noteMinimum*10, 0), 1.5);

                        newNoteSummary += float4(1., n1.y, n1.z, n1.w);
                    }

                    notes[i] = n1;
                    notesB[i] = n1B;
                }

                // Sort by frequency and count notes.
                // These loops are phrased funny because the unity shader compiler gets really
                // confused easily.
                float sortedNoteSlotValue = -1000;
                newNoteSummaryB.z = 0;

                [loop]
                for(i = 0; i < COLORCHORD_MAX_NOTES; i++)
                {
                    //Count notes
                    newNoteSummaryB.z += (notes[i].z > 0) ? 1 : 0;

                    float closestToSlotWithoutGoingOver = 100;
                    int sortID = -1;
                    int j;
                    for(j = 0; j < COLORCHORD_MAX_NOTES; j++)
                    {
                        float4 n2 = notes[j];
                        float noteFreqB = glsl_mod(-notes[0].x + 0.5 + n2.x , AUDIOLINK_EXPBINS);
                        if(n2.z > 0 && noteFreqB > sortedNoteSlotValue && noteFreqB < closestToSlotWithoutGoingOver)
                        {
                            closestToSlotWithoutGoingOver = noteFreqB;
                            sortID = j;
                        }
                    }
                    sortedNoteSlotValue = closestToSlotWithoutGoingOver;
                    notesB[i] = notesB[i] * float4(1, 1, 0, 1) + float4(0, 0, sortID, 0);
                }
                // PIXEL LAYOUT:
                //  Summary / Data[COLORCHORD_MAX_NOTES] / 0 / 0 / Colors[COLORCHORD_MAX_NOTES] / 0
                // We now have a condensed list of all notes that are playing.
                if( coordinateLocal.x < COLORCHORD_MAX_NOTES+1 )
                {
                    if( coordinateLocal.x == 0 )
                    {
                        // Summary note.
                        return (coordinateLocal.y) ? newNoteSummaryB : newNoteSummary;
                    }
                    else
                    {
                        // Actual Note Data
                        return (coordinateLocal.y) ? notesB[coordinateLocal.x - 1] : notes[coordinateLocal.x - 1];
                    }
                }
                else if( coordinateLocal.x >= COLORCHORD_MAX_NOTES+3 && coordinateLocal.x < COLORCHORD_MAX_NOTES*2+4 && coordinateLocal.y == 0 )
                {
                    uint id = coordinateLocal.x - (COLORCHORD_MAX_NOTES+2);
                    float4 ThisNote = notes[id];
                    static const float AudioLinkColorOutputIntensity = 0.4;
                    return float4( AudioLinkCCtoRGB( glsl_mod(ThisNote.x,AUDIOLINK_EXPBINS), ThisNote.z * AudioLinkColorOutputIntensity, AUDIOLINK_ROOTNOTE), 1.0 );
                }
                return 0;
            }
            ENDCG
        }

        Pass
        {
            Name "Pass7-AutoCorrelator"
            CGPROGRAM

            #define AUTOCORRELATOR_EMAXBIN 120
            #define AUTOCORRELATOR_EBASEBIN 0

            float4 frag (v2f_customrendertexture IN) : SV_Target
            {
                AUDIO_LINK_ALPHA_START(ALPASS_AUTOCORRELATOR)

                float wavePosition = (float)coordinateLocal.x;
                float2 fvTotal = 0;
                float fvr = 15.;

                // This computes both the regular autocorrelator in the R channel
                // as well as a uncorrelated autocorrelator in the G channel
                uint i;
                for(i = AUTOCORRELATOR_EBASEBIN; i < AUTOCORRELATOR_EMAXBIN; i++)
                {
                    float bin = AudioLinkGetSelfPixelData(ALPASS_DFT + uint2(i % AUDIOLINK_WIDTH, i / AUDIOLINK_WIDTH)).z;
                    float frequency = pow(2, i / 24.) * AUDIOLINK_BOTTOM_FREQUENCY / AUDIOLINK_SPS * UNITY_TWO_PI;
                    float2 csv = float2(cos(frequency * wavePosition * fvr),  cos(frequency * wavePosition * fvr + i * 0.32));
                    csv.y *= step(i % 4, 1) * 4.;
                    fvTotal += csv * (bin * bin);
                }

                // Red:   Regular autocorrelator
                // Green: Uncorrelated autocorrelator
                // Blue:  Reserved
                return float4(fvTotal, 0, 1);
            }
            ENDCG
        }

        Pass
        {
            Name "Pass8-ColorChord-Linear"
            CGPROGRAM

            float4 frag (v2f_customrendertexture IN) : SV_Target
            {
                AUDIO_LINK_ALPHA_START(ALPASS_CCSTRIP)

                int p;

                const float Brightness = .3;
                const float RootNote = 0;

                float4 NotesSummary = AudioLinkGetSelfPixelData(ALPASS_CCINTERNAL);

                float TotalPower = 0.0;
                TotalPower = NotesSummary.y;

                float PowerPlace = 0.0;
                for(p = 0; p < COLORCHORD_MAX_NOTES; p++)
                {
                    float4 NotesB = AudioLinkGetSelfPixelData(ALPASS_CCINTERNAL + int2(1 + p, 1));
                    float4 Peak = AudioLinkGetSelfPixelData(ALPASS_CCINTERNAL + int2(1 + NotesB.z, 0));
                    if(Peak.y <= 0) continue;

                    float Power = Peak.y/TotalPower;
                    PowerPlace += Power;
                    if(PowerPlace >= IN.globalTexcoord.x)
                    {
                        return float4(AudioLinkCCtoRGB(Peak.x, Peak.w*Brightness, AUDIOLINK_ROOTNOTE), 1.0);
                    }
                }

                return float4(0., 0., 0., 1.);
            }
            ENDCG
        }

        Pass
        {
            Name "Pass9-ColorChord-Lights"
            CGPROGRAM

            static const float _PickNewSpeed = 1.0;

            float tinyrand(float3 uvw)
            {
                return frac(cos(dot(uvw, float3(137.945, 942.32, 593.46))) * 442.5662);
            }

            float SetNewCellValue(float a)
            {
                return a*.5;
            }

            float4 frag(v2f_customrendertexture IN) : SV_Target
            {
                AUDIO_LINK_ALPHA_START(ALPASS_CCLIGHTS)

                float4 NotesSummary = AudioLinkGetSelfPixelData(ALPASS_CCINTERNAL);

                #define NOTESUFFIX(n) n.y       //was pow(n.z, 1.5)

                float4 ComputeCell = AudioLinkGetSelfPixelData(ALPASS_CCLIGHTS + int2(coordinateLocal.x, 1));
                //ComputeCell
                //    .x = Mated Cell # (Or -1 for black)
                //    .y = Minimum Brightness Before Jump
                //    .z = ???

                float4 ThisNote = AudioLinkGetSelfPixelData(ALPASS_CCINTERNAL + int2(ComputeCell.x + 1, 0));
                //  Each element:
                //   R: Peak Location (Note #)
                //   G: Peak Intensity
                //   B: Calm Intensity
                //   A: Other Intensity

                ComputeCell.y -= _PickNewSpeed * 0.01;

                if(NOTESUFFIX(ThisNote) < ComputeCell.y || ComputeCell.y <= 0 || ThisNote.z < 0)
                {
                    //Need to select new cell.
                    float min_to_acquire = tinyrand(float3(coordinateLocal.xy, _Time.x));

                    int n;
                    float4 SelectedNote = 0.;
                    int SelectedNoteNo = -1;

                    float cumulative = 0.0;
                    for(n = 0; n < COLORCHORD_MAX_NOTES; n++)
                    {
                        float4 Note = AudioLinkGetSelfPixelData(ALPASS_CCINTERNAL + int2(n + 1, 0));
                        float unic = NOTESUFFIX(Note);
                        if(unic > 0)
                        cumulative += unic;
                    }

                    float sofar = 0.0;
                    for(n = 0; n < COLORCHORD_MAX_NOTES; n++)
                    {
                        float4 Note = AudioLinkGetSelfPixelData(ALPASS_CCINTERNAL + int2(n + 1, 0));
                        float unic = NOTESUFFIX(Note);
                        if( unic > 0 )
                        {
                            sofar += unic;
                            if(sofar/cumulative > min_to_acquire)
                            {
                                SelectedNote = Note;
                                SelectedNoteNo = n;
                                break;
                            }
                        }
                    }

                    if(SelectedNote.z > 0.0)
                    {
                        ComputeCell.x = SelectedNoteNo;
                        ComputeCell.y = SetNewCellValue(NOTESUFFIX(SelectedNote));
                    }
                    else
                    {
                        ComputeCell.x = 0;
                        ComputeCell.y = 0;
                    }
                }

                ThisNote = AudioLinkGetSelfPixelData(ALPASS_CCINTERNAL + int2(ComputeCell.x + 1, 0));

                if(coordinateLocal.y < 0.5)
                {
                    // the light color output
                    if(ComputeCell.y <= 0)
                    {
                        return 0.;
                    }

                    //XXX TODO: REVISIT THIS!! Ths is an arbitrary value!
                    float intensity = ThisNote.w/3;
                    return float4(AudioLinkCCtoRGB(glsl_mod(ThisNote.x,AUDIOLINK_EXPBINS),intensity, AUDIOLINK_ROOTNOTE), 1.0);
                }
                else
                {
                    // the compute output
                    return ComputeCell;
                }
            }
            ENDCG
        }

        Pass
        {
            Name "Filtered-AudioLinkOutput"
            CGPROGRAM
            float4 frag (v2f_customrendertexture IN) : SV_Target
            {
                AUDIO_LINK_ALPHA_START(ALPASS_FILTEREDAUDIOLINK)
                float4 AudioLinkBase = AudioLinkGetSelfPixelData(ALPASS_AUDIOLINK + uint2(0, coordinateLocal.y));
                if( coordinateLocal.x < 16 )
                {
                    // For pixels 0..15, filtered output.
                    float4 Previous = AudioLinkGetSelfPixelData(ALPASS_FILTEREDAUDIOLINK + int2(coordinateLocal.x, coordinateLocal.y));
                    return lerp( AudioLinkBase, Previous, pow( pow(.55, unity_DeltaTime.x ), coordinateLocal.x+1 ) ); //IIR-Filter
                }
                else if( coordinateLocal.x >= 16 &&  coordinateLocal.x < 24 )
                {
                    // This section is for ALPASS_CHRONOTENSITY
                    uint4 rpx = AudioLinkGetSelfPixelData(coordinateGlobal.xy);

                    float ComparingValue = (coordinateLocal.x & 1) ? 
                    AudioLinkGetSelfPixelData(ALPASS_FILTEREDAUDIOLINK + uint2(4, coordinateLocal.y)) :
                    AudioLinkBase;

                    //Get a heavily filtered value to compare against.
                    float FilteredAudioLinkValue = AudioLinkGetSelfPixelData(ALPASS_FILTEREDAUDIOLINK + uint2( 0, coordinateLocal.y ) );
                    
                    float DifferentialValue = ComparingValue - FilteredAudioLinkValue;

                    float ValueDiff;

                    int mode = ( coordinateLocal.x - 16 ) / 2;

                    // Chronotensity is organized in a (4x2)x4 grid of accumulated values.
                    // Y is which band we are using.  X is as follows:
                    //
                    // x = 0, 1: Accumulates as a function of intensity of band.
                    //           The louder the band, the quicker the function increments.
                    // x = 0: Difference between base and heavily filtered.
                    // x = 1: Difference between slightly filtered and heavily filtered.
                    //
                    // x = 2, 3: Goes positive when band is higher, negative when lower.
                    // x = 2: Difference between base and heavily filtered.
                    // x = 3: Difference between slightly filtered and heavily filtered.
                    //
                    // x = 4, 5: Increments when respective filtered value is 0 or negative.
                    // x = 4: Difference between base and heavily filtered.
                    // x = 5: Difference between slightly filtered and heavily filtered.
                    //
                    // x = 6: Unfiltered, increments when band is above 0.05 threshold.
                    // x = 7: Unfiltered, increments when band is below 0.05 threshold.

                    if( mode == 0 )
                    {
                        ValueDiff = max( DifferentialValue, 0 );
                    }
                    else if( mode == 1 )
                    {
                        ValueDiff = DifferentialValue;
                    }
                    else if( mode == 2 )
                    {
                        ValueDiff = max( -DifferentialValue, 0 );
                    }
                    else
                    {
                        if( coordinateLocal.x & 1 )
                        ValueDiff = max((-(AudioLinkGetSelfPixelData(ALPASS_AUDIOLINK + uint2( 0, coordinateLocal.y ) ) - 0.05 )), 0 )*2;
                        else
                        ValueDiff = max(((AudioLinkGetSelfPixelData(ALPASS_AUDIOLINK + uint2( 0, coordinateLocal.y ) ) - 0.05 )), 0 )*.5;
                    }
                    
                    uint Value = rpx.x + rpx.y * 1024 + rpx.z * 1048576 + rpx.w * 1073741824;
                    Value += ValueDiff * unity_DeltaTime.x * 1048576;

                    return AudioLinkEncodeUInt(Value);
                }
                else
                {
                    // Other features.
                    return 0;
                }
            }
            ENDCG
        }

        Pass
        {
            Name "Pass11-Filtered-VU"
            CGPROGRAM
            float4 frag (v2f_customrendertexture IN) : SV_Target
            {
                AUDIO_LINK_ALPHA_START(ALPASS_FILTEREDVU)
                if( coordinateLocal.x < 4 )
                {
                    float4 prev = AudioLinkGetSelfPixelData(ALPASS_FILTEREDVU + coordinateLocal.xy);
                    float4 RMSPeak = AudioLinkGetSelfPixelData(ALPASS_GENERALVU + uint2(8, 0));
                    float4 lastFilteredRMSPeak = AudioLinkGetSelfPixelData(ALPASS_FILTEREDVU + uint2(coordinateLocal.x, 0));
                    float4 filteredRMSPeak = lerp(RMSPeak, lastFilteredRMSPeak, pow(pow(.046,unity_DeltaTime), coordinateLocal.x+1)).x;

                    float4 markerValue = AudioLinkGetSelfPixelData(ALPASS_FILTEREDVU + uint2(coordinateLocal.x, 2));
                    float4 timerValue = AudioLinkGetSelfPixelData(ALPASS_FILTEREDVU + uint2(coordinateLocal.x, 3));
                    bool4 peak = filteredRMSPeak > markerValue || timerValue > 0.5;
                    // Filtered VU intensity
                    if(coordinateLocal.y == 0)
                    {
                        return filteredRMSPeak;
                    }
                    // Filtered VU marker
                    else if (coordinateLocal.y == 1)
                    {
                        // For linear fallof (we use exp now)
                        /*float4 res =
                        abs(prev - markerValue) <= 0.01
                        ? markerValue
                        : prev < markerValue
                        ? prev + 0.01 
                        : prev - 0.01;*/

                        float4 speed = lerp(0.1, 0.05, abs(prev - markerValue));
                        float4 res = lerp(prev, markerValue, speed);
                        return max(filteredRMSPeak, res);
                    }
                    // VU markers values
                    else if (coordinateLocal.y == 2)
                    {
                        return peak ? filteredRMSPeak : markerValue;
                    }
                    // VU marker timers
                    else if (coordinateLocal.y == 3)
                    {
                        return peak ? 0 : prev + unity_DeltaTime.xxxx;
                    }
                }
                else if (coordinateLocal.x == 4)
                {
                    // PEMA
                }
                else
                {
                    // BEAT DETECTION STILL IN EARLY DEVELOPMENT - DO NOT USE
                    float4 prev = AudioLinkGetSelfPixelData(coordinateGlobal.xy);
                    if( coordinateLocal.x == 5 )
                    {
                        float nowv = AudioLinkGetSelfPixelData(ALPASS_AUDIOLINK + int2(0, coordinateLocal.y));
                        float beatdist = 0;
                        if( prev.x > prev.y && prev.x > nowv )
                        {
                            beatdist = prev.z;
                            prev.z = 0;
                        }
                        return float4( nowv, prev.x, prev.z+1, beatdist );
                    }
                    else if( coordinateLocal.x == 6 )
                    {
                        uint y = coordinateLocal.y;
                        // for y = 0..3, each in decreasing levels of forced confidence
                        // used to enact a change on the one above.

                        for( uint ib = 0; ib < 4; ib++ )
                        {
                            int beat = AudioLinkGetSelfPixelData(ALPASS_FILTEREDVU + uint2( 4, ib ) ).x;
                            // Anywhere beat is nonzero is a data point.
                        }
                    }
                    else
                    {
                        float4 this_bd_data = AudioLinkGetSelfPixelData(ALPASS_FILTEREDVU + uint2(4, coordinateLocal.y));
                        //Assume beats in the range of 80..160 BPM only.
                    }
                }
                return 1;
            }
            ENDCG
        }

        Pass
        {
            Name "Pass12-Global-Strings"
            CGPROGRAM
            float4 frag (v2f_customrendertexture IN) : SV_Target
            {
                AUDIO_LINK_ALPHA_START(ALPASS_GLOBAL_STRINGS)
                float4 char4 = 0;
                if (coordinateLocal.y == 0) {
                    char4 = _StringLocalPlayer[coordinateLocal.x];
                }
                else if (coordinateLocal.y == 1) {
                    char4 = _StringMasterPlayer[coordinateLocal.x];
                }
                else if (coordinateLocal.y == 2) {
                    char4 = _StringCustom1[coordinateLocal.x];
                } 
                else {
                    char4 = _StringCustom2[coordinateLocal.x];
                }
                return char4;
            }
            ENDCG
        }

        Pass
        {
            Name "No-op"
            ColorMask 0
            ZWrite Off
        }
    }
}
