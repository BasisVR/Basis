using System;
using System.Collections.Generic;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Settings;
using Unity.Scripting.LifecycleManagement;

namespace Basis.BasisUI
{
    [NoAutoStaticsCleanup]
    public static class BasisSettingsModeScope
    {
        public const string KeyPrefix = "permode.";
        private static readonly IBasisSettingsBinding[] bindings =
        {
            BasisSettingsDefaults.UseGlobalIllumination,
            BasisSettingsDefaults.GlobalIlluminationSolution,
            BasisSettingsDefaults.GlobalIlluminationMode,
            BasisSettingsDefaults.GlobalIlluminationSkinnedMeshes,
            BasisSettingsDefaults.GlobalIlluminationQuality,
            BasisSettingsDefaults.GlobalIlluminationResolution,
            BasisSettingsDefaults.GlobalIlluminationFallback,
            BasisSettingsDefaults.GlobalIlluminationPathTracerRays,
            BasisSettingsDefaults.GlobalIlluminationPathTracerAvatarRays,
            BasisSettingsDefaults.GlobalIlluminationIgnoreBakedEmission,
            BasisSettingsDefaults.GlobalIlluminationLightmappedReceive,
            BasisSettingsDefaults.GlobalIlluminationIntensity,
            BasisSettingsDefaults.GlobalIlluminationSaturation,
            BasisSettingsDefaults.GlobalIlluminationObscurance,
            BasisSettingsDefaults.GlobalIlluminationRayLength,
            BasisSettingsDefaults.GlobalIlluminationCascadeOverride,
            BasisSettingsDefaults.GlobalIlluminationCascadeCount,
            BasisSettingsDefaults.GlobalIlluminationCascadeSpacing,
            BasisSettingsDefaults.GlobalIlluminationCascadeSteps,
            BasisSettingsDefaults.GlobalIlluminationCascadeMergeFix,
            BasisSettingsDefaults.GlobalIlluminationWorldSpace,
            BasisSettingsDefaults.GlobalIlluminationWorldLevels,
            BasisSettingsDefaults.GlobalIlluminationWorldVoxelSize,
            BasisSettingsDefaults.GlobalIlluminationTemporalResponse,
            BasisSettingsDefaults.GlobalIlluminationTemporalFilter,
            BasisSettingsDefaults.GlobalIlluminationWideBlur,
            BasisSettingsDefaults.GlobalIlluminationAlternateEyes,
            BasisSettingsDefaults.GlobalIlluminationWorldReflections,
            BasisSettingsDefaults.GlobalIlluminationRayReuse,
            BasisSettingsDefaults.GlobalIlluminationEmitters,
            BasisSettingsDefaults.GlobalIlluminationEmitterIntensity,
            BasisSettingsDefaults.GlobalIlluminationLocalAvatarEmitterIntensity,
            BasisSettingsDefaults.GlobalIlluminationRemoteAvatarEmitterIntensity,
            BasisSettingsDefaults.GlobalIlluminationReflectionProbes,
            BasisSettingsDefaults.GlobalIlluminationMirrors,
            BasisSettingsDefaults.GlobalIlluminationSpecular,
            BasisSettingsDefaults.ReflectionsSolution,
            BasisSettingsDefaults.ReflectionsMode,
            BasisSettingsDefaults.GlobalIlluminationSpecularIntensity,
            BasisSettingsDefaults.GlobalIlluminationSpecularMaxRoughness,
            BasisSettingsDefaults.GlobalIlluminationSpecularRayLength,
            BasisSettingsDefaults.GlobalIlluminationSpecularFadeDistance,
            BasisSettingsDefaults.GlobalIlluminationLayers,
            BasisSettingsDefaults.GlobalIlluminationObscuranceRadius,
            BasisSettingsDefaults.GlobalIlluminationFadeDistance,
            BasisSettingsDefaults.GlobalIlluminationNormalBias,
            BasisSettingsDefaults.GlobalIlluminationDistanceBias,
            BasisSettingsDefaults.GlobalIlluminationBounceThreshold,
            BasisSettingsDefaults.GlobalIlluminationFireflyClamp,
        };
        private static readonly HashSet<string> keys = BuildKeys();
        private static readonly List<IBasisSettingsBinding> changed = new List<IBasisSettingsBinding>();
        private static string activeMode;

        public static string ActiveMode => activeMode;
        public static bool Applying { get; private set; }
        public static bool IsScoped(string bindingKey) => bindingKey != null && keys.Contains(bindingKey);
        public static string ScopedKey(string mode, string bindingKey) => KeyPrefix + mode + "." + bindingKey;
        public static bool IsRealMode(string mode) => !string.IsNullOrEmpty(mode) && mode != BasisConstants.None && mode != BasisConstants.InvalidConst;

        private static HashSet<string> BuildKeys()
        {
            HashSet<string> set = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < bindings.Length; index++) { set.Add(bindings[index].BindingKey); }
            return set;
        }

        public static void Initialize()
        {
            activeMode = null;
            BasisSettingsSystem.OnSettingChanged -= OnSettingChanged;
            BasisSettingsSystem.OnSettingChanged += OnSettingChanged;
            BasisDeviceManagement.OnBootModeChanged -= OnBootModeChanged;
            BasisDeviceManagement.OnBootModeChanged += OnBootModeChanged;
            OnBootModeChanged(BasisDeviceManagement.StaticCurrentMode);
        }

        private static void OnSettingChanged(string key, string value)
        {
            if (activeMode == null || !keys.Contains(key)) { return; }
            BasisSettingsSystem.SaveStringQuiet(ScopedKey(activeMode, key), value);
        }

        private static void OnBootModeChanged(string mode)
        {
            if (!IsRealMode(mode) || !BasisSettingsSystem.SettingsLoaded) { activeMode = null; return; }
            if (mode == activeMode) { return; }
            activeMode = mode;
            changed.Clear();
            using (BasisSettingsSystem.Batch())
            {
                for (int index = 0; index < bindings.Length; index++)
                {
                    IBasisSettingsBinding binding = bindings[index];
                    string key = binding.BindingKey;
                    if (!BasisSettingsSystem.TryLoadString(key, out string current)) { continue; }
                    string scoped = ScopedKey(mode, key);
                    if (!BasisSettingsSystem.TryLoadString(scoped, out string wanted)) { BasisSettingsSystem.SaveStringQuiet(scoped, current); continue; }
                    if (wanted == current) { continue; }
                    BasisSettingsSystem.SaveStringQuiet(key, wanted);
                    changed.Add(binding);
                }
                Applying = true;
                try { for (int index = 0; index < changed.Count; index++) { changed[index].ReloadAndNotify(); } }
                finally { Applying = false; }
                for (int index = 0; index < changed.Count; index++) { BasisSettingsSystem.NotifySettingChanged(changed[index].BindingKey); }
            }
            if (changed.Count > 0) { BasisDebug.Log($"Applied {changed.Count} {mode} lighting settings", BasisDebug.LogTag.Device); }
            changed.Clear();
        }
    }
}
