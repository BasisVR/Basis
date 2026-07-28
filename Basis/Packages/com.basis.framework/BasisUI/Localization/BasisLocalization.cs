using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using Basis.Localization;
using Basis.Scripts.Settings;

namespace Basis.BasisUI
{
    /// <summary>
    /// Lightweight key-value localization for Basis UI.
    ///
    /// Language JSON files are loaded via Addressables using the
    /// <see cref="LanguageLabel"/> label and use the same KeyValue list format
    /// as BasisSettingsSystem, so they round-trip through
    /// UnityEngine.JsonUtility without custom parsing. Going through Addressables
    /// (instead of File.IO against StreamingAssets) is required because on
    /// Android the APK packs StreamingAssets into a compressed archive and
    /// Directory.GetFiles silently returns nothing — language discovery was
    /// broken on mobile builds.
    ///
    /// Any package — not just com.basis.framework — can contribute strings:
    /// every JSON tagged with the <see cref="LanguageLabel"/> label is loaded,
    /// and files that share a <c>code</c> are merged by key into one table, so a
    /// package ships its own <c>{code}.json</c> with namespaced keys and
    /// <see cref="Get(string)"/> resolves them from the same flat namespace. No
    /// per-package API is needed; <see cref="Get(string)"/> is not specialized.
    ///
    /// Japanese (and other CJK languages) require a TMP font asset that includes
    /// the relevant glyph set. The default Basis TMP font only ships with Latin
    /// characters, so a CJK-capable fallback font must be assigned to
    /// TMP_Settings.fallbackFontAssets (or to the individual text labels) for
    /// translated strings to render correctly.
    /// </summary>
    public static class BasisLocalization
    {
        public const string DefaultLanguage = "en";
        public const string LanguageLabel = "language";

        private static readonly Dictionary<string, string> _fallback = new();
        private static readonly Dictionary<string, string> _current = new();
        private static readonly Dictionary<string, Dictionary<string, string>> _allTables = new(StringComparer.OrdinalIgnoreCase);
        private static readonly List<LanguageOption> _available = new();
        private static readonly HashSet<string> _missingKeys = new();

        private static bool _initialized;
        private static string _currentLanguage = DefaultLanguage;

        /// <summary>
        /// When enabled, every key that falls all the way through to the raw-key
        /// return path is recorded in <see cref="MissingKeys"/>. The editor
        /// window reads this to highlight unconfigured strings at runtime; no-op
        /// cost in release builds is one branch per Get call.
        /// </summary>
        public static bool TrackMissingKeys = true;

        /// <summary>
        /// Keys that were requested via <see cref="Get(string)"/> but had no
        /// value in either the active or fallback language. Populated only
        /// when <see cref="TrackMissingKeys"/> is true.
        /// </summary>
        public static IReadOnlyCollection<string> MissingKeys => _missingKeys;

        /// <summary>
        /// Clears the <see cref="MissingKeys"/> set. Useful after fixing a
        /// batch of translations so the next runtime pass starts clean.
        /// </summary>
        public static void ClearMissingKeys() => _missingKeys.Clear();

        /// <summary>
        /// Keys loaded from the active language table. Does not include
        /// fallback-only keys; use <see cref="GetFallbackKeys"/> for that.
        /// Intended for editor tooling.
        /// </summary>
        public static IReadOnlyCollection<string> GetActiveKeys() => _current.Keys;

        /// <summary>
        /// Keys loaded from the English fallback table. Intended for editor
        /// tooling that wants to compute "missing from translation" diffs.
        /// </summary>
        public static IReadOnlyCollection<string> GetFallbackKeys() => _fallback.Keys;

        /// <summary>
        /// Fires whenever the active language changes. UI code that caches
        /// resolved strings can re-resolve in response.
        /// </summary>
        public static event Action OnLanguageChanged;

        /// <summary>
        /// Current active language code (e.g. "en", "ja").
        /// </summary>
        public static string CurrentLanguage => _currentLanguage;

        /// <summary>
        /// Display metadata for a language that has a JSON file on disk.
        /// </summary>
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

        /// <summary>
        /// Languages discovered on disk. Always includes English as the first
        /// entry (from the embedded fallback) even if the file is missing.
        /// </summary>
        public static IReadOnlyList<LanguageOption> Available => _available;

        /// <summary>
        /// Loads the fallback (English) table and selects the active language.
        /// On first run (no persisted "language" key) the OS locale is probed
        /// via <see cref="Application.systemLanguage"/>; on subsequent runs the
        /// user's saved choice is honored even if it differs from the OS.
        /// Safe to call more than once — subsequent calls are no-ops.
        /// </summary>
        public static void Initialize()
        {
            if (_initialized)
            {
                return;
            }
            _initialized = true;

            LoadAllTables();

            var languageCode = BasisSettingsSystem.LoadString("language", DefaultLanguage);
            if (string.IsNullOrEmpty(languageCode))
                languageCode = DetectSystemLanguage();

            SetLanguage(languageCode, notify: false);
        }

