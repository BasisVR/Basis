using UnityEngine;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine.Rendering;
using System.Text.RegularExpressions;

namespace AudioLink.Editor
{
    public class AudioLinkPipelineDetector : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        private static readonly string[] s_shaderPrefixes = {
            "AudioLink/StandardLit",
            "AudioLink/Surface"
        };

        const string AUDIOLINK_PIPELINE_FILE = ".audiolink_pipeline";
        private const string AmplifyAbsoluteDir = "Packages/com.llealloo.audiolink/Runtime/Shaders/Amplify/Shaders";
        const string URP = "_UNIVERSAL_RENDER_PIPELINE";
        const string HDRP = "_HIGH_DEFINITION_RENDER_PIPELINE";

        private const string ConvertMenuItemPath = "Tools/AudioLink/Convert AudioLink Amplify Shaders Pipeline";
        private const string ShaderKeyworkMenuItemPath = "Tools/AudioLink/Apply AudioLink Shader Pipeline Keywords";

        private const string ConvertDialogText = "Do you want to convert the AudioLink Amplify shaders to the ";

        private const string ConvertDialogTitle = "Convert AudioLink Amplify Shaders pipeline?";
        private const string ConvertDialogOkButton = "Convert";
        private const string ConvertDialogCancelButton = "Cancel";

        [InitializeOnLoadMethod]
        private static void InitializeOnLoad()
        {
            EditorApplication.delayCall += CheckHasPipelineChanged;
        }

        public void OnPreprocessBuild(BuildReport report)
        {
            CheckHasPipelineChanged();
        }

        private static void CheckHasPipelineChanged()
        {
            if (HasPipelineChanged())
            {
                AskConvertShaderFiles();
                SetShaderKeywords();
            }
        }

        public static bool HasPipelineChanged()
        {
            bool pipelineChanged = true;
            string currentPipeline = "UnityEngine.Rendering.Builtin.BuiltinRenderPipelineAsset";

            if (GraphicsSettings.currentRenderPipeline != null)
                currentPipeline = GraphicsSettings.currentRenderPipeline.GetType().ToString();

            if (System.IO.File.Exists(AUDIOLINK_PIPELINE_FILE))
            {
                string lastPipeline = System.IO.File.ReadAllText(AUDIOLINK_PIPELINE_FILE).Trim();

                if (lastPipeline == currentPipeline)
                    pipelineChanged = false;
            }

            if (pipelineChanged)
                System.IO.File.WriteAllText(AUDIOLINK_PIPELINE_FILE, currentPipeline);

            return pipelineChanged;
        }

        [MenuItem(ShaderKeyworkMenuItemPath)]
        private static void SetShaderKeywords()
        {
            // ReSharper disable ConditionIsAlwaysTrueOrFalse
            string[] materialGuids = AssetDatabase.FindAssets("t:Material");
            int count = 0;

            foreach (string guid in materialGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                Material material = AssetDatabase.LoadAssetAtPath<Material>(path);

                if (material != null && IsTargetShader(material.shader))
                {
#if URP_AVAILABLE
                    bool shouldURP = true, shouldHDRP = false;
#elif HDRP_AVAILABLE
                    bool shouldURP = false, shouldHDRP = true;
#else
                    bool shouldURP = false, shouldHDRP = false;
#endif
                    // update keywords when mismatch is detected
                    if (material.IsKeywordEnabled(URP) != shouldURP
                        || material.IsKeywordEnabled(HDRP) != shouldHDRP)
                    {
                        if (shouldURP) material.EnableKeyword(URP);
                        else material.DisableKeyword(URP);

                        if (shouldHDRP) material.EnableKeyword(HDRP);
                        else material.DisableKeyword(HDRP);

                        EditorUtility.SetDirty(material);
                        count++;
                    }
                }
            }

            // If any changed, save asset changes and log the count
            if (count > 0)
            {
                AssetDatabase.SaveAssets();
                Debug.Log($"AudioLinkPipelineDetector: Updated render pipeline shader keywords on {count} materials.");
            }
        }

        private static bool IsTargetShader(Shader shader)
        {
            if (shader == null) return false;

            foreach (string targetShader in s_shaderPrefixes)
            {
                if (shader.name.StartsWith(targetShader))
                    return true;
            }

            return false;
        }

        private static string SetLightModeTag(string text, string from, string to)
        {
            return new Regex($"\"LightMode\"\\s*=\\s*\"{from}\"").Replace(text, $"\"LightMode\"=\"{to}\"");
        }

        [MenuItem(ConvertMenuItemPath)]
        private static void AskConvertShaderFiles()
        {
            string renderPipelineName = "BuiltinRenderPipelineAsset";

            if (GraphicsSettings.currentRenderPipeline != null)
            {
                renderPipelineName = GraphicsSettings.currentRenderPipeline.GetType().ToString();
                renderPipelineName = renderPipelineName.Substring(renderPipelineName.LastIndexOf(".") + 1);
            }

            ConvertShaderFiles();
        }

        public static void ConvertShaderFiles()
        {
            ShaderInfo[] shaders = ShaderUtil.GetAllShaderInfo();

            foreach (ShaderInfo shaderinfo in shaders)
            {
                Shader shader = Shader.Find(shaderinfo.name);
                string path = AssetDatabase.GetAssetPath(shader);
                // we want to only affect the Amplify shaders here, so we check the Path prefix
                if (path.StartsWith(AmplifyAbsoluteDir))
                {
#if URP_AVAILABLE || HDRP_AVAILABLE
                    ReplaceLightModeInFile(path, true);
#else
                    ReplaceLightModeInFile(path, false);
#endif
                }
            }

            AssetDatabase.Refresh();
        }

        private static void ReplaceLightModeInFile(string path, bool srpBased)
        {
            string[] shaderSource = System.IO.File.ReadAllLines(path);
            bool firstPass = true;
            bool shouldWrite = false;
            for (int i = 0; i < shaderSource.Length; i++)
            {
                string line = shaderSource[i];

                if (line.Contains("\"LightMode\""))
                {
                    string initalLine = line;
                    if (srpBased) // Unity SRP based, URP, HDRP?
                    {
                        line = SetLightModeTag(line, "ForwardBase", "UniversalForward");
                    } else { // Unity BiRP
                        line = SetLightModeTag(line, "UniversalForward", "ForwardBase");
                    }

                    shaderSource[i] = line;
                    if (line != initalLine)
                        shouldWrite = true;
                }

                if (line.TrimStart().StartsWith("Pass") && firstPass)
                {
                    firstPass = false;
                    bool hasDepthCurrently = line.Contains("\"LightMode\"=\"DepthOnly\"");

                    if (!hasDepthCurrently && srpBased)
                    {
                        shaderSource[i - 1] = "\t\tPass { Tags { \"LightMode\"=\"DepthOnly\"} }";
                        shouldWrite = true;
                    } else if (hasDepthCurrently && !srpBased) {
                        shaderSource[i] = "\t\t";
                        shouldWrite = true;
                    }
                }
            }

            if (shouldWrite)
                System.IO.File.WriteAllLines(path, shaderSource);
        }
    }
}
