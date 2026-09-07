using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.UnifiedRayTracing;
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
        [SerializeField] private BasisRTAOTracingMode tracingMode = BasisRTAOTracingMode.ScreenSpace;
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
        // The tracing internals. Constants in everything but name until an artifact needed explaining -
        // a ray that starts inside the geometry it was cast from shadows itself, and the offsets below are
        // what decides whether it does. See the advanced section of the occlusion settings.
        public static bool HasNormalBiasOverride;
        public static float NormalBiasOverride = 0.005f;
        public static bool HasDistanceBiasOverride;
        public static float DistanceBiasOverride = 0.0005f;
        public static bool HasFalloffOverride;
        public static float FalloffOverride = 1f;
        public static bool HasPowerOverride;
        public static float PowerOverride = 1f;
        public static bool HasFadeStartOverride;
        public static float FadeStartOverride = 40f;
        public static bool HasFadeEndOverride;
        public static float FadeEndOverride = 60f;
        public static bool HasTracingModeOverride;
        public static BasisRTAOTracingMode TracingModeOverride = BasisRTAOTracingMode.ScreenSpace;
        public static bool HasLayerMaskOverride;
        public static LayerMask LayerMaskOverride = BasisRTAOSceneSettings.AvatarLayerMask;
        /// <summary>
        /// Hands back a ray tracing structure owned by something else that already holds every category
        /// asked for, or null when there is none. Set by the framework when global illumination is running
        /// its ray traced path: both effects trace the same avatars and the same world, and building that
        /// twice a frame is the single largest thing they duplicate.
        ///
        /// A delegate rather than a reference so this package keeps no knowledge of the other one.
        /// </summary>
        public static Func<byte, IRayTracingAccelStruct> SharedStructureProvider;

        /// <summary>
        /// Records a build of the shared structure if it still needs one. Called by whichever pass reaches
        /// it first in the frame, which is this one - the global illumination pass runs after the opaques.
        /// </summary>
        public static Action<CommandBuffer> SharedStructureBuilder;

        public static bool HasSkinnedModeOverride;
        public static BasisRTAOSkinnedMode SkinnedModeOverride = BasisRTAOSkinnedMode.Off;
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
            if (HasNormalBiasOverride)
                resolved.normalBias = NormalBiasOverride;
            if (HasDistanceBiasOverride)
                resolved.distanceBias = DistanceBiasOverride;
            if (HasFalloffOverride)
                resolved.distanceFalloff = FalloffOverride;
            if (HasPowerOverride)
                resolved.power = PowerOverride;
            if (HasFadeStartOverride)
                resolved.fadeStart = FadeStartOverride;
            if (HasFadeEndOverride)
                resolved.fadeEnd = FadeEndOverride;

            return resolved.Validated();
        }

        // No quality branch left: avatars cost one transform update per limb whatever the level is set to,
        // so there is no per frame budget for the quality preset to ration any more.
        public BasisRTAOSceneSettings ResolveSceneSettings()
        {
            BasisRTAOSceneSettings resolved = sceneSettings;

            if (HasLayerMaskOverride)
                resolved.layerMask = LayerMaskOverride;
            if (HasSkinnedModeOverride)
                resolved.skinnedMode = SkinnedModeOverride;

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
            {
                // After the screen space degrade in Resolve, None means a device with no compute
                // shaders at all - but whatever it means, it must never be silent: this gate sits
                // before Setup, so ReportBackendOnce never gets the chance to say anything.
                if (!loggedFailure)
                {
                    loggedFailure = true;
                    Debug.LogWarning("[BasisRTAO] disabled: no usable backend on this device (no compute shader support).");
                }
                return;
            }

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
