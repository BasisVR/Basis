using UnityEngine;
using UnityEngine.Rendering.Universal;
using Basis.BasisUI;

public class SMModuleHDRURP : BasisSettingsBase
{
    // --- Canonical setting key (from defaults) ---
    private static string K_HDR_SUPPORT => BasisSettingsDefaults.HDRSupport.BindingKey; // "hdr support"

    public override void ValidSettingsChange(string matchedSettingName, string optionValue)
    {
        // Only react to the HDR setting
        if (matchedSettingName != K_HDR_SUPPORT)
            return;

        UniversalRenderPipelineAsset asset =
            (UniversalRenderPipelineAsset)QualitySettings.renderPipeline;

#if UNITY_ANDROID || UNITY_IOS
        // Mobile platforms: HDR forced off
        asset.hdrColorBufferPrecision = HDRColorBufferPrecision._32Bits;
        asset.supportsHDR = false;

        if (Camera.main != null)
        {
            Camera.main.allowHDR = false;
        }
#else
        switch (optionValue)
        {
            case "64bit":
                asset.hdrColorBufferPrecision = HDRColorBufferPrecision._64Bits;
                asset.supportsHDR = true;
                if (Camera.main != null)
                {
                    Camera.main.allowHDR = true;
                }
                break;

            case "32bit":
                asset.hdrColorBufferPrecision = HDRColorBufferPrecision._32Bits;
                asset.supportsHDR = true;
                if (Camera.main != null)
                {
                    Camera.main.allowHDR = true;
                }
                break;

            case "off":
                asset.hdrColorBufferPrecision = HDRColorBufferPrecision._32Bits;
                asset.supportsHDR = false;
                if (Camera.main != null)
                {
                    Camera.main.allowHDR = false;
                }
                break;
        }
#endif
    }

    public override void ChangedSettings()
    {
    }
}
