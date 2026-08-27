using System;
using System.Collections.Generic;
using System.Globalization;
using Basis.BasisUI;
using Basis.Scripts.Settings;
using UnityEngine.XR.OpenXR;

namespace Basis.Scripts.Device_Management.Devices.UnityInputSystem
{
    /// <summary>
    /// Drives the headset display refresh rate from the HeadsetRefreshRate setting through
    /// XR_FB_display_refresh_rate. Without this the runtime picks, which on a headset that
    /// enumerates a wide range means the rate is whatever SteamVR defaulted to.
    /// </summary>
    public static class BasisOpenXRRefreshRate
    {
        public const string Auto = "Auto";

        private static readonly List<float> rates = new List<float>();
        private static bool hooked;

        public static IReadOnlyList<float> SupportedRates => rates;
        public static float ActiveRate { get; private set; }

        private static ValveOpenXRRefreshRateFeature Feature =>
            OpenXRSettings.Instance == null ? null : OpenXRSettings.Instance.GetFeature<ValveOpenXRRefreshRateFeature>();

        public static void Hook()
        {
            if (hooked)
            {
                return;
            }
            hooked = true;
            BasisSettingsSystem.OnSettingChanged += OnSettingChanged;

            ValveOpenXRRefreshRateFeature feature = Feature;
            if (feature == null || !feature.enabled)
            {
                return;
            }
            if (feature.initialized)
            {
                Refresh();
            }
            else
            {
                feature.OnRefreshRateFeatureAvailable += Refresh;
            }
        }

        public static void Unhook()
        {
            if (!hooked)
            {
                return;
            }
            hooked = false;
            BasisSettingsSystem.OnSettingChanged -= OnSettingChanged;

            ValveOpenXRRefreshRateFeature feature = Feature;
            if (feature != null)
            {
                feature.OnRefreshRateFeatureAvailable -= Refresh;
            }
            rates.Clear();
            ActiveRate = 0f;
        }

        private static void OnSettingChanged(string key, string value)
        {
            if (!string.Equals(key, BasisSettingsDefaults.HeadsetRefreshRate.BindingKey, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            Apply(value);
        }

        public static void Refresh()
        {
            ValveOpenXRRefreshRateFeature feature = Feature;
            if (feature == null || !feature.initialized)
            {
                return;
            }

            List<float> enumerated = new List<float>();
            feature.EnumerateRefreshRates(ref enumerated);
            rates.Clear();
            rates.AddRange(enumerated);
            ActiveRate = feature.GetRefreshRate();

            BasisDebug.Log($"Headset refresh rates {string.Join(", ", rates)} (current {ActiveRate:F0})", BasisDebug.LogTag.Device);
            Apply(BasisSettingsDefaults.HeadsetRefreshRate.RawValue);
        }

        public static void Apply(string option)
        {
            ValveOpenXRRefreshRateFeature feature = Feature;
            if (feature == null || !feature.initialized)
            {
                return;
            }
            if (string.IsNullOrEmpty(option) || string.Equals(option, Auto, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            if (!float.TryParse(option, NumberStyles.Any, CultureInfo.InvariantCulture, out float requested) || requested <= 0f)
            {
                return;
            }

            float target = Nearest(requested);
            if (target <= 0f)
            {
                return;
            }

            ActiveRate = feature.GetRefreshRate();
            if (UnityEngine.Mathf.Approximately(ActiveRate, target))
            {
                return;
            }

            if (feature.SetRefreshRate(target) == 0)
            {
                ActiveRate = target;
                BasisDebug.Log($"Headset refresh rate set to {target:F0}Hz (asked {requested:F0}Hz)", BasisDebug.LogTag.Device);
            }
            else
            {
                BasisDebug.LogWarning($"Headset refused refresh rate {target:F0}Hz", BasisDebug.LogTag.Device);
            }
        }

        /// <summary>
        /// Closest enumerated rate to the request, preferring one at or below it so a headset that
        /// cannot reach the asked-for rate lands on something it can actually hold.
        /// </summary>
        private static float Nearest(float requested)
        {
            if (rates.Count == 0)
            {
                return 0f;
            }

            float best = 0f;
            for (int Index = 0; Index < rates.Count; Index++)
            {
                float rate = rates[Index];
                if (rate <= requested + 0.5f && rate > best)
                {
                    best = rate;
                }
            }
            if (best > 0f)
            {
                return best;
            }

            best = rates[0];
            for (int Index = 1; Index < rates.Count; Index++)
            {
                if (rates[Index] < best)
                {
                    best = rates[Index];
                }
            }
            return best;
        }
    }
}
