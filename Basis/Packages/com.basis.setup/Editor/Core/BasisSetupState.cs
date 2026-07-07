using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Basis.Setup
{
    internal static class BasisSetupState
    {
        private const string StatePath = "ProjectSettings/BasisSetup.json";

        [Serializable]
        private class Entry
        {
            public string key;
            public int version;
        }

        [Serializable]
        private class Store
        {
            public List<Entry> entries = new List<Entry>();
        }

        private static Store _cache;

        private static Store Load()
        {
            if (_cache != null)
            {
                return _cache;
            }

            if (File.Exists(StatePath))
            {
                try
                {
                    _cache = JsonUtility.FromJson<Store>(File.ReadAllText(StatePath)) ?? new Store();
                }
                catch
                {
                    _cache = new Store();
                }
            }
            else
            {
                _cache = new Store();
            }

            return _cache;
        }

        public static int GetVersion(string key)
        {
            Store store = Load();
            for (int i = 0; i < store.entries.Count; i++)
            {
                if (store.entries[i].key == key)
                {
                    return store.entries[i].version;
                }
            }

            return 0;
        }

        public static void SetVersion(string key, int version)
        {
            Store store = Load();
            for (int i = 0; i < store.entries.Count; i++)
            {
                if (store.entries[i].key == key)
                {
                    store.entries[i].version = version;
                    Save();
                    return;
                }
            }

            store.entries.Add(new Entry { key = key, version = version });
            Save();
        }

        private static void Save()
        {
            try
            {
                File.WriteAllText(StatePath, JsonUtility.ToJson(_cache, true));
            }
            catch (Exception e)
            {
                Debug.LogError($"[BasisSetup] Failed to write {StatePath}: {e.Message}");
            }
        }
    }
}
