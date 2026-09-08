using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Basis.BasisUI;

namespace BattlePhaze.SettingsManager.Integrations
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
                    ApplyQualitySettings(BasisQualityTier.VeryLow, AnisotropicFiltering.Disable, 256, false, false);
                    Data.renderPostProcessing = false;
                    if (ChangeTerrainSettings) ChangeQualityOfTerrain(VeryLowTerrain);
                    break;

                case "low":
                    ApplyQualitySettings(BasisQualityTier.Low, AnisotropicFiltering.Enable, 512, true, true);
                    Data.renderPostProcessing = true;
                    if (ChangeTerrainSettings) ChangeQualityOfTerrain(LowTerrain);
                    break;

                case "medium":
                    ApplyQualitySettings(BasisQualityTier.Medium, AnisotropicFiltering.Enable, 1024, true, true);
                    Data.renderPostProcessing = true;
                    if (ChangeTerrainSettings) ChangeQualityOfTerrain(MediumTerrain);
                    break;

                case "high":
                    ApplyQualitySettings(BasisQualityTier.High, AnisotropicFiltering.Enable, 2048, true, true);
                    Data.renderPostProcessing = true;
                    if (ChangeTerrainSettings) ChangeQualityOfTerrain(HighTerrain);
                    break;

                case "ultra":
                    ApplyQualitySettings(BasisQualityTier.Ultra, AnisotropicFiltering.Enable, 4096, true, true);
                    Data.renderPostProcessing = true;
                    if (ChangeTerrainSettings) ChangeQualityOfTerrain(UltraTerrain);
                    break;
            }

            // Shadows and HDR own their own dropdowns and stay the only writer for those
            // fields; re-running them here refreshes the clamp they apply against the tier
            // that was just picked. See BasisQualityTier.
            SMModuleShadowQualityURP.Apply(BasisSettingsDefaults.ShadowQuality.RawValue);
            SMModuleHDRURP.Apply(BasisSettingsDefaults.HDRSupport.RawValue);

            // Very Low and Low drop the local head's shadow-only clone; Medium and above put it back.
            Basis.Scripts.Drivers.BasisAvatarDriver.ApplyLocalShadowCloneTier();
        }

        public override void ChangedSettings() { }

        // Per-tier values, cheapest first, indexed by BasisQualityTier.
        private static readonly float[] LodBiasByTier = { 0.5f, 0.7f, 1f, 1.5f, 2f };
        private static readonly SkinWeights[] SkinWeightsByTier =
        {
            SkinWeights.TwoBones, SkinWeights.FourBones, SkinWeights.FourBones,
            SkinWeights.Unlimited, SkinWeights.Unlimited,
        };
        private static readonly int[] TextureMipmapLimitByTier = { 1, 0, 0, 0, 0 };
        private static readonly int[] ColorGradingLutSizeByTier = { 16, 16, 32, 32, 32 };
        private static readonly SoftShadowQuality[] SoftShadowQualityByTier =
        {
            SoftShadowQuality.Low, SoftShadowQuality.Low, SoftShadowQuality.Medium,
            SoftShadowQuality.High, SoftShadowQuality.High,
        };
        private static readonly LightRenderingMode[] AdditionalLightsByTier =
        {
            LightRenderingMode.Disabled, LightRenderingMode.PerVertex, LightRenderingMode.PerPixel,
            LightRenderingMode.PerPixel, LightRenderingMode.PerPixel,
        };
        private static readonly DepthPrimingMode[] DepthPrimingByTier =
        {
            DepthPrimingMode.Disabled, DepthPrimingMode.Disabled, DepthPrimingMode.Auto,
            DepthPrimingMode.Forced, DepthPrimingMode.Forced,
        };

        /// <summary>
        /// Authored values, captured from the pipeline asset the first time a quality level is
        /// applied. Every tier resolves to the cheaper of (tier value, ceiling), so no level can
        /// make anything more expensive than the asset shipped with. That matters because the
        /// per-platform assets differ — the Android asset ships with the opaque/depth copies and
        /// HDR already off, and raising them at runtime would sample shader variants that were
        /// stripped at build time.
        /// </summary>
        private static bool _ceilingsCaptured;
        private static bool _ceilDepthTexture;
        private static bool _ceilOpaqueTexture;
        private static bool _ceilSoftShadows;
        private static bool _ceilReflectionProbeBlending;
        private static bool _ceilReflectionProbeBoxProjection;
        private static bool _ceilLodCrossFade;
        private static ColorGradingMode _ceilColorGradingMode;
        private static int _ceilColorGradingLutSize;
        private static SoftShadowQuality _ceilSoftShadowQuality;
        private static LightRenderingMode _ceilAdditionalLights;
        private static DepthPrimingMode _ceilDepthPriming;

        private static void CaptureCeilings(UniversalRenderPipelineAsset asset, UniversalRendererData renderer)
        {
            if (_ceilingsCaptured)
                return;
            _ceilingsCaptured = true;

            BasisUrpQualityFields.EnsureResolved();

            _ceilDepthTexture = asset.supportsCameraDepthTexture;
            _ceilOpaqueTexture = asset.supportsCameraOpaqueTexture;
            _ceilSoftShadows = asset.supportsSoftShadows;
            _ceilReflectionProbeBlending = asset.reflectionProbeBlending;
            _ceilReflectionProbeBoxProjection = asset.reflectionProbeBoxProjection;
            _ceilLodCrossFade = asset.enableLODCrossFade;
            _ceilColorGradingMode = asset.colorGradingMode;
            _ceilColorGradingLutSize = asset.colorGradingLutSize;
            // softShadowQuality is the one knob whose getter is internal too, so it is the only
            // read that goes through reflection. UsePipelineSettings is a per-light sentinel and
            // shouldn't appear on the asset itself; treat it as the highest tier so it never
            // becomes an accidental floor.
            SoftShadowQuality authoredSoftShadow =
                BasisUrpQualityFields.SoftShadowQualityLevel.Get(asset, SoftShadowQuality.High);
            _ceilSoftShadowQuality = authoredSoftShadow == SoftShadowQuality.UsePipelineSettings
                ? SoftShadowQuality.High
                : authoredSoftShadow;
            _ceilAdditionalLights = asset.additionalLightsRenderingMode;
            _ceilDepthPriming = renderer != null ? renderer.depthPrimingMode : DepthPrimingMode.Disabled;
        }

