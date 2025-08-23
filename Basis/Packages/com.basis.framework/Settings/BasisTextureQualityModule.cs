using UnityEngine;
public class BasisTextureQualityModule : BasisSettingsBase
{
    public int StreamingMipmapsMaxLevelReduction = 4;
    public int treamingMipmapsMaxFileIORequests = 512;
    public override void ValidSettingsChange(string matchedSettingName, string optionValue)
    {
        QualitySettings.streamingMipmapsActive = true;
        QualitySettings.streamingMipmapsAddAllCameras = true;
        QualitySettings.streamingMipmapsMaxLevelReduction = StreamingMipmapsMaxLevelReduction;
        QualitySettings.streamingMipmapsMaxFileIORequests = treamingMipmapsMaxFileIORequests;
        ChangeMemoryAllocation(optionValue);
    }
    public void ChangeMemoryAllocation(string memoryAllocation)
    {
        if (int.TryParse(memoryAllocation, out int mem))
        {
            QualitySettings.streamingMipmapsMemoryBudget = mem;
        }
        else
        {
            QualitySettings.streamingMipmapsMemoryBudget = SystemInfo.graphicsMemorySize;
        }
    }
}
