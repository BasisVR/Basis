using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Basis.BasisUI;

namespace BattlePhaze.SettingsManager.Intergrations
{
    public class SMModuleQualityAndQualitySetURP : BasisSettingsBase
    {
        public UniversalAdditionalCameraData Data;
        public Camera Camera;

        [Header("Terrain")]
        public bool ChangeTerrainSettings = true;

        // --- Canonical setting key (from defaults) ---
        private static string K_QUALITY_LEVEL => BasisSettingsDefaults.QualityLevel.BindingKey; // "quality level"

        // Profiles you can tune in Inspector
        [System.Serializable]
        public class TerrainQualityProfile
        {
            [Header("LOD / Texturing")]
            [Tooltip("Higher = worse quality but faster. (Terrain.heightmapPixelError)")]
            public float heightmapPixelError = 5f;

            [Tooltip("How far before terrain switches to basemap (Terrain.basemapDistance). Lower is faster.")]
            public float basemapDistance = 512f;

            [Header("Detail Objects (Grass/Mesh details)")]
            public float detailObjectDistance = 80f;
            [Range(0f, 1f)] public float detailObjectDensity = 1f;
            public bool drawDetails = true;

            [Header("Trees")]
            public float treeDistance = 500f;
            public float treeBillboardDistance = 200f;
            public float treeCrossFadeLength = 5f;
            public int treeMaximumFullLODCount = 50;

            [Header("Misc")]
            public bool drawInstanced = true; // GPU instancing where supported
            public ShadowCastingMode shadowCastingMode = ShadowCastingMode.On;
            public bool receiveShadows = true;
        }

        [Header("Terrain Profiles")]
        public TerrainQualityProfile VeryLowTerrain = new TerrainQualityProfile
        {
            heightmapPixelError = 20f,
            basemapDistance = 64f,
            detailObjectDistance = 20f,
            detailObjectDensity = 0.25f,
            drawDetails = true,
            treeDistance = 100f,
            treeBillboardDistance = 60f,
            treeCrossFadeLength = 2f,
            treeMaximumFullLODCount = 5,
            drawInstanced = true,
            shadowCastingMode = ShadowCastingMode.Off,
            receiveShadows = false,
        };

        public TerrainQualityProfile LowTerrain = new TerrainQualityProfile
        {
            heightmapPixelError = 12f,
            basemapDistance = 128f,
            detailObjectDistance = 20f,
            detailObjectDensity = 0.25f,
            drawDetails = true,
            treeDistance = 150f,
            treeBillboardDistance = 60f,
            treeCrossFadeLength = 2f,
            treeMaximumFullLODCount = 10,
            drawInstanced = true,
            shadowCastingMode = ShadowCastingMode.Off,
            receiveShadows = false
        };

        public TerrainQualityProfile MediumTerrain = new TerrainQualityProfile
        {
            heightmapPixelError = 8f,
            basemapDistance = 256f,
            detailObjectDistance = 60f,
            detailObjectDensity = 0.6f,
            drawDetails = true,
            treeDistance = 350f,
            treeBillboardDistance = 120f,
            treeCrossFadeLength = 4f,
            treeMaximumFullLODCount = 30,
            drawInstanced = true,
            shadowCastingMode = ShadowCastingMode.On,
            receiveShadows = true
        };

        public TerrainQualityProfile HighTerrain = new TerrainQualityProfile
        {
            heightmapPixelError = 5f,
            basemapDistance = 512f,
            detailObjectDistance = 100f,
            detailObjectDensity = 0.9f,
            drawDetails = true,
            treeDistance = 700f,
            treeBillboardDistance = 250f,
            treeCrossFadeLength = 6f,
            treeMaximumFullLODCount = 80,
            drawInstanced = true,
            shadowCastingMode = ShadowCastingMode.On,
            receiveShadows = true
        };

