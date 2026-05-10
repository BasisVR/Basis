using System;

// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable FieldCanBeMadeReadOnly.Global
// ReSharper disable InconsistentNaming
// ReSharper disable ClassNeverInstantiated.Global
namespace OpusSharp.Core.Interfaces
{
    /// <summary>
    /// An opus decoder interface.
    /// </summary>
    public interface IOpusDecoder : IDisposable
    {
#if NETSTANDARD2_1_OR_GREATER || NET8_0_OR_GREATER
        /// <summary>
        /// Decodes an opus encoded frame.
        /// </summary>
        /// <param name="input">Input payload. Use null to indicate packet loss</param>
        /// <param name="length">Number of bytes in payload.</param>
        /// <param name="output">Output signal (interleaved if 2 channels). length is frame_size*channels*sizeof(short).</param>
        /// <param name="frame_size">Number of samples per channel of available space in pcm. If this is less than the maximum packet duration (120ms; 5760 for 48kHz), this function will not be capable of decoding some packets. In the case of PLC (data==NULL) or FEC (decode_fec=true), then frame_size needs to be exactly the duration of audio that is missing, otherwise the decoder will not be in the optimal state to decode the next incoming packet. For the PLC and FEC cases, frame_size must be a multiple of 2.5 ms.</param>
        /// <param name="decode_fec">Request that any in-band forward error correction data be decoded. If no such data is available, the frame is decoded as if it were lost.</param>
        /// <returns>Number of decoded samples or <see cref="OpusErrorCodes"/>.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        int Decode(Span<byte> input, int length, Span<byte> output, int frame_size, bool decode_fec);

        /// <summary>
        /// Decodes an opus encoded frame.
        /// </summary>
        /// <param name="input">Input payload. Use null to indicate packet loss</param>
        /// <param name="length">Number of bytes in payload.</param>
        /// <param name="output">Output signal (interleaved if 2 channels). length is frame_size*channels.</param>
        /// <param name="frame_size">Number of samples per channel of available space in pcm. If this is less than the maximum packet duration (120ms; 5760 for 48kHz), this function will not be capable of decoding some packets. In the case of PLC (data==NULL) or FEC (decode_fec=true), then frame_size needs to be exactly the duration of audio that is missing, otherwise the decoder will not be in the optimal state to decode the next incoming packet. For the PLC and FEC cases, frame_size must be a multiple of 2.5 ms.</param>
        /// <param name="decode_fec">Request that any in-band forward error correction data be decoded. If no such data is available, the frame is decoded as if it were lost.</param>
        /// <returns>Number of decoded samples or <see cref="OpusErrorCodes"/>.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        int Decode(Span<byte> input, int length, Span<short> output, int frame_size, bool decode_fec);

        /// <summary>
        /// Decodes an opus encoded frame.
        /// </summary>
        /// <param name="input">Input payload. Use null to indicate packet loss</param>
        /// <param name="length">Number of bytes in payload.</param>
        /// <param name="output">Output signal (interleaved if 2 channels). length is frame_size*channels.</param>
        /// <param name="frame_size">Number of samples per channel of available space in pcm. If this is less than the maximum packet duration (120ms; 5760 for 48kHz), this function will not be capable of decoding some packets. In the case of PLC (data==NULL) or FEC (decode_fec=true), then frame_size needs to be exactly the duration of audio that is missing, otherwise the decoder will not be in the optimal state to decode the next incoming packet. For the PLC and FEC cases, frame_size must be a multiple of 2.5 ms.</param>
        /// <param name="decode_fec">Request that any in-band forward error correction data be decoded. If no such data is available, the frame is decoded as if it were lost.</param>
        /// <returns>Number of decoded samples or <see cref="OpusErrorCodes"/>.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        int Decode(Span<byte> input, int length, Span<int> output, int frame_size, bool decode_fec);

        /// <summary>
        /// Decodes an opus encoded frame.
        /// </summary>
        /// <param name="input">Input payload. Use null to indicate packet loss</param>
        /// <param name="length">Number of bytes in payload.</param>
        /// <param name="output">Output signal (interleaved if 2 channels). length is (frame_size*channels)/2. Note: I don't know if this is correct.</param>
        /// <param name="frame_size">Number of samples per channel of available space in pcm. If this is less than the maximum packet duration (120ms; 5760 for 48kHz), this function will not be capable of decoding some packets. In the case of PLC (data==NULL) or FEC (decode_fec=true), then frame_size needs to be exactly the duration of audio that is missing, otherwise the decoder will not be in the optimal state to decode the next incoming packet. For the PLC and FEC cases, frame_size must be a multiple of 2.5 ms.</param>
        /// <param name="decode_fec">Request that any in-band forward error correction data be decoded. If no such data is available, the frame is decoded as if it were lost.</param>
        /// <returns>Number of decoded samples or <see cref="OpusErrorCodes"/>.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        int Decode(Span<byte> input, int length, Span<float> output, int frame_size, bool decode_fec);
#endif

        /// <summary>
        /// Decodes an opus encoded frame.
        /// </summary>
        /// <param name="input">Input payload. Use null to indicate packet loss</param>
        /// <param name="length">Number of bytes in payload.</param>
        /// <param name="output">Output signal (interleaved if 2 channels). length is frame_size*channels*sizeof(short).</param>
        /// <param name="frame_size">Number of samples per channel of available space in pcm. If this is less than the maximum packet duration (120ms; 5760 for 48kHz), this function will not be capable of decoding some packets. In the case of PLC (data==NULL) or FEC (decode_fec=true), then frame_size needs to be exactly the duration of audio that is missing, otherwise the decoder will not be in the optimal state to decode the next incoming packet. For the PLC and FEC cases, frame_size must be a multiple of 2.5 ms.</param>
        /// <param name="decode_fec">Request that any in-band forward error correction data be decoded. If no such data is available, the frame is decoded as if it were lost.</param>
        /// <returns>Number of decoded samples or <see cref="OpusErrorCodes"/>.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        int Decode(byte[]? input, int length, byte[] output, int frame_size, bool decode_fec);

