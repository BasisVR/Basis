using System;
using System.IO;
using Basis.Scripts.Common;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace Basis.Scripts.Editor
{
    [InitializeOnLoad, NoAutoStaticsCleanup]
    public static class BasisAppConfigEditor
    {
        public const string BuildFolder = "Assets/BasisAppConfigBuild";
        private static readonly NamedBuildTarget[] identifierTargets = { NamedBuildTarget.Standalone, NamedBuildTarget.Android, NamedBuildTarget.iOS };
        private static DateTime lastWrite;
        private static double nextCheck;
        static BasisAppConfigEditor()
        {
            EditorApplication.delayCall += () =>
            {
                RemoveBuildCopy();
                Refresh(true);
            };
            EditorApplication.update -= Poll;
            EditorApplication.update += Poll;
        }
        private static void Poll()
        {
            if (EditorApplication.timeSinceStartup < nextCheck)
            {
                return;
            }
            nextCheck = EditorApplication.timeSinceStartup + 2;
            Refresh(false);
        }
        public static void Refresh(bool force)
        {
            string path = BasisAppConfig.EditorPath;
            DateTime write = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (!force && write == lastWrite)
            {
                return;
            }
            lastWrite = write;
            string text = null;
            try
            {
                text = write == DateTime.MinValue ? null : File.ReadAllText(path);
            }
            catch (IOException)
            {
                lastWrite = DateTime.MinValue;
                return;
            }
            BasisAppConfig.Load(text);
            if (text != null && !EditorApplication.isPlayingOrWillChangePlaymode)
            {
                ApplyIdentity();
            }
        }
        public static void ApplyIdentity()
        {
            string changed = "";
            if (!string.IsNullOrWhiteSpace(BasisAppConfig.ProductName) && PlayerSettings.productName != BasisAppConfig.ProductName.Trim())
            {
                PlayerSettings.productName = BasisAppConfig.ProductName.Trim();
                changed += " productName";
            }
            if (!string.IsNullOrWhiteSpace(BasisAppConfig.CompanyName) && PlayerSettings.companyName != BasisAppConfig.CompanyName.Trim())
            {
                PlayerSettings.companyName = BasisAppConfig.CompanyName.Trim();
                changed += " companyName";
            }
            if (!string.IsNullOrWhiteSpace(BasisAppConfig.Version) && PlayerSettings.bundleVersion != BasisAppConfig.Version.Trim())
            {
                PlayerSettings.bundleVersion = BasisAppConfig.Version.Trim();
                changed += " version";
            }
            string identifier = BasisAppConfig.Identifier?.Trim();
            if (!string.IsNullOrEmpty(identifier))
            {
                if (identifier.IndexOf('.') <= 0 || identifier.EndsWith(".") || identifier.Contains(" "))
                {
                    Debug.LogWarning($"{BasisAppConfig.FileName}: identifier \"{identifier}\" isn't a reverse-domain id such as com.myapp.app; ProjectSettings keeps its own.");
                }
                else
                {
                    foreach (NamedBuildTarget target in identifierTargets)
                    {
                        if (PlayerSettings.GetApplicationIdentifier(target) != identifier)
                        {
                            PlayerSettings.SetApplicationIdentifier(target, identifier);
                            changed += " identifier(" + target.TargetName + ")";
                        }
                    }
                }
            }
            if (changed.Length > 0)
            {
                AssetDatabase.SaveAssets();
                Debug.Log($"{BasisAppConfig.FileName} applied to ProjectSettings:{changed}");
            }
        }
        public static void WriteBuildCopy()
        {
            RemoveBuildCopy();
            string path = BasisAppConfig.EditorPath;
            if (!File.Exists(path))
            {
                return;
            }
            string resources = Path.Combine(BuildFolder, "Resources");
            Directory.CreateDirectory(resources);
            string target = Path.Combine(resources, BasisAppConfig.ResourceName + ".json").Replace('\\', '/');
            File.Copy(path, target, true);
            AssetDatabase.ImportAsset(target, ImportAssetOptions.ForceSynchronousImport);
        }
        public static void RemoveBuildCopy()
        {
            if (!Directory.Exists(BuildFolder))
            {
                return;
            }
            if (!AssetDatabase.DeleteAsset(BuildFolder))
            {
                Directory.Delete(BuildFolder, true);
                File.Delete(BuildFolder + ".meta");
            }
        }
        [MenuItem("Basis/App Config/Open basis.config.json")]
        private static void OpenConfig()
        {
            string path = BasisAppConfig.EditorPath;
            if (!File.Exists(path))
            {
                string identifier = PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Standalone);
                File.WriteAllText(path, "{\n  \"productName\": " + Quote(PlayerSettings.productName) + ",\n  \"companyName\": " + Quote(PlayerSettings.companyName) + ",\n  \"identifier\": " + Quote(identifier) + ",\n  \"version\": " + Quote(PlayerSettings.bundleVersion) + ",\n  \"features\": {\n    \"" + BasisAppConfig.MainMenu + "\": true\n  },\n  \"settings\": {}\n}\n");
                Debug.Log($"Wrote {path} from the current ProjectSettings.");
            }
            EditorUtility.OpenWithDefaultApp(path);
        }
        [MenuItem("Basis/App Config/Apply basis.config.json")]
        private static void ApplyNow()
        {
            Refresh(true);
        }
        private static string Quote(string value)
        {
            return "\"" + (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }
    }
    public sealed class BasisAppConfigBuild : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        public int callbackOrder => int.MinValue;
        public void OnPreprocessBuild(BuildReport report)
        {
            BasisAppConfigEditor.Refresh(true);
            BasisAppConfigEditor.WriteBuildCopy();
        }
        public void OnPostprocessBuild(BuildReport report)
        {
            BasisAppConfigEditor.RemoveBuildCopy();
        }
    }
}
