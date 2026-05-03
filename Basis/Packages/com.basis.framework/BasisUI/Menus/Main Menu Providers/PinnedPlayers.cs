using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Basis.BasisUI
{
    public static class PinnedPlayers
    {
        private const string FileName = "pinnedPlayers.json";
        private const string LegacyPlayerPrefsKey = "BasisUI.PinnedPlayerUUIDs";
        private const int MaxPins = 256;

        private static readonly string FilePath = Path.Combine(Application.persistentDataPath, FileName);
        private static readonly object _gate = new object();

        private static HashSet<string> _pinned;

        public static event Action Changed;

        [Serializable]
        private class PinnedData
        {
            public List<string> uuids = new List<string>();
        }

        private static void EnsureCache()
        {
            if (_pinned != null) return;
            _pinned = new HashSet<string>(StringComparer.Ordinal);

            if (File.Exists(FilePath))
            {
                try
                {
                    string json = File.ReadAllText(FilePath);
                    PinnedData data = JsonUtility.FromJson<PinnedData>(json);
                    if (data?.uuids != null)
                    {
                        for (int i = 0; i < data.uuids.Count && _pinned.Count < MaxPins; i++)
                        {
                            string id = data.uuids[i];
                            if (!string.IsNullOrEmpty(id)) _pinned.Add(id);
                        }
                    }
                }
                catch (Exception e)
                {
                    BasisDebug.LogError($"[PinnedPlayers] Failed to load {FilePath}: {e}");
                }
            }

            if (MigrateFromPlayerPrefs())
                Save();
        }

        private static bool MigrateFromPlayerPrefs()
        {
            if (!PlayerPrefs.HasKey(LegacyPlayerPrefsKey)) return false;
            bool changed = false;
            string raw = PlayerPrefs.GetString(LegacyPlayerPrefsKey, string.Empty);
            if (!string.IsNullOrEmpty(raw))
            {
                foreach (string id in raw.Split('|'))
                {
                    if (!string.IsNullOrEmpty(id) && _pinned.Count < MaxPins && _pinned.Add(id))
                        changed = true;
                }
            }
            PlayerPrefs.DeleteKey(LegacyPlayerPrefsKey);
            PlayerPrefs.Save();
            return changed;
        }

        private static void Save()
        {
            try
            {
                PinnedData data = new PinnedData();
                data.uuids.AddRange(_pinned);
                string json = JsonUtility.ToJson(data, true);
                File.WriteAllText(FilePath, json);
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"[PinnedPlayers] Failed to save {FilePath}: {e}");
            }
        }

        public static bool IsPinned(string uuid)
        {
            if (string.IsNullOrEmpty(uuid)) return false;
            lock (_gate)
            {
                EnsureCache();
                return _pinned.Contains(uuid);
            }
        }

        public static bool Toggle(string uuid)
        {
            if (string.IsNullOrEmpty(uuid)) return false;
            bool nowPinned;
            lock (_gate)
            {
                EnsureCache();
                if (_pinned.Contains(uuid))
                {
                    _pinned.Remove(uuid);
                    nowPinned = false;
                }
                else
                {
                    if (_pinned.Count >= MaxPins)
                    {
                        BasisDebug.LogWarning($"[PinnedPlayers] Pin cap reached ({MaxPins}); refusing to add {uuid}.");
                        return false;
                    }
                    _pinned.Add(uuid);
                    nowPinned = true;
                }
                Save();
            }
            Changed?.Invoke();
            return nowPinned;
        }

        public static List<string> GetAll()
        {
            lock (_gate)
            {
                EnsureCache();
                return new List<string>(_pinned);
            }
        }

        public static void ClearAll()
        {
            lock (_gate)
            {
                if (_pinned == null)
                    _pinned = new HashSet<string>(StringComparer.Ordinal);
                else
                    _pinned.Clear();
                Save();
            }
            Changed?.Invoke();
        }
    }
}
