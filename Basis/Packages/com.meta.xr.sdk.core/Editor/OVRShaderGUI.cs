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

using UnityEngine;
using UnityEditor;

namespace Oculus.ShaderGUI
{
    internal class MetaLit : OVRShaderGUI
    {
        protected override bool IsOpaque() => true;
    }

    internal class MetaLitTransparent : OVRShaderGUI
    {
        protected override bool IsOpaque() => false;
    }

    internal abstract class OVRShaderGUI : UnityEditor.ShaderGUI
    {
        protected abstract bool IsOpaque();

        public override void OnGUI(MaterialEditor editor, MaterialProperty[] props)
        {
            var material = editor.target as Material;
            if (material == null)
                return;

            // culling
            var cullProp = FindProp(Property.Cull, props);
            PopupShaderProperty(editor, cullProp, Label.Culling, Property.CullNames);

            if (!IsOpaque())
            {
                // blending
                var blendingProp = FindProp(Property.Blend, props);
                PopupShaderProperty(editor, blendingProp, Label.Blending, Property.BlendNames);

                // depth write
                var zProp = FindProp(Property.ZWrite, props);
                PopupShaderProperty(editor, zProp, Label.ZWrite, Property.ZNames);
            }

            // Surface Input section
            EditorGUILayout.Separator();
            EditorGUILayout.LabelField("Surface Inputs", EditorStyles.boldLabel);

            // base color
            var rgbProp = FindProp(Property.BaseColor, props);
            var rgbTextureProp = FindProp(Property.BaseMap, props);
            editor.TexturePropertySingleLine(Label.BaseMap, rgbTextureProp,
                rgbProp);

            // metallic/smoothness
            var metallicTextureProp = FindProp(Property.MetallicGlossMap, props);
            var metallicProp = FindProp(Property.Metallic, props);
            var metallicLabel = Label.MetallicMap;
            var smoothnessProp = FindProp(Property.Smoothness, props);

            var hasGlossMap = metallicTextureProp.textureValue != null;
            editor.TexturePropertySingleLine(metallicLabel, metallicTextureProp,
                hasGlossMap ? null : metallicProp);

            using (new EditorGUI.IndentLevelScope(2))
                editor.ShaderProperty(smoothnessProp, Label.Smoothness);

            // normals
            var bumpMap = FindProp(Property.BumpMap, props);
            editor.TexturePropertySingleLine(Label.NormalMap, bumpMap);

            // occlusion
            var occlusionMap = FindProp(Property.OcclusionMap, props);
            var occlusionStrength = FindProp(Property.OcclusionStrength, props);
            editor.TexturePropertySingleLine(Label.OcclusionMap, occlusionMap,
                occlusionMap.textureValue != null ? occlusionStrength : null);

            // texture tiling
            var tileOffset = FindProp(Property.BaseMap, props);
            editor.TextureScaleOffsetProperty(tileOffset);

            // emission
            var emissionColor = FindProp(Property.EmissionColor, props);
            var emissionMap = FindProp(Property.EmissionMap, props);
            var emission = editor.EmissionEnabledProperty();
            using (new EditorGUI.DisabledScope(!emission))
            {
                using (new EditorGUI.IndentLevelScope(2))
                {
                    editor.TexturePropertyWithHDRColor(Label.Emission, emissionMap, emissionColor, false);
                }
            }

            // if texture is assigned and color was black, set to white
            var hadEmissionTexture = emissionMap.textureValue != null;
            var brightness = emissionColor.colorValue.maxColorComponent;
            if (emissionMap.textureValue != null && !hadEmissionTexture && brightness <= 0f)
                emissionColor.colorValue = Color.white;

            // Advanced Options section
            EditorGUILayout.Separator();
            EditorGUILayout.LabelField("Advanced Options", EditorStyles.boldLabel);

            // reflections
            var reflectionsProp = FindProp(Property.EnvironmentReflections, props);
            var reflectionsOff = reflectionsProp.floatValue == 0.0f;

            EditorGUI.BeginChangeCheck();
            var reflectionValue = EditorGUILayout.Toggle(Label.Reflections, !reflectionsOff);
            if (EditorGUI.EndChangeCheck())
                reflectionsProp.floatValue = reflectionValue ? 1.0f : 0.0f;

            // queue offset
            var queueProp = FindProp(Property.QueueOffset, props);
            var queueLabel = Label.Queue;
            var (min, max) = (-50, 50);

            var rect = EditorGUILayout.GetControlRect(true, MaterialEditor.GetDefaultPropertyHeight(queueProp),
                EditorStyles.layerMaskField);

            EditorGUI.BeginChangeCheck();
            EditorGUI.showMixedValue = queueProp.hasMixedValue;
            var newQueueValue = EditorGUI.IntSlider(rect, queueLabel, (int)queueProp.floatValue, min, max);
            EditorGUI.showMixedValue = false;
            if (EditorGUI.EndChangeCheck())
            {
                editor.RegisterPropertyChangeUndo(queueLabel.text);
                queueProp.floatValue = newQueueValue;
            }

            // material instancing
            editor.EnableInstancingField();
        }

