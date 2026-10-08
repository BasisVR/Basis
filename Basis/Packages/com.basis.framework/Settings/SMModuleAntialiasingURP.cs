using System;
using System.Text.RegularExpressions;
using Basis.BasisUI;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
public class SMModuleAntialiasingURP : BasisSettingsBase
{
    public Camera Camera;
    public UniversalAdditionalCameraData Data;
    public int LowmsaaSampleCount = 2;
    public int MediumLowmsaaSampleCount = 4;
    public int HighmsaaSampleCount = 8;
    public static readonly string[] TemporalUpscalerOptions = { "DLSS", "FSR 2", "FSR 3", "FSR 4" };
    public static readonly string[] TemporalUpscalerLabelKeys = { "settings.graphics.aa.dlss", "settings.graphics.aa.fsr2", "settings.graphics.aa.fsr3", "settings.graphics.aa.fsr4" };
    public static readonly string[] UpscalerQualityOptions = { "Native", "Quality", "Balanced", "Performance", "Ultra Performance" };
    public static readonly string[] UpscalerQualityLabelKeys = { "settings.graphics.upscalerQuality.native", "settings.graphics.upscalerQuality.quality", "settings.graphics.upscalerQuality.balanced", "settings.graphics.upscalerQuality.performance", "settings.graphics.upscalerQuality.ultraPerformance" };
    public static readonly string[] DlssModelOptions = { "Auto", "Default", "K", "M", "L" };
    public static readonly string[] DlssModelLabelKeys = { "settings.graphics.dlssModel.auto", "settings.graphics.dlssModel.default", "settings.graphics.dlssModel.k", "settings.graphics.dlssModel.m", "settings.graphics.dlssModel.l" };
    public static bool TemporalUpscalerActive { get; private set; }
    public static event Action TemporalUpscalerChanged;
    public static float UpscalerSharpness => Mathf.Clamp01(BasisSettingsDefaults.UpscalerSharpness.RawValue);
    private const string Rtx40OrNewerPattern = @"\bRTX\s+(?:PRO\s+)?[4-9]0[5-9]0\b|\bAda\b|\bBlackwell\b";
    private static string requestedUpscalerId = string.Empty;
    private static string requestedQuality;
    private static float cameraSharpening;
#if ENABLE_UPSCALER_FRAMEWORK && ENABLE_NVIDIA && BASIS_HAS_NVIDIA
    private static readonly UnityEngine.NVIDIA.DLSSQuality[] DlssQualityTiers = { UnityEngine.NVIDIA.DLSSQuality.DLAA, UnityEngine.NVIDIA.DLSSQuality.MaximumQuality, UnityEngine.NVIDIA.DLSSQuality.Balanced, UnityEngine.NVIDIA.DLSSQuality.MaximumPerformance, UnityEngine.NVIDIA.DLSSQuality.UltraPerformance };
#endif
#if ENABLE_UPSCALER_FRAMEWORK && ENABLE_AMD && BASIS_HAS_AMD
    private static readonly UnityEngine.AMD.FSR2Quality[] Fsr2QualityTiers = { UnityEngine.AMD.FSR2Quality.Quality, UnityEngine.AMD.FSR2Quality.Quality, UnityEngine.AMD.FSR2Quality.Balanced, UnityEngine.AMD.FSR2Quality.Performance, UnityEngine.AMD.FSR2Quality.UltraPerformance };
    private static readonly UnityEngine.AMD.FSR3Quality[] Fsr3QualityTiers = { UnityEngine.AMD.FSR3Quality.NativeAA, UnityEngine.AMD.FSR3Quality.Quality, UnityEngine.AMD.FSR3Quality.Balanced, UnityEngine.AMD.FSR3Quality.Performance, UnityEngine.AMD.FSR3Quality.UltraPerformance };
    private static readonly UnityEngine.AMD.FSR4Quality[] Fsr4QualityTiers = { UnityEngine.AMD.FSR4Quality.NativeAA, UnityEngine.AMD.FSR4Quality.Quality, UnityEngine.AMD.FSR4Quality.Balanced, UnityEngine.AMD.FSR4Quality.Performance, UnityEngine.AMD.FSR4Quality.UltraPerformance };
#endif
    public static string TemporalUpscalerId(string option)
    {
        switch (option?.ToLowerInvariant())
        {
            case "dlss":
                return "nvidia.dlss4";
            case "fsr 2":
                return "amd.fsr2";
            case "fsr 3":
                return "amd.fsr3";
            case "fsr 4":
                return "amd.fsr4";
            default:
                return null;
        }
    }
    public static bool IsTemporalUpscalerOption(string option) => TemporalUpscalerId(option) != null;
    public static bool IsDlssOption(string option) => TemporalUpscalerId(option) == "nvidia.dlss4";
    public static bool IsRtx40OrNewer(string deviceName) => !string.IsNullOrEmpty(deviceName) && Regex.IsMatch(deviceName, Rtx40OrNewerPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public static bool IsUpscalerSupported(string option)
    {
#if ENABLE_UPSCALER_FRAMEWORK
        string upscalerId = TemporalUpscalerId(option);
        return upscalerId != null && RenderPipelineManager.currentPipeline is UniversalRenderPipeline pipeline && pipeline.GetUpscaler(upscalerId) is IUpscaler upscaler && upscaler.isSupportedOnDevice;
#else
        return false;
#endif
    }
#if ENABLE_UPSCALER_FRAMEWORK
#if UNITY_EDITOR
    [UnityEditor.InitializeOnLoadMethod]
#endif
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void SkipUnavailableUpscalers()
    {
        UniversalRenderPipeline.skipUpscaler = IsUpscalerUnavailable;
    }
    public static bool IsUpscalerUnavailable(string upscalerId)
    {
#if ENABLE_AMD && BASIS_HAS_AMD
        UnityEngine.AMD.GraphicsDeviceFeature feature;
        if (upscalerId == FSR3IUpscaler.registeredId)
        {
            feature = UnityEngine.AMD.GraphicsDeviceFeature.FSRUpscale3;
        }
        else if (upscalerId == FSR4IUpscaler.registeredId)
        {
            feature = UnityEngine.AMD.GraphicsDeviceFeature.FSRUpscale4;
        }
        else
        {
            return false;
        }
        if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12 || !UnityEngine.AMD.AMDUnityPlugin.IsLoaded())
        {
            return true;
        }
        UnityEngine.AMD.GraphicsDevice device = UnityEngine.AMD.GraphicsDevice.device ?? UnityEngine.AMD.GraphicsDevice.CreateGraphicsDevice();
        return device == null || !device.IsFeatureAvailable(feature);
#else
        return false;
#endif
    }
#endif
    private void OnEnable()
    {
        RenderPipelineManager.activeRenderPipelineCreated += OnRenderPipelineCreated;
        BasisDeviceManagement.OnBootModeChanged += OnBootModeChanged;
        BasisLocalCameraDriver.InstanceExists += ApplyCameraSharpening;
    }
    private void OnDisable()
    {
        RenderPipelineManager.activeRenderPipelineCreated -= OnRenderPipelineCreated;
        BasisDeviceManagement.OnBootModeChanged -= OnBootModeChanged;
        BasisLocalCameraDriver.InstanceExists -= ApplyCameraSharpening;
    }
    private void OnRenderPipelineCreated()
    {
        ApplyAntialiasing(BasisSettingsDefaults.Antialiasing.RawValue, false);
    }
    private void OnBootModeChanged(string mode)
    {
        ApplyUpscaler(requestedUpscalerId);
    }
    public override void ValidSettingsChange(string matchedSettingName, string optionValue)
    {
        if (matchedSettingName == BasisSettingsDefaults.UpscalerQuality.BindingKey)
        {
            requestedQuality = optionValue;
            ApplyUpscaler(requestedUpscalerId);
            return;
        }
        if (matchedSettingName == BasisSettingsDefaults.UpscalerSharpness.BindingKey || matchedSettingName == BasisSettingsDefaults.DlssModel.BindingKey)
        {
            ApplyUpscaler(requestedUpscalerId);
            return;
        }
        if (matchedSettingName != BasisSettingsDefaults.Antialiasing.BindingKey)
        {
            return;
        }
        ApplyAntialiasing(optionValue, true);
    }
    private void ApplyAntialiasing(string optionValue, bool reportMissing)
    {
        UniversalRenderPipelineAsset Asset = QualitySettings.renderPipeline as UniversalRenderPipelineAsset;
        if (Asset == null)
        {
            if (reportMissing)
            {
                BasisDebug.LogError("Missing Asset Pipeline!");
            }
            return;
        }
        optionValue = optionValue == null ? string.Empty : optionValue.ToLowerInvariant();
        if (IsTemporalUpscalerOption(optionValue) && !IsUpscalerSupported(optionValue))
        {
            string fallback = BasisSettingsDefaults.Antialiasing.DefaultValue.GetDefault().ToLowerInvariant();
            if (RenderPipelineManager.currentPipeline != null)
            {
                BasisDebug.LogWarning($"Antialiasing {optionValue} is not supported on this device, using {fallback}", BasisDebug.LogTag.Local);
            }
            optionValue = fallback;
        }
        int sampleCount = 1;
        string upscalerId = string.Empty;
        switch (optionValue)
        {
            case "off":
            case "msaa off":
                break;
            case "msaa 2x":
                sampleCount = LowmsaaSampleCount;
                break;
            case "msaa 4x":
                sampleCount = MediumLowmsaaSampleCount;
                break;
            case "msaa 8x":
                sampleCount = HighmsaaSampleCount;
                break;
            case "linear":
                upscalerId = "unity.bilinear";
                break;
            case "point":
                upscalerId = "unity.point";
                break;
            case "fsr":
                upscalerId = "amd.fsr1";
                break;
            case "stp":
                upscalerId = "unity.stp";
                break;
            default:
                upscalerId = TemporalUpscalerId(optionValue);
                if (upscalerId == null)
                {
                    return;
                }
                break;
        }
        Asset.msaaSampleCount = sampleCount;
#if ENABLE_UPSCALER_FRAMEWORK
        ApplyUpscaler(upscalerId);
#else
        Asset.upscalingFilter = LegacyUpscalingFilter(upscalerId);
#endif
        if (Camera == null)
        {
            if (BasisLocalCameraDriver.Instance != null)
            {
                Camera = BasisLocalCameraDriver.Instance.Camera;
                Data = BasisLocalCameraDriver.Instance.CameraData;
            }
            if (Camera == null)
            {
                Camera = Camera.main;
#if UNITY_SERVER
                if (Camera != null)
                {
                    Camera.TryGetComponent<UniversalAdditionalCameraData>(out Data);
                }
#endif
            }
        }
        if (Camera == null || Data == null)
        {
            if (reportMissing)
            {
                BasisDebug.LogError("Missing Camera Or Data!");
            }
            return;
        }
        BasisDebug.Log($"Antialiasing Changed to {optionValue}", BasisDebug.LogTag.Local);
        Camera.allowMSAA = sampleCount > 1;
        Data.antialiasing = AntialiasingMode.None;
        Data.antialiasingQuality = AntialiasingQuality.Low;
    }
    private static void ApplyUpscaler(string upscalerId)
    {
        requestedUpscalerId = upscalerId ?? string.Empty;
        bool temporal = false;
        bool sharpenOnCamera = false;
#if ENABLE_UPSCALER_FRAMEWORK
        if (RenderPipelineManager.currentPipeline is UniversalRenderPipeline pipeline)
        {
            IUpscaler upscaler = string.IsNullOrEmpty(requestedUpscalerId) ? null : pipeline.GetUpscaler(requestedUpscalerId);
            if (upscaler != null && !upscaler.isSupportedOnDevice)
            {
                upscaler = null;
            }
            pipeline.SetUpscaler(upscaler != null ? requestedUpscalerId : string.Empty);
            if (upscaler != null)
            {
                ApplyUpscalerOptions(pipeline.GetUpscalerOptions(requestedUpscalerId), requestedQuality ?? BasisSettingsDefaults.UpscalerQuality.RawValue);
            }
            temporal = upscaler != null && upscaler.isTemporal;
            sharpenOnCamera = temporal && !upscaler.supportsSharpening;
        }
#endif
        cameraSharpening = sharpenOnCamera ? UpscalerSharpness : 0f;
        ApplyCameraSharpening();
        if (temporal != TemporalUpscalerActive)
        {
            TemporalUpscalerActive = temporal;
            TemporalUpscalerChanged?.Invoke();
        }
    }
    private static void ApplyCameraSharpening()
    {
        if (BasisLocalCameraDriver.HasInstance && BasisLocalCameraDriver.Instance != null && BasisLocalCameraDriver.Instance.CameraData != null)
        {
            BasisLocalCameraDriver.Instance.CameraData.taaSettings.contrastAdaptiveSharpening = cameraSharpening;
        }
    }
#if ENABLE_UPSCALER_FRAMEWORK
    private static void ApplyUpscalerOptions(UpscalerOptions options, string quality)
    {
        if (options == null)
        {
            return;
        }
        int tier = 0;
        for (int index = 0; index < UpscalerQualityOptions.Length; index++)
        {
            if (string.Equals(UpscalerQualityOptions[index], quality, StringComparison.OrdinalIgnoreCase))
            {
                tier = index;
                break;
            }
        }
        if (BasisDeviceManagement.IsCurrentModeVR())
        {
            tier = 0;
        }
        float sharpness = UpscalerSharpness;
        options.resolutionMode = UpscalerResolutionMode.QualityMode;
#if ENABLE_NVIDIA && BASIS_HAS_NVIDIA
        if (options is DLSSOptions dlss)
        {
            dlss.dlssQualityMode = DlssQualityTiers[tier];
            ApplyDlssModel(dlss, BasisSettingsDefaults.DlssModel.RawValue);
            return;
        }
#endif
#if ENABLE_AMD && BASIS_HAS_AMD
        if (options is FSR2Options fsr2)
        {
            fsr2.fsr2QualityMode = Fsr2QualityTiers[tier];
            fsr2.enableSharpening = sharpness > 0f;
            fsr2.sharpness = sharpness;
            if (tier == 0)
            {
                options.resolutionMode = UpscalerResolutionMode.CustomScaling;
            }
            return;
        }
        if (options is FSR3Options fsr3)
        {
            fsr3.fsr3QualityMode = Fsr3QualityTiers[tier];
            fsr3.enableSharpening = sharpness > 0f;
            fsr3.sharpness = sharpness;
            return;
        }
        if (options is FSR4Options fsr4)
        {
            fsr4.fsr4QualityMode = Fsr4QualityTiers[tier];
            fsr4.enableSharpening = sharpness > 0f;
            fsr4.sharpness = sharpness;
        }
#endif
    }
#if ENABLE_NVIDIA && BASIS_HAS_NVIDIA
    private static void ApplyDlssModel(DLSSOptions dlss, string model)
    {
        UnityEngine.NVIDIA.DLSSPreset preset = UnityEngine.NVIDIA.DLSSPreset.Preset_Default;
        UnityEngine.NVIDIA.DLSSPreset ultraPerformancePreset = UnityEngine.NVIDIA.DLSSPreset.Preset_Default;
        switch (model?.ToLowerInvariant())
        {
            case "default":
                break;
            case "k":
                preset = ultraPerformancePreset = UnityEngine.NVIDIA.DLSSPreset.Preset_K;
                break;
            case "m":
                preset = ultraPerformancePreset = UnityEngine.NVIDIA.DLSSPreset.Preset_M;
                break;
            case "l":
                preset = ultraPerformancePreset = UnityEngine.NVIDIA.DLSSPreset.Preset_L;
                break;
            default:
                if (IsRtx40OrNewer(SystemInfo.graphicsDeviceName))
                {
                    preset = ultraPerformancePreset = UnityEngine.NVIDIA.DLSSPreset.Preset_L;
                }
                break;
        }
        dlss.dlssRenderPresetDLAA = preset;
        dlss.dlssRenderPresetQuality = preset;
        dlss.dlssRenderPresetBalanced = preset;
        dlss.dlssRenderPresetPerformance = preset;
        dlss.dlssRenderPresetUltraPerformance = ultraPerformancePreset;
    }
#endif
#else
    private static UpscalingFilterSelection LegacyUpscalingFilter(string upscalerId)
    {
        switch (upscalerId)
        {
            case "unity.bilinear":
                return UpscalingFilterSelection.Linear;
            case "unity.point":
                return UpscalingFilterSelection.Point;
            case "amd.fsr1":
                return UpscalingFilterSelection.FSR;
            case "unity.stp":
                return UpscalingFilterSelection.STP;
            default:
                return UpscalingFilterSelection.Auto;
        }
    }
#endif
    public override void ChangedSettings()
    {
    }
}

