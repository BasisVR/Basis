using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Basis.Localization;

namespace Basis.Editor.Localization
{
    /// <summary>
    /// Editor-time twin of <c>Basis.BasisUI.BasisLocalization</c>. Same key/value
    /// JSON schema, same Get/SetLanguage surface — but loads tables straight from
    /// disk via <see cref="AssetDatabase"/> because editor UI runs before the
    /// Addressables catalog is built and the runtime path can't be relied on
    /// during authoring.
    ///
    /// <para>Language JSON files live under
    /// <c>Packages/com.basis.sdk/Localization/Languages/{code}.json</c>; dropping
    /// a new file in there is enough to make it appear — no Addressables
    /// bookkeeping, no menu item to run.</para>
    /// </summary>
    public static class BasisEditorLocalization
    {
        public const string DefaultLanguage = "en";
        public const string LanguagesFolder = "Packages/com.basis.sdk/Localization/Languages";
        private const string EditorPrefsLanguageKey = "Basis.Editor.Localization.Language";

        private static readonly Dictionary<string, string> _fallback = new();
        private static readonly Dictionary<string, string> _current = new();
        private static readonly Dictionary<string, Dictionary<string, string>> _allTables = new(StringComparer.OrdinalIgnoreCase);
        private static readonly List<LanguageOption> _available = new();
        private static readonly HashSet<string> _missingKeys = new();

        private static bool _initialized;
        private static string _currentLanguage = DefaultLanguage;

        public static bool TrackMissingKeys = true;

        public static IReadOnlyCollection<string> MissingKeys => _missingKeys;
        public static void ClearMissingKeys() => _missingKeys.Clear();

        public static IReadOnlyCollection<string> GetActiveKeys() => _current.Keys;
        public static IReadOnlyCollection<string> GetFallbackKeys() => _fallback.Keys;

        public static event Action OnLanguageChanged;
        public static string CurrentLanguage => _currentLanguage;

        public readonly struct LanguageOption
        {
            public readonly string Code;
            public readonly string NativeName;

            public LanguageOption(string code, string nativeName)
            {
                Code = code;
                NativeName = nativeName;
            }
        }

        public static IReadOnlyList<LanguageOption> Available => _available;

        public static void Initialize()
        {
            if (_initialized)
            {
                return;
            }
            _initialized = true;

            LoadAllTables();

            string languageCode = EditorPrefs.GetString(EditorPrefsLanguageKey, string.Empty);
            if (string.IsNullOrEmpty(languageCode))
            {
                languageCode = DetectSystemLanguage();
            }

            SetLanguage(languageCode, notify: false);
        }

        private static string DetectSystemLanguage()
        {
            List<string> codes = new(_available.Count);
            for (int i = 0; i < _available.Count; i++)
            {
                codes.Add(_available[i].Code);
            }
            return BasisLocalizationCore.ResolveSystemLanguage(Application.systemLanguage, codes, DefaultLanguage);
        }

        public static void SetLanguage(string languageCode)
        {
            SetLanguage(languageCode, notify: true);
        }

        private static void SetLanguage(string languageCode, bool notify)
        {
            if (string.IsNullOrEmpty(languageCode))
            {
                languageCode = DefaultLanguage;
            }

            _current.Clear();
            if (!string.Equals(languageCode, DefaultLanguage, StringComparison.OrdinalIgnoreCase))
            {
                if (_allTables.TryGetValue(languageCode, out Dictionary<string, string> table))
                {
                    foreach (KeyValuePair<string, string> kv in table)
                    {
                        _current[kv.Key] = kv.Value;
                    }
                }
                else
                {
                    Debug.LogError($"[BasisEditorLocalization] Language table not loaded for code \"{languageCode}\" — falling back to English. Add the JSON to {LanguagesFolder} and let the AssetDatabase reimport.");
                    languageCode = DefaultLanguage;
                }
            }

            _currentLanguage = languageCode;
            EditorPrefs.SetString(EditorPrefsLanguageKey, languageCode);

            if (notify)
            {
                try
                {
                    OnLanguageChanged?.Invoke();
                }
                catch (Exception e)
                {
                    Debug.LogError($"[BasisEditorLocalization] OnLanguageChanged handler threw: {e}");
                }
            }
        }

        public static string Get(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return string.Empty;
            }

            if (!_initialized)
            {
                Initialize();
            }

