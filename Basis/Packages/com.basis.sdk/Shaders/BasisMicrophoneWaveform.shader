Shader "Basis/UI/MicrophoneWaveform"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Data ("Data", 2D) = "black" {}
    }

    SubShader
    {
        Tags
        {
            "RenderType"="Opaque"
            "PreviewType"="Plane"
        }

        Cull Off
        Lighting Off
        Fog { Mode Off }
        ZWrite Off
        ZTest Always

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0

            #include "UnityCG.cginc"

            #define SCOPE_COLUMNS 256.0
            #define SCOPE_ROWS 3.0

            sampler2D _Data;

            float4 _Background;
            float4 _Guide;
            float4 _Sent;
            float4 _Denoiser;
            float4 _Gate;
            float4 _Agc;
            float4 _Limiter;
            float4 _Focus;
            float4 _Boost;
            float4 _Pixel;

            float _Oldest;
            float _Pad;
            float _GainSpan;
            float _GateMode;
            float _GateLevel;
            float _AgcOn;
            float _AgcTarget;
            float _LimitLevel;

            struct Column
            {
                float input;
                float denoised;
                float body;
                float peak;
                float preLimit;
                float gateLine;
                float gateShown;
                float gateCut;
                float denoiser;
                float sent;
            };

            float4 Fetch(float column, float row)
            {
                float ring = column + _Oldest;
                ring = ring >= SCOPE_COLUMNS ? ring - SCOPE_COLUMNS : ring;
                return tex2Dlod(_Data, float4((ring + 0.5) / SCOPE_COLUMNS, (row + 0.5) / SCOPE_ROWS, 0.0, 0.0));
            }

            float Bit(float flags, float bit)
            {
                return fmod(floor(floor(flags + 0.5) / bit), 2.0);
            }

            float Shift(float level, float gain)
            {
                return level > 0.0005 ? saturate(level + gain) : 0.0;
            }

            Column Read(float index)
            {
                float4 levels = Fetch(index, 0.0);
                float4 stages = Fetch(index, 1.0);
                float4 meta = Fetch(index, 2.0);
                float gain = meta.x * _GainSpan;
                float automatic = step(1.5, _GateMode);
                float gated = Bit(meta.y, 8.0) * meta.w;

                Column c;
                c.input = Shift(levels.y, gain);
                c.denoised = Shift(stages.x, gain);
                c.body = levels.w;
                c.peak = levels.z;
                c.preLimit = stages.y;
                c.gateLine = lerp(Shift(_GateLevel, gain), Shift(stages.z, gain), automatic);
                c.gateShown = lerp(step(0.5, _GateMode) * meta.w, gated, automatic);
                c.gateCut = gated * saturate((1.0 - meta.z) * 20.0);
                c.denoiser = Bit(meta.y, 4.0) * meta.w;
                c.sent = Bit(meta.y, 1.0);
                return c;
            }

            Column Blend(Column a, Column b, float t)
            {
                Column c;
                c.input = lerp(a.input, b.input, t);
                c.denoised = lerp(a.denoised, b.denoised, t);
                c.body = lerp(a.body, b.body, t);
                c.peak = lerp(a.peak, b.peak, t);
                c.preLimit = lerp(a.preLimit, b.preLimit, t);
                c.gateLine = lerp(a.gateLine, b.gateLine, t);
                c.gateShown = lerp(a.gateShown, b.gateShown, t);
                c.gateCut = lerp(a.gateCut, b.gateCut, t);
                c.denoiser = lerp(a.denoiser, b.denoiser, t);
                c.sent = lerp(a.sent, b.sent, t);
                return c;
            }

            float Fill(float level, float v, float pixel)
            {
                return saturate((level - v) / pixel + 0.5) * step(0.0005, level);
            }

            float Band(float low, float high, float v, float pixel)
            {
                return Fill(high, v, pixel) * saturate((v - low) / pixel + 0.5) * step(low + 0.002, high);
            }

            float Stroke(float level, float v, float pixel, float halfWidth, float slope)
            {
                float distance = abs(v - level) / pixel / sqrt(1.0 + slope * slope);
                return saturate(halfWidth + 0.5 - distance);
            }

            float Dash(float x, float period, float duty)
            {
                return step(frac(x / period), duty);
            }

            fixed4 frag(v2f_img i) : SV_Target
            {
                float span = max(1.0 - _Pad, 0.0001);
                float v = abs(i.uv.y - 0.5) * 2.0 / span;
                float pixel = 2.0 * _Pixel.y / span;
                float x = i.uv.x * _Pixel.z;
                float y = i.uv.y * _Pixel.w;
                float columnPixels = max(_Pixel.z / SCOPE_COLUMNS, 0.0001);

                float position = i.uv.x * SCOPE_COLUMNS - 0.5;
                float first = clamp(floor(position), 0.0, SCOPE_COLUMNS - 1.0);
                float second = min(first + 1.0, SCOPE_COLUMNS - 1.0);
                float blend = saturate(position - first);

                Column a = Read(first);
                Column b = Read(second);
                Column c = Blend(a, b, blend);

                float stripes = Dash(x + y, 8.0, 0.5);
                float3 colour = _Background.rgb;
                colour = lerp(colour, _Guide.rgb, saturate(1.0 - abs(i.uv.y - 0.5) * _Pixel.w) * 0.25);

                float gateSlope = (b.gateLine - a.gateLine) / pixel / columnPixels;
                colour = lerp(colour, _Gate.rgb, Stroke(c.gateLine, v, pixel, 1.0 + 0.6 * _Boost.y, gateSlope) * c.gateShown * _Focus.y);

                float body = Fill(c.body, v, pixel);
                float halo = Band(c.body, c.peak, v, pixel);
                float held = lerp(0.18, 0.6, stripes);
                colour = lerp(colour, _Sent.rgb, body * 0.9 * c.sent);
                colour = lerp(colour, _Sent.rgb, halo * 0.35 * c.sent);
                colour = lerp(colour, _Guide.rgb, body * held * (1.0 - c.sent));
                colour = lerp(colour, _Guide.rgb, halo * held * 0.45 * (1.0 - c.sent));

                colour = lerp(colour, _Limiter.rgb, Band(c.peak, c.preLimit, v, pixel) * 0.95 * _Focus.w);
                colour = lerp(colour, _Gate.rgb, Band(c.body, c.denoised, v, pixel) * c.gateCut * lerp(0.12, 0.62, stripes) * _Focus.y);
                colour = lerp(colour, _Denoiser.rgb, Band(c.denoised, c.input, v, pixel) * c.denoiser * lerp(0.1, 0.5, stripes) * _Focus.x);

                colour = lerp(colour, _Agc.rgb, Stroke(_AgcTarget, v, pixel, 0.6 + 0.6 * _Boost.z, 0.0) * _AgcOn * 0.85 * _Focus.z);
                colour = lerp(colour, _Limiter.rgb, Stroke(_LimitLevel, v, pixel, 0.6 + 0.6 * _Boost.w, 0.0) * _Focus.w);

                return fixed4(colour, 1.0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
