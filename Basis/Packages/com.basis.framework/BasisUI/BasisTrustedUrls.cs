using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Basis.BasisUI
{
    public static class BasisTrustedUrls
    {
        private const string DefaultsAddress = "BasisDefaultTrustedUrls";

        private const string FileName = "trustedUrls.json";
        private const string LegacyFileName = "trustedVideoUrls.json";
        private static readonly string FilePath = Path.Combine(Application.persistentDataPath, FileName);
        private static readonly string LegacyFilePath = Path.Combine(Application.persistentDataPath, LegacyFileName);

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
            if (!File.Exists(FilePath) && File.Exists(LegacyFilePath))
            {
                try { File.Move(LegacyFilePath, FilePath); }
                catch (Exception e) { BasisDebug.LogError($"[BasisTrustedUrls] Failed to migrate {LegacyFilePath} to {FilePath}: {e}"); }
            }
            if (File.Exists(FilePath)) {
                try
                {
                    string json = File.ReadAllText(FilePath);
                    TrustedUrlData data = JsonUtility.FromJson<TrustedUrlData>(json);
                    if (data?.urls != null)
                    {
                        for (int i = 0; i < data.urls.Count; i++)
                        {
                            if (string.IsNullOrEmpty(data.urls[i])) continue;
                            if (!data.urls[i].StartsWith("https://")) continue;
                            _cachedUrls.Add(data.urls[i]);
                        }
                    }
                }
                catch (Exception e)
                {
                    BasisDebug.LogError($"[BasisTrustedUrls] Failed to load {FilePath}: {e}");
                }
            } else
            {
                Reset();
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
                BasisDebug.LogError($"[BasisTrustedUrls] Failed to save {FilePath}: {e}");
            }
            OnListChanged?.Invoke();
        }

        public static bool IsTrusted(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            EnsureCache();
            foreach (string trustedUrl in _cachedUrls)
            {
                if (MatchesWithWildcards(url, trustedUrl))
                    return true;
            }
            return false;
        }

        private static bool MatchesWithWildcards(string url, string pattern)
        {
            // Convert wildcard pattern to regex-like matching
            string regexPattern = "^" + Regex.Escape(pattern)
                .Replace("\\*", ".*") + "$";
            return Regex.IsMatch(url, regexPattern, 
                RegexOptions.IgnoreCase);
        }

        public static void Add(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            if (!url.StartsWith("https://")) return;
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

        public static void Reset()
        {
            ClearAll();
            BasisDefaultTrustedUrlsAsset defaults = LoadDefaults();
            if (defaults != null && defaults.Urls != null)
            {
                foreach (string url in defaults.Urls)
                {
                    if (string.IsNullOrEmpty(url)) continue;
                    if (!url.StartsWith("https://")) continue;
                    _cachedUrls.Add(url);
                }
            }
            Save();
        }

        private static BasisDefaultTrustedUrlsAsset LoadDefaults()
        {
            AsyncOperationHandle<BasisDefaultTrustedUrlsAsset> handle =
                Addressables.LoadAssetAsync<BasisDefaultTrustedUrlsAsset>(DefaultsAddress);
            BasisDefaultTrustedUrlsAsset asset = handle.WaitForCompletion();
            if (asset == null)
            {
                BasisDebug.LogError($"[BasisTrustedUrls] Could not load defaults asset at address \"{DefaultsAddress}\".");
            }
            return asset;
        }

        public static void InvalidateCache()
        {
            _cachedUrls = null;
        }
    }
}
