#if SETTINGS_MANAGER_UNIVERSAL
using BattlePhaze.SettingsManager;
using UnityEngine;
using UnityEngine.Rendering.Universal;

public class SMModuleHDRURP : SettingsManagerOption
{
    public override void ReceiveOption(SettingsMenuInput Option, SettingsManager Manager)
    {
        if (NameReturn(0, Option))
        {
            SetHDRPrecision(Option.SelectedValue);
        }
    }
    public void SetHDRPrecision(string SelectedValue)
    {
        UniversalRenderPipelineAsset Asset = (UniversalRenderPipelineAsset)QualitySettings.renderPipeline;
#if UNITY_ANDROID
        Asset.hdrColorBufferPrecision = HDRColorBufferPrecision._32Bits;
        Asset.supportsHDR = false;
#else
        switch (SelectedValue)
        {
            case "ultra":
                Asset.hdrColorBufferPrecision = HDRColorBufferPrecision._64Bits;
                Asset.supportsHDR = true;
                break;
            case "normal":
                Asset.hdrColorBufferPrecision = HDRColorBufferPrecision._32Bits;
                Asset.supportsHDR = true;
                break;
            case "off":
                Asset.hdrColorBufferPrecision = HDRColorBufferPrecision._32Bits;
                Asset.supportsHDR = false;
                break;
        }
#endif
    }
}
#endif
