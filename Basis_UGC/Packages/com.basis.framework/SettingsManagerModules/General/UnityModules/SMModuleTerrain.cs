using BattlePhaze.SettingsManager;
using UnityEngine;
namespace BattlePhaze.SettingsManager.Intergrations
{
    public class SMModuleTerrain : SettingsManagerOption
    {
        public Terrain terrain;
        public override void ReceiveOption(SettingsMenuInput Option, SettingsManager Manager = null)
        {
            if (NameReturn(0, Option))
            {
                if (terrain == null)
                {
                    terrain = FindFirstObjectByType<Terrain>();
                    if (terrain == null)
                    {
                        return;
                    }
                }
                switch (Option.SelectedValue)
                {
                    case "very low":
                        terrain.detailObjectDistance = 30;
                        terrain.detailObjectDensity = 0.15f;
                        terrain.heightmapPixelError = 200;
                        terrain.basemapDistance = 50;
                        terrain.treeDistance = 200;
                        terrain.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
                        terrain.drawTreesAndFoliage = true;
                        break;
                    case "low":
                        terrain.detailObjectDistance = 40;
                        terrain.heightmapPixelError = 150;
                        terrain.detailObjectDensity = 0.28f;
                        terrain.basemapDistance = 60;
                        terrain.treeDistance = 300;
                        terrain.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.BlendProbes;
                        terrain.drawTreesAndFoliage = true;
                        break;
                    case "medium":
                        terrain.detailObjectDistance = 50;
                        terrain.heightmapPixelError = 100;
                        terrain.detailObjectDensity = 0.30f;
                        terrain.basemapDistance = 70;
                        terrain.treeDistance = 400;
                        terrain.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.BlendProbesAndSkybox;
                        terrain.drawTreesAndFoliage = true;
                        break;
                    case "high":
                        terrain.detailObjectDistance = 60;
                        terrain.heightmapPixelError = 50;
                        terrain.detailObjectDensity = 0.33f;
                        terrain.basemapDistance = 80;
                        terrain.treeDistance = 500;
                        terrain.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.BlendProbesAndSkybox;
                        terrain.drawTreesAndFoliage = true;
                        break;
                    case "ultra":
                        terrain.detailObjectDistance = 70;
                        terrain.heightmapPixelError = 20;
                        terrain.detailObjectDensity = 0.33f;
                        terrain.basemapDistance = 90;
                        terrain.treeDistance = 600;
                        terrain.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.BlendProbesAndSkybox;
                        terrain.drawTreesAndFoliage = true;
                        break;
                }
            }
        }
    }
}
