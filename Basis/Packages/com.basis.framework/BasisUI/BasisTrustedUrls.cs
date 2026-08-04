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

        private static HashSet<string> _builtInUrls;
        private static HashSet<string> _userUrls;

        public static event Action OnListChanged;

        [Serializable]
        private class TrustedUrlData
        {
            public List<string> urls = new List<string>();
        }

        private static void EnsureCache()
        {
            if (_userUrls != null) return;

            _builtInUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            LoadBuiltIns();

            _userUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!File.Exists(FilePath) && File.Exists(LegacyFilePath))
            {
                try { File.Move(LegacyFilePath, FilePath); }
                catch (Exception e) { BasisDebug.LogError($"[BasisTrustedUrls] Failed to migrate {LegacyFilePath} to {FilePath}: {e}"); }
            }

            if (!File.Exists(FilePath)) return;

            bool rewrite = false;
            try
            {
                string json = File.ReadAllText(FilePath);
                TrustedUrlData data = JsonUtility.FromJson<TrustedUrlData>(json);
                if (data?.urls != null)
                {
                    for (int i = 0; i < data.urls.Count; i++)
                    {
                        string url = data.urls[i];
                        if (string.IsNullOrEmpty(url)) continue;
                        if (!url.StartsWith("https://")) continue;
                        // Older builds baked the built-in defaults into this file. Drop any
                        // entry already covered by a built-in so it isn't shown as user-added,
                        // and rewrite the file once to clean it up.
                        if (_builtInUrls.Contains(url)) { rewrite = true; continue; }
                        if (!_userUrls.Add(url)) rewrite = true;
                    }
                }
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"[BasisTrustedUrls] Failed to load {FilePath}: {e}");
            }

            if (rewrite && _builtInUrls.Count > 0) Save();
        }

        private static void LoadBuiltIns()
        {
            BasisDefaultTrustedUrlsAsset defaults = LoadDefaults();
            if (defaults == null || defaults.Urls == null) return;
            foreach (string url in defaults.Urls)
            {
                if (string.IsNullOrEmpty(url)) continue;
                if (!url.StartsWith("https://")) continue;
                _builtInUrls.Add(url);
            }
        }

        private static void Save()
        {
            try
            {
                TrustedUrlData data = new TrustedUrlData();
                data.urls.AddRange(_userUrls);
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
            foreach (string trustedUrl in _builtInUrls)
            {
                if (MatchesWithWildcards(url, trustedUrl))
                    return true;
            }
            foreach (string trustedUrl in _userUrls)
            {
                if (MatchesWithWildcards(url, trustedUrl))
                    return true;
            }
            return false;
        }

        // Matched per URL component, never against the raw string: a single regex over the whole
        // URL lets '*' swallow the '/', '?' and '#' delimiters.
        private static bool MatchesWithWildcards(string url, string pattern)
        {
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(pattern)) return false;
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri)) return false;

            int schemeEnd = pattern.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd <= 0) return false;
            int hostStart = schemeEnd + 3;
            int hostEnd = pattern.IndexOf('/', hostStart);

            string patternScheme = pattern.Substring(0, schemeEnd);
            string patternHost = hostEnd < 0
                ? pattern.Substring(hostStart)
                : pattern.Substring(hostStart, hostEnd - hostStart);
            string patternPath = hostEnd < 0 ? string.Empty : pattern.Substring(hostEnd);

            if (patternHost.Length == 0) return false;
            if (!string.Equals(uri.Scheme, patternScheme, StringComparison.OrdinalIgnoreCase)) return false;

            if (!ComponentMatches(uri.Host, patternHost, "[^/?#]*")) return false;

            if (patternPath.Length == 0) return true;
            return ComponentMatches(uri.PathAndQuery + uri.Fragment, patternPath, "[\\s\\S]*");
        }

        private static bool ComponentMatches(string value, string pattern, string wildcard)
        {
            string regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", wildcard) + "$";
            return Regex.IsMatch(value, regexPattern, RegexOptions.IgnoreCase);
        }

        public static void Add(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            if (!url.StartsWith("https://")) return;
            EnsureCache();
            if (_builtInUrls.Contains(url)) return;
            if (_userUrls.Add(url))
                Save();
        }

        public static void Remove(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            EnsureCache();
            if (_userUrls.Remove(url))
                Save();
        }

        public static List<string> GetAll()
        {
            EnsureCache();
            List<string> all = new List<string>(_builtInUrls.Count + _userUrls.Count);
            all.AddRange(_builtInUrls);
            all.AddRange(_userUrls);
            return all;
        }

        public static List<string> GetBuiltIn()
        {
            EnsureCache();
            return new List<string>(_builtInUrls);
        }

        public static List<string> GetUserAdded()
        {
            EnsureCache();
            return new List<string>(_userUrls);
        }

        public static void ClearAll()
        {
            EnsureCache();
            if (_userUrls.Count == 0) return;
            _userUrls.Clear();
            Save();
        }

        public static void Reset()
        {
            ClearAll();
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
            _builtInUrls = null;
            _userUrls = null;
        }
    }
}
