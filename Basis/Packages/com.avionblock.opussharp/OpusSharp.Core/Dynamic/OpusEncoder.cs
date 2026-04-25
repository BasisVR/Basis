using OpusSharp.Core.SafeHandlers;
using System;
using OpusSharp.Core.Interfaces;

// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable FieldCanBeMadeReadOnly.Global
// ReSharper disable InconsistentNaming
namespace OpusSharp.Core.Dynamic
{
    /// <summary>
    /// An opus encoder using dynamic binding calls.
    /// </summary>
    public class OpusEncoder : IOpusEncoder
    {
        /// <summary>
        /// Direct safe handle for the <see cref="OpusEncoder"/>. IT IS NOT RECOMMENDED TO CLOSE THE HANDLE DIRECTLY! Instead, use <see cref="Dispose(bool)"/> to dispose the handle and object safely.
        /// </summary>
        protected OpusEncoderSafeHandle _handler;

        private bool _disposed;

        /// <summary>
        /// Creates a new opus encoder.
        /// </summary>
        /// <param name="sample_rate">The sample rate, this must be one of 8000, 12000, 16000, 24000, or 48000.</param>
        /// <param name="channels">Number of channels, this must be 1 or 2.</param>
        /// <param name="application">Coding mode (one of <see cref="OpusPredefinedValues.OPUS_APPLICATION_VOIP"/>, <see cref="OpusPredefinedValues.OPUS_APPLICATION_AUDIO"/> or <see cref="OpusPredefinedValues.OPUS_APPLICATION_RESTRICTED_LOWDELAY"/></param>
        /// <exception cref="OpusException" />
        public unsafe OpusEncoder(int sample_rate, int channels, OpusPredefinedValues application)
        {
            var error = 0;
            _handler = NativeOpus.opus_encoder_create(sample_rate, channels, (int)application, &error);
            CheckError(error);
        }

        /// <summary>
        /// Opus encoder destructor.
        /// </summary>
        ~OpusEncoder()
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
        public unsafe int Encode(Span<byte> input, int frame_size, Span<byte> output, int max_data_bytes)
        {
            ThrowIfDisposed();
            fixed (byte* inputPtr = input)
            fixed (byte* outputPtr = output)
            {
                var result = NativeOpus.opus_encode(_handler, (short*)inputPtr, frame_size, outputPtr,
                    max_data_bytes);
                CheckError(result);
                return result;
            }
        }

        /// <inheritdoc/>
        public unsafe int Encode(Span<short> input, int frame_size, Span<byte> output, int max_data_bytes)
        {
            ThrowIfDisposed();
            fixed (short* inputPtr = input)
            fixed (byte* outputPtr = output)
            {
                var result = NativeOpus.opus_encode(_handler, inputPtr, frame_size, outputPtr, max_data_bytes);
                CheckError(result);
                return result;
            }
        }

        /// <inheritdoc/>
        public unsafe int Encode(Span<int> input, int frame_size, Span<byte> output, int max_data_bytes)
        {
            ThrowIfDisposed();
            fixed (int* inputPtr = input)
            fixed (byte* outputPtr = output)
            {
                var result = NativeOpus.opus_encode24(_handler, inputPtr, frame_size, outputPtr,
                    max_data_bytes);
                CheckError(result);
                return result;
            }
        }

        /// <inheritdoc/>
        public unsafe int Encode(Span<float> input, int frame_size, Span<byte> output, int max_data_bytes)
        {
            ThrowIfDisposed();
            fixed (float* inputPtr = input)
            fixed (byte* outputPtr = output)
            {
                var result = NativeOpus.opus_encode_float(_handler, inputPtr, frame_size, outputPtr,
                    max_data_bytes);
                CheckError(result);
                return result;
            }
        }
#endif

        /// <inheritdoc/>
        public unsafe int Encode(byte[] input, int frame_size, byte[] output, int max_data_bytes)
        {
            ThrowIfDisposed();
            fixed (byte* inputPtr = input)
            fixed (byte* outputPtr = output)
            {
                var result = NativeOpus.opus_encode(_handler, (short*)inputPtr, frame_size, outputPtr,
                    max_data_bytes);
                CheckError(result);
                return result;
            }
        }