        public override void ValidateMaterial(Material material)
        {
            // no support for parallax
            SetKeyword(material, Keyword.ParallaxMap, false);

            // env for urp, glossy for birp
            var reflectionsOff = material.GetFloat(Property.EnvironmentReflections) == 0.0f;
            SetKeyword(material, Keyword.EnvironmentReflectionsOff, reflectionsOff);
            SetKeyword(material, Keyword.GlossyReflectionsOff, reflectionsOff);

            // bump maps
            SetKeyword(material, Keyword.NormalMap, material.GetTexture(Property.BumpMap));

            // metallic and smoothness
            SetKeyword(material, Keyword.MetallicSpecGlossMap, material.GetTexture(Property.MetallicGlossMap));
            SetKeyword(material, Keyword.SmoothnessTextureAlbedoChannelA, false);

            // occlusion
            SetKeyword(material, Keyword.OcclusionMap, material.GetTexture(Property.OcclusionMap));

            // emission
            if (material.HasProperty(Property.EmissionColor))
                MaterialEditor.FixupEmissiveFlag(material);
            var shouldEmissionBeEnabled =
                (material.globalIlluminationFlags & MaterialGlobalIlluminationFlags.EmissiveIsBlack) == 0;
            SetKeyword(material, Keyword.Emission, shouldEmissionBeEnabled);

            // double sided gi - cull mode != front/2f
            material.doubleSidedGI = (int)material.GetFloat(Property.Cull) != 2;

            // alpha clipping
            SetKeyword(material, Keyword.AlphaTestOn, false);

            if (IsOpaque())
            {
                // render queue
                const int opaqueRenderQueue = 2000;
                material.renderQueue = opaqueRenderQueue + (int)material.GetFloat(Property.QueueOffset);
            }
            else
            {
                // render queue
                const int transparentRenderQueue = 3000;
                material.renderQueue = transparentRenderQueue + (int)material.GetFloat(Property.QueueOffset);

                // blending
                SetKeyword(material, Keyword.AlphaPremultiplyOn, false);
                SetKeyword(material, Keyword.AlphaModulateOn, false);
                SetKeyword(material, Keyword.SurfaceTypeTransparent, true);
                switch ((int)material.GetFloat(Property.Blend))
                {
                    case 0: // Alpha
                        material.SetFloat(Property.SrcBlend, 5.0f);
                        material.SetFloat(Property.DstBlend, 10.0f);
                        material.SetFloat(Property.SrcBlendAlpha, 1.0f);
                        material.SetFloat(Property.DstBlendAlpha, 10.0f);
                        break;
                    case 1: // Premultiply
                        material.SetFloat(Property.SrcBlend, 1.0f);
                        material.SetFloat(Property.DstBlend, 10.0f);
                        material.SetFloat(Property.SrcBlendAlpha, 1.0f);
                        material.SetFloat(Property.DstBlendAlpha, 10.0f);
                        SetKeyword(material, Keyword.AlphaPremultiplyOn, true);
                        break;
                    case 2: // Additive
                        material.SetFloat(Property.SrcBlend, 5.0f);
                        material.SetFloat(Property.DstBlend, 1.0f);
                        material.SetFloat(Property.SrcBlendAlpha, 1.0f);
                        material.SetFloat(Property.DstBlendAlpha, 1.0f);
                        break;
                    case 3: // Multiply
                        material.SetFloat(Property.SrcBlend, 2.0f);
                        material.SetFloat(Property.DstBlend, 0.0f);
                        material.SetFloat(Property.SrcBlendAlpha, 0.0f);
                        material.SetFloat(Property.DstBlendAlpha, 1.0f);
                        SetKeyword(material, Keyword.AlphaModulateOn, true);
                        break;
                }
            }
        }

