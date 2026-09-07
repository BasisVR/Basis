using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

namespace Basis.Scripts.Editor
{
    /// <summary>
    /// The performance bar material is created at runtime through Shader.Find, which in a
    /// player only resolves shaders the build kept. Nothing references the bar shader from
    /// a scene, prefab or material, so put it in Always Included Shaders.
    /// </summary>
    public sealed class BasisPerformanceBarShaderInclusion : IPreprocessBuildWithReport
    {
        public const string BarShaderName = "Basis/UI/PerformanceBar";

        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report) => EnsureIncluded();

        [MenuItem("Basis/Build/Shaders/Include Performance Bar Shader", false, 363)]
        private static void EnsureIncluded()
        {
            Shader shader = Shader.Find(BarShaderName);
            if (shader == null)
            {
                Debug.LogError($"[BasisPerformanceBar] Shader '{BarShaderName}' not found — the performance bar will not render in builds.");
                return;
            }

            SerializedObject graphics = new SerializedObject(GraphicsSettings.GetGraphicsSettings());
            SerializedProperty included = graphics.FindProperty("m_AlwaysIncludedShaders");
            if (included == null)
            {
                Debug.LogError($"[BasisPerformanceBar] Could not reach Always Included Shaders — add '{BarShaderName}' by hand under Project Settings > Graphics.");
                return;
            }

            for (int Index = 0; Index < included.arraySize; Index++)
            {
                if (included.GetArrayElementAtIndex(Index).objectReferenceValue == shader)
                {
                    return;
                }
            }

            included.InsertArrayElementAtIndex(included.arraySize);
            included.GetArrayElementAtIndex(included.arraySize - 1).objectReferenceValue = shader;
            graphics.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();
            Debug.Log($"[BasisPerformanceBar] Added '{BarShaderName}' to Always Included Shaders.");
        }
    }
}
