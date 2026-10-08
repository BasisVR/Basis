using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace HVR.Basis.Comms
{
    [AutoStaticsCleanup]
    public static partial class HVRVixxyPersistentStore
    {
        private const string FileName = "VixxyAvatarCustomization.BAS";
        private const float FlushDebounceSeconds = 1f;

        private static Dictionary<string, float> _values;
        private static bool _dirty;
        private static float _flushAtRealtime;

        private static string FilePath => Path.Combine(Application.persistentDataPath, FileName);

        private static void EnsureLoaded()
        {
            if (_values != null) return;
            _values = new Dictionary<string, float>();
            try
            {
                var path = FilePath;
                if (!File.Exists(path)) return;

                var root = JsonUtility.FromJson<Root>(File.ReadAllText(path));
                if (root?.entries == null) return;

                foreach (var entry in root.entries)
                {
                    if (!string.IsNullOrEmpty(entry.key) &&
                        float.TryParse(entry.value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    {
                        _values[entry.key] = value;
                    }
                }
            }
            catch (Exception e)
            {
                BasisDebug.LogWarning("Vixxy customization load failed: " + e.Message);
            }
        }

        public static bool TryGet(string key, out float value)
        {
            value = 0f;
            if (string.IsNullOrEmpty(key)) return false;
            EnsureLoaded();
            return _values.TryGetValue(key, out value);
        }

        public static void Set(string key, float value, float defaultValue)
        {
            if (string.IsNullOrEmpty(key)) return;
            EnsureLoaded();

            if (Mathf.Approximately(value, defaultValue))
            {
                if (!_values.Remove(key)) return;
            }
            else
            {
                if (_values.TryGetValue(key, out var existing) && Mathf.Approximately(existing, value)) return;
                _values[key] = value;
            }

            _dirty = true;
            _flushAtRealtime = Time.realtimeSinceStartup + FlushDebounceSeconds;
        }

        public static void Tick()
        {
            if (!_dirty) return;
            if (Time.realtimeSinceStartup < _flushAtRealtime) return;
            FlushNow();
        }

        public static void FlushNow()
        {
            if (!_dirty || _values == null) return;
            _dirty = false;
            try
            {
                var root = new Root { entries = new List<Entry>(_values.Count) };
                foreach (var pair in _values)
                {
                    root.entries.Add(new Entry { key = pair.Key, value = pair.Value.ToString(CultureInfo.InvariantCulture) });
                }
                File.WriteAllText(FilePath, JsonUtility.ToJson(root));
            }
            catch (Exception e)
            {
                BasisDebug.LogWarning("Vixxy customization save failed: " + e.Message);
            }
        }

        [Serializable]
        private class Entry
        {
            public string key;
            public string value;
        }

        [Serializable]
        private class Root
        {
            public List<Entry> entries;
        }
    }
}
