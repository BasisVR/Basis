using System;

// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable FieldCanBeMadeReadOnly.Global
// ReSharper disable InconsistentNaming
// ReSharper disable ClassNeverInstantiated.Global
namespace OpusSharp.Core.Interfaces
{
    /// <summary>
    /// An opus encoder interface.
    /// </summary>
    public interface IOpusEncoder : IDisposable
    {
#if NETSTANDARD2_1_OR_GREATER || NET8_0_OR_GREATER
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
        int Encode(Span<byte> input, int frame_size, Span<byte> output, int max_data_bytes);

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
        int Encode(Span<short> input, int frame_size, Span<byte> output, int max_data_bytes);

        /// <summary>
        /// Encodes a pcm frame.
        /// </summary>
        /// <param name="input">Input signal (interleaved if 2 channels). length is frame_size*channels*sizeof(int).</param>
        /// <param name="frame_size">The frame size of the pcm data. This must be an Opus frame size for the encoder's sampling rate. For example, at 48 kHz the permitted values are 120, 240, 480, 960, 1920, and 2880. Passing in a duration of less than 10 ms (480 samples at 48 kHz) will prevent the encoder from using the LPC or hybrid modes.</param>
        /// <param name="output">Output payload. This must contain storage for at least max_data_bytes.</param>
        /// <param name="max_data_bytes">Size of the allocated memory for the output payload. This may be used to impose an upper limit on the instant bitrate, but should not be used as the only bitrate control. Use <see cref="EncoderCTL.OPUS_SET_BITRATE"/> to control the bitrate.</param>
        /// <returns>The length of the encoded packet (in bytes).</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        int Encode(Span<int> input, int frame_size, Span<byte> output, int max_data_bytes);

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
        int Encode(Span<float> input, int frame_size, Span<byte> output, int max_data_bytes);
#endif

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
        int Encode(byte[] input, int frame_size, byte[] output, int max_data_bytes);

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
        int Encode(short[] input, int frame_size, byte[] output, int max_data_bytes);

        /// <summary>
        /// Encodes a pcm frame.
        /// </summary>
        /// <param name="input">Input signal (interleaved if 2 channels). length is frame_size*channels*sizeof(int).</param>
        /// <param name="frame_size">The frame size of the pcm data. This must be an Opus frame size for the encoder's sampling rate. For example, at 48 kHz the permitted values are 120, 240, 480, 960, 1920, and 2880. Passing in a duration of less than 10 ms (480 samples at 48 kHz) will prevent the encoder from using the LPC or hybrid modes.</param>
        /// <param name="output">Output payload. This must contain storage for at least max_data_bytes.</param>
        /// <param name="max_data_bytes">Size of the allocated memory for the output payload. This may be used to impose an upper limit on the instant bitrate, but should not be used as the only bitrate control. Use <see cref="EncoderCTL.OPUS_SET_BITRATE"/> to control the bitrate.</param>
        /// <returns>The length of the encoded packet (in bytes).</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        int Encode(int[] input, int frame_size, byte[] output, int max_data_bytes);

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
        int Encode(float[] input, int frame_size, byte[] output, int max_data_bytes);

        /// <summary>
        /// Performs a ctl request.
        /// </summary>
        /// <param name="request">The request you want to specify.</param>
        /// <returns>The result code of the request. See <see cref="OpusErrorCodes"/>.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        int Ctl(EncoderCTL request);

        /// <summary>
        /// Performs a ctl request.
        /// </summary>
        /// <param name="request">The request you want to specify.</param>
        /// /// <param name="value">The input value.</param>
        /// <returns>The result code of the request. See <see cref="OpusErrorCodes"/>.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        int Ctl(EncoderCTL request, int value);

        /// <summary>
        /// Performs a ctl request.
        /// </summary>
        /// <typeparam name="T">The type you want to input/output.</typeparam>
        /// <param name="request">The request you want to specify.</param>
        /// <param name="value">The input/output value.</param>
        /// <returns>The result code of the request. See <see cref="OpusErrorCodes"/>.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        int Ctl<T>(EncoderCTL request, ref T value) where T : unmanaged;

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
        int Ctl<T>(EncoderCTL request, ref T value, int value2) where T : unmanaged;

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
        int Ctl<T>(EncoderCTL request, int value, ref T value2) where T : unmanaged;

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
        int Ctl<T, T2>(EncoderCTL request, ref T value, ref T2 value2) where T : unmanaged where T2 : unmanaged;

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