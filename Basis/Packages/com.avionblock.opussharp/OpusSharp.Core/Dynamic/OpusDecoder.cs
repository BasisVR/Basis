using System;
using OpusSharp.Core.Interfaces;
using OpusSharp.Core.SafeHandlers;

// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable FieldCanBeMadeReadOnly.Global
// ReSharper disable InconsistentNaming
// ReSharper disable ClassNeverInstantiated.Global
namespace OpusSharp.Core.Dynamic
{
    /// <summary>
    /// An opus decoder using dynamic binding calls.
    /// </summary>
    public class OpusDecoder : IOpusDecoder
    {
        /// <summary>
        /// Direct safe handle for the <see cref="OpusDecoder"/>. IT IS NOT RECOMMENDED TO CLOSE THE HANDLE DIRECTLY! Instead, use <see cref="Dispose(bool)"/> to dispose the handle and object safely.
        /// </summary>
        protected OpusDecoderSafeHandle _handler;

        private bool _disposed;

        /// <summary>
        /// Creates a new opus decoder.
        /// </summary>
        /// <param name="sample_rate">The sample rate, this must be one of 8000, 12000, 16000, 24000, or 48000.</param>
        /// <param name="channels">Number of channels, this must be 1 or 2.</param>
        /// <exception cref="OpusException" />
        public unsafe OpusDecoder(int sample_rate, int channels)
        {
            var error = 0;
            _handler = NativeOpus.opus_decoder_create(sample_rate, channels, &error);
            CheckError(error);
        }

        /// <summary>
        /// Opus decoder destructor.
        /// </summary>
        ~OpusDecoder()
        {
            Dispose(false);
        }
        
        /// <inheritdoc/>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

#if NETSTANDARD2_1_OR_GREATER || NET8_0_OR_GREATER
        /// <inheritdoc/>
        public unsafe int Decode(Span<byte> input, int length, Span<byte> output, int frame_size, bool decode_fec)
        {
            ThrowIfDisposed();

            fixed (byte* inputPtr = input)
            fixed (byte* outputPtr = output)
            {
                var result = NativeOpus.opus_decode(_handler, inputPtr, length, (short*)outputPtr, frame_size,
                    decode_fec ? 1 : 0);
                CheckError(result);
                return result;
            }
        }

        /// <inheritdoc/>
        public unsafe int Decode(Span<byte> input, int length, Span<short> output, int frame_size, bool decode_fec)
        {
            ThrowIfDisposed();

            fixed (byte* inputPtr = input)
            fixed (short* outputPtr = output)
            {
                var result = NativeOpus.opus_decode(_handler, inputPtr, length, outputPtr, frame_size,
                    decode_fec ? 1 : 0);
                CheckError(result);
                return result;
            }
        }

        /// <inheritdoc/>
        public unsafe int Decode(Span<byte> input, int length, Span<int> output, int frame_size, bool decode_fec)
        {
            ThrowIfDisposed();

            fixed (byte* inputPtr = input)
            fixed (int* outputPtr = output)
            {
                var result = NativeOpus.opus_decode24(_handler, inputPtr, length, outputPtr, frame_size,
                    decode_fec ? 1 : 0);
                CheckError(result);
                return result;
            }
        }

        /// <inheritdoc/>
        public unsafe int Decode(Span<byte> input, int length, Span<float> output, int frame_size, bool decode_fec)
        {
            ThrowIfDisposed();

            fixed (byte* inputPtr = input)
            fixed (float* outputPtr = output)
            {
                var result = NativeOpus.opus_decode_float(_handler, inputPtr, length, outputPtr, frame_size,
                    decode_fec ? 1 : 0);
                CheckError(result);
                return result;
            }
        }
#endif

        /// <inheritdoc/>
        public unsafe int Decode(byte[]? input, int length, byte[] output, int frame_size, bool decode_fec)
        {
            ThrowIfDisposed();

            fixed (byte* inputPtr = input)
            fixed (byte* outputPtr = output)
            {
                var result = NativeOpus.opus_decode(_handler, inputPtr, length, (short*)outputPtr, frame_size,
                    decode_fec ? 1 : 0);
                CheckError(result);
                return result;
            }
        }

