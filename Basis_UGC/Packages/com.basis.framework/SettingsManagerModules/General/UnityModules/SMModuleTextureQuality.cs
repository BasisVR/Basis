using UnityEngine;

namespace BattlePhaze.SettingsManager.Intergrations
{
    public class SMModuleTextureQuality : SettingsManagerOption
    {
        [Space]
        public int veryLowStreamingMipmapsMaxLevelReduction = 4;
        public int veryLowStreamingMipmapsMaxFileIORequests = 512;
        [Space]
        public int lowStreamingMipmapsMaxLevelReduction = 4;
        public int lowStreamingMipmapsMaxFileIORequests = 512;
        [Space]
        public int mediumStreamingMipmapsMaxLevelReduction = 4;
        public int mediumStreamingMipmapsMaxFileIORequests = 512;
        [Space]
        public int highStreamingMipmapsMaxLevelReduction = 4;
        public int highStreamingMipmapsMaxFileIORequests = 512;
        [Space]
        public int ultraStreamingMipmapsMaxLevelReduction = 4;
        public int ultraStreamingMipmapsMaxFileIORequests = 512;

        public override void ReceiveOption(SettingsMenuInput option, SettingsManager manager)
        {
            if (NameReturn(0, option))
            {
                ChangeTextureQuality(option.SelectedValue);
            }
            if (NameReturn(1, option))
            {
                ChangeMemoryAllocation(option.SelectedValue);
            }
        }

        public void ChangeTextureQuality(string quality)
        {
#if UNITY_2017
            BasisDebug.Log("2017 does not support texture streaming");
#else
            QualitySettings.streamingMipmapsActive = true;
            QualitySettings.streamingMipmapsAddAllCameras = true;
            switch (quality.ToLower())
            {
                case "very low":
                    QualitySettings.streamingMipmapsMaxLevelReduction = veryLowStreamingMipmapsMaxLevelReduction;
                    QualitySettings.streamingMipmapsMaxFileIORequests = veryLowStreamingMipmapsMaxFileIORequests;
                    break;
                case "low":
                    QualitySettings.streamingMipmapsMaxLevelReduction = lowStreamingMipmapsMaxLevelReduction;
                    QualitySettings.streamingMipmapsMaxFileIORequests = lowStreamingMipmapsMaxFileIORequests;
                    break;
                case "medium":
                    QualitySettings.streamingMipmapsMaxLevelReduction = mediumStreamingMipmapsMaxLevelReduction;
                    QualitySettings.streamingMipmapsMaxFileIORequests = mediumStreamingMipmapsMaxFileIORequests;
                    break;
                case "high":
                    QualitySettings.streamingMipmapsMaxLevelReduction = highStreamingMipmapsMaxLevelReduction;
                    QualitySettings.streamingMipmapsMaxFileIORequests = highStreamingMipmapsMaxFileIORequests;
                    break;
                case "ultra":
                    QualitySettings.streamingMipmapsMaxLevelReduction = ultraStreamingMipmapsMaxLevelReduction;
                    QualitySettings.streamingMipmapsMaxFileIORequests = ultraStreamingMipmapsMaxFileIORequests;
                    break;
                default:
                    BasisDebug.LogError($"Invalid texture quality value: {quality}");
                    break;
            }
#endif
        }

        public void ChangeMemoryAllocation(string memoryAllocation)
        {
#if UNITY_2017
            BasisDebug.Log("2017 does not support texture streaming");
#else
            if (int.TryParse(memoryAllocation, out int mem))
            {
                QualitySettings.streamingMipmapsMemoryBudget = mem;
            }
            else
            {
                QualitySettings.streamingMipmapsMemoryBudget = SystemInfo.graphicsMemorySize;
            }
#endif
        }
    }
}