#if UNITY_EDITOR
        /// <summary>
        /// Writing to the pipeline asset at runtime mutates the ScriptableObject itself, and in
        /// the Editor that survives leaving play mode — the .asset is left holding whatever tier
        /// the last session ended on. That is merely untidy for the modules that write absolute
        /// values, but it would be a one-way ratchet here: the next session would capture the
        /// lowered values as its ceiling and could never climb back. Restoring on exit keeps the
        /// authored asset authoritative and stops play mode dirtying it.
        /// </summary>
        [UnityEditor.InitializeOnLoadMethod]
        private static void HookEditorPlayModeRestore()
        {
            UnityEditor.EditorApplication.playModeStateChanged -= OnEditorPlayModeStateChanged;
            UnityEditor.EditorApplication.playModeStateChanged += OnEditorPlayModeStateChanged;
        }

        private static void OnEditorPlayModeStateChanged(UnityEditor.PlayModeStateChange change)
        {
            if (change != UnityEditor.PlayModeStateChange.ExitingPlayMode || !_ceilingsCaptured)
                return;

            UniversalRenderPipelineAsset asset = QualitySettings.renderPipeline as UniversalRenderPipelineAsset;
            if (asset != null)
            {
                asset.supportsCameraDepthTexture = _ceilDepthTexture;
                asset.supportsCameraOpaqueTexture = _ceilOpaqueTexture;
                asset.colorGradingMode = _ceilColorGradingMode;
                asset.colorGradingLutSize = _ceilColorGradingLutSize;

                BasisUrpQualityFields.SupportsSoftShadows.Set(asset, _ceilSoftShadows);
                BasisUrpQualityFields.ReflectionProbeBlending.Set(asset, _ceilReflectionProbeBlending);
                BasisUrpQualityFields.ReflectionProbeBoxProjection.Set(asset, _ceilReflectionProbeBoxProjection);
                BasisUrpQualityFields.EnableLODCrossFade.Set(asset, _ceilLodCrossFade);
                BasisUrpQualityFields.SoftShadowQualityLevel.Set(asset, _ceilSoftShadowQuality);
                BasisUrpQualityFields.AdditionalLightsRenderingMode.Set(asset, _ceilAdditionalLights);

                UniversalRendererData renderer = ResolveRendererData(asset);
                if (renderer != null)
                {
                    renderer.depthPrimingMode = _ceilDepthPriming;
                }
            }

            _ceilingsCaptured = false;
        }
