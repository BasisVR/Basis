using System;
using Basis;
using UnityEngine;
using Basis.Scripts.Settings;

namespace Basis.Scripts.Avatar
{
    /// <summary>
    /// User-side blocklist of content tags. Content authors stamp tags into the bee
    /// bundle's <c>BasisBundleDescription.Tags</c>; users tick boxes here to refuse
    /// content carrying those tags. Honor system — only enforces tags creators
    /// declared, but the comparison itself is mandatory once configured.
    ///
    /// Storage is a single JSON-encoded <see cref="BasisSettingsBinding{T}"/> string
    /// because the binding layer only knows about primitives. Reads parse the JSON
    /// on demand and cache the result so the hot <see cref="IsBlocked"/> path on
    /// every avatar/scene/prop load doesn't re-parse.
    /// </summary>
    public static class BasisContentTagFilter
    {
        /// <summary>
        /// The setting key persisted via <see cref="BasisSettingsSystem"/>. Public so
        /// <c>SMModuleAvatarPerformanceLimits.IsPerfLimitKey</c> can recognize a change
        /// to this key as a reconcile trigger.
        /// </summary>
        public const string SettingKey = "contenttagblocklist";

        /// <summary>
        /// Default blocks the most-likely-to-disturb categories ("18+", "Horror",
        /// "Gore") so a brand-new user isn't surprised by them on first load. Still
        /// honor-system — only enforces tags creators declared — but a safer
        /// out-of-box opt-out beats a permissive default for first impressions.
        /// Users can clear or extend the list from the settings panel.
        /// </summary>
        private static readonly BasisSettingsBinding<string> _binding =
            new BasisSettingsBinding<string>(SettingKey, new BasisPlatformDefault<string>("{\"Tags\":[\"18+\",\"Horror\",\"Gore\"]}"));

        /// <summary>
        /// Cached parse of <see cref="_binding"/>'s JSON payload. <see cref="_cacheValid"/>
        /// flips false on every Set so the next Get reparses; the parse is amortized
        /// across many <see cref="IsBlocked"/> calls per frame.
        /// </summary>
        private static string[] _cached;
        private static bool _cacheValid;

        /// <summary>
        /// Fired when the blocked-tag list changes. Used by the perf-limits SM module
        /// to debounce a reconcile pass so already-loaded avatars re-evaluate.
        /// </summary>
        public static event Action OnChanged;

        [Serializable]
        private class TagListWrapper
        {
            public string[] Tags;
        }

        /// <summary>
        /// Current blocked tag list. Returned array is a fresh copy — mutating it does
        /// not change persisted state. Order matches insertion order.
        /// </summary>
        public static string[] GetBlockedTags()
        {
            if (_cacheValid && _cached != null)
            {
                var copy = new string[_cached.Length];
                Array.Copy(_cached, copy, _cached.Length);
                return copy;
            }

            string raw = _binding.RawValue;
            if (string.IsNullOrWhiteSpace(raw))
            {
                _cached = new string[0];
                _cacheValid = true;
                return new string[0];
            }

            try
            {
                var wrapper = JsonUtility.FromJson<TagListWrapper>(raw);
                _cached = wrapper?.Tags ?? new string[0];
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[BasisContentTagFilter] Failed to parse blocked-tag list, treating as empty. {ex.Message}");
                _cached = new string[0];
            }
            _cacheValid = true;

            var ret = new string[_cached.Length];
            Array.Copy(_cached, ret, _cached.Length);
            return ret;
        }

        /// <summary>
        /// Replace the blocked-tag list. Null/empty entries are dropped, duplicates
        /// (case-insensitive) are collapsed. Persists to disk and fires
        /// <see cref="OnChanged"/> so dependent systems can reconcile.
        /// </summary>
        public static void SetBlockedTags(string[] tags)
        {
            string[] sanitized = Sanitize(tags);
            string json = sanitized.Length == 0
                ? string.Empty
                : JsonUtility.ToJson(new TagListWrapper { Tags = sanitized });

            _cached = sanitized;
            _cacheValid = true;
            _binding.SetValue(json);
            OnChanged?.Invoke();
        }

        /// <summary>
        /// Re-reads the persisted blocklist after the settings file has loaded. The binding
        /// self-loads at static init, which can run before BasisSettingsSystem has read
        /// settingsConfig.json — leaving RawValue (and the parse cache) on the default
        /// blocklist for the whole session. Called from BasisSettingsDefaults.LoadAll.
        /// </summary>
        public static void ReloadBinding()
        {
            string before = _binding.RawValue;
            _binding.LoadBindingValue();
            if (!string.Equals(before, _binding.RawValue, StringComparison.Ordinal))
            {
                _cacheValid = false;
                OnChanged?.Invoke();
            }
        }

        /// <summary>
        /// True if the user has at least one tag on their blocklist. Cheap so the
        /// gate can short-circuit before iterating the bundle's tags.
        /// </summary>
        public static bool AnyBlocked()
        {
            EnsureCache();
            return _cached.Length > 0;
        }

        /// <summary>
        /// True if any tag in <paramref name="contentTags"/> is on the user's
        /// blocklist. Case-insensitive match. Null or empty content tags pass.
        /// </summary>
        public static bool IsBlocked(string[] contentTags, out string matchedTag)
        {
            matchedTag = null;
            if (contentTags == null || contentTags.Length == 0) return false;
            EnsureCache();
            if (_cached.Length == 0) return false;

            for (int i = 0; i < contentTags.Length; i++)
            {
                string tag = contentTags[i];
                if (string.IsNullOrEmpty(tag)) continue;
                for (int j = 0; j < _cached.Length; j++)
                {
                    if (string.Equals(tag, _cached[j], StringComparison.OrdinalIgnoreCase))
                    {
                        matchedTag = tag;
                        return true;
                    }
                }
            }
            return false;
        }

        private static void EnsureCache()
        {
            if (_cacheValid && _cached != null) return;
            GetBlockedTags(); // populates the cache as a side effect
        }

        private static string[] Sanitize(string[] tags)
        {
            if (tags == null || tags.Length == 0) return new string[0];
            // Two-pass: first count survivors, then fill. Avoids a List allocation in
            // the editor-driven hot path of repeatedly setting on every keystroke.
            int kept = 0;
            for (int i = 0; i < tags.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(tags[i])) continue;
                string trimmed = tags[i].Trim();
                bool dup = false;
                for (int j = 0; j < i; j++)
                {
                    string prior = tags[j];
                    if (prior == null) continue;
                    if (string.Equals(prior.Trim(), trimmed, StringComparison.OrdinalIgnoreCase))
                    {
                        dup = true;
                        break;
                    }
                }
                if (!dup) kept++;
            }
            if (kept == 0) return new string[0];

            var result = new string[kept];
            int k = 0;
            for (int i = 0; i < tags.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(tags[i])) continue;
                string trimmed = tags[i].Trim();
                bool dup = false;
                for (int j = 0; j < i; j++)
                {
                    string prior = tags[j];
                    if (prior == null) continue;
                    if (string.Equals(prior.Trim(), trimmed, StringComparison.OrdinalIgnoreCase))
                    {
                        dup = true;
                        break;
                    }
                }
                if (!dup) result[k++] = trimmed;
            }
            return result;
        }
    }
}
