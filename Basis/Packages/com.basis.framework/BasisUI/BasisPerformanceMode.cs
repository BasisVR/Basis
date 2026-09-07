using System;
using System.Collections.Generic;
using System.Globalization;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Settings;
using UnityEngine;

namespace Basis.BasisUI
{

    /// <summary>
    /// Reversible, multi-level Performance Mode.
    ///
    /// <para>The mode owns a table of graphics / crowd / avatar-cost settings. Turning it
    /// on snapshots the player's own values, then writes the level's preset over the top;
    /// turning it off writes the snapshot back. Nothing is lost, so the mode can be flipped
    /// from the hotbar or the Graphics tab at any time, including mid-instance.</para>
    ///
    /// <para>A preset value is never applied verbatim: it is combined with the snapshot so
    /// the result is the cheaper of the two. A player who already runs tighter limits than
    /// the preset keeps their own, and no level can ever make anything more expensive than
    /// it was before the mode was switched on.</para>
    ///
    /// <para>Edits the player makes to a controlled setting while the mode is on are folded
    /// back into the snapshot, so they survive turning the mode off again.</para>
    /// </summary>
    public static class BasisPerformanceMode
    {
        public const string LevelOff = "Off";
        public const string LevelLight = "Light";
        public const string LevelBalanced = "Balanced";
        public const string LevelAggressive = "Aggressive";

        /// <summary>Occupant counts (local player included) that arm Light / Balanced / Aggressive.</summary>
        public static readonly int[] PopulationThresholds = { 250, 500, 1000 };

        private const float DeEscalateMargin = 0.9f;
        private const int AutoFrameGateMask = 31;

        private sealed class FloatRule
        {
            public BasisSettingsBinding<float> Binding;
            public float[] ByLevel;
            public bool HigherIsCheaper;
            public bool ZeroIsUnlimited;
        }

        private sealed class BoolRule
        {
            public BasisSettingsBinding<bool> Binding;
            public bool?[] ByLevel;
        }

        private sealed class TierRule
        {
            public BasisSettingsBinding<string> Binding;
            public string[] CheapestFirst;
            public string[] ByLevel;
        }

        [Serializable]
        private sealed class BaselineEntry
        {
            public string Key;
            public string Value;
        }

        [Serializable]
        private sealed class BaselineStore
        {
            public List<BaselineEntry> Entries = new List<BaselineEntry>();
        }

        private static readonly string[] QualityTiers = { "Very Low", "Low", "Medium", "High", "Ultra" };
        private static readonly string[] AntialiasingTiers = { "Off", "MSAA 2X", "MSAA 4X", "MSAA 8X" };

        private static FloatRule[] _floatRules;
        private static BoolRule[] _boolRules;
        private static TierRule[] _tierRules;

        private static readonly Dictionary<string, string> _baseline = new Dictionary<string, string>();
        private static bool _initialized;
        private static bool _applying;
        private static bool _subscribed;
        private static bool _baselineDirty;
        private static int _autoFrameGate;

        // Per-app-run only (like BasisHighPlayerCapPerformanceMode's own tier-asked flags) — whether
        // the current non-Off level was armed by the crowd system (the prompt, via
        // SetLevelFromPopulation) rather than picked by hand from the Graphics tab. Only that case is
        // safe to relax automatically: it's just handing the player their own settings back, not
        // overriding a manual choice. _pendingPopulationArm is the one-shot handoff into ApplyLevel,
        // since a binding change only carries the new value, not who asked for it.
        private static bool _populationArmed;
        private static bool _pendingPopulationArm;

        public static BasisPerformanceLevel ActiveLevel { get; private set; }

        public static bool IsActive => ActiveLevel != BasisPerformanceLevel.Off;

        /// <summary>Raised after a level change has finished applying.</summary>
        public static event Action<BasisPerformanceLevel> OnLevelChanged;

