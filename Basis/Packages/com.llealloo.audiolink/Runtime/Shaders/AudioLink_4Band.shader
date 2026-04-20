Shader "AudioLink/Internal/AudioLink_4Band"
{
    Properties
    {
        _Band0Color("Band 0 Color", Color) = (0,0,0,0)
        _Band1Color("Band 1 Color", Color) = (0,0,0,0)
        _Band2Color("Band 2 Color", Color) = (0,0,0,0)
        _Band3Color("Band 3 Color", Color) = (0,0,0,0)
        [ToggleUI]_SmoothHistory("Smooth History", Float) = 0
        _History("History", Range(0, 128)) = 32
    }

    SubShader
    {
        Tags
        {
            "RenderType"="Opaque"
        }
        LOD 100

        Blend Off
        AlphaToMask Off
        Cull Back
        ColorMask RGBA
        ZWrite On
        ZTest LEqual
        Offset 0, 0

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma target 3.0

            #include "UnityCG.cginc"
            #include "Packages/com.llealloo.audiolink/Runtime/Shaders/AudioLink.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float _SmoothHistory;
            float _History;
            float4 _Band0Color;
            float4 _Band1Color;
            float4 _Band2Color;
            float4 _Band3Color;

            inline float AudioLinkLerp3(int Band, float Delay)
            {
                return AudioLinkLerp(ALPASS_AUDIOLINK + float2(Delay, Band)).r;
            }

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                UNITY_TRANSFER_INSTANCE_ID(v, o);

                o.uv = v.uv;
                o.vertex = UnityObjectToClipPos(v.vertex);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                float2 texCoord = i.uv;
                float bandValue = texCoord.y * 4.0;
                int band = (int)bandValue;

                float historyValue = _History * texCoord.x;
                float delay = _SmoothHistory ? historyValue : floor(historyValue);

                float audioValue = AudioLinkLerp3(band, delay);
                float4 audioColor = float4(audioValue, audioValue, audioValue, 1);

                float bandNumber = floor(bandValue);
                float4 bandColor = float4(0, 0, 0, 0);

                if (bandNumber == 0.0)
                    bandColor = _Band0Color;
                else if (bandNumber == 1.0)
                    bandColor = _Band1Color;
                else if (bandNumber == 2.0)
                    bandColor = _Band2Color;
                else if (bandNumber == 3.0)
                    bandColor = _Band3Color;

                // Color blend operation (overlay blend)
                float luminance = audioValue; // Simplified since audioValue is already grayscale

                float4 finalColor;
                if (luminance < 0.5)
                    finalColor = 2.0 * audioColor * bandColor;
                else
                    finalColor = 1.0 - (2.0 * (1.0 - audioColor) * (1.0 - bandColor));

                return finalColor;
            }
            ENDCG
        }

        Pass
        {
            Name "DepthOnly"
            Tags
            {
                "LightMode" = "DepthOnly"
            }

            ZWrite On
            ColorMask 0
            Cull Back

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                o.vertex = UnityObjectToClipPos(v.vertex);
                return o;
            }

            float4 frag(v2f i) : SV_TARGET
            {
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                return 0;
            }
            ENDCG
        }

        Pass
        {
            Name "DepthNormals"
            Tags
            {
                "LightMode" = "DepthNormals"
            }

            ZWrite On
            Cull Back

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float3 normal : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.normal = UnityObjectToWorldNormal(v.normal);
                return o;
            }

            float4 frag(v2f i) : SV_TARGET
            {
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float3 normal = normalize(i.normal);
                return float4(normal * 0.5 + 0.5, 1);
            }
            ENDCG
        }
    }
}