        public TerrainQualityProfile UltraTerrain = new TerrainQualityProfile
        {
            heightmapPixelError = 1f,
            basemapDistance = 1024f,
            detailObjectDistance = 160f,
            detailObjectDensity = 1f,
            drawDetails = true,
            treeDistance = 1200f,
            treeBillboardDistance = 400f,
            treeCrossFadeLength = 10f,
            treeMaximumFullLODCount = 200,
            drawInstanced = true,
            shadowCastingMode = ShadowCastingMode.On,
            receiveShadows = true
        };

        public override void ValidSettingsChange(string matchedSettingName, string optionValue)
        {
            if (matchedSettingName != K_QUALITY_LEVEL)
                return;

            if (Camera == null)
            {
                Camera = Camera.main;
                if (Camera != null)
                    Data = Camera.GetComponent<UniversalAdditionalCameraData>();
            }

            if (Data == null)
                return;

            switch (optionValue)
            {
                case "very low":
                    ApplyQualitySettings(AnisotropicFiltering.Disable, 256, false, false);
                    Data.renderPostProcessing = false;
                    if (ChangeTerrainSettings) ChangeQualityOfTerrain(VeryLowTerrain);
                    break;

                case "low":
                    ApplyQualitySettings(AnisotropicFiltering.Enable, 512, true, true);
                    Data.renderPostProcessing = true;
                    if (ChangeTerrainSettings) ChangeQualityOfTerrain(LowTerrain);
                    break;

                case "medium":
                    ApplyQualitySettings(AnisotropicFiltering.Enable, 1024, true, true);
                    Data.renderPostProcessing = true;
                    if (ChangeTerrainSettings) ChangeQualityOfTerrain(MediumTerrain);
                    break;

                case "high":
                    ApplyQualitySettings(AnisotropicFiltering.Enable, 2048, true, true);
                    Data.renderPostProcessing = true;
                    if (ChangeTerrainSettings) ChangeQualityOfTerrain(HighTerrain);
                    break;

                case "ultra":
                    ApplyQualitySettings(AnisotropicFiltering.Enable, 4096, true, true);
                    Data.renderPostProcessing = true;
                    if (ChangeTerrainSettings) ChangeQualityOfTerrain(UltraTerrain);
                    break;
            }
        }

        public override void ChangedSettings() { }

        private void ApplyQualitySettings(
            AnisotropicFiltering anisotropicFilter,
            int particleBudget,
            bool renderShadows,
            bool stopNaN)
        {
            QualitySettings.anisotropicFiltering = anisotropicFilter;
            QualitySettings.particleRaycastBudget = particleBudget;
            BasisDebug.Log("Apply Quality Settings", BasisDebug.LogTag.System);

            if (Data != null)
            {
                Data.renderShadows = renderShadows;
                Data.stopNaN = stopNaN;
            }
        }

        private void ChangeQualityOfTerrain(TerrainQualityProfile profile)
        {
            // Includes inactive terrains too, which is usually what you want in settings menus.
            Terrain[] terrains = FindObjectsByType<Terrain>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);

            for (int i = 0; i < terrains.Length; i++)
            {
                Terrain t = terrains[i];
                if (t == null) continue;

                // Big win knobs
                t.heightmapPixelError = profile.heightmapPixelError;
                t.basemapDistance = profile.basemapDistance;

                // Details (grass / detail meshes)
                t.detailObjectDistance =profile.detailObjectDistance;
                t.detailObjectDensity = profile.detailObjectDensity;
                t.drawTreesAndFoliage = profile.drawDetails;

                // Trees (note: drawTreesAndFoliage also gates tree rendering)
                t.treeDistance = profile.treeDistance;
                t.treeBillboardDistance = profile.treeBillboardDistance;
                t.treeCrossFadeLength = profile.treeCrossFadeLength;
                t.treeMaximumFullLODCount = profile.treeMaximumFullLODCount;

                // Misc / rendering
                t.drawInstanced = profile.drawInstanced;
                t.shadowCastingMode = profile.shadowCastingMode;

                // Force the terrain to refresh LOD decisions now
                t.Flush();
            }

            BasisDebug.Log($"Applied Terrain Profile: {profile.heightmapPixelError}pxErr, baseMap {profile.basemapDistance}",
                BasisDebug.LogTag.System);
        }
    }
}
