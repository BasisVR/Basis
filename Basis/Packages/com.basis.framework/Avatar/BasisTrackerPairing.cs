using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Basis.Scripts.Avatar
{
    /// <summary>
    /// Stores symmetric tracker→tracker links keyed by UniqueDeviceIdentifier so
    /// pairings survive disconnect/reconnect. When two trackers are paired the
    /// constellation classifier in BasisAvatarIKStageCalibration treats them as
    /// a single virtual tracker positioned at the midpoint of the pair, and
    /// binds the resulting role to the canonical (lower-id) device only — the
    /// partner is left unassigned.
    ///
    /// Pairings are persisted to <see cref="Application.persistentDataPath"/> as
    /// JSON so they survive process restart. The pairing service rehydrates the
    /// in-memory dictionary on first access (lazy load) and writes it back after
    /// every successful Link/Unlink. Trackers that are paired but offline stay
    /// in the file as dormant entries; when they reconnect, the pairing service's
    /// reconcile pass finds the pre-existing pairing and re-creates the merged
    /// virtual on the spot — no user re-pairing required.
    /// </summary>
    public static class BasisTrackerPairing
    {
        private const string FileName = "trackerPairings.json";

        // Lazy-evaluated so accessing persistentDataPath happens on the main
        // thread at runtime rather than at type initialization time.
        private static string _filePath;
        private static string FilePath =>
            _filePath ??= Path.Combine(Application.persistentDataPath, FileName);

        // Symmetric: every Link inserts both directions, every Unlink removes both.
        private static readonly Dictionary<string, string> _pairs = new();
        private static bool _loaded;

        /// <summary>
        /// Fired whenever a pairing is created, removed, or replaced. UI uses this
        /// to refresh dropdowns when another control changed the pairing graph.
        /// </summary>
        public static event Action OnPairingsChanged;

        public static bool IsPaired(string id)
        {
            EnsureLoaded();
            return !string.IsNullOrEmpty(id) && _pairs.ContainsKey(id);
        }

        public static bool TryGetPartner(string id, out string partnerId)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(id))
            {
                partnerId = null;
                return false;
            }
            return _pairs.TryGetValue(id, out partnerId);
        }

        /// <summary>
        /// Whichever id sorts first by ordinal comparison is the canonical half of
        /// the pair. The canonical device receives the role assignment during
        /// calibration; the partner stays free. Unpaired ids are their own canonical
        /// so calibration treats them as ordinary single trackers.
        /// </summary>
        public static bool IsCanonical(string id)
        {
            if (!TryGetPartner(id, out string partner))
            {
                return true;
            }
            return string.CompareOrdinal(id, partner) <= 0;
        }

        /// <summary>
        /// Pair two trackers. Replaces any prior pairings either side might have.
        /// Self-pairing or empty-id input is a no-op. The pair is persisted to
        /// disk so it's restored when the same trackers reconnect later.
        /// </summary>
        public static void Link(string idA, string idB)
        {
            if (string.IsNullOrEmpty(idA) || string.IsNullOrEmpty(idB) || idA == idB)
            {
                return;
            }

            EnsureLoaded();

            if (_pairs.TryGetValue(idA, out string existing) && existing == idB)
            {
                return;
            }

            UnlinkInternal(idA, raiseEvent: false, persist: false);
            UnlinkInternal(idB, raiseEvent: false, persist: false);

            _pairs[idA] = idB;
            _pairs[idB] = idA;

            Save();
            OnPairingsChanged?.Invoke();
        }

        /// <summary>
        /// Remove the pairing involving <paramref name="id"/>, if any. Symmetric:
        /// removes the partner's mapping at the same time. Persists immediately.
        /// </summary>
        public static void Unlink(string id) => UnlinkInternal(id, raiseEvent: true, persist: true);

        private static void UnlinkInternal(string id, bool raiseEvent, bool persist)
        {
            if (string.IsNullOrEmpty(id))
            {
                return;
            }

            EnsureLoaded();
            if (!_pairs.TryGetValue(id, out string partner))
            {
                return;
            }

            _pairs.Remove(id);
            _pairs.Remove(partner);

            if (persist)
            {
                Save();
            }
            if (raiseEvent)
            {
                OnPairingsChanged?.Invoke();
            }
        }

        /// <summary>
        /// Snapshot enumeration of pairings, with each pair reported once from the
        /// canonical side. Useful for diagnostics or UI summaries.
        /// </summary>
        public static IEnumerable<KeyValuePair<string, string>> EnumerateCanonicalPairs()
        {
            EnsureLoaded();
            foreach (KeyValuePair<string, string> kvp in _pairs)
            {
                if (string.CompareOrdinal(kvp.Key, kvp.Value) <= 0)
                {
                    yield return kvp;
                }
            }
        }

        // ----------------------------------------------------------------
        // Persistence
        // ----------------------------------------------------------------

        [Serializable]
        private class PairData
        {
            public string a;
            public string b;
        }

        [Serializable]
        private class PairingsFile
        {
            public List<PairData> pairs = new List<PairData>();
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
                PairingsFile data = JsonUtility.FromJson<PairingsFile>(json);
                if (data?.pairs == null) return;
                for (int i = 0; i < data.pairs.Count; i++)
                {
                    PairData p = data.pairs[i];
                    if (p == null) continue;
                    if (string.IsNullOrEmpty(p.a) || string.IsNullOrEmpty(p.b)) continue;
                    if (p.a == p.b) continue;
                    // Last write wins — replace any prior in-memory mapping. Both
                    // directions inserted so the symmetric invariant holds even
                    // if the file was hand-edited and only contains one side.
                    _pairs[p.a] = p.b;
                    _pairs[p.b] = p.a;
                }
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"[BasisTrackerPairing] Failed to load {FilePath}: {e}");
            }
        }

        private static void Save()
        {
            try
            {
                PairingsFile data = new PairingsFile();
                // Each pair is symmetric in _pairs (A→B and B→A). Save only the
                // canonical-side entry so the file isn't twice as big as needed.
                HashSet<string> seen = new HashSet<string>();
                foreach (KeyValuePair<string, string> kvp in _pairs)
                {
                    if (seen.Contains(kvp.Key)) continue;
                    if (string.CompareOrdinal(kvp.Key, kvp.Value) > 0) continue;
                    seen.Add(kvp.Key);
                    seen.Add(kvp.Value);
                    data.pairs.Add(new PairData { a = kvp.Key, b = kvp.Value });
                }
                string json = JsonUtility.ToJson(data, true);
                File.WriteAllText(FilePath, json);
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"[BasisTrackerPairing] Failed to save {FilePath}: {e}");
            }
        }
    }
}
