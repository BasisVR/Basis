using UnityEngine;
using UnityEngine.Rendering;
using UnityEditor.SpeedTree.Importer;
using UnityEngine.Rendering.Universal;

namespace UnityEditor.Rendering.Universal
{
    class UniversalSpeedTree9Upgrader : SpeedTree9MaterialUpgrader, IBuiltInToURPMaterialUpgrader
    {
        internal UniversalSpeedTree9Upgrader(string oldShaderName)
        {
            RenameShader(oldShaderName, "Universal Render Pipeline/Nature/SpeedTree9_URP", UniversalSpeedTree9MaterialFinalizer);
            RenameFloat("_TwoSided", Property.CullMode);
            RenameColor("_ColorTint", "_Color");
            RenameKeywordToFloat("EFFECT_HUE_VARIATION", "_HueVariationKwToggle", 1, 0);
            RenameKeywordToFloat("EFFECT_SUBSURFACE", "_SubsurfaceKwToggle", 1, 0);
            RenameKeywordToFloat("EFFECT_BUMP", "_NormalMapKwToggle", 1, 0);
            RenameKeywordToFloat("EFFECT_BILLBOARD", "_BillboardKwToggle", 1, 0);
            RenameKeywordToFloat("EFFECT_EXTRA_TEX", "EFFECT_EXTRA_TEX", 1, 0);
        }

        const int kMaterialUpgraderVersion = 1;

        [MaterialSettingsCallbackAttribute(kMaterialUpgraderVersion)]
        private static void OnAssetPostProcessDelegate(GameObject mainObject)
        {
            if (IsCurrentPipelineURP())
            {
                SpeedTree9MaterialUpgrader.PostprocessSpeedTree9Materials(mainObject, UniversalSpeedTree9MaterialFinalizer);
            }
        }

        static private bool IsCurrentPipelineURP()
        {
            return GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset;
        }

        static public void UniversalSpeedTree9MaterialFinalizer(Material mat)
        {
            if (mat.HasFloat("_TwoSided"))
                mat.SetFloat(Property.CullMode, mat.GetFloat("_TwoSided"));

            Unity.Rendering.Universal.ShaderUtils.UpdateMaterial(mat,
                Unity.Rendering.Universal.ShaderUtils.MaterialUpdateType.CreatedNewMaterial,
                Unity.Rendering.Universal.ShaderUtils.ShaderID.SpeedTree9);
        }
    }
}
