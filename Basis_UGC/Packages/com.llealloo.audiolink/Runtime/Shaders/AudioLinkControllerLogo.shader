Shader "AudioLink/Internal/AudioLinkControllerLogo"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (0,0,0,0)
        _PulseWidth ("Pulse Width", Range(0,1)) = 0.4
        _PulseOffsetQ ("Pulse Offset Q", Range(0,1)) = 0.58
        _QPower("Q Power", Range(0.1,5)) = 4
        _Gain ("Gain", Range(0,10)) = 4.38
    }

    SubShader
    {
        Tags{"RenderType" = "Custom"  "Queue" = "Transparent+0" "IgnoreProjector" = "True"}
        Cull Off
        ZWrite On
        Blend One One

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
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

                UNITY_VERTEX_OUTPUT_STEREO
            };

            float4 _BaseColor;
            float _PulseWidth;
            float _PulseOffsetQ;
            float _Gain;
            float _QPower;

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

            fixed4 frag (v2f i) : SV_Target
            {
                float2 uv = i.uv;
                
                float offset = pow(abs(2. * uv.y - 1.) * _PulseOffsetQ, _QPower);
                float position = abs(2. * uv.x - 1.);
                float4 bassColor = AudioLinkLerp(ALPASS_AUDIOLINK + float2((position + offset) * _PulseWidth * 128., 0)).r * AudioLinkData(ALPASS_THEME_COLOR0) * smoothstep(1, 0, position);
                float4 lowMidColor = AudioLinkLerp(ALPASS_AUDIOLINK + float2((position + offset) * _PulseWidth * 128., 1)).r * AudioLinkData(ALPASS_THEME_COLOR3) * smoothstep(1, 0, position);
                float4 highMidColor = AudioLinkLerp(ALPASS_AUDIOLINK + float2((1 - (position - offset)) * _PulseWidth * 128., 2)).r * AudioLinkData(ALPASS_THEME_COLOR2) * smoothstep(0, 1, position);
                float4 trebleColor = AudioLinkLerp(ALPASS_AUDIOLINK + float2((1 - (position - offset)) * _PulseWidth * 128., 3)).r * AudioLinkData(ALPASS_THEME_COLOR1) * smoothstep(0, 1, position);

                return saturate(_BaseColor + (bassColor + lowMidColor + highMidColor + trebleColor) * _Gain);
            }
            ENDCG
        }
    }
}
