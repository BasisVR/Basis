using OpusSharp.Core.SafeHandlers;
using System;

// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable FieldCanBeMadeReadOnly.Global
// ReSharper disable InconsistentNaming
namespace OpusSharp.Core
{
    /// <summary>
    /// An opus encoder.
    /// </summary>
    public class OpusEncoder : IDisposable
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

        /// <summary>
        /// Encodes a pcm frame.
        /// </summary>
        /// <param name="input">Input signal (interleaved if 2 channels). length is frame_size*channels.</param>
        /// <param name="frame_size">The frame size of the pcm data. This must be an Opus frame size for the encoder's sampling rate. For example, at 48 kHz the permitted values are 120, 240, 480, 960, 1920, and 2880. Passing in a duration of less than 10 ms (480 samples at 48 kHz) will prevent the encoder from using the LPC or hybrid modes.</param>
        /// <param name="output">Output payload. This must contain storage for at least max_data_bytes.</param>
        /// <param name="max_data_bytes">Size of the allocated memory for the output payload. This may be used to impose an upper limit on the instant bitrate, but should not be used as the only bitrate control. Use <see cref="EncoderCTL.OPUS_SET_BITRATE"/> to control the bitrate.</param>
        /// <returns>The length of the encoded packet (in bytes).</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public unsafe int Encode(Span<byte> input, int frame_size, Span<byte> output, int max_data_bytes)
        {
            ThrowIfDisposed();
            fixed (byte* inputPtr = input)
            fixed (byte* outputPtr = output)
            {
                var result = NativeOpus.opus_encode(_handler, (short*)inputPtr, frame_size, outputPtr, max_data_bytes);
                CheckError(result);
                return result;
            }
        }

        /// <summary>
        /// Encodes a pcm frame.
        /// </summary>
        /// <param name="input">Input signal (interleaved if 2 channels). length is frame_size*channels*sizeof(short).</param>
        /// <param name="frame_size">The frame size of the pcm data. This must be an Opus frame size for the encoder's sampling rate. For example, at 48 kHz the permitted values are 120, 240, 480, 960, 1920, and 2880. Passing in a duration of less than 10 ms (480 samples at 48 kHz) will prevent the encoder from using the LPC or hybrid modes.</param>
        /// <param name="output">Output payload. This must contain storage for at least max_data_bytes.</param>
        /// <param name="max_data_bytes">Size of the allocated memory for the output payload. This may be used to impose an upper limit on the instant bitrate, but should not be used as the only bitrate control. Use <see cref="EncoderCTL.OPUS_SET_BITRATE"/> to control the bitrate.</param>
        /// <returns>The length of the encoded packet (in bytes).</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
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

        /// <summary>
        /// Encodes a floating point pcm frame.
        /// </summary>
        /// <param name="input">Input signal (interleaved if 2 channels). length is frame_size*channels*sizeof(float).</param>
        /// <param name="frame_size">The frame size of the pcm data. This must be an Opus frame size for the encoder's sampling rate. For example, at 48 kHz the permitted values are 120, 240, 480, 960, 1920, and 2880. Passing in a duration of less than 10 ms (480 samples at 48 kHz) will prevent the encoder from using the LPC or hybrid modes.</param>
        /// <param name="output">Output payload. This must contain storage for at least max_data_bytes.</param>
        /// <param name="max_data_bytes">Size of the allocated memory for the output payload. This may be used to impose an upper limit on the instant bitrate, but should not be used as the only bitrate control. Use <see cref="EncoderCTL.OPUS_SET_BITRATE"/> to control the bitrate.</param>
        /// <returns>The length of the encoded packet (in bytes).</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public unsafe int Encode(Span<float> input, int frame_size, Span<byte> output, int max_data_bytes)
        {
            ThrowIfDisposed();
            fixed (float* inputPtr = input)
            fixed (byte* outputPtr = output)
            {
                var result = NativeOpus.opus_encode_float(_handler, inputPtr, frame_size, outputPtr, max_data_bytes);
                CheckError(result);
                return result;
            }
        }

