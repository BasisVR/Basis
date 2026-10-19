using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;

namespace Basis.Editor
{
    /// <summary>
    /// Organizes Basis Addressable groups for memory. Moves self-contained clusters into
    /// their own group via path rules (gizmos, fonts), then isolates every dependency shared
    /// across 2+ groups into a shared bundle (fonts -> "Basis Fonts", the rest -> "Basis Shared")
    /// so it is referenced once instead of copied into each bundle. Editor-only and script
    /// assets are ignored. Idempotent; run the Dependency Report afterwards to verify.
    /// </summary>
    [NoAutoStaticsCleanup]
    public static class BasisAddressableOrganizer
    {
        private const string FontsGroup = "Basis Fonts";
        private const string SharedGroup = "Basis Shared";

        private static readonly (string prefix, string group, BundledAssetGroupSchema.BundlePackingMode packing)[] PathRules =
        {
            ("Packages/com.basis.gizmos/", "Basis Gizmos", BundledAssetGroupSchema.BundlePackingMode.PackTogether),
            ("Packages/com.basis.sdk/Fonts/", FontsGroup, BundledAssetGroupSchema.BundlePackingMode.PackTogether),
        };

        [MenuItem("Basis/Build/Addressables/Organize Groups", false, 340)]
        public static void OrganizeMenu()
        {
            AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                Debug.LogError("[BasisAddressables] AddressableAssetSettings not found.");
                return;
            }

            int moved = ApplyPathRules(settings);
            int isolated = IsolateSharedDependencies(settings);

            if (moved + isolated > 0)
            {
                settings.SetDirty(AddressableAssetSettings.ModificationEvent.BatchModification, null, true, true);
                AssetDatabase.SaveAssets();
            }
            Debug.Log($"[BasisAddressables] Organize: moved {moved} clustered asset(s), isolated {isolated} shared dependency(ies). Re-run the Dependency Report to verify, then rebuild Addressables content.");
        }

        private static int ApplyPathRules(AddressableAssetSettings settings)
        {
            int moved = 0;
            foreach ((string prefix, string groupName, BundledAssetGroupSchema.BundlePackingMode packing) in PathRules)
            {
                List<string> guids = new List<string>();
                foreach (AddressableAssetGroup g in settings.groups)
                {
                    if (g == null)
                    {
                        continue;
                    }
                    foreach (AddressableAssetEntry e in g.entries)
                    {
                        if (e != null && !string.IsNullOrEmpty(e.AssetPath)
                            && e.AssetPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            guids.Add(e.guid);
                        }
                    }
                }
                if (guids.Count == 0)
                {
                    continue;
                }

                AddressableAssetGroup group = BasisAddressableGroups.GetOrCreate(settings, groupName, packing);
                for (int i = 0; i < guids.Count; i++)
                {
                    AddressableAssetEntry existing = settings.FindAssetEntry(guids[i]);
                    if (existing != null && existing.parentGroup == group)
                    {
                        continue;
                    }
                    if (settings.CreateOrMoveEntry(guids[i], group, false, false) != null)
                    {
                        moved++;
                    }
                }
            }
            return moved;
        }

        private static int IsolateSharedDependencies(AddressableAssetSettings settings)
        {
            List<(string path, string group)> entries = new List<(string, string)>();
            HashSet<string> addressablePaths = new HashSet<string>();
            foreach (AddressableAssetGroup g in settings.groups)
            {
                if (g == null)
                {
                    continue;
                }
                foreach (AddressableAssetEntry e in g.entries)
                {
                    if (e == null || string.IsNullOrEmpty(e.AssetPath) || AssetDatabase.IsValidFolder(e.AssetPath))
                    {
                        continue;
                    }
                    addressablePaths.Add(e.AssetPath);
                    entries.Add((e.AssetPath, g.Name));
                }
            }

            Dictionary<string, HashSet<string>> depToGroups = new Dictionary<string, HashSet<string>>();
            foreach ((string path, string group) in entries)
            {
                string[] deps = AssetDatabase.GetDependencies(path, true);
                for (int i = 0; i < deps.Length; i++)
                {
                    string dep = deps[i];
                    if (dep == path || addressablePaths.Contains(dep) || !IsShippable(dep))
                    {
                        continue;
                    }
                    if (!depToGroups.TryGetValue(dep, out HashSet<string> set))
                    {
                        set = new HashSet<string>();
                        depToGroups[dep] = set;
                    }
                    set.Add(group);
                }
            }

            int isolated = 0;
            foreach (KeyValuePair<string, HashSet<string>> kv in depToGroups)
            {
                if (kv.Value.Count < 2)
                {
                    continue;
                }
                string targetName = IsFont(kv.Key) ? FontsGroup : SharedGroup;
                AddressableAssetGroup target = BasisAddressableGroups.GetOrCreate(
                    settings, targetName, BundledAssetGroupSchema.BundlePackingMode.PackTogether);
                string guid = AssetDatabase.AssetPathToGUID(kv.Key);
                if (string.IsNullOrEmpty(guid))
                {
                    continue;
                }
                if (settings.CreateOrMoveEntry(guid, target, false, false) != null)
                {
                    isolated++;
                }
            }
            return isolated;
        }

        private static bool IsShippable(string path)
        {
            if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            string p = path.Replace('\\', '/');
            return !p.Contains("/Editor/") && !p.Contains("/Editor Resources/");
        }

        private static bool IsFont(string path)
        {
            string p = path.Replace('\\', '/');
            return p.Contains("/Fonts")
                || p.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)
                || p.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)
                || p.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase);
        }
    }
}