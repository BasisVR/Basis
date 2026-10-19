using UnityEditor.Rendering.Universal;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace UnityEditor.Rendering
{
    class OnTilePostProcessResourceStripper : IRenderPipelineGraphicsSettingsStripper<OnTilePostProcessResource>
    {
        public bool active => URPBuildData.instance.buildingPlayerForUniversalRenderPipeline;

        public bool CanRemoveSettings(OnTilePostProcessResource resources)
        {
            if (GraphicsSettings.TryGetRenderPipelineSettings<URPShaderStrippingSetting>(out var urpShaderStrippingSettings) && !urpShaderStrippingSettings.stripUnusedVariants)
                return false;
            
            foreach (var rendererData in URPBuildData.instance.rendererDataList)
            {
                if (rendererData is not UniversalRendererData)
                    continue;

                foreach (var rendererFeature in rendererData.rendererFeatures)
                {
                    if (rendererFeature is OnTilePostProcessFeature { isActive: true })
                        return false;
                }
            }

            return true;
        }
    }
}
