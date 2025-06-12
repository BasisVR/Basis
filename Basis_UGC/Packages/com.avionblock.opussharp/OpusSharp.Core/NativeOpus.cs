using OpusSharp.Core.SafeHandlers;
using System;
using System.Runtime.InteropServices;
                                   
//This is for rider purposes ONLY!
// ReSharper disable All
namespace OpusSharp.Core
{
    /// <summary>
    /// Native opus handler that directly calls the exported opus functions. Requires a dynamically loaded library.
    /// </summary>
    public static class NativeOpus
    {
#if IOS
        private const string DllName = "__Internal__";
#else
        private const string DllName = "opus";
#endif

        //Encoder
        /// <summary>
        /// Gets the size of an <see cref="OpusEncoderSafeHandle"/> structure.
        /// </summary>
        /// <param name="channels">Number of channels. This must be 1 or 2.</param>
        /// <returns>The size in bytes.</returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int opus_encoder_get_size(int channels);

        /// <summary>
        /// Allocates and initializes an encoder state.
        /// </summary>
        /// <param name="Fs">Sampling rate of input signal (Hz) This must be one of 8000, 12000, 16000, 24000, or 48000.</param>
        /// <param name="channels">Number of channels (1 or 2) in input signal.</param>
        /// <param name="application">Coding mode (one of <see cref="OpusPredefinedValues.OPUS_APPLICATION_VOIP"/>, <see cref="OpusPredefinedValues.OPUS_APPLICATION_AUDIO"/> or <see cref="OpusPredefinedValues.OPUS_APPLICATION_RESTRICTED_LOWDELAY"/>)</param>
        /// <param name="error"><see cref="OpusErrorCodes.OPUS_OK"/> Success or <see cref="OpusErrorCodes"/>.</param>
        /// <returns><see cref="OpusEncoderSafeHandle"/></returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe OpusEncoderSafeHandle opus_encoder_create(int Fs, int channels, int application, int* error);

        /// <summary>
        /// Initializes a previously allocated <see cref="OpusEncoderSafeHandle"/> state. The memory pointed to by st must be at least the size returned by <see cref="opus_encoder_get_size(int)"/>.
        /// </summary>
        /// <param name="st">Encoder state.</param>
        /// <param name="Fs">Sampling rate of input signal (Hz) This must be one of 8000, 12000, 16000, 24000, or 48000.</param>
        /// <param name="channels">Number of channels (1 or 2) in input signal.</param>
        /// <param name="application">>Coding mode (one of <see cref="OpusPredefinedValues.OPUS_APPLICATION_VOIP"/>, <see cref="OpusPredefinedValues.OPUS_APPLICATION_AUDIO"/> or <see cref="OpusPredefinedValues.OPUS_APPLICATION_RESTRICTED_LOWDELAY"/>)</param>
        /// <returns><see cref="OpusErrorCodes"/></returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int opus_encoder_init(OpusEncoderSafeHandle st, int Fs, int channels, int application);

        /// <summary>
        /// Encodes an Opus frame.
        /// </summary>
        /// <param name="st">Encoder state.</param>
        /// <param name="pcm">Input signal (interleaved if 2 channels). length is frame_size*channels*sizeof(short)</param>
        /// <param name="frame_size">Number of samples per channel in the input signal. This must be an Opus frame size for the encoder's sampling rate. For example, at 48 kHz the permitted values are 120, 240, 480, 960, 1920, and 2880. Passing in a duration of less than 10 ms (480 samples at 48 kHz) will prevent the encoder from using the LPC or hybrid modes.</param>
        /// <param name="data">Output payload. This must contain storage for at least max_data_bytes.</param>
        /// <param name="max_data_bytes">Size of the allocated memory for the output payload. This may be used to impose an upper limit on the instant bitrate, but should not be used as the only bitrate control. Use <see cref="EncoderCTL.OPUS_SET_BITRATE"/> to control the bitrate.</param>
        /// <returns>The length of the encoded packet (in bytes) on success or a negative error code (see <see cref="OpusErrorCodes"/>) on failure.</returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int opus_encode(OpusEncoderSafeHandle st, short* pcm, int frame_size, byte* data, int max_data_bytes);

        /// <summary>
        /// Encodes an Opus frame from floating point input.
        /// </summary>
        /// <param name="st">Encoder state.</param>
        /// <param name="pcm">Input in float format (interleaved if 2 channels), with a normal range of +/-1.0. Samples with a range beyond +/-1.0 are supported but will be clipped by decoders using the integer API and should only be used if it is known that the far end supports extended dynamic range. length is frame_size*channels*sizeof(float)</param>
        /// <param name="frame_size">Number of samples per channel in the input signal. This must be an Opus frame size for the encoder's sampling rate. For example, at 48 kHz the permitted values are 120, 240, 480, 960, 1920, and 2880. Passing in a duration of less than 10 ms (480 samples at 48 kHz) will prevent the encoder from using the LPC or hybrid modes.</param>
        /// <param name="data">Output payload. This must contain storage for at least max_data_bytes.</param>
        /// <param name="max_data_bytes">Size of the allocated memory for the output payload. This may be used to impose an upper limit on the instant bitrate, but should not be used as the only bitrate control. Use <see cref="EncoderCTL.OPUS_SET_BITRATE"/> to control the bitrate.</param>
        /// <returns>The length of the encoded packet (in bytes) on success or a negative error code (see <see cref="OpusErrorCodes"/>) on failure.</returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int opus_encode_float(OpusEncoderSafeHandle st, float* pcm, int frame_size, byte* data, int max_data_bytes);

        /// <summary>
        /// Frees an <see cref="OpusEncoderSafeHandle"/> allocated by <see cref="opus_encoder_create(int, int, int, int*)"/>.
        /// </summary>
        /// <param name="st">State to be freed.</param>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void opus_encoder_destroy(IntPtr st);

        /// <summary>
        /// Perform a CTL function on an <see cref="OpusEncoderSafeHandle"/>.
        /// </summary>
        /// <param name="st">Encoder state.</param>
        /// <param name="request">This and all remaining parameters should be replaced by one of the convenience macros in <see cref="GenericCTL"/> or <see cref="EncoderCTL"/>.</param>
        /// <returns><see cref="OpusErrorCodes"/></returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int opus_encoder_ctl(OpusEncoderSafeHandle st, int request); //Apparently GenericCTL.OPUS_RESET_STATE exists.
        
        /// <summary>
        /// Perform a CTL function on an <see cref="OpusEncoderSafeHandle"/>.
        /// </summary>
        /// <param name="st">Encoder state.</param>
        /// <param name="request">This and all remaining parameters should be replaced by one of the convenience macros in <see cref="GenericCTL"/> or <see cref="EncoderCTL"/>.</param>
        /// <param name="data">The data to input.</param>
        /// <returns><see cref="OpusErrorCodes"/></returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int opus_encoder_ctl(OpusEncoderSafeHandle st, int request, int data);

