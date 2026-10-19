using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Basis.BasisUI
{

    /// <summary>
    /// Persisted per-UUID policy for incoming direct-connection requests. Mirrors
    /// <see cref="BasisTrustedUrls"/>: a JSON file in persistentDataPath, cached in
    /// memory. Only non-default (non-Ask) policies are stored; everyone else falls back
    /// to <see cref="BasisDirectConnectionPolicy.Ask"/>.
    /// </summary>
    [AutoStaticsCleanup]
    public static partial class BasisTrustedConnections
    {
        private const string FileName = "trustedConnections.json";
        private static readonly string FilePath = Path.Combine(Application.persistentDataPath, FileName);

        private static Dictionary<string, BasisDirectConnectionPolicy> _cache;

        /// <summary>Raised whenever a policy changes (UI can refresh).</summary>
        public static event Action OnChanged;

        [Serializable]
        private class Entry
        {
            public string uuid;
            public string policy;
        }

        [Serializable]
        private class Data
        {
            public List<Entry> entries = new List<Entry>();
        }

        private static void EnsureCache()
        {
            if (_cache != null) return;
            _cache = new Dictionary<string, BasisDirectConnectionPolicy>(StringComparer.OrdinalIgnoreCase);

            if (!File.Exists(FilePath)) return;
            try
            {
                Data data = JsonUtility.FromJson<Data>(File.ReadAllText(FilePath));
                if (data?.entries == null) return;
                foreach (Entry e in data.entries)
                {
                    if (string.IsNullOrEmpty(e?.uuid)) continue;
                    if (Enum.TryParse(e.policy, out BasisDirectConnectionPolicy policy) &&
                        policy != BasisDirectConnectionPolicy.Ask)
                    {
                        _cache[e.uuid] = policy;
                    }
                }
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"[BasisTrustedConnections] Failed to load {FilePath}: {ex}");
            }
        }

        private static void Save()
        {
            try
            {
                Data data = new Data();
                foreach (KeyValuePair<string, BasisDirectConnectionPolicy> kv in _cache)
                {
                    data.entries.Add(new Entry { uuid = kv.Key, policy = kv.Value.ToString() });
                }
                File.WriteAllText(FilePath, JsonUtility.ToJson(data, true));
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"[BasisTrustedConnections] Failed to save {FilePath}: {ex}");
            }
            OnChanged?.Invoke();
        }

        /// <summary>Saved policy for a UUID, or <see cref="BasisDirectConnectionPolicy.Ask"/> if none.</summary>
        public static BasisDirectConnectionPolicy GetPolicy(string uuid)
        {
            if (string.IsNullOrEmpty(uuid)) return BasisDirectConnectionPolicy.Ask;
            EnsureCache();
            return _cache.TryGetValue(uuid, out BasisDirectConnectionPolicy policy)
                ? policy
                : BasisDirectConnectionPolicy.Ask;
        }

        /// <summary>
        /// Set (or clear, when <paramref name="policy"/> is Ask) the saved policy for a UUID.
        /// </summary>
        public static void SetPolicy(string uuid, BasisDirectConnectionPolicy policy)
        {
            if (string.IsNullOrEmpty(uuid)) return;
            EnsureCache();

            if (policy == BasisDirectConnectionPolicy.Ask)
            {
                if (!_cache.Remove(uuid)) return; // already default — nothing changed
            }
            else
            {
                if (_cache.TryGetValue(uuid, out BasisDirectConnectionPolicy existing) && existing == policy) return;
                _cache[uuid] = policy;
            }
            Save();
        }

        public static void ClearAll()
        {
            EnsureCache();
            if (_cache.Count == 0) return;
            _cache.Clear();
            Save();
        }
    }
}