        /// <summary>
        /// Decodes an opus encoded frame.
        /// </summary>
        /// <param name="input">Input payload. Use null to indicate packet loss</param>
        /// <param name="length">Number of bytes in payload.</param>
        /// <param name="output">Output signal (interleaved if 2 channels). length is frame_size*channels.</param>
        /// <param name="frame_size">Number of samples per channel of available space in pcm. If this is less than the maximum packet duration (120ms; 5760 for 48kHz), this function will not be capable of decoding some packets. In the case of PLC (data==NULL) or FEC (decode_fec=true), then frame_size needs to be exactly the duration of audio that is missing, otherwise the decoder will not be in the optimal state to decode the next incoming packet. For the PLC and FEC cases, frame_size must be a multiple of 2.5 ms.</param>
        /// <param name="decode_fec">Request that any in-band forward error correction data be decoded. If no such data is available, the frame is decoded as if it were lost.</param>
        /// <returns>Number of decoded samples or <see cref="OpusErrorCodes"/>.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        int Decode(byte[]? input, int length, short[] output, int frame_size, bool decode_fec);

        /// <summary>
        /// Decodes an opus encoded frame.
        /// </summary>
        /// <param name="input">Input payload. Use null to indicate packet loss</param>
        /// <param name="length">Number of bytes in payload.</param>
        /// <param name="output">Output signal (interleaved if 2 channels). length is frame_size*channels.</param>
        /// <param name="frame_size">Number of samples per channel of available space in pcm. If this is less than the maximum packet duration (120ms; 5760 for 48kHz), this function will not be capable of decoding some packets. In the case of PLC (data==NULL) or FEC (decode_fec=true), then frame_size needs to be exactly the duration of audio that is missing, otherwise the decoder will not be in the optimal state to decode the next incoming packet. For the PLC and FEC cases, frame_size must be a multiple of 2.5 ms.</param>
        /// <param name="decode_fec">Request that any in-band forward error correction data be decoded. If no such data is available, the frame is decoded as if it were lost.</param>
        /// <returns>Number of decoded samples or <see cref="OpusErrorCodes"/>.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        int Decode(byte[]? input, int length, int[] output, int frame_size, bool decode_fec);

        /// <summary>
        /// Decodes an opus encoded frame.
        /// </summary>
        /// <param name="input">Input payload. Use null to indicate packet loss</param>
        /// <param name="length">Number of bytes in payload.</param>
        /// <param name="output">Output signal (interleaved if 2 channels). length is (frame_size*channels)/2. Note: I don't know if this is correct.</param>
        /// <param name="frame_size">Number of samples per channel of available space in pcm. If this is less than the maximum packet duration (120ms; 5760 for 48kHz), this function will not be capable of decoding some packets. In the case of PLC (data==NULL) or FEC (decode_fec=true), then frame_size needs to be exactly the duration of audio that is missing, otherwise the decoder will not be in the optimal state to decode the next incoming packet. For the PLC and FEC cases, frame_size must be a multiple of 2.5 ms.</param>
        /// <param name="decode_fec">Request that any in-band forward error correction data be decoded. If no such data is available, the frame is decoded as if it were lost.</param>
        /// <returns>Number of decoded samples or <see cref="OpusErrorCodes"/>.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        int Decode(byte[]? input, int length, float[] output, int frame_size, bool decode_fec);

        /// <summary>
        /// Performs a ctl request.
        /// </summary>
        /// <param name="request">The request you want to specify.</param>
        /// <returns>The result code of the request. See <see cref="OpusErrorCodes"/>.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        int Ctl(DecoderCTL request);

        /// <summary>
        /// Performs a ctl set request.
        /// </summary>
        /// <param name="request">The request you want to specify.</param>
        /// <param name="value">The input value.</param>
        /// <returns>The result code of the request. See <see cref="OpusErrorCodes"/>.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        int Ctl(DecoderCTL request, int value);

        /// <summary>
        /// Performs a ctl get/set request.
        /// </summary>
        /// <typeparam name="T">The type you want to input/output.</typeparam>
        /// <param name="request">The request you want to specify.</param>
        /// <param name="value">The input/output value.</param>
        /// <returns>The result code of the request. See <see cref="OpusErrorCodes"/>.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        int Ctl<T>(DecoderCTL request, ref T value) where T : unmanaged;

        /// <summary>
        /// Performs a ctl request.
        /// </summary>
        /// <param name="request">The request you want to specify.</param>
        /// <returns>The result code of the request. See <see cref="OpusErrorCodes"/>.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        int Ctl(GenericCTL request);

        /// <summary>
        /// Performs a ctl set request.
        /// </summary>
        /// <param name="request">The request you want to specify.</param>
        /// <param name="value">The input value.</param>
        /// <returns>The result code of the request. See <see cref="OpusErrorCodes"/>.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        int Ctl(GenericCTL request, int value);

        /// <summary>
        /// Performs a ctl request.
        /// </summary>
        /// <typeparam name="T">The type you want to input/output.</typeparam>
        /// <param name="request">The request you want to specify.</param>
        /// <param name="value">The input/output value.</param>
        /// <returns>The result code of the request. See <see cref="OpusErrorCodes"/>.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        int Ctl<T>(GenericCTL request, ref T value) where T : unmanaged;
    }
}