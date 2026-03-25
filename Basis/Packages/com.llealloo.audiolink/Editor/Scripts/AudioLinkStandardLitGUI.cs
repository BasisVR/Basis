using UnityEngine;
using UnityEditor;
using System;
using System.Linq;
using System.Reflection;
using UnityEngine.Rendering;

namespace AudioLink.Editor.Shaders
{
// Create a custom shader GUI that switches between Standard and URP Lit inspector
    public class AudioLinkStandardLitGUI : ShaderGUI
    {
        // References to keep track of our reflected editor instances
        private ShaderGUI urpLitGUI = null;
        private ShaderGUI standardGUI = null;
        private bool guiCheckComplete;

        private FieldInfo standard_MaterialEditor;
        private FieldInfo standard_WorkflowMode;
        private MethodInfo standard_FindProperties;
        private MethodInfo standard_ShaderPropertiesGUI;

        private void EnsureShaderGUIAvailable()
        {
            if (guiCheckComplete) return;
            guiCheckComplete = true;
            // Check if the project is using URP
            bool isURP = GraphicsSettings.currentRenderPipeline != null &&
                         GraphicsSettings.currentRenderPipeline.GetType().ToString().Contains("Universal");

            if (isURP)
            {
                if (urpLitGUI != null) return;
                // Try to create URP Lit GUI instance via reflection
                try
                {
                    // Get the URP Editor assembly
                    Assembly urpEditorAssembly = null;
                    Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                    foreach (var assembly in assemblies)
                    {
                        if (assembly.GetName().Name == "Unity.RenderPipelines.Universal.Editor")
                        {
                            urpEditorAssembly = assembly;
                            break;
                        }
                    }

                    if (urpEditorAssembly != null)
                    {
                        // Get the LitShaderGUI type
                        Type litGUIType = urpEditorAssembly.GetType("UnityEditor.Rendering.Universal.ShaderGUI.LitShader");
                        if (litGUIType != null) urpLitGUI = Activator.CreateInstance(litGUIType) as ShaderGUI;
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError("Failed to create URP Lit GUI: " + e.Message);
                }
            }
            else
            {
                try
                {
                    // Get the Standard ShaderGUI type from UnityEditor assembly
                    Assembly editorAssembly = typeof(EditorGUILayout).Assembly;
                    Type standardGUIType = editorAssembly.GetType("UnityEditor.StandardShaderGUI");
                    if (standardGUIType != null) standardGUI = Activator.CreateInstance(standardGUIType) as ShaderGUI;
                    if (standardGUI != null)
                    {
                        var t = standardGUI.GetType();
                        standard_MaterialEditor = t.GetField("m_MaterialEditor", (BindingFlags)~0);
                        standard_WorkflowMode = t.GetField("m_WorkflowMode", (BindingFlags)~0);
                        standard_FindProperties = t.GetMethod("FindProperties", (BindingFlags)~0);
                        standard_ShaderPropertiesGUI = t.GetMethod("ShaderPropertiesGUI", (BindingFlags)~0);
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError("Failed to create Standard GUI: " + e.Message);
                }
            }
        }

        // material changed check
        public override void ValidateMaterial(Material material)
        {
            EnsureShaderGUIAvailable();
            urpLitGUI?.ValidateMaterial(material);
            standardGUI?.ValidateMaterial(material);
        }

        // shader change check
        public override void AssignNewShaderToMaterial(Material material, Shader oldShader, Shader newShader)
        {
            EnsureShaderGUIAvailable();
            urpLitGUI?.AssignNewShaderToMaterial(material, oldShader, newShader);
            standardGUI?.AssignNewShaderToMaterial(material, oldShader, newShader);
        }

        public override void OnGUI(MaterialEditor materialEditor, MaterialProperty[] properties)
        {
            EnsureShaderGUIAvailable();
            urpLitGUI?.OnGUI(materialEditor, properties);
            if (standardGUI != null)
            {
                // since the shader includes specGlossMap as a property for the URP lit mode
                // we need to reflect the standard OnGUI call contents in order to force the workflow mode to metallic
                // so the correct inspector fields show up.
                standard_FindProperties.Invoke(standardGUI, new[] { properties });
                standard_WorkflowMode.SetValue(standardGUI, 1); // force metallic mode
                standard_MaterialEditor.SetValue(standardGUI, materialEditor);
                Material material = materialEditor.target as Material;
                standard_ShaderPropertiesGUI.Invoke(standardGUI, new[] { material });
            }
        }
    }
}