        /// <summary>
        /// Level the population tiers would pick for <paramref name="occupants"/>, ignoring
        /// hysteresis. Thresholds are exclusive, so "over 250" means 251 occupants.
        /// </summary>
        public static BasisPerformanceLevel LevelForPopulation(int occupants)
        {
            for (int i = PopulationThresholds.Length - 1; i >= 0; i--)
            {
                if (occupants > PopulationThresholds[i])
                {
                    return (BasisPerformanceLevel)(i + 1);
                }
            }
            return BasisPerformanceLevel.Off;
        }

        public static string LevelToId(BasisPerformanceLevel level)
        {
            switch (level)
            {
                case BasisPerformanceLevel.Light: return LevelLight;
                case BasisPerformanceLevel.Balanced: return LevelBalanced;
                case BasisPerformanceLevel.Aggressive: return LevelAggressive;
                default: return LevelOff;
            }
        }

        public static BasisPerformanceLevel IdToLevel(string id)
        {
            if (string.Equals(id, LevelLight, StringComparison.OrdinalIgnoreCase)) return BasisPerformanceLevel.Light;
            if (string.Equals(id, LevelBalanced, StringComparison.OrdinalIgnoreCase)) return BasisPerformanceLevel.Balanced;
            if (string.Equals(id, LevelAggressive, StringComparison.OrdinalIgnoreCase)) return BasisPerformanceLevel.Aggressive;
            return BasisPerformanceLevel.Off;
        }

        public static string LocalizationKeyFor(BasisPerformanceLevel level)
        {
            switch (level)
            {
                case BasisPerformanceLevel.Light: return "settings.performanceMode.level.light";
                case BasisPerformanceLevel.Balanced: return "settings.performanceMode.level.balanced";
                case BasisPerformanceLevel.Aggressive: return "settings.performanceMode.level.aggressive";
                default: return "settings.performanceMode.level.off";
            }
        }

        public static string DisplayName(BasisPerformanceLevel level)
            => BasisLocalization.Get(LocalizationKeyFor(level));

        /// <summary>
        /// Occupant count that arms <paramref name="level"/> — the tier the crowd prompt offers it
        /// at, and the count Follow Player Count switches to it on. 0 for <see cref="BasisPerformanceLevel.Off"/>.
        /// </summary>
        public static int ThresholdFor(BasisPerformanceLevel level)
        {
            int index = (int)level - 1;
            return index >= 0 && index < PopulationThresholds.Length ? PopulationThresholds[index] : 0;
        }

        /// <summary>
        /// Level name carrying its activation point, e.g. "Light (250+)". Off has no threshold and
        /// comes back as the plain name.
        /// </summary>
        public static string DisplayNameWithThreshold(BasisPerformanceLevel level)
        {
            if (level == BasisPerformanceLevel.Off)
            {
                return DisplayName(level);
            }

            return BasisLocalization.Get("settings.performanceMode.level.withThreshold",
                DisplayName(level), ThresholdFor(level));
        }

        /// <summary>
        /// Applies <paramref name="level"/>, snapshotting the player's own values on the way
        /// in and restoring them when the level lands on <see cref="BasisPerformanceLevel.Off"/>.
        /// </summary>
        public static void SetLevel(BasisPerformanceLevel level)
        {
            EnsureInitialized();

            // The batch takes in the level key's own write alongside the preset it triggers, so
            // the whole switch is one save and one quality refresh. See ApplyLevel.
            using (BasisSettingsSystem.Batch())
            {
                string id = LevelToId(level);
                if (string.Equals(BasisSettingsDefaults.PerformanceModeLevel.RawValue, id, StringComparison.OrdinalIgnoreCase))
                {
                    ApplyLevel(level);
                    return;
                }

                BasisSettingsDefaults.PerformanceModeLevel.SetValue(id);
            }
        }

