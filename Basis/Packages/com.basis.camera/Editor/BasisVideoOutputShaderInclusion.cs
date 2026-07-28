using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

// Not Basis.Camera.* — a namespace segment named Camera shadows UnityEngine.Camera.
namespace Basis.HandHeldCamera.Editor
{
    /// <summary>
    /// The video output sinks build their Spout/Syphon resources at runtime through
    /// Shader.Find, which in a player only resolves shaders the build kept. Neither blit
    /// shader is referenced by a scene, prefab or material, so nothing else drags them in
    /// and the sink refuses to start in a build. Put them in Always Included Shaders.
    /// </summary>
    public sealed class BasisVideoOutputShaderInclusion : IPreprocessBuildWithReport
    {
        private static readonly string[] RequiredShaders =
        {
            "Hidden/Klak/Spout/Blit",
            "Hidden/Klak/Syphon/Blit",
        };

        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report) => EnsureIncluded();

        [MenuItem("Basis/Camera/Include Video Output Shaders")]
        private static void EnsureIncluded()
        {
            SerializedObject graphics = new SerializedObject(GraphicsSettings.GetGraphicsSettings());
            SerializedProperty included = graphics.FindProperty("m_AlwaysIncludedShaders");
            if (included == null)
            {
                Debug.LogError("[BasisCamera] Could not reach Always Included Shaders — add the Spout/Syphon blit shaders by hand under Project Settings > Graphics.");
                return;
            }

            bool changed = false;
            for (int Index = 0; Index < RequiredShaders.Length; Index++)
            {
                // Missing simply means that sink's package isn't installed.
                Shader shader = Shader.Find(RequiredShaders[Index]);
                if (shader == null || Contains(included, shader)) continue;

                included.InsertArrayElementAtIndex(included.arraySize);
                included.GetArrayElementAtIndex(included.arraySize - 1).objectReferenceValue = shader;
                changed = true;
                Debug.Log($"[BasisCamera] Added '{RequiredShaders[Index]}' to Always Included Shaders so live video output survives the build.");
            }

            if (!changed) return;
            graphics.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();
        }

        private static bool Contains(SerializedProperty array, Shader shader)
        {
            for (int Index = 0; Index < array.arraySize; Index++)
            {
                if (array.GetArrayElementAtIndex(Index).objectReferenceValue == shader) return true;
            }
            return false;
        }
    }
}
