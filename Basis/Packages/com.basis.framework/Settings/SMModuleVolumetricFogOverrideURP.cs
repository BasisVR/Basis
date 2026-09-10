using System;
using System.Runtime.CompilerServices;
using Basis.BasisUI;
using Basis.Scripts.Common;
using Basis.Scripts.Device_Management;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Accessibility settings module that controls volumetric fog density
/// by modifying VolumetricFogVolumeComponent overrides on all existing Volumes in the scene.
/// When the override toggle is disabled the scene's authored fog overrides are restored.
/// </summary>
public class SMModuleVolumetricFogOverrideURP : BasisSettingsBase
{
    private sealed class AuthoredFogState
    {
        public bool EnabledOverride;
        public bool EnabledValue;
        public bool DensityOverride;
        public float DensityValue;
    }

    private bool _overrideEnabled;
    private float _pendingDensity = 0.2f;

    private readonly ConditionalWeakTable<VolumetricFogVolumeComponent, AuthoredFogState> _authored = new();

    private static string K_USE_FOG_OVERRIDE => BasisSettingsDefaults.UseVolumetricFogOverride.BindingKey;
    private static string K_FOG_DENSITY => BasisSettingsDefaults.VolumetricFogDensity.BindingKey;
    private static string K_FOG_BAKED_APV => BasisSettingsDefaults.VolumetricFogBakedAPV.BindingKey;
    private static string K_FOG_RESOLUTION => BasisSettingsDefaults.VolumetricFogResolution.BindingKey;
    private static string K_FOG_MAX_STEPS => BasisSettingsDefaults.VolumetricFogMaxSteps.BindingKey;
    private static string K_FOG_BLUR_ITERATIONS => BasisSettingsDefaults.VolumetricFogBlurIterations.BindingKey;
    private static string K_FOG_TEMPORAL => BasisSettingsDefaults.VolumetricFogTemporal.BindingKey;
    private static string K_FOG_FROXELS => BasisSettingsDefaults.VolumetricFogFroxels.BindingKey;
    private static string K_FOG_ANALYTIC_DEPTH => BasisSettingsDefaults.VolumetricFogAnalyticDepth.BindingKey;
    private static string K_FOG_SCALE_STEPS => BasisSettingsDefaults.VolumetricFogScaleSteps.BindingKey;
    private static string K_FOG_SUN_TRIMS => BasisSettingsDefaults.VolumetricFogSunTrims.BindingKey;
    private static string K_FOG_FROXEL_GRID => BasisSettingsDefaults.VolumetricFogFroxelGrid.BindingKey;

    public override void ValidSettingsChange(string matchedSettingName, string optionValue)
    {
        if (matchedSettingName == K_USE_FOG_OVERRIDE)
        {
            _overrideEnabled = optionValue == "true";
            ApplyOverride();
        }
        else if (matchedSettingName == K_FOG_DENSITY)
        {
            if (SliderReadOption(optionValue, out float density))
            {
                _pendingDensity = density;
                ApplyOverride();
            }
        }
        else if (matchedSettingName == K_FOG_BAKED_APV)
        {
            bool bakedAPV = optionValue == "true";
            VolumetricFogQuality.APVMode = bakedAPV ? VolumetricFogAPVMode.Baked : VolumetricFogAPVMode.Live;
            // Convert the world's APV to a baked volume now so baked mode has data to sample straight away.
            if (bakedAPV)
                VolumetricFogAPVBaker.RequestRebake();
        }
        else if (matchedSettingName == K_FOG_RESOLUTION)
        {
            VolumetricFogQuality.Resolution = ParseResolution(optionValue);
        }
        else if (matchedSettingName == K_FOG_MAX_STEPS)
        {
            if (SliderReadOption(optionValue, out float maxSteps))
                VolumetricFogQuality.MaxSteps = Mathf.RoundToInt(maxSteps);
        }
        else if (matchedSettingName == K_FOG_BLUR_ITERATIONS)
        {
            if (SliderReadOption(optionValue, out float blurIterations))
                VolumetricFogQuality.BlurIterations = Mathf.RoundToInt(blurIterations);
        }
        else if (matchedSettingName == K_FOG_TEMPORAL)
        {
            VolumetricFogQuality.TemporalReprojection = optionValue == "true";
        }
        else if (matchedSettingName == K_FOG_FROXELS)
        {
            VolumetricFogQuality.FroxelVolume = optionValue == "true";
        }
        else if (matchedSettingName == K_FOG_ANALYTIC_DEPTH)
        {
            VolumetricFogQuality.AnalyticOpticalDepth = optionValue == "true";
        }
        else if (matchedSettingName == K_FOG_SCALE_STEPS)
        {
            VolumetricFogQuality.ScaleStepsWithResolution = optionValue == "true";
        }
        else if (matchedSettingName == K_FOG_SUN_TRIMS)
        {
            VolumetricFogQuality.SunPathTrims = optionValue == "true";
        }
        else if (matchedSettingName == K_FOG_FROXEL_GRID)
        {
            ApplyFroxelGrid(optionValue);
        }
    }