        /// <summary>
        /// Same as <see cref="SetLevel"/>, but marks the resulting level as crowd-armed so
        /// <see cref="Simulate"/> will relax it back down on its own once the population that
        /// justified it has thinned out again — used by the accept button on the crowd prompt.
        /// </summary>
        internal static void SetLevelFromPopulation(BasisPerformanceLevel level)
        {
            _pendingPopulationArm = true;
            SetLevel(level);
        }

        /// <summary>
        /// Accent used by the Graphics page to show at a glance how hard the mode is currently
        /// cutting. White when off, so the section keeps its normal styling.
        /// </summary>
        public static Color AccentColor(BasisPerformanceLevel level)
        {
            switch (level)
            {
                case BasisPerformanceLevel.Light: return BasisPanelTint.Calm;
                case BasisPerformanceLevel.Balanced: return BasisPanelTint.Caution;
                case BasisPerformanceLevel.Aggressive: return BasisPanelTint.Hot;
                default: return Color.white;
            }
        }

        /// <summary>
        /// Pumped from the central tick. Drives the automatic level when
        /// <see cref="BasisSettingsDefaults.PerformanceModeAuto"/> is on; otherwise, if the active
        /// level was armed by the crowd prompt rather than picked by hand, still relaxes it back
        /// down (never up — that still needs a fresh prompt) as the population that justified it
        /// thins out, so leaving a busy instance or watching it empty out both settle back to
        /// normal on their own.
        /// </summary>
        public static void Simulate()
        {
            if (!_initialized)
            {
                if (!BasisSettingsSystem.SettingsLoaded)
                {
                    return;
                }
                EnsureInitialized();
            }

            // Ahead of the auto gate: edits the player makes to a controlled setting while the mode
            // is on are folded into the snapshot from a change event, and this is where that lands
            // on disk. It has to run whether or not the level is following the population.
            FlushBaseline();

            bool auto = BasisSettingsDefaults.PerformanceModeAuto.RawValue;
            bool relaxOnly = !auto && _populationArmed && ActiveLevel != BasisPerformanceLevel.Off;
            if (!auto && !relaxOnly)
            {
                return;
            }

            if ((++_autoFrameGate & AutoFrameGateMask) != 0)
            {
                return;
            }

            BasisPerformanceLevel next = EvaluateAuto(SafePlayerCount(), ActiveLevel);
            if (relaxOnly ? next < ActiveLevel : next != ActiveLevel)
            {
                // Re-affirm crowd provenance on the way into ApplyLevel: a multi-step relax (e.g.
                // Aggressive -> Balanced -> Light -> Off as the instance keeps emptying) needs
                // _populationArmed to survive each intermediate SetLevel, or it would stall after
                // one step. Landing on Off clears it regardless, so marking it unconditionally here
                // is safe.
                _pendingPopulationArm = true;
                SetLevel(next);
            }
        }

        /// <summary>
        /// Re-evaluates the automatic level immediately instead of waiting for the next gated
        /// tick. Used when the player turns automatic following on from the settings page.
        /// </summary>
        public static void ApplyAutoNow()
        {
            EnsureInitialized();

            if (!BasisSettingsDefaults.PerformanceModeAuto.RawValue)
            {
                return;
            }

            BasisPerformanceLevel next = EvaluateAuto(SafePlayerCount(), ActiveLevel);
            if (next != ActiveLevel)
            {
                SetLevel(next);
            }
        }

        /// <summary>
        /// Headline values a level resolves to right now, for prompt and status copy.
        /// </summary>
        public static void DescribeLevel(BasisPerformanceLevel level,
            out float avatarRange, out float maxVisibleAvatars, out float poseLod,
            out float avatarMeshLodPercent)
        {
            EnsureInitialized();

            avatarRange = PreviewFloat(BasisSettingsDefaults.AvatarRange, level);
            maxVisibleAvatars = PreviewFloat(BasisSettingsDefaults.MaxVisibleAvatars, level);
            poseLod = PreviewFloat(BasisSettingsDefaults.PoseLOD, level);
            avatarMeshLodPercent = PreviewFloat(BasisSettingsDefaults.AvatarMeshLOD, level) * 100f;
        }

