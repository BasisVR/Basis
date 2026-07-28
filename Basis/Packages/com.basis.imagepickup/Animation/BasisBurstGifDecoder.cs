using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Basis.ImagePickup
{
    internal enum BasisGifErrorCode
    {
        None = 0,
        BadSignature,
        HeaderTruncated,
        InvalidCanvas,
        LogicalDescriptorTruncated,
        ColorTableTruncated,
        ExtensionTruncated,
        GraphicControlMalformed,
        ApplicationExtensionMalformed,
        SubBlocksTruncated,
        UnsupportedBlock,
        TooManyFrames,
        FrameDescriptorTruncated,
        FrameOutsideCanvas,
        PixelBudgetExceeded,
        MissingColorTable,
        InvalidCodeSize,
        NoFrames,
        DurationExceeded,
        LzwTruncated,
        LzwUndefinedCode,
        LzwCodeOutOfRange,
        LzwDictionaryInvalid,
        LzwReservedCode,
        LzwOutputOverflow,
        LzwOutputSizeMismatch,
        PaletteIndexOutOfRange,
    }

    internal struct BasisGifFrameScan
    {
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public int PixelOffset;
        public int PixelCount;
        public int PaletteOffset;
        public int PaletteCount;
        public int DataSubBlockOffset;
        public long DurationMicroseconds;
        public long EndTimeMicroseconds;
        public byte MinimumCodeSize;
        public byte TransparentIndex;
        public byte HasTransparency;
        public byte Interlaced;
        public BasisAnimationBlend Blend;
        public BasisAnimationDisposal Disposal;
        public ushort Reserved;
    }

    internal struct BasisGifScanResult
    {
        public BasisGifErrorCode Error;
        public int ErrorOffset;
        public int ErrorFrame;
        public int CanvasWidth;
        public int CanvasHeight;
        public int FrameCount;
        public int PixelCount;
        public int TotalPlayCount;
        public long TotalDurationMicroseconds;
        public Color32 BackgroundColor;
        public byte HasAnyAlpha;
        public byte RequiresPrevious;
    }

    internal sealed class BasisBurstGifDecodeResult : IDisposable
    {
        public bool Ok;
        public string Error;
        public BasisAnimatedImageData Animation;
        public NativeArray<Color32> PosterPixels;
        public long WorkerElapsedTicks;

        /// <summary>Transfers animation ownership to the caller.</summary>
        public BasisAnimatedImageData TakeAnimation()
        {
            BasisAnimatedImageData animation = Animation;
            Animation = null;
            return animation;
        }

        public void Dispose()
        {
            Animation?.Dispose();
            Animation = null;
            if (PosterPixels.IsCreated)
                PosterPixels.Dispose();
        }
    }

    /// <summary>
    /// Two-stage Burst GIF pipeline. Stage one scans and validates the container, producing fixed frame
    /// descriptors. Stage two decodes every frame in parallel into a single native pixel pool and composes
    /// the poster in Burst. The only managed work is source-file acquisition and the final Texture2D upload.
    /// </summary>
    internal sealed class BasisBurstGifDecodeRequest : IDisposable
    {
        private enum RequestState
        {
            Scan,
            Decode,
            Complete,
        }

        private NativeArray<byte> _source;
        private NativeArray<BasisGifFrameScan> _scans;
        private NativeArray<BasisGifScanResult> _scanResult;
        private NativeArray<BasisAnimatedImageFrame> _frames;
        private NativeArray<Color32> _pixels;
        private NativeArray<long> _frameEnds;
        private NativeArray<ushort> _prefixScratch;
        private NativeArray<byte> _suffixScratch;
        private NativeArray<byte> _stackScratch;
        private NativeArray<int> _decodeErrors;
        private NativeArray<Color32> _posterPixels;
        private JobHandle _handle;
        private RequestState _state;
        private BasisBurstGifDecodeResult _result;
        private long _reservedWorkingBytes;
        private bool _resultClaimed;
        private bool _disposed;
        private readonly long _startTimestamp;

        public bool IsCompleted =>
            _state == RequestState.Complete || _handle.IsCompleted;

        public BasisBurstGifDecodeRequest(byte[] source)
        {
            if (source == null || source.Length == 0)
                throw new ArgumentException("GIF source is empty.", nameof(source));
            if (source.Length > BasisImagePickupSettings.MaxAnimationSourceBytes)
                throw new ArgumentOutOfRangeException(nameof(source), "GIF source exceeds the configured limit.");

            _startTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            _reservedWorkingBytes =
                BasisAnimatedImageData.EstimateGifDecodeWorkingBytes(source.Length);
            if (!BasisAnimatedImageData.TryReserveWorkingBytes(_reservedWorkingBytes, out string budgetError))
            {
                _reservedWorkingBytes = 0;
                throw new BasisAnimationMemoryBudgetException(budgetError);
            }
            try
            {
                _source = new NativeArray<byte>(
                    source.Length,
                    Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory
                );
                _source.CopyFrom(source);
                ScheduleScan();
            }
            catch
            {
                _handle.Complete();
                DisposeAllNative();
                throw;
            }
        }

        private void ScheduleScan()
        {
            _scans = new NativeArray<BasisGifFrameScan>(
                BasisImagePickupSettings.MaxAnimationFrames,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory
            );
            _scanResult = new NativeArray<BasisGifScanResult>(1, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _handle = new BasisGifScanJob
            {
                Source = _source,
                Frames = _scans,
                Result = _scanResult,
            }.Schedule();
            JobHandle.ScheduleBatchedJobs();
            _state = RequestState.Scan;
        }

        public bool TryComplete(out BasisBurstGifDecodeResult result)
        {
            result = null;
            if (_state != RequestState.Complete && !_handle.IsCompleted)
                return false;
            Advance(false);
            if (_state != RequestState.Complete)
                return false;
            result = _result;
            if (result?.Ok == true)
                _resultClaimed = true;
            return true;
        }

        public BasisBurstGifDecodeResult Complete()
        {
            while (_state != RequestState.Complete)
                Advance(true);
            if (_result?.Ok == true)
                _resultClaimed = true;
            return _result;
        }

        private void Advance(bool block)
        {
            if (_state == RequestState.Complete)
                return;
            if (!block && !_handle.IsCompleted)
                return;
            _handle.Complete();

            if (_state == RequestState.Scan)
            {
                BasisGifScanResult scan = _scanResult[0];
                if (scan.Error != BasisGifErrorCode.None)
                {
                    FinishFailure(BasisBurstGifDecoder.DescribeError(scan));
                    return;
                }

                try
                {
                    _frames = new NativeArray<BasisAnimatedImageFrame>(
                        scan.FrameCount,
                        Allocator.Persistent,
                        NativeArrayOptions.UninitializedMemory
                    );
                    _pixels = new NativeArray<Color32>(
                        scan.PixelCount,
                        Allocator.Persistent,
                        NativeArrayOptions.UninitializedMemory
                    );
                    _frameEnds = new NativeArray<long>(
                        scan.FrameCount,
                        Allocator.Persistent,
                        NativeArrayOptions.UninitializedMemory
                    );
                    _decodeErrors = new NativeArray<int>(
                        scan.FrameCount,
                        Allocator.Persistent,
                        NativeArrayOptions.ClearMemory
                    );
                    _prefixScratch = new NativeArray<ushort>(
                        checked(scan.FrameCount * 4096),
                        Allocator.Persistent,
                        NativeArrayOptions.UninitializedMemory
                    );
                    _suffixScratch = new NativeArray<byte>(
                        checked(scan.FrameCount * 4096),
                        Allocator.Persistent,
                        NativeArrayOptions.UninitializedMemory
                    );
                    _stackScratch = new NativeArray<byte>(
                        checked(scan.FrameCount * 4097),
                        Allocator.Persistent,
                        NativeArrayOptions.UninitializedMemory
                    );
                    _posterPixels = new NativeArray<Color32>(
                        checked(scan.CanvasWidth * scan.CanvasHeight),
                        Allocator.Persistent,
                        NativeArrayOptions.UninitializedMemory
                    );
                }
                catch (Exception exception)
                {
                    FinishFailure("GIF native allocation failed: " + exception.Message);
                    return;
                }

                var decodeFramesJob = new BasisGifDecodeFramesJob
                {
                    Source = _source,
                    Scans = _scans,
                    Frames = _frames,
                    Pixels = _pixels,
                    FrameEnds = _frameEnds,
                    PrefixScratch = _prefixScratch,
                    SuffixScratch = _suffixScratch,
                    StackScratch = _stackScratch,
                    Errors = _decodeErrors,
                };
                JobHandle decodeHandle = decodeFramesJob.ScheduleParallelByRef(scan.FrameCount, 1, default);
                var fillPosterJob = new BasisGifFillPosterJob
                {
                    Pixels = _posterPixels,
                    Color = scan.BackgroundColor,
                };
                JobHandle clearHandle = fillPosterJob.ScheduleParallelByRef(_posterPixels.Length, 256, default);
                var drawPosterJob = new BasisGifDrawPosterJob
                {
                    CanvasWidth = scan.CanvasWidth,
                    Frames = _frames,
                    FramePixels = _pixels,
                    PosterPixels = _posterPixels,
                };
                JobHandle drawHandle = drawPosterJob.ScheduleParallelByRef(
                    _scans[0].PixelCount,
                    128,
                    JobHandle.CombineDependencies(decodeHandle, clearHandle)
                );

                _handle = drawHandle;
                JobHandle.ScheduleBatchedJobs();
                _state = RequestState.Decode;
                return;
            }

            BasisGifScanResult completedScan = _scanResult[0];
            for (int i = 0; i < completedScan.FrameCount; i++)
            {
                if (_decodeErrors[i] == 0)
                    continue;
                var failed = completedScan;
                failed.Error = (BasisGifErrorCode)_decodeErrors[i];
                failed.ErrorFrame = i;
                FinishFailure(BasisBurstGifDecoder.DescribeError(failed));
                return;
            }

            if (
                !BasisAnimatedImageData.TryAdoptNative(
                    completedScan.CanvasWidth,
                    completedScan.CanvasHeight,
                    completedScan.TotalPlayCount,
                    completedScan.BackgroundColor,
                    _frames,
                    _pixels,
                    _frameEnds,
                    completedScan.TotalDurationMicroseconds,
                    completedScan.HasAnyAlpha != 0,
                    false,
                    completedScan.RequiresPrevious != 0,
                    out BasisAnimatedImageData animation,
                    out string error
                )
            )
            {
                FinishFailure(error);
                return;
            }

            _frames = default;
            _pixels = default;
            _frameEnds = default;
            _result = new BasisBurstGifDecodeResult
            {
                Ok = true,
                Animation = animation,
                PosterPixels = _posterPixels,
                WorkerElapsedTicks =
                    System.Diagnostics.Stopwatch.GetTimestamp() - _startTimestamp,
            };
            _posterPixels = default;
            DisposeTemporary();
            _state = RequestState.Complete;
        }

        private void FinishFailure(string error)
        {
            _result = new BasisBurstGifDecodeResult
            {
                Ok = false,
                Error = error,
                WorkerElapsedTicks =
                    System.Diagnostics.Stopwatch.GetTimestamp() - _startTimestamp,
            };
            DisposeAllNative();
            _state = RequestState.Complete;
        }

        private void DisposeTemporary()
        {
            if (_source.IsCreated)
                _source.Dispose();
            if (_scans.IsCreated)
                _scans.Dispose();
            if (_scanResult.IsCreated)
                _scanResult.Dispose();
            if (_prefixScratch.IsCreated)
                _prefixScratch.Dispose();
            if (_suffixScratch.IsCreated)
                _suffixScratch.Dispose();
            if (_stackScratch.IsCreated)
                _stackScratch.Dispose();
            if (_decodeErrors.IsCreated)
                _decodeErrors.Dispose();
        }

        private void DisposeAllNative()
        {
            DisposeTemporary();
            if (_frames.IsCreated)
                _frames.Dispose();
            if (_pixels.IsCreated)
                _pixels.Dispose();
            if (_frameEnds.IsCreated)
                _frameEnds.Dispose();
            if (_posterPixels.IsCreated)
                _posterPixels.Dispose();
            if (_reservedWorkingBytes > 0)
            {
                BasisAnimatedImageData.ReleaseWorkingBytes(_reservedWorkingBytes);
                _reservedWorkingBytes = 0;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                if (_state != RequestState.Complete)
                    _handle.Complete();
                if (!_resultClaimed)
                    _result?.Dispose();
            }
            finally
            {
                _result = null;
                DisposeAllNative();
            }
        }
    }

    internal static class BasisBurstGifDecoder
    {
        public static BasisBurstGifDecodeRequest Schedule(byte[] source)
        {
            return new BasisBurstGifDecodeRequest(source);
        }

        public static string DescribeError(BasisGifScanResult result)
        {
            string location =
                result.ErrorFrame >= 0 ? $" at frame {result.ErrorFrame + 1:N0}"
                : result.ErrorOffset > 0 ? $" near byte {result.ErrorOffset:N0}"
                : string.Empty;
            return result.Error switch
            {
                BasisGifErrorCode.BadSignature => "Not a GIF (bad signature).",
                BasisGifErrorCode.HeaderTruncated => "GIF header is truncated.",
                BasisGifErrorCode.InvalidCanvas =>
                    "GIF canvas dimensions exceed the configured limits.",
                BasisGifErrorCode.LogicalDescriptorTruncated =>
                    "GIF logical screen descriptor is truncated.",
                BasisGifErrorCode.ColorTableTruncated => "GIF color table is truncated"
                    + location
                    + ".",
                BasisGifErrorCode.ExtensionTruncated => "GIF extension is truncated"
                    + location
                    + ".",
                BasisGifErrorCode.GraphicControlMalformed =>
                    "GIF graphic control extension is malformed" + location + ".",
                BasisGifErrorCode.ApplicationExtensionMalformed =>
                    "GIF application extension is malformed" + location + ".",
                BasisGifErrorCode.SubBlocksTruncated =>
                    "GIF data sub-blocks are truncated" + location + ".",
                BasisGifErrorCode.UnsupportedBlock =>
                    "GIF contains an unsupported block" + location + ".",
                BasisGifErrorCode.TooManyFrames =>
                    $"GIF exceeds {BasisImagePickupSettings.MaxAnimationFrames:N0} frames.",
                BasisGifErrorCode.FrameDescriptorTruncated =>
                    "GIF frame descriptor is truncated" + location + ".",
                BasisGifErrorCode.FrameOutsideCanvas =>
                    "GIF frame lies outside the canvas" + location + ".",
                BasisGifErrorCode.PixelBudgetExceeded =>
                    "GIF exceeds the decoded frame-pixel budget" + location + ".",
                BasisGifErrorCode.MissingColorTable => "GIF frame has no color table"
                    + location
                    + ".",
                BasisGifErrorCode.InvalidCodeSize => "GIF LZW code size is invalid"
                    + location
                    + ".",
                BasisGifErrorCode.NoFrames => "GIF contains no image frames.",
                BasisGifErrorCode.DurationExceeded =>
                    "GIF loop duration exceeds the configured limit" + location + ".",
                BasisGifErrorCode.LzwTruncated => "GIF LZW stream is truncated"
                    + location
                    + ".",
                BasisGifErrorCode.LzwUndefinedCode =>
                    "GIF LZW stream references an undefined code" + location + ".",
                BasisGifErrorCode.LzwCodeOutOfRange => "GIF LZW code is out of range"
                    + location
                    + ".",
                BasisGifErrorCode.LzwDictionaryInvalid =>
                    "GIF LZW dictionary is invalid" + location + ".",
                BasisGifErrorCode.LzwReservedCode =>
                    "GIF LZW dictionary references a reserved code" + location + ".",
                BasisGifErrorCode.LzwOutputOverflow =>
                    "GIF LZW output exceeds the frame size" + location + ".",
                BasisGifErrorCode.LzwOutputSizeMismatch =>
                    "GIF LZW output size does not match the frame" + location + ".",
                BasisGifErrorCode.PaletteIndexOutOfRange =>
                    "GIF palette index is out of range" + location + ".",
                _ => "GIF decoding failed" + location + ".",
            };
        }
    }

    [BurstCompile]
    internal struct BasisGifScanJob : IJob
    {
        [ReadOnly]
        public NativeArray<byte> Source;

        [WriteOnly]
        public NativeArray<BasisGifFrameScan> Frames;
        public NativeArray<BasisGifScanResult> Result;

        public void Execute()
        {
            var result = new BasisGifScanResult { ErrorFrame = -1 };
            int offset = 0;
            if (Source.Length < 13)
            {
                Fail(ref result, BasisGifErrorCode.HeaderTruncated, offset);
                Result[0] = result;
                return;
            }
            if (
                Source[0] != (byte)'G'
                || Source[1] != (byte)'I'
                || Source[2] != (byte)'F'
                || Source[3] != (byte)'8'
                || (Source[4] != (byte)'7' && Source[4] != (byte)'9')
                || Source[5] != (byte)'a'
            )
            {
                Fail(ref result, BasisGifErrorCode.BadSignature, 0);
                Result[0] = result;
                return;
            }

            offset = 6;
            int canvasWidth = ReadUInt16(ref offset, ref result);
            int canvasHeight = ReadUInt16(ref offset, ref result);
            if (result.Error != BasisGifErrorCode.None)
            {
                Result[0] = result;
                return;
            }
            if (
                canvasWidth <= 0
                || canvasHeight <= 0
                || canvasWidth > BasisImagePickupSettings.MaxAnimationDimension
                || canvasHeight > BasisImagePickupSettings.MaxAnimationDimension
                || (long)canvasWidth * canvasHeight
                    > BasisImagePickupSettings.MaxAnimationCanvasPixels
            )
            {
                Fail(ref result, BasisGifErrorCode.InvalidCanvas, offset);
                Result[0] = result;
                return;
            }

            if (
                !TryReadByte(ref offset, out byte logicalPacked)
                || !TryReadByte(ref offset, out byte backgroundIndex)
                || !TryReadByte(ref offset, out _)
            )
            {
                Fail(ref result, BasisGifErrorCode.LogicalDescriptorTruncated, offset);
                Result[0] = result;
                return;
            }

            int globalPaletteOffset = -1;
            int globalPaletteCount = 0;
            if ((logicalPacked & 0x80) != 0)
            {
                globalPaletteCount = 1 << ((logicalPacked & 0x07) + 1);
                globalPaletteOffset = offset;
                if (!Skip(ref offset, globalPaletteCount * 3))
                {
                    Fail(ref result, BasisGifErrorCode.ColorTableTruncated, offset);
                    Result[0] = result;
                    return;
                }
            }

            Color32 background = new Color32(0, 0, 0, 0);
            if (globalPaletteOffset >= 0 && backgroundIndex < globalPaletteCount)
                background = ReadPaletteColor(globalPaletteOffset, backgroundIndex);

            int frameCount = 0;
            int pixelOffset = 0;
            long totalDuration = 0;
            int rawLoopCount = -1;
            int pendingDelayHundredths = 0;
            byte pendingTransparentIndex = 0;
            byte pendingHasTransparency = 0;
            BasisAnimationDisposal pendingDisposal = BasisAnimationDisposal.None;
            byte hasAnyAlpha = background.a < byte.MaxValue ? (byte)1 : (byte)0;
            byte requiresPrevious = 0;

            while (offset < Source.Length)
            {
                int blockOffset = offset;
                byte blockType = Source[offset++];
                if (blockType == 0x3B)
                    break;

                if (blockType == 0x21)
                {
                    if (!TryReadByte(ref offset, out byte extensionLabel))
                    {
                        Fail(ref result, BasisGifErrorCode.ExtensionTruncated, offset);
                        break;
                    }

                    if (extensionLabel == 0xF9)
                    {
                        if (
                            !TryReadByte(ref offset, out byte blockSize)
                            || blockSize != 4
                            || !TryReadByte(ref offset, out byte packed)
                            || !TryReadUInt16(ref offset, out pendingDelayHundredths)
                            || !TryReadByte(ref offset, out pendingTransparentIndex)
                            || !TryReadByte(ref offset, out byte terminator)
                            || terminator != 0
                        )
                        {
                            Fail(ref result, BasisGifErrorCode.GraphicControlMalformed, offset);
                            break;
                        }
                        int disposal = (packed >> 2) & 7;
                        pendingDisposal =
                            disposal == 2 ? BasisAnimationDisposal.Background
                            : disposal == 3 ? BasisAnimationDisposal.Previous
                            : BasisAnimationDisposal.None;
                        pendingHasTransparency = (byte)((packed & 1) != 0 ? 1 : 0);
                        continue;
                    }

                    if (extensionLabel == 0xFF)
                    {
                        if (
                            !TryReadByte(ref offset, out byte identifierLength)
                            || offset + identifierLength > Source.Length
                        )
                        {
                            Fail(ref result, BasisGifErrorCode.ApplicationExtensionMalformed, offset);
                            break;
                        }
                        bool loopExtension = IsLoopIdentifier(offset, identifierLength);
                        offset += identifierLength;
                        if (!ReadApplicationSubBlocks(ref offset, loopExtension, ref rawLoopCount))
                        {
                            Fail(ref result, BasisGifErrorCode.SubBlocksTruncated, offset);
                            break;
                        }
                        continue;
                    }

                    if (!SkipSubBlocks(ref offset))
                    {
                        Fail(ref result, BasisGifErrorCode.SubBlocksTruncated, offset);
                        break;
                    }
                    if (extensionLabel == 0x01)
                    {
                        // A Graphic Control Extension applies to the next graphic-rendering
                        // block. Plain Text is such a block even though this decoder skips it.
                        pendingDelayHundredths = 0;
                        pendingTransparentIndex = 0;
                        pendingHasTransparency = 0;
                        pendingDisposal = BasisAnimationDisposal.None;
                    }
                    continue;
                }

                if (blockType != 0x2C)
                {
                    Fail(ref result, BasisGifErrorCode.UnsupportedBlock, blockOffset);
                    break;
                }
                if (frameCount >= Frames.Length)
                {
                    result.ErrorFrame = frameCount;
                    Fail(ref result, BasisGifErrorCode.TooManyFrames, blockOffset);
                    break;
                }

                if (
                    !TryReadUInt16(ref offset, out int left)
                    || !TryReadUInt16(ref offset, out int top)
                    || !TryReadUInt16(ref offset, out int width)
                    || !TryReadUInt16(ref offset, out int height)
                    || !TryReadByte(ref offset, out byte imagePacked)
                )
                {
                    result.ErrorFrame = frameCount;
                    Fail(ref result, BasisGifErrorCode.FrameDescriptorTruncated, offset);
                    break;
                }
                if (
                    width <= 0
                    || height <= 0
                    || left < 0
                    || top < 0
                    || left + width > canvasWidth
                    || top + height > canvasHeight
                )
                {
                    result.ErrorFrame = frameCount;
                    Fail(ref result, BasisGifErrorCode.FrameOutsideCanvas, offset);
                    break;
                }

                int framePixels = width * height;
                if ((long)pixelOffset + framePixels > BasisImagePickupSettings.MaxAnimationDecodedFramePixels)
                {
                    result.ErrorFrame = frameCount;
                    Fail(ref result, BasisGifErrorCode.PixelBudgetExceeded, offset);
                    break;
                }

                int paletteOffset = globalPaletteOffset;
                int paletteCount = globalPaletteCount;
                if ((imagePacked & 0x80) != 0)
                {
                    paletteCount = 1 << ((imagePacked & 7) + 1);
                    paletteOffset = offset;
                    if (!Skip(ref offset, paletteCount * 3))
                    {
                        result.ErrorFrame = frameCount;
                        Fail(ref result, BasisGifErrorCode.ColorTableTruncated, offset);
                        break;
                    }
                }
                if (paletteOffset < 0 || paletteCount <= 0)
                {
                    result.ErrorFrame = frameCount;
                    Fail(ref result, BasisGifErrorCode.MissingColorTable, offset);
                    break;
                }

                if (!TryReadByte(ref offset, out byte minimumCodeSize) || minimumCodeSize < 2 || minimumCodeSize > 8)
                {
                    result.ErrorFrame = frameCount;
                    Fail(ref result, BasisGifErrorCode.InvalidCodeSize, offset);
                    break;
                }
                int dataOffset = offset;
                if (!SkipSubBlocks(ref offset))
                {
                    result.ErrorFrame = frameCount;
                    Fail(ref result, BasisGifErrorCode.SubBlocksTruncated, offset);
                    break;
                }

                long rawDuration = pendingDelayHundredths * 10000L;
                long duration =
                    rawDuration
                    < BasisImagePickupSettings.MinAnimationFrameDurationMicroseconds
                        ? BasisImagePickupSettings.MinAnimationFrameDurationMicroseconds
                        : rawDuration;
                totalDuration += duration;
                if (totalDuration > BasisImagePickupSettings.MaxAnimationDurationMicroseconds)
                {
                    result.ErrorFrame = frameCount;
                    Fail(ref result, BasisGifErrorCode.DurationExceeded, offset);
                    break;
                }

                Frames[frameCount] = new BasisGifFrameScan
                {
                    X = left,
                    Y = canvasHeight - top - height,
                    Width = width,
                    Height = height,
                    PixelOffset = pixelOffset,
                    PixelCount = framePixels,
                    PaletteOffset = paletteOffset,
                    PaletteCount = paletteCount,
                    DataSubBlockOffset = dataOffset,
                    DurationMicroseconds = duration,
                    EndTimeMicroseconds = totalDuration,
                    MinimumCodeSize = minimumCodeSize,
                    TransparentIndex = pendingTransparentIndex,
                    HasTransparency = pendingHasTransparency,
                    Interlaced = (byte)((imagePacked & 0x40) != 0 ? 1 : 0),
                    Blend =
                        pendingHasTransparency != 0
                            ? BasisAnimationBlend.Over
                            : BasisAnimationBlend.Source,
                    Disposal = pendingDisposal,
                };

                if (pendingHasTransparency != 0)
                {
                    hasAnyAlpha = 1;
                    if (frameCount == 0 && pendingTransparentIndex == backgroundIndex)
                        background = new Color32(0, 0, 0, 0);
                }
                if (pendingDisposal == BasisAnimationDisposal.Previous)
                    requiresPrevious = 1;
                frameCount++;
                pixelOffset += framePixels;
                pendingDelayHundredths = 0;
                pendingTransparentIndex = 0;
                pendingHasTransparency = 0;
                pendingDisposal = BasisAnimationDisposal.None;
            }

            if (result.Error == BasisGifErrorCode.None && frameCount == 0)
                Fail(ref result, BasisGifErrorCode.NoFrames, offset);

            result.CanvasWidth = canvasWidth;
            result.CanvasHeight = canvasHeight;
            result.FrameCount = frameCount;
            result.PixelCount = pixelOffset;
            result.TotalDurationMicroseconds = totalDuration;
            result.TotalPlayCount =
                rawLoopCount < 0 ? 1
                : rawLoopCount == 0 ? 0
                : rawLoopCount + 1;
            result.BackgroundColor = background;
            result.HasAnyAlpha = hasAnyAlpha;
            result.RequiresPrevious = requiresPrevious;
            Result[0] = result;
        }

        private bool TryReadByte(ref int offset, out byte value)
        {
            if ((uint)offset >= (uint)Source.Length)
            {
                value = 0;
                return false;
            }
            value = Source[offset++];
            return true;
        }

        private bool TryReadUInt16(ref int offset, out int value)
        {
            if (offset < 0 || offset + 2 > Source.Length)
            {
                value = 0;
                return false;
            }
            value = Source[offset] | (Source[offset + 1] << 8);
            offset += 2;
            return true;
        }

        private int ReadUInt16(ref int offset, ref BasisGifScanResult result)
        {
            if (!TryReadUInt16(ref offset, out int value))
                Fail(ref result, BasisGifErrorCode.LogicalDescriptorTruncated, offset);
            return value;
        }

        private bool Skip(ref int offset, int count)
        {
            if (count < 0 || offset < 0 || offset + count > Source.Length)
                return false;
            offset += count;
            return true;
        }

        private bool SkipSubBlocks(ref int offset)
        {
            while (true)
            {
                if (!TryReadByte(ref offset, out byte size))
                    return false;
                if (size == 0)
                    return true;
                if (!Skip(ref offset, size))
                    return false;
            }
        }

        private bool ReadApplicationSubBlocks(ref int offset, bool loopExtension, ref int rawLoopCount)
        {
            bool first = true;
            while (true)
            {
                if (!TryReadByte(ref offset, out byte size))
                    return false;
                if (size == 0)
                    return true;
                if (offset + size > Source.Length)
                    return false;
                if (first && loopExtension && size >= 3 && Source[offset] == 1)
                    rawLoopCount = Source[offset + 1] | (Source[offset + 2] << 8);
                first = false;
                offset += size;
            }
        }

        private bool IsLoopIdentifier(int offset, int length)
        {
            if (length < 11 || offset < 0 || offset + 11 > Source.Length)
                return false;
            ulong first = 0;
            uint tail = 0;
            for (int i = 0; i < 8; i++)
                first |= (ulong)Source[offset + i] << (i * 8);
            for (int i = 0; i < 3; i++)
                tail |= (uint)Source[offset + 8 + i] << (i * 8);
            return (first == 0x455041435354454Eul && tail == 0x00302E32u)
                || (first == 0x535458454D494E41ul && tail == 0x00302E31u);
        }

        private Color32 ReadPaletteColor(int paletteOffset, int index)
        {
            int offset = paletteOffset + index * 3;
            return new Color32(Source[offset], Source[offset + 1], Source[offset + 2], byte.MaxValue);
        }

        private static void Fail(ref BasisGifScanResult result, BasisGifErrorCode error, int offset)
        {
            if (result.Error != BasisGifErrorCode.None)
                return;
            result.Error = error;
            result.ErrorOffset = offset;
        }
    }

    [BurstCompile]
    internal struct BasisGifDecodeFramesJob : IJobFor
    {
        [ReadOnly]
        public NativeArray<byte> Source;

        [ReadOnly]
        public NativeArray<BasisGifFrameScan> Scans;

        [NativeDisableParallelForRestriction]
        public NativeArray<BasisAnimatedImageFrame> Frames;

        [NativeDisableParallelForRestriction]
        public NativeArray<Color32> Pixels;

        [NativeDisableParallelForRestriction]
        public NativeArray<long> FrameEnds;

        [NativeDisableParallelForRestriction]
        public NativeArray<ushort> PrefixScratch;

        [NativeDisableParallelForRestriction]
        public NativeArray<byte> SuffixScratch;

        [NativeDisableParallelForRestriction]
        public NativeArray<byte> StackScratch;

        [NativeDisableParallelForRestriction]
        public NativeArray<int> Errors;

        public void Execute(int frameIndex)
        {
            BasisGifFrameScan scan = Scans[frameIndex];
            Frames[frameIndex] = new BasisAnimatedImageFrame
            {
                X = scan.X,
                Y = scan.Y,
                Width = scan.Width,
                Height = scan.Height,
                PixelOffset = scan.PixelOffset,
                PixelCount = scan.PixelCount,
                DurationMicroseconds = scan.DurationMicroseconds,
                EndTimeMicroseconds = scan.EndTimeMicroseconds,
                Blend = scan.Blend,
                Disposal = scan.Disposal,
            };
            FrameEnds[frameIndex] = scan.EndTimeMicroseconds;

            int dictionaryBase = frameIndex * 4096;
            int stackBase = frameIndex * 4097;
            int clearCode = 1 << scan.MinimumCodeSize;
            int endCode = clearCode + 1;
            int nextCode = endCode + 1;
            int codeSize = scan.MinimumCodeSize + 1;
            int oldCode = -1;
            byte firstCharacter = 0;
            int outputIndex = 0;
            bool sawEndCode = false;
            for (int i = 0; i < clearCode; i++)
                SuffixScratch[dictionaryBase + i] = (byte)i;

            var reader = new BasisGifSubBlockBitReader
            {
                Source = Source,
                Offset = scan.DataSubBlockOffset,
            };

            while (reader.TryReadCode(codeSize, out int code))
            {
                if (code == clearCode)
                {
                    codeSize = scan.MinimumCodeSize + 1;
                    nextCode = endCode + 1;
                    oldCode = -1;
                    continue;
                }
                if (code == endCode)
                {
                    sawEndCode = true;
                    break;
                }

                int inputCode = code;
                int stackCount = 0;
                if (code == nextCode)
                {
                    if (oldCode < 0)
                    {
                        Errors[frameIndex] = (int)BasisGifErrorCode.LzwUndefinedCode;
                        return;
                    }
                    StackScratch[stackBase + stackCount++] = firstCharacter;
                    code = oldCode;
                }
                else if (code > nextCode)
                {
                    Errors[frameIndex] = (int)BasisGifErrorCode.LzwCodeOutOfRange;
                    return;
                }

                while (code >= endCode + 1)
                {
                    if (code >= nextCode || stackCount >= 4097)
                    {
                        Errors[frameIndex] = (int)
                            BasisGifErrorCode.LzwDictionaryInvalid;
                        return;
                    }
                    StackScratch[stackBase + stackCount++] = SuffixScratch[
                        dictionaryBase + code
                    ];
                    code = PrefixScratch[dictionaryBase + code];
                }
                if (code >= clearCode || stackCount >= 4097)
                {
                    Errors[frameIndex] = (int)BasisGifErrorCode.LzwReservedCode;
                    return;
                }

                firstCharacter = SuffixScratch[dictionaryBase + code];
                StackScratch[stackBase + stackCount++] = firstCharacter;
                while (stackCount > 0)
                {
                    if (outputIndex >= scan.PixelCount)
                    {
                        Errors[frameIndex] = (int)BasisGifErrorCode.LzwOutputOverflow;
                        return;
                    }
                    byte paletteIndex = StackScratch[stackBase + --stackCount];
                    if (paletteIndex >= scan.PaletteCount)
                    {
                        Errors[frameIndex] = (int)
                            BasisGifErrorCode.PaletteIndexOutOfRange;
                        return;
                    }
                    WritePixel(scan, outputIndex++, paletteIndex);
                }

                if (oldCode >= 0 && nextCode < 4096)
                {
                    PrefixScratch[dictionaryBase + nextCode] = (ushort)oldCode;
                    SuffixScratch[dictionaryBase + nextCode] = firstCharacter;
                    nextCode++;
                    if (nextCode == (1 << codeSize) && codeSize < 12)
                        codeSize++;
                }
                oldCode = inputCode;
            }

            if (reader.Truncated || !sawEndCode)
            {
                Errors[frameIndex] = (int)BasisGifErrorCode.LzwTruncated;
                return;
            }
            if (outputIndex != scan.PixelCount)
                Errors[frameIndex] = (int)BasisGifErrorCode.LzwOutputSizeMismatch;
        }

        private void WritePixel(BasisGifFrameScan scan, int streamIndex, byte paletteIndex)
        {
            int streamRow = streamIndex / scan.Width;
            int x = streamIndex - streamRow * scan.Width;
            int gifRow =
                scan.Interlaced != 0
                    ? InterlacedRow(streamRow, scan.Height)
                    : streamRow;
            int unityRow = scan.Height - 1 - gifRow;
            int destination = scan.PixelOffset + unityRow * scan.Width + x;

            if (scan.HasTransparency != 0 && paletteIndex == scan.TransparentIndex)
            {
                Pixels[destination] = new Color32(0, 0, 0, 0);
                return;
            }

            int paletteOffset = scan.PaletteOffset + paletteIndex * 3;
            Pixels[destination] = new Color32(
                Source[paletteOffset],
                Source[paletteOffset + 1],
                Source[paletteOffset + 2],
                byte.MaxValue
            );
        }

        private static int InterlacedRow(int streamRow, int height)
        {
            int count0 = (height + 7) / 8;
            if (streamRow < count0)
                return streamRow * 8;
            streamRow -= count0;

            int count1 = height > 4 ? (height - 4 + 7) / 8 : 0;
            if (streamRow < count1)
                return 4 + streamRow * 8;
            streamRow -= count1;

            int count2 = height > 2 ? (height - 2 + 3) / 4 : 0;
            if (streamRow < count2)
                return 2 + streamRow * 4;
            streamRow -= count2;
            return 1 + streamRow * 2;
        }
    }

    internal struct BasisGifSubBlockBitReader
    {
        [ReadOnly]
        public NativeArray<byte> Source;
        public int Offset;
        public int RemainingInBlock;
        public ulong Bits;
        public int BitCount;
        public bool Ended;
        public bool Truncated;

        public bool TryReadCode(int bitWidth, out int code)
        {
            while (BitCount < bitWidth)
            {
                if (!TryReadDataByte(out byte value))
                {
                    code = 0;
                    return false;
                }
                Bits |= (ulong)value << BitCount;
                BitCount += 8;
            }
            code = (int)(Bits & ((1u << bitWidth) - 1u));
            Bits >>= bitWidth;
            BitCount -= bitWidth;
            return true;
        }

        private bool TryReadDataByte(out byte value)
        {
            value = 0;
            if (Ended)
                return false;
            if (RemainingInBlock == 0)
            {
                if ((uint)Offset >= (uint)Source.Length)
                {
                    Truncated = true;
                    return false;
                }
                RemainingInBlock = Source[Offset++];
                if (RemainingInBlock == 0)
                {
                    Ended = true;
                    return false;
                }
            }
            if ((uint)Offset >= (uint)Source.Length)
            {
                Truncated = true;
                return false;
            }
            value = Source[Offset++];
            RemainingInBlock--;
            return true;
        }
    }

    [BurstCompile]
    internal struct BasisGifFillPosterJob : IJobFor
    {
        [WriteOnly]
        public NativeArray<Color32> Pixels;
        public Color32 Color;

        public void Execute(int index)
        {
            Pixels[index] = Color;
        }
    }

    [BurstCompile]
    internal struct BasisGifDrawPosterJob : IJobFor
    {
        public int CanvasWidth;

        [ReadOnly]
        public NativeArray<BasisAnimatedImageFrame> Frames;

        [ReadOnly]
        public NativeArray<Color32> FramePixels;

        [NativeDisableParallelForRestriction]
        public NativeArray<Color32> PosterPixels;

        public void Execute(int index)
        {
            BasisAnimatedImageFrame frame = Frames[0];
            if (index >= frame.PixelCount)
                return;
            int x = index % frame.Width;
            int y = index / frame.Width;
            int destination = (frame.Y + y) * CanvasWidth + frame.X + x;
            Color32 source = FramePixels[frame.PixelOffset + index];
            if (frame.Blend == BasisAnimationBlend.Source || source.a == byte.MaxValue)
            {
                PosterPixels[destination] = source;
                return;
            }
            if (source.a == 0)
                return;
            PosterPixels[destination] = BlendStraightOver(source, PosterPixels[destination]);
        }

        private static Color32 BlendStraightOver(Color32 source, Color32 destination)
        {
            int sourceAlpha = source.a;
            int inverse = byte.MaxValue - sourceAlpha;
            int outputAlpha =
                sourceAlpha + (destination.a * inverse + 127) / byte.MaxValue;
            if (outputAlpha <= 0)
                return default;
            int sourceWeight = sourceAlpha * byte.MaxValue;
            int destinationWeight = destination.a * inverse;
            int denominator = outputAlpha * byte.MaxValue;
            return new Color32(
                BlendChannel(source.r, destination.r, sourceWeight, destinationWeight, denominator),
                BlendChannel(source.g, destination.g, sourceWeight, destinationWeight, denominator),
                BlendChannel(source.b, destination.b, sourceWeight, destinationWeight, denominator),
                (byte)outputAlpha
            );
        }

        private static byte BlendChannel(int source, int destination, int sw, int dw, int denominator)
        {
            return (byte)
                math.min(255, (source * sw + destination * dw + denominator / 2) / denominator);
        }
    }
}
