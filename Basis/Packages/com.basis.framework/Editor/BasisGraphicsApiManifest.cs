using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

namespace Basis.Scripts.Rendering
{
    /// <summary>
    /// Bakes the graphics APIs a build ships into Resources, because nothing reads that list back
    /// at runtime and BasisGraphicsApiSelection needs it to answer the only question the Renderer
    /// section rests on: is there more than one renderer to choose between. Without it the runtime
    /// has to hardcode the list, which goes stale the first time anyone edits Player Settings and
    /// goes stale silently — a single-API build would go on offering a second renderer it has no
    /// compiled shaders for.
    ///
    /// Written before the build and removed after it, so the generated file never lands in a commit.
    /// A build that fails partway can leave it behind; it is one line of text and the next build
    /// overwrites it.
    /// </summary>
    public sealed class BasisGraphicsApiManifest : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        private const string ResourcesFolder = "Assets/Resources";
        private static string AssetPath => $"{ResourcesFolder}/{BasisGraphicsApiSelection.ManifestResourcePath}.txt";

        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            BuildTarget target = report.summary.platform;

            // Auto Graphics API hands the choice to Unity's own platform list at startup, so there
            // is no shipped set for a player to pick within. An empty manifest says exactly that.
            GraphicsDeviceType[] apis = PlayerSettings.GetUseDefaultGraphicsAPIs(target)
                ? Array.Empty<GraphicsDeviceType>()
                : PlayerSettings.GetGraphicsAPIs(target) ?? Array.Empty<GraphicsDeviceType>();

            StringBuilder builder = new StringBuilder();
            for (int Index = 0; Index < apis.Length; Index++)
            {
                builder.Append(apis[Index]).Append('\n');
            }

            try
            {
                Directory.CreateDirectory(ResourcesFolder);
                File.WriteAllText(AssetPath, builder.ToString());
                AssetDatabase.ImportAsset(AssetPath, ImportAssetOptions.ForceSynchronousImport);
            }
            catch (Exception exception)
            {
                // Not worth failing a build over: the player falls back to offering no choice,
                // which is the same thing every build before this manifest existed did.
                Debug.LogWarning($"[BasisGraphicsApiManifest] Could not write {AssetPath}, so the built player will not offer a renderer choice: {exception.Message}");
            }
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            if (!File.Exists(AssetPath))
            {
                return;
            }

            try
            {
                AssetDatabase.DeleteAsset(AssetPath);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[BasisGraphicsApiManifest] Could not remove {AssetPath} after the build: {exception.Message}");
            }
        }
    }
}