        /// <summary>
        /// Perform a CTL function on an <see cref="OpusEncoderSafeHandle"/>.
        /// </summary>
        /// <param name="st">Encoder state.</param>
        /// <param name="request">This and all remaining parameters should be replaced by one of the convenience macros in <see cref="GenericCTL"/> or <see cref="EncoderCTL"/>.</param>
        /// <param name="data">The data to input/output.</param>
        /// <returns><see cref="OpusErrorCodes"/></returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int opus_encoder_ctl(OpusEncoderSafeHandle st, int request, void* data);
        
        /// <summary>
        /// Perform a CTL function on an Opus encoder.
        /// </summary>
        /// <param name="st">Encoder state.</param>
        /// <param name="request">This and all remaining parameters should be replaced by one of the convenience macros in <see cref="GenericCTL"/> or <see cref="EncoderCTL"/>.</param>
        /// <param name="data">The data to input/output.</param>
        /// <param name="data2">The second data to input.</param>
        /// <returns><see cref="OpusErrorCodes"/></returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int opus_encoder_ctl(OpusEncoderSafeHandle st, int request, void* data, int data2);
        
        /// <summary>
        /// Perform a CTL function on an Opus encoder.
        /// </summary>
        /// <param name="st">Encoder state.</param>
        /// <param name="request">This and all remaining parameters should be replaced by one of the convenience macros in <see cref="GenericCTL"/> or <see cref="EncoderCTL"/>.</param>
        /// <param name="data">The data to input.</param>
        /// <param name="data2">The second data to input/output.</param>
        /// <returns><see cref="OpusErrorCodes"/></returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int opus_encoder_ctl(OpusEncoderSafeHandle st, int request, int data, void* data2);

        /// <summary>
        /// Perform a CTL function on an Opus encoder.
        /// </summary>
        /// <param name="st">Encoder state.</param>
        /// <param name="request">This and all remaining parameters should be replaced by one of the convenience macros in <see cref="GenericCTL"/> or <see cref="EncoderCTL"/>.</param>
        /// <param name="data">The data to input/output.</param>
        /// <param name="data2">The second data to input/output.</param>
        /// <returns><see cref="OpusErrorCodes"/></returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int opus_encoder_ctl(OpusEncoderSafeHandle st, int request, void* data, void* data2);

        //Decoder
        /// <summary>
        /// Gets the size of an <see cref="OpusDecoderSafeHandle"/> structure.
        /// </summary>
        /// <param name="channels">Number of channels. This must be 1 or 2.</param>
        /// <returns>The size in bytes.</returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int opus_decoder_get_size(int channels);

        /// <summary>
        /// Allocates and initializes a <see cref="OpusDecoderSafeHandle"/> state.
        /// </summary>
        /// <param name="Fs">Sample rate to decode at (Hz). This must be one of 8000, 12000, 16000, 24000, or 48000.</param>
        /// <param name="channels">Number of channels (1 or 2) to decode.</param>
        /// <param name="error"><see cref="OpusErrorCodes.OPUS_OK"/> Success or <see cref="OpusErrorCodes"/>.</param>
        /// <returns><see cref="OpusDecoderSafeHandle"/></returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe OpusDecoderSafeHandle opus_decoder_create(int Fs, int channels, int* error);

        /// <summary>
        /// Initializes a previously allocated <see cref="OpusDecoderSafeHandle"/> state.
        /// </summary>
        /// <param name="st">Decoder state.</param>
        /// <param name="Fs">Sampling rate to decode to (Hz). This must be one of 8000, 12000, 16000, 24000, or 48000.</param>
        /// <param name="channels">Number of channels (1 or 2) to decode.</param>
        /// <returns><see cref="OpusErrorCodes.OPUS_OK"/> Success or <see cref="OpusErrorCodes"/>.</returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int opus_decoder_init(OpusDecoderSafeHandle st, int Fs, int channels);

        /// <summary>
        /// Decode an Opus packet.
        /// </summary>
        /// <param name="st">Decoder state.</param>
        /// <param name="data">Input payload. Use a NULL pointer to indicate packet loss.</param>
        /// <param name="len">Number of bytes in payload.</param>
        /// <param name="pcm">Output signal (interleaved if 2 channels). length is frame_size*channels*sizeof(short).</param>
        /// <param name="frame_size">Number of samples per channel of available space in pcm. If this is less than the maximum packet duration (120ms; 5760 for 48kHz), this function will not be capable of decoding some packets. In the case of PLC (data==NULL) or FEC (decode_fec=1), then frame_size needs to be exactly the duration of audio that is missing, otherwise the decoder will not be in the optimal state to decode the next incoming packet. For the PLC and FEC cases, frame_size must be a multiple of 2.5 ms.</param>
        /// <param name="decode_fec">Flag (0 or 1) to request that any in-band forward error correction data be decoded. If no such data is available, the frame is decoded as if it were lost.</param>
        /// <returns>Number of decoded samples or <see cref="OpusErrorCodes"/>.</returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int opus_decode(OpusDecoderSafeHandle st, byte* data, int len, short* pcm, int frame_size, int decode_fec);

        /// <summary>
        /// Decode an Opus packet with floating point output.
        /// </summary>
        /// <param name="st">Decoder state.</param>
        /// <param name="data">Input payload. Use a NULL pointer to indicate packet loss.</param>
        /// <param name="len">Number of bytes in payload.</param>
        /// <param name="pcm">Output signal (interleaved if 2 channels). length is frame_size*channels*sizeof(float).</param>
        /// <param name="frame_size">Number of samples per channel of available space in pcm. If this is less than the maximum packet duration (120ms; 5760 for 48kHz), this function will not be capable of decoding some packets. In the case of PLC (data==NULL) or FEC (decode_fec=1), then frame_size needs to be exactly the duration of audio that is missing, otherwise the decoder will not be in the optimal state to decode the next incoming packet. For the PLC and FEC cases, frame_size must be a multiple of 2.5 ms.</param>
        /// <param name="decode_fec">Flag (0 or 1) to request that any in-band forward error correction data be decoded. If no such data is available the frame is decoded as if it were lost.</param>
        /// <returns>Number of decoded samples or <see cref="OpusErrorCodes"/></returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int opus_decode_float(OpusDecoderSafeHandle st, byte* data, int len, float* pcm, int frame_size, int decode_fec);

