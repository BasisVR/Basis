using System;
using OpusSharp.Core.Interfaces;

// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable FieldCanBeMadeReadOnly.Global
// ReSharper disable InconsistentNaming
namespace OpusSharp.Core
{
    /// <summary>
    /// An opus encoder.
    /// </summary>
    public class OpusEncoder : IOpusEncoder
    {
        /// <summary>
        /// Direct opus encoder for the <see cref="OpusEncoder"/>. You can close this directly.
        /// </summary>
        protected IOpusEncoder _encoder;

        /// <summary>
        /// Creates a new opus encoder.
        /// </summary>
        /// <param name="sample_rate">The sample rate, this must be one of 8000, 12000, 16000, 24000, or 48000.</param>
        /// <param name="channels">Number of channels, this must be 1 or 2.</param>
        /// <param name="use_static">Set to <see langword="true"/> to force static imports, <see langword="false"/> to force dynamic imports, or <see langword="null"/> to auto-select based on platform.</param>
        /// <param name="application">Coding mode (one of <see cref="OpusPredefinedValues.OPUS_APPLICATION_VOIP"/>, <see cref="OpusPredefinedValues.OPUS_APPLICATION_AUDIO"/> or <see cref="OpusPredefinedValues.OPUS_APPLICATION_RESTRICTED_LOWDELAY"/></param>
        /// <exception cref="OpusException" />
        public OpusEncoder(int sample_rate, int channels, OpusPredefinedValues application,
            bool? use_static = null)
        {
            var useStatic = OpusRuntime.ShouldUseStaticImports(use_static);
            _encoder = useStatic
                ? (IOpusEncoder)new Static.OpusEncoder(sample_rate, channels, application)
                : new Dynamic.OpusEncoder(sample_rate, channels, application);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            _encoder.Dispose();
            GC.SuppressFinalize(this);
        }

#if NETSTANDARD2_1_OR_GREATER || NET8_0_OR_GREATER
        /// <inheritdoc/>
        public int Encode(Span<byte> input, int frame_size, Span<byte> output, int max_data_bytes)
        {
            return _encoder.Encode(input, frame_size, output, max_data_bytes);
        }

        /// <inheritdoc/>
        public int Encode(Span<short> input, int frame_size, Span<byte> output, int max_data_bytes)
        {
            return _encoder.Encode(input, frame_size, output, max_data_bytes);
        }

        /// <inheritdoc/>
        public int Encode(Span<int> input, int frame_size, Span<byte> output, int max_data_bytes)
        {
            return _encoder.Encode(input, frame_size, output, max_data_bytes);
        }

        /// <inheritdoc/>
        public int Encode(Span<float> input, int frame_size, Span<byte> output, int max_data_bytes)
        {
            return _encoder.Encode(input, frame_size, output, max_data_bytes);
        }
#endif

        /// <inheritdoc/>
        public int Encode(byte[] input, int frame_size, byte[] output, int max_data_bytes)
        {
            return _encoder.Encode(input, frame_size, output, max_data_bytes);
        }

        /// <inheritdoc/>
        public int Encode(short[] input, int frame_size, byte[] output, int max_data_bytes)
        {
            return _encoder.Encode(input, frame_size, output, max_data_bytes);
        }

        /// <inheritdoc/>
        public int Encode(int[] input, int frame_size, byte[] output, int max_data_bytes)
        {
            return _encoder.Encode(input, frame_size, output, max_data_bytes);
        }

        /// <inheritdoc/>
        public int Encode(float[] input, int frame_size, byte[] output, int max_data_bytes)
        {
            return _encoder.Encode(input, frame_size, output, max_data_bytes);
        }

        /// <inheritdoc/>
        public int Ctl(EncoderCTL request)
        {
            return _encoder.Ctl(request);
        }

        /// <inheritdoc/>
        public int Ctl(EncoderCTL request, int value)
        {
            return _encoder.Ctl(request, value);
        }

        /// <inheritdoc/>
        public int Ctl<T>(EncoderCTL request, ref T value) where T : unmanaged
        {
            return _encoder.Ctl(request, ref value);
        }

        /// <inheritdoc/>
        public int Ctl<T>(EncoderCTL request, ref T value, int value2)
            where T : unmanaged
        {
            return _encoder.Ctl(request, ref value, value2);
        }

        /// <inheritdoc/>
        public int Ctl<T>(EncoderCTL request, int value, ref T value2)
            where T : unmanaged
        {
            return _encoder.Ctl(request, value, ref value2);
        }

        /// <inheritdoc/>
        public int Ctl<T, T2>(EncoderCTL request, ref T value, ref T2 value2)
            where T : unmanaged
            where T2 : unmanaged
        {
            return _encoder.Ctl(request, ref value, ref value2);
        }

        /// <inheritdoc/>
        public int Ctl(GenericCTL request)
        {
            return _encoder.Ctl(request);
        }

        /// <inheritdoc/>
        public int Ctl(GenericCTL request, int value)
        {
            return _encoder.Ctl(request, value);
        }

        /// <inheritdoc/>
        public int Ctl<T>(GenericCTL request, ref T value) where T : unmanaged
        {
            return _encoder.Ctl(request, ref value);
        }
    }
}