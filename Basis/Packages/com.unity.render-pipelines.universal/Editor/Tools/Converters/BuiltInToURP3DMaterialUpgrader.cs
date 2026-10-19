using System;
using System.Collections.Generic;
using UnityEditor.Rendering.Converter;
using UnityEngine.Categorization;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace UnityEditor.Rendering.Universal
{
    [Serializable]
    [PipelineConverter("Built-in", "Universal Render Pipeline (Universal Renderer)")]
    [BatchModeConverterClassInfo("BuiltInToURP", "Material")]
    [ElementInfo(Name = "Material Shader Converter",
                 Order = 100,
                 Description = "This converter scans all materials that reference Built-in shaders and upgrades them to use Universal Render Pipeline (URP) shaders.")]
    internal sealed class BuiltInToURP3DMaterialUpgrader : RenderPipelineConverterMaterialUpgrader
    {

        public override bool isEnabled
        {
            get
            {
                if (GraphicsSettings.currentRenderPipeline is not UniversalRenderPipelineAsset urpAsset)
                    return false;

                return urpAsset.scriptableRenderer is UniversalRenderer;
            }
        }

        public override string isDisabledMessage => "Converter requires URP with a Universal Renderer. Convert your project to URP to use this converter.";

        internal static List<MaterialUpgrader> FetchMaterialUpgraders()
        {
            var allURPUpgraders = MaterialUpgrader.FetchAllUpgradersForPipeline(typeof(UniversalRenderPipelineAsset));

            var builtInToURPUpgraders = new List<MaterialUpgrader>();
            foreach (var upgrader in allURPUpgraders)
            {
                if (upgrader is IBuiltInToURPMaterialUpgrader)
                    builtInToURPUpgraders.Add(upgrader);
            }

            return builtInToURPUpgraders;
        }

        protected override List<MaterialUpgrader> upgraders => FetchMaterialUpgraders();    
    }
}