    public override void ChangedSettings()
    {
        ApplyQuality();
    }

    private static void ApplyQuality()
    {
        VolumetricFogQuality.Resolution = ParseResolution(BasisSettingsDefaults.VolumetricFogResolution.RawValue);
        VolumetricFogQuality.MaxSteps = Mathf.RoundToInt(BasisSettingsDefaults.VolumetricFogMaxSteps.RawValue);
        VolumetricFogQuality.BlurIterations = Mathf.RoundToInt(BasisSettingsDefaults.VolumetricFogBlurIterations.RawValue);
        VolumetricFogQuality.APVMode = BasisSettingsDefaults.VolumetricFogBakedAPV.RawValue ? VolumetricFogAPVMode.Baked : VolumetricFogAPVMode.Live;
        VolumetricFogQuality.TemporalReprojection = BasisSettingsDefaults.VolumetricFogTemporal.RawValue;
        VolumetricFogQuality.FroxelVolume = BasisSettingsDefaults.VolumetricFogFroxels.RawValue;
        VolumetricFogQuality.AnalyticOpticalDepth = BasisSettingsDefaults.VolumetricFogAnalyticDepth.RawValue;
        VolumetricFogQuality.ScaleStepsWithResolution = BasisSettingsDefaults.VolumetricFogScaleSteps.RawValue;
        VolumetricFogQuality.SunPathTrims = BasisSettingsDefaults.VolumetricFogSunTrims.RawValue;
        ApplyFroxelGrid(BasisSettingsDefaults.VolumetricFogFroxelGrid.RawValue);
    }

    private void OnEnable()
    {
        BasisDeviceManagement.OnBootModeChanged += OnBootModeChanged;
    }

    private void OnDisable()
    {
        BasisDeviceManagement.OnBootModeChanged -= OnBootModeChanged;
    }

    private void OnBootModeChanged(string mode)
    {
        ApplyFroxelGrid(BasisSettingsDefaults.VolumetricFogFroxelGrid.RawValue);
    }

    private static void ApplyFroxelGrid(string option)
    {
        bool auto = string.IsNullOrEmpty(option) || string.Equals(option, BasisSettingsDefaults.FogFroxelGridAuto, StringComparison.OrdinalIgnoreCase);
        int divisor = 8;
        int slices = 64;
        if (string.Equals(option, "balanced", StringComparison.OrdinalIgnoreCase) || (auto && BasisDeviceManagement.IsCurrentModeVR()))
        {
            divisor = 12;
            slices = 48;
        }
        if (string.Equals(option, "coarse", StringComparison.OrdinalIgnoreCase) || (auto && BasisGpuDetection.IsMobileGpu))
        {
            divisor = 16;
            slices = 32;
        }
        VolumetricFogQuality.FroxelDivisor = divisor;
        VolumetricFogQuality.FroxelSlices = slices;
    }

    private static VolumetricFogResolution ParseResolution(string option)
    {
        if (string.Equals(option, "full", StringComparison.OrdinalIgnoreCase))
            return VolumetricFogResolution.Full;
        if (string.Equals(option, "quarter", StringComparison.OrdinalIgnoreCase))
            return VolumetricFogResolution.Quarter;
        return VolumetricFogResolution.Half;
    }

    private void ApplyOverride()
    {
        int playerVolumeLayers = PlayerVolumeLayerMask;
        Volume[] volumes = FindObjectsByType<Volume>(FindObjectsInactive.Exclude);
        foreach (Volume volume in volumes)
        {
            if (!CanOverrideVolume(volume, playerVolumeLayers))
                continue;

            if (volume.profile == null)
                continue;

            if (!volume.profile.TryGet<VolumetricFogVolumeComponent>(out VolumetricFogVolumeComponent fog))
                continue;

            if (!_authored.TryGetValue(fog, out AuthoredFogState authored))
            {
                authored = new AuthoredFogState
                {
                    EnabledOverride = fog.enabled.overrideState,
                    EnabledValue = fog.enabled.value,
                    DensityOverride = fog.density.overrideState,
                    DensityValue = fog.density.value,
                };
                _authored.Add(fog, authored);
            }

            if (_overrideEnabled)
            {
                fog.enabled.overrideState = true;
                fog.enabled.value = true;
                fog.density.overrideState = true;
                fog.density.value = _pendingDensity;
            }
            else
            {
                fog.enabled.overrideState = authored.EnabledOverride;
                fog.enabled.value = authored.EnabledValue;
                fog.density.overrideState = authored.DensityOverride;
                fog.density.value = authored.DensityValue;
            }
        }
    }
}
