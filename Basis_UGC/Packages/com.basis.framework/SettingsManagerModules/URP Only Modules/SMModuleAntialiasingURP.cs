#if SETTINGS_MANAGER_UNIVERSAL
using BattlePhaze.SettingsManager;
using UnityEngine;
using UnityEngine.Rendering.Universal;
public class SMModuleAntialiasingURP : SettingsManagerOption
{
    public Camera Camera;
    public UniversalAdditionalCameraData Data;
    public override void ReceiveOption(SettingsMenuInput Option, SettingsManager Manager)
    {
        if (NameReturn(0, Option))
        {
            SetMSAAQuality(Option.SelectedValue);
        }
    }
    public int LowmsaaSampleCount = 2;
    public int MediumLowmsaaSampleCount = 4;
    public int HighmsaaSampleCount = 8;
    public void SetMSAAQuality(string Quality)
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
#if UNITY_ANDROID
        Asset.msaaSampleCount = 2;
        Camera.allowMSAA = false;
        Data.antialiasing = AntialiasingMode.None;
        Data.antialiasingQuality = AntialiasingQuality.Low;
#else
        switch (Quality)
        {
            case "very low":
                Asset.msaaSampleCount = 1;
                Camera.allowMSAA = false;
                Data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
                Data.antialiasingQuality = AntialiasingQuality.Low;
                break;
            case "low":
                Asset.msaaSampleCount = LowmsaaSampleCount;
                Camera.allowMSAA = true;
                Data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
                Data.antialiasingQuality = AntialiasingQuality.Low;
                break;
            case "medium":
                Asset.msaaSampleCount = MediumLowmsaaSampleCount;
                Camera.allowMSAA = true;
                Data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
                Data.antialiasingQuality = AntialiasingQuality.Medium;
                break;
            case "high":
                Asset.msaaSampleCount = HighmsaaSampleCount;
                Camera.allowMSAA = true;
                Data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
                Data.antialiasingQuality = AntialiasingQuality.High;
                break;
            case "ultra":
                Asset.msaaSampleCount = HighmsaaSampleCount;
                Camera.allowMSAA = true;
                Data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
                Data.antialiasingQuality = AntialiasingQuality.High;
                break;
        }
#endif
    }
}
#endif
