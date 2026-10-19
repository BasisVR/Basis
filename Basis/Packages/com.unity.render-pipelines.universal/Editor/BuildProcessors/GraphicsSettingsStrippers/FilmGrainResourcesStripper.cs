using System.Collections.Generic;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace UnityEditor.Rendering.Universal
{
    class FilmGrainResourcesStripper : IRenderPipelineGraphicsSettingsStripper<UniversalRenderPipelineFilmGrainResources>
    {
        public bool active => URPBuildData.instance.buildingPlayerForUniversalRenderPipeline;

        public bool CanRemoveSettings(UniversalRenderPipelineFilmGrainResources resources)
        {
            if (!GraphicsSettings.TryGetRenderPipelineSettings<URPShaderStrippingSetting>(out var urpShaderStrippingSettings)
                || !urpShaderStrippingSettings.stripUnusedPostProcessingVariants)
                return false;

            if ((ShaderBuildPreprocessor.volumeFeatures & VolumeFeatures.FilmGrain) == 0)
                return true;

            return PostProcessDisabledInAllRenderers(URPBuildData.instance.rendererDataList);
        }

        internal static bool PostProcessDisabledInAllRenderers(List<ScriptableRendererData> rendererDataList)
        {
            foreach (var rendererData in rendererDataList)
            {
                if (rendererData is UniversalRendererData rd && rd.postProcessData != null ||
                    rendererData is Renderer2DData r2d && r2d.postProcessData != null)
                    return false;
            }
            return true;
        }
    }
}
