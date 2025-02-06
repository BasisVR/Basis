using Concentus.Enums;
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Concentus.Native
{
#if !NETSTANDARD1_1 && !NET8_0_OR_GREATER
    // SafeHandle flavor of the decoder
    internal class NativeOpusDecoder : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid, IOpusDecoder
    {
        internal NativeOpusDecoder() : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle()
        {
            NativeOpus.opus_encoder_destroy(handle);
            return true;
        }

        public static NativeOpusDecoder Create(int sampleRate, int channelCount)
        {
            int error;
            NativeOpusDecoder returnVal = NativeOpus.opus_decoder_create(sampleRate, channelCount, out error);
            if (error != OpusError.OPUS_OK)
            {
                returnVal.Dispose();
                throw new Exception($"Failed to create opus decoder: error {error}");
            }

            returnVal._sampleRate = sampleRate;
            returnVal._numChannels = channelCount;
            return returnVal;
        }

        private NativeOpusDecoder NativeHandle => this;
#else
    // IntPtr flavor of the decoder
    internal class NativeOpusDecoder : IOpusDecoder
    {
        private readonly IntPtr _handle = IntPtr.Zero;
        private int _disposed = 0;

        internal NativeOpusDecoder(IntPtr handle)
        {
            _handle = handle;
        }

        ~NativeOpusDecoder()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
            {
                return;
            }

            if (_handle != IntPtr.Zero)
            {
                NativeOpus.opus_decoder_destroy(_handle);
            }
        }

        public static NativeOpusDecoder Create(int sampleRate, int channelCount)
        {
            int error;
            IntPtr handle = NativeOpus.opus_decoder_create(sampleRate, channelCount, out error);
            if (error != OpusError.OPUS_OK)
            {
                if (handle != IntPtr.Zero)
                {
                    NativeOpus.opus_decoder_destroy(handle);
                }

                throw new Exception($"Failed to create opus decoder: error {error}");
            }

            NativeOpusDecoder returnVal = new NativeOpusDecoder(handle);
            returnVal._sampleRate = sampleRate;
            returnVal._numChannels = channelCount;
            return returnVal;
        }
        private IntPtr NativeHandle => _handle;
#endif

        private int _sampleRate;
        private int _numChannels;

        /// <inheritdoc/>
        public unsafe int Decode(ReadOnlySpan<byte> in_data, Span<float> out_pcm, int frame_size, bool decode_fec = false)
        {
            fixed (float* outPtr = out_pcm)
            {
                if (in_data.Length == 0)
                {
                    // If input is an empty span (for FEC), pass an explicit null pointer
                    return NativeOpus.opus_decode_float(NativeHandle, null, 0, outPtr, frame_size, decode_fec ? 1 : 0);
                }
                else
                {
                    fixed (byte* inPtr = in_data)
                    {
                        return NativeOpus.opus_decode_float(NativeHandle, inPtr, in_data.Length, outPtr, frame_size, decode_fec ? 1 : 0);
                    }
                }
            }
        }

        /// <inheritdoc/>
        public unsafe int Decode(ReadOnlySpan<byte> in_data, Span<short> out_pcm, int frame_size, bool decode_fec = false)
        {
            fixed (short* outPtr = out_pcm)
            {
                if (in_data.Length == 0)
                {
                    // If input is an empty span (for FEC), pass an explicit null pointer
                    return NativeOpus.opus_decode(NativeHandle, null, 0, outPtr, frame_size, decode_fec ? 1 : 0);
                }
                else
                {
                    fixed (byte* inPtr = in_data)
                    {
                        return NativeOpus.opus_decode(NativeHandle, inPtr, in_data.Length, outPtr, frame_size, decode_fec ? 1 : 0);
                    }
                }
            }
        }

        /// <inheritdoc/>
        public void ResetState()
        {
            NativeOpus.opus_decoder_ctl(NativeHandle, NativeOpus.OPUS_RESET_STATE, 0);
        }

        /// <inheritdoc/>
        public string GetVersionString()
        {
            return Marshal.PtrToStringAnsi(NativeOpus.opus_get_version_string()); // returned pointer is hardcoded in lib so no need to free anything
        }

        /// <inheritdoc/>
        public int SampleRate => _sampleRate;

        /// <inheritdoc/>
        public int NumChannels => _numChannels;

        /// <inheritdoc/>
        public OpusBandwidth Bandwidth
        {
            get
            {
                int returnVal;
                NativeOpus.opus_decoder_ctl(NativeHandle, NativeOpus.OPUS_GET_BANDWIDTH_REQUEST, out returnVal);
                return (OpusBandwidth)returnVal;
            }
        }

        /// <inheritdoc/>
        public uint FinalRange
        {
            get
            {
                int returnVal;
                NativeOpus.opus_decoder_ctl(NativeHandle, NativeOpus.OPUS_GET_FINAL_RANGE_REQUEST, out returnVal);
                return (uint)returnVal;
            }
        }

        /// <inheritdoc/>
        public int Gain
        {
            get
            {
                int returnVal;
                NativeOpus.opus_decoder_ctl(NativeHandle, NativeOpus.OPUS_GET_GAIN_REQUEST, out returnVal);
                return returnVal;
            }
            set
            {
                NativeOpus.opus_decoder_ctl(NativeHandle, NativeOpus.OPUS_SET_GAIN_REQUEST, value);
            }
        }

        /// <inheritdoc/>
        public int LastPacketDuration
        {
            get
            {
                int returnVal;
                NativeOpus.opus_decoder_ctl(NativeHandle, NativeOpus.OPUS_GET_LAST_PACKET_DURATION_REQUEST, out returnVal);
                return returnVal;
            }
        }

        /// <inheritdoc/>
        public int Pitch
        {
            get
            {
                int returnVal;
                NativeOpus.opus_decoder_ctl(NativeHandle, NativeOpus.OPUS_GET_PITCH_REQUEST, out returnVal);
                return returnVal;
            }
        }
    }
}