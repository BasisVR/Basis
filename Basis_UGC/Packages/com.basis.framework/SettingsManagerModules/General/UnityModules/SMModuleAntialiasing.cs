using UnityEngine;
#if SETTINGS_MANAGER_LEGACY && UNITY_POST_PROCESSING_STACK_V2
using UnityEngine.Rendering.PostProcessing;
#endif
#if SETTINGS_MANAGER_HD
using UnityEngine.Rendering.HighDefinition;
#endif
namespace BattlePhaze.SettingsManager.Integrations
{
    public class SMModuleAntialiasing : SettingsManagerOption
    {
        public Camera Cam;

        public override void ReceiveOption(SettingsMenuInput Option, SettingsManager Manager)
        {
            if (NameReturn(0, Option))
            {
                ChangeAntialiasing(Option.SelectedValue);
            }
        }

        public void ChangeAntialiasing(string Quality)
        {
            if (Cam == null)
            {
                Cam = FindFirstObjectByType<Camera>();
            }
#if SETTINGS_MANAGER_HD
            if (Cam != null && Cam.TryGetComponent(out HDAdditionalCameraData HDAdditionalCameraData))
            {
                HDAdditionalCameraData.antialiasing = Quality switch
                {
                    "taa" => HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing,
                    "fxaa" => HDAdditionalCameraData.AntialiasingMode.FastApproximateAntialiasing,
                    "smaa" => HDAdditionalCameraData.AntialiasingMode.SubpixelMorphologicalAntiAliasing,
                    "noaa" => HDAdditionalCameraData.AntialiasingMode.None,
                    _ => HDAdditionalCameraData.antialiasing,
                };
            }
#endif

#if SETTINGS_MANAGER_LEGACY && UNITY_POST_PROCESSING_STACK_V2
            if (Cam != null && Cam.TryGetComponent(out PostProcessLayer Layer))
            {
                Layer.antialiasingMode = Quality switch
                {
                    "taa" => PostProcessLayer.Antialiasing.TemporalAntialiasing,
                    "fxaa" => PostProcessLayer.Antialiasing.FastApproximateAntialiasing,
                    "smaa" => PostProcessLayer.Antialiasing.SubpixelMorphologicalAntialiasing,
                    "noaa" => PostProcessLayer.Antialiasing.None,
                    _ => Layer.antialiasingMode,
                };

                if (Layer.antialiasingMode == PostProcessLayer.Antialiasing.SubpixelMorphologicalAntialiasing)
                {
                    Layer.subpixelMorphologicalAntialiasing.quality = SubpixelMorphologicalAntialiasing.Quality.High;
                }
            }
#endif
        }
    }
}