using UnityEngine;
namespace BattlePhaze.SettingsManager
{
    [System.Serializable]
    public class SMModuleGenericToggle : SettingsManagerOption
    {
        public SettingsMenuInput OptionSettings;
        [SerializeField]
        public SMModuleGenericToggleObjectToggle[] ObjectToggles;
        public override void ReceiveOption(SettingsMenuInput Option, SettingsManager Manager)
        {
            if (NameReturn(0, Option))
            {
                ChangeClosePointQuality(Option.SelectedValue);
            }
        }
        /// <summary>
        /// Changes the quality level components
        /// (does not set the actual quality setting level as this would interfere with other modules)
        /// </summary>
        /// <param name="Quality"></param>
        public void ChangeClosePointQuality(string Quality)
        {
            for (int ObjectTogglesIndex = 0; ObjectTogglesIndex < ObjectToggles.Length; ObjectTogglesIndex++)
            {
                if (ObjectToggles[ObjectTogglesIndex].LookingForQualityLevelValue == Quality)
                {
                    for (int ObjectToggleComponentOff = 0; ObjectToggleComponentOff < ObjectToggles[ObjectToggleComponentOff].ComponentOff.Length; ObjectToggleComponentOff++)
                    {
                        ObjectToggles[ObjectTogglesIndex].ComponentOff[ObjectToggleComponentOff].enabled = false;
                    }
                    for (int ObjectToggleComponentOn = 0; ObjectToggleComponentOn < ObjectToggles[ObjectToggleComponentOn].ComponentOn.Length; ObjectToggleComponentOn++)
                    {
                        ObjectToggles[ObjectTogglesIndex].ComponentOff[ObjectToggleComponentOn].enabled = true;
                    }
                    for (int ToggleOffIndex = 0; ToggleOffIndex < ObjectToggles[ToggleOffIndex].ToggleOff.Length; ToggleOffIndex++)
                    {
                        ObjectToggles[ObjectTogglesIndex].ToggleOff[ToggleOffIndex].SetActive(false);
                    }
                    for (int ToggleOnIndex = 0; ToggleOnIndex < ObjectToggles[ToggleOnIndex].ToggleOn.Length; ToggleOnIndex++)
                    {
                        ObjectToggles[ObjectTogglesIndex].ToggleOn[ToggleOnIndex].SetActive(true);
                    }
                }
            }
        }
    }
}