        public override void AssignNewShaderToMaterial(Material material, Shader oldShader, Shader newShader)
        {
            base.AssignNewShaderToMaterial(material, oldShader, newShader);

            // we need to do a reset on these properties, in case the previous
            // material was using the same properties, even if we've hardcoded them
            material.SetFloat(Property.SpecularHighlights, 1.0f);
            material.SetFloat(Property.BumpScale, 1.0f);
            material.SetFloat(Property.AlphaClip, 0.0f);
            material.SetFloat(Property.AlphaToMask, 0.0f);

            if (IsOpaque())
            {
                // reset the blend and transparency modes
                material.SetFloat(Property.Surface, 0.0f);
                material.SetFloat(Property.Blend, 0.0f);
                material.SetFloat(Property.SrcBlend, 1.0f);
                material.SetFloat(Property.DstBlend, 0.0f);
            }
            else
            {
                material.SetFloat(Property.Surface, 1.0f);
            }
        }

        private static MaterialProperty FindProp(string name, MaterialProperty[] props)
        {
            for (int index = 0; index < props.Length; ++index)
                if (props[index]?.name == name)
                    return props[index];
            Debug.Assert(false);
            return null;
        }

        private static int PopupShaderProperty(MaterialEditor editor, MaterialProperty prop, GUIContent label,
            string[] displayedOptions)
        {
            var val = (int)prop.floatValue;

            EditorGUI.BeginChangeCheck();
            EditorGUI.showMixedValue = prop.hasMixedValue;
            var newValue = EditorGUILayout.Popup(label, val, displayedOptions);
            EditorGUI.showMixedValue = false;
            if (EditorGUI.EndChangeCheck() && (newValue != val || prop.hasMixedValue))
            {
                editor.RegisterPropertyChangeUndo(label.text);
                prop.floatValue = val = newValue;
            }

            return val;
        }

        private static void SetKeyword(Material material, string keyword, bool enable)
        {
            if (enable)
                material.EnableKeyword(keyword);
            else
                material.DisableKeyword(keyword);
        }

        private static class Keyword
        {
            public const string AlphaModulateOn = "_ALPHAMODULATE_ON";
            public const string AlphaPremultiplyOn = "_ALPHAPREMULTIPLY_ON";
            public const string AlphaTestOn = "_ALPHATEST_ON";
            public const string Emission = "_EMISSION";
            public const string EnvironmentReflectionsOff = "_ENVIRONMENTREFLECTIONS_OFF";
            public const string GlossyReflectionsOff = "_GLOSSYREFLECTIONS_OFF";
            public const string MetallicSpecGlossMap = "_METALLICSPECGLOSSMAP";
            public const string NormalMap = "_NORMALMAP";
            public const string OcclusionMap = "_OCCLUSIONMAP";
            public const string ParallaxMap = "_PARALLAXMAP";
            public const string SmoothnessTextureAlbedoChannelA = "_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A";
            public const string SurfaceTypeTransparent = "_SURFACE_TYPE_TRANSPARENT";
        }

