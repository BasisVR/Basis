using Basis.BasisUI;
using UnityEngine;
using UnityEngine.Serialization;

public class BasisTextureQualityModule : BasisSettingsBase
{
    public int StreamingMipmapsMaxLevelReduction = 4;

    [FormerlySerializedAs("treamingMipmapsMaxFileIORequests")]
    public int StreamingMipmapsMaxFileIORequests = 512;

    public override void ValidSettingsChange(string matchedSettingName, string optionValue)
    {
        if (matchedSettingName != BasisSettingsDefaults.MemoryAllocation.BindingKey)
            return;

        QualitySettings.streamingMipmapsActive = true;
        QualitySettings.streamingMipmapsAddAllCameras = true;
        QualitySettings.streamingMipmapsMaxLevelReduction = StreamingMipmapsMaxLevelReduction;
        QualitySettings.streamingMipmapsMaxFileIORequests = StreamingMipmapsMaxFileIORequests;

        int hardwareCap = SystemInfo.graphicsMemorySize / 4;
        int requested = int.TryParse(optionValue, out int mem) ? mem : hardwareCap;
        QualitySettings.streamingMipmapsMemoryBudget = Mathf.Min(requested, hardwareCap);
    }

    public override void ChangedSettings()
    {
    }
}