        /// <summary>
        /// Perform a CTL function on an <see cref="OpusDecoderSafeHandle"/>.
        /// </summary>
        /// <param name="st">Decoder state.</param>
        /// <param name="request">This and all remaining parameters should be replaced by one of the convenience macros in <see cref="GenericCTL"/> or <see cref="DecoderCTL"/>.</param>
        /// <returns><see cref="OpusErrorCodes"/></returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int opus_decoder_ctl(OpusDecoderSafeHandle st, int request); //Apparently GenericCTL.OPUS_RESET_STATE exists.

        /// <summary>
        /// Perform a CTL function on an <see cref="OpusDecoderSafeHandle"/>.
        /// </summary>
        /// <param name="st">Decoder state.</param>
        /// <param name="request">This and all remaining parameters should be replaced by one of the convenience macros in <see cref="GenericCTL"/> or <see cref="DecoderCTL"/>.</param>
        /// <param name="data">The data to input.</param>
        /// <returns><see cref="OpusErrorCodes"/></returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int opus_decoder_ctl(OpusDecoderSafeHandle st, int request, int data);
        
        /// <summary>
        /// Perform a CTL function on an <see cref="OpusDecoderSafeHandle"/>.
        /// </summary>
        /// <param name="st">Decoder state.</param>
        /// <param name="request">This and all remaining parameters should be replaced by one of the convenience macros in <see cref="GenericCTL"/> or <see cref="DecoderCTL"/>.</param>
        /// <param name="data">The data to input or output.</param>
        /// <returns><see cref="OpusErrorCodes"/></returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int opus_decoder_ctl(OpusDecoderSafeHandle st, int request, void* data);

        /// <summary>
        /// Frees an <see cref="OpusDecoderSafeHandle"/> allocated by <see cref="opus_decoder_create(int, int, int*)"/>.
        /// </summary>
        /// <param name="st">State to be freed.</param>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void opus_decoder_destroy(IntPtr st);

        //Dred Decoder
        /// <summary>
        /// Gets the size of an <see cref="OpusDREDDecoderSafeHandle"/> structure.
        /// </summary>
        /// <returns>The size in bytes.</returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int opus_dred_decoder_get_size();

        /// <summary>
        /// Allocates and initializes an <see cref="OpusDREDDecoderSafeHandle"/> state.
        /// </summary>
        /// <param name="error"><see cref="OpusErrorCodes.OPUS_OK"/> Success or <see cref="OpusErrorCodes"/>.</param>
        /// <returns><see cref="OpusDREDDecoderSafeHandle"/></returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe OpusDREDDecoderSafeHandle opus_dred_decoder_create(int* error);

        /// <summary>
        /// Initializes an <see cref="OpusDREDDecoderSafeHandle"/> state.
        /// </summary>
        /// <param name="dec">State to be initialized.</param>
        /// <returns><see cref="OpusErrorCodes"/></returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int opus_dred_decoder_init(OpusDREDDecoderSafeHandle dec);

        /// <summary>
        /// Frees an <see cref="OpusDREDDecoderSafeHandle"/> allocated by <see cref="opus_dred_decoder_create(int*)"/>.
        /// </summary>
        /// <param name="dec">State to be freed.</param>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void opus_dred_decoder_destroy(IntPtr dec);

        /// <summary>
        /// Perform a CTL function on an <see cref="OpusDREDDecoderSafeHandle"/>.
        /// </summary>
        /// <param name="dred_dec">DRED Decoder state.</param>
        /// <param name="request">This and all remaining parameters should be replaced by one of the convenience macros in <see cref="GenericCTL"/> or <see cref="DecoderCTL"/>.</param>
        /// <returns><see cref="OpusErrorCodes"/></returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int opus_dred_decoder_ctl(OpusDREDDecoderSafeHandle dred_dec, int request); //Apparently GenericCTL.OPUS_RESET_STATE exists.

        /// <summary>
        /// Perform a CTL function on an <see cref="OpusDREDDecoderSafeHandle"/>.
        /// </summary>
        /// <param name="dred_dec">DRED Decoder state.</param>
        /// <param name="request">This and all remaining parameters should be replaced by one of the convenience macros in <see cref="GenericCTL"/> or <see cref="DecoderCTL"/>.</param>
        /// <param name="data">The data to input or output.</param>
        /// <returns><see cref="OpusErrorCodes"/></returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int opus_dred_decoder_ctl(OpusDREDDecoderSafeHandle dred_dec, int request, void* data);

        //Dred Packet?
        /// <summary>
        /// Gets the size of an <see cref="OpusDREDSafeHandle"/> structure.
        /// </summary>
        /// <returns>The size in bytes.</returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int opus_dred_get_size();

        /// <summary>
        /// Allocates and initializes a <see cref="OpusDREDSafeHandle"/> state.
        /// </summary>
        /// <param name="error"><see cref="OpusErrorCodes.OPUS_OK"/> Success or <see cref="OpusErrorCodes"/>.</param>
        /// <returns><see cref="OpusDREDSafeHandle"/></returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe OpusDREDSafeHandle opus_dred_alloc(int* error);

        /// <summary>
        /// Frees an <see cref="OpusDREDSafeHandle"/> allocated by <see cref="opus_dred_alloc(int*)"/>.
        /// </summary>
        /// <param name="dec">State to be freed.</param>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void opus_dred_free(IntPtr dec); //I'M JUST FOLLOWING THE DOCS!

        /// <summary>
        /// Decode an Opus DRED packet.
        /// </summary>
        /// <param name="dred_dec">DRED Decoder state.</param>
        /// <param name="dred">DRED state.</param>
        /// <param name="data">Input payload.</param>
        /// <param name="len">Number of bytes in payload.</param>
        /// <param name="max_dred_samples">Maximum number of DRED samples that may be needed (if available in the packet).</param>
        /// <param name="sampling_rate">Sampling rate used for max_dred_samples argument. Needs not match the actual sampling rate of the decoder.</param>
        /// <param name="dred_end">Number of non-encoded (silence) samples between the DRED timestamp and the last DRED sample.</param>
        /// <param name="defer_processing">Flag (0 or 1). If set to one, the CPU-intensive part of the DRED decoding is deferred until <see cref="opus_dred_process(OpusDREDDecoderSafeHandle, OpusDREDSafeHandle, OpusDREDSafeHandle)"/> is called.</param>
        /// <returns>Offset (positive) of the first decoded DRED samples, zero if no DRED is present, or <see cref="OpusErrorCodes"/></returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int opus_dred_parse(OpusDREDDecoderSafeHandle dred_dec, OpusDREDSafeHandle dred, byte* data, int len, int max_dred_samples, int sampling_rate, int* dred_end, int defer_processing);

