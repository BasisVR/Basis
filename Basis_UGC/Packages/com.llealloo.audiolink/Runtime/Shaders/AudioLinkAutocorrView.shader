Shader "AudioLink/Debug/AudioLinkAutocorrView"
{
    Properties
    {
        [Header(Autocorrelator)]
        _AutocorrIntensity ("Autocorrelator Intensity", Float) = 0.1
        _AutocorrNormalization ("Normalization Amount", Float) = 1
        _AutocorrRound("Roundness", Range(0, 1)) = 1

        [Header(Color)]
        _ColorForeground ("Color Foreground", Color) = (1, 1, 1, 1)
        _ColorBackground ("Color Background", Color) = (0, 0, 0, 1)
        _ColorChord ("ColorChord Usage", Range(-1, 1)) = 1
        _ColorChordRange ("ColorChord Range", Float) = 350
        _Fadeyness ("Fade",Range(0, 2)) = 1
        _Brightness ("Brightness", Float ) = 2

        [Header(Bubble)]
        _BubbleOffset ("Bubble Radius", Float) = 0.5
        _XOffset ("X Offset", Float) = 0
        _YOffset ("Y Offset", Float) = 0
        _BubbleRotationSpeed ("Bubble Rotation Speed", Float ) = 0
        _BubbleRotationMultiply ("Bubble Rotation Multiply", Float ) = 1
        _BubbleRotationOffset ("Bubble Rotation Offset",Float ) = -1
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha

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

            float _AutocorrIntensity;
            float _AutocorrNormalization;
            float _AutocorrRound;

            float4 _ColorForeground;
            float4 _ColorBackground;
            float _ColorChord;
            float _ColorChordRange;
            float _Brightness;
            float _Fadeyness;
            
            float _BubbleOffset;
            float _XOffset;
            float _YOffset;
            float _BubbleRotationSpeed;
            float _BubbleRotationMultiply;
            float _BubbleRotationOffset;
            
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

            fixed4 frag (v2f i) : SV_Target
            {
                float2 uvCenter = float2(i.uv.x - _XOffset, i.uv.y - _YOffset) * 2 - 1;

                // Get polar angle after transformations, calc difference to upright angle
                float angle = atan2(uvCenter.x, uvCenter.y) / UNITY_PI;
                angle = glsl_mod(angle * _BubbleRotationMultiply + _BubbleRotationSpeed * _Time.y + _BubbleRotationOffset, 2.0);
                float angleDelta = abs(angle - 1.0) * (AUDIOLINK_WIDTH - 1);
                
                // Read autocorrelator value, apply normalization
                float sinCoord = lerp(abs(uvCenter.x * AUDIOLINK_WIDTH), angleDelta, _AutocorrRound);
                float sinVal = AudioLinkLerpMultiline(ALPASS_AUTOCORRELATOR + float2(sinCoord, 0));
                sinVal *= lerp(1.0, rsqrt(AudioLinkData( ALPASS_AUTOCORRELATOR ).r), _AutocorrNormalization);
                sinVal *= _AutocorrIntensity;
                
                // Get distance to circle, subtract from autocorrelator value
                float dist = lerp(abs(uvCenter.y), length(uvCenter), _AutocorrRound) - _BubbleOffset;
                dist = sinVal - dist;
                
                // Fetch colorchord data, lerp to chosen colors
                float4 mainColor = lerp(_ColorBackground, _ColorForeground, dist > 0);
                float3 colorChordColor = AudioLinkData(int2(ALPASS_CCSTRIP + int2(clamp(abs(dist * _ColorChordRange), 0, AUDIOLINK_WIDTH - 1), 0)));
                float3 color = lerp(mainColor.rgb, colorChordColor.rgb, _ColorChord);
                
                // Apply fade, brightness, fog
                color *= lerp(1, dist, _Fadeyness);
                color *= _Brightness;
                float4 finalColor = float4(color, mainColor.a);
                UNITY_APPLY_FOG(i.fogCoord, finalColor);

                return saturate(finalColor);
            }
            ENDCG
        }
    }
}
