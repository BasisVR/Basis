using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Basis.ImagePickup
{
    internal static class BasisAnimatedImageWorkEstimator
    {
        public static long ResetPixelCost(BasisAnimatedImageData data)
        {
            return (long)data.CanvasWidth * data.CanvasHeight;
        }

        public static long TransitionPixelCost(
            BasisAnimatedImageData data,
            int previousFrameIndex,
            int nextFrameIndex
        )
        {
            long pixels =
                previousFrameIndex >= 0
                    ? DisposalPixelCost(data.GetFrame(previousFrameIndex))
                    : 0;
            BasisAnimatedImageFrame frame = data.GetFrame(nextFrameIndex);
            pixels += frame.PixelCount;
            if (frame.Disposal == BasisAnimationDisposal.Previous)
                pixels += frame.PixelCount;
            return pixels;
        }

        public static void Estimate(
            BasisAnimatedImageData data,
            bool stateValid,
            long currentPlayIndex,
            int currentFrameIndex,
            long targetPlayIndex,
            int targetFrameIndex,
            out int transitions,
            out long pixels
        )
        {
            if (!stateValid || targetPlayIndex != currentPlayIndex || targetFrameIndex < currentFrameIndex)
            {
                transitions = targetFrameIndex + 1;
                pixels = ResetPixelCost(data);
                int previous = -1;
                for (int i = 0; i <= targetFrameIndex; i++)
                {
                    pixels += TransitionPixelCost(data, previous, i);
                    previous = i;
                }
                return;
            }

            transitions = Math.Max(0, targetFrameIndex - currentFrameIndex);
            pixels = 0;
            for (int i = currentFrameIndex + 1; i <= targetFrameIndex; i++)
            {
                pixels += TransitionPixelCost(data, i - 1, i);
            }
        }

        private static long DisposalPixelCost(BasisAnimatedImageFrame frame)
        {
            return frame.Disposal == BasisAnimationDisposal.None ? 0 : frame.PixelCount;
        }
    }

    /// <summary>
    /// CPU compositor used as a correctness reference and fallback when the GPU compositor
    /// cannot be initialized. The output texture stores premultiplied RGBA pixels.
    /// </summary>
    internal sealed class BasisAnimatedImageCpuCanvas : IDisposable
    {
        private readonly BasisAnimatedImageData _data;
        private NativeArray<Color32> _pixels;
        private NativeArray<Color32> _previousPixels;

        private Texture2D _texture;
        private JobHandle _composeHandle;
        private long _reservedCompositorBytes;
        private long _currentPlayIndex = -1;
        private int _currentFrameIndex = -1;
        private bool _stateValid;
        private bool _composePending;
        private bool _disposed;

        public Texture OutputTexture => _texture;
        public bool HasPendingCompose => _composePending;

        public BasisAnimatedImageCpuCanvas(BasisAnimatedImageData data)
        {
			_data = data ?? throw new ArgumentNullException(nameof(data));
            try
            {
                int canvasPixels = checked(data.CanvasWidth * data.CanvasHeight);
                _reservedCompositorBytes = checked(
					(long)canvasPixels
					* sizeof(byte)
					* 4L
					* (data.RequiresPreviousCanvas ? 4L : 3L)
                );
                if (!BasisAnimatedImageData.TryReserveCompositorBytes(_reservedCompositorBytes, out string budgetError))
                {
                    _reservedCompositorBytes = 0;
                    throw new BasisAnimationMemoryBudgetException(budgetError);
                }
                _pixels = new NativeArray<Color32>(
                    canvasPixels,
                    Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory
                );
                _previousPixels = new NativeArray<Color32>(
					data.RequiresPreviousCanvas ? canvasPixels : 1,
                    Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory
                );
                _texture = new Texture2D(_data.CanvasWidth, _data.CanvasHeight, TextureFormat.RGBA32, false, false)
                {
                    name = "Basis Animated Image CPU Canvas",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                    anisoLevel = 0,
                    hideFlags = HideFlags.HideAndDontSave,
                };
            }
            catch
            {
                if (_pixels.IsCreated)
                    _pixels.Dispose();
                if (_previousPixels.IsCreated)
                    _previousPixels.Dispose();
                if (_texture != null)
                {
#if UNITY_EDITOR
                    if (!Application.isPlaying)
                        UnityEngine.Object.DestroyImmediate(_texture);
                    else
#endif
                        UnityEngine.Object.Destroy(_texture);
                }
                _texture = null;
                ReleaseCompositorReservation();
                throw;
            }
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
            if (transitions > 0)
            {
                // The CPU fallback cannot upload only the changed rectangles.
                pixels += (long)_data.CanvasWidth * _data.CanvasHeight;
            }
        }

        public int UpdateToState(long targetPlayIndex, int targetFrameIndex)
        {
            return UpdateToState(
                targetPlayIndex,
                targetFrameIndex,
                int.MaxValue,
                long.MaxValue,
                out _
            );
        }

        public int UpdateToState(
            long targetPlayIndex,
            int targetFrameIndex,
            int transitionBudget,
            long pixelBudget,
            out long pixelsUsed
        )
        {
            int transitions = BeginUpdateToState(
                targetPlayIndex,
                targetFrameIndex,
                transitionBudget,
                pixelBudget,
                out pixelsUsed
            );
            FlushPendingCompose();
            return transitions;
        }

        /// <summary>
        /// Schedules composition and returns without waiting for it. The output texture only holds
        /// the requested state once <see cref="FlushPendingCompose"/> lands the job.
        /// </summary>
        public int BeginUpdateToState(
            long targetPlayIndex,
            int targetFrameIndex,
            int transitionBudget,
            long pixelBudget,
            out long pixelsUsed
        )
        {
            ThrowIfDisposed();
            ValidateTarget(targetPlayIndex, targetFrameIndex);
            // A queued job still owns the canvas buffers; land it before scheduling over them.
            FlushPendingCompose();
            pixelsUsed = 0;
            if (transitionBudget <= 0 || pixelBudget <= 0)
                return 0;

            bool reset =
                !_stateValid
                || targetPlayIndex != _currentPlayIndex
                || targetFrameIndex < _currentFrameIndex;
            int startFrame = reset ? -1 : _currentFrameIndex;
            long canvasPixels = BasisAnimatedImageWorkEstimator.ResetPixelCost(_data);
            long requiredPixels = canvasPixels;
            if (reset)
                requiredPixels += canvasPixels;
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

            _composeHandle = new BasisAnimatedImageCpuComposeJob
            {
                CanvasWidth = _data.CanvasWidth,
                Reset = reset ? (byte)1 : (byte)0,
                StartFrame = startFrame,
                TargetFrame = partialTargetFrame,
                Linear =
                    QualitySettings.activeColorSpace == ColorSpace.Linear
                        ? (byte)1
                        : (byte)0,
                Background = _data.BackgroundColor,
                Frames = _data.FramesNative,
                FramePixels = _data.PixelsNative,
                Canvas = _pixels,
                Previous = _previousPixels,
            }.Schedule();
            _composePending = true;

            _currentPlayIndex = targetPlayIndex;
            _currentFrameIndex = partialTargetFrame;
            _stateValid = true;
            pixelsUsed = requiredPixels;
            return transitions;
        }

        /// <summary>
        /// Completes a scheduled composition and uploads the canvas. Safe when nothing is pending.
        /// </summary>
        public void FlushPendingCompose()
        {
            if (!_composePending)
                return;
            _composePending = false;
            _composeHandle.Complete();
            _composeHandle = default;
            if (_disposed || _texture == null)
                return;
            _texture.SetPixelData(_pixels, 0);
            _texture.Apply(false, false);
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
                throw new ObjectDisposedException(nameof(BasisAnimatedImageCpuCanvas));
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            // The compose job writes these buffers; it must land before they are freed.
            _composeHandle.Complete();
            _composeHandle = default;
            _composePending = false;
            if (_pixels.IsCreated)
                _pixels.Dispose();
            if (_previousPixels.IsCreated)
                _previousPixels.Dispose();
            if (_texture != null)
            {
#if UNITY_EDITOR
                if (!Application.isPlaying)
                    UnityEngine.Object.DestroyImmediate(_texture);
                else
#endif
                    UnityEngine.Object.Destroy(_texture);
                _texture = null;
            }
            ReleaseCompositorReservation();
        }

        private void ReleaseCompositorReservation()
        {
            if (_reservedCompositorBytes <= 0)
                return;
            BasisAnimatedImageData.ReleaseCompositorBytes(_reservedCompositorBytes);
            _reservedCompositorBytes = 0;
        }
    }
}
