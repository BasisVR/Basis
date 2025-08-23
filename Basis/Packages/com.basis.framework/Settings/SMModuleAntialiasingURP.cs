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
        UniversalRenderPipelineAsset Asset = (UniversalRenderPipelineAsset)QualitySettings.renderPipeline;
        if (Camera == null)
        {
            Camera = Camera.main;
            Data = Camera.GetComponent<UniversalAdditionalCameraData>();
        }
        if (Camera == null)
        {
            return;
        }
        switch (optionValue)
        {
            case "MSAA off":
                Asset.msaaSampleCount = 1;
                Camera.allowMSAA = false;
                Data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
                Data.antialiasingQuality = AntialiasingQuality.Low;
                Asset.upscalingFilter = UpscalingFilterSelection.Auto;
                break;
            case "MSAA 2X":
                Asset.msaaSampleCount = LowmsaaSampleCount;
                Camera.allowMSAA = true;
                Data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
                Data.antialiasingQuality = AntialiasingQuality.Low;
                Asset.upscalingFilter = UpscalingFilterSelection.Auto;
                break;
            case "MSAA 4X":
                Asset.msaaSampleCount = MediumLowmsaaSampleCount;
                Camera.allowMSAA = true;
                Data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
                Data.antialiasingQuality = AntialiasingQuality.Medium;
                Asset.upscalingFilter = UpscalingFilterSelection.Auto;
                break;
            case "MSAA 8X":
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
}