        /// <summary>
        /// Encodes a pcm frame.
        /// </summary>
        /// <param name="input">Input signal (interleaved if 2 channels). length is frame_size*channels.</param>
        /// <param name="frame_size">The frame size of the pcm data. This must be an Opus frame size for the encoder's sampling rate. For example, at 48 kHz the permitted values are 120, 240, 480, 960, 1920, and 2880. Passing in a duration of less than 10 ms (480 samples at 48 kHz) will prevent the encoder from using the LPC or hybrid modes.</param>
        /// <param name="output">Output payload. This must contain storage for at least max_data_bytes.</param>
        /// <param name="max_data_bytes">Size of the allocated memory for the output payload. This may be used to impose an upper limit on the instant bitrate, but should not be used as the only bitrate control. Use <see cref="EncoderCTL.OPUS_SET_BITRATE"/> to control the bitrate.</param>
        /// <returns>The length of the encoded packet (in bytes).</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public int Encode(byte[] input, int frame_size, byte[] output, int max_data_bytes) => Encode(input.AsSpan(), frame_size, output.AsSpan(), max_data_bytes);

        /// <summary>
        /// Encodes a pcm frame.
        /// </summary>
        /// <param name="input">Input signal (interleaved if 2 channels). length is frame_size*channels*sizeof(short).</param>
        /// <param name="frame_size">The frame size of the pcm data. This must be an Opus frame size for the encoder's sampling rate. For example, at 48 kHz the permitted values are 120, 240, 480, 960, 1920, and 2880. Passing in a duration of less than 10 ms (480 samples at 48 kHz) will prevent the encoder from using the LPC or hybrid modes.</param>
        /// <param name="output">Output payload. This must contain storage for at least max_data_bytes.</param>
        /// <param name="max_data_bytes">Size of the allocated memory for the output payload. This may be used to impose an upper limit on the instant bitrate, but should not be used as the only bitrate control. Use <see cref="EncoderCTL.OPUS_SET_BITRATE"/> to control the bitrate.</param>
        /// <returns>The length of the encoded packet (in bytes).</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public int Encode(short[] input, int frame_size, byte[] output, int max_data_bytes) => Encode(input.AsSpan(), frame_size, output.AsSpan(), max_data_bytes);

        /// <summary>
        /// Encodes a floating point pcm frame.
        /// </summary>
        /// <param name="input">Input signal (interleaved if 2 channels). length is frame_size*channels*sizeof(float).</param>
        /// <param name="frame_size">The frame size of the pcm data. This must be an Opus frame size for the encoder's sampling rate. For example, at 48 kHz the permitted values are 120, 240, 480, 960, 1920, and 2880. Passing in a duration of less than 10 ms (480 samples at 48 kHz) will prevent the encoder from using the LPC or hybrid modes.</param>
        /// <param name="output">Output payload. This must contain storage for at least max_data_bytes.</param>
        /// <param name="max_data_bytes">Size of the allocated memory for the output payload. This may be used to impose an upper limit on the instant bitrate, but should not be used as the only bitrate control. Use <see cref="EncoderCTL.OPUS_SET_BITRATE"/> to control the bitrate.</param>
        /// <returns>The length of the encoded packet (in bytes).</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public int Encode(float[] input, int frame_size, byte[] output, int max_data_bytes) => Encode(input.AsSpan(), frame_size, output.AsSpan(), max_data_bytes);

        /// <summary>
        /// Performs a ctl request.
        /// </summary>
        /// <param name="request">The request you want to specify.</param>
        /// <returns>The result code of the request. See <see cref="OpusErrorCodes"/>.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public int Ctl(EncoderCTL request)
        {
            ThrowIfDisposed();
            var result = NativeOpus.opus_encoder_ctl(_handler, (int)request);
            CheckError(result);
            return result;
        }
        
        /// <summary>
        /// Performs a ctl request.
        /// </summary>
        /// <param name="request">The request you want to specify.</param>
        /// /// <param name="value">The input value.</param>
        /// <returns>The result code of the request. See <see cref="OpusErrorCodes"/>.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public int Ctl(EncoderCTL request, int value)
        {
            ThrowIfDisposed();
            var result = NativeOpus.opus_encoder_ctl(_handler, (int)request, value);
            CheckError(result);
            return result;
        }
        
