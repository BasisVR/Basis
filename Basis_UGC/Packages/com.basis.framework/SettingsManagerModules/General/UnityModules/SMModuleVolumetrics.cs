using BattlePhaze.SettingsManager;
using UnityEngine.Rendering;
using UnityEngine;
#if SETTINGS_MANAGER_HD
using UnityEngine.Rendering.HighDefinition;
#endif
namespace BattlePhaze.SettingsManager.Integrations
{
    public class SMModuleVolumetrics : SettingsManagerOption
    {
        public override void ReceiveOption(SettingsMenuInput option, SettingsManager manager)
        {
            if (NameReturn(0, option))
            {
                ChangeVolumetricQuality(option.SelectedValue);
            }
        }

        private void ChangeVolumetricQuality(string quality)
        {
#if SETTINGS_MANAGER_HD
            if (FindObjectOfType<Volume>() is { } volume)
            {
                volume.sharedProfile.TryGet(out Fog fog);
                if (fog != null)
                {
                    fog.enableVolumetricFog.value = quality switch
                    {
                        "very low" => false,
                        "low" => false,
                        "medium" => true,
                        "high" => true,
                        "ultra" => true,
                        _ => fog.enableVolumetricFog.value
                    };
                }
            }
#endif
        }
    }
}