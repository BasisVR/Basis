Shader "AudioLink/Internal/AudioLinkController"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
        _MainTex ("Albedo (RGB)", 2D) = "white" {}
        _Metallic ("Metallic", 2D) = "white" {}
    }

    SubShader
    {
        Tags
        {
            "RenderType"="Opaque"
        }

        // Forward Base Pass
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fwdbase

            #include "UnityCG.cginc"
            #include "Lighting.cginc"
            #include "AutoLight.cginc"
            #include "UnityPBSLighting.cginc"

            sampler2D _MainTex;
            sampler2D _Metallic;
            float4 _Color;
            float4 _MainTex_ST;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                float3 normal : NORMAL;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 worldNormal : TEXCOORD1;
                float3 worldPos : TEXCOORD2;
                SHADOW_COORDS(3)
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                TRANSFER_SHADOW(o);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // Sample textures
                float4 c = tex2D(_MainTex, i.uv) * _Color;
                float4 b = tex2D(_Metallic, i.uv);

                // Extract PBR properties
                float3 albedo = c.rgb;
                float metallic = b.r;
                float smoothness = b.a;
                float occlusion = b.g;

                // Calculate vectors
                float3 worldNormal = normalize(i.worldNormal);
                float3 worldViewDir = normalize(UnityWorldSpaceViewDir(i.worldPos));
                float3 worldLightDir = normalize(UnityWorldSpaceLightDir(i.worldPos));

                // Create surface data structure
                SurfaceOutputStandard surf = (SurfaceOutputStandard)0;
                surf.Albedo = albedo;
                surf.Normal = worldNormal;
                surf.Metallic = metallic;
                surf.Smoothness = smoothness;
                surf.Occlusion = occlusion;
                surf.Alpha = c.a;

                // Setup UnityGI structure
                UnityGI gi = (UnityGI)0;
                gi.light.color = _LightColor0.rgb;
                gi.light.dir = worldLightDir;

                // Apply shadow attenuation
                float shadow = SHADOW_ATTENUATION(i);
                gi.light.color *= shadow;

                // Setup indirect lighting
                gi.indirect.diffuse = ShadeSH9(float4(worldNormal, 1));
                gi.indirect.specular = 0;

                // Use Unity's Standard lighting function
                float4 result = LightingStandard(surf, worldViewDir, gi);

                return result;
            }
            ENDCG
        }

        // Forward Add Pass
        Pass
        {
            Tags
            {
                "LightMode"="ForwardAdd"
            }
            Blend One One
            ZWrite Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_fwdadd_fullshadows

            #include "UnityCG.cginc"
            #include "Lighting.cginc"
            #include "AutoLight.cginc"

            sampler2D _MainTex;
            sampler2D _Metallic;
            float4 _Color;
            float4 _MainTex_ST;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                float3 normal : NORMAL;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 worldNormal : TEXCOORD1;
                float3 worldPos : TEXCOORD2;
                SHADOW_COORDS(3)
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                TRANSFER_SHADOW(o);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float4 c = tex2D(_MainTex, i.uv) * _Color;
                float4 b = tex2D(_Metallic, i.uv);

                float3 albedo = c.rgb;
                float occlusion = b.g;

                float3 worldNormal = normalize(i.worldNormal);
                float3 worldLightDir = normalize(UnityWorldSpaceLightDir(i.worldPos));

                float NdotL = max(0, dot(worldNormal, worldLightDir));
                float3 diffuse = albedo * _LightColor0.rgb * NdotL;

                float shadow = SHADOW_ATTENUATION(i);
                diffuse *= occlusion * shadow;

                return float4(diffuse, 0);
            }
            ENDCG
        }

        // ShadowCaster Pass
        Pass
        {
            Tags
            {
                "LightMode"="ShadowCaster"
            }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_shadowcaster

            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _Color;
            float4 _MainTex_ST;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                V2F_SHADOW_CASTER;
                float2 uv : TEXCOORD1;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                TRANSFER_SHADOW_CASTER_NORMALOFFSET(o)
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                // Alpha test for transparent parts (if needed)
                float4 c = tex2D(_MainTex, i.uv) * _Color;
                clip(c.a - 0.001);

                SHADOW_CASTER_FRAGMENT(i)
            }
            ENDCG
        }

        // DepthOnly Pass
        Pass
        {
            Tags
            {
                "LightMode"="DepthOnly"
            }

            ZWrite On
            ColorMask 0

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _Color;
            float4 _MainTex_ST;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                // Alpha test for transparent parts (if needed)
                float4 c = tex2D(_MainTex, i.uv) * _Color;
                clip(c.a - 0.001);

                return 0;
            }
            ENDCG
        }

        // DepthNormals Pass
        Pass
        {
            Tags
            {
                "LightMode"="DepthNormals"
            }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_prepassfinal

            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _Color;
            float4 _MainTex_ST;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 nz : TEXCOORD0;
                float2 uv : TEXCOORD1;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.nz.xyz = COMPUTE_VIEW_NORMAL;
                o.nz.w = COMPUTE_DEPTH_01;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // Alpha test for transparent parts (if needed)
                float4 c = tex2D(_MainTex, i.uv) * _Color;
                clip(c.a - 0.001);

                return EncodeDepthNormal(i.nz.w, i.nz.xyz);
            }
            ENDCG
        }
    }
    FallBack "Diffuse"
}
