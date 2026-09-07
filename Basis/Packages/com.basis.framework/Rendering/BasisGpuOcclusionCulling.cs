using Basis.BasisUI;
using Basis.Scripts.Common;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Basis.Scripts.Rendering
{
    /// <summary>
    /// Startup switch for URP's GPU occlusion culling — the GPU Resident Drawer's depth-pyramid
    /// occlusion test on game cameras.
    ///
    /// The drawer snapshots its settings when it is constructed and the per-frame path reads that
    /// snapshot rather than the pipeline asset, so writing the asset field changes nothing on its
    /// own; the drawer has to be rebuilt. That rebuild re-registers every MeshRenderer and LODGroup
    /// in the loaded scenes, which is why this applies once at boot while the scene is still the
    /// loading scene, and a change made later waits for a restart instead of paying that mid-session.
    /// </summary>
    public static class BasisGpuOcclusionCulling
    {
        private static bool _applied;
        private static bool _appliedKnown;

        /// <summary>
        /// Whether the option is worth offering: a build on a desktop GPU whose active pipeline
        /// asset actually runs the GPU Resident Drawer. Android ships the drawer disabled, and URP
        /// refuses GPU occlusion on tile-only renderers and on Qualcomm GPUs anyway, which a
        /// Windows build on Frame hardware is as much as the Android build is.
        /// </summary>
        public static bool IsSupported
        {
            get
            {
                if (Application.platform == RuntimePlatform.Android || BasisGpuDetection.IsMobileGpu)
                {
                    return false;
                }

                UniversalRenderPipelineAsset asset = ResolveAsset();
                return asset != null && asset.gpuResidentDrawerMode != GPUResidentDrawerMode.Disabled;
            }
        }

        /// <summary>
        /// True once the toggle has been moved away from the value this session booted with, i.e.
        /// the setting is saved but only a restart will make it real.
        /// </summary>
        public static bool NeedsRestart =>
            _appliedKnown && _applied != BasisSettingsDefaults.UseGpuOcclusionCulling.RawValue;

        /// <summary>
        /// Pushes the saved setting onto the active pipeline asset and rebuilds the drawer so it
        /// holds for this session. Called once from device management startup, after
        /// <c>BasisSettingsDefaults.LoadAll</c>.
        /// </summary>
        public static void ApplyStartupSetting()
        {
            UniversalRenderPipelineAsset asset = ResolveAsset();
            if (asset == null || asset.gpuResidentDrawerMode == GPUResidentDrawerMode.Disabled)
            {
                return;
            }

            bool enabled = BasisSettingsDefaults.UseGpuOcclusionCulling.RawValue;
            _applied = enabled;
            _appliedKnown = true;

            if (asset.gpuResidentDrawerEnableOcclusionCullingInCameras == enabled)
            {
                return;
            }

            asset.gpuResidentDrawerEnableOcclusionCullingInCameras = enabled;

            // Nothing polls the asset in a player build — URP's per-frame ReinitializeIfNeeded is
            // editor-only — so the rebuild has to be asked for directly.
            IGPUResidentRenderPipeline.ReinitializeGPUResidentDrawer();

            BasisDebug.Log($"GPU occlusion culling {(enabled ? "enabled" : "disabled")} for this session.",
                BasisDebug.LogTag.System);
        }

        /// <summary>
        /// The drawer reads its settings from <see cref="GraphicsSettings.currentRenderPipeline"/>,
        /// so that is the asset instance the flag has to land on — not the one behind
        /// <c>QualitySettings.renderPipeline</c>, which the other URP settings modules use.
        /// </summary>
        private static UniversalRenderPipelineAsset ResolveAsset()
        {
            return GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
        }
    }
}
