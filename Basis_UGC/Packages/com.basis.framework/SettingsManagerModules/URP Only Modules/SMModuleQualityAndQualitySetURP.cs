using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace BattlePhaze.SettingsManager.Intergrations
{
    public class SMModuleQualityAndQualitySetURP : SettingsManagerOption
    {
        public UniversalAdditionalCameraData Data;

        public override void ReceiveOption(SettingsMenuInput Option, SettingsManager Manager)
        {
            if (NameReturn(0, Option))
            {
#if UNITY_ANDROID
#else
  //    ChangeOpaque(Option.SelectedValue);
#endif
                QualitySettings.SetQualityLevel(QualitySettings.GetQualityLevel(), true);
            }
            if (NameReturn(1, Option))
            {
#if UNITY_ANDROID
#else
    //  ChangeDepth(Option.SelectedValue);
#endif
                QualitySettings.SetQualityLevel(QualitySettings.GetQualityLevel(), true);
            }
            if (NameReturn(2, Option))
            {
                ChangeQualityLevel(Option.SelectedValue);

            }
#if UNITY_ANDROID
            if (NameReturn(2, Option))
            {
                ChangeQualityLevel("very low");

            }
#else
            if (NameReturn(2, Option))
            {
                ChangeQualityLevel(Option.SelectedValue);

            }
#endif
        }

        public Camera Camera;

        private void EnsureCameraData()
        {
            if (Camera == null)
            {
                Camera = Camera.main;
                Data = Camera.GetComponent<UniversalAdditionalCameraData>();
            }
        }

        public void ChangeOpaque(string value)
        {
            EnsureCameraData();

            if (Data != null)
            {
                bool State = value == "on";
                Data.requiresColorOption = State ? CameraOverrideOption.On: CameraOverrideOption.Off;
                Data.requiresColorTexture = State;
                BasisDebug.Log($"Opaque rendering set to {value}.");
            }
        }

        public void ChangeDepth(string value)
        {
            EnsureCameraData();

            if (Data != null)
            {
                bool State = value == "on";
                Data.requiresDepthOption = State? CameraOverrideOption.On: CameraOverrideOption.Off;
                Data.requiresDepthTexture = State;
                BasisDebug.Log($"Depth rendering set to {value}.");
            }
        }
        public void ChangeQualityLevel(string quality)
        {
            EnsureCameraData();

            switch (quality)
            {
                case "very low":
                    ApplyQualitySettings(AnisotropicFiltering.Enable, 256, false, false);
                    Data.renderPostProcessing = false;
                    break;
                case "low":
                    ApplyQualitySettings(AnisotropicFiltering.Enable, 512, true, true);
                    Data.renderPostProcessing = true;
                    break;
                case "medium":
                    ApplyQualitySettings(AnisotropicFiltering.Enable, 1024, true, true);
                    Data.renderPostProcessing = true;
                    break;
                case "high":
                    ApplyQualitySettings(AnisotropicFiltering.Enable, 2048, true, true);
                    Data.renderPostProcessing = true;
                    break;
                case "ultra":
                    ApplyQualitySettings(AnisotropicFiltering.Enable, 4096, true, true);
                    Data.renderPostProcessing = true;
                    break;
            }
        }

        private void ApplyQualitySettings(
            AnisotropicFiltering anisotropicFilter,
            int particleBudget,
            bool renderShadows,
            bool stopNaN)
        {
            QualitySettings.anisotropicFiltering = anisotropicFilter;
            QualitySettings.particleRaycastBudget = particleBudget;
            QualitySettings.SetQualityLevel(QualitySettings.GetQualityLevel(), true);

            if (Data != null)
            {
                Data.renderShadows = renderShadows;
                Data.stopNaN = stopNaN;
            }
        }
    }
}
