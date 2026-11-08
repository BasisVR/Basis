using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;

namespace Basis.Editor.Localization
{
    /// <summary>
    /// Registers every runtime language JSON as an Addressable with the
    /// <c>language</c> label that <c>BasisLocalization</c> loads at runtime.
    ///
    /// <para>Any package can contribute strings, not just the framework: every
    /// <c>*.json</c> whose asset path passes through a <c>Localization/Languages</c>
    /// folder is discovered through the <see cref="AssetDatabase"/>, so it works
    /// regardless of how the package was added — embedded under <c>Packages/</c>,
    /// referenced locally, or installed from a registry/git into the read-only
    /// package cache. Dropping <c>{code}.json</c> into
    /// <c>&lt;your-package&gt;/.../Localization/Languages/</c> is enough; files
    /// that share a language code merge by key at runtime, so a package only ships
    /// the keys it owns.</para>
    ///
    /// <para>The SDK editor twin folder is excluded — those strings belong to
    /// <c>BasisEditorLocalization</c>, which loads from disk via AssetDatabase and
    /// must not ship in runtime bundles.</para>
    ///
    /// <para>Runs as both a menu item and an <see cref="AssetPostprocessor"/> so
    /// importing a package (or editing a language file) auto-registers it — no
    /// manual Addressables bookkeeping required. A content rebuild is still needed
    /// before the strings appear in a player build.</para>
    /// </summary>
    public static class BasisLocalizationAddressableSetup
    {
        private const string FrameworkNamespace = "com.basis.framework";
        // Editor-only twin (BasisEditorLocalization) — loaded via AssetDatabase, never Addressable.
        private const string EditorOnlyLanguagesFolder = "Packages/com.basis.sdk/Localization/Languages";
        private const string LanguagesFolderToken = "/Localization/Languages/";
        private const string TargetGroupName = "Basis Localization";
        private const string LanguageLabel = "language"; // Must match BasisLocalization.LanguageLabel.
        private const string AddressPrefix = "Languages/";

        [MenuItem("Basis/Settings/Localization/Register Languages as Addressable", false, 441)]
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

            AddressableAssetGroup group = BasisAddressableGroups.GetOrCreate(
                settings, TargetGroupName, BundledAssetGroupSchema.BundlePackingMode.PackTogether);
            if (group == null)
            {
                Debug.LogError($"[BasisLocalization] Could not create Addressable group \"{TargetGroupName}\".");
                return 0;
            }

            List<string> assetPaths = DiscoverLanguageAssetPaths();
            if (assetPaths.Count == 0)
            {
                Debug.LogError($"[BasisLocalization] No language JSON found. Expected files under a \"{LanguagesFolderToken.Trim('/')}\" folder in any package.");
                return 0;
            }

            int updated = PruneMissing(group);
            for (int i = 0; i < assetPaths.Count; i++)
            {
                updated += RegisterFile(settings, group, assetPaths[i], logEach);
            }

            if (updated > 0)
            {
                settings.SetDirty(AddressableAssetSettings.ModificationEvent.BatchModification, null, postEvent: true, settingsModified: true);
            }

            return updated;
        }

        private static int PruneMissing(AddressableAssetGroup group)
        {
            List<AddressableAssetEntry> missing = null;
            foreach (AddressableAssetEntry entry in group.entries)
            {
                if (entry != null && string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(entry.guid)))
                {
                    (missing ??= new List<AddressableAssetEntry>()).Add(entry);
                }
            }
            if (missing == null)
            {
                return 0;
            }
            for (int i = 0; i < missing.Count; i++)
            {
                group.RemoveAssetEntry(missing[i], postEvent: false);
            }
            return missing.Count;
        }

        private static int RegisterFile(AddressableAssetSettings settings, AddressableAssetGroup group, string unityPath, bool logEach)
        {
            string guid = AssetDatabase.AssetPathToGUID(unityPath);
            if (string.IsNullOrEmpty(guid))
            {
                Debug.LogError($"[BasisLocalization] No GUID for {unityPath} — reimport may not have finished.");
                return 0;
            }

            AddressableAssetEntry entry = settings.CreateOrMoveEntry(guid, group, readOnly: false, postEvent: false);
            if (entry == null)
            {
                Debug.LogError($"[BasisLocalization] Failed to create Addressable entry for {unityPath}");
                return 0;
            }

            string code = Path.GetFileNameWithoutExtension(unityPath);
            string owner = NamespaceOf(unityPath);

            // Framework keeps the bare Languages/{code} address for back-compat;
            // every other owner is namespaced so a shared code can't collide.
            string newAddress = string.IsNullOrEmpty(owner) || string.Equals(owner, FrameworkNamespace, StringComparison.OrdinalIgnoreCase)
                ? AddressPrefix + code
                : AddressPrefix + owner + "/" + code;
            if (entry.address != newAddress)
            {
                entry.SetAddress(newAddress, postEvent: false);
            }

            if (!entry.labels.Contains(LanguageLabel))
            {
                entry.SetLabel(LanguageLabel, enable: true, force: true, postEvent: false);
            }

            if (logEach)
            {
                Debug.Log($"[BasisLocalization] Registered {unityPath} as \"{newAddress}\"");
            }

            return 1;
        }

        private static List<string> DiscoverLanguageAssetPaths()
        {
            var paths = new List<string>();
            string[] guids = AssetDatabase.FindAssets("t:TextAsset");
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }
                if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (path.IndexOf(LanguagesFolderToken, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                if (path.StartsWith(EditorOnlyLanguagesFolder + "/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                paths.Add(path);
            }

            return paths;
        }

        // The owner segment used to namespace addresses: the second path segment,
        // i.e. the package id under "Packages/<id>/..." or the top folder under
        // "Assets/<folder>/...".
        private static string NamespaceOf(string unityPath)
        {
            string normalized = unityPath.Replace('\\', '/');
            int first = normalized.IndexOf('/');
            if (first < 0)
            {
                return string.Empty;
            }

            int second = normalized.IndexOf('/', first + 1);
            if (second < 0)
            {
                return string.Empty;
            }

            return normalized.Substring(first + 1, second - first - 1);
        }

        private class Importer : AssetPostprocessor
        {
            private static void OnPostprocessAllAssets(
                string[] importedAssets,
                string[] deletedAssets,
                string[] movedAssets,
                string[] movedFromAssetPaths)
            {
                if (!TouchesLanguagesFolder(importedAssets) && !TouchesLanguagesFolder(movedAssets) && !TouchesLanguagesFolder(deletedAssets))
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
                    if (!normalized.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (normalized.IndexOf(LanguagesFolderToken, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }
                    if (normalized.StartsWith(EditorOnlyLanguagesFolder + "/", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    return true;
                }

                return false;
            }
        }
    }
}