        /// <inheritdoc/>
        public unsafe int Encode(short[] input, int frame_size, byte[] output, int max_data_bytes)
        {
            ThrowIfDisposed();
            fixed (short* inputPtr = input)
            fixed (byte* outputPtr = output)
            {
                var result = NativeOpus.opus_encode(_handler, inputPtr, frame_size, outputPtr, max_data_bytes);
                CheckError(result);
                return result;
            }
        }

        /// <inheritdoc/>
        public unsafe int Encode(int[] input, int frame_size, byte[] output, int max_data_bytes)
        {
            ThrowIfDisposed();
            fixed (int* inputPtr = input)
            fixed (byte* outputPtr = output)
            {
                var result = NativeOpus.opus_encode24(_handler, inputPtr, frame_size, outputPtr,
                    max_data_bytes);
                CheckError(result);
                return result;
            }
        }

        /// <inheritdoc/>
        public unsafe int Encode(float[] input, int frame_size, byte[] output, int max_data_bytes)
        {
            ThrowIfDisposed();
            fixed (float* inputPtr = input)
            fixed (byte* outputPtr = output)
            {
                var result = NativeOpus.opus_encode_float(_handler, inputPtr, frame_size, outputPtr,
                    max_data_bytes);
                CheckError(result);
                return result;
            }
        }

        /// <inheritdoc/>
        public int Ctl(EncoderCTL request)
        {
            ThrowIfDisposed();
            var result = NativeOpus.opus_encoder_ctl(_handler, (int)request);
            CheckError(result);
            return result;
        }

        /// <inheritdoc/>
        public int Ctl(EncoderCTL request, int value)
        {
            ThrowIfDisposed();
            var result = NativeOpus.opus_encoder_ctl(_handler, (int)request, value);
            CheckError(result);
            return result;
        }

        /// <inheritdoc/>
        public unsafe int Ctl<T>(EncoderCTL request, ref T value) where T : unmanaged
        {
            ThrowIfDisposed();
            fixed (void* valuePtr = &value)
            {
                var result = NativeOpus.opus_encoder_ctl(_handler, (int)request, valuePtr);
                CheckError(result);
                return result;
            }
        }

        /// <inheritdoc/>
        public unsafe int Ctl<T>(EncoderCTL request, ref T value, int value2) where T : unmanaged
        {
            ThrowIfDisposed();
            fixed (void* valuePtr = &value)
            {
                var result = NativeOpus.opus_encoder_ctl(_handler, (int)request, valuePtr, value2);
                CheckError(result);
                return result;
            }
        }

        /// <inheritdoc/>
        public unsafe int Ctl<T>(EncoderCTL request, int value, ref T value2) where T : unmanaged
        {
            ThrowIfDisposed();
            fixed (void* value2Ptr = &value2)
            {
                var result = NativeOpus.opus_encoder_ctl(_handler, (int)request, value, value2Ptr);
                CheckError(result);
                return result;
            }
        }

        /// <inheritdoc/>
        public unsafe int Ctl<T, T2>(EncoderCTL request, ref T value, ref T2 value2)
            where T : unmanaged where T2 : unmanaged
        {
            ThrowIfDisposed();
            fixed (void* valuePtr = &value)
            fixed (void* value2Ptr = &value2)
            {
                var result = NativeOpus.opus_encoder_ctl(_handler, (int)request, valuePtr, value2Ptr);
                CheckError(result);
                return result;
            }
        }

        /// <inheritdoc/>
        public int Ctl(GenericCTL request)
        {
            ThrowIfDisposed();
            var result = NativeOpus.opus_encoder_ctl(_handler, (int)request);
            CheckError(result);
            return result;
        }

        /// <inheritdoc/>
        public int Ctl(GenericCTL request, int value)
        {
            ThrowIfDisposed();
            var result = NativeOpus.opus_encoder_ctl(_handler, (int)request, value);
            CheckError(result);
            return result;
        }

        /// <inheritdoc/>
        public unsafe int Ctl<T>(GenericCTL request, ref T value) where T : unmanaged
        {
            ThrowIfDisposed();
            fixed (void* valuePtr = &value)
            {
                var result = NativeOpus.opus_encoder_ctl(_handler, (int)request, valuePtr);
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