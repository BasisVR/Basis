using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace Basis.ImagePickup
{
    public enum BasisAnimationBlend : byte
    {
        Source = 0,
        Over = 1,
    }

    public enum BasisAnimationDisposal : byte
    {
        None = 0,
        Background = 1,
        Previous = 2,
    }

    /// <summary>
    /// Burst-compatible frame metadata. All pixels live in one persistent native pool owned by
    /// <see cref="BasisAnimatedImageData"/>; PixelOffset and PixelCount index that pool.
    /// </summary>
    public struct BasisAnimatedImageFrame
    {
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public int PixelOffset;
        public int PixelCount;
        public long DurationMicroseconds;
        public long EndTimeMicroseconds;
        public BasisAnimationBlend Blend;
        public BasisAnimationDisposal Disposal;
        public ushort Reserved;

        public RectInt Destination => new RectInt(X, Y, Width, Height);
    }

    /// <summary>
    /// Managed authoring/test input. Runtime decoders do not retain this object or its pixel array.
    /// </summary>
    public sealed class BasisAnimatedImageFrameSource
    {
        public RectInt Destination { get; }
        public long DurationMicroseconds { get; }
        public BasisAnimationBlend Blend { get; }
        public BasisAnimationDisposal Disposal { get; }
        public Color32[] Pixels { get; }

        public BasisAnimatedImageFrameSource(
            RectInt destination,
            long durationMicroseconds,
            BasisAnimationBlend blend,
            BasisAnimationDisposal disposal,
            Color32[] pixels
        )
        {
            Destination = destination;
            DurationMicroseconds = durationMicroseconds;
            Blend = blend;
            Disposal = disposal;
            Pixels = pixels;
        }
    }

    /// <summary>
    /// Native animation owner used by every runtime stage while an animation is resident. Payload-backed
    /// pickups may dispose this decoded pool and reconstruct it later from their compact payload.
    /// </summary>
    public sealed class BasisAnimatedImageData : IDisposable
    {
        private static readonly object MemoryBudgetLock = new();
        private static long _residentNativeBytes;
        private static long _residentCompositorBytes;
        private static long _reservedRestoreBytes;
        private static long _reservedWorkingBytes;

        private NativeArray<BasisAnimatedImageFrame> _frames;
        private NativeArray<Color32> _pixels;
        private NativeArray<long> _frameEndTimesMicroseconds;
        private long _nativeByteCount;
        private bool _disposed;

        public int CanvasWidth { get; }
        public int CanvasHeight { get; }
        public int TotalPlayCount { get; }
        public long TotalDurationMicroseconds { get; }
        public long DecodedFramePixels { get; }
        public long NativeByteCount => _nativeByteCount;
        internal static long TotalResidentNativeBytes
        {
            get
            {
                lock (MemoryBudgetLock)
                    return _residentNativeBytes;
            }
        }
        internal static long TotalResidentCompositorBytes
        {
            get
            {
                lock (MemoryBudgetLock)
                    return _residentCompositorBytes;
            }
        }
        public Color32 BackgroundColor { get; }
        public bool HasAnyAlpha { get; }
        public bool HasPartialAlpha { get; }
        public bool RequiresPreviousCanvas { get; }
        public int FrameCount => _frames.IsCreated ? _frames.Length : 0;
		public bool IsCreated => !_disposed && _frames.IsCreated && _pixels.IsCreated;

        internal NativeArray<BasisAnimatedImageFrame> FramesNative => _frames;
        internal NativeArray<Color32> PixelsNative => _pixels;

        internal BasisAnimatedImageData(
            int canvasWidth,
            int canvasHeight,
            int totalPlayCount,
            Color32 backgroundColor,
            NativeArray<BasisAnimatedImageFrame> frames,
            NativeArray<Color32> pixels,
            NativeArray<long> frameEndTimesMicroseconds,
            long totalDurationMicroseconds,
            bool hasAnyAlpha,
            bool hasPartialAlpha,
            bool requiresPreviousCanvas
        )
        {
            CanvasWidth = canvasWidth;
            CanvasHeight = canvasHeight;
            TotalPlayCount = totalPlayCount;
            BackgroundColor = backgroundColor;
            _frames = frames;
            _pixels = pixels;
            _frameEndTimesMicroseconds = frameEndTimesMicroseconds;
            TotalDurationMicroseconds = totalDurationMicroseconds;
            DecodedFramePixels = pixels.IsCreated ? pixels.Length : 0;
            _nativeByteCount = CalculateNativeByteCount(
                frames.IsCreated ? frames.Length : 0,
                pixels.IsCreated ? pixels.Length : 0,
                frameEndTimesMicroseconds.IsCreated
                    ? frameEndTimesMicroseconds.Length
                    : 0
            );
            lock (MemoryBudgetLock)
                _residentNativeBytes = checked(_residentNativeBytes + _nativeByteCount);
            HasAnyAlpha = hasAnyAlpha;
            HasPartialAlpha = hasPartialAlpha;
            RequiresPreviousCanvas = requiresPreviousCanvas;
        }

        internal static long CalculateNativeByteCount(int frameCount, int pixelCount, int frameEndCount)
        {
            if (frameCount < 0 || pixelCount < 0 || frameEndCount < 0)
                throw new ArgumentOutOfRangeException();
            return checked(
                (long)frameCount * UnsafeUtility.SizeOf<BasisAnimatedImageFrame>()
                + (long)pixelCount * UnsafeUtility.SizeOf<Color32>()
                + (long)frameEndCount * sizeof(long)
            );
        }

        internal static bool ShouldPauseNewDecode()
        {
            lock (MemoryBudgetLock)
            {
                return _residentNativeBytes
                        >= BasisImagePickupSettings.MaxResidentAnimationNativeBytes
                    || _residentCompositorBytes
                        >= BasisImagePickupSettings.MaxResidentAnimationCompositorBytes
                    || _residentNativeBytes
                        + _residentCompositorBytes
                        + _reservedRestoreBytes
                        + _reservedWorkingBytes
                        >= BasisImagePickupSettings.MaxAnimationNativeWorkingSetBytes;
            }
        }

        internal static long EstimateGifDecodeWorkingBytes(int sourceBytes)
        {
            if (sourceBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(sourceBytes));

            long frameScratchBytes = checked(
                (long)BasisImagePickupSettings.MaxAnimationFrames
                * (4096L * sizeof(ushort) + 4096L + 4097L + sizeof(int) + 256L)
            );
            return checked(
                sourceBytes
                + BasisImagePickupSettings.MaxAnimationDecodedFramePixels * 4L
                + BasisImagePickupSettings.MaxAnimationCanvasPixels * 4L
                + frameScratchBytes
                + 1024L * 1024L
            );
        }

        internal static bool TryReserveRestoreBytes(long nativeBytes)
        {
            if (nativeBytes <= 0)
                return false;
            lock (MemoryBudgetLock)
            {
                if (
                    !FitsMemoryBudget(
                        _residentNativeBytes,
                        _reservedRestoreBytes,
                        nativeBytes,
                        BasisImagePickupSettings.MaxResidentAnimationNativeBytes
                    )
                    || !FitsMemoryBudget(
                        checked(_residentNativeBytes + _residentCompositorBytes),
                        checked(_reservedRestoreBytes + _reservedWorkingBytes),
                        nativeBytes,
                        BasisImagePickupSettings.MaxAnimationNativeWorkingSetBytes
                    )
                )
                {
                    return false;
                }
                _reservedRestoreBytes = checked(_reservedRestoreBytes + nativeBytes);
                return true;
            }
        }

        internal static void ReleaseRestoreBytes(long nativeBytes)
        {
            if (nativeBytes <= 0)
                return;
            lock (MemoryBudgetLock)
                _reservedRestoreBytes = Math.Max(0, _reservedRestoreBytes - nativeBytes);
        }

        internal static bool CanReserveWorkingBytes(long nativeBytes)
        {
            if (nativeBytes <= 0)
                return false;
            lock (MemoryBudgetLock)
                return FitsWorkingMemoryBudgetNoLock(nativeBytes);
        }

        internal static bool TryReserveWorkingBytes(long nativeBytes, out string error)
        {
            error = null;
            if (nativeBytes <= 0)
            {
                error = "Animation working-memory reservation is invalid.";
                return false;
            }
            lock (MemoryBudgetLock)
            {
                if (!FitsWorkingMemoryBudgetNoLock(nativeBytes))
                {
                    error =
                        $"Animation native working set would exceed "
                        + $"{BasisImagePickupSettings.MaxAnimationNativeWorkingSetBytes / (1024L * 1024L):N0} MiB "
                        + $"{((_residentNativeBytes + _residentCompositorBytes) / (1024L * 1024L)):N0} MiB resident, "
                        + $"{(_residentCompositorBytes / (1024L * 1024L)):N0} MiB compositor, "
                        + $"{(_reservedRestoreBytes + _reservedWorkingBytes) / (1024L * 1024L):N0} MiB active, "
                        + $"{nativeBytes / (1024L * 1024L):N0} MiB requested).";
                    return false;
                }
                _reservedWorkingBytes = checked(_reservedWorkingBytes + nativeBytes);
                return true;
            }
        }

        private static bool FitsWorkingMemoryBudgetNoLock(long nativeBytes)
        {
            return FitsMemoryBudget(
                checked(_residentNativeBytes + _residentCompositorBytes),
                checked(_reservedRestoreBytes + _reservedWorkingBytes),
                nativeBytes,
                BasisImagePickupSettings.MaxAnimationNativeWorkingSetBytes
            );
        }

        internal static void ReleaseWorkingBytes(long nativeBytes)
        {
            if (nativeBytes <= 0)
                return;
            lock (MemoryBudgetLock)
                _reservedWorkingBytes = Math.Max(0, _reservedWorkingBytes - nativeBytes);
        }

        internal static bool TryReserveCompositorBytes(long nativeBytes, out string error)
        {
            error = null;
            if (nativeBytes <= 0)
            {
                error = "Animation compositor reservation is invalid.";
                return false;
            }

            lock (MemoryBudgetLock)
            {
                if (
                    !FitsMemoryBudget(
                        _residentCompositorBytes,
                        0,
                        nativeBytes,
                        BasisImagePickupSettings.MaxResidentAnimationCompositorBytes
                    )
                    || !FitsMemoryBudget(
                        checked(_residentNativeBytes + _residentCompositorBytes),
                        checked(_reservedRestoreBytes + _reservedWorkingBytes),
                        nativeBytes,
                        BasisImagePickupSettings.MaxAnimationNativeWorkingSetBytes
                    )
                )
                {
                    error = "Animation compositor memory budget is exhausted.";
                    return false;
                }

                _residentCompositorBytes = checked(_residentCompositorBytes + nativeBytes);
                return true;
            }
        }

        internal static void ReleaseCompositorBytes(long nativeBytes)
        {
            if (nativeBytes <= 0)
                return;
            lock (MemoryBudgetLock)
                _residentCompositorBytes = Math.Max(0, _residentCompositorBytes - nativeBytes);
        }

        internal static bool FitsMemoryBudget(
            long residentBytes,
            long reservedBytes,
            long candidateBytes,
            long limitBytes
        )
        {
            return residentBytes >= 0
                && reservedBytes >= 0
                && candidateBytes >= 0
                && limitBytes >= 0
                && residentBytes <= limitBytes
                && reservedBytes <= limitBytes - residentBytes
                && candidateBytes <= limitBytes - residentBytes - reservedBytes;
        }

        public BasisAnimatedImageFrame GetFrame(int index)
        {
            ThrowIfDisposed();
            if ((uint)index >= (uint)_frames.Length)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _frames[index];
        }

        public Color32[] CopyFramePixelsToManaged(int index)
        {
            BasisAnimatedImageFrame frame = GetFrame(index);
            var result = new Color32[frame.PixelCount];
            NativeArray<Color32>.Copy(_pixels, frame.PixelOffset, result, 0, frame.PixelCount);
            return result;
        }

        public static bool TryCreate(
            int canvasWidth,
            int canvasHeight,
            int totalPlayCount,
            Color32 backgroundColor,
            IReadOnlyList<BasisAnimatedImageFrameSource> sourceFrames,
            out BasisAnimatedImageData data,
            out string error
        )
        {
            data = null;
            error = null;

            if (!ValidateCanvas(canvasWidth, canvasHeight, out error))
                return false;
            if (totalPlayCount < 0)
            {
                error = "Animation play count cannot be negative.";
                return false;
            }
            if (sourceFrames == null)
            {
                error = "Animation has no frames.";
                return false;
            }
            int sourceFrameCount = sourceFrames.Count;
            if (sourceFrameCount == 0)
            {
                error = "Animation has no frames.";
                return false;
            }
            if (sourceFrameCount > BasisImagePickupSettings.MaxAnimationFrames)
            {
                error =
                    $"Animation has {sourceFrameCount:N0} frames. The maximum is "
                    + $"{BasisImagePickupSettings.MaxAnimationFrames:N0}.";
                return false;
            }

            long decodedPixels = 0;
            long totalDuration = 0;
            bool hasAnyAlpha = backgroundColor.a < byte.MaxValue;
            bool hasPartialAlpha =
                backgroundColor.a > 0 && backgroundColor.a < byte.MaxValue;
            bool requiresPrevious = false;

            for (int i = 0; i < sourceFrameCount; i++)
            {
                BasisAnimatedImageFrameSource source = sourceFrames[i];
                if (source == null)
                {
                    error = $"Animation frame {i + 1:N0} is null.";
                    return false;
                }
                if (
                    !ValidateSourceFrame(
                        source,
                        i,
                        canvasWidth,
                        canvasHeight,
                        ref decodedPixels,
                        ref totalDuration,
                        ref hasAnyAlpha,
                        ref hasPartialAlpha,
                        ref requiresPrevious,
                        out error
                    )
                )
                {
                    return false;
                }
            }

            NativeArray<BasisAnimatedImageFrame> frames = default;
            NativeArray<Color32> pixels = default;
            NativeArray<long> frameEnds = default;
            try
            {
                frames = new NativeArray<BasisAnimatedImageFrame>(
                    sourceFrameCount,
                    Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory
                );
                pixels = new NativeArray<Color32>(
                    checked((int)decodedPixels),
                    Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory
                );
                frameEnds = new NativeArray<long>(
                    sourceFrameCount,
                    Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory
                );

                int pixelOffset = 0;
                long endTime = 0;
                for (int frameIndex = 0; frameIndex < sourceFrameCount; frameIndex++)
                {
                    BasisAnimatedImageFrameSource source = sourceFrames[frameIndex];
                    long duration = Math.Max(
                        source.DurationMicroseconds,
                        BasisImagePickupSettings.MinAnimationFrameDurationMicroseconds
                    );
                    endTime = checked(endTime + duration);
                    RectInt destination = source.Destination;
                    int pixelCount = checked(destination.width * destination.height);

                    frames[frameIndex] = new BasisAnimatedImageFrame
                    {
                        X = destination.x,
                        Y = destination.y,
                        Width = destination.width,
                        Height = destination.height,
                        PixelOffset = pixelOffset,
                        PixelCount = pixelCount,
                        DurationMicroseconds = duration,
                        EndTimeMicroseconds = endTime,
                        Blend = source.Blend,
                        Disposal = source.Disposal,
                        Reserved = 0,
                    };
                    frameEnds[frameIndex] = endTime;
                    for (int pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
                        pixels[pixelOffset + pixelIndex] = source.Pixels[pixelIndex];
                    pixelOffset += pixelCount;
                }

                data = new BasisAnimatedImageData(
                    canvasWidth,
                    canvasHeight,
                    totalPlayCount,
                    backgroundColor,
                    frames,
                    pixels,
                    frameEnds,
                    totalDuration,
                    hasAnyAlpha,
                    hasPartialAlpha,
                    requiresPrevious
                );
                return true;
            }
            catch (Exception exception)
            {
                if (frames.IsCreated)
                    frames.Dispose();
                if (pixels.IsCreated)
                    pixels.Dispose();
                if (frameEnds.IsCreated)
                    frameEnds.Dispose();
                error = "Animation native allocation failed: " + exception.Message;
                return false;
            }
        }

        internal static bool TryAdoptNative(
            int canvasWidth,
            int canvasHeight,
            int totalPlayCount,
            Color32 backgroundColor,
            NativeArray<BasisAnimatedImageFrame> frames,
            NativeArray<Color32> pixels,
            NativeArray<long> frameEnds,
            long totalDurationMicroseconds,
            bool hasAnyAlpha,
            bool hasPartialAlpha,
            bool requiresPreviousCanvas,
            out BasisAnimatedImageData data,
            out string error
        )
        {
            data = null;
            error = null;
            if (!ValidateCanvas(canvasWidth, canvasHeight, out error))
                return false;
            if (!frames.IsCreated || frames.Length == 0 || frames.Length > BasisImagePickupSettings.MaxAnimationFrames)
            {
                error = "Animation native frame array is invalid.";
                return false;
            }
            if (
                !pixels.IsCreated
                || pixels.Length <= 0
                || pixels.Length
                    > BasisImagePickupSettings.MaxAnimationDecodedFramePixels
            )
            {
                error = "Animation native pixel pool is invalid.";
                return false;
            }
            if (!frameEnds.IsCreated || frameEnds.Length != frames.Length)
            {
                error = "Animation native frame-end array is invalid.";
                return false;
            }
            if (
                totalPlayCount < 0
                || totalDurationMicroseconds <= 0
                || totalDurationMicroseconds
                    > BasisImagePickupSettings.MaxAnimationDurationMicroseconds
            )
            {
                error = "Animation native timing data is invalid.";
                return false;
            }

            long nativeByteCount;
            try
            {
                nativeByteCount = CalculateNativeByteCount(frames.Length, pixels.Length, frameEnds.Length);
            }
            catch (OverflowException)
            {
                error = "Animation native allocation size overflowed.";
                return false;
            }
            if (nativeByteCount <= 0 || nativeByteCount > BasisImagePickupSettings.MaxResidentAnimationNativeBytes)
            {
                error =
                    "Animation native allocation exceeds the configured memory limit.";
                return false;
            }

            bool backgroundHasAlpha = backgroundColor.a < byte.MaxValue;
            bool backgroundHasPartialAlpha =
                backgroundColor.a > 0 && backgroundColor.a < byte.MaxValue;
            if (
                (backgroundHasAlpha && !hasAnyAlpha)
                || (backgroundHasPartialAlpha && !hasPartialAlpha)
                || (hasPartialAlpha && !hasAnyAlpha)
            )
            {
                error = "Animation native alpha flags are inconsistent.";
                return false;
            }
            if (
                !ValidateNativeFrames(
                    canvasWidth,
                    canvasHeight,
                    frames,
                    pixels.Length,
                    frameEnds,
                    totalDurationMicroseconds,
                    out bool actualRequiresPreviousCanvas,
                    out error
                )
            )
            {
                return false;
            }
            if (requiresPreviousCanvas != actualRequiresPreviousCanvas)
            {
                error =
                    "Animation previous-canvas flag is inconsistent with its frames.";
                return false;
            }

            data = new BasisAnimatedImageData(
                canvasWidth,
                canvasHeight,
                totalPlayCount,
                backgroundColor,
                frames,
                pixels,
                frameEnds,
                totalDurationMicroseconds,
                hasAnyAlpha,
                hasPartialAlpha,
                requiresPreviousCanvas
            );
            return true;
        }

        private static bool ValidateNativeFrames(
            int canvasWidth,
            int canvasHeight,
            NativeArray<BasisAnimatedImageFrame> frames,
            int pixelPoolLength,
            NativeArray<long> frameEnds,
            long totalDurationMicroseconds,
            out bool requiresPreviousCanvas,
            out string error
        )
        {
            requiresPreviousCanvas = false;
            error = null;
            long expectedPixelOffset = 0;
            long previousEndTime = 0;
            int frameCount = frames.Length;
            for (int i = 0; i < frameCount; i++)
            {
                BasisAnimatedImageFrame frame = frames[i];
                long frameArea = (long)frame.Width * frame.Height;
                if (
                    frame.Width <= 0
                    || frame.Height <= 0
                    || frame.X < 0
                    || frame.Y < 0
                    || (long)frame.X + frame.Width > canvasWidth
                    || (long)frame.Y + frame.Height > canvasHeight
                    || frameArea <= 0
                    || frameArea != frame.PixelCount
                    || frame.PixelOffset != expectedPixelOffset
                    || frame.PixelOffset < 0
                    || (long)frame.PixelOffset + frame.PixelCount > pixelPoolLength
                )
                {
                    error =
                        $"Animation native frame {i + 1:N0} has invalid bounds or pixel data.";
                    return false;
                }
                if (frame.Blend != BasisAnimationBlend.Source && frame.Blend != BasisAnimationBlend.Over)
                {
                    error =
                        $"Animation native frame {i + 1:N0} has an invalid blend mode.";
                    return false;
                }
                if (
                    frame.Disposal != BasisAnimationDisposal.None
                    && frame.Disposal != BasisAnimationDisposal.Background
                    && frame.Disposal != BasisAnimationDisposal.Previous
                )
                {
                    error =
                        $"Animation native frame {i + 1:N0} has an invalid disposal mode.";
                    return false;
                }
                if (
                    frame.Reserved != 0
                    || frame.DurationMicroseconds
                        < BasisImagePickupSettings.MinAnimationFrameDurationMicroseconds
                    || frame.EndTimeMicroseconds <= previousEndTime
                    || frame.EndTimeMicroseconds > totalDurationMicroseconds
                    || frame.EndTimeMicroseconds - previousEndTime
                        != frame.DurationMicroseconds
                    || frameEnds[i] != frame.EndTimeMicroseconds
                )
                {
                    error =
                        $"Animation native frame {i + 1:N0} has invalid timing data.";
                    return false;
                }

                expectedPixelOffset += frame.PixelCount;
                previousEndTime = frame.EndTimeMicroseconds;
                if (frame.Disposal == BasisAnimationDisposal.Previous)
                    requiresPreviousCanvas = true;
            }

            if (expectedPixelOffset != pixelPoolLength || previousEndTime != totalDurationMicroseconds)
            {
                error = "Animation native frame sequence does not cover its data pool.";
                return false;
            }
            return true;
        }

        private static bool ValidateCanvas(int width, int height, out string error)
        {
            error = null;
            long pixels = (long)width * height;
            if (width <= 0 || height <= 0)
            {
                error = "Animation canvas dimensions must be positive.";
                return false;
            }
            if (
                width > BasisImagePickupSettings.MaxAnimationDimension
                || height > BasisImagePickupSettings.MaxAnimationDimension
                || pixels > BasisImagePickupSettings.MaxAnimationCanvasPixels
            )
            {
                error =
                    $"Animation canvas is {width:N0}×{height:N0} ({pixels:N0} pixels), "
                    + $"above the {BasisImagePickupSettings.MaxAnimationDimension:N0}×"
                    + $"{BasisImagePickupSettings.MaxAnimationDimension:N0} / "
                    + $"{BasisImagePickupSettings.MaxAnimationCanvasPixels:N0}-pixel limit.";
                return false;
            }
            return true;
        }

        private static bool ValidateSourceFrame(
            BasisAnimatedImageFrameSource frame,
            int frameIndex,
            int canvasWidth,
            int canvasHeight,
            ref long decodedPixels,
            ref long totalDuration,
            ref bool hasAnyAlpha,
            ref bool hasPartialAlpha,
            ref bool requiresPrevious,
            out string error
        )
        {
            error = null;
            if (frame.Blend != BasisAnimationBlend.Source && frame.Blend != BasisAnimationBlend.Over)
            {
                error =
                    $"Animation frame {frameIndex + 1:N0} has an invalid blend mode.";
                return false;
            }
            if (
                frame.Disposal != BasisAnimationDisposal.None
                && frame.Disposal != BasisAnimationDisposal.Background
                && frame.Disposal != BasisAnimationDisposal.Previous
            )
            {
                error =
                    $"Animation frame {frameIndex + 1:N0} has an invalid disposal mode.";
                return false;
            }

            RectInt destination = frame.Destination;
            if (
                destination.width <= 0
                || destination.height <= 0
                || destination.x < 0
                || destination.y < 0
                || (long)destination.x + destination.width > canvasWidth
                || (long)destination.y + destination.height > canvasHeight
            )
            {
                error = $"Animation frame {frameIndex + 1:N0} lies outside the canvas.";
                return false;
            }

            int pixelCount = checked(destination.width * destination.height);
            if (frame.Pixels == null || frame.Pixels.Length != pixelCount)
            {
                error =
                    $"Animation frame {frameIndex + 1:N0} pixel data does not match its rectangle.";
                return false;
            }

            decodedPixels = checked(decodedPixels + pixelCount);
            if (decodedPixels > BasisImagePickupSettings.MaxAnimationDecodedFramePixels)
            {
                error =
                    $"Animation decoded pixel count exceeds "
                    + $"{BasisImagePickupSettings.MaxAnimationDecodedFramePixels:N0}.";
                return false;
            }

            long duration = Math.Max(
                frame.DurationMicroseconds,
                BasisImagePickupSettings.MinAnimationFrameDurationMicroseconds
            );
            totalDuration = checked(totalDuration + duration);
            if (totalDuration > BasisImagePickupSettings.MaxAnimationDurationMicroseconds)
            {
                error =
                    "Animation duration exceeds the configured loop-duration limit.";
                return false;
            }

            int framePixelCount = frame.Pixels.Length;
            for (int pixelIndex = 0; pixelIndex < framePixelCount; pixelIndex++)
            {
                byte alpha = frame.Pixels[pixelIndex].a;
                if (alpha < byte.MaxValue)
                    hasAnyAlpha = true;
                if (alpha > 0 && alpha < byte.MaxValue)
                    hasPartialAlpha = true;
            }
            if (frame.Disposal == BasisAnimationDisposal.Previous)
                requiresPrevious = true;
            return true;
        }

        public int FindFrameIndex(long positionInPlayMicroseconds)
        {
            ThrowIfDisposed();
            if (positionInPlayMicroseconds <= 0)
                return 0;
            if (positionInPlayMicroseconds >= TotalDurationMicroseconds)
                return _frames.Length - 1;

            int low = 0;
            int high = _frameEndTimesMicroseconds.Length - 1;
            while (low < high)
            {
                int middle = low + ((high - low) >> 1);
                if (positionInPlayMicroseconds < _frameEndTimesMicroseconds[middle])
                    high = middle;
                else
                    low = middle + 1;
            }
            return low;
        }

        public void GetPlaybackState(
            long elapsedMicroseconds,
            out long playIndex,
            out int frameIndex,
            out bool completed
        )
        {
            ThrowIfDisposed();
            if (elapsedMicroseconds <= 0)
            {
                playIndex = 0;
                frameIndex = 0;
                completed = false;
                return;
            }

            playIndex = elapsedMicroseconds / TotalDurationMicroseconds;
            if (TotalPlayCount > 0 && playIndex >= TotalPlayCount)
            {
                playIndex = TotalPlayCount - 1L;
                frameIndex = _frames.Length - 1;
                completed = true;
                return;
            }

            long position = elapsedMicroseconds % TotalDurationMicroseconds;
            frameIndex = FindFrameIndex(position);
            completed = false;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(BasisAnimatedImageData));
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_frames.IsCreated)
                _frames.Dispose();
            if (_pixels.IsCreated)
                _pixels.Dispose();
            if (_frameEndTimesMicroseconds.IsCreated)
                _frameEndTimesMicroseconds.Dispose();
            if (_nativeByteCount > 0)
            {
                lock (MemoryBudgetLock)
                    _residentNativeBytes = Math.Max(0, _residentNativeBytes - _nativeByteCount);
                _nativeByteCount = 0;
            }
        }
    }
}
