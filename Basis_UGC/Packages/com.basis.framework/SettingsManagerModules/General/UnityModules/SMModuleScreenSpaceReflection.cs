#if SETTINGS_MANAGER_HD 
using UnityEngine.Rendering.HighDefinition;
#endif
namespace BattlePhaze.SettingsManager.Intergrations
{
    public class SMModuleScreenSpaceReflection : SettingsManagerOption
    {
        public override void ReceiveOption(SettingsMenuInput Option, SettingsManager Manager)
        {
            if (NameReturn(0, Option))
            {
                AmbientOcclusionQuality(Option.SelectedValue);
            }
        }
        public void AmbientOcclusionQuality(string Quality)
        {
#if SETTINGS_MANAGER_HD
            ScreenSpaceReflection ScreenSpaceReflection;
            if (FindObjectOfType<Volume>())
            {
                FindObjectOfType<Volume>().sharedProfile.TryGet(out ScreenSpaceReflection);
                if (ScreenSpaceReflection != null)
                {
                    switch (Quality)
                    {
                        case "very low":
                            ScreenSpaceReflection.active = true;
                            ScreenSpaceReflection.rayMaxIterations = 4;
                            break;
                        case "low":
                            ScreenSpaceReflection.active = true;
                            ScreenSpaceReflection.rayMaxIterations = 8;
                            break;
                        case "medium":
                            ScreenSpaceReflection.active = true;
                            ScreenSpaceReflection.rayMaxIterations = 12;
                            break;
                        case "high":
                            ScreenSpaceReflection.active = true;
                            ScreenSpaceReflection.rayMaxIterations = 16;
                            break;
                        case "ultra":
                            ScreenSpaceReflection.active = true;
                            ScreenSpaceReflection.rayMaxIterations = 32;
                            break;
                    }
                }
            }
#endif
        }
    }
}