        /// <summary>
        /// Finish decoding an <see cref="OpusDREDSafeHandle"/> packet.
        /// </summary>
        /// <param name="dred_dec">DRED Decoder state.</param>
        /// <param name="src">Source DRED state to start the processing from.</param>
        /// <param name="dst">Destination DRED state to store the updated state after processing.</param>
        /// <returns><see cref="OpusErrorCodes"/></returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int opus_dred_process(OpusDREDDecoderSafeHandle dred_dec, OpusDREDSafeHandle src, OpusDREDSafeHandle dst);

        /// <summary>
        /// Decode audio from an <see cref="OpusDREDSafeHandle"/> packet with floating point output.
        /// </summary>
        /// <param name="st">Decoder state.</param>
        /// <param name="dred">DRED state.</param>
        /// <param name="dred_offset">position of the redundancy to decode (in samples before the beginning of the real audio data in the packet).</param>
        /// <param name="pcm">Output signal (interleaved if 2 channels). length is frame_size*channels*sizeof(short)</param>
        /// <param name="frame_size">Number of samples per channel to decode in pcm. frame_size must be a multiple of 2.5 ms.</param>
        /// <returns>Number of decoded samples or <see cref="OpusErrorCodes"/>.</returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int opus_decoder_dred_decode(OpusDecoderSafeHandle st, OpusDREDSafeHandle dred, int dred_offset, short* pcm, int frame_size);

        /// <summary>
        /// Decode audio from an <see cref="OpusDREDSafeHandle"/> packet with floating point output.
        /// </summary>
        /// <param name="st">Decoder state.</param>
        /// <param name="dred">DRED state.</param>
        /// <param name="dred_offset">position of the redundancy to decode (in samples before the beginning of the real audio data in the packet).</param>
        /// <param name="pcm">Output signal (interleaved if 2 channels). length is frame_size*channels*sizeof(float).</param>
        /// <param name="frame_size">Number of samples per channel to decode in pcm. frame_size must be a multiple of 2.5 ms.</param>
        /// <returns>Number of decoded samples or <see cref="OpusErrorCodes"/>.</returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int opus_decoder_dred_decode_float(OpusDecoderSafeHandle st, OpusDREDSafeHandle dred, int dred_offset, float* pcm, int frame_size);

        //Opus Packet Parsers
        /// <summary>
        /// Parse an opus packet into one or more frames.
        /// </summary>
        /// <param name="data">Opus packet to be parsed.</param>
        /// <param name="len">size of data.</param>
        /// <param name="out_toc">TOC pointer.</param>
        /// <param name="frames">encapsulated frames.</param>
        /// <param name="size">sizes of the encapsulated frames.</param>
        /// <param name="payload_offset">returns the position of the payload within the packet (in bytes).</param>
        /// <returns>number of frames.</returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int opus_packet_parse(byte* data, int len, byte* out_toc, byte*[] frames, short[] size, int* payload_offset);

        /// <summary>
        /// Gets the bandwidth of an Opus packet.
        /// </summary>
        /// <param name="data">Opus packet.</param>
        /// <returns><see cref="OpusPredefinedValues"/> or <see cref="OpusErrorCodes"/></returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int opus_packet_get_bandwidth(byte* data);

        /// <summary>
        /// Gets the number of samples per frame from an Opus packet.
        /// </summary>
        /// <param name="data">Opus packet. This must contain at least one byte of data.</param>
        /// <param name="Fs">Sampling rate in Hz. This must be a multiple of 400, or inaccurate results will be returned.</param>
        /// <returns>Number of samples per frame.</returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int opus_packet_get_samples_per_frame(byte* data, int Fs);

        /// <summary>
        /// Gets the number of channels from an Opus packet.
        /// </summary>
        /// <param name="data">Opus packet.</param>
        /// <returns>Number of channels or <see cref="OpusErrorCodes"/>.</returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int opus_packet_get_nb_channels(byte* data);

        /// <summary>
        /// Gets the number of frames in an Opus packet.
        /// </summary>
        /// <param name="packet">Opus packet.</param>
        /// <param name="len">Length of packet.</param>
        /// <returns>Number of frames or <see cref="OpusErrorCodes"/>.</returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int opus_packet_get_nb_frames(byte* packet, int len);

        /// <summary>
        /// Gets the number of samples of an Opus packet.
        /// </summary>
        /// <param name="packet">Opus packet.</param>
        /// <param name="len">Length of packet.</param>
        /// <param name="Fs">Sampling rate in Hz. This must be a multiple of 400, or inaccurate results will be returned.</param>
        /// <returns>Number of samples or <see cref="OpusErrorCodes"/>.</returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int opus_packet_get_nb_samples(byte* packet, int len, int Fs);

        /// <summary>
        /// Checks whether an Opus packet has LBRR.
        /// </summary>
        /// <param name="packet">Opus packet.</param>
        /// <param name="len">Length of packet.</param>
        /// <returns>1 is LBRR is present, 0 otherwise or <see cref="OpusErrorCodes"/></returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int opus_packet_has_lbrr(byte* packet, int len);

        /// <summary>
        /// Gets the number of samples of an Opus packet.
        /// </summary>
        /// <param name="dec">Decoder state.</param>
        /// <param name="packet">Opus packet.</param>
        /// <param name="len">Length of packet.</param>
        /// <returns>Number of samples or <see cref="OpusErrorCodes"/>.</returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int opus_decoder_get_nb_samples(OpusDecoderSafeHandle dec, byte* packet, int len);

        /// <summary>
        /// Applies soft-clipping to bring a float signal within the [-1,1] range.
        /// </summary>
        /// <param name="pcm">Input PCM and modified PCM.</param>
        /// <param name="frame_size">Number of samples per channel to process.</param>
        /// <param name="channels">Number of channels.</param>
        /// <param name="softclip_mem">State memory for the soft clipping process (one float per channel, initialized to zero).</param>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe void opus_pcm_soft_clip(float* pcm, int frame_size, int channels, float* softclip_mem);

        //Repacketizer
        /// <summary>
        /// Gets the size of an <see cref="OpusRepacketizerSafeHandle"/> structure.
        /// </summary>
        /// <returns>The size in bytes.</returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int opus_repacketizer_get_size();

        /// <summary>
        /// (Re)initializes a previously allocated <see cref="OpusRepacketizerSafeHandle"/> state.
        /// </summary>
        /// <param name="rp">The <see cref="OpusRepacketizerSafeHandle"/> state to (re)initialize.</param>
        /// <returns><see cref="OpusRepacketizerSafeHandle"/></returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern OpusRepacketizerSafeHandle opus_repacketizer_init(OpusRepacketizerSafeHandle rp);

        /// <summary>
        /// Allocates memory and initializes the new <see cref="OpusRepacketizerSafeHandle"/> with <see cref="opus_repacketizer_init(OpusRepacketizerSafeHandle)"/>.
        /// </summary>
        /// <returns><see cref="OpusRepacketizerSafeHandle"/></returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern OpusRepacketizerSafeHandle opus_repacketizer_create();

