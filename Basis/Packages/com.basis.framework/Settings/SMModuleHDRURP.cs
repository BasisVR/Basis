
using UnityEngine;
using UnityEngine.Rendering.Universal;

public class SMModuleHDRURP : BasisSettingsBase
{
    public override void ValidSettingsChange(string matchedSettingName, string optionValue)
    {
        UniversalRenderPipelineAsset Asset = (UniversalRenderPipelineAsset)QualitySettings.renderPipeline;
#if UNITY_ANDROID
        Asset.hdrColorBufferPrecision = HDRColorBufferPrecision._32Bits;
        Asset.supportsHDR = false;
                if (Camera.main != null)
                {
                    Camera.main.allowHDR = false;
                }
#else
        switch (optionValue)
        {
            case "64bit":
                Asset.hdrColorBufferPrecision = HDRColorBufferPrecision._64Bits;
                Asset.supportsHDR = true;
                if (Camera.main != null)
                {
                    Camera.main.allowHDR = true;
                }
                break;
            case "32bit":
                Asset.hdrColorBufferPrecision = HDRColorBufferPrecision._32Bits;
                Asset.supportsHDR = true;
                if (Camera.main != null)
                {
                    Camera.main.allowHDR = true;
                }
                break;
            case "off":
                Asset.hdrColorBufferPrecision = HDRColorBufferPrecision._32Bits;
                Asset.supportsHDR = false;
                if (Camera.main != null)
                {
                    Camera.main.allowHDR = false;
                }
                break;
        }
#endif
    }
}
