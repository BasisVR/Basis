using Basis.BasisUI;
using Basis.Editor.Localization;
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

        [MenuItem("Basis/Servers/First-Run Welcome/Show Again", false, 700)]
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

        [MenuItem("Basis/Servers/First-Run Welcome/Simulate Fresh Install (Backup Settings)", false, 701)]
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

            if (!EditorUtility.DisplayDialog(BasisEditorLocalization.Get("editor.servers.welcomeDebug.simulateTitle"),
                BasisEditorLocalization.Get("editor.servers.welcomeDebug.simulateBody", BasisSettingsSystem.SettingsJson),
                BasisEditorLocalization.Get("editor.servers.welcomeDebug.backupRemove"), BasisEditorLocalization.Get("editor.common.cancel")))
            {
                return;
            }

            File.Copy(SettingsPath, BackupPath, true);
            File.Delete(SettingsPath);
            Debug.Log($"Settings backed up to {BackupPath}. Next Play is a fresh install (welcome, default language detect, default settings).");
        }

        [MenuItem("Basis/Servers/First-Run Welcome/Restore Settings Backup", false, 702)]
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