        /// <inheritdoc/>
        public unsafe int Decode(byte[]? input, int length, short[] output, int frame_size, bool decode_fec)
        {
            ThrowIfDisposed();

            fixed (byte* inputPtr = input)
            fixed (short* outputPtr = output)
            {
                var result = NativeOpus.opus_decode(_handler, inputPtr, length, outputPtr, frame_size,
                    decode_fec ? 1 : 0);
                CheckError(result);
                return result;
            }
        }

        /// <inheritdoc/>
        public unsafe int Decode(byte[]? input, int length, int[] output, int frame_size, bool decode_fec)
        {
            ThrowIfDisposed();

            fixed (byte* inputPtr = input)
            fixed (int* outputPtr = output)
            {
                var result = NativeOpus.opus_decode24(_handler, inputPtr, length, outputPtr, frame_size,
                    decode_fec ? 1 : 0);
                CheckError(result);
                return result;
            }
        }

        /// <inheritdoc/>
        public unsafe int Decode(byte[]? input, int length, float[] output, int frame_size, bool decode_fec)
        {
            ThrowIfDisposed();

            fixed (byte* inputPtr = input)
            fixed (float* outputPtr = output)
            {
                var result = NativeOpus.opus_decode_float(_handler, inputPtr, length, outputPtr, frame_size,
                    decode_fec ? 1 : 0);
                CheckError(result);
                return result;
            }
        }

        /// <inheritdoc/>
        public int Ctl(DecoderCTL request)
        {
            ThrowIfDisposed();
            var result = NativeOpus.opus_decoder_ctl(_handler, (int)request);
            CheckError(result);
            return result;
        }

        /// <inheritdoc/>
        public int Ctl(DecoderCTL request, int value)
        {
            ThrowIfDisposed();
            var result = NativeOpus.opus_decoder_ctl(_handler, (int)request, value);
            CheckError(result);
            return result;
        }

        /// <inheritdoc/>
        public unsafe int Ctl<T>(DecoderCTL request, ref T value) where T : unmanaged
        {
            ThrowIfDisposed();
            fixed (void* valuePtr = &value)
            {
                var result = NativeOpus.opus_decoder_ctl(_handler, (int)request, valuePtr);
                CheckError(result);
                return result;
            }
        }

        /// <inheritdoc/>
        public int Ctl(GenericCTL request)
        {
            ThrowIfDisposed();
            var result = NativeOpus.opus_decoder_ctl(_handler, (int)request);
            CheckError(result);
            return result;
        }

        /// <inheritdoc/>
        public int Ctl(GenericCTL request, int value)
        {
            ThrowIfDisposed();
            var result = NativeOpus.opus_decoder_ctl(_handler, (int)request, value);
            CheckError(result);
            return result;
        }

        /// <inheritdoc/>
        public unsafe int Ctl<T>(GenericCTL request, ref T value) where T : unmanaged
        {
            ThrowIfDisposed();
            fixed (void* valuePtr = &value)
            {
                var result = NativeOpus.opus_decoder_ctl(_handler, (int)request, valuePtr);
                CheckError(result);
                return result;
            }
        }

        /// <summary>
        /// Dispose logic.
        /// </summary>
        /// <param name="disposing">Set to true if fully disposing.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;

            if (disposing)
            {
                if (!_handler.IsClosed)
                    _handler.Close();
            }

            _disposed = true;
        }

        /// <summary>
        /// Throws an exception if this object is disposed or the handler is closed.
        /// </summary>
        /// <exception cref="ObjectDisposedException" />
        protected virtual void ThrowIfDisposed()
        {
            if (_disposed || _handler.IsClosed)
                throw new ObjectDisposedException(GetType().FullName);
        }

        /// <summary>
        /// Checks if there is an opus error and throws if the error is a negative value.
        /// </summary>
        /// <param name="error">The error code to input.</param>
        /// <exception cref="OpusException"></exception>
        protected static void CheckError(int error)
        {
            if (error < 0)
                throw new OpusException(((OpusErrorCodes)error).ToString());
        }
    }
}