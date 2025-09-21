/*
 * Copyright (c) Meta Platforms, Inc. and affiliates.
 * All rights reserved.
 *
 * Licensed under the Oculus SDK License Agreement (the "License");
 * you may not use the Oculus SDK except in compliance with the License,
 * which is provided at the time of installation or download, or which
 * otherwise accompanies this software in either electronic or hard copy form.
 *
 * You may obtain a copy of the License at
 *
 * https://developer.oculus.com/licenses/oculussdk/
 *
 * Unless required by applicable law or agreed to in writing, the Oculus SDK
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

Shader "Meta/Lit"
{
    Properties
    {
        [MainTexture] _BaseMap("Albedo", 2D) = "white" {}
        [MainColor] _BaseColor("Color", Color) = (1,1,1,1)

        _Metallic("Metallic", Range(0.0, 1.0)) = 0.0
        _MetallicGlossMap("Metallic Map", 2D) = "white" {}
        _Smoothness("Smoothness", Range(0.0, 1.0)) = 0.5

        [HideInInspector][ToggleUI] _SpecularHighlights("Specular Highlights", Float) = 1.0
        [ToggleUI] _EnvironmentReflections("Environment Reflections", Float) = 1.0

        [HideInInspector] _BumpScale("Scale", Float) = 1.0
        _BumpMap("Normal Map", 2D) = "bump" {}

        _OcclusionStrength("Strength", Range(0.0, 1.0)) = 1.0
        _OcclusionMap("Occlusion", 2D) = "white" {}

        [HDR] _EmissionColor("Color", Color) = (0,0,0)
        _EmissionMap("Emission", 2D) = "white" {}

        _Surface("__surface", Float) = 0.0 // opaque | transparent
        _Blend("__blend", Float) = 0.0
        _Cull("__cull", Float) = 2.0 // both | back | front
        _ZWrite("__zw", Float) = 1.0 // auto | force enabled | force disabled
        _QueueOffset("Queue offset", Float) = 0.0

        // unsupported but needed for URP passes
        [HideInInspector][ToggleUI] _AlphaClip("__clip", Float) = 0.0 // toggle
        [HideInInspector] _SrcBlend("__src", Float) = 1.0
        [HideInInspector] _DstBlend("__dst", Float) = 0.0
        [HideInInspector] _SrcBlendAlpha("__srcA", Float) = 1.0
        [HideInInspector] _DstBlendAlpha("__dstA", Float) = 0.0
        [HideInInspector] _BlendModePreserveSpecular("_BlendModePreserveSpecular", Float) = 1.0
        [HideInInspector] _AlphaToMask("__alphaToMask", Float) = 0.0
        [HideInInspector] [ToggleUI] _ReceiveShadows("Receive Shadows", Float) = 1.0

        // no support for: alpha clipping, specular workflow, albedo-alpha-for-smoothness
        //   parallax, detail masks, blending, deferred rendering
    }

    SubShader
    {
        PackageRequirements { "com.unity.render-pipelines.universal" }
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }

        UsePass "Universal Render Pipeline/Lit/ForwardLit"
        UsePass "Universal Render Pipeline/Lit/ShadowCaster"
        UsePass "Universal Render Pipeline/Lit/DepthOnly"
        UsePass "Universal Render Pipeline/Lit/DepthNormals"
        UsePass "Universal Render Pipeline/Lit/Meta"
    }

    SubShader
    {
        Tags { "Queue"="Geometry" "RenderType"="Opaque" }
        LOD 200
        ZWrite[_ZWrite]
        Cull [_Cull]

        CGPROGRAM

        // physically based Standard lighting model,
        // enable shadows on all light types
        #pragma surface surf Standard fullforwardshadows addshadow

        // shader model 3.0 target for nicer lighting
        #pragma target 3.0

        // the features we can toggle
        #pragma shader_feature _GLOSSYREFLECTIONS_OFF
        #pragma shader_feature _METALLICSPECGLOSSMAP
        #pragma shader_feature _OCCLUSIONMAP
        #pragma shader_feature _NORMALMAP
        #pragma shader_feature _EMISSION

        // accessing our properties
        sampler2D _BaseMap;
        sampler2D _BumpMap;
        sampler2D _OcclusionMap;
        sampler2D _EmissionMap;
        sampler2D _MetallicGlossMap;
        half _Smoothness;
        half _Metallic;
        fixed4 _BaseColor;
        fixed4 _EmissionColor;
        half _OcclusionStrength;

        struct Input
        {
            float2 uv_BaseMap;
        };

        void surf (Input IN, inout SurfaceOutputStandard o)
        {
            fixed4 c = tex2D (_BaseMap, IN.uv_BaseMap) * _BaseColor;
            o.Albedo = c.rgb;

            half metallic = _Metallic;
            half smoothness = _Smoothness;
            #if _METALLICSPECGLOSSMAP
            fixed4 metallicGloss = tex2D(_MetallicGlossMap, IN.uv_BaseMap);
            metallic = metallicGloss.r;
            smoothness *= metallicGloss.a;
            #endif
            o.Metallic = metallic;
            o.Smoothness = smoothness;

            #if _NORMALMAP
            fixed4 normal = tex2D(_BumpMap, IN.uv_BaseMap);
            o.Normal = UnpackNormal(normal);
            #endif

            #if _EMISSION
            o.Emission = tex2D(_EmissionMap, IN.uv_BaseMap) * _EmissionColor;
            #endif

            #if _OCCLUSIONMAP
            half ao = tex2D(_OcclusionMap, IN.uv_BaseMap).r;
            o.Occlusion = lerp(1.0, ao, _OcclusionStrength);
            #endif
        }
        ENDCG
    }

    CustomEditor "Oculus.ShaderGUI.MetaLit"
}
