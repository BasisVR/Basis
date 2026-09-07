using System.Globalization;
using Basis.BasisUI;
using UnityEngine;
using UnityEngine.Serialization;

public class BasisTextureQualityModule : BasisSettingsBase
{
    public int StreamingMipmapsMaxLevelReduction = 4;

    [FormerlySerializedAs("treamingMipmapsMaxFileIORequests")]
    public int StreamingMipmapsMaxFileIORequests = 512;

    public const int MinimumBudgetMegabytes = 256;

    public override void Awake()
    {
        base.Awake();
        // The store only broadcasts keys it already holds, so a build that has never had Memory
        // Allocation touched would otherwise run on whatever budget the quality asset shipped with.
        Apply(BasisSettingsDefaults.MemoryAllocation.RawValue);
    }

    public override void ValidSettingsChange(string matchedSettingName, string optionValue)
    {
        if (matchedSettingName != BasisSettingsDefaults.MemoryAllocation.BindingKey)
            return;

        Apply(optionValue);
    }

    private void Apply(string optionValue)
    {
        QualitySettings.streamingMipmapsActive = true;
        QualitySettings.streamingMipmapsAddAllCameras = true;
        QualitySettings.streamingMipmapsMaxLevelReduction = StreamingMipmapsMaxLevelReduction;
        QualitySettings.streamingMipmapsMaxFileIORequests = StreamingMipmapsMaxFileIORequests;

        // graphicsMemorySize reports 0 on drivers that do not publish it; a 0 cap would pin every
        // streamed texture to its lowest mip rather than freeing memory.
        float hardwareCap = SystemInfo.graphicsMemorySize > 0
            ? Mathf.Max(SystemInfo.graphicsMemorySize / 4f, MinimumBudgetMegabytes)
            : QualitySettings.streamingMipmapsMemoryBudget;
        float requested = int.TryParse(optionValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mem) ? mem : hardwareCap;
        QualitySettings.streamingMipmapsMemoryBudget = Mathf.Max(Mathf.Min(requested, hardwareCap), MinimumBudgetMegabytes);
    }

    public override void ChangedSettings()
    {
    }
}
