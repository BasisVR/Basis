Shader "AudioLink/Examples/Demo4"
{
    Properties
    {
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }

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
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float3 vpOrig : TEXCOORD0;
                float3 vpXform : TEXCOORD1;
                float4 vertex : SV_POSITION;
                float  corrmax : TEXCOORD2;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;

                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                float3 vPos = v.vertex;

                o.vpOrig = vPos;

                vPos *= AudioLinkGetSphericalMappedAutoCorrelatorValue(vPos);

                // Perform same operation to find max.  The 0th bin on the
                // autocorrelator will almost always be the max
                o.corrmax = AudioLinkLerp(ALPASS_AUTOCORRELATOR) * 0.2 + .6;

                o.vpXform = vPos;
                o.vertex = UnityObjectToClipPos(vPos);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // Decide how we want to color from colorchord.
                float ccplace = length(i.vpXform.xz) * 2. / i.corrmax;

                // Get a color from ColorChord
                float4 colorchordcolor = AudioLinkData(ALPASS_CCSTRIP +
                    float2( AUDIOLINK_WIDTH * ccplace, 0. )) + 0.01;

                // Shade the color a little.
                colorchordcolor *= length(i.vpXform.xyz) * 15. - 2.0;
                return colorchordcolor;
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

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"
            #include "Packages/com.llealloo.audiolink/Runtime/Shaders/AudioLink.cginc"

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

                float3 vp = v.vertex;
                vp *= AudioLinkGetSphericalMappedAutoCorrelatorValue(v.vertex);
                o.vertex = UnityObjectToClipPos(vp);
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

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"
            #include "Packages/com.llealloo.audiolink/Runtime/Shaders/AudioLink.cginc"

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

                float3 vp = v.vertex;

                float autocorrvalue = AudioLinkGetSphericalMappedAutoCorrelatorValue(vp);

                // Apply to vertex position
                vp *= autocorrvalue;

                // For a proper normal calculation, we need to adjust the normal based on the deformation
                // This is a simplified approach - for a more accurate normal, you'd use a derivative-based approach
                float3 normal = UnityObjectToWorldNormal(v.normal);

                // Scale normal inverse to how the vertex was scaled to approximate the surface normal change
                normal /= autocorrvalue;
                normal = normalize(normal);

                o.normal = normal;
                o.vertex = UnityObjectToClipPos(vp);
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float3 normalWS = normalize(i.normal);
                float3 normalEncoded = normalWS * 0.5 + 0.5;
                return float4(normalEncoded, 1.0);
            }
            ENDCG
        }
    }
}
