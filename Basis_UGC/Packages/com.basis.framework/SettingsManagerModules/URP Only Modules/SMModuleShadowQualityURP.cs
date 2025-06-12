#if SETTINGS_MANAGER_UNIVERSAL
using BattlePhaze.SettingsManager;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using ShadowResolution = UnityEngine.Rendering.Universal.ShadowResolution;

public class SMModuleShadowQualityURP : SettingsManagerOption
{
    public override void ReceiveOption(SettingsMenuInput Option, SettingsManager Manager)
    {
        if (NameReturn(0, Option))
        {
            ChangeShadowQuality(Option.SelectedValue);
        }
    }
    public void ChangeShadowQuality(string Quality)
    {
#if UNITY_ANDROID
        UniversalRenderPipelineAsset Asset = (UniversalRenderPipelineAsset)QualitySettings.renderPipeline;
        Asset.shadowCascadeCount = 2;               // Four cascades for shadow quality
        // Set cascade splits based on intended distances as a fraction of shadowDistance
        Asset.cascade2Split = 0.12f;                // 12% for 2-cascade setting
        Asset.cascade3Split = new Vector2(0.12f, 0.5f);  // 12% and 50% for 3-cascade setting
        Asset.cascade4Split = new Vector3(0.12f, 0.3f, 0.6f); // 12%, 30%, and 60% for 4-cascade setting
        Asset.mainLightShadowmapResolution = 256;
        Asset.additionalLightsShadowmapResolution = 256;
        Asset.maxAdditionalLightsCount = 0;
        Asset.shadowDistance = 5;
#else
        UniversalRenderPipelineAsset Asset = (UniversalRenderPipelineAsset)QualitySettings.renderPipeline;
        Asset.shadowCascadeCount = 4;               // Four cascades for shadow quality
        // Set cascade splits based on intended distances as a fraction of shadowDistance
        Asset.cascade2Split = 0.12f;                // 12% for 2-cascade setting
        Asset.cascade3Split = new Vector2(0.12f, 0.5f);  // 12% and 50% for 3-cascade setting
        Asset.cascade4Split = new Vector3(0.12f, 0.3f, 0.6f); // 12%, 30%, and 60% for 4-cascade setting
        switch (Quality)
        {
            case "very low":
                Asset.mainLightShadowmapResolution = 256;
                Asset.additionalLightsShadowmapResolution = 256;
                Asset.maxAdditionalLightsCount = 0;
                Asset.shadowDistance = 5;
                break;
            case "low":
                Asset.mainLightShadowmapResolution = 512;
                Asset.additionalLightsShadowmapResolution = 512;
                Asset.maxAdditionalLightsCount = 2;
                Asset.shadowDistance = 150;
                break;
            case "medium":
                Asset.mainLightShadowmapResolution = 2048;
                Asset.additionalLightsShadowmapResolution = 2048;
                Asset.maxAdditionalLightsCount = 8;
                Asset.shadowDistance = 150;
                break;
            case "high":
                Asset.mainLightShadowmapResolution = 4096;
                Asset.additionalLightsShadowmapResolution = 4096;
                Asset.maxAdditionalLightsCount = 12;
                Asset.shadowDistance = 150;
                break;
            case "ultra":
                Asset.mainLightShadowmapResolution = 8192;
                Asset.additionalLightsShadowmapResolution = 8192;
                Asset.maxAdditionalLightsCount = 16;
                Asset.shadowDistance = 150;
                break;
        }
#endif
    }
    private System.Type universalRenderPipelineAssetType;
    private FieldInfo mainLightShadowmapResolutionFieldInfo;
    private FieldInfo additionalLightsRenderingMode;
    private void InitializeShadowMapFieldInfo()
    {
        UniversalRenderPipelineAsset Asset = (UniversalRenderPipelineAsset)QualitySettings.renderPipeline;
        universalRenderPipelineAssetType = Asset.GetType();
        mainLightShadowmapResolutionFieldInfo = universalRenderPipelineAssetType.GetField("m_MainLightShadowmapResolution", BindingFlags.Instance | BindingFlags.NonPublic);
        additionalLightsRenderingMode = universalRenderPipelineAssetType.GetField("m_AdditionalLightsShadowmapResolution", BindingFlags.Instance | BindingFlags.NonPublic);
    }

    public ShadowResolution MainLightShadowResolution
    {

        get
        {
            UniversalRenderPipelineAsset Asset = (UniversalRenderPipelineAsset)QualitySettings.renderPipeline;
            if (mainLightShadowmapResolutionFieldInfo == null)
            {
                InitializeShadowMapFieldInfo();
            }
            return (ShadowResolution)mainLightShadowmapResolutionFieldInfo.GetValue(Asset);
        }
        set
        {
            UniversalRenderPipelineAsset Asset = (UniversalRenderPipelineAsset)QualitySettings.renderPipeline;
            if (mainLightShadowmapResolutionFieldInfo == null)
            {
                InitializeShadowMapFieldInfo();
            }
            mainLightShadowmapResolutionFieldInfo.SetValue(Asset, value);
        }
    }
    public ShadowResolution AdditionalLightsShadowResolution
    {
        get
        {
            UniversalRenderPipelineAsset Asset = (UniversalRenderPipelineAsset)QualitySettings.renderPipeline;
            if (additionalLightsRenderingMode == null)
            {
                InitializeShadowMapFieldInfo();
            }
            return (ShadowResolution)additionalLightsRenderingMode.GetValue(Asset);
        }
        set
        {
            UniversalRenderPipelineAsset Asset = (UniversalRenderPipelineAsset)QualitySettings.renderPipeline;
            if (additionalLightsRenderingMode == null)
            {
                InitializeShadowMapFieldInfo();
            }
            additionalLightsRenderingMode.SetValue(Asset, value);
        }
    }
}
#endif