        internal static void EnsureInitialized()
        {
            if (_initialized)
            {
                return;
            }
            _initialized = true;

            BuildRules();
            LoadBaseline();

            ActiveLevel = IdToLevel(BasisSettingsDefaults.PerformanceModeLevel.RawValue);
            if (ActiveLevel == BasisPerformanceLevel.Off)
            {
                ClearBaseline();
            }
            else if (_baseline.Count == 0)
            {
                CaptureBaseline();
            }

            Subscribe();
            BasisSettingsDefaults.PerformanceModeLevel.OnChanged += OnLevelSettingChanged;
        }

        private static void OnLevelSettingChanged(string id)
        {
            ApplyLevel(IdToLevel(id));
        }

        /// <summary>
        /// A level change rewrites around fifty bindings in one go. Each write used to end in a
        /// full settings-file save, a finished-changes broadcast and
        /// <c>QualitySettings.SetQualityLevel(level, applyExpensiveChanges: true)</c> — the last of
        /// which re-uploads every texture mip. Fifty of those back to back blocked the main thread
        /// for seconds, long enough for the XR compositor to declare the app hung. The batch runs
        /// that tail once, at the end; the per-setting notifications modules act on are unaffected.
        /// The tint/UI subscribers on <see cref="OnLevelChanged"/> are inside the batch too, so
        /// anything they write joins the same flush.
        /// </summary>
        private static void ApplyLevel(BasisPerformanceLevel level)
        {
            // Consumed here rather than read directly by the caller: a manual pick from the
            // Graphics tab dropdown writes the bound setting directly and reaches this method
            // through OnLevelSettingChanged, never through SetLevel, so it always finds this false
            // and correctly reads as hand-picked.
            bool populationArmed = _pendingPopulationArm;
            _pendingPopulationArm = false;

            using (BasisSettingsSystem.Batch())
            {
                if (level == BasisPerformanceLevel.Off)
                {
                    if (ActiveLevel != BasisPerformanceLevel.Off)
                    {
                        RestoreBaseline();
                    }
                    ActiveLevel = BasisPerformanceLevel.Off;
                    _populationArmed = false;
                    ClearBaseline();
                    OnLevelChanged?.Invoke(ActiveLevel);
                    return;
                }

                if (ActiveLevel == BasisPerformanceLevel.Off || _baseline.Count == 0)
                {
                    CaptureBaseline();
                }

                ActiveLevel = level;
                _populationArmed = populationArmed;
                WritePreset(level);
                FlushBaseline();
                OnLevelChanged?.Invoke(ActiveLevel);
            }
        }

        private static BasisPerformanceLevel EvaluateAuto(int occupants, BasisPerformanceLevel current)
        {
            BasisPerformanceLevel raw = LevelForPopulation(occupants);
            if (raw >= current || current == BasisPerformanceLevel.Off)
            {
                return raw;
            }

            int armed = PopulationThresholds[(int)current - 1];
            return occupants <= Mathf.RoundToInt(armed * DeEscalateMargin) ? raw : current;
        }

