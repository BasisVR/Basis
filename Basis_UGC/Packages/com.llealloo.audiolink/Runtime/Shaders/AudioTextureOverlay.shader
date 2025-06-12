Shader "AudioLink/Debug/AudioTextureOverlay"
{
    Properties
    {
        _OverlayTexture("Overlay Texture", 2D) = "black" {}
        _OverlayOpacity("Overlay Opacity", Range(0.0, 1.0)) = 0.2
        _HighlightPosition("Highlight Position", Vector) = (0.0, 0.0, 0.0, 0.0)
        _HighlightOpacity("Highlight Opacity", Range(0.0, 1.0)) = 0.3
        _HighlightThickness("Highlight Thickness", Float) = 0.1
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

            #include "UnityCG.cginc"
            #include "AudioLink.cginc"

            #define TEXTURE_WIDTH 128
            #define TEXTURE_HEIGHT 64
            #define STROKE_POWER 1000

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

            sampler2D _OverlayTexture;

            uniform float _OverlayOpacity;
            uniform float4 _HighlightPosition;
            uniform float _HighlightOpacity;
            uniform float _HighlightThickness;

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
                // Main overlay
                int2 coord = i.uv * float2(TEXTURE_WIDTH, TEXTURE_HEIGHT);
                float4 overlay = tex2D(_OverlayTexture, i.uv);
                overlay.a = 1.0;
                // Additive blending under 0.5 _OverlayOpacity, then normal opacity blend above 0.5
                float4 baseLayer = lerp(saturate(AudioLinkData(coord) + (overlay * _OverlayOpacity)), overlay, saturate((_OverlayOpacity * 2.0) - 1.0));

                // Highlighted area stroke lines
                float2 textureSize = float2(TEXTURE_WIDTH, TEXTURE_HEIGHT);
                float2 pointA = _HighlightPosition.xy / textureSize;       // x = p1x; y = p1y; z = p2x; w = p2y;
                float2 pointB = _HighlightPosition.zw / textureSize;
                float2 thickness = _HighlightThickness / textureSize;
                float4 stroke = float4(saturate((thickness - abs(i.uv - pointA)) * STROKE_POWER), saturate((thickness - abs(i.uv - pointB)) * STROKE_POWER));
                stroke = min(dot(stroke, 1), 1.0);

                // Highlighted area rectangle mask
                float2 mask = (i.uv > pointA && i.uv < pointB) ? 1.0 : 0.0;
                stroke *= min(mask.x, mask.y) * _HighlightOpacity;

                return baseLayer + stroke;
            }
            ENDCG
        }
    }
}
