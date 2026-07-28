using System;
using System.IO;
using UnityEngine;

namespace Basis.Scripts.Common
{
    /// <summary>
    /// One-time move of the persistent data folder from the old "Basis Unity" name to "BasisVR"
    /// so existing settings, cache and keys survive the rename. Runs before any persistentDataPath read.
    /// </summary>
    public static class BasisPersistentDataMigration
    {
        const string OldCompany = "Basis Unity";
        const string OldProduct = "Basis Unity";
        const string MarkerName = ".migrated-from-basis-unity";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Migrate()
        {
            switch (Application.platform)
            {
                case RuntimePlatform.WindowsPlayer:
                case RuntimePlatform.WindowsEditor:
                case RuntimePlatform.LinuxPlayer:
                case RuntimePlatform.LinuxEditor:
                case RuntimePlatform.OSXPlayer:
                case RuntimePlatform.OSXEditor:
                    break;
                default:
                    return;
            }

            try
            {
                string newPath = Application.persistentDataPath;
                DirectoryInfo root = Directory.GetParent(newPath)?.Parent;
                if (root == null)
                {
                    return;
                }

                string oldPath = Path.Combine(root.FullName, OldCompany, OldProduct);
                if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                string marker = Path.Combine(newPath, MarkerName);
                if (File.Exists(marker) || !Directory.Exists(oldPath))
                {
                    return;
                }

                Directory.CreateDirectory(newPath);
                MoveMerge(oldPath, newPath);
                File.WriteAllText(marker, DateTime.UtcNow.ToString("O"));
                BasisDebug.Log($"Migrated persistent data from \"{oldPath}\" to \"{newPath}\".");
            }
            catch (Exception e)
            {
                Debug.LogWarning("Persistent data migration skipped: " + e.Message);
            }
        }

        static void MoveMerge(string source, string destination)
        {
            Directory.CreateDirectory(destination);

            foreach (string file in Directory.GetFiles(source))
            {
                string target = Path.Combine(destination, Path.GetFileName(file));
                if (!File.Exists(target))
                {
                    File.Move(file, target);
                }
            }

            foreach (string directory in Directory.GetDirectories(source))
            {
                string target = Path.Combine(destination, Path.GetFileName(directory));
                if (Directory.Exists(target))
                {
                    MoveMerge(directory, target);
                }
                else
                {
                    Directory.Move(directory, target);
                }
            }
        }
    }
}
