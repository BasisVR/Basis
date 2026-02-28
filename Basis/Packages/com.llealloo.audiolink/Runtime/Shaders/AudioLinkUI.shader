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
            #include "AudioLinkUI-Functions.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                // Prevent z-fighting on mobile by moving the panel out a bit
                #if defined(SHADER_API_MOBILE) || SHADER_API_GLCORE
                v.vertex.z -= 0.0012;
                #endif
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                float2 uv = float2(i.uv.x, 1.0 - i.uv.y);
                uv.y *= 0.3398717 / 0.218; // aspect ratio
                return float4(drawUI(uv), 1);
            }
            ENDCG
        }

        // DepthOnly pass
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

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                #if defined(SHADER_API_MOBILE) || SHADER_API_GLCORE
                v.vertex.z -= 0.0012;
                #endif
                o.vertex = UnityObjectToClipPos(v.vertex);
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                return 0;
            }
            ENDCG
        }

        // DepthNormals pass
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
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                #if defined(SHADER_API_MOBILE) || SHADER_API_GLCORE
                v.vertex.z -= 0.0012;
                #endif
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.normal = UnityObjectToWorldNormal(v.normal);
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float3 normalWS = normalize(i.normal);
                return float4(normalWS * 0.5 + 0.5, 1);
            }
            ENDCG
        }
    }
}
