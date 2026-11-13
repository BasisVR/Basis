using System;
using Unity.Scripting.LifecycleManagement;
using Basis.BasisUI;
using Basis.Scripts.Drivers;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Basis.Scripts.Rendering
{
    [AutoStaticsCleanup]
    internal partial class BasisVariableRateShadingPass : ScriptableRenderPass
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
        private const string PropRateOut = "_BasisVrsRateOut";
        private const string PropDetail = "_BasisVrsDetail";
        private const string PropAdaptive = "_BasisVrsAdaptive";
        private const string PropReprojL = "_BasisVrsReprojL";
        private const string PropReprojR = "_BasisVrsReprojR";
        private const string PropPrevUnprojL = "_BasisVrsPrevUnprojL";
        private const string PropPrevUnprojR = "_BasisVrsPrevUnprojR";
        private const string PropColor = "_BasisVrsColor";
        private const string PropColorArray = "_BasisVrsColorArray";
        private const string PropRateIn = "_BasisVrsRateIn";
        private const string PropDetailOut = "_BasisVrsDetailOut";
        private const string PropAnalyze = "_BasisVrsAnalyze";
        internal static readonly int GazeFocalId = Shader.PropertyToID("_BasisGazeFocal");
        private static readonly int GazeBandsId = Shader.PropertyToID("_BasisGazeBands");
        private static readonly int GazeEffectsId = Shader.PropertyToID("_BasisGazeEffects");

        // Gaze foveated effects: full step counts out to where shading leaves 1x1, half of them
        // from where it reaches 4x4. Only while live eye-tracked gaze drives the foveation.
        private const float GazeEffectFloor = 0.5f;

        // Content adaptive thresholds on the largest step between neighbouring 2x2 / 4x4 block
        // averages, in sqrt(L / (1 + L)) units. Below these the coarser rate changes the tile by
        // less than a visible step.
        private const float AdaptiveThreshold2x2 = 0.02f;
        private const float AdaptiveThreshold4x4 = 0.015f;

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

        [NoAutoStaticsCleanup] private static readonly ProfilingSampler samplerVrs = new ProfilingSampler("BasisVariableRateShading");
        public static float GpuMs => samplerVrs.gpuElapsedTime;
        public static void SetProfilingEnabled(bool enabled) => samplerVrs.enableRecording = enabled;

        // Hardware shading-rate caps, resolved once — the graphics API cannot change without a restart.
        private static bool sCapsCached;
        private static bool sSriUsable;
        private static Vector4 sRates;
        private static Vector4 sRatesAniso;

        public static bool HardwareUsable
        {
            get
            {
                if (!BasisVariableRateShadingFeature.IsSupported) return false;
                CacheHardwareCaps();
                return sSriUsable;
            }
        }

        private readonly ComputeShader _buildShader;
        private readonly int _kernel;
        private readonly int _kernelAnalyze2D = -1;
        private readonly int _kernelAnalyzeArray = -1;

        // Content adaptive history: the build pass writes the rate it chose, the analysis pass
        // late in the frame stores per-tile detail plus that rate, and the next build reads it
        // back through a head-rotation reprojection.
        private RenderTexture _rateOut;
        private RTHandle _rateOutHandle;
        private RenderTexture _detail;
        private RTHandle _detailHandle;
        private int _detailFrame = -1;
        private int _builtFrame = -1;
        private Vector2Int _builtTiles;
        private int _builtTileSize;
        private readonly Matrix4x4[] _prevView = new Matrix4x4[2];
        private readonly Vector4[] _prevUnproj = new Vector4[2];
        private bool _havePrev;
        private TextureHandle _rateOutTexture;
        private TextureHandle _detailTexture;

        private float _gazeProjectDistance;
        private bool _yFlip;

        private Vector3 _lastFocalWorld;
        // D3D11 plugin path: the native view wraps a fixed texture, so the image is persistent
        // instead of a transient graph texture that could move between frames.
        private RenderTexture _nativeImage;
        private RTHandle _nativeImageHandle;
        private IntPtr _nativeImagePtr;
        private float _lastGazeTime = float.NegativeInfinity;
        private bool _activeLogged;

        public BasisVariableRateShadingPass(ComputeShader buildShader)
        {
            _buildShader = buildShader;
            if (_buildShader != null)
            {
                _kernel = _buildShader.FindKernel("CSMain");
                if (_buildShader.HasKernel("Analyze2D")) _kernelAnalyze2D = _buildShader.FindKernel("Analyze2D");
                if (_buildShader.HasKernel("AnalyzeArray")) _kernelAnalyzeArray = _buildShader.FindKernel("AnalyzeArray");
            }
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
            public TextureHandle rateOut;
            public TextureHandle detail;
            public Vector4 adaptive;
            public Matrix4x4 reprojL;
            public Matrix4x4 reprojR;
            public Vector4 prevUnprojL;
            public Vector4 prevUnprojR;
            public Vector4 gazeFocal;
            public Vector4 gazeBands;
            public Vector4 gazeEffects;
        }

        private class AnalyzePassData
        {
            public ComputeShader cs;
            public int kernel;
            public TextureHandle color;
            public bool array;
            public TextureHandle rateIn;
            public TextureHandle detailOut;
            public Vector4 analyze;
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

            bool d3d11 = BasisVariableRateShadingFeature.UsesD3D11Plugin;
            Vector2Int tiles = d3d11
                ? new Vector2Int((camDesc.width + BasisVrsD3D11.TileSize - 1) / BasisVrsD3D11.TileSize, (camDesc.height + BasisVrsD3D11.TileSize - 1) / BasisVrsD3D11.TileSize)
                : ShadingRateImage.GetAllocTileSize(camDesc.width, camDesc.height);
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

            RenderTextureDescriptor sriDesc = new RenderTextureDescriptor(tiles.x, tiles.y, d3d11 ? GraphicsFormat.R8_UInt : ShadingRateInfo.graphicsFormat, GraphicsFormat.None, 0)
            {
                msaaSamples = 1,
                useMipMap = false,
                autoGenerateMips = false,
                dimension = TextureDimension.Tex2D,
                enableRandomWrite = true,
                enableShadingRate = !d3d11,
            };
            TextureHandle sri = d3d11
                ? renderGraph.ImportTexture(NativeImage(tiles))
                : UniversalRenderer.CreateRenderGraphTexture(renderGraph, sriDesc, "_BasisShadingRateImage", clear: false);

            int tileSize = d3d11 ? BasisVrsD3D11.TileSize : ShadingRateInfo.imageTileSize.x;
            bool singleView = !cameraData.xr.enabled || cameraData.xr.singlePassEnabled;
            bool adaptive = BasisSettingsDefaults.VrsContentAdaptive.RawValue && singleView
                && _kernelAnalyze2D >= 0 && _kernelAnalyzeArray >= 0
                && tileSize >= 4 && tileSize <= 32 && (tileSize & 1) == 0;
            EnsureHistory(tiles);
            Matrix4x4 viewL = cameraData.GetViewMatrix(0);
            Matrix4x4 viewR = cameraData.GetViewMatrix(rightEye);
            bool historyUsable = adaptive && _havePrev && _detailFrame == Time.frameCount - 1
                && _builtTiles == tiles && _builtTileSize == tileSize;
            Matrix4x4 reprojL = historyUsable ? BasisVrsMath.ViewReprojection(_prevView[0], viewL) : Matrix4x4.identity;
            Matrix4x4 reprojR = historyUsable ? BasisVrsMath.ViewReprojection(_prevView[1], viewR) : Matrix4x4.identity;
            Vector4 prevUnprojL = _prevUnproj[0];
            Vector4 prevUnprojR = _prevUnproj[1];
            _prevView[0] = viewL;
            _prevView[1] = viewR;
            _prevUnproj[0] = unprojL;
            _prevUnproj[1] = unprojR;
            _havePrev = singleView;
            _builtFrame = adaptive ? Time.frameCount : -1;
            _builtTiles = tiles;
            _builtTileSize = tileSize;
            _rateOutTexture = renderGraph.ImportTexture(_rateOutHandle);
            _detailTexture = renderGraph.ImportTexture(_detailHandle);

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
                data.rateOut = _rateOutTexture;
                data.detail = _detailTexture;
                data.adaptive = new Vector4(AdaptiveThreshold2x2, AdaptiveThreshold4x4, historyUsable ? 1f : 0f, adaptive ? 1f : 0f);
                data.reprojL = reprojL;
                data.reprojR = reprojR;
                data.prevUnprojL = prevUnprojL;
                data.prevUnprojR = prevUnprojR;
                data.gazeFocal = new Vector4(_lastFocalWorld.x, _lastFocalWorld.y, _lastFocalWorld.z, gazeWeight);
                data.gazeBands = new Vector4(cosL.x, cosL.z, GazeEffectFloor, 0f);
                data.gazeEffects = new Vector4(
                    BasisSettingsDefaults.GazeFoveateReflections.RawValue ? 1f : 0f,
                    BasisSettingsDefaults.GazeFoveateFog.RawValue ? 1f : 0f,
                    BasisSettingsDefaults.GazeFoveateRayTracedGI.RawValue ? 1f : 0f,
                    BasisSettingsDefaults.GazeFoveateGI.RawValue ? 1f : 0f);
                BasisGazeFoveationState.Focal = data.gazeFocal;
                BasisGazeFoveationState.Bands = data.gazeBands;
                BasisGazeFoveationState.Effects = data.gazeEffects;

                builder.UseTexture(sri, AccessFlags.Write);
                builder.UseTexture(_rateOutTexture, AccessFlags.Write);
                builder.UseTexture(_detailTexture, AccessFlags.Read);
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);

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
                    ctx.cmd.SetComputeTextureParam(d.cs, d.kernel, PropRateOut, d.rateOut);
                    ctx.cmd.SetComputeTextureParam(d.cs, d.kernel, PropDetail, d.detail);
                    ctx.cmd.SetComputeVectorParam(d.cs, PropAdaptive, d.adaptive);
                    ctx.cmd.SetComputeMatrixParam(d.cs, PropReprojL, d.reprojL);
                    ctx.cmd.SetComputeMatrixParam(d.cs, PropReprojR, d.reprojR);
                    ctx.cmd.SetComputeVectorParam(d.cs, PropPrevUnprojL, d.prevUnprojL);
                    ctx.cmd.SetComputeVectorParam(d.cs, PropPrevUnprojR, d.prevUnprojR);
                    ctx.cmd.SetGlobalVector(GazeFocalId, d.gazeFocal);
                    ctx.cmd.SetGlobalVector(GazeBandsId, d.gazeBands);
                    ctx.cmd.SetGlobalVector(GazeEffectsId, d.gazeEffects);
                    int groupsX = (d.tiles.x + 7) / 8;
                    int groupsY = (d.tiles.y + 7) / 8;
                    ctx.cmd.DispatchCompute(d.cs, d.kernel, groupsX, groupsY, 1);
                });
            }

            UniversalShadingRateData vrsData = frameData.GetOrCreate<UniversalShadingRateData>();
            vrsData.shadingRateImage = sri;
            vrsData.nativeShadingRateImage = d3d11 ? _nativeImagePtr : IntPtr.Zero;
            vrsData.isValid = true;
            if (d3d11)
            {
                UniversalShadingRateData.nativeRenderEvent = BasisVrsD3D11.RenderEvent;
                if (BasisVrsD3D11.UncoveredCount > 0)
                    BasisDebug.LogWarningOnce($"D3D11 VRS skipped passes whose target was larger than the shading rate image ({tiles.x}x{tiles.y} tiles); usually an eye texture resize frame.", BasisDebug.LogTag.Device);
            }

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

        internal void RecordAnalyze(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (_builtFrame != Time.frameCount || _buildShader == null)
                return;

            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            if (!ReferenceEquals(cameraData.camera, BasisLocalCameraDriver.CameraInstance))
                return;

            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            if (resourceData.isActiveTargetBackBuffer)
                return;
            TextureHandle color = resourceData.activeColorTexture;
            if (!color.IsValid())
                return;
            TextureDesc colorDesc = renderGraph.GetTextureDesc(color);
            if (colorDesc.bindTextureMS)
                return;
            bool array = colorDesc.dimension == TextureDimension.Tex2DArray;
            RenderTextureDescriptor camDesc = cameraData.cameraTargetDescriptor;

            using (var builder = renderGraph.AddComputePass<AnalyzePassData>("BasisVRS Analyze", out AnalyzePassData data, samplerVrs))
            {
                data.cs = _buildShader;
                data.kernel = array ? _kernelAnalyzeArray : _kernelAnalyze2D;
                data.color = color;
                data.array = array;
                data.rateIn = _rateOutTexture;
                data.detailOut = _detailTexture;
                data.analyze = new Vector4(_builtTileSize, camDesc.width, camDesc.height, array ? Mathf.Max(1, colorDesc.slices) : 1f);
                data.tiles = _builtTiles;

                builder.UseTexture(color, AccessFlags.Read);
                builder.UseTexture(_rateOutTexture, AccessFlags.Read);
                builder.UseTexture(_detailTexture, AccessFlags.Write);
                builder.AllowPassCulling(false);

                builder.SetRenderFunc((AnalyzePassData d, ComputeGraphContext ctx) =>
                {
                    ctx.cmd.SetComputeTextureParam(d.cs, d.kernel, d.array ? PropColorArray : PropColor, d.color);
                    ctx.cmd.SetComputeTextureParam(d.cs, d.kernel, PropRateIn, d.rateIn);
                    ctx.cmd.SetComputeTextureParam(d.cs, d.kernel, PropDetailOut, d.detailOut);
                    ctx.cmd.SetComputeVectorParam(d.cs, PropAnalyze, d.analyze);
                    ctx.cmd.DispatchCompute(d.cs, d.kernel, d.tiles.x, d.tiles.y, 1);
                });
            }
            _detailFrame = Time.frameCount;
        }

        private void EnsureHistory(Vector2Int tiles)
        {
            if (_rateOut != null && _rateOut.width == tiles.x && _rateOut.height == tiles.y && _rateOut.IsCreated() && _detail.IsCreated())
                return;
            ReleaseHistory();
            _rateOut = CreateTileTarget(tiles, GraphicsFormat.R8_UInt, "_BasisVrsRateHistory");
            _rateOutHandle = RTHandles.Alloc(_rateOut);
            _detail = CreateTileTarget(tiles, GraphicsFormat.R16G16B16A16_SFloat, "_BasisVrsDetail");
            _detailHandle = RTHandles.Alloc(_detail);
            _detailFrame = -1;
        }

        private static RenderTexture CreateTileTarget(Vector2Int tiles, GraphicsFormat format, string name)
        {
            RenderTexture rt = new RenderTexture(new RenderTextureDescriptor(tiles.x, tiles.y, format, GraphicsFormat.None, 0) { enableRandomWrite = true, useMipMap = false, autoGenerateMips = false, msaaSamples = 1 })
            {
                name = name,
                filterMode = FilterMode.Point,
                hideFlags = HideFlags.HideAndDontSave,
            };
            rt.Create();
            return rt;
        }

        private void ReleaseHistory()
        {
            _rateOutHandle?.Release();
            _rateOutHandle = null;
            _detailHandle?.Release();
            _detailHandle = null;
            if (_rateOut != null)
            {
                _rateOut.Release();
                UnityEngine.Object.DestroyImmediate(_rateOut);
                _rateOut = null;
            }
            if (_detail != null)
            {
                _detail.Release();
                UnityEngine.Object.DestroyImmediate(_detail);
                _detail = null;
            }
            _detailFrame = -1;
            _havePrev = false;
        }

        private RTHandle NativeImage(Vector2Int tiles)
        {
            if (_nativeImage != null && _nativeImage.width == tiles.x && _nativeImage.height == tiles.y && _nativeImage.IsCreated())
                return _nativeImageHandle;
            ReleaseNativeImage();
            _nativeImage = new RenderTexture(new RenderTextureDescriptor(tiles.x, tiles.y, GraphicsFormat.R8_UInt, GraphicsFormat.None, 0) { enableRandomWrite = true, useMipMap = false, autoGenerateMips = false, msaaSamples = 1 })
            {
                name = "_BasisShadingRateImageD3D11",
                filterMode = FilterMode.Point,
                hideFlags = HideFlags.HideAndDontSave,
            };
            _nativeImage.Create();
            _nativeImagePtr = _nativeImage.GetNativeTexturePtr();
            _nativeImageHandle = RTHandles.Alloc(_nativeImage);
            return _nativeImageHandle;
        }

        private void ReleaseNativeImage()
        {
            if (_nativeImage == null)
                return;
            IntPtr renderEvent = BasisVrsD3D11.RenderEvent;
            if (renderEvent != IntPtr.Zero) GL.IssuePluginEvent(renderEvent, BasisVrsD3D11.EventRelease);
            _nativeImageHandle?.Release();
            _nativeImageHandle = null;
            _nativeImage.Release();
            UnityEngine.Object.DestroyImmediate(_nativeImage);
            _nativeImage = null;
            _nativeImagePtr = IntPtr.Zero;
        }

        public void Dispose()
        {
            ReleaseNativeImage();
            ReleaseHistory();
        }

        private static void CacheHardwareCaps()
        {
            if (sCapsCached)
                return;
            sCapsCached = true;

            if (BasisVariableRateShadingFeature.UsesD3D11Plugin)
            {
                // NVAPI covers every rate the build writes; the plugin maps the D3D12 codes through its lookup table.
                sSriUsable = true;
                BasisVrsMath.ResolveRates(true, true, true, true, 5u, 9u, 6u, 10u, out sRates, out sRatesAniso);
                return;
            }

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
    /// Records the content adaptive analysis late in the frame, once the scene colour is final
    /// but before post processing, so next frame's shading rate image can coarsen flat tiles.
    /// </summary>
    internal class BasisVrsAnalyzePass : ScriptableRenderPass
    {
        private readonly BasisVariableRateShadingPass _owner;

        public BasisVrsAnalyzePass(BasisVariableRateShadingPass owner)
        {
            _owner = owner;
            profilingSampler = new ProfilingSampler("BasisVariableRateShading Analyze");
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            _owner.RecordAnalyze(renderGraph, frameData);
        }
    }

    /// <summary>
    /// Clears the gaze foveated effect weight at the end of the local player's camera, so mirrors,
    /// handheld cameras and the next frame's other cameras render their effects at full quality.
    /// </summary>
    internal class BasisGazeEffectsResetPass : ScriptableRenderPass
    {
        private class PassData { }

        public BasisGazeEffectsResetPass()
        {
            profilingSampler = new ProfilingSampler("BasisVariableRateShading Gaze Reset");
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            BasisGazeFoveationState.Clear();
            using (var builder = renderGraph.AddUnsafePass<PassData>("BasisVRS Gaze Reset", out PassData _, profilingSampler))
            {
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc((PassData d, UnsafeGraphContext ctx) => ctx.cmd.SetGlobalVector(BasisVariableRateShadingPass.GazeFocalId, Vector4.zero));
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