        private static int SafePlayerCount()
        {
            try
            {
                return BasisNetworkPlayer.GetPlayerCount();
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static void WritePreset(BasisPerformanceLevel level)
        {
            int index = (int)level - 1;
            _applying = true;
            try
            {
                for (int i = 0; i < _floatRules.Length; i++)
                {
                    FloatRule rule = _floatRules[i];
                    float preset = rule.ByLevel[index];
                    if (float.IsNaN(preset))
                    {
                        continue;
                    }

                    float resolved = ResolveFloat(rule, preset, BaselineFloat(rule.Binding));
                    if (!Mathf.Approximately(rule.Binding.RawValue, resolved))
                    {
                        rule.Binding.SetValue(resolved);
                    }
                }

                for (int i = 0; i < _boolRules.Length; i++)
                {
                    BoolRule rule = _boolRules[i];
                    bool? preset = rule.ByLevel[index];
                    if (!preset.HasValue)
                    {
                        continue;
                    }

                    if (rule.Binding.RawValue != preset.Value)
                    {
                        rule.Binding.SetValue(preset.Value);
                    }
                }

                for (int i = 0; i < _tierRules.Length; i++)
                {
                    TierRule rule = _tierRules[i];
                    string preset = rule.ByLevel[index];
                    if (string.IsNullOrEmpty(preset))
                    {
                        continue;
                    }

                    string resolved = ResolveTier(rule, preset, BaselineString(rule.Binding));
                    if (resolved != null && !string.Equals(rule.Binding.RawValue, resolved, StringComparison.OrdinalIgnoreCase))
                    {
                        rule.Binding.SetValue(resolved);
                    }
                }
            }
            finally
            {
                _applying = false;
            }
        }

        private static float PreviewFloat(BasisSettingsBinding<float> binding, BasisPerformanceLevel level)
        {
            if (level == BasisPerformanceLevel.Off)
            {
                return binding.RawValue;
            }

            for (int i = 0; i < _floatRules.Length; i++)
            {
                FloatRule rule = _floatRules[i];
                if (rule.Binding != binding)
                {
                    continue;
                }

                float preset = rule.ByLevel[(int)level - 1];
                return float.IsNaN(preset) ? binding.RawValue : ResolveFloat(rule, preset, BaselineFloat(binding));
            }

            return binding.RawValue;
        }

        private static float ResolveFloat(FloatRule rule, float preset, float baseline)
        {
            if (rule.ZeroIsUnlimited)
            {
                if (baseline <= 0f) return preset;
                if (preset <= 0f) return baseline;
                return Mathf.Min(preset, baseline);
            }

            return rule.HigherIsCheaper ? Mathf.Max(preset, baseline) : Mathf.Min(preset, baseline);
        }

        private static string ResolveTier(TierRule rule, string preset, string baseline)
        {
            int presetIndex = TierIndex(rule.CheapestFirst, preset);
            int baselineIndex = TierIndex(rule.CheapestFirst, baseline);
            if (presetIndex < 0 || baselineIndex < 0)
            {
                return null;
            }
            return rule.CheapestFirst[Mathf.Min(presetIndex, baselineIndex)];
        }

        private static int TierIndex(string[] tiers, string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return -1;
            }

            for (int i = 0; i < tiers.Length; i++)
            {
                if (string.Equals(tiers[i], value, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
            return -1;
        }

        private static void CaptureBaseline()
        {
            _baseline.Clear();

            for (int i = 0; i < _floatRules.Length; i++)
            {
                BasisSettingsBinding<float> binding = _floatRules[i].Binding;
                _baseline[binding.BindingKey] = binding.RawValue.ToString(CultureInfo.InvariantCulture);
            }

            for (int i = 0; i < _boolRules.Length; i++)
            {
                BasisSettingsBinding<bool> binding = _boolRules[i].Binding;
                _baseline[binding.BindingKey] = binding.RawValue ? "true" : "false";
            }

            for (int i = 0; i < _tierRules.Length; i++)
            {
                BasisSettingsBinding<string> binding = _tierRules[i].Binding;
                _baseline[binding.BindingKey] = binding.RawValue ?? string.Empty;
            }

            SaveBaseline();
        }

        private static void RestoreBaseline()
        {
            _applying = true;
            try
            {
                for (int i = 0; i < _floatRules.Length; i++)
                {
                    BasisSettingsBinding<float> binding = _floatRules[i].Binding;
                    if (_baseline.TryGetValue(binding.BindingKey, out string stored)
                        && float.TryParse(stored, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
                        && !Mathf.Approximately(binding.RawValue, value))
                    {
                        binding.SetValue(value);
                    }
                }

                for (int i = 0; i < _boolRules.Length; i++)
                {
                    BasisSettingsBinding<bool> binding = _boolRules[i].Binding;
                    if (_baseline.TryGetValue(binding.BindingKey, out string stored))
                    {
                        bool value = string.Equals(stored, "true", StringComparison.OrdinalIgnoreCase);
                        if (binding.RawValue != value)
                        {
                            binding.SetValue(value);
                        }
                    }
                }

                for (int i = 0; i < _tierRules.Length; i++)
                {
                    BasisSettingsBinding<string> binding = _tierRules[i].Binding;
                    if (_baseline.TryGetValue(binding.BindingKey, out string stored)
                        && !string.IsNullOrEmpty(stored)
                        && !string.Equals(binding.RawValue, stored, StringComparison.Ordinal))
                    {
                        binding.SetValue(stored);
                    }
                }
            }
            finally
            {
                _applying = false;
            }
        }

        private static float BaselineFloat(BasisSettingsBinding<float> binding)
        {
            if (_baseline.TryGetValue(binding.BindingKey, out string stored)
                && float.TryParse(stored, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
            {
                return value;
            }
            return binding.RawValue;
        }

        private static string BaselineString(BasisSettingsBinding<string> binding)
        {
            if (_baseline.TryGetValue(binding.BindingKey, out string stored) && !string.IsNullOrEmpty(stored))
            {
                return stored;
            }
            return binding.RawValue;
        }

        /// <summary>
        /// Marks the snapshot for persisting rather than writing it out here. The manual-capture
        /// hooks below run from binding change events, and a slider fires one of those every frame
        /// it is dragged — writing through would serialize the whole snapshot and rewrite the
        /// settings file on each of those frames. <see cref="FlushBaseline"/> does the write at
        /// most once a frame instead.
        /// </summary>
        private static void SaveBaseline()
        {
            _baselineDirty = true;
        }

        private static void FlushBaseline()
        {
            if (!_baselineDirty)
            {
                return;
            }
            _baselineDirty = false;

            BaselineStore store = new BaselineStore();
            foreach (KeyValuePair<string, string> pair in _baseline)
            {
                store.Entries.Add(new BaselineEntry { Key = pair.Key, Value = pair.Value });
            }
            BasisSettingsDefaults.PerformanceModeBaseline.SetValue(JsonUtility.ToJson(store));
        }

        private static void LoadBaseline()
        {
            _baseline.Clear();

            string json = BasisSettingsDefaults.PerformanceModeBaseline.RawValue;
            if (string.IsNullOrEmpty(json))
            {
                return;
            }

            BaselineStore store;
            try
            {
                store = JsonUtility.FromJson<BaselineStore>(json);
            }
            catch (Exception)
            {
                store = null;
            }

            if (store?.Entries == null)
            {
                return;
            }

            for (int i = 0; i < store.Entries.Count; i++)
            {
                BaselineEntry entry = store.Entries[i];
                if (entry != null && !string.IsNullOrEmpty(entry.Key))
                {
                    _baseline[entry.Key] = entry.Value;
                }
            }
        }

        private static void ClearBaseline()
        {
            _baseline.Clear();
            _baselineDirty = false;
            if (!string.IsNullOrEmpty(BasisSettingsDefaults.PerformanceModeBaseline.RawValue))
            {
                BasisSettingsDefaults.PerformanceModeBaseline.SetValue(string.Empty);
            }
        }

        private static void Subscribe()
        {
            if (_subscribed)
            {
                return;
            }
            _subscribed = true;

            for (int i = 0; i < _floatRules.Length; i++)
            {
                BasisSettingsBinding<float> binding = _floatRules[i].Binding;
                binding.OnChanged += value => CaptureManualFloat(binding, value);
            }

            for (int i = 0; i < _boolRules.Length; i++)
            {
                BasisSettingsBinding<bool> binding = _boolRules[i].Binding;
                binding.OnChanged += value => CaptureManualBool(binding, value);
            }

            for (int i = 0; i < _tierRules.Length; i++)
            {
                BasisSettingsBinding<string> binding = _tierRules[i].Binding;
                binding.OnChanged += value => CaptureManualString(binding, value);
            }
        }

        private static void CaptureManualFloat(BasisSettingsBinding<float> binding, float value)
        {
            if (_applying || !IsActive) return;
            _baseline[binding.BindingKey] = value.ToString(CultureInfo.InvariantCulture);
            SaveBaseline();
        }

        private static void CaptureManualBool(BasisSettingsBinding<bool> binding, bool value)
        {
            if (_applying || !IsActive) return;
            _baseline[binding.BindingKey] = value ? "true" : "false";
            SaveBaseline();
        }

        private static void CaptureManualString(BasisSettingsBinding<string> binding, string value)
        {
            if (_applying || !IsActive) return;
            _baseline[binding.BindingKey] = value ?? string.Empty;
            SaveBaseline();
        }

        private static void BuildRules()
        {
            _floatRules = new[]
            {
                Floats(BasisSettingsDefaults.AvatarRange, 25f, 18f, 12f),
                Floats(BasisSettingsDefaults.MaxVisibleAvatars, 60f, 30f, 15f, zeroIsUnlimited: true),
                Floats(BasisSettingsDefaults.PoseLOD, 2f, 3f, 5f, higherIsCheaper: true),
                Floats(BasisSettingsDefaults.AvatarMeshLOD, 0.08f, 0.12f, 0.2f, higherIsCheaper: true),
                Floats(BasisSettingsDefaults.GlobalMeshLOD, 20f, 40f, 60f, higherIsCheaper: true),
                // Far avatars now begin where the max avatar range ends — no separate distance.
                //Floats(BasisSettingsDefaults.AvatarFarLodDistance, 18f, 12f, 8f),

                Floats(BasisSettingsDefaults.MaxAudioSources, 32f, 16f, 8f, zeroIsUnlimited: true),
                Floats(BasisSettingsDefaults.OpenLipSyncMaxSlots, 20f, 12f, 6f),

                Floats(BasisSettingsDefaults.RenderResolution, float.NaN, float.NaN, 0.85f),

                Floats(BasisSettingsDefaults.JiggleCollisionCullDistance, 15f, 10f, 6f),
                Floats(BasisSettingsDefaults.JiggleColliderLodNearDistance, 15f, 10f, 5f),
                Floats(BasisSettingsDefaults.JiggleColliderLodMidDistance, 30f, 20f, 10f),
                Floats(BasisSettingsDefaults.JiggleColliderLodFarDistance, 60f, 40f, 20f),

                Floats(BasisSettingsDefaults.MaxPerfTriangles, float.NaN, 500000f, 200000f),
                Floats(BasisSettingsDefaults.MaxPerfTextureMemoryMB, float.NaN, 256f, 128f),
                Floats(BasisSettingsDefaults.MaxPerfSkinnedMeshes, float.NaN, 32f, 16f),
                Floats(BasisSettingsDefaults.MaxPerfMaterialSlots, float.NaN, 96f, 48f),
                Floats(BasisSettingsDefaults.MaxPerfJiggleBones, float.NaN, 64f, 16f),
                Floats(BasisSettingsDefaults.MaxPerfJiggleColliders, float.NaN, 32f, 8f),
                Floats(BasisSettingsDefaults.MaxPerfParticleSystems, float.NaN, 2f, 0f),
                Floats(BasisSettingsDefaults.MaxPerfTrailRenderers, float.NaN, 2f, 0f),
                Floats(BasisSettingsDefaults.MaxPerfLineRenderers, float.NaN, 2f, 0f),
                Floats(BasisSettingsDefaults.MaxPerfCilboxBehaviours, float.NaN, 3f, 1f),
            };

            _boolRules = new[]
            {
                Bools(BasisSettingsDefaults.UseMaxVisibleAvatars, true, true, true),
                Bools(BasisSettingsDefaults.UseMaxAudioSources, true, true, true),
                Bools(BasisSettingsDefaults.UseOpenLipSyncLimit, true, true, true),

                Bools(BasisSettingsDefaults.UseJiggleCollisionFrustumCull, true, true, true),
                Bools(BasisSettingsDefaults.UseJiggleCollisionDistanceCull, true, true, true),
                Bools(BasisSettingsDefaults.UseJiggleColliderDistanceLod, true, true, true),

                Bools(BasisSettingsDefaults.UseAvatarSkinLod, true, true, true),
                Bools(BasisSettingsDefaults.UseAvatarShadowLod, true, true, true),
                Bools(BasisSettingsDefaults.UseAvatarVisibilityCull, true, true, true),
                Bools(BasisSettingsDefaults.UseAvatarFarLod, true, true, true),

                // Plates already vanish with the avatar's distance downgrades, so Light — which
                // pulls AvatarRange in to 25 — needs no rule of its own. Past that the survivors
                // are the players close enough to matter, and reading them on demand from the
                // menu is cheaper than carrying a plate each.
                Bools(BasisSettingsDefaults.NPMenuOnly, null, true, true),

                //Bools(BasisSettingsDefaults.UseRealtimeReflectionProbes, false, false, false), // commented out 2026-08-04 with the unimplemented probe-driver bindings
                Bools(BasisSettingsDefaults.LimitHandHeldCameraRate, true, true, true),
                Bools(BasisSettingsDefaults.LimitAvatarPreviewRate, true, true, true),
                Bools(BasisSettingsDefaults.LocalHeadBlendShapes, null, false, false),

                Bools(BasisSettingsDefaults.UsePerfLimitTriangles, null, true, true),
                Bools(BasisSettingsDefaults.UsePerfLimitTextureMemory, null, true, true),
                Bools(BasisSettingsDefaults.UsePerfLimitSkinnedMeshes, null, true, true),
                Bools(BasisSettingsDefaults.UsePerfLimitMaterialSlots, null, true, true),
                Bools(BasisSettingsDefaults.UsePerfLimitJiggleBones, null, true, true),
                Bools(BasisSettingsDefaults.UsePerfLimitJiggleColliders, null, true, true),
                Bools(BasisSettingsDefaults.UsePerfLimitParticleSystems, null, true, true),
                Bools(BasisSettingsDefaults.UsePerfLimitTrailRenderers, null, true, true),
                Bools(BasisSettingsDefaults.UsePerfLimitLineRenderers, null, true, true),
                Bools(BasisSettingsDefaults.UsePerfLimitCilboxBehaviours, null, true, true),
            };

            _tierRules = new[]
            {
                Tiers(BasisSettingsDefaults.ShadowQuality, QualityTiers, "Medium", "Low", "Very Low"),
                Tiers(BasisSettingsDefaults.Antialiasing, AntialiasingTiers, null, "MSAA 2X", "Off"),
                Tiers(BasisSettingsDefaults.QualityLevel, QualityTiers, null, null, "Low"),
            };
        }

        private static FloatRule Floats(BasisSettingsBinding<float> binding, float light, float balanced, float aggressive,
            bool higherIsCheaper = false, bool zeroIsUnlimited = false)
            => new FloatRule
            {
                Binding = binding,
                ByLevel = new[] { light, balanced, aggressive },
                HigherIsCheaper = higherIsCheaper,
                ZeroIsUnlimited = zeroIsUnlimited,
            };

        private static BoolRule Bools(BasisSettingsBinding<bool> binding, bool? light, bool? balanced, bool? aggressive)
            => new BoolRule
            {
                Binding = binding,
                ByLevel = new[] { light, balanced, aggressive },
            };

        private static TierRule Tiers(BasisSettingsBinding<string> binding, string[] cheapestFirst,
            string light, string balanced, string aggressive)
            => new TierRule
            {
                Binding = binding,
                CheapestFirst = cheapestFirst,
                ByLevel = new[] { light, balanced, aggressive },
            };
    }
}