        /// <summary>
        /// Frees an <see cref="OpusRepacketizerSafeHandle"/> allocated by <see cref="opus_repacketizer_create"/>.
        /// </summary>
        /// <param name="rp">State to be freed.</param>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void opus_repacketizer_destroy(IntPtr rp);

        /// <summary>
        /// Add a packet to the current <see cref="OpusRepacketizerSafeHandle"/> state.
        /// </summary>
        /// <param name="rp">The repacketizer state to which to add the packet.</param>
        /// <param name="data">The packet data. The application must ensure this pointer remains valid until the next call to <see cref="opus_repacketizer_init(OpusRepacketizerSafeHandle)"/> or <see cref="opus_repacketizer_destroy(IntPtr)"/>.</param>
        /// <param name="len">The number of bytes in the packet data.</param>
        /// <returns><see cref="OpusErrorCodes"/></returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int opus_repacketizer_cat(OpusRepacketizerSafeHandle rp, byte* data, int len);

        /// <summary>
        /// Construct a new packet from data previously submitted to the <see cref="OpusRepacketizerSafeHandle"/> state via <see cref="opus_repacketizer_cat(OpusRepacketizerSafeHandle, byte*, int)"/>.
        /// </summary>
        /// <param name="rp">The repacketizer state from which to construct the new packet.</param>
        /// <param name="begin">The index of the first frame in the current repacketizer state to include in the output.</param>
        /// <param name="end">One past the index of the last frame in the current repacketizer state to include in the output.</param>
        /// <param name="data">The buffer in which to store the output packet.</param>
        /// <param name="maxlen">The maximum number of bytes to store in the output buffer. In order to guarantee success, this should be at least 1276 for a single frame, or for multiple frames, 1277*(end-begin). However, 1*(end-begin) plus the size of all packet data submitted to the repacketizer since the last call to <see cref="opus_repacketizer_init(OpusRepacketizerSafeHandle)"/> or <see cref="opus_repacketizer_create"/> is also sufficient, and possibly much smaller.</param>
        /// <returns>The total size of the output packet on success, or an <see cref="OpusErrorCodes"/> on failure.</returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int opus_repacketizer_out_range(OpusRepacketizerSafeHandle rp, int begin, int end, byte* data, int maxlen);

        /// <summary>
        /// Return the total number of frames contained in packet data submitted to the <see cref="OpusRepacketizerSafeHandle"/> state so far via <see cref="opus_repacketizer_cat(OpusRepacketizerSafeHandle, byte*, int)"/> since the last call to <see cref="opus_repacketizer_init(OpusRepacketizerSafeHandle)"/> or <see cref="opus_repacketizer_create"/>.
        /// </summary>
        /// <param name="rp">The repacketizer state containing the frames.</param>
        /// <returns>The total number of frames contained in the packet data submitted to the repacketizer state.</returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int opus_repacketizer_get_nb_frames(OpusRepacketizerSafeHandle rp);

        /// <summary>
        /// Construct a new packet from data previously submitted to the <see cref="OpusRepacketizerSafeHandle"/> state via <see cref="opus_repacketizer_cat(OpusRepacketizerSafeHandle, byte*, int)"/>.
        /// </summary>
        /// <param name="rp">The repacketizer state from which to construct the new packet.</param>
        /// <param name="data">The buffer in which to store the output packet.</param>
        /// <param name="maxlen">The maximum number of bytes to store in the output buffer. In order to guarantee success, this should be at least 1277*opus_repacketizer_get_nb_frames(rp). However, 1*opus_repacketizer_get_nb_frames(rp) plus the size of all packet data submitted to the repacketizer since the last call to opus_repacketizer_init() or opus_repacketizer_create() is also sufficient, and possibly much smaller.</param>
        /// <returns>The total size of the output packet on success, or an <see cref="OpusErrorCodes"/> on failure.</returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int opus_repacketizer_out(OpusRepacketizerSafeHandle rp, byte* data, int maxlen);

        /// <summary>
        /// Pads a given Opus packet to a larger size (possibly changing the TOC sequence).
        /// </summary>
        /// <param name="data">The buffer containing the packet to pad.</param>
        /// <param name="len">The size of the packet. This must be at least 1.</param>
        /// <param name="new_len">The desired size of the packet after padding. This must be at least as large as len.</param>
        /// <returns><see cref="OpusErrorCodes"/></returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int opus_packet_pad(byte* data, int len, int new_len);

        /// <summary>
        /// Remove all padding from a given Opus packet and rewrite the TOC sequence to minimize space usage.
        /// </summary>
        /// <param name="data">The buffer containing the packet to strip.</param>
        /// <param name="len">The size of the packet. This must be at least 1.</param>
        /// <returns>The new size of the output packet on success, or an <see cref="OpusErrorCodes"/> on failure.</returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int opus_packet_unpad(byte* data, int len);

        /// <summary>
        /// Pads a given Opus multi-stream packet to a larger size (possibly changing the TOC sequence).
        /// </summary>
        /// <param name="data">The buffer containing the packet to pad.</param>
        /// <param name="len">The size of the packet. This must be at least 1.</param>
        /// <param name="new_len">The desired size of the packet after padding. This must be at least 1.</param>
        /// <param name="nb_streams">The number of streams (not channels) in the packet. This must be at least as large as len.</param>
        /// <returns><see cref="OpusErrorCodes"/></returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int opus_multistream_packet_pad(byte* data, int len, int new_len, int nb_streams);

        /// <summary>
        /// Remove all padding from a given Opus multi-stream packet and rewrite the TOC sequence to minimize space usage.
        /// </summary>
        /// <param name="data">The buffer containing the packet to strip.</param>
        /// <param name="len">The size of the packet. This must be at least 1.</param>
        /// <param name="nb_streams">The number of streams (not channels) in the packet. This must be at least 1.</param>
        /// <returns>The new size of the output packet on success, or an <see cref="OpusErrorCodes"/> on failure.</returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int opus_multistream_packet_unpad(byte* data, int len, int nb_streams);

        //Multistream Encoder
        /// <summary>
        /// Gets the size of an <see cref="OpusMSEncoderSafeHandle"/> structure.
        /// </summary>
        /// <param name="streams">The total number of streams to encode from the input. This must be no more than 255.</param>
        /// <param name="coupled_streams">Number of coupled (2 channel) streams to encode. This must be no larger than the total number of streams. Additionally, The total number of encoded channels (streams + coupled_streams) must be no more than 255.</param>
        /// <returns>The size in bytes on success, or a negative error code (see <see cref="OpusErrorCodes"/>) on error.</returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int opus_multistream_encoder_get_size(int streams, int coupled_streams);

