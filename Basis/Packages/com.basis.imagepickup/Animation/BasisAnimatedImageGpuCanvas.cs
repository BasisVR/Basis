using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Basis.ImagePickup
{
    /// <summary>
    /// Maintains a persistent GPU canvas and appends GIF/APNG-style frame composition
    /// operations to a caller-owned command buffer.
    /// </summary>
    internal sealed class BasisAnimatedImageGpuCanvas : IDisposable
    {
        private const int SourcePass = 0;
        private const int OverPass = 1;
        private const int ClearPass = 2;
        private const int CopyPremultipliedPass = 3;

        private static readonly int SourceTextureId = Shader.PropertyToID("_BasisImageAnimSourceTex");
        private static readonly int SourceUvRectId = Shader.PropertyToID("_BasisImageAnimSourceUvRect");
        private static readonly int ClearColorId = Shader.PropertyToID("_BasisImageAnimClearColor");

        private readonly BasisAnimatedImageData _data;
        private readonly Material _compositorMaterial;
        private BasisAnimationFrameAtlas _frameAtlas;
        private readonly bool _canCopyRenderTextureRegions;

        private RenderTexture _canvas;
        private RenderTexture _previousCanvas;
        private long _reservedCompositorBytes;
        private long _currentPlayIndex = -1;
        private int _currentFrameIndex = -1;
        private bool _stateValid;
        private bool _disposed;

        public Texture OutputTexture => _canvas;

        public BasisAnimatedImageGpuCanvas(BasisAnimatedImageData data, Material compositorMaterial)
        {
			_data = data ?? throw new ArgumentNullException(nameof(data));
            if (
                compositorMaterial == null
                || !BasisImagePickupRuntimeUtility.CanUseAnimationCompositorShader(
                    compositorMaterial.shader
                )
            )
            {
                throw new ArgumentException(
                    "Animated image compositor material is unsupported or missing required passes.",
                    nameof(compositorMaterial)
                );
            }
            _compositorMaterial = compositorMaterial;
            _canCopyRenderTextureRegions =
                (SystemInfo.copyTextureSupport & CopyTextureSupport.Basic) != 0;

            BasisAnimationFrameAtlas frameAtlas = null;
            try
            {
                long canvasPixels = checked((long)data.CanvasWidth * data.CanvasHeight);
                _reservedCompositorBytes = checked(
                    data.DecodedFramePixels * 12L
					+ canvasPixels * 4L * (data.RequiresPreviousCanvas ? 2L : 1L)
                    + (long)data.FrameCount * 32L
                );
                if (!BasisAnimatedImageData.TryReserveCompositorBytes(_reservedCompositorBytes, out string budgetError))
                {
                    _reservedCompositorBytes = 0;
                    throw new BasisAnimationMemoryBudgetException(budgetError);
                }
                frameAtlas = new BasisAnimationFrameAtlas(data);
                _frameAtlas = frameAtlas;
                if (!EnsureCreated())
                    throw new InvalidOperationException("Animated image GPU canvas could not be created.");
            }
            catch
            {
                frameAtlas?.Dispose();
                ReleaseRenderTexture(ref _previousCanvas);
                ReleaseRenderTexture(ref _canvas);
                ReleaseCompositorReservation();
                throw;
            }
        }

        public bool EnsureCreated()
        {
            ThrowIfDisposed();

            bool recreated = false;
            bool recoverFrameAtlas = false;
            if (_canvas == null)
            {
                _canvas = CreateCanvas("Basis Animated Image Canvas");
                recreated = true;
            }
            else if (!_canvas.IsCreated())
            {
                recreated = _canvas.Create();
                recoverFrameAtlas = recreated;
            }

			if (_data.RequiresPreviousCanvas)
            {
                if (_previousCanvas == null)
                {
                    _previousCanvas = CreateCanvas("Basis Animated Image Previous Canvas");
                    recreated = true;
                }
                else if (!_previousCanvas.IsCreated())
                {
                    bool previousCanvasRecreated = _previousCanvas.Create();
                    recreated |= previousCanvasRecreated;
                    recoverFrameAtlas |= previousCanvasRecreated;
                }
            }

            if (recoverFrameAtlas && !TryRebuildFrameAtlas())
            {
                Invalidate();
                return false;
            }

            bool canvasCreated = _canvas != null && _canvas.IsCreated();
            bool previousCanvasCreated =
				!_data.RequiresPreviousCanvas
                || (_previousCanvas != null && _previousCanvas.IsCreated());
            bool allRequiredCanvasesCreated = canvasCreated && previousCanvasCreated;

            if (recreated || !allRequiredCanvasesCreated)
                Invalidate();
            return allRequiredCanvasesCreated;
        }

        public bool HasPendingAtlasPage => _frameAtlas != null && _frameAtlas.HasPendingPage;

        public bool TryPrepareFrameAtlas(ref long pixelsRemaining)
        {
            ThrowIfDisposed();
            if (_frameAtlas == null)
                return false;
            bool ready = _frameAtlas.BeginNextPage(
                pixelsRemaining,
                out long pixelsUsed
            );
            pixelsRemaining = Math.Max(0, pixelsRemaining - pixelsUsed);
            return ready;
        }

        /// <summary>Completes a scheduled atlas page and uploads it to the GPU.</summary>
        public void FlushPendingAtlasPage()
        {
            _frameAtlas?.FlushPendingPage();
        }

        private bool TryRebuildFrameAtlas()
        {
            BasisAnimationFrameAtlas replacement = null;
            try
            {
                replacement = new BasisAnimationFrameAtlas(_data);
                _frameAtlas?.Dispose();
                _frameAtlas = replacement;
                return true;
            }
            catch
            {
                replacement?.Dispose();
                return false;
            }
        }

        public void Invalidate()
        {
            _stateValid = false;
            _currentPlayIndex = -1;
            _currentFrameIndex = -1;
        }

        public void EstimateWork(long targetPlayIndex, int targetFrameIndex, out int transitions, out long pixels)
        {
            ValidateTarget(targetPlayIndex, targetFrameIndex);

            BasisAnimatedImageWorkEstimator.Estimate(
                _data,
                _stateValid,
                _currentPlayIndex,
                _currentFrameIndex,
                targetPlayIndex,
                targetFrameIndex,
                out transitions,
                out pixels
            );
        }

        public int AppendToState(
            CommandBuffer commands,
            long targetPlayIndex,
            int targetFrameIndex,
            int transitionBudget,
            long pixelBudget,
            out long pixelsUsed
        )
        {
            if (commands == null)
                throw new ArgumentNullException(nameof(commands));
            ThrowIfDisposed();
            ValidateTarget(targetPlayIndex, targetFrameIndex);
            pixelsUsed = 0;

            if (
                transitionBudget <= 0
                || pixelBudget <= 0
                || !EnsureCreated()
                || _frameAtlas == null
                || !_frameAtlas.IsReady
            )
            {
                return 0;
            }

            bool reset =
                !_stateValid
                || targetPlayIndex != _currentPlayIndex
                || targetFrameIndex < _currentFrameIndex;
            int startFrame = reset ? -1 : _currentFrameIndex;
            long requiredPixels =
                reset ? BasisAnimatedImageWorkEstimator.ResetPixelCost(_data) : 0;
            if (requiredPixels > pixelBudget)
                return 0;

            int partialTargetFrame = startFrame;
            int transitions = 0;
            while (
                partialTargetFrame < targetFrameIndex
                && transitions < transitionBudget
            )
            {
                int nextFrameIndex = partialTargetFrame + 1;
                long transitionPixels = BasisAnimatedImageWorkEstimator.TransitionPixelCost(
                    _data,
                    partialTargetFrame,
                    nextFrameIndex
                );
                if (transitionPixels > pixelBudget - requiredPixels)
                    break;
                requiredPixels += transitionPixels;
                partialTargetFrame = nextFrameIndex;
                transitions++;
            }
            if (transitions <= 0)
                return 0;

            if (reset)
            {
                AppendReset(commands);
                _currentPlayIndex = targetPlayIndex;
                _currentFrameIndex = -1;
                _stateValid = true;
            }

            while (_currentFrameIndex < partialTargetFrame)
            {
                int nextFrameIndex = _currentFrameIndex + 1;
                if (_currentFrameIndex >= 0)
                    AppendDisposal(commands, _data.GetFrame(_currentFrameIndex));

                BasisAnimatedImageFrame nextFrame = _data.GetFrame(nextFrameIndex);
                if (nextFrame.Disposal == BasisAnimationDisposal.Previous)
                    AppendSavePrevious(commands, nextFrame.Destination);

                AppendFrame(commands, nextFrameIndex, nextFrame);
                _currentFrameIndex = nextFrameIndex;
            }

            pixelsUsed = requiredPixels;
            return transitions;
        }

        private void AppendReset(CommandBuffer commands)
        {
            commands.SetRenderTarget(_canvas, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store);
            commands.ClearRenderTarget(
                false,
                true,
                ToWorkingPremultipliedColor(_data.BackgroundColor)
            );
        }

        private void AppendDisposal(CommandBuffer commands, BasisAnimatedImageFrame frame)
        {
            switch (frame.Disposal)
            {
                case BasisAnimationDisposal.None:
                    return;
                case BasisAnimationDisposal.Background:
                    AppendClearRect(commands, frame.Destination, _data.BackgroundColor);
                    return;
                case BasisAnimationDisposal.Previous:
                    AppendRestorePrevious(commands, frame.Destination);
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(frame.Disposal), frame.Disposal, null);
            }
        }

        private void AppendFrame(CommandBuffer commands, int frameIndex, BasisAnimatedImageFrame frame)
        {
            int pass =
                frame.Blend == BasisAnimationBlend.Source ? SourcePass : OverPass;
            BasisAnimationFrameAtlasLocation location = _frameAtlas.GetLocation(frameIndex);
            Texture2D source = _frameAtlas.GetPage(location.PageIndex);
            AppendTexturedRect(commands, source, location.SourceRectangle, _canvas, frame.Destination, pass);
        }

        private void AppendSavePrevious(CommandBuffer commands, RectInt rectangle)
        {
            if (_previousCanvas == null)
                return;

            if (_canCopyRenderTextureRegions)
            {
                commands.CopyTexture(
                    _canvas,
                    0,
                    0,
                    rectangle.x,
                    rectangle.y,
                    rectangle.width,
                    rectangle.height,
                    _previousCanvas,
                    0,
                    0,
					rectangle.x,
					rectangle.y
                );
                return;
            }

            AppendTexturedRect(commands, _canvas, rectangle, _previousCanvas, rectangle, CopyPremultipliedPass);
        }

        private void AppendRestorePrevious(CommandBuffer commands, RectInt rectangle)
        {
            if (_previousCanvas == null)
                return;

            if (_canCopyRenderTextureRegions)
            {
                commands.CopyTexture(
                    _previousCanvas,
                    0,
                    0,
					rectangle.x,
					rectangle.y,
                    rectangle.width,
                    rectangle.height,
                    _canvas,
                    0,
                    0,
                    rectangle.x,
                    rectangle.y
                );
                return;
            }

            AppendTexturedRect(commands, _previousCanvas, rectangle, _canvas, rectangle, CopyPremultipliedPass);
        }

        private void AppendClearRect(CommandBuffer commands, RectInt destination, Color32 color)
        {
            Rect viewport = ToRect(destination);
            commands.SetRenderTarget(_canvas, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
            commands.SetViewport(viewport);
            commands.EnableScissorRect(viewport);
            commands.SetGlobalColor(ClearColorId, ToWorkingStraightColor(color));
            commands.DrawProcedural(Matrix4x4.identity, _compositorMaterial, ClearPass, MeshTopology.Triangles, 3, 1);
            commands.DisableScissorRect();
        }

        private void AppendTexturedRect(
            CommandBuffer commands,
            Texture source,
            RectInt sourceRectangle,
            RenderTexture destination,
            RectInt destinationRectangle,
            int pass
        )
        {
            Rect viewport = ToRect(destinationRectangle);
            Vector4 uvRect = new Vector4(
                sourceRectangle.x / (float)source.width,
                sourceRectangle.y / (float)source.height,
                sourceRectangle.width / (float)source.width,
                sourceRectangle.height / (float)source.height
            );

            commands.SetRenderTarget(destination, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
            commands.SetViewport(viewport);
            commands.EnableScissorRect(viewport);
            commands.SetGlobalTexture(SourceTextureId, source);
            commands.SetGlobalVector(SourceUvRectId, uvRect);
            commands.DrawProcedural(Matrix4x4.identity, _compositorMaterial, pass, MeshTopology.Triangles, 3, 1);
            commands.DisableScissorRect();
        }

        private RenderTexture CreateCanvas(string name)
        {
            var descriptor = new RenderTextureDescriptor(
				_data.CanvasWidth,
				_data.CanvasHeight,
                RenderTextureFormat.ARGB32,
                0
            )
            {
                msaaSamples = 1,
                volumeDepth = 1,
                useMipMap = false,
                autoGenerateMips = false,
                enableRandomWrite = false,
                useDynamicScale = false,
                sRGB = QualitySettings.activeColorSpace == ColorSpace.Linear,
                dimension = TextureDimension.Tex2D,
            };

            var texture = new RenderTexture(descriptor)
            {
                name = name,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                anisoLevel = 0,
                hideFlags = HideFlags.HideAndDontSave,
            };
            try
            {
                if (!texture.Create())
                    throw new InvalidOperationException($"Could not create {name}.");
                return texture;
            }
            catch
            {
                ReleaseRenderTexture(ref texture);
                throw;
            }
        }

        private static Rect ToRect(RectInt rectangle)
        {
            return new Rect(rectangle.x, rectangle.y, rectangle.width, rectangle.height);
        }

        private static Color ToWorkingStraightColor(Color32 color)
        {
            Color value = color;
            if (QualitySettings.activeColorSpace == ColorSpace.Linear)
            {
                float alpha = value.a;
                value = value.linear;
                value.a = alpha;
            }
            return value;
        }

        private static Color ToWorkingPremultipliedColor(Color32 color)
        {
            Color value = ToWorkingStraightColor(color);
            value.r *= value.a;
            value.g *= value.a;
            value.b *= value.a;
            return value;
        }

        private void ValidateTarget(long targetPlayIndex, int targetFrameIndex)
        {
            if (targetPlayIndex < 0)
                throw new ArgumentOutOfRangeException(nameof(targetPlayIndex));
            if (targetFrameIndex < 0 || targetFrameIndex >= _data.FrameCount)
                throw new ArgumentOutOfRangeException(nameof(targetFrameIndex));
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(BasisAnimatedImageGpuCanvas));
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            _frameAtlas?.Dispose();
            _frameAtlas = null;

            ReleaseRenderTexture(ref _previousCanvas);
            ReleaseRenderTexture(ref _canvas);
            ReleaseCompositorReservation();
        }

        private void ReleaseCompositorReservation()
        {
            if (_reservedCompositorBytes <= 0)
                return;
            BasisAnimatedImageData.ReleaseCompositorBytes(_reservedCompositorBytes);
            _reservedCompositorBytes = 0;
        }

        private static void ReleaseRenderTexture(ref RenderTexture texture)
        {
            if (texture == null)
                return;
            if (texture.IsCreated())
                texture.Release();
#if UNITY_EDITOR
            if (!Application.isPlaying)
                UnityEngine.Object.DestroyImmediate(texture);
            else
#endif
                UnityEngine.Object.Destroy(texture);
            texture = null;
        }
    }
}
