using UnityEngine;
using UnityEngine.Rendering.Universal;
namespace BattlePhaze.SettingsManager.Intergrations
{
    public class SMModuleQualityAndQualitySetURP : BasisSettingsBase
    {
        public UniversalAdditionalCameraData Data;
        public Camera Camera;
        public override void ValidSettingsChange(string matchedSettingName, string optionValue)
        {
            switch (matchedSettingName)
            {
                case "Quality Level":
                    QualitySettings.SetQualityLevel(QualitySettings.GetQualityLevel(), true);
                    if (Camera == null)
                    {
                        Camera = Camera.main;
                        Data = Camera.GetComponent<UniversalAdditionalCameraData>();
                    }
                    switch (optionValue.ToLower())
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
                    break;
            }
        }
        private void ApplyQualitySettings(AnisotropicFiltering anisotropicFilter,int particleBudget,bool renderShadows, bool stopNaN)
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