        /// <summary>
        /// Performs a ctl request.
        /// </summary>
        /// <typeparam name="T">The type you want to input/output.</typeparam>
        /// <param name="request">The request you want to specify.</param>
        /// <param name="value">The input/output value.</param>
        /// <returns>The result code of the request. See <see cref="OpusErrorCodes"/>.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
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
        
        /// <summary>
        /// Performs a ctl request.
        /// </summary>
        /// <typeparam name="T">The type you want to input/output.</typeparam>
        /// <param name="request">The request you want to specify.</param>
        /// <param name="value">The input/output value.</param>
        /// <param name="value2">The second input value.</param>
        /// <returns>The result code of the request. See <see cref="OpusErrorCodes"/>.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public unsafe int Ctl<T>(EncoderCTL request, ref T value, int value2) 
            where T : unmanaged
        {
            ThrowIfDisposed();
            fixed (void* valuePtr = &value)
            {
                var result = NativeOpus.opus_encoder_ctl(_handler, (int)request, valuePtr, value2);
                CheckError(result);
                return result;
            }
        }
        
        /// <summary>
        /// Performs a ctl request.
        /// </summary>
        /// <typeparam name="T">The type you want to input/output.</typeparam>
        /// <param name="request">The request you want to specify.</param>
        /// <param name="value">The input value.</param>
        /// <param name="value2">The second input/output value.</param>
        /// <returns>The result code of the request. See <see cref="OpusErrorCodes"/>.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public unsafe int Ctl<T>(EncoderCTL request, int value, ref T value2) 
            where T : unmanaged
        {
            ThrowIfDisposed();
            fixed (void* value2Ptr = &value2)
            {
                var result = NativeOpus.opus_encoder_ctl(_handler, (int)request, value, value2Ptr);
                CheckError(result);
                return result;
            }
        }

        /// <summary>
        /// Performs a ctl request.
        /// </summary>
        /// <typeparam name="T">The type you want to input/output.</typeparam>        
        /// <typeparam name="T2">The second type you want to input/output.</typeparam>
        /// <param name="request">The request you want to specify.</param>
        /// <param name="value">The input/output value.</param>
        /// <param name="value2">The second input/output value.</param>
        /// <returns>The result code of the request. See <see cref="OpusErrorCodes"/>.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public unsafe int Ctl<T, T2>(EncoderCTL request, ref T value, ref T2 value2) 
            where T : unmanaged
            where T2 : unmanaged
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

        /// <summary>
        /// Performs a ctl request.
        /// </summary>
        /// <param name="request">The request you want to specify.</param>
        /// <returns>The result code of the request. See <see cref="OpusErrorCodes"/>.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public int Ctl(GenericCTL request)
        {
            ThrowIfDisposed();
            var result = NativeOpus.opus_encoder_ctl(_handler, (int)request);
            CheckError(result);
            return result;
        }
        
        /// <summary>
        /// Performs a ctl set request.
        /// </summary>
        /// <param name="request">The request you want to specify.</param>
        /// <param name="value">The input value.</param>
        /// <returns>The result code of the request. See <see cref="OpusErrorCodes"/>.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public int Ctl(GenericCTL request, int value)
        {
            ThrowIfDisposed();
            var result = NativeOpus.opus_encoder_ctl(_handler, (int)request, value);
            CheckError(result);
            return result;
        }

        /// <summary>
        /// Performs a ctl request.
        /// </summary>
        /// <typeparam name="T">The type you want to input/output.</typeparam>
        /// <param name="request">The request you want to specify.</param>
        /// <param name="value">The input/output value.</param>
        /// <returns>The result code of the request. See <see cref="OpusErrorCodes"/>.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
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

        /// <inheritdoc/>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
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
        protected void CheckError(int error)
        {
            if (error < 0)
                throw new OpusException(((OpusErrorCodes)error).ToString());
        }
    }
}