        /// <summary>
        /// N.A.
        /// </summary>
        /// <param name="channels"></param>
        /// <param name="mapping_family"></param>
        /// <returns></returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int opus_multistream_surround_encoder_get_size(int channels, int mapping_family);

        /// <summary>
        /// Allocates and initializes a <see cref="OpusMSEncoderSafeHandle"/> state.
        /// </summary>
        /// <param name="Fs">Sampling rate of the input signal (in Hz). This must be one of 8000, 12000, 16000, 24000, or 48000.</param>
        /// <param name="channels">Number of channels in the input signal. This must be at most 255. It may be greater than the number of coded channels (streams + coupled_streams).</param>
        /// <param name="streams">The total number of streams to encode from the input. This must be no more than the number of channels.</param>
        /// <param name="coupled_streams">Number of coupled (2 channel) streams to encode. This must be no larger than the total number of streams. Additionally, The total number of encoded channels (streams + coupled_streams) must be no more than the number of input channels.</param>
        /// <param name="mapping">Mapping from encoded channels to input channels, as described in Opus Multistream API. As an extra constraint, the multistream encoder does not allow encoding coupled streams for which one channel is unused since this is never a good idea.</param>
        /// <param name="application">The target encoder application. This must be one of the following: <see cref="OpusPredefinedValues.OPUS_APPLICATION_VOIP"/>, <see cref="OpusPredefinedValues.OPUS_APPLICATION_AUDIO"/> or <see cref="OpusPredefinedValues.OPUS_APPLICATION_RESTRICTED_LOWDELAY"/>.</param>
        /// <param name="error">Returns <see cref="OpusErrorCodes.OPUS_OK"/> on success, or an error code (see <see cref="OpusErrorCodes"/>) on failure.</param>
        /// <returns><see cref="OpusMSEncoderSafeHandle"/></returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe OpusMSEncoderSafeHandle opus_multistream_encoder_create(int Fs, int channels, int streams, int coupled_streams, byte* mapping, int application, int* error);

        /// <summary>
        /// N.A.
        /// </summary>
        /// <param name="Fs"></param>
        /// <param name="channels"></param>
        /// <param name="mapping_family"></param>
        /// <param name="streams"></param>
        /// <param name="coupled_streams"></param>
        /// <param name="mapping"></param>
        /// <param name="application"></param>
        /// <param name="error"></param>
        /// <returns></returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe OpusMSEncoderSafeHandle opus_multistream_surround_encoder_create(int Fs, int channels, int mapping_family, int* streams, int* coupled_streams, byte* mapping, int application, int* error);

        /// <summary>
        /// Initialize a previously allocated <see cref="OpusMSEncoderSafeHandle"/> state.
        /// </summary>
        /// <param name="st">Multistream encoder state to initialize.</param>
        /// <param name="Fs">Sampling rate of the input signal (in Hz). This must be one of 8000, 12000, 16000, 24000, or 48000.</param>
        /// <param name="channels">Number of channels in the input signal. This must be at most 255. It may be greater than the number of coded channels (streams + coupled_streams).</param>
        /// <param name="streams">The total number of streams to encode from the input. This must be no more than the number of channels.</param>
        /// <param name="coupled_streams">Number of coupled (2 channel) streams to encode. This must be no larger than the total number of streams. Additionally, The total number of encoded channels (streams + coupled_streams) must be no more than the number of input channels.</param>
        /// <param name="mapping">Mapping from encoded channels to input channels, as described in Opus Multistream API. As an extra constraint, the multistream encoder does not allow encoding coupled streams for which one channel is unused since this is never a good idea.</param>
        /// <param name="application">The target encoder application. This must be one of the following: <see cref="OpusPredefinedValues.OPUS_APPLICATION_VOIP"/>, <see cref="OpusPredefinedValues.OPUS_APPLICATION_AUDIO"/> or <see cref="OpusPredefinedValues.OPUS_APPLICATION_RESTRICTED_LOWDELAY"/>.</param>
        /// <returns><see cref="OpusErrorCodes.OPUS_OK"/> on success, or an error code (see <see cref="OpusErrorCodes"/>) on failure.</returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int opus_multistream_encoder_init(OpusMSEncoderSafeHandle st, int Fs, int channels, int streams, int coupled_streams, byte* mapping, int application);

        /// <summary>
        /// N.A.
        /// </summary>
        /// <param name="st"></param>
        /// <param name="Fs"></param>
        /// <param name="channels"></param>
        /// <param name="mapping_family"></param>
        /// <param name="streams"></param>
        /// <param name="coupled_streams"></param>
        /// <param name="mapping"></param>
        /// <param name="application"></param>
        /// <returns></returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int opus_multistream_surround_encoder_init(OpusMSEncoderSafeHandle st, int Fs, int channels, int mapping_family, int* streams, int* coupled_streams, byte* mapping, int application);

        /// <summary>
        /// Encodes a multistream Opus frame.
        /// </summary>
        /// <param name="st">Multistream encoder state.</param>
        /// <param name="pcm">The input signal as interleaved samples. This must contain frame_size*channels samples.</param>
        /// <param name="frame_size">Number of samples per channel in the input signal. This must be an Opus frame size for the encoder's sampling rate. For example, at 48 kHz the permitted values are 120, 240, 480, 960, 1920, and 2880. Passing in a duration of less than 10 ms (480 samples at 48 kHz) will prevent the encoder from using the LPC or hybrid modes.</param>
        /// <param name="data">Output payload. This must contain storage for at least max_data_bytes.</param>
        /// <param name="max_data_bytes">Size of the allocated memory for the output payload. This may be used to impose an upper limit on the instant bitrate, but should not be used as the only bitrate control. Use <see cref="EncoderCTL.OPUS_SET_BITRATE"/> to control the bitrate.</param>
        /// <returns>The length of the encoded packet (in bytes) on success or a negative error code (see <see cref="OpusErrorCodes"/>) on failure.</returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int opus_multistream_encode(OpusMSEncoderSafeHandle st, short* pcm, int frame_size, byte* data, int max_data_bytes);

