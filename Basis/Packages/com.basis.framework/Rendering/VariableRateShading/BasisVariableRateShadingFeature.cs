using Unity.Scripting.LifecycleManagement;
using Basis.BasisUI;
using Basis.Scripts.Drivers;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Basis.Scripts.Rendering
{
    /// <summary>
    /// Gaze-driven Variable Rate Shading for desktop (DirectX 12, or DirectX 11 on NVIDIA through
    /// the BasisVrsD3D11 plugin). Builds a per-frame shading rate image that keeps each eye's
    /// foveal region sharp and coarsens the periphery, then binds it onto every camera-resolution
    /// URP pass that draws with depth. Desktop only; Quest keeps its native OpenXR foveation.
    /// </summary>
    [AutoStaticsCleanup]
    [DisallowMultipleRendererFeature("BasisVariableRateShading")]
    public partial class BasisVariableRateShadingFeature : ScriptableRendererFeature
    {
        [SerializeField] private ComputeShader buildShader;
        [SerializeField] private float gazeProjectDistance = 3f;
        [SerializeField] private bool yFlip = true;
        [SerializeField] private bool debugVisualize;

        private BasisVariableRateShadingPass _pass;
        private BasisVrsAnalyzePass _analyzePass;
        private BasisGazeEffectsResetPass _gazeResetPass;
        private BasisVariableRateShadingDebugPass _debugPass;

        public static bool IsSupported => SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D12 || UsesD3D11Plugin;
        public static bool UsesD3D11Plugin => SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D11 && BasisVrsD3D11.Supported;

        // Liveness/status for diagnostics (Basis eye pipeline window).
        public static int LastDispatchFrame;
        public static Vector2Int LastTiles;
        public static float LastGazeWeight;
        public static float GpuMilliseconds => BasisVariableRateShadingPass.GpuMs;

        public override void Create()
        {
            _pass?.Dispose();
            _pass = new BasisVariableRateShadingPass(buildShader)
            {
                renderPassEvent = RenderPassEvent.BeforeRendering,
            };
            _pass.Configure(gazeProjectDistance, yFlip);
            _analyzePass = new BasisVrsAnalyzePass(_pass)
            {
                renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing,
            };

            _gazeResetPass = new BasisGazeEffectsResetPass
            {
                renderPassEvent = RenderPassEvent.AfterRendering + 50,
            };

            _debugPass = new BasisVariableRateShadingDebugPass
            {
                renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing,
            };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (_pass == null || buildShader == null)
                return;

            if (!IsSupported)
                return;

            // Only the local player's view is foveated — mirrors and the handheld/360
            // capture are Game cameras too, so gate on camera identity, not cameraType.
            if (!ReferenceEquals(renderingData.cameraData.camera, BasisLocalCameraDriver.CameraInstance))
                return;

            if (!BasisSettingsDefaults.DevVariableRateShading.RawValue
                && !BasisSettingsDefaults.DevVariableRateShadingDesktop.RawValue)
                return;

            _pass.Configure(gazeProjectDistance, yFlip);
            renderer.EnqueuePass(_pass);
            if (BasisSettingsDefaults.VrsContentAdaptive.RawValue && _analyzePass != null)
                renderer.EnqueuePass(_analyzePass);
            if (_gazeResetPass != null)
                renderer.EnqueuePass(_gazeResetPass);

            if (debugVisualize && _debugPass != null && !UsesD3D11Plugin)
                renderer.EnqueuePass(_debugPass);
        }

        protected override void Dispose(bool disposing)
        {
            _pass?.Dispose();
            _pass = null;
            _analyzePass = null;
            _gazeResetPass = null;
            _debugPass = null;
        }
    }
}
