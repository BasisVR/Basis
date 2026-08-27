using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Basis.Rendering.RTAO
{
    [DisallowMultipleRendererFeature("BasisRTAO")]
    public sealed class BasisRTAOFeature : ScriptableRendererFeature
    {
        [SerializeField] private BasisRTAOResources resources;
        [SerializeField] private BasisRTAOQuality quality = BasisRTAOQuality.Medium;
        [SerializeField] private bool overrideQualityPreset;
        [SerializeField] private BasisRTAOSettings settings = BasisRTAOSettings.Default;
        [SerializeField] private BasisRTAOSceneSettings sceneSettings = BasisRTAOSceneSettings.Default;
        [SerializeField] private BasisRTAOTracingMode tracingMode = BasisRTAOTracingMode.Auto;
        [SerializeField] private BasisRTAOApplyMode applyMode = BasisRTAOApplyMode.Lighting;
        [SerializeField] private BasisRTAODebugStage debugStage = BasisRTAODebugStage.Final;
        [SerializeField] private bool debugView;

        private static readonly List<BasisRTAOFeature> Live = new List<BasisRTAOFeature>();

        private BasisRTAOPass pass;
        private BasisRTAODebugPass debugPass;
        private BasisRTAOAfterOpaquePass afterOpaquePass;
        private bool loggedFailure;

        public static Func<Camera, bool> CameraFilter;
        // Mirrors and the handheld camera are separate Game cameras, so each one runs its own trace and
        // keeps its own history. The acceleration structure is shared and built once a frame.
        public static bool AllowSecondaryCameras = true;
        // The skinned bake budget spends itself on whoever is nearest the player, not nearest a mirror.
        public static Func<Vector3> ViewerPosition;
        public static bool RuntimeEnabled = true;
        public static bool HasQualityOverride;
        public static BasisRTAOQuality QualityOverride = BasisRTAOQuality.Medium;
        public static bool HasIntensityOverride;
        public static float IntensityOverride = 1f;
        public static bool HasRadiusOverride;
        public static float RadiusOverride = 0.1f;
        public static bool HasDirectStrengthOverride;
        public static float DirectStrengthOverride = 0.5f;
        public static bool HasSpecularOcclusionOverride;
        public static float SpecularOcclusionReliefOverride;
        public static bool HasDenoisePassesOverride;
        public static int DenoisePassesOverride = 2;
        public static bool HasTracingModeOverride;
        public static BasisRTAOTracingMode TracingModeOverride = BasisRTAOTracingMode.Auto;
        public static bool HasLayerMaskOverride;
        public static LayerMask LayerMaskOverride = BasisRTAOSceneSettings.AvatarLayerMask;
        public static bool HasSkinnedModeOverride;
        public static BasisRTAOSkinnedMode SkinnedModeOverride = BasisRTAOSkinnedMode.Off;
        public static bool HasSkinnedBudgetOverride;
        public static int SkinnedBudgetOverride = 4;
        public static bool HasDebugViewOverride;
        public static bool HasDebugStageOverride;
        public static BasisRTAODebugStage DebugStageOverride = BasisRTAODebugStage.Final;
        public static bool DebugViewOverride;
        public static bool HasApplyModeOverride;
        public static BasisRTAOApplyMode ApplyModeOverride = BasisRTAOApplyMode.Lighting;

        public BasisRTAOResources Resources => resources;
        public BasisRTAOQuality Quality => quality;
        public bool OverrideQualityPreset => overrideQualityPreset;
        public BasisRTAOSettings Settings => settings;
        public BasisRTAOSceneSettings SceneSettings => sceneSettings;
        public bool DebugViewActive => HasDebugViewOverride ? DebugViewOverride : debugView;
        public BasisRTAOTracingMode TracingMode => HasTracingModeOverride ? TracingModeOverride : tracingMode;
        public BasisRTAOApplyMode ApplyMode => HasApplyModeOverride ? ApplyModeOverride : applyMode;
        public BasisRTAODebugStage DebugStage => HasDebugStageOverride ? DebugStageOverride : debugStage;
        public BasisRTAOBackend ResolvedBackend => BasisRTAOTracing.Resolve(TracingMode);
        public bool DebugView => debugView;
        internal BasisRTAOPass Pass => pass;

        // Avatars load, switch and leave far more often than the rescan interval, and an avatar that is not
        // in the acceleration structure casts no contact shadow at all. The framework calls this on every
        // avatar lifecycle event so the next frame picks the change up.
        public static void MarkSceneDirty()
        {
            for (int i = 0; i < Live.Count; i++)
                Live[i]?.Pass?.Scene?.MarkDirty();
        }

        public static bool AcceptsCamera(Camera camera)
        {
            Func<Camera, bool> filter = CameraFilter;
            return filter == null || filter(camera);
        }

        public static bool IsSupported => BasisRTAOContext.HardwareSupported;
        public static bool IsRayTracingSupported => BasisRTAOContext.HardwareSupported;

        public BasisRTAOQuality EffectiveQuality => HasQualityOverride ? QualityOverride : quality;

        public BasisRTAOSettings ResolveSettings()
        {
            // Without this the authored intensity, radius and direct strength were silently discarded and
            // replaced wholesale by the preset, so dragging them on the feature did nothing at all.
            BasisRTAOSettings resolved = overrideQualityPreset
                ? settings
                : settings.WithCostFrom(BasisRTAOSettings.FromQuality(EffectiveQuality));

            if (HasIntensityOverride)
                resolved.intensity = IntensityOverride;
            if (HasRadiusOverride)
                resolved.radius = RadiusOverride;
            if (HasDirectStrengthOverride)
                resolved.directLightingStrength = DirectStrengthOverride;
            if (HasSpecularOcclusionOverride)
                resolved.specularOcclusionRelief = SpecularOcclusionReliefOverride;
            if (HasDenoisePassesOverride)
                resolved.denoisePasses = DenoisePassesOverride;

            return resolved.Validated();
        }

        public BasisRTAOSceneSettings ResolveSceneSettings()
        {
            BasisRTAOSceneSettings resolved = sceneSettings;

            // The bake budget is what the occlusion quality actually buys on avatars, so unless the renderer
            // is authored by hand it rides the quality level rather than sitting on a number nobody sees.
            if (!overrideQualityPreset)
            {
                resolved.skinnedBakesPerFrame = BasisRTAOSceneSettings.BakeBudgetForQuality(EffectiveQuality);
                resolved.skinnedBakeInterval = BasisRTAOSceneSettings.BakeIntervalForQuality(EffectiveQuality);
            }

            if (HasLayerMaskOverride)
                resolved.layerMask = LayerMaskOverride;
            if (HasSkinnedModeOverride)
                resolved.skinnedMode = SkinnedModeOverride;
            if (HasSkinnedBudgetOverride)
                resolved.skinnedBakesPerFrame = SkinnedBudgetOverride;

            return resolved.Validated();
        }

        public override void Create()
        {
            if (!Live.Contains(this))
                Live.Add(this);
            pass ??= new BasisRTAOPass();
            debugPass ??= new BasisRTAODebugPass();
            afterOpaquePass ??= new BasisRTAOAfterOpaquePass();
            loggedFailure = false;
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (pass == null || resources == null)
                return;
            if (!RuntimeEnabled)
                return;
            if (renderingData.cameraData.cameraType != CameraType.Game)
                return;
            if (!AcceptsCamera(renderingData.cameraData.camera))
                return;
            BasisRTAOBackend resolved = ResolvedBackend;
            if (resolved == BasisRTAOBackend.None)
                return;

            pass.Setup(resources, ResolveSettings(), ResolveSceneSettings(), TracingMode, ApplyMode, DebugViewActive);
            if (!pass.EnsureReady())
            {
                if (!loggedFailure)
                {
                    loggedFailure = true;
                    Debug.LogWarning($"[BasisRTAO] disabled: {pass.Failure}");
                }
                return;
            }

            loggedFailure = false;
            // Depth only. Asking for Normal would be better data - it is the surface's own normal rather
            // than one inferred from neighbouring depths - but it makes URP run DrawDepthNormalPrepass, and
            // with depth priming on that prepass renders into the MSAA depth attachment while URP forces the
            // normals texture to no MSAA ("Never use MSAA for the normal texture!"). Render graph refuses to
            // build the pass. Both renderers here have depth priming Forced, so the normal reconstruction has
            // to be good enough on its own.
            pass.ConfigureInput(ScriptableRenderPassInput.Depth);
            renderer.EnqueuePass(pass);

            if (ApplyMode == BasisRTAOApplyMode.AfterOpaque && afterOpaquePass != null)
            {
                afterOpaquePass.Setup(pass.CompositeMaterial);
                renderer.EnqueuePass(afterOpaquePass);
            }

            if (DebugViewActive && debugPass != null)
            {
                debugPass.Setup(pass.CompositeMaterial, DebugStage);
                renderer.EnqueuePass(debugPass);
            }
        }

        protected override void Dispose(bool disposing)
        {
            Live.Remove(this);
            pass?.Dispose();
            pass = null;
            debugPass = null;
            afterOpaquePass = null;
        }
    }
}
