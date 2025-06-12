using BattlePhaze.SettingsManager;
using UnityEngine;
using UnityEngine.Rendering;

#if SETTINGS_MANAGER_HD
using UnityEngine.Rendering.HighDefinition;
#endif

#if SETTINGS_MANAGER_LEGACY && UNITY_POST_PROCESSING_STACK_V2
using UnityEngine.Rendering.PostProcessing;
#endif

namespace BattlePhaze.SettingsManager.Integrations
{
    public class SMModuleAmbientOcclusion : SettingsManagerOption
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
            AmbientOcclusion AmbientOcclusion;
            if (FindObjectOfType<Volume>())
            {
                FindObjectOfType<Volume>().sharedProfile.TryGet(out AmbientOcclusion);
                if (AmbientOcclusion != null)
                {
                    switch (Quality)
                    {
                        case "very low":
                            AmbientOcclusion.active = false;
                            break;
                        case "low":
                            AmbientOcclusion.active = true;
                            AmbientOcclusion.fullResolution = false;
                            break;
                        case "medium":
                            AmbientOcclusion.active = true;
                            AmbientOcclusion.fullResolution = false;
                            AmbientOcclusion.stepCount = 12;
                            break;
                        case "high":
                            AmbientOcclusion.active = true;
                            AmbientOcclusion.fullResolution = true;
                            AmbientOcclusion.stepCount = 16;
                            break;
                        case "ultra":
                            AmbientOcclusion.active = true;
                            AmbientOcclusion.fullResolution = true;
                            AmbientOcclusion.stepCount = 32;
                            break;
                    }
                }
            }
#endif

#if SETTINGS_MANAGER_LEGACY && UNITY_POST_PROCESSING_STACK_V2
            AmbientOcclusion AmbientOcclusion;
            if (FindObjectOfType<PostProcessVolume>())
            {
                FindObjectOfType<PostProcessVolume>().sharedProfile.TryGetSettings(out AmbientOcclusion);
                if (AmbientOcclusion != null)
                {
                    switch (Quality)
                    {
                        case "very low":
                            AmbientOcclusion.quality.value = UnityEngine.Rendering.PostProcessing.AmbientOcclusionQuality.Lowest;
                            break;
                        case "low":
                            AmbientOcclusion.quality.value = UnityEngine.Rendering.PostProcessing.AmbientOcclusionQuality.Low;
                            break;
                        case "medium":
                            AmbientOcclusion.quality.value = UnityEngine.Rendering.PostProcessing.AmbientOcclusionQuality.Medium;
                            break;
                        case "high":
                            AmbientOcclusion.quality.value = UnityEngine.Rendering.PostProcessing.AmbientOcclusionQuality.High;
                            break;
                        case "ultra":
                            AmbientOcclusion.quality.value = UnityEngine.Rendering.PostProcessing.AmbientOcclusionQuality.Ultra;
                            break;
                    }
                }
            }
#endif
        }
    }
}