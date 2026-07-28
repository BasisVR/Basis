using System;
using System.IO;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Basis.ImagePickup
{
    internal sealed class BasisAnimationMemoryBudgetException
        : InvalidOperationException
    {
        public BasisAnimationMemoryBudgetException(string message)
            : base(message) { }
    }

    internal enum BasisAnimationDecodeTrust : byte
    {
        UntrustedRemote = 0,
        TrustedLocal = 1,
    }

    internal enum BasisAnimationCodecError
    {
        None = 0,
        InvalidInput,
        OutputTooLarge,
        OutputOverflow,
        InvalidMagic,
        UnsupportedVersion,
        Truncated,
        InvalidLength,
        InvalidHeader,
        InvalidFrame,
        InvalidPixelRange,
        InvalidTiming,
        InvalidLz4Offset,
        InvalidLz4Token,
    }

    internal sealed class BasisNativeAnimationPayload : IDisposable
    {
        private static readonly object MemoryBudgetLock = new();
        private static long _allocatedBytes;
        private static long _reservedBytes;

        private NativeArray<byte> _bytes;
        private int _allocatedByteCount;
        private bool _disposed;

        public int Length { get; }
        internal int AllocatedBytes => IsCreated ? _allocatedByteCount : 0;
        internal static long TotalAllocatedBytes
        {
            get
            {
                lock (MemoryBudgetLock)
                    return _allocatedBytes;
            }
        }
        public bool IsCreated => !_disposed && _bytes.IsCreated;
        internal NativeArray<byte> Bytes => _bytes;

        internal BasisNativeAnimationPayload(NativeArray<byte> bytes, int length, bool consumeReservation)
        {
            if (!bytes.IsCreated || length <= 0 || length > bytes.Length)
                throw new ArgumentException("Animation payload is invalid.");
            _bytes = bytes;
            Length = length;
            _allocatedByteCount = bytes.IsCreated ? bytes.Length : 0;
            lock (MemoryBudgetLock)
            {
                if (consumeReservation)
                    _reservedBytes = Math.Max(0, _reservedBytes - _allocatedByteCount);
                _allocatedBytes = checked(_allocatedBytes + _allocatedByteCount);
            }
        }

        internal static bool TryReserveBytes(int bytes, out string error)
        {
            error = null;
            if (bytes <= 0)
            {
                error = "Animation payload reservation is invalid.";
                return false;
            }
            lock (MemoryBudgetLock)
            {
                if (
                    !BasisAnimatedImageData.FitsMemoryBudget(
                        _allocatedBytes,
                        _reservedBytes,
                        bytes,
                        BasisImagePickupSettings.MaxResidentAnimationPayloadBytes
                    )
                )
                {
                    error =
                        $"Animation payload memory would exceed "
                        + $"{BasisImagePickupSettings.MaxResidentAnimationPayloadBytes / (1024L * 1024L):N0} MiB "
                        + $"({_allocatedBytes / (1024L * 1024L):N0} MiB resident, "
                        + $"{_reservedBytes / (1024L * 1024L):N0} MiB reserved, "
                        + $"{bytes / (1024L * 1024L):N0} MiB requested).";
                    return false;
                }
                _reservedBytes = checked(_reservedBytes + bytes);
                return true;
            }
        }

        internal static void ReleaseReservation(int bytes)
        {
            if (bytes <= 0)
                return;
            lock (MemoryBudgetLock)
                _reservedBytes = Math.Max(0, _reservedBytes - bytes);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_bytes.IsCreated)
                _bytes.Dispose();
            if (_allocatedByteCount > 0)
            {
                lock (MemoryBudgetLock)
                    _allocatedBytes = Math.Max(0, _allocatedBytes - _allocatedByteCount);
                _allocatedByteCount = 0;
            }
        }
    }

    internal sealed class BasisBurstAnimationEncodeResult
    {
        public bool Ok;
        public string Error;
        public BasisNativeAnimationPayload Payload;
        public long WorkerElapsedTicks;

        public BasisNativeAnimationPayload TakePayload()
        {
            BasisNativeAnimationPayload payload = Payload;
            Payload = null;
            return payload;
        }
    }

    internal sealed class BasisBurstAnimationDecodeResult
    {
        public bool Ok;
        public string Error;
        public BasisAnimatedImageData Animation;
        public long WorkerElapsedTicks;

        public BasisAnimatedImageData TakeAnimation()
        {
            BasisAnimatedImageData animation = Animation;
            Animation = null;
            return animation;
        }
    }

    internal sealed class BasisBurstAnimationEncodeRequest : IDisposable
    {
        private NativeArray<byte> _raw;
        private NativeArray<byte> _output;
        private NativeArray<int> _hash;
        private NativeArray<int> _result;
        private JobHandle _handle;
        private BasisBurstAnimationEncodeResult _completed;
        private long _reservedWorkingBytes;
        private bool _resultClaimed;
        private bool _disposed;
        private readonly long _startTimestamp;

        public bool IsCompleted => _completed != null || _handle.IsCompleted;

        public BasisBurstAnimationEncodeRequest(BasisAnimatedImageData data)
        {
			if (data == null || !data.IsCreated)
                throw new ArgumentNullException(nameof(data));

            _startTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            JobHandle cleanupHandle = default;
            try
            {
                int rawLength = BasisBurstAnimationCodec.CalculateRawLength(data);
                int maximumOutput = math.min(
                    BasisImagePickupSettings.MaxAnimationNetworkBytes,
                    BasisBurstAnimationCodec.Lz4MaximumLength(rawLength)
                        + BasisBurstAnimationCodec.OuterHeaderBytes
                );
                _reservedWorkingBytes = checked(
                    (long)rawLength
                    + maximumOutput
                    + (long)BasisBurstAnimationCodec.Lz4HashSize * sizeof(int)
                    + 2L * sizeof(int)
                );
                if (!BasisAnimatedImageData.TryReserveWorkingBytes(_reservedWorkingBytes, out string budgetError))
                {
                    _reservedWorkingBytes = 0;
                    throw new BasisAnimationMemoryBudgetException(budgetError);
                }
                _raw = new NativeArray<byte>(rawLength, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                _output = new NativeArray<byte>(
                    maximumOutput,
                    Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory
                );
                _hash = new NativeArray<int>(
                    BasisBurstAnimationCodec.Lz4HashSize,
                    Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory
                );
                _result = new NativeArray<int>(2, Allocator.Persistent, NativeArrayOptions.ClearMemory);

                JobHandle header = new BasisAnimationPackHeaderJob
                {
                    CanvasWidth = data.CanvasWidth,
                    CanvasHeight = data.CanvasHeight,
                    TotalPlayCount = data.TotalPlayCount,
                    BackgroundColor = data.BackgroundColor,
                    TotalDurationMicroseconds = data.TotalDurationMicroseconds,
                    HasAnyAlpha = data.HasAnyAlpha ? (byte)1 : (byte)0,
                    HasPartialAlpha = data.HasPartialAlpha ? (byte)1 : (byte)0,
                    RequiresPrevious = data.RequiresPreviousCanvas ? (byte)1 : (byte)0,
                    Frames = data.FramesNative,
                    Raw = _raw,
                }.Schedule();
                cleanupHandle = header;
                var pixelPackJob = new BasisAnimationPackPixelsJob
                {
                    DestinationOffset =
                        BasisBurstAnimationCodec.BodyHeaderBytes
                        + data.FrameCount * BasisBurstAnimationCodec.FrameRecordBytes,
                    Pixels = data.PixelsNative,
                    Raw = _raw,
                };
                JobHandle pixels = pixelPackJob.ScheduleParallelByRef(data.PixelsNative.Length, 512, header);
                cleanupHandle = pixels;
                var clearHashJob = new BasisLz4ClearHashJob { Hash = _hash };
                JobHandle clearHash = clearHashJob.ScheduleParallelByRef(_hash.Length, 512, default);
                cleanupHandle = JobHandle.CombineDependencies(pixels, clearHash);
                _handle = new BasisLz4CompressJob
                {
                    Raw = _raw,
                    Output = _output,
                    Hash = _hash,
                    Result = _result,
                }.Schedule(cleanupHandle);
                cleanupHandle = _handle;
                JobHandle.ScheduleBatchedJobs();
            }
            catch
            {
                cleanupHandle.Complete();
                DisposeNative(true);
                throw;
            }
        }

        public bool TryComplete(out BasisBurstAnimationEncodeResult result)
        {
            result = null;
            if (_completed == null && !_handle.IsCompleted)
                return false;
            result = Complete();
            return true;
        }

        public BasisBurstAnimationEncodeResult Complete()
        {
            if (_completed != null)
            {
                if (_completed.Ok)
                    _resultClaimed = true;
                return _completed;
            }
            _handle.Complete();
            BasisAnimationCodecError error = (BasisAnimationCodecError)_result[1];
            if (error != BasisAnimationCodecError.None)
            {
                _completed = new BasisBurstAnimationEncodeResult
                {
                    Ok = false,
                    Error = BasisBurstAnimationCodec.DescribeError(error),
                    WorkerElapsedTicks =
                        System.Diagnostics.Stopwatch.GetTimestamp() - _startTimestamp,
                };
                DisposeNative(true);
                return _completed;
            }

            int length = _result[0];
            if (!BasisNativeAnimationPayload.TryReserveBytes(length, out string payloadError))
            {
                _completed = new BasisBurstAnimationEncodeResult
                {
                    Ok = false,
                    Error = payloadError,
                    WorkerElapsedTicks =
                        System.Diagnostics.Stopwatch.GetTimestamp() - _startTimestamp,
                };
                DisposeNative(true);
                return _completed;
            }

            // _output is sized for the LZ4 worst case and can be tens of megabytes even when
            // the compressed payload is small. Never retain that scratch capacity per pickup.
            NativeArray<byte> payloadBytes = default;
            bool payloadReservationHeld = true;
            try
            {
                payloadBytes = new NativeArray<byte>(
                    length,
                    Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory
                );
                NativeArray<byte>.Copy(_output, 0, payloadBytes, 0, length);
            }
            catch (Exception exception)
            {
                if (payloadBytes.IsCreated)
                    payloadBytes.Dispose();
                if (payloadReservationHeld)
                {
                    BasisNativeAnimationPayload.ReleaseReservation(length);
                    payloadReservationHeld = false;
                }
                _completed = new BasisBurstAnimationEncodeResult
                {
                    Ok = false,
                    Error = "Animation payload compaction failed: " + exception.Message,
                    WorkerElapsedTicks =
                        System.Diagnostics.Stopwatch.GetTimestamp() - _startTimestamp,
                };
                DisposeNative(true);
                return _completed;
            }

            BasisNativeAnimationPayload payload;
            try
            {
                payload = new BasisNativeAnimationPayload(payloadBytes, length, true);
                payloadReservationHeld = false;
            }
            catch
            {
                if (payloadBytes.IsCreated)
                    payloadBytes.Dispose();
                if (payloadReservationHeld)
                    BasisNativeAnimationPayload.ReleaseReservation(length);
                DisposeNative(true);
                throw;
            }

            _completed = new BasisBurstAnimationEncodeResult
            {
                Ok = true,
                Payload = payload,
                WorkerElapsedTicks =
                    System.Diagnostics.Stopwatch.GetTimestamp() - _startTimestamp,
            };
            DisposeNative(true);
            _resultClaimed = true;
            return _completed;
        }

        private void DisposeNative(bool includeOutput)
        {
            if (_raw.IsCreated)
                _raw.Dispose();
            if (includeOutput && _output.IsCreated)
                _output.Dispose();
            if (_hash.IsCreated)
                _hash.Dispose();
            if (_result.IsCreated)
                _result.Dispose();
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
                if (_completed == null)
                    _handle.Complete();
                if (!_resultClaimed)
                    _completed?.TakePayload()?.Dispose();
            }
            finally
            {
                _completed = null;
                DisposeNative(true);
            }
        }
    }

    internal sealed class BasisBurstAnimationDecodeRequest : IDisposable
    {
        private enum DecodeState
        {
            DecompressHeader,
            Unpack,
            Complete,
        }

        private NativeArray<byte> _payload;
        private NativeArray<byte> _raw;
        private NativeArray<int> _outer;
        private NativeArray<BasisAnimationBodyHeader> _bodyHeader;
        private NativeArray<BasisAnimatedImageFrame> _frames;
        private NativeArray<Color32> _pixels;
        private NativeArray<long> _frameEnds;
        private NativeArray<int> _errors;
        private JobHandle _handle;
        private DecodeState _state;
        private BasisBurstAnimationDecodeResult _completed;
        private long _reservedWorkingBytes;
        private bool _resultClaimed;
        private bool _disposed;
        private readonly long _startTimestamp;

        public bool IsCompleted =>
            _state == DecodeState.Complete || _handle.IsCompleted;

        public BasisBurstAnimationDecodeRequest(NativeArray<byte> payload, int payloadLength, bool takeOwnership)
            : this(
                payload,
                payloadLength,
                takeOwnership,
                false,
                BasisAnimationDecodeTrust.UntrustedRemote
            ) { }

        internal BasisBurstAnimationDecodeRequest(
            NativeArray<byte> payload,
            int payloadLength,
            bool takeOwnership,
            BasisAnimationDecodeTrust trust
        )
            : this(payload, payloadLength, takeOwnership, false, trust) { }

        private BasisBurstAnimationDecodeRequest(
            NativeArray<byte> payload,
            int payloadLength,
            bool takeOwnership,
            bool disposeOwnedInputOnFailure,
            BasisAnimationDecodeTrust trust
        )
        {
            if (
                !payload.IsCreated
                || payloadLength < BasisBurstAnimationCodec.OuterHeaderBytes
                || payloadLength > payload.Length
            )
            {
                throw new ArgumentException("Animation payload is invalid.", nameof(payload));
            }

            _startTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            JobHandle cleanupHandle = default;
            bool payloadAliasesCaller = false;
            try
            {
                if (
                    !BasisBurstAnimationCodec.TryReadOuterHeader(
                        payload,
                        payloadLength,
                        trust,
                        out int rawLength,
                        out string headerError
                    )
                )
                {
                    if (takeOwnership)
                        payload.Dispose();
                    _completed = new BasisBurstAnimationDecodeResult
                    {
                        Ok = false,
                        Error = headerError,
                        WorkerElapsedTicks =
                            System.Diagnostics.Stopwatch.GetTimestamp()
                            - _startTimestamp,
                    };
                    _state = DecodeState.Complete;
                    return;
                }
                _reservedWorkingBytes = checked(
                    (long)payloadLength
                    + 2L * rawLength
                    + (long)BasisImagePickupSettings.MaxAnimationFrames * 16L
                    + 256L
                );
                if (!BasisAnimatedImageData.TryReserveWorkingBytes(_reservedWorkingBytes, out string budgetError))
                {
                    _reservedWorkingBytes = 0;
                    throw new BasisAnimationMemoryBudgetException(budgetError);
                }

                if (takeOwnership && payloadLength == payload.Length)
                {
                    _payload = payload;
                    payloadAliasesCaller = true;
                }
                else
                {
                    _payload = new NativeArray<byte>(
                        payloadLength,
                        Allocator.Persistent,
                        NativeArrayOptions.UninitializedMemory
                    );
                    NativeArray<byte>.Copy(payload, 0, _payload, 0, payloadLength);
                }

                _raw = new NativeArray<byte>(rawLength, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                _outer = new NativeArray<int>(2, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                _bodyHeader = new NativeArray<BasisAnimationBodyHeader>(
                    1,
                    Allocator.Persistent,
                    NativeArrayOptions.ClearMemory
                );

                JobHandle decompress = new BasisLz4DecompressJob
                {
                    Payload = _payload,
                    Raw = _raw,
                    Result = _outer,
                }.Schedule();
                cleanupHandle = decompress;
                _handle = new BasisAnimationParseBodyHeaderJob
                {
                    Raw = _raw,
                    Result = _bodyHeader,
                }.Schedule(decompress);
                cleanupHandle = _handle;
                JobHandle.ScheduleBatchedJobs();
                _state = DecodeState.DecompressHeader;
                if (takeOwnership && !payloadAliasesCaller)
                    payload.Dispose();
            }
            catch
            {
                cleanupHandle.Complete();
                if (payloadAliasesCaller && !disposeOwnedInputOnFailure)
                    _payload = default;
                DisposeAllNative();
                throw;
            }
        }

        public BasisBurstAnimationDecodeRequest(byte[] payload)
            : this(
                ToNative(payload),
                payload?.Length ?? 0,
                true,
                true,
                BasisAnimationDecodeTrust.UntrustedRemote
            ) { }

        public bool TryComplete(out BasisBurstAnimationDecodeResult result)
        {
            result = null;
            if (_state != DecodeState.Complete && !_handle.IsCompleted)
                return false;
            Advance(false);
            if (_state != DecodeState.Complete)
                return false;
            result = _completed;
            if (result?.Ok == true)
                _resultClaimed = true;
            return true;
        }

        public BasisBurstAnimationDecodeResult Complete()
        {
            while (_state != DecodeState.Complete)
                Advance(true);
            if (_completed?.Ok == true)
                _resultClaimed = true;
            return _completed;
        }

        private void Advance(bool block)
        {
            if (_state == DecodeState.Complete)
                return;
            if (!block && !_handle.IsCompleted)
                return;
            _handle.Complete();

            if (_state == DecodeState.DecompressHeader)
            {
                BasisAnimationCodecError outerError = (BasisAnimationCodecError)
                    _outer[1];
                BasisAnimationBodyHeader header = _bodyHeader[0];
                if (outerError != BasisAnimationCodecError.None)
                {
                    FinishFailure(BasisBurstAnimationCodec.DescribeError(outerError));
                    return;
                }
                if (header.Error != BasisAnimationCodecError.None)
                {
                    FinishFailure(BasisBurstAnimationCodec.DescribeError(header.Error));
                    return;
                }

                try
                {
                    _frames = new NativeArray<BasisAnimatedImageFrame>(
                        header.FrameCount,
                        Allocator.Persistent,
                        NativeArrayOptions.UninitializedMemory
                    );
                    _pixels = new NativeArray<Color32>(
                        header.PixelCount,
                        Allocator.Persistent,
                        NativeArrayOptions.UninitializedMemory
                    );
                    _frameEnds = new NativeArray<long>(
                        header.FrameCount,
                        Allocator.Persistent,
                        NativeArrayOptions.UninitializedMemory
                    );
                    _errors = new NativeArray<int>(
                        header.FrameCount + 1,
                        Allocator.Persistent,
                        NativeArrayOptions.ClearMemory
                    );
                }
                catch (Exception exception)
                {
                    FinishFailure("Animation native allocation failed: " + exception.Message);
                    return;
                }

                JobHandle frames = default;
                JobHandle pixels = default;
                try
                {
                    var unpackFramesJob = new BasisAnimationUnpackFramesJob
                    {
                        Raw = _raw,
                        Header = header,
                        Frames = _frames,
                        FrameEnds = _frameEnds,
                        Errors = _errors,
                    };
                    frames = unpackFramesJob.ScheduleParallelByRef(header.FrameCount, 64, default);
                    var unpackPixelsJob = new BasisAnimationUnpackPixelsJob
                    {
                        SourceOffset = header.PixelDataOffset,
                        Raw = _raw,
                        Pixels = _pixels,
                    };
                    pixels = unpackPixelsJob.ScheduleParallelByRef(header.PixelCount, 512, default);
                    JobHandle dependencies = JobHandle.CombineDependencies(frames, pixels);
                    _handle = new BasisAnimationValidateAlphaJob
                    {
                        Header = header,
                        Pixels = _pixels,
                        Errors = _errors,
                        ErrorIndex = header.FrameCount,
                    }.Schedule(dependencies);
                    JobHandle.ScheduleBatchedJobs();
                    _state = DecodeState.Unpack;
                    return;
                }
                catch (Exception exception)
                {
                    _handle.Complete();
                    pixels.Complete();
                    frames.Complete();
                    FinishFailure("Animation job scheduling failed: " + exception.Message);
                    return;
                }
            }

            BasisAnimationBodyHeader completedHeader = _bodyHeader[0];
            int errorCount = _errors.Length;
            for (int i = 0; i < errorCount; i++)
            {
                if (_errors[i] == 0)
                    continue;
                FinishFailure(BasisBurstAnimationCodec.DescribeError((BasisAnimationCodecError)_errors[i]));
                return;
            }

            if (
                !BasisAnimatedImageData.TryAdoptNative(
                    completedHeader.CanvasWidth,
                    completedHeader.CanvasHeight,
                    completedHeader.TotalPlayCount,
                    completedHeader.BackgroundColor,
                    _frames,
                    _pixels,
                    _frameEnds,
                    completedHeader.TotalDurationMicroseconds,
                    completedHeader.HasAnyAlpha != 0,
                    completedHeader.HasPartialAlpha != 0,
                    completedHeader.RequiresPrevious != 0,
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
            _completed = new BasisBurstAnimationDecodeResult
            {
                Ok = true,
                Animation = animation,
                WorkerElapsedTicks =
                    System.Diagnostics.Stopwatch.GetTimestamp() - _startTimestamp,
            };
            DisposeTemporary();
            _state = DecodeState.Complete;
        }

        private void FinishFailure(string error)
        {
            _completed = new BasisBurstAnimationDecodeResult
            {
                Ok = false,
                Error = error,
                WorkerElapsedTicks =
                    System.Diagnostics.Stopwatch.GetTimestamp() - _startTimestamp,
            };
            DisposeAllNative();
            _state = DecodeState.Complete;
        }

        private void DisposeTemporary()
        {
            if (_payload.IsCreated)
                _payload.Dispose();
            if (_raw.IsCreated)
                _raw.Dispose();
            if (_outer.IsCreated)
                _outer.Dispose();
            if (_bodyHeader.IsCreated)
                _bodyHeader.Dispose();
            if (_errors.IsCreated)
                _errors.Dispose();
            if (_reservedWorkingBytes > 0)
            {
                BasisAnimatedImageData.ReleaseWorkingBytes(_reservedWorkingBytes);
                _reservedWorkingBytes = 0;
            }
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
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                if (_state != DecodeState.Complete)
                    _handle.Complete();
                if (!_resultClaimed)
                    _completed?.TakeAnimation()?.Dispose();
            }
            finally
            {
                _completed = null;
                DisposeAllNative();
            }
        }

        private static NativeArray<byte> ToNative(byte[] payload)
        {
            if (payload == null || payload.Length < BasisBurstAnimationCodec.OuterHeaderBytes)
            {
                throw new ArgumentException("Animation payload is invalid.", nameof(payload));
            }
            var native = new NativeArray<byte>(
                payload.Length,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory
            );
            native.CopyFrom(payload);
            return native;
        }
    }

    internal struct BasisAnimationBodyHeader
    {
        public BasisAnimationCodecError Error;
        public int CanvasWidth;
        public int CanvasHeight;
        public int TotalPlayCount;
        public int FrameCount;
        public Color32 BackgroundColor;
        public long TotalDurationMicroseconds;
        public int PixelCount;
        public int PixelDataOffset;
        public byte HasAnyAlpha;
        public byte HasPartialAlpha;
        public byte RequiresPrevious;
        public byte Reserved;
    }

    internal static class BasisBurstAnimationCodec
    {
        public const uint Magic = 0x32494142u;
        public const byte Version = 2;
        public const int OuterHeaderBytes = 16;
        public const int BodyHeaderBytes = 40;
        public const int FrameRecordBytes = 48;
        public const int Lz4HashSize = 1 << 16;

        public static bool TryReadOuterHeader(
            NativeArray<byte> payload,
            int length,
            out int decodedLength,
            out string error
        )
        {
            return TryReadOuterHeader(
                payload,
                length,
                BasisAnimationDecodeTrust.UntrustedRemote,
                out decodedLength,
                out error
            );
        }

        internal static bool TryReadOuterHeader(
            NativeArray<byte> payload,
            int length,
            BasisAnimationDecodeTrust trust,
            out int decodedLength,
            out string error
        )
        {
            long decodedBodyLimit =
                trust == BasisAnimationDecodeTrust.TrustedLocal
                    ? BasisImagePickupSettings.MaxAnimationNetworkDecodedBytes
                    : BasisImagePickupSettings.MaxInboundAnimationDecodedBodyBytes;
            return TryReadOuterHeader(
                payload,
                length,
                decodedBodyLimit,
                out decodedLength,
                out error
            );
        }

        internal static bool TryReadOuterHeader(
            NativeArray<byte> payload,
            int length,
            long decodedBodyLimit,
            out int decodedLength,
            out string error
        )
        {
            decodedLength = 0;
            error = null;
            if (!payload.IsCreated || length < OuterHeaderBytes || length > payload.Length)
            {
                error = "Animation payload header is truncated.";
                return false;
            }
            uint magic = (uint)(payload[0] | (payload[1] << 8) | (payload[2] << 16) | (payload[3] << 24));
            if (magic != Magic || payload[4] != Version)
            {
                error =
                    magic != Magic
                        ? "Animation payload magic is invalid."
                        : "Animation payload version is unsupported.";
                return false;
            }
            if (payload[5] != 0 || payload[6] != 0 || payload[7] != 0)
            {
                error = "Animation payload reserved header bytes are non-zero.";
                return false;
            }
            decodedLength = ReadInt32(payload, 8);
            int compressedLength = ReadInt32(payload, 12);
            if (
                decodedBodyLimit <= 0
                || decodedBodyLimit
                    > BasisImagePickupSettings.MaxAnimationNetworkDecodedBytes
                || decodedLength <= 0
                || decodedLength > decodedBodyLimit
                || compressedLength <= 0
                || compressedLength != length - OuterHeaderBytes
            )
            {
                error = "Animation payload lengths are invalid.";
                decodedLength = 0;
                return false;
            }
            return true;
        }

        public static int CalculateRawLength(BasisAnimatedImageData data)
        {
            long value =
                BodyHeaderBytes
                + (long)data.FrameCount * FrameRecordBytes
                + (long)data.PixelsNative.Length * 4L;
            if (value <= 0 || value > BasisImagePickupSettings.MaxAnimationNetworkDecodedBytes || value > int.MaxValue)
            {
                throw new InvalidOperationException("Animation raw network body exceeds the configured limit.");
            }
            return (int)value;
        }

        public static int Lz4MaximumLength(int inputLength)
        {
            return checked(inputLength + inputLength / 255 + 16);
        }

        internal static bool TryAccumulateLz4Length(ref int length, int value)
        {
            if (value < 0 || length > int.MaxValue - value)
                return false;
            length += value;
            return true;
        }

        public static string DescribeError(BasisAnimationCodecError error)
        {
            return error switch
            {
                BasisAnimationCodecError.InvalidInput =>
                    "Animation codec input is invalid.",
                BasisAnimationCodecError.OutputTooLarge =>
                    "Animation does not compress below the network limit.",
                BasisAnimationCodecError.OutputOverflow =>
                    "Animation codec output buffer overflowed.",
                BasisAnimationCodecError.InvalidMagic =>
                    "Animation payload magic is invalid.",
                BasisAnimationCodecError.UnsupportedVersion =>
                    "Animation payload version is unsupported.",
                BasisAnimationCodecError.Truncated => "Animation payload is truncated.",
                BasisAnimationCodecError.InvalidLength =>
                    "Animation payload length is invalid.",
                BasisAnimationCodecError.InvalidHeader =>
                    "Animation body header is invalid.",
                BasisAnimationCodecError.InvalidFrame =>
                    "Animation frame metadata is invalid.",
                BasisAnimationCodecError.InvalidPixelRange =>
                    "Animation frame pixel range is invalid.",
                BasisAnimationCodecError.InvalidTiming =>
                    "Animation timing metadata is invalid.",
                BasisAnimationCodecError.InvalidLz4Offset =>
                    "Animation LZ4 stream contains an invalid offset.",
                BasisAnimationCodecError.InvalidLz4Token =>
                    "Animation LZ4 stream contains an invalid token.",
                _ => "Animation codec failed.",
            };
        }

        internal static int ReadInt32(NativeArray<byte> bytes, int offset)
        {
            if (!bytes.IsCreated || offset < 0 || offset + 4 > bytes.Length)
                return -1;
            return bytes[offset]
                | (bytes[offset + 1] << 8)
                | (bytes[offset + 2] << 16)
                | (bytes[offset + 3] << 24);
        }
    }

    [BurstCompile]
    internal struct BasisAnimationPackHeaderJob : IJob
    {
        public int CanvasWidth;
        public int CanvasHeight;
        public int TotalPlayCount;
        public Color32 BackgroundColor;
        public long TotalDurationMicroseconds;
        public byte HasAnyAlpha;
        public byte HasPartialAlpha;
        public byte RequiresPrevious;

        [ReadOnly]
        public NativeArray<BasisAnimatedImageFrame> Frames;
        public NativeArray<byte> Raw;

        public void Execute()
        {
            int frameCount = Frames.Length;
            int pixelCount = 0;
            for (int i = 0; i < frameCount; i++)
                pixelCount += Frames[i].PixelCount;

            WriteInt32(Raw, 0, CanvasWidth);
            WriteInt32(Raw, 4, CanvasHeight);
            WriteInt32(Raw, 8, TotalPlayCount);
            WriteInt32(Raw, 12, frameCount);
            Raw[16] = BackgroundColor.r;
            Raw[17] = BackgroundColor.g;
            Raw[18] = BackgroundColor.b;
            Raw[19] = BackgroundColor.a;
            Raw[20] = HasAnyAlpha;
            Raw[21] = HasPartialAlpha;
            Raw[22] = RequiresPrevious;
            Raw[23] = 0;
            WriteInt64(Raw, 24, TotalDurationMicroseconds);
            WriteInt32(Raw, 32, pixelCount);
            WriteInt32(
                Raw,
                36,
                BasisBurstAnimationCodec.BodyHeaderBytes
                    + frameCount * BasisBurstAnimationCodec.FrameRecordBytes
            );

            for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
            {
                BasisAnimatedImageFrame frame = Frames[frameIndex];
                int offset =
                    BasisBurstAnimationCodec.BodyHeaderBytes
                    + frameIndex * BasisBurstAnimationCodec.FrameRecordBytes;
                WriteInt32(Raw, offset, frame.X);
                WriteInt32(Raw, offset + 4, frame.Y);
                WriteInt32(Raw, offset + 8, frame.Width);
                WriteInt32(Raw, offset + 12, frame.Height);
                WriteInt32(Raw, offset + 16, frame.PixelOffset);
                WriteInt32(Raw, offset + 20, frame.PixelCount);
                WriteInt64(Raw, offset + 24, frame.DurationMicroseconds);
                WriteInt64(Raw, offset + 32, frame.EndTimeMicroseconds);
                Raw[offset + 40] = (byte)frame.Blend;
                Raw[offset + 41] = (byte)frame.Disposal;
                for (int reserved = 42; reserved < BasisBurstAnimationCodec.FrameRecordBytes; reserved++)
                    Raw[offset + reserved] = 0;
            }
        }

        private static void WriteInt32(NativeArray<byte> bytes, int offset, int value)
        {
            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
            bytes[offset + 2] = (byte)(value >> 16);
            bytes[offset + 3] = (byte)(value >> 24);
        }

        private static void WriteInt64(NativeArray<byte> bytes, int offset, long value)
        {
            ulong unsigned = (ulong)value;
            for (int i = 0; i < 8; i++)
                bytes[offset + i] = (byte)(unsigned >> (i * 8));
        }
    }

    [BurstCompile]
    internal struct BasisAnimationPackPixelsJob : IJobFor
    {
        public int DestinationOffset;

        [ReadOnly]
        public NativeArray<Color32> Pixels;

        [NativeDisableParallelForRestriction]
        public NativeArray<byte> Raw;

        public void Execute(int index)
        {
            Color32 pixel = Pixels[index];
            int offset = DestinationOffset + index * 4;
            Raw[offset] = pixel.r;
            Raw[offset + 1] = pixel.g;
            Raw[offset + 2] = pixel.b;
            Raw[offset + 3] = pixel.a;
        }
    }

    [BurstCompile]
    internal struct BasisLz4ClearHashJob : IJobFor
    {
        [WriteOnly]
        public NativeArray<int> Hash;

        public void Execute(int index) => Hash[index] = -1;
    }

    [BurstCompile]
    internal struct BasisLz4CompressJob : IJob
    {
        [ReadOnly]
        public NativeArray<byte> Raw;
        public NativeArray<byte> Output;
        public NativeArray<int> Hash;
        public NativeArray<int> Result;

        public void Execute()
        {
            int inputLength = Raw.Length;
            int input = 0;
            int anchor = 0;
            int output = BasisBurstAnimationCodec.OuterHeaderBytes;

            while (input + 4 <= inputLength)
            {
                uint sequence = ReadUInt32(Raw, input);
                int hash =
                    (int)((sequence * 2654435761u) >> 16)
                    & (BasisBurstAnimationCodec.Lz4HashSize - 1);
                int match = Hash[hash];
                Hash[hash] = input;

                if (
                    match < 0
                    || input - match > ushort.MaxValue
                    || match + 4 > inputLength
                    || ReadUInt32(Raw, match) != sequence
                )
                {
                    input++;
                    continue;
                }

                int literalLength = input - anchor;
                int matchLength = 4;
                int maximumMatch = inputLength - input;
                while (matchLength < maximumMatch && Raw[match + matchLength] == Raw[input + matchLength])
                {
                    matchLength++;
                }

                if (!WriteSequence(ref output, anchor, literalLength, input - match, matchLength))
                {
                    Result[1] = (int)BasisAnimationCodecError.OutputTooLarge;
                    return;
                }

                input += matchLength;
                anchor = input;
                if (input + 4 <= inputLength)
                {
                    int update = math.max(anchor - 2, 0);
                    while (update < anchor && update + 4 <= inputLength)
                    {
                        uint updateSequence = ReadUInt32(Raw, update);
                        int updateHash =
                            (int)((updateSequence * 2654435761u) >> 16)
                            & (BasisBurstAnimationCodec.Lz4HashSize - 1);
                        Hash[updateHash] = update;
                        update++;
                    }
                }
            }

            int lastLiterals = inputLength - anchor;
            if (!WriteLastLiterals(ref output, anchor, lastLiterals))
            {
                Result[1] = (int)BasisAnimationCodecError.OutputTooLarge;
                return;
            }

            int compressedLength = output - BasisBurstAnimationCodec.OuterHeaderBytes;
            WriteUInt32(Output, 0, BasisBurstAnimationCodec.Magic);
            Output[4] = BasisBurstAnimationCodec.Version;
            Output[5] = 0;
            Output[6] = 0;
            Output[7] = 0;
            WriteInt32(Output, 8, inputLength);
            WriteInt32(Output, 12, compressedLength);
            Result[0] = output;
            Result[1] = 0;
        }

        private bool WriteSequence(
            ref int output,
            int literalOffset,
            int literalLength,
            int matchOffset,
            int matchLength
        )
        {
            if (output >= Output.Length)
                return false;
            int tokenOffset = output++;
            int literalToken = math.min(literalLength, 15);
            int matchToken = math.min(matchLength - 4, 15);
            Output[tokenOffset] = (byte)((literalToken << 4) | matchToken);
            if (literalLength >= 15 && !WriteExtendedLength(ref output, literalLength - 15))
                return false;
            if (output + literalLength + 2 > Output.Length)
                return false;
            for (int i = 0; i < literalLength; i++)
                Output[output + i] = Raw[literalOffset + i];
            output += literalLength;
            Output[output++] = (byte)matchOffset;
            Output[output++] = (byte)(matchOffset >> 8);
            return matchLength - 4 < 15
                || WriteExtendedLength(ref output, matchLength - 4 - 15);
        }

        private bool WriteLastLiterals(ref int output, int offset, int length)
        {
            if (output >= Output.Length)
                return false;
            Output[output++] = (byte)(math.min(length, 15) << 4);
            if (length >= 15 && !WriteExtendedLength(ref output, length - 15))
                return false;
            if (output + length > Output.Length)
                return false;
            for (int i = 0; i < length; i++)
                Output[output + i] = Raw[offset + i];
            output += length;
            return true;
        }

        private bool WriteExtendedLength(ref int output, int value)
        {
            while (value >= 255)
            {
                if (output >= Output.Length)
                    return false;
                Output[output++] = 255;
                value -= 255;
            }
            if (output >= Output.Length)
                return false;
            Output[output++] = (byte)value;
            return true;
        }

        private static uint ReadUInt32(NativeArray<byte> bytes, int offset)
        {
            return (uint)(
                bytes[offset]
                | (bytes[offset + 1] << 8)
                | (bytes[offset + 2] << 16)
                | (bytes[offset + 3] << 24)
            );
        }

        private static void WriteUInt32(NativeArray<byte> bytes, int offset, uint value)
        {
            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
            bytes[offset + 2] = (byte)(value >> 16);
            bytes[offset + 3] = (byte)(value >> 24);
        }

        private static void WriteInt32(NativeArray<byte> bytes, int offset, int value)
        {
            WriteUInt32(bytes, offset, (uint)value);
        }
    }

    [BurstCompile]
    internal struct BasisLz4DecompressJob : IJob
    {
        [ReadOnly]
        public NativeArray<byte> Payload;
        public NativeArray<byte> Raw;
        public NativeArray<int> Result;

        public void Execute()
        {
            if (Payload.Length < BasisBurstAnimationCodec.OuterHeaderBytes)
            {
                Fail(BasisAnimationCodecError.Truncated);
                return;
            }
            if (ReadUInt32(Payload, 0) != BasisBurstAnimationCodec.Magic)
            {
                Fail(BasisAnimationCodecError.InvalidMagic);
                return;
            }
            if (Payload[4] != BasisBurstAnimationCodec.Version)
            {
                Fail(BasisAnimationCodecError.UnsupportedVersion);
                return;
            }
            if (Payload[5] != 0 || Payload[6] != 0 || Payload[7] != 0)
            {
                Fail(BasisAnimationCodecError.InvalidHeader);
                return;
            }

            int rawLength = ReadInt32(Payload, 8);
            int compressedLength = ReadInt32(Payload, 12);
            if (
                rawLength != Raw.Length
                || compressedLength <= 0
                || compressedLength
                    != Payload.Length - BasisBurstAnimationCodec.OuterHeaderBytes
            )
            {
                Fail(BasisAnimationCodecError.InvalidLength);
                return;
            }

            int input = BasisBurstAnimationCodec.OuterHeaderBytes;
            int inputEnd = Payload.Length;
            int output = 0;
            while (input < inputEnd)
            {
                byte token = Payload[input++];
                int literalLength = token >> 4;
                if (literalLength == 15 && !ReadExtendedLength(ref input, inputEnd, ref literalLength))
                {
                    Fail(BasisAnimationCodecError.InvalidLz4Token);
                    return;
                }
                if (
                    literalLength > inputEnd - input
                    || literalLength > Raw.Length - output
                )
                {
                    Fail(BasisAnimationCodecError.Truncated);
                    return;
                }
                for (int i = 0; i < literalLength; i++)
                    Raw[output + i] = Payload[input + i];
                input += literalLength;
                output += literalLength;
                if (input == inputEnd)
                    break;
                if (inputEnd - input < 2)
                {
                    Fail(BasisAnimationCodecError.Truncated);
                    return;
                }

                int matchOffset = Payload[input] | (Payload[input + 1] << 8);
                input += 2;
                if (matchOffset <= 0 || matchOffset > output)
                {
                    Fail(BasisAnimationCodecError.InvalidLz4Offset);
                    return;
                }

                int matchLength = (token & 15) + 4;
                if ((token & 15) == 15 && !ReadExtendedLength(ref input, inputEnd, ref matchLength))
                {
                    Fail(BasisAnimationCodecError.InvalidLz4Token);
                    return;
                }
                if (matchLength > Raw.Length - output)
                {
                    Fail(BasisAnimationCodecError.OutputOverflow);
                    return;
                }

                int match = output - matchOffset;
                for (int i = 0; i < matchLength; i++)
                    Raw[output++] = Raw[match + i];
            }

            if (output != Raw.Length)
            {
                Fail(BasisAnimationCodecError.InvalidLength);
                return;
            }
            Result[0] = output;
            Result[1] = 0;
        }

        private bool ReadExtendedLength(ref int input, int end, ref int length)
        {
            while (true)
            {
                if (input >= end)
                    return false;
                int value = Payload[input++];
                if (!BasisBurstAnimationCodec.TryAccumulateLz4Length(ref length, value))
                    return false;
                if (value != 255)
                    return true;
            }
        }

        private void Fail(BasisAnimationCodecError error)
        {
            Result[1] = (int)error;
        }

        private static uint ReadUInt32(NativeArray<byte> bytes, int offset)
        {
            return (uint)(
                bytes[offset]
                | (bytes[offset + 1] << 8)
                | (bytes[offset + 2] << 16)
                | (bytes[offset + 3] << 24)
            );
        }

        private static int ReadInt32(NativeArray<byte> bytes, int offset)
        {
            return (int)ReadUInt32(bytes, offset);
        }
    }

    [BurstCompile]
    internal struct BasisAnimationParseBodyHeaderJob : IJob
    {
        [ReadOnly]
        public NativeArray<byte> Raw;
        public NativeArray<BasisAnimationBodyHeader> Result;

        public void Execute()
        {
            var header = new BasisAnimationBodyHeader();
            if (Raw.Length < BasisBurstAnimationCodec.BodyHeaderBytes)
            {
                header.Error = BasisAnimationCodecError.Truncated;
                Result[0] = header;
                return;
            }

            header.CanvasWidth = BasisAnimatedImageNetworkCodec.ReadInt32(Raw, 0);
            header.CanvasHeight = BasisAnimatedImageNetworkCodec.ReadInt32(Raw, 4);
            header.TotalPlayCount = BasisAnimatedImageNetworkCodec.ReadInt32(Raw, 8);
            header.FrameCount = BasisAnimatedImageNetworkCodec.ReadInt32(Raw, 12);
            header.BackgroundColor = new Color32(Raw[16], Raw[17], Raw[18], Raw[19]);
            header.HasAnyAlpha = Raw[20];
            header.HasPartialAlpha = Raw[21];
            header.RequiresPrevious = Raw[22];
            header.Reserved = Raw[23];
            header.TotalDurationMicroseconds = BasisAnimatedImageNetworkCodec.ReadInt64(Raw, 24);
            header.PixelCount = BasisAnimatedImageNetworkCodec.ReadInt32(Raw, 32);
            header.PixelDataOffset = BasisAnimatedImageNetworkCodec.ReadInt32(Raw, 36);

            long expectedOffset =
                BasisBurstAnimationCodec.BodyHeaderBytes
                + (long)header.FrameCount * BasisBurstAnimationCodec.FrameRecordBytes;
            long expectedLength = expectedOffset + (long)header.PixelCount * 4L;
            if (
                header.CanvasWidth <= 0
                || header.CanvasHeight <= 0
                || header.CanvasWidth > BasisImagePickupSettings.MaxAnimationDimension
                || header.CanvasHeight > BasisImagePickupSettings.MaxAnimationDimension
                || (long)header.CanvasWidth * header.CanvasHeight
                    > BasisImagePickupSettings.MaxAnimationCanvasPixels
                || header.TotalPlayCount < 0
                || header.HasAnyAlpha > 1
                || header.HasPartialAlpha > 1
                || header.RequiresPrevious > 1
                || header.Reserved != 0
                || header.FrameCount <= 0
                || header.FrameCount > BasisImagePickupSettings.MaxAnimationFrames
                || header.PixelCount <= 0
                || header.PixelCount
                    > BasisImagePickupSettings.MaxAnimationDecodedFramePixels
                || header.TotalDurationMicroseconds <= 0
                || header.TotalDurationMicroseconds
                    > BasisImagePickupSettings.MaxAnimationDurationMicroseconds
                || header.PixelDataOffset != expectedOffset
                || expectedLength != Raw.Length
            )
            {
                header.Error = BasisAnimationCodecError.InvalidHeader;
            }
            Result[0] = header;
        }
    }

    [BurstCompile]
    internal struct BasisAnimationUnpackFramesJob : IJobFor
    {
        [ReadOnly]
        public NativeArray<byte> Raw;
        public BasisAnimationBodyHeader Header;

        [NativeDisableParallelForRestriction]
        public NativeArray<BasisAnimatedImageFrame> Frames;

        [NativeDisableParallelForRestriction]
        public NativeArray<long> FrameEnds;

        [NativeDisableParallelForRestriction]
        public NativeArray<int> Errors;

        public void Execute(int index)
        {
            int offset =
                BasisBurstAnimationCodec.BodyHeaderBytes
                + index * BasisBurstAnimationCodec.FrameRecordBytes;
            var frame = new BasisAnimatedImageFrame
            {
                X = BasisAnimatedImageNetworkCodec.ReadInt32(Raw, offset),
                Y = BasisAnimatedImageNetworkCodec.ReadInt32(Raw, offset + 4),
                Width = BasisAnimatedImageNetworkCodec.ReadInt32(Raw, offset + 8),
                Height = BasisAnimatedImageNetworkCodec.ReadInt32(Raw, offset + 12),
                PixelOffset = BasisAnimatedImageNetworkCodec.ReadInt32(Raw, offset + 16),
                PixelCount = BasisAnimatedImageNetworkCodec.ReadInt32(Raw, offset + 20),
                DurationMicroseconds = BasisAnimatedImageNetworkCodec.ReadInt64(Raw, offset + 24),
                EndTimeMicroseconds = BasisAnimatedImageNetworkCodec.ReadInt64(Raw, offset + 32),
                Blend = (BasisAnimationBlend)Raw[offset + 40],
                Disposal = (BasisAnimationDisposal)Raw[offset + 41],
            };

            bool reservedBytesAreZero = true;
            for (int reservedOffset = 42; reservedOffset < 48; reservedOffset++)
            {
                if (Raw[offset + reservedOffset] == 0)
                    continue;
                reservedBytesAreZero = false;
                break;
            }

            long frameArea = (long)frame.Width * frame.Height;
            if (
                frame.Width <= 0
                || frame.Height <= 0
                || frame.X < 0
                || frame.Y < 0
                || (long)frame.X + frame.Width > Header.CanvasWidth
                || (long)frame.Y + frame.Height > Header.CanvasHeight
                || frameArea != frame.PixelCount
                || frame.PixelOffset < 0
                || frame.PixelCount <= 0
                || (long)frame.PixelOffset + frame.PixelCount > Header.PixelCount
                || !reservedBytesAreZero
                || frame.DurationMicroseconds
                    < BasisImagePickupSettings.MinAnimationFrameDurationMicroseconds
                || frame.EndTimeMicroseconds <= 0
                || frame.EndTimeMicroseconds > Header.TotalDurationMicroseconds
                || (frame.Blend != BasisAnimationBlend.Source && frame.Blend != BasisAnimationBlend.Over)
                || (
                    frame.Disposal != BasisAnimationDisposal.None
                    && frame.Disposal != BasisAnimationDisposal.Background
                    && frame.Disposal != BasisAnimationDisposal.Previous
                )
            )
            {
                Errors[index] = (int)BasisAnimationCodecError.InvalidFrame;
                return;
            }
            if (index > 0)
            {
                int previousOffset = offset - BasisBurstAnimationCodec.FrameRecordBytes;
                long previousEnd = BasisAnimatedImageNetworkCodec.ReadInt64(Raw, previousOffset + 32);
                int previousPixelOffset = BasisAnimatedImageNetworkCodec.ReadInt32(Raw, previousOffset + 16);
                int previousPixelCount = BasisAnimatedImageNetworkCodec.ReadInt32(Raw, previousOffset + 20);
                if (
                    frame.EndTimeMicroseconds <= previousEnd
                    || frame.PixelOffset != previousPixelOffset + previousPixelCount
                )
                {
                    Errors[index] = (int)BasisAnimationCodecError.InvalidTiming;
                    return;
                }
            }
            else if (frame.PixelOffset != 0)
            {
                Errors[index] = (int)BasisAnimationCodecError.InvalidPixelRange;
                return;
            }
            if (
                index == Header.FrameCount - 1
                && (
                    frame.EndTimeMicroseconds != Header.TotalDurationMicroseconds
                    || frame.PixelOffset + frame.PixelCount != Header.PixelCount
                )
            )
            {
                Errors[index] = (int)BasisAnimationCodecError.InvalidTiming;
                return;
            }

            Frames[index] = frame;
            FrameEnds[index] = frame.EndTimeMicroseconds;
        }
    }

    [BurstCompile]
    internal struct BasisAnimationValidateAlphaJob : IJob
    {
        public BasisAnimationBodyHeader Header;

        [ReadOnly]
        public NativeArray<Color32> Pixels;

        [NativeDisableParallelForRestriction]
        public NativeArray<int> Errors;
        public int ErrorIndex;

        public void Execute()
        {
            bool hasAnyAlpha = Header.BackgroundColor.a < byte.MaxValue;
            bool hasPartialAlpha =
                Header.BackgroundColor.a > 0
                && Header.BackgroundColor.a < byte.MaxValue;
            int pixelCount = Pixels.Length;
            for (int i = 0; i < pixelCount; i++)
            {
                byte alpha = Pixels[i].a;
                hasAnyAlpha |= alpha < byte.MaxValue;
                hasPartialAlpha |= alpha > 0 && alpha < byte.MaxValue;
            }

            if ((Header.HasAnyAlpha != 0) != hasAnyAlpha || (Header.HasPartialAlpha != 0) != hasPartialAlpha)
            {
                Errors[ErrorIndex] = (int)BasisAnimationCodecError.InvalidHeader;
            }
        }
    }

    [BurstCompile]
    internal struct BasisAnimationUnpackPixelsJob : IJobFor
    {
        public int SourceOffset;

        [ReadOnly]
        public NativeArray<byte> Raw;

        [WriteOnly]
        public NativeArray<Color32> Pixels;

        public void Execute(int index)
        {
            int offset = SourceOffset + index * 4;
            Pixels[index] = new Color32(Raw[offset], Raw[offset + 1], Raw[offset + 2], Raw[offset + 3]);
        }
    }
}
