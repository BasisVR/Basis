using Concentus.Enums;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Concentus.Native
{
#if !NETSTANDARD1_1 && !NET8_0_OR_GREATER
    // SafeHandle flavor of the decoder
    internal class NativeOpusMultistreamDecoder : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid, IOpusMultiStreamDecoder
    {
        internal NativeOpusMultistreamDecoder() : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle()
        {
            NativeOpus.opus_multistream_decoder_destroy(handle);
            return true;
        }

        public unsafe static NativeOpusMultistreamDecoder Create(int sampleRate, int channelCount, int streams, int coupledStreams, byte[] channelMapping)
        {
            fixed (byte* mappingPtr = channelMapping)
            {
                int error;
                NativeOpusMultistreamDecoder returnVal = NativeOpus.opus_multistream_decoder_create(sampleRate, channelCount, streams, coupledStreams, mappingPtr, out error);
                if (error != OpusError.OPUS_OK)
                {
                    returnVal.Dispose();
                    throw new Exception($"Failed to create opus MS decoder: error {error}");
                }
                
                returnVal._sampleRate = sampleRate;
                returnVal._numChannels = channelCount;
                return returnVal;
            }
        }

        private NativeOpusMultistreamDecoder NativeHandle => this;
#else
    // IntPtr flavor of the decoder
    internal class NativeOpusMultistreamDecoder : IOpusMultiStreamDecoder
    {
        private readonly IntPtr _handle = IntPtr.Zero;
        private int _disposed = 0;

        internal NativeOpusMultistreamDecoder(IntPtr handle)
        {
            _handle = handle;
        }

        ~NativeOpusMultistreamDecoder()
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
                NativeOpus.opus_multistream_decoder_destroy(_handle);
            }
        }

        public unsafe static NativeOpusMultistreamDecoder Create(int sampleRate, int channelCount, int streams, int coupledStreams, byte[] channelMapping)
        {
            fixed (byte* mappingPtr = channelMapping)
            {
                int error;
                IntPtr handle = NativeOpus.opus_multistream_decoder_create(sampleRate, channelCount, streams, coupledStreams, mappingPtr, out error);
                if (error != OpusError.OPUS_OK)
                {
                    if (handle != IntPtr.Zero)
                    {
                        NativeOpus.opus_multistream_decoder_destroy(handle);
                    }

                    throw new Exception($"Failed to create opus MS decoder: error {error}");
                }

                NativeOpusMultistreamDecoder returnVal = new NativeOpusMultistreamDecoder(handle);
                returnVal._sampleRate = sampleRate;
                returnVal._numChannels = channelCount;
                return returnVal;
            }
        }

        private IntPtr NativeHandle => _handle;
#endif

        private int _sampleRate;
        private int _numChannels;

        /// <inheritdoc/>
        public unsafe int DecodeMultistream(ReadOnlySpan<byte> data, Span<float> out_pcm, int frame_size, bool decode_fec)
        {
            fixed (float* outPtr = out_pcm)
            {
                if (data.Length == 0)
                {
                    // If input is an empty span (for FEC), pass an explicit null pointer
                    return NativeOpus.opus_multistream_decode_float(NativeHandle, null, 0, outPtr, frame_size, decode_fec ? 1 : 0);
                }
                else
                {
                    fixed (byte* inPtr = data)
                    {
                        return NativeOpus.opus_multistream_decode_float(NativeHandle, inPtr, data.Length, outPtr, frame_size, decode_fec ? 1 : 0);
                    }
                }
            }
        }

        /// <inheritdoc/>
        public unsafe int DecodeMultistream(ReadOnlySpan<byte> data, Span<short> out_pcm, int frame_size, bool decode_fec)
        {
            fixed (short* outPtr = out_pcm)
            {
                if (data.Length == 0)
                {
                    // If input is an empty span (for FEC), pass an explicit null pointer
                    return NativeOpus.opus_multistream_decode(NativeHandle, null, 0, outPtr, frame_size, decode_fec ? 1 : 0);
                }
                else
                {
                    fixed (byte* inPtr = data)
                    {
                        return NativeOpus.opus_multistream_decode(NativeHandle, inPtr, data.Length, outPtr, frame_size, decode_fec ? 1 : 0);
                    }
                }
            }
        }

        /// <inheritdoc/>
        public void ResetState()
        {
            NativeOpus.opus_multistream_decoder_ctl(NativeHandle, NativeOpus.OPUS_RESET_STATE, 0);
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
                NativeOpus.opus_multistream_decoder_ctl(NativeHandle, NativeOpus.OPUS_GET_BANDWIDTH_REQUEST, out returnVal);
                return (OpusBandwidth)returnVal;
            }
        }

        /// <inheritdoc/>
        public uint FinalRange
        {
            get
            {
                int returnVal;
                NativeOpus.opus_multistream_decoder_ctl(NativeHandle, NativeOpus.OPUS_GET_FINAL_RANGE_REQUEST, out returnVal);
                return (uint)returnVal;
            }
        }

        /// <inheritdoc/>
        public int Gain
        {
            get
            {
                int returnVal;
                NativeOpus.opus_multistream_decoder_ctl(NativeHandle, NativeOpus.OPUS_GET_GAIN_REQUEST, out returnVal);
                return returnVal;
            }
            set
            {
                NativeOpus.opus_multistream_decoder_ctl(NativeHandle, NativeOpus.OPUS_SET_GAIN_REQUEST, value);
            }
        }

        /// <inheritdoc/>
        public int LastPacketDuration
        {
            get
            {
                int returnVal;
                NativeOpus.opus_multistream_decoder_ctl(NativeHandle, NativeOpus.OPUS_GET_LAST_PACKET_DURATION_REQUEST, out returnVal);
                return returnVal;
            }
        }
    }
}