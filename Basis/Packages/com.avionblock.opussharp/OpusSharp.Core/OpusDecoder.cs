using System;
using OpusSharp.Core.Interfaces;

// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable FieldCanBeMadeReadOnly.Global
// ReSharper disable InconsistentNaming
// ReSharper disable ClassNeverInstantiated.Global
namespace OpusSharp.Core
{
    /// <summary>
    /// An opus decoder.
    /// </summary>
    public class OpusDecoder : IOpusDecoder
    {
        /// <summary>
        /// Direct opus decoder for the <see cref="OpusDecoder"/>. You can close this directly.
        /// </summary>
        protected IOpusDecoder _decoder;

        /// <summary>
        /// Creates a new opus decoder.
        /// </summary>
        /// <param name="sample_rate">The sample rate, this must be one of 8000, 12000, 16000, 24000, or 48000.</param>
        /// <param name="channels">Number of channels, this must be 1 or 2.</param>
        /// <param name="use_static">Set to <see langword="true"/> to force static imports, <see langword="false"/> to force dynamic imports, or <see langword="null"/> to auto-select based on platform.</param>
        /// <exception cref="OpusException" />
        public OpusDecoder(int sample_rate, int channels, bool? use_static = null)
        {
            var useStatic = OpusRuntime.ShouldUseStaticImports(use_static);
            _decoder = useStatic
                ? (IOpusDecoder) new Static.OpusDecoder(sample_rate, channels)
                : new Dynamic.OpusDecoder(sample_rate, channels);
        }
        
        /// <inheritdoc/>
        public void Dispose()
        {
            _decoder.Dispose();
            GC.SuppressFinalize(this);
        }

#if NETSTANDARD2_1_OR_GREATER || NET8_0_OR_GREATER
        /// <inheritdoc/>
        public int Decode(Span<byte> input, int length, Span<byte> output, int frame_size, bool decode_fec)
        {
            return _decoder.Decode(input, length, output, frame_size, decode_fec);
        }

        /// <inheritdoc/>
        public int Decode(Span<byte> input, int length, Span<short> output, int frame_size, bool decode_fec)
        {
            return _decoder.Decode(input, length, output, frame_size, decode_fec);
        }
        
        /// <inheritdoc/>
        public int Decode(Span<byte> input, int length, Span<int> output, int frame_size, bool decode_fec)
        {
            return _decoder.Decode(input, length, output, frame_size, decode_fec);
        }

        /// <inheritdoc/>
        public int Decode(Span<byte> input, int length, Span<float> output, int frame_size, bool decode_fec)
        {
            return _decoder.Decode(input, length, output, frame_size, decode_fec);
        }
#endif

        /// <inheritdoc/>
        public int Decode(byte[]? input, int length, byte[] output, int frame_size, bool decode_fec)
        {
            return _decoder.Decode(input, length, output, frame_size, decode_fec);
        }

        /// <inheritdoc/>
        public int Decode(byte[]? input, int length, short[] output, int frame_size, bool decode_fec)
        {
            return _decoder.Decode(input, length, output, frame_size, decode_fec);
        }

        /// <inheritdoc/>
        public int Decode(byte[]? input, int length, int[] output, int frame_size, bool decode_fec)
        {
            return _decoder.Decode(input, length, output, frame_size, decode_fec);
        }

        /// <inheritdoc/>
        public int Decode(byte[]? input, int length, float[] output, int frame_size, bool decode_fec)
        {
            return _decoder.Decode(input, length, output, frame_size, decode_fec);
        }

        /// <inheritdoc/>
        public int Ctl(DecoderCTL request)
        {
            return _decoder.Ctl(request);
        }

        /// <inheritdoc/>
        public int Ctl(DecoderCTL request, int value)
        {
            return _decoder.Ctl(request, value);
        }

        /// <inheritdoc/>
        public int Ctl<T>(DecoderCTL request, ref T value) where T : unmanaged
        {
            return _decoder.Ctl(request, ref value);
        }

        /// <inheritdoc/>
        public int Ctl(GenericCTL request)
        {
            return _decoder.Ctl(request);
        }

        /// <inheritdoc/>
        public int Ctl(GenericCTL request, int value)
        {
            return _decoder.Ctl(request, value);
        }

        /// <inheritdoc/>
        public int Ctl<T>(GenericCTL request, ref T value) where T : unmanaged
        {
            return _decoder.Ctl(request, ref value);
        }
    }
}