        /// <summary>
        /// Maps <see cref="Application.systemLanguage"/> to one of the
        /// language codes found in the Addressable catalog. Unknown or
        /// unavailable system languages fall back to <see cref="DefaultLanguage"/>,
        /// so dropping a new language file into the Addressable Languages
        /// folder is enough to make auto-detection pick it up.
        /// </summary>
        private static string DetectSystemLanguage()
        {
            List<string> codes = new(_available.Count);
            for (int i = 0; i < _available.Count; i++)
            {
                codes.Add(_available[i].Code);
            }
            return BasisLocalizationCore.ResolveSystemLanguage(Application.systemLanguage, codes, DefaultLanguage);
        }

        /// <summary>
        /// Switches the active language, loads its table, persists the choice,
        /// and fires <see cref="OnLanguageChanged"/>.
        /// </summary>
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
                    BasisDebug.LogError($"[BasisLocalization] Language table not loaded for code \"{languageCode}\" — falling back to English. Check that the JSON file is in an Addressable group with the \"{LanguageLabel}\" label and that the Addressables content has been built.");
                    languageCode = DefaultLanguage;
                }
            }

            _currentLanguage = languageCode;
            BasisSettingsSystem.SaveString("language", languageCode);

            if (notify)
            {
                try
                {
                    OnLanguageChanged?.Invoke();
                }
                catch (Exception e)
                {
                    BasisDebug.LogError($"[BasisLocalization] OnLanguageChanged handler threw: {e}");
                }
            }
        }

        /// <summary>
        /// Resolves a key to the active language. Falls back to English, then
        /// to the key itself so missing translations are visible instead of
        /// silently blank.
        /// </summary>
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

        /// <summary>
        /// Formatted variant of <see cref="Get(string)"/>. Uses the
        /// invariant culture so numeric formatting stays consistent with the
        /// rest of BasisSettingsSystem.
        /// </summary>
        public static string Get(string key, params object[] args)
        {
            string template = Get(key);
            return BasisLocalizationCore.Format(template, args);
        }

        /// <summary>
        /// Loads every language TextAsset tagged with the
        /// <see cref="LanguageLabel"/> Addressable label, parses each, and
        /// caches the resulting tables plus the available-language dropdown.
        ///
        /// <para>Uses <c>WaitForCompletion</c> intentionally: language data is
        /// local, tiny, and must be ready before any UI renders. The content
        /// is copied into managed dictionaries so the Addressable handle is
        /// released immediately after parsing.</para>
        /// </summary>
        private static void LoadAllTables()
        {
            _allTables.Clear();
            _fallback.Clear();
            _available.Clear();

            // English is always present even if its asset is missing, so
            // Get(key) falls back to the raw key instead of returning empty.
            _available.Add(new LanguageOption(DefaultLanguage, "English"));

            AsyncOperationHandle<IList<TextAsset>> handle = Addressables.LoadAssetsAsync<TextAsset>(LanguageLabel, null);
            IList<TextAsset> assets;
            try
            {
                assets = handle.WaitForCompletion();
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"[BasisLocalization] Failed to load language Addressables (label \"{LanguageLabel}\"): {e}");
                if (handle.IsValid())
                {
                    Addressables.Release(handle);
                }
                return;
            }

            if (handle.Status != AsyncOperationStatus.Succeeded || assets == null || assets.Count == 0)
            {
                BasisDebug.LogError($"[BasisLocalization] No assets found for Addressable label \"{LanguageLabel}\". Run \"Basis/Settings/Localization/Register Languages as Addressable\" and rebuild Addressables content.");
                if (handle.IsValid())
                {
                    Addressables.Release(handle);
                }
                return;
            }

            for (int i = 0; i < assets.Count; i++)
            {
                TextAsset asset = assets[i];
                if (asset == null || string.IsNullOrEmpty(asset.text))
                {
                    continue;
                }

                BasisLanguageTable parsed;
                try
                {
                    parsed = JsonUtility.FromJson<BasisLanguageTable>(asset.text);
                }
                catch (Exception e)
                {
                    BasisDebug.LogError($"[BasisLocalization] Failed to parse language asset \"{asset.name}\": {e}");
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

                if (!_allTables.TryGetValue(parsed.code, out Dictionary<string, string> merged))
                {
                    merged = new Dictionary<string, string>(table.Count);
                    _allTables[parsed.code] = merged;
                }
                foreach (KeyValuePair<string, string> kv in table)
                {
                    merged[kv.Key] = kv.Value;
                }

                if (string.Equals(parsed.code, DefaultLanguage, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (KeyValuePair<string, string> kv in table)
                    {
                        _fallback[kv.Key] = kv.Value;
                    }
                    continue;
                }

                bool alreadyListed = false;
                for (int a = 0; a < _available.Count; a++)
                {
                    if (string.Equals(_available[a].Code, parsed.code, StringComparison.OrdinalIgnoreCase))
                    {
                        alreadyListed = true;
                        break;
                    }
                }
                if (!alreadyListed)
                {
                    string nativeName = string.IsNullOrEmpty(parsed.nativeName) ? parsed.code : parsed.nativeName;
                    _available.Add(new LanguageOption(parsed.code, nativeName));
                }
            }

            Addressables.Release(handle);
        }
    }
}
