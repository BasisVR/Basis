using BattlePhaze.SettingsManager;
using UnityEngine.Rendering;
using UnityEngine;
#if SETTINGS_MANAGER_HD
using UnityEngine.Rendering.HighDefinition;
#endif
#if SETTINGS_MANAGER_LEGACY && UNITY_POST_PROCESSING_STACK_V2
using UnityEngine.Rendering.PostProcessing;
#endif
namespace BattlePhaze.SettingsManager.Intergrations
{
    public class SMModuleClosePointShadows : SettingsManagerOption
    {
        public override void ReceiveOption(SettingsMenuInput Option, SettingsManager Manager)
        {
            if (NameReturn(0, Option))
            {
                ChangeClosePointQuality(Option.SelectedValue);
            }
        }
        public void ChangeClosePointQuality(string Quality)
        {
#if SETTINGS_MANAGER_HD
        ContactShadows ContactShadow;
        if (FindObjectOfType<Volume>())
        {
            FindObjectOfType<Volume>().sharedProfile.TryGet(out ContactShadow);
            if (ContactShadow != null)
            {
                switch (Quality)
                {
                    case "very low":
                        ContactShadow.active = false;
                        break;
                    case "low":
                        ContactShadow.active = false;
                        break;
                    case "medium":
                        ContactShadow.active = true;
                        ContactShadow.sampleCount = 32;
                        break;
                    case "high":
                        ContactShadow.active = true;
                        ContactShadow.sampleCount = 64;
                        break;
                    case "ultra":
                        ContactShadow.active = true;
                        ContactShadow.sampleCount = 64;
                        break;
                }
            }
        }
#endif
        }
    }
}