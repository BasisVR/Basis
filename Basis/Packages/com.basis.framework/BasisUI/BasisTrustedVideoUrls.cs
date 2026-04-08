using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Basis.BasisUI
{
    public static class BasisTrustedVideoUrls
    {
        private const string FileName = "trustedVideoUrls.json";
        private static readonly string FilePath = Path.Combine(Application.persistentDataPath, FileName);

        private static HashSet<string> _cachedUrls;

        public static event Action OnListChanged;

        [Serializable]
        private class TrustedUrlData
        {
            public List<string> urls = new List<string>();
        }

        private static void EnsureCache()
        {
            if (_cachedUrls != null) return;
            _cachedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(FilePath)) return;
            try
            {
                string json = File.ReadAllText(FilePath);
                TrustedUrlData data = JsonUtility.FromJson<TrustedUrlData>(json);
                if (data?.urls != null)
                {
                    for (int i = 0; i < data.urls.Count; i++)
                    {
                        if (!string.IsNullOrEmpty(data.urls[i]))
                            _cachedUrls.Add(data.urls[i]);
                    }
                }
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"[BasisTrustedVideoUrls] Failed to load {FilePath}: {e}");
            }
        }

        private static void Save()
        {
            try
            {
                TrustedUrlData data = new TrustedUrlData();
                data.urls.AddRange(_cachedUrls);
                string json = JsonUtility.ToJson(data, true);
                File.WriteAllText(FilePath, json);
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"[BasisTrustedVideoUrls] Failed to save {FilePath}: {e}");
            }
            OnListChanged?.Invoke();
        }

        public static bool IsTrusted(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            EnsureCache();
            return _cachedUrls.Contains(url);
        }

        public static void Add(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            EnsureCache();
            if (_cachedUrls.Add(url))
                Save();
        }

        public static void Remove(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            EnsureCache();
            if (_cachedUrls.Remove(url))
                Save();
        }

        public static List<string> GetAll()
        {
            EnsureCache();
            return new List<string>(_cachedUrls);
        }

        public static void ClearAll()
        {
            if (_cachedUrls == null)
                _cachedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            else
                _cachedUrls.Clear();
            Save();
        }

        public static void InvalidateCache()
        {
            _cachedUrls = null;
        }
    }
}
