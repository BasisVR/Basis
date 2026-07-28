using Basis.BasisUI;
using Basis.Scripts.Settings;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Basis.BasisUI.EditorTools
{
    public static class ServersFirstRunWelcomeDebugEditor
    {
        private static string SettingsPath => Path.Combine(Application.persistentDataPath, BasisSettingsSystem.SettingsJson);
        private static string BackupPath => SettingsPath + ".firstrun-backup";

        [MenuItem("Basis/Servers/First-Run Welcome/Show Again")]
        public static void ShowAgain()
        {
            if (Application.isPlaying)
            {
                ServersFirstRunWelcome.ResetSeen();
                Debug.Log("First-run welcome re-armed. Close and reopen the Servers panel to see it.");
                return;
            }

            if (!File.Exists(SettingsPath))
            {
                Debug.Log($"No settings file at {SettingsPath} — the welcome will already show as a fresh install on next Play.");
                return;
            }

            SettingsData data = JsonUtility.FromJson<SettingsData>(File.ReadAllText(SettingsPath));
            if (data == null)
            {
                Debug.LogWarning($"Could not parse {SettingsPath}; not modified.");
                return;
            }
            data.RebuildDictionary();
            data.settings[ServersFirstRunWelcome.SeenKey] = "false";
            data.RebuildList();
            File.WriteAllText(SettingsPath, JsonUtility.ToJson(data, true));
            Debug.Log("First-run welcome re-armed. It will show on the Servers panel next Play.");
        }

        [MenuItem("Basis/Servers/First-Run Welcome/Simulate Fresh Install (Backup Settings)")]
        public static void SimulateFreshInstall()
        {
            if (Application.isPlaying)
            {
                Debug.LogWarning("Exit Play mode first — the settings file is live.");
                return;
            }

            if (!File.Exists(SettingsPath))
            {
                Debug.Log($"No settings file at {SettingsPath} — already a fresh install.");
                return;
            }

            if (!EditorUtility.DisplayDialog("Simulate Fresh Install",
                $"Move {BasisSettingsSystem.SettingsJson} to a backup so the next Play behaves like a brand-new install?\n\nRestore it afterwards via Basis/Servers/First-Run Welcome/Restore Settings Backup.",
                "Backup & Remove", "Cancel"))
            {
                return;
            }

            File.Copy(SettingsPath, BackupPath, true);
            File.Delete(SettingsPath);
            Debug.Log($"Settings backed up to {BackupPath}. Next Play is a fresh install (welcome, default language detect, default settings).");
        }

        [MenuItem("Basis/Servers/First-Run Welcome/Restore Settings Backup")]
        public static void RestoreSettingsBackup()
        {
            if (Application.isPlaying)
            {
                Debug.LogWarning("Exit Play mode first — the settings file is live.");
                return;
            }

            if (!File.Exists(BackupPath))
            {
                Debug.LogWarning($"No backup found at {BackupPath}.");
                return;
            }

            File.Copy(BackupPath, SettingsPath, true);
            File.Delete(BackupPath);
            Debug.Log($"Settings restored from backup to {SettingsPath}.");
        }
    }
}