#endif

        /// <summary>
        /// Cost rank for <see cref="LightRenderingMode"/>. The enum is not ordered by cost —
        /// it is Disabled = 0, PerPixel = 1, PerVertex = 2 — so comparing the raw values would
        /// pick per-pixel as the cheaper of per-pixel and per-vertex.
        /// </summary>
        private static int LightCost(LightRenderingMode mode)
        {
            switch (mode)
            {
                case LightRenderingMode.Disabled: return 0;
                case LightRenderingMode.PerVertex: return 1;
                default: return 2;
            }
        }

        private static LightRenderingMode CheaperLight(LightRenderingMode a, LightRenderingMode b)
            => LightCost(a) <= LightCost(b) ? a : b;

        /// <summary>
        /// First renderer on the pipeline asset. Basis ships one renderer per platform asset,
        /// so index 0 is the one the quality level should be tiering.
        /// </summary>
        private static UniversalRendererData ResolveRendererData(UniversalRenderPipelineAsset asset)
        {
            System.ReadOnlySpan<ScriptableRendererData> list = asset.rendererDataList;
            for (int i = 0; i < list.Length; i++)
            {
                if (list[i] is UniversalRendererData universal)
                {
                    return universal;
                }
            }
            return null;
        }

        private void ApplyQualitySettings(
            int tier,
            AnisotropicFiltering anisotropicFilter,
            int particleBudget,
            bool renderShadows,
            bool stopNaN)
        {
            QualitySettings.anisotropicFiltering = anisotropicFilter;
            QualitySettings.particleRaycastBudget = particleBudget;

            // Geometry / skinning cost. lodBias shipped at 2 on every platform tier, which
            // doubles each LOD switch distance — mobile was holding LOD0 twice as far out as
            // the authored distance.
            QualitySettings.lodBias = LodBiasByTier[tier];
            QualitySettings.skinWeights = SkinWeightsByTier[tier];
            QualitySettings.globalTextureMipmapLimit = TextureMipmapLimitByTier[tier];
            QualitySettings.softParticles = tier >= BasisQualityTier.Medium;
            QualitySettings.realtimeReflectionProbes = tier >= BasisQualityTier.Medium;
            QualitySettings.softVegetation = tier >= BasisQualityTier.Low;

            if (Data != null)
            {
                Data.renderShadows = renderShadows;
                Data.stopNaN = stopNaN;
            }

            UniversalRenderPipelineAsset asset = QualitySettings.renderPipeline as UniversalRenderPipelineAsset;
            if (asset == null)
            {
                BasisDebug.LogWarning("SMModuleQualityAndQualitySetURP: no URP asset; skipping pipeline tiering.",
                    BasisDebug.LogTag.System);
                return;
            }

            UniversalRendererData renderer = ResolveRendererData(asset);
            CaptureCeilings(asset, renderer);

            // Screen-space buffers. Both are full-frame copies, and in stereo they cost twice —
            // the first thing to drop once the tier stops needing them.
            asset.supportsCameraDepthTexture = _ceilDepthTexture && tier >= BasisQualityTier.Medium;
            asset.supportsCameraOpaqueTexture = _ceilOpaqueTexture && tier >= BasisQualityTier.Medium;

            asset.colorGradingMode = (ColorGradingMode)Mathf.Min(
                (int)_ceilColorGradingMode,
                tier >= BasisQualityTier.Medium ? (int)ColorGradingMode.HighDynamicRange : (int)ColorGradingMode.LowDynamicRange);
            asset.colorGradingLutSize = Mathf.Min(_ceilColorGradingLutSize, ColorGradingLutSizeByTier[tier]);

            // These six have internal setters in URP, so they go through cached reflection
            // rather than a fork edit. See BasisUrpQualityFields.
            BasisUrpQualityFields.SupportsSoftShadows.Set(asset, _ceilSoftShadows && tier >= BasisQualityTier.Low);
            BasisUrpQualityFields.SoftShadowQualityLevel.Set(asset, (SoftShadowQuality)Mathf.Min(
                (int)_ceilSoftShadowQuality, (int)SoftShadowQualityByTier[tier]));

            BasisUrpQualityFields.AdditionalLightsRenderingMode.Set(asset,
                CheaperLight(_ceilAdditionalLights, AdditionalLightsByTier[tier]));

            BasisUrpQualityFields.ReflectionProbeBlending.Set(asset,
                _ceilReflectionProbeBlending && tier >= BasisQualityTier.Medium);
            BasisUrpQualityFields.ReflectionProbeBoxProjection.Set(asset,
                _ceilReflectionProbeBoxProjection && tier >= BasisQualityTier.Medium);

            // Cross-fade dithers with clip/discard, which defeats early-Z — and this project
            // forces depth priming, so the prepass pays for it a second time.
            BasisUrpQualityFields.EnableLODCrossFade.Set(asset,
                _ceilLodCrossFade && tier >= BasisQualityTier.Medium);

            if (renderer != null)
            {
                DepthPrimingMode priming = (DepthPrimingMode)Mathf.Min(
                    (int)_ceilDepthPriming, (int)DepthPrimingByTier[tier]);
                if (renderer.depthPrimingMode != priming)
                {
                    renderer.depthPrimingMode = priming;
                }
            }

            BasisDebug.Log($"Applied quality tier {tier}: lodBias {QualitySettings.lodBias}, " +
                $"depth {asset.supportsCameraDepthTexture}, opaque {asset.supportsCameraOpaqueTexture}, " +
                $"addLights {asset.additionalLightsRenderingMode}", BasisDebug.LogTag.System);
        }

        private void ChangeQualityOfTerrain(TerrainQualityProfile profile)
        {
            // Includes inactive terrains too, which is usually what you want in settings menus.
            Terrain[] terrains = FindObjectsByType<Terrain>(
                FindObjectsInactive.Include);

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
