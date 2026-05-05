using Basis.Scripts.TransformBinders.BoneControl;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Basis.Scripts.Avatar
{
    /// <summary>
    /// Per-tracker forced-role table, keyed by <see cref="Device_Management.Devices.BasisInput.UniqueDeviceIdentifier"/>.
    /// When an override is set, the constellation classifier in
    /// <see cref="BasisAvatarIKStageCalibration"/> binds the tracker straight to
    /// the chosen role at calibration time and never scores it against any
    /// prior — the user has told us where the tracker lives, so discovery is
    /// pointless.
    ///
    /// Persisted as JSON next to the pairing file so a one-time setup survives
    /// restarts. Symmetric with <see cref="BasisTrackerPairing"/> in shape — the
    /// two stores are independent (a tracker can be paired AND have its role
    /// forced; the override applies to the merged virtual midpoint via either
    /// half's id).
    /// </summary>
    public static class BasisTrackerRoleOverride
    {
        private const string FileName = "trackerRoleOverrides.json";

        private static string _filePath;
        private static string FilePath =>
            _filePath ??= Path.Combine(Application.persistentDataPath, FileName);

        private static readonly Dictionary<string, BasisBoneTrackedRole> _overrides = new();
        private static bool _loaded;

        public static event Action OnOverridesChanged;

        public static bool TryGetOverride(string id, out BasisBoneTrackedRole role)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(id))
            {
                role = BasisBoneTrackedRole.CenterEye;
                return false;
            }
            return _overrides.TryGetValue(id, out role);
        }

        /// <summary>
        /// Force <paramref name="role"/> on the tracker with id <paramref name="id"/>.
        /// Replaces any prior override on that tracker. Persists immediately.
        /// </summary>
        public static void SetOverride(string id, BasisBoneTrackedRole role)
        {
            if (string.IsNullOrEmpty(id)) return;

            EnsureLoaded();
            if (_overrides.TryGetValue(id, out BasisBoneTrackedRole existing) && existing == role)
            {
                return;
            }
            _overrides[id] = role;
            Save();
            OnOverridesChanged?.Invoke();
        }

        /// <summary>
        /// Remove any override for <paramref name="id"/>. No-op if there isn't one.
        /// </summary>
        public static void ClearOverride(string id)
        {
            if (string.IsNullOrEmpty(id)) return;

            EnsureLoaded();
            if (!_overrides.Remove(id)) return;
            Save();
            OnOverridesChanged?.Invoke();
        }

        // ----------------------------------------------------------------
        // Persistence
        // ----------------------------------------------------------------

        [Serializable]
        private class OverrideEntry
        {
            public string id;
            // Stored as the enum's int value so renames in BasisBoneTrackedRole
            // don't silently corrupt the file.
            public int role;
        }

        [Serializable]
        private class OverridesFile
        {
            public List<OverrideEntry> entries = new List<OverrideEntry>();
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (!File.Exists(FilePath)) return;
                string json = File.ReadAllText(FilePath);
                if (string.IsNullOrEmpty(json)) return;
                OverridesFile data = JsonUtility.FromJson<OverridesFile>(json);
                if (data?.entries == null) return;
                for (int i = 0; i < data.entries.Count; i++)
                {
                    OverrideEntry e = data.entries[i];
                    if (e == null || string.IsNullOrEmpty(e.id)) continue;
                    if (!Enum.IsDefined(typeof(BasisBoneTrackedRole), e.role)) continue;
                    _overrides[e.id] = (BasisBoneTrackedRole)e.role;
                }
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"[BasisTrackerRoleOverride] Failed to load {FilePath}: {ex}");
            }
        }

        private static void Save()
        {
            try
            {
                OverridesFile data = new OverridesFile();
                foreach (KeyValuePair<string, BasisBoneTrackedRole> kvp in _overrides)
                {
                    data.entries.Add(new OverrideEntry { id = kvp.Key, role = (int)kvp.Value });
                }
                string json = JsonUtility.ToJson(data, true);
                File.WriteAllText(FilePath, json);
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"[BasisTrackerRoleOverride] Failed to save {FilePath}: {ex}");
            }
        }
    }
}