        private static class Property
        {
            public const string AlphaClip = "_AlphaClip";
            public const string AlphaToMask = "_AlphaToMask";
            public const string BaseColor = "_BaseColor";
            public const string BaseMap = "_BaseMap";
            public const string Blend = "_Blend";
            public const string BumpMap = "_BumpMap";
            public const string BumpScale = "_BumpScale";
            public const string Cull = "_Cull";
            public const string DstBlend = "_DstBlend";
            public const string DstBlendAlpha = "_DstBlendAlpha";
            public const string EmissionColor = "_EmissionColor";
            public const string EmissionMap = "_EmissionMap";
            public const string EnvironmentReflections = "_EnvironmentReflections";
            public const string MetallicGlossMap = "_MetallicGlossMap";
            public const string Metallic = "_Metallic";
            public const string OcclusionMap = "_OcclusionMap";
            public const string OcclusionStrength = "_OcclusionStrength";
            public const string QueueOffset = "_QueueOffset";
            public const string Smoothness = "_Smoothness";
            public const string SpecularHighlights = "_SpecularHighlights";
            public const string SrcBlend = "_SrcBlend";
            public const string SrcBlendAlpha = "_SrcBlendAlpha";
            public const string Surface = "_Surface";
            public const string ZWrite = "_ZWrite";

            public static readonly string[] CullNames = { "Both", "Back", "Front" };
            public static readonly string[] BlendNames = { "Alpha", "Premultiply", "Additive", "Multiply" };
            public static readonly string[] ZNames = { "Off", "On" };
        }

        private static class Label
        {
            public static readonly GUIContent BaseMap = EditorGUIUtility.TrTextContent("Base Map",
                "Specifies the base Material and/or Color of the surface. If you’ve selected Transparent or Alpha Clipping under Surface Options, your Material uses the Texture’s alpha channel or color.");

            public static readonly GUIContent MetallicMap = EditorGUIUtility.TrTextContent("Metallic Map",
                "Sets and configures the map for the Metallic workflow.");

            public static readonly GUIContent Smoothness = EditorGUIUtility.TrTextContent("Smoothness",
                "Controls the spread of highlights and reflections on the surface.");

            public static readonly GUIContent OcclusionMap = EditorGUIUtility.TrTextContent("Occlusion Map",
                "Sets an occlusion map to simulate shadowing from ambient lighting.");

            public static readonly GUIContent NormalMap = EditorGUIUtility.TrTextContent("Normal Map",
                "Designates a Normal Map to create the illusion of bumps and dents on this Material's surface.");

            public static readonly GUIContent Culling = EditorGUIUtility.TrTextContent("Render Face",
                "Specifies which faces to cull from your geometry. Front culls front faces. Back culls backfaces. None means that both sides are rendered.");

            public static readonly GUIContent Reflections = EditorGUIUtility.TrTextContent("Environment Reflections",
                "When enabled, the Material samples reflections from the nearest Reflection Probes or Lighting Probe.");

            public static readonly GUIContent Queue = EditorGUIUtility.TrTextContent("Sorting Priority",
                "Determines the chronological rendering order for a Material. Materials with lower value are rendered first.");

            public static readonly GUIContent Emission = EditorGUIUtility.TrTextContent("Emission Map",
                "Determines the color and intensity of light that the surface of the material emits.");

            public static readonly GUIContent Blending = EditorGUIUtility.TrTextContent("Blending Mode",
                "Controls how the color of the Transparent surface blends with the Material color in the background.");

            public static readonly GUIContent ZWrite = EditorGUIUtility.TrTextContent("Depth Write",
                "Controls whether the shader writes depth.  Auto will write only when the shader is opaque.");
        }
    }
}
