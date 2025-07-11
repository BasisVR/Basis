Shader "AudioLink/Internal/AudioLinkUI"
{
    Properties
    {
        [ToggleUI] _Power("On/Off", Float) = 1.0

        _Gain("Gain", Range(0, 2)) = 1.0
        [ToggleUI] _AutoGain("Autogain", Float) = 0.0

        _Threshold0("Low Threshold", Range(0, 1)) = 0.5
        _Threshold1("Low Mid Threshold", Range(0, 1)) = 0.5
        _Threshold2("High Mid Threshold", Range(0, 1)) = 0.5
        _Threshold3("High Threshold", Range(0, 1)) = 0.5

        _X0("Crossover X0", Range(0.0, 0.168)) = 0.0
        _X1("Crossover X1", Range(0.242, 0.387)) = 0.25
        _X2("Crossover X2", Range(0.461, 0.628)) = 0.5
        _X3("Crossover X3", Range(0.704, 0.953)) = 0.75

        _HitFade("Hit Fade", Range(0, 1)) = 0.5
        _ExpFalloff("Exp Falloff", Range(0, 1)) = 0.5

        [ToggleUI] _ThemeColorMode ("Use Custom Colors", Int) = 0
        _SelectedColor("Selected Color", Range(0, 3)) = 0
        _Hue("(HSV) Hue", Range(0, 1)) = 0.5
        _Saturation("(HSV) Saturation", Range(0, 1)) = 0.5
        _Value("(HSV) Value", Range(0, 1)) = 0.5

        _CustomColor0 ("Custom Color 0", Color) = (1.0, 0.0, 0.0, 1.0)
        _CustomColor1 ("Custom Color 1", Color) = (0.0, 1.0, 0.0, 1.0)
        _CustomColor2 ("Custom Color 2", Color) = (0.0, 0.0, 1.0, 1.0)
        _CustomColor3 ("Custom Color 3", Color) = (1.0, 1.0, 0.0, 1.0)

        _GainTexture ("Gain Texture", 2D) = "white" {}
        _AutoGainTexture ("Autogain Texture", 2D) = "white" {}
        _PowerTexture ("Power Texture", 2D) = "white" {}
        _ResetTexture ("Reset Texture", 2D) = "white" {}
    }
    SubShader
    {
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
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

            v2f vert (appdata v)
            {
                v2f o;
                // Prevent z-fighting on mobile by moving the panel out a bit
                #ifdef SHADER_API_MOBILE
                v.vertex.z -= 0.0012;
                #endif
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            // Uniforms
            float _Power;
            float _Gain;
            float _AutoGain;
            float _Threshold0;
            float _Threshold1;
            float _Threshold2;
            float _Threshold3;
            float _X0;
            float _X1;
            float _X2;
            float _X3;
            float _HitFade;
            float _ExpFalloff;
            uint _ThemeColorMode;
            uint _SelectedColor;
            float _Hue;
            float _Saturation;
            float _Value;
            float3 _CustomColor0;
            float3 _CustomColor1;
            float3 _CustomColor2;
            float3 _CustomColor3;
            sampler2D _GainTexture;
            float4 _GainTexture_TexelSize;
            sampler2D _AutoGainTexture;
            float4 _AutoGainTexture_TexelSize;
            sampler2D _PowerTexture;
            float4 _PowerTexture_TexelSize;
            sampler2D _ResetTexture;
            float4 _ResetTexture_TexelSize;

            // Colors
            const static float3 BACKGROUND_COLOR = 0.033;
            const static float3 FOREGROUND_COLOR = 0.075;
            const static float3 INACTIVE_COLOR = 0.13;
            const static float3 ACTIVE_COLOR = 0.8;
            const static float3 BASS_COLOR_BG = pow(float3(44.0/255.0, 12.0/255.0, 43.0/255.0), 2.2);
            const static float3 BASS_COLOR_MG = pow(float3(103.0/255.0, 27.0/255.0, 100.0/255.0), 2.2);
            const static float3 BASS_COLOR_FG = pow(float3(147.0/255.0, 39.0/255.0, 143.0/255.0), 2.2);
            const static float3 LOWMID_COLOR_BG = pow(float3(76.0/255.0, 53.0/255.0, 18.0/255.0), 2.2);
            const static float3 HIGHMID_COLOR_BG = pow(float3(42.0/255.0, 60.0/255.0, 19.0/255.0), 2.2);
            const static float3 HIGH_COLOR_BG = pow(float3(12.0/255.0, 52.0/255.0, 68.0/255.0), 2.2);
            const static float3 HIGH_COLOR_FG = pow(float3(41.0/255.0, 171.0/255.0, 226.0/255.0), 2.2);

            // Spacing
            const static float CORNER_RADIUS = 0.025;
            const static float FRAME_MARGIN = 0.03;
            const static float HANDLE_RADIUS = 0.007;
            const static float OUTLINE_WIDTH = 0.002;

            #define remap(value, low1, high1, low2, high2) ((low2) + ((value) - (low1)) * ((high2) - (low2)) / ((high1) - (low1)))

            #define COHERENT_CONDITION(condition) ((condition) || any(fwidth(condition)))
            #define ADD_ELEMENT(existing, elementColor, elementDist) [branch] if (COHERENT_CONDITION(elementDist <= 0.01)) addElement(existing, elementColor, elementDist)

            float3 selectColor(uint i, float3 a, float3 b, float3 c, float3 d)
            {
                return float4x4(
                    float4(a, 0.0),
                    float4(b, 0.0),
                    float4(c, 0.0),
                    float4(d, 0.0)
                )[i % 4];
            }

            float3 selectColorLerp(float i, float3 a, float3 b, float3 c, float3 d)
            {
                int me = floor(i);
                float3 meColor = selectColor(me, a, b, c, d);

                // avoid singularity at 0.5
                if (COHERENT_CONDITION(distance(frac(i), 0.5) < 0.1))
                    return meColor;

                int side = sign(frac(i) - 0.5);
                int other = clamp(me + side, 0, 3);

                float3 otherColor = selectColor(other, a, b, c, d);

                float dist = round(i) - i;
                const float pixelDiagonal = sqrt(2.0) / 2.0 * side;
                float distDerivativeLength = sqrt(pow(ddx(dist), 2) + pow(ddy(dist), 2));

                return lerp(otherColor, meColor, smoothstep(-pixelDiagonal, pixelDiagonal, dist/distDerivativeLength));
            }

            float3 getBandColor(uint i) { return selectColor(i, BASS_COLOR_BG, LOWMID_COLOR_BG, HIGHMID_COLOR_BG, HIGH_COLOR_BG); }
            float3 getBandColorLerp(float i) { return selectColorLerp(i, BASS_COLOR_BG, LOWMID_COLOR_BG, HIGHMID_COLOR_BG, HIGH_COLOR_BG); }

            // TODO: Deduplicate this
            float3 getBandAmplitudeLerp(float i, float delay)
            {
                int me = floor(i);
                float meStrength = AudioLinkLerp(float2(delay, me)).r;

                // avoid singularity at 0.5
                if (COHERENT_CONDITION(distance(frac(i), 0.5) < 0.1))
                    return meStrength;

                int side = sign(frac(i) - 0.5);
                int other = clamp(me + side, 0, 3);

                float otherStrength = AudioLinkLerp(float2(delay, other)).r;

                float dist = round(i) - i;
                const float pixelDiagonal = sqrt(2.0) / 2.0 * side;
                float distDerivativeLength = sqrt(pow(ddx(dist), 2) + pow(ddy(dist), 2));

                return lerp(otherStrength, meStrength, smoothstep(-pixelDiagonal, pixelDiagonal, dist/distDerivativeLength));
            }

            float2x2 rotationMatrix(float angle)
            {
                return float2x2(
                    float2(cos(angle), -sin(angle)),
                    float2(sin(angle), cos(angle))
                );
            }

            float2 translate(float2 p, float2 offset)
            {
                return p - offset;
            }

            float2 rotate(float2 p, float angle)
            {
                return mul(rotationMatrix(angle), p);
            }

            float shell(float d, float thickness)
            {
                return abs(d) - thickness;
            }

            float inflate(float d, float thickness)
            {
                return d - thickness;
            }

            float lerpstep(float a, float b, float x)
            {
                return saturate((x - a)/(b - a));
            }

            void addElement(inout float3 existing, float3 elementColor, float elementDist)
            {
                const float pixelDiagonal = sqrt(2.0) / 2.0;
                float distDerivativeLength = sqrt(pow(ddx(elementDist), 2) + pow(ddy(elementDist), 2));
                existing = lerp(elementColor, existing, lerpstep(-pixelDiagonal, pixelDiagonal, elementDist/distDerivativeLength));
            }

            float sdRoundedBoxCentered(float2 p, float2 b, float4 r)
            {
                r.xy = (p.x>0.0)?r.xy : r.zw;
                r.x  = (p.y>0.0)?r.x  : r.y;
                float2 q = abs(p)-b*0.5+r.x;
                return min(max(q.x,q.y),0.0) + length(max(q,0.0)) - r.x;
            }

            float sdRoundedBoxTopLeft(float2 p, float2 b, float4 r)
            {
                return sdRoundedBoxCentered(translate(p, b*0.5), b, r);
            }

            float sdRoundedBoxBottomRight(float2 p, float2 b, float4 r)
            {
                return sdRoundedBoxCentered(translate(p, float2(b.x,-b.y)*0.5), b, r);
            }

            float sdSphere(float2 p, float r)
            {
                return length(p) - r;
            }

            float sdTriangleIsosceles(float2 p, float2 q)
            {
                p.x = abs(p.x);
                float2 a = p - q*clamp( dot(p,q)/dot(q,q), 0.0, 1.0 );
                float2 b = p - q*float2( clamp( p.x/q.x, 0.0, 1.0 ), 1.0 );
                float s = -sign( q.y );
                float2 d = min( float2( dot(a,a), s*(p.x*q.y-p.y*q.x) ),
                            float2( dot(b,b), s*(p.y-q.y)  ));
                return -sqrt(d.x)*sign(d.y);
            }

            float sdTriangleRight(float2 p, float halfWidth, float halfHeight)
            {
                float2 end = float2(halfWidth, -halfHeight);
                float2 d = p - end * clamp(dot(p, end) / dot(end, end), -1.0, 1.0);
                if (max(d.x, d.y) > 0.0) {
                return length(d);
                }
                p += float2(halfWidth, halfHeight);
                if (max(p.x, p.y) > 0.0) {
                    return -min(length(d), min(p.x, p.y));
                }
                return length(p);
            }

            float sdSegment(float2 p, float2 a, float2 b)
            {
                float2 pa = p-a, ba = b-a;
                float h = clamp( dot(pa,ba)/dot(ba,ba), 0.0, 1.0 );
                return length( pa - ba*h );
            }

            #define TEX2D_MSDF(tex, uv) tex2DMSDF(tex, tex##_TexelSize.xy * 4.0, uv)

            float tex2DMSDF(sampler2D tex, float2 unit, float2 uv)
            {
                float3 c = tex2D(tex, uv).rgb;
                return saturate(
                    (max(min(c.r, c.g), min(max(c.r, c.g), c.b)) - 0.5) *
                    max(dot(unit, 0.5 / fwidth(uv)), 1) + 0.5
                );
            }

            float3 drawTopArea(float2 uv)
            {
                float3 color = FOREGROUND_COLOR;

                float areaWidth = 1.0 - FRAME_MARGIN * 2;
                float areaHeight = 0.35;
                float handleWidth = 0.015 * areaWidth;

                float threshold[4] = { _Threshold0, _Threshold1, _Threshold2, _Threshold3 };
                float crossover[4] = { _X0 * areaWidth, _X1 * areaWidth, _X2 * areaWidth, _X3 * areaWidth };

                // prefix sum to calculate offsets and sizes for boxes
                uint start = 0;
                uint stop = 4;
                float currentBoxOffset = crossover[start];
                float boxOffsets[4] = { 0, 0, 0, 0 };
                float boxWidths[4] = { 0, 0, 0, 0 };
                for (uint i = 0; i < 4; i++)
                {
                    float boxWidth = 0.0;
                    if (i == 3) // The last box should just stretch to fill
                        boxWidth = areaWidth - currentBoxOffset;
                    else
                        boxWidth = crossover[i + 1] - crossover[i];

                    boxOffsets[i] = currentBoxOffset;
                    boxWidths[i] = boxWidth;

                    // Keep track of the range of boxes we need to draw
                    if (COHERENT_CONDITION(uv.x > currentBoxOffset + OUTLINE_WIDTH))
                        start = i;
                    if (COHERENT_CONDITION(uv.x < currentBoxOffset + boxWidth - handleWidth))
                        stop = min(stop, i + 1);

                    currentBoxOffset += boxWidth;
                }

                // waveform calculation
                uint totalBins = AUDIOLINK_EXPBINS * AUDIOLINK_EXPOCT;
                uint noteno = AudioLinkRemap(uv.x, 0., 1., AUDIOLINK_4BAND_FREQFLOOR * totalBins, AUDIOLINK_4BAND_FREQCEILING * totalBins);
                float notenof = AudioLinkRemap(uv.x, 0., 1., AUDIOLINK_4BAND_FREQFLOOR * totalBins, AUDIOLINK_4BAND_FREQCEILING * totalBins);
                float4 specLow = AudioLinkData(float2(fmod(noteno, 128), (noteno/128)+4.0));
                float4 specHigh = AudioLinkData(float2(fmod(noteno+1, 128), ((noteno+1)/128)+4.0));
                float4 intensity = lerp(specLow, specHigh, frac(notenof)) * _Gain;
                float bandIntensity = AudioLinkData(float2(0., start ^ 0)); // XOR with 0 to avoid FXC miscompilation
                float funcY = areaHeight - (intensity.g * areaHeight);
                float waveformDist = smoothstep(0.005, 0.003, funcY - uv.y);
                float waveformDistAbs = abs(smoothstep(0.005, 0.003, abs(funcY - uv.y)));

                // background waveform
                color = lerp(color, color * 2, waveformDist);
                color = lerp(color, color * 2, waveformDistAbs);

                // This optimization increases performance, but introduces aliasing. The perf difference is only really noticeable on Quest.
                #if defined(UNITY_PBS_USE_BRDF2) || defined(SHADER_API_MOBILE)
                [loop] for (uint i = start; i < min(stop, 4); i++)
                #else
                for (uint i = 0; i < 4; i++)
                #endif
                {
                    float boxHeight = threshold[i] * areaHeight;
                    float boxWidth = boxWidths[i];
                    float boxOffset = boxOffsets[i];

                    float leftCornerRadius = i == 0 ? CORNER_RADIUS : 0.0;
                    float rightCornerRadius = i == 3 ? CORNER_RADIUS : 0.0;
                    float boxDist = sdRoundedBoxBottomRight(
                        translate(uv, float2(boxOffset, areaHeight)),
                        float2(boxWidth, boxHeight),
                        float4(rightCornerRadius, CORNER_RADIUS, leftCornerRadius, CORNER_RADIUS)
                    );

                    // colored inner portion
                    float3 innerColor = getBandColor(i);
                    innerColor = lerp(innerColor, innerColor * 3, waveformDist);
                    innerColor = lerp(innerColor, lerp(innerColor * 3, 1.0, bandIntensity > threshold[i]), waveformDistAbs);
                    ADD_ELEMENT(color, innerColor, boxDist+OUTLINE_WIDTH);

                    // outer shell
                    float shellDist = shell(boxDist, OUTLINE_WIDTH);
                    ADD_ELEMENT(color, ACTIVE_COLOR, shellDist);

                    // Top pivot
                    float handleDist = sdSphere(
                        translate(uv, float2(boxWidth * 0.5 + boxOffset, areaHeight-boxHeight)),
                        HANDLE_RADIUS
                    );
                    ADD_ELEMENT(color, 1.0, handleDist);

                    // Side pivot
                    handleDist = sdRoundedBoxCentered(
                        translate(uv, float2(boxOffset, areaHeight - boxHeight * 0.5)),
                        float2(handleWidth, 0.35 * boxHeight),
                        HANDLE_RADIUS
                    );
                    ADD_ELEMENT(color, 1.0, handleDist);
                }

                return color;
            }

            float3 drawGainArea(float2 uv, float2 size)
            {
                float3 inactiveColor = INACTIVE_COLOR;
                float3 activeColor = ACTIVE_COLOR;
                float3 t = _Gain / 2.0f;

                float3 color = FOREGROUND_COLOR;

                float gainIcon = TEX2D_MSDF(_GainTexture, saturate((uv - float2(0.01, 0.0)) / size.y));
                color = lerp(color, ACTIVE_COLOR, gainIcon.r);

                const float sliderOffsetLeft = 0.16;
                const float sliderOffsetRight = 0.02;

                // Background fill
                float maxTriangleWidth = size.x - sliderOffsetLeft - sliderOffsetRight;
                float bgTriangleDist = inflate(sdTriangleIsosceles(
                    rotate(translate(uv, float2(sliderOffsetLeft, size.y * 0.5)), UNITY_PI*0.5),
                    float2(size.y*0.3, maxTriangleWidth)
                ), 0.002);
                ADD_ELEMENT(color, inactiveColor, bgTriangleDist);

                // Current active area
                float currentTriangleWidth = maxTriangleWidth * t;
                float currentTriangleDist = max(bgTriangleDist, uv.x - currentTriangleWidth - sliderOffsetLeft);
                ADD_ELEMENT(color, activeColor, currentTriangleDist);

                // Slider handle
                float handleDist = sdSphere(
                    translate(uv, float2(currentTriangleWidth + sliderOffsetLeft, size.y * 0.5)),
                    HANDLE_RADIUS
                );
                ADD_ELEMENT(color, ACTIVE_COLOR, handleDist);

                // Slider vertical grip
                float gripDist = abs(uv.x - currentTriangleWidth - sliderOffsetLeft) - OUTLINE_WIDTH;
                ADD_ELEMENT(color, ACTIVE_COLOR, gripDist);

                return color;
            }

            float drawAutoGainButton(float2 uv, float2 size)
            {
                float2 scaledUV = uv / size;
                float autoGainIcon = TEX2D_MSDF(_AutoGainTexture, float2(scaledUV.x, 1-scaledUV.y));
                return lerp(FOREGROUND_COLOR, _AutoGain ? ACTIVE_COLOR : INACTIVE_COLOR, autoGainIcon);
            }

            float drawPowerButton(float2 uv, float2 size)
            {
                float2 scaledUV = uv / size;
                float powerIcon = TEX2D_MSDF(_PowerTexture, float2(scaledUV.x, 1-scaledUV.y));
                return lerp(FOREGROUND_COLOR, _Power ? ACTIVE_COLOR : INACTIVE_COLOR, powerIcon);
            }

            float drawResetButton(float2 uv, float2 size)
            {
                float2 scaledUV = uv / size;
                float resetIcon = TEX2D_MSDF(_ResetTexture, float2(scaledUV.x, 1-scaledUV.y));
                return lerp(FOREGROUND_COLOR, ACTIVE_COLOR, resetIcon);
            }

            float3 drawHitFadeArea(float2 uv, float2 size)
            {
                float3 color = FOREGROUND_COLOR;

                // Background fill
                float2 triUV = -(uv - float2(size.x / 2, size.y / 2));

                float halfWidth = 0.45 * size.x;
                float halfHeight = 0.37 * size.y;
                float fullWidth = halfWidth * 2;
                float fullHeight = halfHeight * 2;
                float bgTriangleDist = inflate(sdTriangleRight(triUV, halfWidth, halfHeight), 0.002);
                ADD_ELEMENT(color, INACTIVE_COLOR, bgTriangleDist);

                // Current active area
                float remainingWidth = size.x - fullWidth;
                float remainingHeight = size.y - fullHeight;
                float marginX = remainingWidth / 2;
                float marginY = remainingHeight / 2;

                float invHitFade = 1 - _HitFade;
                triUV.x += halfWidth * invHitFade;
                float fgTriangleDist = inflate(sdTriangleRight(triUV, halfWidth * _HitFade, halfHeight), 0.002);
                ADD_ELEMENT(color, ACTIVE_COLOR, fgTriangleDist);

                // Slider handle
                float handleDist = sdSphere(
                    translate(uv, float2(invHitFade * fullWidth + marginX, size.y * 0.5)),
                    HANDLE_RADIUS
                );
                ADD_ELEMENT(color, ACTIVE_COLOR, handleDist);

                // Slider vertical grip
                float gripDist = abs(uv.x - invHitFade * halfWidth * 2 - marginX) - OUTLINE_WIDTH;
                ADD_ELEMENT(color, ACTIVE_COLOR, gripDist);

                return color;
            }

            float3 drawExpFalloffArea(float2 uv, float2 size)
            {
                float3 color = FOREGROUND_COLOR;

                // Background fill
                float2 triUV = -(uv - float2(size.x / 2, size.y / 2));

                float halfWidth = 0.45 * size.x;
                float halfHeight = 0.37 * size.y;
                float fullWidth = halfWidth * 2;
                float fullHeight = halfHeight * 2;
                float bgTriangleDist = inflate(sdTriangleRight(triUV, halfWidth, halfHeight), 0.002);
                ADD_ELEMENT(color, INACTIVE_COLOR, bgTriangleDist);

                // Current active area
                float remainingWidth = size.x - fullWidth;
                float remainingHeight = size.y - fullHeight;
                float marginX = remainingWidth / 2;
                float marginY = remainingHeight / 2;
                float triUVx = remap(uv.x, marginX, size.x-marginX, 0, 1);
                float triUVy = remap(uv.y, marginY, size.y-marginY, 0, 1);

                float expFalloffY = (1.0 + (pow(triUVx, 4.0) * _ExpFalloff) - _ExpFalloff) * triUVx;
                float fgDist = inflate((1.0 - triUVy) - expFalloffY, 0.02);
                ADD_ELEMENT(color, ACTIVE_COLOR, max(bgTriangleDist, fgDist*0.1));

                // Slider handle
                float handleDist = sdSphere(
                    translate(uv, float2(_ExpFalloff * fullWidth + marginX, size.y * 0.5)),
                    HANDLE_RADIUS
                );
                ADD_ELEMENT(color, ACTIVE_COLOR, handleDist);

                // Slider vertical grip
                float gripDist = abs(uv.x - _ExpFalloff * halfWidth * 2 - marginX) - OUTLINE_WIDTH;
                ADD_ELEMENT(color, ACTIVE_COLOR, gripDist);

                return color;
            }

            float3 drawFourBandArea(float2 uv, float2 size)
            {
                float3 color = FOREGROUND_COLOR;

                float2 sliceSize = float2(size.x, size.y / 4.0);
                float strength = getBandAmplitudeLerp((uv.y / size.y) * 4.0, uv.x / size.x * 64.0);
                float3 sliceColor = getBandColorLerp(uv.y / sliceSize.y);
                sliceColor = saturate(lerp(sliceColor, sliceColor * 15, strength));

                return sliceColor;
            }

            float3 drawHueArea(float2 uv, float2 size)
            {
                float hue = uv.x / size.x;
                float3 color = AudioLinkHSVtoRGB(float3(hue, 1.0, 1.0));

                float sliderOffset = size.x * _Hue;
                float handleDist = sdSphere(
                    translate(uv, float2(sliderOffset, size.y * 0.5)),
                    HANDLE_RADIUS
                );
                ADD_ELEMENT(color, ACTIVE_COLOR, handleDist);

                float gripDist = abs(uv.x - sliderOffset) - OUTLINE_WIDTH;
                ADD_ELEMENT(color, ACTIVE_COLOR, gripDist);

                return color;
            }

            float3 drawSaturationArea(float2 uv, float2 size)
            {
                float saturation = 1.0 - uv.y / size.y;
                float3 color = AudioLinkHSVtoRGB(float3(_Hue, saturation, _Value));

                float sliderOffset = size.y * (1 - _Saturation);
                float handleDist = sdSphere(
                    translate(uv, float2(size.x * 0.5, sliderOffset)),
                    HANDLE_RADIUS
                );
                ADD_ELEMENT(color, ACTIVE_COLOR, handleDist);

                float gripDist = abs(uv.y - sliderOffset) - OUTLINE_WIDTH;
                ADD_ELEMENT(color, ACTIVE_COLOR, gripDist);

                return color;
            }

            float3 drawValueArea(float2 uv, float2 size)
            {
                float value = 1.0 - uv.y / size.y;
                float3 color = AudioLinkHSVtoRGB(float3(_Hue, _Saturation, value));

                float sliderOffset = size.y * (1 - _Value);
                float handleDist = sdSphere(
                    translate(uv, float2(size.x * 0.5, sliderOffset)),
                    HANDLE_RADIUS
                );
                ADD_ELEMENT(color, ACTIVE_COLOR, handleDist);

                float gripDist = abs(uv.y - sliderOffset) - OUTLINE_WIDTH;
                ADD_ELEMENT(color, ACTIVE_COLOR, gripDist);

                return color;
            }

            float3 drawColorChordToggle(float2 uv, float2 size)
            {
                float colorIndex = uv.x / size.x * 3.97;
                float3 a = AudioLinkData(ALPASS_THEME_COLOR0 + uint2(0, 0));
                float3 b = AudioLinkData(ALPASS_THEME_COLOR0 + uint2(1, 0));
                float3 c = AudioLinkData(ALPASS_THEME_COLOR0 + uint2(2, 0));
                float3 d = AudioLinkData(ALPASS_THEME_COLOR0 + uint2(3, 0));

                // If no music is playing, let's just show theme colors so it isn't black
                if (!any(a) && !any(b) && !any(c) && !any(d))
                {
                    a = _CustomColor0;
                    b = _CustomColor1;
                    c = _CustomColor2;
                    d = _CustomColor3;
                }

                return selectColorLerp(colorIndex, a, b, c, d);
            }

            float3 drawAutoCorrelatorArea(float2 uv, float2 size)
            {
                float3 color = FOREGROUND_COLOR;

                float2 scaledUV = uv / size;

                float2 mirroredUV = abs(2*(scaledUV - 0.5));
                float3 autoCorrelator = AudioLinkLerp(ALPASS_AUTOCORRELATOR + float2(mirroredUV.x * AUDIOLINK_WIDTH, 0));
                float scaledAutoCorrelator = abs(autoCorrelator.r*0.007);

                float middle = size.y * 0.5;
                float autoCorrelatorDist = smoothstep(0.005, 0.003, middle - uv.y);
                float autoCorrelatorDistAbs = abs(smoothstep(0.005, 0.003, abs(middle - uv.y) - scaledAutoCorrelator));

                float4 vu = saturate(AudioLinkData(ALPASS_FILTEREDVU_INTENSITY) * 2.5);
                float autoCorrelatorColor = lerp(FOREGROUND_COLOR, ACTIVE_COLOR, vu);
                autoCorrelatorColor = lerp(autoCorrelatorColor, FOREGROUND_COLOR, smoothstep(0, 1, mirroredUV.x));

                return lerp(BACKGROUND_COLOR * 0.8, autoCorrelatorColor, autoCorrelatorDistAbs);
            }

            float3 drawUI(float2 uv)
            {
                float3 color = BACKGROUND_COLOR;

                const float margin = 0.03;
                float currentY = 0;

                // Top area
                float2 topAreaOrigin = translate(uv, FRAME_MARGIN);
                float2 topAreaSize = float2(1.0 - FRAME_MARGIN * 2, 0.35);
                float topAreaDist = sdRoundedBoxTopLeft(topAreaOrigin, topAreaSize, CORNER_RADIUS);
                ADD_ELEMENT(color, drawTopArea(topAreaOrigin), topAreaDist);
                currentY += topAreaSize.y + margin;

                const float gainSliderHeight = 0.13;
                const float gainSliderWidth = topAreaSize.x - gainSliderHeight - margin;
                const float fadeSliderHeight = 0.19;

                // Gain slider
                float2 gainSliderOrigin = translate(uv, FRAME_MARGIN + float2(0, currentY));
                float2 gainSliderSize = float2(gainSliderWidth, gainSliderHeight);
                float gainSliderDist = sdRoundedBoxTopLeft(gainSliderOrigin, gainSliderSize, CORNER_RADIUS);
                ADD_ELEMENT(color, drawGainArea(gainSliderOrigin, gainSliderSize), gainSliderDist);

                // Autogain button
                float2 autogainButtonOrigin = translate(uv, FRAME_MARGIN + float2(gainSliderWidth + margin, currentY));
                float2 autogainButtonSize = float2(gainSliderHeight, gainSliderHeight);
                float autogainButtonDist = sdRoundedBoxTopLeft(autogainButtonOrigin, autogainButtonSize, CORNER_RADIUS);
                ADD_ELEMENT(color, drawAutoGainButton(autogainButtonOrigin, autogainButtonSize), autogainButtonDist);
                currentY += autogainButtonSize.y + margin;

                // Hit fade
                float2 hitFadeAreaOrigin = translate(uv, FRAME_MARGIN + float2(0, currentY));
                float2 hitFadeAreaSize = float2(topAreaSize.x * 0.5 - margin * 0.5, fadeSliderHeight);
                float hitFadeAreaDist = sdRoundedBoxTopLeft(hitFadeAreaOrigin, hitFadeAreaSize, CORNER_RADIUS);
                ADD_ELEMENT(color, drawHitFadeArea(hitFadeAreaOrigin, hitFadeAreaSize), hitFadeAreaDist);

                // Exp fallof
                float2 expFalloffAreaOrigin = translate(uv, FRAME_MARGIN + float2(hitFadeAreaSize.x + margin, currentY));
                float2 expFalloffAreaSize = float2(topAreaSize.x * 0.5 - margin * 0.5, fadeSliderHeight);
                float expFalloffAreaDist = sdRoundedBoxTopLeft(expFalloffAreaOrigin, expFalloffAreaSize, CORNER_RADIUS);
                ADD_ELEMENT(color, drawExpFalloffArea(expFalloffAreaOrigin, expFalloffAreaSize), expFalloffAreaDist);
                currentY += expFalloffAreaSize.y + margin;

                // 4-band
                float2 fourBandOrigin = translate(uv, FRAME_MARGIN + float2(0, currentY));
                float2 fourBandSize = float2(topAreaSize.x, fadeSliderHeight);
                float fourBandDist = sdRoundedBoxTopLeft(fourBandOrigin, fourBandSize, CORNER_RADIUS);
                ADD_ELEMENT(color, drawFourBandArea(fourBandOrigin, fourBandSize), fourBandDist);
                currentY += fourBandSize.y + margin;

                // Gray out irrelevant controls
                float themeColorMultiplier = lerp(0.2, 1.0, _ThemeColorMode);
                float colorChordMultiplier = lerp(1.0, 0.2, _ThemeColorMode);

                // Theme colors
                float colorWidth = topAreaSize.x * 0.25 - margin * 0.75;
                float2 colorSize = float2(colorWidth, gainSliderHeight);
                uint colorIndex = min(remap(uv.x, margin, 1-margin, 0, 4), 3);
                float2 colorOrigin = translate(uv, FRAME_MARGIN + float2(0, currentY) + float2(colorIndex * (colorSize.x + margin), 0));
                float colorDist = sdRoundedBoxTopLeft(colorOrigin, colorSize, CORNER_RADIUS);
                float3 colors[4] = { _CustomColor0, _CustomColor1, _CustomColor2, _CustomColor3 };
                if (fwidth(colorIndex) == 0)
                {
                    ADD_ELEMENT(color, colors[colorIndex] * themeColorMultiplier, colorDist);

                    if (colorIndex == _SelectedColor % 4)
                    {
                        float shellDist = shell(colorDist, OUTLINE_WIDTH);
                        ADD_ELEMENT(color, ACTIVE_COLOR * themeColorMultiplier, shellDist);
                    }
                }
                currentY += colorSize.y + margin;

                // Hue
                float2 hueOrigin = translate(uv, FRAME_MARGIN + float2(0, currentY));
                float2 hueSize = float2(topAreaSize.x * 0.5 - margin * 0.5, 0.2);
                float hueDist = sdRoundedBoxTopLeft(hueOrigin, hueSize, CORNER_RADIUS);
                ADD_ELEMENT(color, drawHueArea(hueOrigin, hueSize) * themeColorMultiplier, hueDist);

                // Saturation / Value
                float2 satOrigin = translate(uv, FRAME_MARGIN + float2(hueSize.x + margin, currentY));
                float2 satSize = float2(colorSize.x / 2 - margin / 2, 0.2);
                float satDist = sdRoundedBoxTopLeft(satOrigin, satSize, CORNER_RADIUS);
                ADD_ELEMENT(color, drawSaturationArea(satOrigin, satSize) * themeColorMultiplier, satDist);

                float2 valOrigin = translate(uv, FRAME_MARGIN + float2(hueSize.x + satSize.x + margin * 2, currentY));
                float2 valSize = satSize;
                float valDist = sdRoundedBoxTopLeft(valOrigin, valSize, CORNER_RADIUS);
                ADD_ELEMENT(color, drawValueArea(valOrigin, valSize) * themeColorMultiplier, valDist);

                // CC toggle
                float2 ccToggleOrigin = translate(uv, FRAME_MARGIN + float2(colorSize.x + margin, currentY) + float2(hueSize.x + margin, 0));
                float2 ccToggleSize = float2(colorSize.x, 0.2);
                float ccToggleDist = sdRoundedBoxTopLeft(ccToggleOrigin, ccToggleSize, CORNER_RADIUS);
                ADD_ELEMENT(color, drawColorChordToggle(ccToggleOrigin, ccToggleSize) * colorChordMultiplier, ccToggleDist);
                if (_ThemeColorMode == 0)
                {
                    float shellDist = shell(ccToggleDist, OUTLINE_WIDTH);
                    ADD_ELEMENT(color, ACTIVE_COLOR, shellDist);
                }
                currentY += hueSize.y + margin;

                // Power button
                float2 powerButtonOrigin = translate(uv, FRAME_MARGIN + float2(0, currentY));
                float2 powerButtonSize = float2(gainSliderHeight, gainSliderHeight);
                float powerButtonDist = sdRoundedBoxTopLeft(powerButtonOrigin, powerButtonSize, CORNER_RADIUS);
                ADD_ELEMENT(color, drawPowerButton(powerButtonOrigin, powerButtonSize), powerButtonDist);

                // Reset button
                float2 resetButtonOrigin = translate(uv, FRAME_MARGIN + float2(gainSliderWidth + margin, currentY));
                float2 resetButtonSize = float2(gainSliderHeight, gainSliderHeight);
                float resetButtonDist = sdRoundedBoxTopLeft(resetButtonOrigin, resetButtonSize, CORNER_RADIUS);
                ADD_ELEMENT(color, drawResetButton(resetButtonOrigin, resetButtonSize), resetButtonDist);

                // Spectrogram area
                float2 autoCorrelatorButtonOrigin = translate(uv, FRAME_MARGIN + float2(powerButtonSize.x + margin, currentY));
                float2 autoCorrelatorButtonSize = float2(topAreaSize.x - powerButtonSize.x - resetButtonSize.x - margin * 2, gainSliderHeight);
                float autoCorrelatorButtonDist = sdRoundedBoxTopLeft(autoCorrelatorButtonOrigin, autoCorrelatorButtonSize, CORNER_RADIUS);
                ADD_ELEMENT(color, drawAutoCorrelatorArea(autoCorrelatorButtonOrigin, autoCorrelatorButtonSize), autoCorrelatorButtonDist);

                return color;
            }

            float4 frag (v2f i) : SV_Target
            {
                float2 uv = float2(i.uv.x, 1.0 - i.uv.y);
                uv.y *= 0.3398717 / 0.218; // aspect ratio
                return float4(drawUI(uv), 1);
            }
            ENDCG
        }
    }
}
