using OpusSharp.Core.SafeHandlers;
using System;

// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable FieldCanBeMadeReadOnly.Global
// ReSharper disable InconsistentNaming
namespace OpusSharp.Core
{
    /// <summary>
    /// An opus decoder.
    /// </summary>
    public class OpusDecoder : IDisposable
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

        /// <summary>
        /// Decodes an opus encoded frame.
        /// </summary>
        /// <param name="input">Input payload. Use null to indicate packet loss</param>
        /// <param name="length">Number of bytes in payload.</param>
        /// <param name="output">Output signal (interleaved if 2 channels). length is frame_size*channels*sizeof(short).</param>
        /// <param name="frame_size">Number of samples per channel of available space in pcm. If this is less than the maximum packet duration (120ms; 5760 for 48kHz), this function will not be capable of decoding some packets. In the case of PLC (data==NULL) or FEC (decode_fec=true), then frame_size needs to be exactly the duration of audio that is missing, otherwise the decoder will not be in the optimal state to decode the next incoming packet. For the PLC and FEC cases, frame_size must be a multiple of 2.5 ms.</param>
        /// <param name="decode_fec">Request that any in-band forward error correction data be decoded. If no such data is available, the frame is decoded as if it were lost.</param>
        /// <returns>Number of decoded samples or <see cref="OpusErrorCodes"/>.</returns>
        public unsafe int Decode(Span<byte> input, int length, Span<byte> output, int frame_size, bool decode_fec)
        {
            ThrowIfDisposed();

            fixed (byte* inputPtr = input)
            fixed (byte* outputPtr = output)
            {
                var result = NativeOpus.opus_decode(_handler, inputPtr, length, (short*)outputPtr, frame_size, decode_fec ? 1 : 0);
                CheckError(result);
                return result;
            }
        }

        /// <summary>
        /// Decodes an opus encoded frame.
        /// </summary>
        /// <param name="input">Input payload. Use null to indicate packet loss</param>
        /// <param name="length">Number of bytes in payload.</param>
        /// <param name="output">Output signal (interleaved if 2 channels). length is frame_size*channels.</param>
        /// <param name="frame_size">Number of samples per channel of available space in pcm. If this is less than the maximum packet duration (120ms; 5760 for 48kHz), this function will not be capable of decoding some packets. In the case of PLC (data==NULL) or FEC (decode_fec=true), then frame_size needs to be exactly the duration of audio that is missing, otherwise the decoder will not be in the optimal state to decode the next incoming packet. For the PLC and FEC cases, frame_size must be a multiple of 2.5 ms.</param>
        /// <param name="decode_fec">Request that any in-band forward error correction data be decoded. If no such data is available, the frame is decoded as if it were lost.</param>
        /// <returns>Number of decoded samples or <see cref="OpusErrorCodes"/>.</returns>
        public unsafe int Decode(Span<byte> input, int length, Span<short> output, int frame_size, bool decode_fec)
        {
            ThrowIfDisposed();

            fixed (byte* inputPtr = input)
            fixed (short* outputPtr = output)
            {
                var result = NativeOpus.opus_decode(_handler, inputPtr, length, outputPtr, frame_size, decode_fec ? 1 : 0);
                CheckError(result);
                return result;
            }
        }

        /// <summary>
        /// Decodes an opus encoded frame.
        /// </summary>
        /// <param name="input">Input payload. Use null to indicate packet loss</param>
        /// <param name="length">Number of bytes in payload.</param>
        /// <param name="output">Output signal (interleaved if 2 channels). length is (frame_size*channels)/2. Note: I don't know if this is correct.</param>
        /// <param name="frame_size">Number of samples per channel of available space in pcm. If this is less than the maximum packet duration (120ms; 5760 for 48kHz), this function will not be capable of decoding some packets. In the case of PLC (data==NULL) or FEC (decode_fec=true), then frame_size needs to be exactly the duration of audio that is missing, otherwise the decoder will not be in the optimal state to decode the next incoming packet. For the PLC and FEC cases, frame_size must be a multiple of 2.5 ms.</param>
        /// <param name="decode_fec">Request that any in-band forward error correction data be decoded. If no such data is available, the frame is decoded as if it were lost.</param>
        /// <returns>Number of decoded samples or <see cref="OpusErrorCodes"/>.</returns>
        public unsafe int Decode(Span<byte> input, int length, Span<float> output, int frame_size, bool decode_fec)
        {
            ThrowIfDisposed();

            fixed (byte* inputPtr = input)
            fixed (float* outputPtr = output)
            {
                var result = NativeOpus.opus_decode_float(_handler, inputPtr, length, outputPtr, frame_size, decode_fec ? 1 : 0);
                CheckError(result);
                return result;
            }
        }

