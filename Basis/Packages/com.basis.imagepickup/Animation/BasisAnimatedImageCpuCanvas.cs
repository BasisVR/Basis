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

        internal static bool IsKeyframe(BasisAnimatedImageData data, BasisAnimatedImageFrame frame)
        {
            return frame.Blend == BasisAnimationBlend.Source
                && frame.Disposal != BasisAnimationDisposal.Previous
                && frame.X == 0
                && frame.Y == 0
                && frame.Width == data.CanvasWidth
                && frame.Height == data.CanvasHeight;
        }

        internal static int FindFirstFrameToDraw(
            BasisAnimatedImageData data,
            int currentFrameIndex,
            int targetFrameIndex,
            out bool keyframe
        )
        {
            for (int frameIndex = targetFrameIndex; frameIndex > currentFrameIndex; frameIndex--)
            {
                if (IsKeyframe(data, data.GetFrame(frameIndex)))
                {
                    keyframe = true;
                    return frameIndex;
                }
            }
            keyframe = false;
            return currentFrameIndex + 1;
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
            bool reset = !stateValid || targetPlayIndex != currentPlayIndex || targetFrameIndex < currentFrameIndex;
            int firstFrame = FindFirstFrameToDraw(data, reset ? -1 : currentFrameIndex, targetFrameIndex, out bool keyframe);
            transitions = Math.Max(0, targetFrameIndex - firstFrame + 1);
            pixels = reset && !keyframe ? ResetPixelCost(data) : 0;
            int previous = keyframe ? -1 : firstFrame - 1;
            for (int i = firstFrame; i <= targetFrameIndex; i++)
            {
                pixels += TransitionPixelCost(data, previous, i);
                previous = i;
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
            int firstFrame = BasisAnimatedImageWorkEstimator.FindFirstFrameToDraw(
                _data,
                reset ? -1 : _currentFrameIndex,
                targetFrameIndex,
                out bool keyframe
            );
            long canvasPixels = BasisAnimatedImageWorkEstimator.ResetPixelCost(_data);
            long requiredPixels = canvasPixels;
            if (reset && !keyframe)
                requiredPixels += canvasPixels;
            if (requiredPixels > pixelBudget)
                return 0;

            int partialTargetFrame = firstFrame - 1;
            int transitions = 0;
            while (
                partialTargetFrame < targetFrameIndex
                && transitions < transitionBudget
            )
            {
                int nextFrameIndex = partialTargetFrame + 1;
                long transitionPixels = BasisAnimatedImageWorkEstimator.TransitionPixelCost(
                    _data,
                    keyframe && transitions == 0 ? -1 : partialTargetFrame,
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
                Reset = reset && !keyframe ? (byte)1 : (byte)0,
                Keyframe = keyframe ? (byte)1 : (byte)0,
                StartFrame = firstFrame - 1,
                TargetFrame = partialTargetFrame,
                Linear =
                    QualitySettings.activeColorSpace == ColorSpace.Linear
                        ? (byte)1
                        : (byte)0,
                Background = _data.BackgroundColor,
                Frames = _data.FramesNative,
                FramePixels = _data.PixelsNative,
                Canvas = _texture.GetPixelData<Color32>(0),
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
