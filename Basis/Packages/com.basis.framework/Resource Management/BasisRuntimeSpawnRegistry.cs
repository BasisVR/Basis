using System;
using System.Collections.Generic;
using UnityEngine;

namespace Basis
{
    public static class BasisRuntimeSpawnRegistry
    {
        [Serializable]
        public class SpawnInstance
        {
            public string InstanceId;     // unique per spawn
            public string Url;
            public string LoadedNetID;    // what you pass to RequestGameObjectUnLoad
            public bool Persistent;
            public DateTime SpawnedUtc;
        }

        // URL -> instances
        private static readonly Dictionary<string, List<SpawnInstance>> _map = new();

        public static bool HasAny(string url)
            => !string.IsNullOrWhiteSpace(url)
               && _map.TryGetValue(url, out var list)
               && list != null
               && list.Count > 0;

        public static IReadOnlyList<SpawnInstance> GetInstances(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return Array.Empty<SpawnInstance>();
            return _map.TryGetValue(url, out var list) ? list : Array.Empty<SpawnInstance>();
        }

        public static void Add(string url, string loadedNetId, bool persistent, out SpawnInstance instance)
        {
            if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException(nameof(url));
            if (!_map.TryGetValue(url, out var list))
            {
                list = new List<SpawnInstance>();
                _map[url] = list;
            }

            instance = new SpawnInstance
            {
                InstanceId = Guid.NewGuid().ToString("N"),
                Url = url,
                LoadedNetID = loadedNetId,
                Persistent = persistent,
                SpawnedUtc = DateTime.UtcNow
            };

            list.Add(instance);
        }

        public static bool RemoveInstance(string url, string instanceId, out SpawnInstance removed)
        {
            removed = null;
            if (!_map.TryGetValue(url, out var list) || list == null) return false;

            int idx = list.FindIndex(x => x != null && x.InstanceId == instanceId);
            if (idx < 0) return false;

            removed = list[idx];
            list.RemoveAt(idx);

            if (list.Count == 0) _map.Remove(url);
            return true;
        }

        public static bool TryGetAny(string url, out SpawnInstance instance)
        {
            instance = null;
            if (!_map.TryGetValue(url, out var list) || list == null || list.Count == 0) return false;
            instance = list[list.Count - 1]; // e.g. most recent
            return true;
        }

        public static void ClearAll(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            _map.Remove(url);
        }
        public static int Count(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return 0;
            return _map.TryGetValue(url, out var list) && list != null
                ? list.Count
                : 0;
        }
    }
}