        /// <summary>
        /// Encodes a multistream Opus frame from floating point input.
        /// </summary>
        /// <param name="st">Multistream encoder state.</param>
        /// <param name="pcm">The input signal as interleaved samples with a normal range of +/-1.0. Samples with a range beyond +/-1.0 are supported but will be clipped by decoders using the integer API and should only be used if it is known that the far end supports extended dynamic range. This must contain frame_size*channels samples.</param>
        /// <param name="frame_size">Number of samples per channel in the input signal. This must be an Opus frame size for the encoder's sampling rate. For example, at 48 kHz the permitted values are 120, 240, 480, 960, 1920, and 2880. Passing in a duration of less than 10 ms (480 samples at 48 kHz) will prevent the encoder from using the LPC or hybrid modes.</param>
        /// <param name="data">Output payload. This must contain storage for at least max_data_bytes.</param>
        /// <param name="max_data_bytes">Size of the allocated memory for the output payload. This may be used to impose an upper limit on the instant bitrate, but should not be used as the only bitrate control. Use <see cref="EncoderCTL.OPUS_SET_BITRATE"/> to control the bitrate.</param>
        /// <returns>The length of the encoded packet (in bytes) on success or a negative error code (see <see cref="OpusErrorCodes"/>) on failure.</returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int opus_multistream_encode_float(OpusMSEncoderSafeHandle st, float* pcm, int frame_size, byte* data, int max_data_bytes);

        /// <summary>
        /// Frees an <see cref="OpusMSEncoderSafeHandle"/> allocated by <see cref="opus_multistream_encoder_create(int, int, int, int, byte*, int, int*)"/>.
        /// </summary>
        /// <param name="st">Multistream encoder state to be freed.</param>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void opus_multistream_encoder_destroy(IntPtr st);

        /// <summary>
        /// Perform a CTL function on a <see cref="OpusMSEncoderSafeHandle"/>.
        /// </summary>
        /// <param name="st">Multistream encoder state.</param>
        /// <param name="request">This and all remaining parameters should be replaced by one of the convenience macros in <see cref="GenericCTL"/>, <see cref="EncoderCTL"/>, or <see cref="MultistreamCTL"/> specific encoder and decoder CTLs.</param>
        /// <returns><see cref="OpusErrorCodes"/></returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int opus_multistream_encoder_ctl(OpusMSEncoderSafeHandle st, int request); //Apparently GenericCTL.OPUS_RESET_STATE exists.
        
        /// <summary>
        /// Perform a CTL function on a <see cref="OpusMSEncoderSafeHandle"/>.
        /// </summary>
        /// <param name="st">Multistream encoder state.</param>
        /// <param name="request">This and all remaining parameters should be replaced by one of the convenience macros in <see cref="GenericCTL"/>, <see cref="EncoderCTL"/>, or <see cref="MultistreamCTL"/> specific encoder and decoder CTLs.</param>
        /// <param name="data">The input data.</param>
        /// <returns><see cref="OpusErrorCodes"/></returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int opus_multistream_encoder_ctl(OpusMSEncoderSafeHandle st, int request, int data);

        /// <summary>
        /// Perform a CTL function on a <see cref="OpusMSEncoderSafeHandle"/>.
        /// </summary>
        /// <param name="st">Multistream encoder state.</param>
        /// <param name="request">This and all remaining parameters should be replaced by one of the convenience macros in <see cref="GenericCTL"/>, <see cref="EncoderCTL"/>, or <see cref="MultistreamCTL"/> specific encoder and decoder CTLs.</param>
        /// <param name="data">The input/output data.</param>
        /// <returns><see cref="OpusErrorCodes"/></returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int opus_multistream_encoder_ctl(OpusMSEncoderSafeHandle st, int request, void* data);
        
        /// <summary>
        /// Perform a CTL function on a <see cref="OpusMSEncoderSafeHandle"/>.
        /// </summary>
        /// <param name="st">Multistream encoder state.</param>
        /// <param name="request">This and all remaining parameters should be replaced by one of the convenience macros in <see cref="GenericCTL"/>, <see cref="EncoderCTL"/>, or <see cref="MultistreamCTL"/> specific encoder and decoder CTLs.</param>
        /// <param name="data">The input/output data.</param>
        /// <param name="data2">The input data.</param>
        /// <returns><see cref="OpusErrorCodes"/></returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int opus_multistream_encoder_ctl(OpusMSEncoderSafeHandle st, int request, void* data, int data2);
        
        /// <summary>
        /// Perform a CTL function on a <see cref="OpusMSEncoderSafeHandle"/>.
        /// </summary>
        /// <param name="st">Multistream encoder state.</param>
        /// <param name="request">This and all remaining parameters should be replaced by one of the convenience macros in <see cref="GenericCTL"/>, <see cref="EncoderCTL"/>, or <see cref="MultistreamCTL"/> specific encoder and decoder CTLs.</param>
        /// <param name="data">The input data.</param>
        /// <param name="data2">The input/output data.</param>
        /// <returns><see cref="OpusErrorCodes"/></returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int opus_multistream_encoder_ctl(OpusMSEncoderSafeHandle st, int request, int data, void* data2);

        /// <summary>
        /// Perform a CTL function on a <see cref="OpusMSEncoderSafeHandle"/>.
        /// </summary>
        /// <param name="st">Multistream encoder state.</param>
        /// <param name="request">This and all remaining parameters should be replaced by one of the convenience macros in <see cref="GenericCTL"/>, <see cref="EncoderCTL"/>, or <see cref="MultistreamCTL"/> specific encoder and decoder CTLs.</param>
        /// <param name="data">The input/output data.</param>
        /// <param name="data2">The input/output data.</param>
        /// <returns><see cref="OpusErrorCodes"/></returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int opus_multistream_encoder_ctl(OpusMSEncoderSafeHandle st, int request, void* data, void* data2);

        //Multistream Decoder
        /// <summary>
        /// Gets the size of an <see cref="OpusMSDecoderSafeHandle"/> structure.
        /// </summary>
        /// <param name="streams">The total number of streams coded in the input. This must be no more than 255.</param>
        /// <param name="coupled_streams">Number streams to decode as coupled (2 channel) streams. This must be no larger than the total number of streams. Additionally, The total number of coded channels (streams + coupled_streams) must be no more than 255.</param>
        /// <returns>The size in bytes on success, or a negative error code (see <see cref="OpusErrorCodes"/>) on error.</returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int opus_multistream_decoder_get_size(int streams, int coupled_streams);

        /// <summary>
        /// Allocates and initializes a <see cref="OpusMSDecoderSafeHandle"/> state.
        /// </summary>
        /// <param name="Fs">Sampling rate to decode at (in Hz). This must be one of 8000, 12000, 16000, 24000, or 48000.</param>
        /// <param name="channels">Number of channels to output. This must be at most 255. It may be different from the number of coded channels (streams + coupled_streams).</param>
        /// <param name="streams">The total number of streams coded in the input. This must be no more than 255.</param>
        /// <param name="coupled_streams">Number of streams to decode as coupled (2 channel) streams. This must be no larger than the total number of streams. Additionally, The total number of coded channels (streams + coupled_streams) must be no more than 255.</param>
        /// <param name="mapping">Mapping from coded channels to output channels, as described in Opus Multistream API.</param>
        /// <param name="error">Returns <see cref="OpusErrorCodes.OPUS_OK"/> on success, or an error code (see <see cref="OpusErrorCodes"/>) on failure.</param>
        /// <returns><see cref="OpusMSDecoderSafeHandle"/></returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe OpusMSDecoderSafeHandle opus_multistream_decoder_create(int Fs, int channels, int streams, int coupled_streams, byte* mapping, int* error);

