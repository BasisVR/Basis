Shader "AudioLink/GlobalStringExample"
{
    SubShader
    {
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"
            #include "Packages/com.llealloo.audiolink/Runtime/Shaders/AudioLink.cginc"
            #include "Packages/com.llealloo.audiolink/Runtime/Shaders/SmoothPixelFont.cginc"

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

            #define PIXELFONT_ROWS 4
            #define PIXELFONT_COLS 32

            float4 frag (v2f i) : SV_Target
            {
                i.uv.y = 1.0 - i.uv.y;
                
                // Pixel location on font pixel grid
                float2 pos = i.uv * float2(PIXELFONT_COLS, PIXELFONT_ROWS);
                uint2 pixel = (uint2)pos;
                
                // Fetch character from audiolink 
                int character = AudioLinkGetGlobalStringChar(pixel.y, pixel.x);

                // AA trick
                float2 softness_uv = pos * float2( 4, 6 );
                float softness = 4./(pow( length( float2( ddx( softness_uv.x ), ddy( softness_uv.y ) ) ), 0.5 ))-1.;

                // Render char
                float2 charUV = float2(4, 6) - fmod(pos, 1.0) * float2(4.0, 6.0);
                return PrintChar(character, charUV, softness, 0);
            }
            ENDCG
        }
    }
}
