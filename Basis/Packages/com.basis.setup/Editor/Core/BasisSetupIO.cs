using System;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Basis.Setup
{
    public static class BasisSetupIO
    {
        private const string BackupRoot = "BasisSetupBackups";

        public static void ForceGuid(string assetPath, string guid)
        {
            if (string.IsNullOrEmpty(guid))
            {
                return;
            }

            if (AssetDatabase.AssetPathToGUID(assetPath) == guid)
            {
                return;
            }

            string metaFull = ToFullPath(assetPath) + ".meta";
            if (!File.Exists(metaFull))
            {
                return;
            }

            string meta = File.ReadAllText(metaFull);
            string patched = Regex.Replace(meta, "guid: [0-9a-fA-F]{32}", "guid: " + guid);
            if (patched != meta)
            {
                File.WriteAllText(metaFull, patched);
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            }
        }

        public static void EnsureAssetFolder(string assetFolder)
        {
            assetFolder = assetFolder.Replace('\\', '/').TrimEnd('/');
            if (string.IsNullOrEmpty(assetFolder) || AssetDatabase.IsValidFolder(assetFolder))
            {
                return;
            }

            string[] parts = assetFolder.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, parts[i]);
                }

                current = next;
            }
        }

        public static void EnsureParentFolder(string assetPath)
        {
            int slash = assetPath.Replace('\\', '/').LastIndexOf('/');
            if (slash > 0)
            {
                EnsureAssetFolder(assetPath.Substring(0, slash));
            }
        }

        public static bool BackupIfExists(string assetPath)
        {
            string full = ToFullPath(assetPath);
            if (!File.Exists(full))
            {
                return false;
            }

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string dest = Path.Combine(ProjectRoot(), BackupRoot, stamp, assetPath.Replace('\\', '/'));
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            File.Copy(full, dest, true);

            string meta = full + ".meta";
            if (File.Exists(meta))
            {
                File.Copy(meta, dest + ".meta", true);
            }

            return true;
        }

        public static bool WriteTextAsset(string assetPath, string content, BasisSetupMode mode)
        {
            string full = ToFullPath(assetPath);
            bool exists = File.Exists(full);

            if (exists)
            {
                if (mode == BasisSetupMode.EnsureExists)
                {
                    return false;
                }

                if (File.ReadAllText(full) == content)
                {
                    return false;
                }

                BackupIfExists(assetPath);
            }

            EnsureParentFolder(assetPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllText(full, content);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            return true;
        }

        public static bool WriteRawFile(string projectRelativePath, string content, BasisSetupMode mode)
        {
            string full = Path.Combine(ProjectRoot(), projectRelativePath);
            bool exists = File.Exists(full);

            if (exists)
            {
                if (mode == BasisSetupMode.EnsureExists)
                {
                    return false;
                }

                if (File.ReadAllText(full) == content)
                {
                    return false;
                }

                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string dest = Path.Combine(ProjectRoot(), BackupRoot, stamp, projectRelativePath.Replace('\\', '/'));
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                File.Copy(full, dest, true);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllText(full, content);
            return true;
        }

        public static T CreateOrUpdateAsset<T>(string assetPath, BasisSetupMode mode, Action<T> configure, out bool changed)
            where T : ScriptableObject
        {
            T asset = AssetDatabase.LoadAssetAtPath<T>(assetPath);
            if (asset != null && mode == BasisSetupMode.EnsureExists)
            {
                changed = false;
                return asset;
            }

            if (asset != null && mode == BasisSetupMode.Update)
            {
                BackupIfExists(assetPath);
                configure(asset);
                EditorUtility.SetDirty(asset);
                changed = true;
                return asset;
            }

            EnsureParentFolder(assetPath);
            asset = ScriptableObject.CreateInstance<T>();
            configure(asset);
            AssetDatabase.CreateAsset(asset, assetPath);
            changed = true;
            return asset;
        }

        public static string ToFullPath(string assetPath)
        {
            return Path.Combine(ProjectRoot(), assetPath.Replace('/', Path.DirectorySeparatorChar));
        }

        public static string ProjectRoot()
        {
            return Directory.GetParent(Application.dataPath).FullName;
        }
    }
}
