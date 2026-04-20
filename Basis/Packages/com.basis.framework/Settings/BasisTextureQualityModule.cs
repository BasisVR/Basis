using Basis.BasisUI;
using UnityEngine;
public class BasisTextureQualityModule : BasisSettingsBase
{
    public int StreamingMipmapsMaxLevelReduction = 4;
    public int treamingMipmapsMaxFileIORequests = 512;
    public override void ValidSettingsChange(string matchedSettingName, string optionValue)
    {
        if (matchedSettingName == BasisSettingsDefaults.MemoryAllocation.BindingKey)
        {
            QualitySettings.streamingMipmapsActive = true;
            QualitySettings.streamingMipmapsAddAllCameras = true;
            QualitySettings.streamingMipmapsMaxLevelReduction = StreamingMipmapsMaxLevelReduction;
            QualitySettings.streamingMipmapsMaxFileIORequests = treamingMipmapsMaxFileIORequests;
            if (int.TryParse(optionValue, out int mem))
            {
                QualitySettings.streamingMipmapsMemoryBudget = mem;
            }
            else
            {
                QualitySettings.streamingMipmapsMemoryBudget = SystemInfo.graphicsMemorySize;
            }
        }
    }
    public override void ChangedSettings()
    {
    }
}