            if (_current.TryGetValue(key, out string translated) && !string.IsNullOrEmpty(translated))
            {
                return translated;
            }

            if (_fallback.TryGetValue(key, out string fallback) && !string.IsNullOrEmpty(fallback))
            {
                return fallback;
            }

            if (TrackMissingKeys)
            {
                _missingKeys.Add(key);
            }

            return key;
        }

        public static string Get(string key, params object[] args)
        {
            string template = Get(key);
            return BasisLocalizationCore.Format(template, args);
        }

        private static void LoadAllTables()
        {
            _allTables.Clear();
            _fallback.Clear();
            _available.Clear();

            _available.Add(new LanguageOption(DefaultLanguage, "English"));

            if (!Directory.Exists(LanguagesFolder))
            {
                Debug.LogError($"[BasisEditorLocalization] Languages folder not found: {LanguagesFolder}. The default English fallback will be used and Get() returns raw keys.");
                return;
            }

            string[] jsonFiles = Directory.GetFiles(LanguagesFolder, "*.json");
            for (int i = 0; i < jsonFiles.Length; i++)
            {
                string unityPath = jsonFiles[i].Replace('\\', '/');
                TextAsset asset = AssetDatabase.LoadAssetAtPath<TextAsset>(unityPath);
                string text;
                if (asset != null && !string.IsNullOrEmpty(asset.text))
                {
                    text = asset.text;
                }
                else
                {
                    try
                    {
                        text = File.ReadAllText(unityPath);
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"[BasisEditorLocalization] Failed to read \"{unityPath}\": {e}");
                        continue;
                    }
                }

                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }

                BasisLanguageTable parsed;
                try
                {
                    parsed = JsonUtility.FromJson<BasisLanguageTable>(text);
                }
                catch (Exception e)
                {
                    Debug.LogError($"[BasisEditorLocalization] Failed to parse language file \"{unityPath}\": {e}");
                    continue;
                }

                if (parsed == null || string.IsNullOrEmpty(parsed.code))
                {
                    continue;
                }

                Dictionary<string, string> table = new(parsed.entries?.Count ?? 0);
                if (parsed.entries != null)
                {
                    for (int j = 0; j < parsed.entries.Count; j++)
                    {
                        BasisLanguageEntry entry = parsed.entries[j];
                        if (entry == null || string.IsNullOrEmpty(entry.key))
                        {
                            continue;
                        }

                        table[entry.key] = entry.value ?? string.Empty;
                    }
                }

                _allTables[parsed.code] = table;

                if (string.Equals(parsed.code, DefaultLanguage, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (KeyValuePair<string, string> kv in table)
                    {
                        _fallback[kv.Key] = kv.Value;
                    }
                    continue;
                }

                string nativeName = string.IsNullOrEmpty(parsed.nativeName) ? parsed.code : parsed.nativeName;
                _available.Add(new LanguageOption(parsed.code, nativeName));
            }
        }

        /// <summary>
        /// Forces a re-scan of <see cref="LanguagesFolder"/>. Useful after dropping
        /// a new JSON in or editing one outside Unity — call from a menu item if
        /// the AssetPostprocessor doesn't trigger.
        /// </summary>
        public static void ReloadTables()
        {
            LoadAllTables();
            string code = _currentLanguage;
            _initialized = true;
            SetLanguage(code, notify: true);
        }

        private class Importer : AssetPostprocessor
        {
            private static void OnPostprocessAllAssets(
                string[] importedAssets,
                string[] deletedAssets,
                string[] movedAssets,
                string[] movedFromAssetPaths)
            {
                if (!_initialized) return;
                if (TouchesLanguagesFolder(importedAssets)
                    || TouchesLanguagesFolder(deletedAssets)
                    || TouchesLanguagesFolder(movedAssets)
                    || TouchesLanguagesFolder(movedFromAssetPaths))
                {
                    ReloadTables();
                }
            }

            private static bool TouchesLanguagesFolder(string[] paths)
            {
                if (paths == null) return false;
                for (int i = 0; i < paths.Length; i++)
                {
                    string p = paths[i];
                    if (string.IsNullOrEmpty(p)) continue;
                    string normalized = p.Replace('\\', '/');
                    if (normalized.StartsWith(LanguagesFolder, StringComparison.OrdinalIgnoreCase)
                        && normalized.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                return false;
            }
        }
    }
}
