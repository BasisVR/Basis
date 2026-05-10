using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

namespace Basis.Editor.Localization
{
    /// <summary>
    /// Registers every language JSON under
    /// <c>Packages/com.basis.framework/BasisUI/Localization/Languages/</c>
    /// as an Addressable with address <c>Languages/{code}</c> and the
    /// <c>language</c> label that <c>BasisLocalization</c> loads at runtime.
    ///
    /// <para>Runs as both a menu item and an <see cref="AssetPostprocessor"/>
    /// so dropping a new language file into the folder is enough to wire it
    /// up — no manual Addressables bookkeeping required.</para>
    /// </summary>
    public static class BasisLocalizationAddressableSetup
    {
        private const string LanguagesFolder = "Packages/com.basis.framework/BasisUI/Localization/Languages";
        private const string TargetGroupName = "Basis UI Assets";
        private const string LanguageLabel = "language"; // Must match BasisLocalization.LanguageLabel.
        private const string AddressPrefix = "Languages/";

        [MenuItem("Basis/Localization/Register Languages as Addressable")]
        public static void RegisterAllMenu()
        {
            int count = Register(logEach: true);
            if (count > 0)
            {
                AssetDatabase.SaveAssets();
                Debug.Log($"[BasisLocalization] Registered {count} language file(s) as Addressable with label \"{LanguageLabel}\". Now rebuild Addressables content (Window > Asset Management > Addressables > Groups > Build > New Build > Default Build Script).");
            }
        }

        private static int Register(bool logEach)
        {
            AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                Debug.LogError("[BasisLocalization] AddressableAssetSettings not found. Open Window > Asset Management > Addressables > Groups to create one.");
                return 0;
            }

            List<string> labels = settings.GetLabels();
            if (labels == null || !labels.Contains(LanguageLabel))
            {
                settings.AddLabel(LanguageLabel, postEvent: false);
            }

            AddressableAssetGroup group = settings.FindGroup(TargetGroupName) ?? settings.DefaultGroup;
            if (group == null)
            {
                Debug.LogError($"[BasisLocalization] No Addressable group available (looked for \"{TargetGroupName}\" and DefaultGroup).");
                return 0;
            }

            if (!Directory.Exists(LanguagesFolder))
            {
                Debug.LogError($"[BasisLocalization] Language folder not found: {LanguagesFolder}");
                return 0;
            }

            string[] jsonFiles = Directory.GetFiles(LanguagesFolder, "*.json");
            int updated = 0;
            for (int i = 0; i < jsonFiles.Length; i++)
            {
                string unityPath = jsonFiles[i].Replace('\\', '/');
                string code = Path.GetFileNameWithoutExtension(unityPath);
                string guid = AssetDatabase.AssetPathToGUID(unityPath);
                if (string.IsNullOrEmpty(guid))
                {
                    Debug.LogError($"[BasisLocalization] No GUID for {unityPath} — reimport may not have finished.");
                    continue;
                }

                AddressableAssetEntry entry = settings.CreateOrMoveEntry(guid, group, readOnly: false, postEvent: false);
                if (entry == null)
                {
                    Debug.LogError($"[BasisLocalization] Failed to create Addressable entry for {unityPath}");
                    continue;
                }

                string newAddress = AddressPrefix + code;
                if (entry.address != newAddress)
                {
                    entry.SetAddress(newAddress, postEvent: false);
                }

                if (!entry.labels.Contains(LanguageLabel))
                {
                    entry.SetLabel(LanguageLabel, enable: true, force: true, postEvent: false);
                }

                updated++;
                if (logEach)
                {
                    Debug.Log($"[BasisLocalization] Registered {unityPath} as \"{newAddress}\"");
                }
            }

            if (updated > 0)
            {
                settings.SetDirty(AddressableAssetSettings.ModificationEvent.BatchModification, null, postEvent: true, settingsModified: true);
            }

            return updated;
        }

        private class Importer : AssetPostprocessor
        {
            private static void OnPostprocessAllAssets(
                string[] importedAssets,
                string[] deletedAssets,
                string[] movedAssets,
                string[] movedFromAssetPaths)
            {
                if (!TouchesLanguagesFolder(importedAssets) && !TouchesLanguagesFolder(movedAssets))
                {
                    return;
                }

                Register(logEach: false);
            }

            private static bool TouchesLanguagesFolder(string[] paths)
            {
                if (paths == null)
                {
                    return false;
                }

                for (int i = 0; i < paths.Length; i++)
                {
                    string p = paths[i];
                    if (string.IsNullOrEmpty(p))
                    {
                        continue;
                    }

                    string normalized = p.Replace('\\', '/');
                    if (normalized.StartsWith(LanguagesFolder, StringComparison.OrdinalIgnoreCase)
                        && normalized.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                return false;
            }
        }
    }
}