        /// <summary>
        /// Decodes an opus encoded frame.
        /// </summary>
        /// <param name="input">Input payload. Use null to indicate packet loss</param>
        /// <param name="length">Number of bytes in payload.</param>
        /// <param name="output">Output signal (interleaved if 2 channels). length is frame_size*channels</param>
        /// <param name="frame_size">Number of samples per channel of available space in pcm. If this is less than the maximum packet duration (120ms; 5760 for 48kHz), this function will not be capable of decoding some packets. In the case of PLC (data==NULL) or FEC (decode_fec=true), then frame_size needs to be exactly the duration of audio that is missing, otherwise the decoder will not be in the optimal state to decode the next incoming packet. For the PLC and FEC cases, frame_size must be a multiple of 2.5 ms.</param>
        /// <param name="decode_fec">Request that any in-band forward error correction data be decoded. If no such data is available, the frame is decoded as if it were lost.</param>
        /// <returns>Number of decoded samples or <see cref="OpusErrorCodes"/>.</returns>
        public int Decode(byte[]? input, int length, byte[] output, int frame_size, bool decode_fec) =>
            Decode(input.AsSpan(), length, output.AsSpan(), frame_size, decode_fec);

        /// <summary>
        /// Decodes an opus encoded frame.
        /// </summary>
        /// <param name="input">Input payload. Use null to indicate packet loss</param>
        /// <param name="length">Number of bytes in payload.</param>
        /// <param name="output">Output signal (interleaved if 2 channels). length is frame_size*channels*sizeof(short)</param>
        /// <param name="frame_size">Number of samples per channel of available space in pcm. If this is less than the maximum packet duration (120ms; 5760 for 48kHz), this function will not be capable of decoding some packets. In the case of PLC (data==NULL) or FEC (decode_fec=true), then frame_size needs to be exactly the duration of audio that is missing, otherwise the decoder will not be in the optimal state to decode the next incoming packet. For the PLC and FEC cases, frame_size must be a multiple of 2.5 ms.</param>
        /// <param name="decode_fec">Request that any in-band forward error correction data be decoded. If no such data is available, the frame is decoded as if it were lost.</param>
        /// <returns>Number of decoded samples or <see cref="OpusErrorCodes"/>.</returns>
        public int Decode(byte[]? input, int length, short[] output, int frame_size, bool decode_fec) =>
            Decode(input.AsSpan(), length, output.AsSpan(), frame_size, decode_fec);

        /// <summary>
        /// Decodes an opus encoded frame.
        /// </summary>
        /// <param name="input">Input payload. Use null to indicate packet loss</param>
        /// <param name="length">Number of bytes in payload.</param>
        /// <param name="output">Output signal (interleaved if 2 channels). length is frame_size*channels*sizeof(float)</param>
        /// <param name="frame_size">Number of samples per channel of available space in pcm. If this is less than the maximum packet duration (120ms; 5760 for 48kHz), this function will not be capable of decoding some packets. In the case of PLC (data==NULL) or FEC (decode_fec=true), then frame_size needs to be exactly the duration of audio that is missing, otherwise the decoder will not be in the optimal state to decode the next incoming packet. For the PLC and FEC cases, frame_size must be a multiple of 2.5 ms.</param>
        /// <param name="decode_fec">Request that any in-band forward error correction data be decoded. If no such data is available, the frame is decoded as if it were lost.</param>
        /// <returns>Number of decoded samples or <see cref="OpusErrorCodes"/>.</returns>
        public int Decode(byte[]? input, int length, float[] output, int frame_size, bool decode_fec) =>
            Decode(input.AsSpan(), length, output.AsSpan(), frame_size, decode_fec);

        /// <summary>
        /// Performs a ctl request.
        /// </summary>
        /// <param name="request">The request you want to specify.</param>
        /// <returns>The result code of the request. See <see cref="OpusErrorCodes"/>.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public int Ctl(DecoderCTL request)
        {
            ThrowIfDisposed();
            var result = NativeOpus.opus_decoder_ctl(_handler, (int)request);
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
        public int Ctl(DecoderCTL request, int value)
        {
            ThrowIfDisposed();
            var result = NativeOpus.opus_decoder_ctl(_handler, (int)request, value);
            CheckError(result);
            return result;
        }

        /// <summary>
        /// Performs a ctl get/set request.
        /// </summary>
        /// <typeparam name="T">The type you want to input/output.</typeparam>
        /// <param name="request">The request you want to specify.</param>
        /// <param name="value">The input/output value.</param>
        /// <returns>The result code of the request. See <see cref="OpusErrorCodes"/>.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
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
            var result = NativeOpus.opus_decoder_ctl(_handler, (int)request);
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
            var result = NativeOpus.opus_decoder_ctl(_handler, (int)request, value);
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
                var result = NativeOpus.opus_decoder_ctl(_handler, (int)request, valuePtr);
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