        /// <summary>
        /// Initialize a previously allocated <see cref="OpusMSDecoderSafeHandle"/> state object.
        /// </summary>
        /// <param name="st">Multistream encoder state to initialize.</param>
        /// <param name="Fs">Sampling rate to decode at (in Hz). This must be one of 8000, 12000, 16000, 24000, or 48000.</param>
        /// <param name="channels">Number of channels to output. This must be at most 255. It may be different from the number of coded channels (streams + coupled_streams).</param>
        /// <param name="streams">The total number of streams coded in the input. This must be no more than 255.</param>
        /// <param name="coupled_streams">Number of streams to decode as coupled (2 channel) streams. This must be no larger than the total number of streams. Additionally, The total number of coded channels (streams + coupled_streams) must be no more than 255.</param>
        /// <param name="mapping">Mapping from coded channels to output channels, as described in Opus Multistream API.</param>
        /// <returns><see cref="OpusErrorCodes.OPUS_OK"/> on success, or an error code (see <see cref="OpusErrorCodes"/>) on failure.</returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int opus_multistream_decoder_init(OpusMSDecoderSafeHandle st, int Fs, int channels, int streams, int coupled_streams, byte* mapping);

        /// <summary>
        /// Decode a multistream Opus packet.
        /// </summary>
        /// <param name="st">Multistream decoder state.</param>
        /// <param name="data">Input payload. Use a NULL pointer to indicate packet loss.</param>
        /// <param name="len">Number of bytes in payload.</param>
        /// <param name="pcm">Output signal, with interleaved samples. This must contain room for frame_size*channels samples.</param>
        /// <param name="frame_size">The number of samples per channel of available space in pcm. If this is less than the maximum packet duration (120 ms; 5760 for 48kHz), this function will not be capable of decoding some packets. In the case of PLC (data==NULL) or FEC (decode_fec=1), then frame_size needs to be exactly the duration of audio that is missing, otherwise the decoder will not be in the optimal state to decode the next incoming packet. For the PLC and FEC cases, frame_size must be a multiple of 2.5 ms.</param>
        /// <param name="decode_fec">Flag (0 or 1) to request that any in-band forward error correction data be decoded. If no such data is available, the frame is decoded as if it were lost.</param>
        /// <returns>Number of samples decoded on success or a negative error code (see <see cref="OpusErrorCodes"/>) on failure.</returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int opus_multistream_decode(OpusMSDecoderSafeHandle st, byte* data, int len, short* pcm, int frame_size, int decode_fec);

        /// <summary>
        /// Decode a multistream Opus packet with floating point output.
        /// </summary>
        /// <param name="st">Multistream decoder state.</param>
        /// <param name="data">Input payload. Use a NULL pointer to indicate packet loss.</param>
        /// <param name="len">Number of bytes in payload.</param>
        /// <param name="pcm">Output signal, with interleaved samples. This must contain room for frame_size*channels samples.</param>
        /// <param name="frame_size">The number of samples per channel of available space in pcm. If this is less than the maximum packet duration (120 ms; 5760 for 48kHz), this function will not be capable of decoding some packets. In the case of PLC (data==NULL) or FEC (decode_fec=1), then frame_size needs to be exactly the duration of audio that is missing, otherwise the decoder will not be in the optimal state to decode the next incoming packet. For the PLC and FEC cases, frame_size must be a multiple of 2.5 ms.</param>
        /// <param name="decode_fec">Flag (0 or 1) to request that any in-band forward error correction data be decoded. If no such data is available, the frame is decoded as if it were lost.</param>
        /// <returns>Number of samples decoded on success or a negative error code (see <see cref="OpusErrorCodes"/>) on failure.</returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int opus_multistream_decode_float(OpusMSDecoderSafeHandle st, byte* data, int len, float* pcm, int frame_size, int decode_fec);

        /// <summary>
        /// Perform a CTL function on a <see cref="OpusMSEncoderSafeHandle"/>.
        /// </summary>
        /// <param name="st">Multistream decoder state.</param>
        /// <param name="request">This and all remaining parameters should be replaced by one of the convenience macros in <see cref="GenericCTL"/>, <see cref="DecoderCTL"/>, or <see cref="MultistreamCTL"/> specific encoder and decoder CTLs.</param>
        /// <returns><see cref="OpusErrorCodes"/></returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int opus_multistream_decoder_ctl(OpusMSDecoderSafeHandle st, int request); //Apparently GenericCTL.OPUS_RESET_STATE exists.

        /// <summary>
        /// Perform a CTL function on a <see cref="OpusMSEncoderSafeHandle"/>.
        /// </summary>
        /// <param name="st">Multistream decoder state.</param>
        /// <param name="request">This and all remaining parameters should be replaced by one of the convenience macros in <see cref="GenericCTL"/>, <see cref="DecoderCTL"/>, or <see cref="MultistreamCTL"/> specific encoder and decoder CTLs.</param>
        /// <param name="data">The input data.</param>
        /// <returns><see cref="OpusErrorCodes"/></returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int opus_multistream_decoder_ctl(OpusMSDecoderSafeHandle st, int request, int data);
        
        /// <summary>
        /// Perform a CTL function on a <see cref="OpusMSEncoderSafeHandle"/>.
        /// </summary>
        /// <param name="st">Multistream decoder state.</param>
        /// <param name="request">This and all remaining parameters should be replaced by one of the convenience macros in <see cref="GenericCTL"/>, <see cref="DecoderCTL"/>, or <see cref="MultistreamCTL"/> specific encoder and decoder CTLs.</param>
        /// <param name="data">The input/output data.</param>
        /// <returns><see cref="OpusErrorCodes"/></returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int opus_multistream_decoder_ctl(OpusMSDecoderSafeHandle st, int request, void* data);

        /// <summary>
        /// Frees an <see cref="OpusMSDecoderSafeHandle"/> allocated by <see cref="opus_multistream_decoder_create(int, int, int, int, byte*, int*)"/>.
        /// </summary>
        /// <param name="st">Multistream decoder state to be freed.</param>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void opus_multistream_decoder_destroy(IntPtr st);

        //Library Information
        /// <summary>
        /// Gets the libopus version string.
        /// </summary>
        /// <returns>Version string</returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe byte* opus_get_version_string();

        /// <summary>
        /// Converts an opus error code into a human-readable string.
        /// </summary>
        /// <param name="error">Error number.</param>
        /// <returns>Error string.</returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe byte* opus_strerror(int error);
    }
}