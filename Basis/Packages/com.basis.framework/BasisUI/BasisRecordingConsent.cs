using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Basis.BasisUI
{
    /// <summary>
    /// Per-person policy for handling an incoming voice-record request.
    /// </summary>
    public enum BasisRecordingConsentPolicy
    {
        /// <summary>Prompt the user each time (default for unknown people).</summary>
        Ask,
        /// <summary>Auto-grant without prompting.</summary>
        AlwaysAllow,
        /// <summary>Auto-deny without prompting.</summary>
        AlwaysDeny,
    }

    /// <summary>
    /// Persisted per-UUID policy for incoming voice-record requests. Mirrors
    /// <see cref="BasisTrustedConnections"/>: a JSON file in persistentDataPath, cached in
    /// memory. Only non-default (non-Ask) policies are stored; everyone else falls back to
    /// <see cref="BasisRecordingConsentPolicy.Ask"/>.
    /// </summary>
    public static class BasisRecordingConsent
    {
        private const string FileName = "recordingConsent.json";
        private static readonly string FilePath = Path.Combine(Application.persistentDataPath, FileName);

        private static Dictionary<string, BasisRecordingConsentPolicy> _cache;

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
            _cache = new Dictionary<string, BasisRecordingConsentPolicy>(StringComparer.OrdinalIgnoreCase);

            if (!File.Exists(FilePath)) return;
            try
            {
                Data data = JsonUtility.FromJson<Data>(File.ReadAllText(FilePath));
                if (data?.entries == null) return;
                foreach (Entry e in data.entries)
                {
                    if (string.IsNullOrEmpty(e?.uuid)) continue;
                    if (Enum.TryParse(e.policy, out BasisRecordingConsentPolicy policy) &&
                        policy != BasisRecordingConsentPolicy.Ask)
                    {
                        _cache[e.uuid] = policy;
                    }
                }
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"[BasisRecordingConsent] Failed to load {FilePath}: {ex}");
            }
        }

        private static void Save()
        {
            try
            {
                Data data = new Data();
                foreach (KeyValuePair<string, BasisRecordingConsentPolicy> kv in _cache)
                {
                    data.entries.Add(new Entry { uuid = kv.Key, policy = kv.Value.ToString() });
                }
                File.WriteAllText(FilePath, JsonUtility.ToJson(data, true));
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"[BasisRecordingConsent] Failed to save {FilePath}: {ex}");
            }
            OnChanged?.Invoke();
        }

        /// <summary>Saved policy for a UUID, or <see cref="BasisRecordingConsentPolicy.Ask"/> if none.</summary>
        public static BasisRecordingConsentPolicy GetPolicy(string uuid)
        {
            if (string.IsNullOrEmpty(uuid)) return BasisRecordingConsentPolicy.Ask;
            EnsureCache();
            return _cache.TryGetValue(uuid, out BasisRecordingConsentPolicy policy)
                ? policy
                : BasisRecordingConsentPolicy.Ask;
        }

        /// <summary>
        /// Set (or clear, when <paramref name="policy"/> is Ask) the saved policy for a UUID.
        /// </summary>
        public static void SetPolicy(string uuid, BasisRecordingConsentPolicy policy)
        {
            if (string.IsNullOrEmpty(uuid)) return;
            EnsureCache();

            if (policy == BasisRecordingConsentPolicy.Ask)
            {
                if (!_cache.Remove(uuid)) return;
            }
            else
            {
                if (_cache.TryGetValue(uuid, out BasisRecordingConsentPolicy existing) && existing == policy) return;
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
