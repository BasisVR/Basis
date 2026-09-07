using Basis.BasisUI;
using Basis.Scripts.Drivers;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Basis.Scripts.Rendering
{
    internal class BasisVariableRateShadingPass : ScriptableRenderPass
    {
        private const string PropSri = "_BasisSri";
        private const string PropTile = "_BasisVrsTile";
        private const string PropGazeL = "_BasisVrsGazeL";
        private const string PropGazeR = "_BasisVrsGazeR";
        private const string PropUnprojL = "_BasisVrsUnprojL";
        private const string PropUnprojR = "_BasisVrsUnprojR";
        private const string PropCosL = "_BasisVrsCosL";
        private const string PropCosR = "_BasisVrsCosR";
        private const string PropCenterLR = "_BasisVrsCenterLR";
        private const string PropRates = "_BasisVrsRates";
        private const string PropRatesAniso = "_BasisVrsRatesAniso";

        // Gaze loss handling: hold the sharp region in place briefly (blinks, tracking
        // dropouts), then ease it back to the optical axis instead of snapping, relaxing
        // the tightened radii on the way.
        private const float GazeHoldSeconds = 0.25f;
        private const float GazeFadeSeconds = 0.35f;
        // With live gaze the sharp region follows the fovea, so it can be smaller than the
        // no-tracking fallback the sliders are sized for.
        private const float GazeFovealTighten = 0.7f;
        // Width of the anisotropic 4x2/2x4 band bridging 2x2 -> 4x4, relative to (outer - inner).
        private const float AnisoBandGrowth = 1f;
        private const float AnisoBandMin = 0.04f;

        private static readonly ProfilingSampler samplerVrs = new ProfilingSampler("BasisVariableRateShading");
        public static float GpuMs => samplerVrs.gpuElapsedTime;
        public static void SetProfilingEnabled(bool enabled) => samplerVrs.enableRecording = enabled;

        // Hardware shading-rate caps, resolved once — the graphics API cannot change without a restart.
        private static bool sCapsCached;
        private static bool sSriUsable;
        private static Vector4 sRates;
        private static Vector4 sRatesAniso;

        private readonly ComputeShader _buildShader;
        private readonly int _kernel;

        private float _gazeProjectDistance;
        private bool _yFlip;

        private Vector3 _lastFocalWorld;
        private float _lastGazeTime = float.NegativeInfinity;
        private bool _activeLogged;

        public BasisVariableRateShadingPass(ComputeShader buildShader)
        {
            _buildShader = buildShader;
            if (_buildShader != null)
                _kernel = _buildShader.FindKernel("CSMain");
            profilingSampler = samplerVrs;
        }

        public void Configure(float gazeProjectDistance, bool yFlip)
        {
            _gazeProjectDistance = gazeProjectDistance;
            _yFlip = yFlip;
        }

        private class BuildPassData
        {
            public ComputeShader cs;
            public int kernel;
            public TextureHandle sri;
            public Vector4 tile;
            public Vector4 gazeL;
            public Vector4 gazeR;
            public Vector4 unprojL;
            public Vector4 unprojR;
            public Vector4 cosL;
            public Vector4 cosR;
            public Vector4 centers;
            public Vector2Int tiles;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (_buildShader == null)
                return;

            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            if (cameraData.cameraType != CameraType.Game)
                return;

            if (!ReferenceEquals(cameraData.camera, BasisLocalCameraDriver.CameraInstance))
                return;

            if (!BasisVariableRateShadingFeature.IsSupported)
                return;

            bool enabled = cameraData.xr.enabled
                ? BasisSettingsDefaults.DevVariableRateShading.RawValue
                : BasisSettingsDefaults.DevVariableRateShadingDesktop.RawValue;
            if (!enabled)
                return;

            CacheHardwareCaps();
            if (!sSriUsable)
            {
                BasisDebug.LogWarningOnce("VRS enabled but this GPU/driver reports no per-tile shading rate support (needs image-based VRS with at least 2x2) — gaze foveation stays off.", BasisDebug.LogTag.Device);
                return;
            }

            RenderTextureDescriptor camDesc = cameraData.cameraTargetDescriptor;
            if (camDesc.width <= 0 || camDesc.height <= 0)
                return;

            Vector2Int tiles = ShadingRateImage.GetAllocTileSize(camDesc.width, camDesc.height);
            if (tiles.x <= 0 || tiles.y <= 0)
            {
                BasisDebug.LogWarningOnce("VRS enabled but ShadingRateImage.GetAllocTileSize returned an empty tile grid — the driver refused a shading rate image, gaze foveation stays off.", BasisDebug.LogTag.Device);
                return;
            }

            float now = Time.unscaledTime;
            bool hasGaze = BasisLocalCameraDriver.HasInstance && BasisLocalCameraDriver.HasEyeGaze;
            if (hasGaze)
            {
                _lastFocalWorld = BasisLocalCameraDriver.GazeOrigin + BasisLocalCameraDriver.GazeDirection * _gazeProjectDistance;
                _lastGazeTime = now;
            }
            float sinceGaze = now - _lastGazeTime;
            float gazeWeight = sinceGaze <= GazeHoldSeconds
                ? 1f
                : 1f - Mathf.SmoothStep(0f, 1f, (sinceGaze - GazeHoldSeconds) / GazeFadeSeconds);

            // The graphics quality level tightens the sharp region rather than writing the
            // player's sliders — a smaller foveal radius leaves more of the frame at the coarse
            // shading rate. Clamping here instead of overwriting the setting keeps the slider
            // showing what the player chose, the same way shadows and HDR clamp themselves.
            float fovealScale = FovealScaleForTier(BasisQualityTier.Current) * Mathf.Lerp(1f, GazeFovealTighten, gazeWeight);
            float inner = Mathf.Max(0f, BasisSettingsDefaults.VrsFovealInnerRadius.RawValue) * fovealScale;
            float outer = Mathf.Max(inner, BasisSettingsDefaults.VrsFovealOuterRadius.RawValue * fovealScale);
            float farStart = outer + Mathf.Max(AnisoBandMin * fovealScale, (outer - inner) * AnisoBandGrowth);
            float aspect = (float)camDesc.width / Mathf.Max(1, camDesc.height);

            int rightEye = cameraData.xr.enabled && cameraData.xr.singlePassEnabled ? 1 : 0;
            ComputeEyeParams(cameraData, 0, gazeWeight, inner, outer, farStart,
                out Vector4 unprojL, out Vector3 gazeDirL, out Vector4 cosL, out Vector2 centerL);
            ComputeEyeParams(cameraData, rightEye, gazeWeight, inner, outer, farStart,
                out Vector4 unprojR, out Vector3 gazeDirR, out Vector4 cosR, out Vector2 centerR);

            RenderTextureDescriptor sriDesc = new RenderTextureDescriptor(tiles.x, tiles.y, ShadingRateInfo.graphicsFormat, GraphicsFormat.None, 0)
            {
                msaaSamples = 1,
                useMipMap = false,
                autoGenerateMips = false,
                dimension = TextureDimension.Tex2D,
                enableRandomWrite = true,
                enableShadingRate = true,
            };
            TextureHandle sri = UniversalRenderer.CreateRenderGraphTexture(renderGraph, sriDesc, "_BasisShadingRateImage", clear: false);

            using (var builder = renderGraph.AddComputePass<BuildPassData>("BasisVRS Build", out BuildPassData data, samplerVrs))
            {
                data.cs = _buildShader;
                data.kernel = _kernel;
                data.sri = sri;
                data.tile = new Vector4(tiles.x, tiles.y, _yFlip ? 1f : 0f, 0f);
                data.gazeL = new Vector4(gazeDirL.x, gazeDirL.y, gazeDirL.z, aspect);
                data.gazeR = new Vector4(gazeDirR.x, gazeDirR.y, gazeDirR.z, 0f);
                data.unprojL = unprojL;
                data.unprojR = unprojR;
                data.cosL = cosL;
                data.cosR = cosR;
                data.centers = new Vector4(centerL.x, centerL.y, centerR.x, centerR.y);
                data.tiles = tiles;

                builder.UseTexture(sri, AccessFlags.Write);
                builder.AllowPassCulling(false);

                builder.SetRenderFunc((BuildPassData d, ComputeGraphContext ctx) =>
                {
                    ctx.cmd.SetComputeTextureParam(d.cs, d.kernel, PropSri, d.sri);
                    ctx.cmd.SetComputeVectorParam(d.cs, PropTile, d.tile);
                    ctx.cmd.SetComputeVectorParam(d.cs, PropGazeL, d.gazeL);
                    ctx.cmd.SetComputeVectorParam(d.cs, PropGazeR, d.gazeR);
                    ctx.cmd.SetComputeVectorParam(d.cs, PropUnprojL, d.unprojL);
                    ctx.cmd.SetComputeVectorParam(d.cs, PropUnprojR, d.unprojR);
                    ctx.cmd.SetComputeVectorParam(d.cs, PropCosL, d.cosL);
                    ctx.cmd.SetComputeVectorParam(d.cs, PropCosR, d.cosR);
                    ctx.cmd.SetComputeVectorParam(d.cs, PropCenterLR, d.centers);
                    ctx.cmd.SetComputeVectorParam(d.cs, PropRates, sRates);
                    ctx.cmd.SetComputeVectorParam(d.cs, PropRatesAniso, sRatesAniso);
                    int groupsX = (d.tiles.x + 7) / 8;
                    int groupsY = (d.tiles.y + 7) / 8;
                    ctx.cmd.DispatchCompute(d.cs, d.kernel, groupsX, groupsY, 1);
                });
            }

            UniversalShadingRateData vrsData = frameData.GetOrCreate<UniversalShadingRateData>();
            vrsData.shadingRateImage = sri;
            vrsData.isValid = true;

            BasisVariableRateShadingFeature.LastDispatchFrame = Time.frameCount;
            BasisVariableRateShadingFeature.LastTiles = tiles;
            BasisVariableRateShadingFeature.LastGazeWeight = gazeWeight;
            if (!_activeLogged)
            {
                _activeLogged = true;
                BasisDebug.Log($"Gaze-foveated VRS active: {tiles.x}x{tiles.y} tiles, rates 1x1/{(uint)sRates.y}/{(uint)sRatesAniso.x}|{(uint)sRatesAniso.y}/{(uint)sRates.z} (native codes), gaze {(hasGaze ? "tracked" : "optical-axis fallback")}.", BasisDebug.LogTag.Device);
            }
        }

        private void ComputeEyeParams(UniversalCameraData cameraData, int eye, float gazeWeight,
            float inner, float outer, float farStart,
            out Vector4 unproj, out Vector3 gazeDir, out Vector4 cosBands, out Vector2 center)
        {
            Matrix4x4 view = cameraData.GetViewMatrix(eye);
            Matrix4x4 proj = GL.GetGPUProjectionMatrix(cameraData.GetProjectionMatrix(eye), false);
            unproj = BasisVrsMath.UnprojectParams(proj);
            gazeDir = BasisVrsMath.EyeGazeViewDir(view, _lastFocalWorld, gazeWeight);
            center = BasisVrsMath.ViewDirToUV(proj, gazeDir, new Vector2(0.5f, 0.5f));
            if (_yFlip)
                center.y = 1f - center.y;
            cosBands = new Vector4(
                BasisVrsMath.CosForUvRadius(inner, proj.m11),
                BasisVrsMath.CosForUvRadius(outer, proj.m11),
                BasisVrsMath.CosForUvRadius(farStart, proj.m11),
                0f);
        }

        private static void CacheHardwareCaps()
        {
            if (sCapsCached)
                return;
            sCapsCached = true;

            bool has2x2 = false, hasWide = false, hasTall = false, has4x4 = false;
            foreach (ShadingRateFragmentSize size in ShadingRateInfo.availableFragmentSizes)
            {
                if (size == ShadingRateFragmentSize.FragmentSize2x2) has2x2 = true;
                else if (size == ShadingRateFragmentSize.FragmentSize4x2) hasWide = true;
                else if (size == ShadingRateFragmentSize.FragmentSize2x4) hasTall = true;
                else if (size == ShadingRateFragmentSize.FragmentSize4x4) has4x4 = true;
            }
            sSriUsable = ShadingRateInfo.supportsPerImageTile && has2x2;
            BasisVrsMath.ResolveRates(has2x2, hasWide, hasTall, has4x4,
                ShadingRateInfo.QueryNativeValue(ShadingRateFragmentSize.FragmentSize2x2),
                ShadingRateInfo.QueryNativeValue(ShadingRateFragmentSize.FragmentSize4x2),
                ShadingRateInfo.QueryNativeValue(ShadingRateFragmentSize.FragmentSize2x4),
                ShadingRateInfo.QueryNativeValue(ShadingRateFragmentSize.FragmentSize4x4),
                out sRates, out sRatesAniso);
        }

        /// <summary>
        /// How far the graphics quality level shrinks the foveal radii. Medium and above leave
        /// the player's sliders alone; the two low tiers pull the sharp region in so more of
        /// the frame shades at the coarse rate.
        /// </summary>
        private static float FovealScaleForTier(int tier)
        {
            switch (tier)
            {
                case BasisQualityTier.VeryLow: return 0.5f;
                case BasisQualityTier.Low: return 0.7f;
                default: return 1f;
            }
        }
    }

    /// <summary>
    /// Debug overlay: paints the produced shading rate image over the final frame so the
    /// foveal regions are visible. Enqueued only when the feature's Debug Visualize flag is on.
    /// </summary>
    internal class BasisVariableRateShadingDebugPass : ScriptableRenderPass
    {
        public BasisVariableRateShadingDebugPass()
        {
            profilingSampler = new ProfilingSampler("BasisVariableRateShading Debug");
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (!frameData.Contains<UniversalShadingRateData>())
                return;

            UniversalShadingRateData vrsData = frameData.Get<UniversalShadingRateData>();
            if (!vrsData.isValid || !vrsData.shadingRateImage.IsValid())
                return;

            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            Vrs.ShadingRateImageToColorMaskTexture(renderGraph, vrsData.shadingRateImage, resourceData.activeColorTexture);
        }
    }
}
