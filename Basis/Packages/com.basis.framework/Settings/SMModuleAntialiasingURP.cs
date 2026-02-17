using Basis.BasisUI;
using Basis.Scripts.Drivers;
using UnityEngine;
using UnityEngine.Rendering.Universal;
public class SMModuleAntialiasingURP : BasisSettingsBase
{
    public Camera Camera;
    public UniversalAdditionalCameraData Data;
    public int LowmsaaSampleCount = 2;
    public int MediumLowmsaaSampleCount = 4;
    public int HighmsaaSampleCount = 8;
    public override void ValidSettingsChange(string matchedSettingName, string optionValue)
    {
        if(matchedSettingName != BasisSettingsDefaults.Antialiasing.BindingKey)
        {
            return;
        }
        UniversalRenderPipelineAsset Asset = (UniversalRenderPipelineAsset)QualitySettings.renderPipeline;
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
                Camera.TryGetComponent<UniversalAdditionalCameraData>(out Data);
            }
        }
        if (Camera == null || Data == null)
        {
            BasisDebug.LogError("Missing Camera Or Data!");
            return;
        }
        BasisDebug.Log($"Antialiasing Changed to {optionValue}", BasisDebug.LogTag.Local);
        switch (optionValue)
        {
            case "msaa off":
                Asset.msaaSampleCount = 1;
                Camera.allowMSAA = false;
                Data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
                Data.antialiasingQuality = AntialiasingQuality.Low;
                Asset.upscalingFilter = UpscalingFilterSelection.Auto;
                break;
            case "msaa 2x":
                Asset.msaaSampleCount = LowmsaaSampleCount;
                Camera.allowMSAA = true;
                Data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
                Data.antialiasingQuality = AntialiasingQuality.Low;
                Asset.upscalingFilter = UpscalingFilterSelection.Auto;
                break;
            case "msaa 4x":
                Asset.msaaSampleCount = MediumLowmsaaSampleCount;
                Camera.allowMSAA = true;
                Data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
                Data.antialiasingQuality = AntialiasingQuality.Medium;
                Asset.upscalingFilter = UpscalingFilterSelection.Auto;
                break;
            case "msaa 8x":
                Asset.msaaSampleCount = HighmsaaSampleCount;
                Camera.allowMSAA = true;
                Data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
                Data.antialiasingQuality = AntialiasingQuality.High;
                Asset.upscalingFilter = UpscalingFilterSelection.Auto;
                break;
            case "linear":
                Asset.upscalingFilter = UpscalingFilterSelection.Linear;
                Camera.allowMSAA = false;
                Data.antialiasing = AntialiasingMode.None;
                Data.antialiasingQuality = AntialiasingQuality.Low;
                break;
            case "point":
                Asset.upscalingFilter = UpscalingFilterSelection.Point;
                Camera.allowMSAA = false;
                Data.antialiasing = AntialiasingMode.None;
                Data.antialiasingQuality = AntialiasingQuality.Low;
                break;
            case "fsr":
                Asset.upscalingFilter = UpscalingFilterSelection.FSR;
                Camera.allowMSAA = false;
                Data.antialiasing = AntialiasingMode.None;
                Data.antialiasingQuality = AntialiasingQuality.Low;
                break;
            case "stp":
                Asset.upscalingFilter = UpscalingFilterSelection.STP;
                Camera.allowMSAA = false;
                Data.antialiasing = AntialiasingMode.None;
                Data.antialiasingQuality = AntialiasingQuality.Low;
                break;
        }
    }
    public override void ChangedSettings()
    {
    }
}
