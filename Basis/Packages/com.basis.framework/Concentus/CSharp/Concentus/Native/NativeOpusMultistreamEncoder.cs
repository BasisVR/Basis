
using Concentus.Enums;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Concentus.Native
{
#if !NETSTANDARD1_1 && !NET8_0_OR_GREATER
    // SafeHandle flavor of the encoder
    internal class NativeOpusMultistreamEncoder : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid, IOpusMultiStreamEncoder
    {
        internal NativeOpusMultistreamEncoder() : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle()
        {
            NativeOpus.opus_multistream_encoder_destroy(handle);
            return true;
        }

        public static unsafe NativeOpusMultistreamEncoder Create(
              int sampleRate,
              int channelCount,
              int mapping_family,
              out int streams,
              out int coupled_streams,
              byte[] mapping,
              OpusApplication application)
        {
            fixed (byte* mappingPtr = mapping)
            {
                int error;
                NativeOpusMultistreamEncoder returnVal = NativeOpus.opus_multistream_surround_encoder_create(
                    sampleRate,
                    channelCount,
                    mapping_family,
                    out streams,
                    out coupled_streams,
                    mappingPtr,
                    (int)application,
                    out error);
                if (error != OpusError.OPUS_OK)
                {
                    returnVal.Dispose();
                    throw new Exception($"Failed to create opus MS encoder: error {error}");
                }

                returnVal._sampleRate = sampleRate;
                returnVal._numChannels = channelCount;
                return returnVal;
            }
        }

        private NativeOpusMultistreamEncoder NativeHandle => this;
#else
    // IntPtr flavor of the encoder
    internal class NativeOpusMultistreamEncoder : IOpusMultiStreamEncoder
    {
        private readonly IntPtr _handle = IntPtr.Zero;
        private int _disposed = 0;

        internal NativeOpusMultistreamEncoder(IntPtr handle)
        {
            _handle = handle;
        }

        ~NativeOpusMultistreamEncoder()
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
                NativeOpus.opus_multistream_encoder_destroy(_handle);
            }
        }

        public static unsafe NativeOpusMultistreamEncoder Create(
              int sampleRate,
              int channelCount,
              int mapping_family,
              out int streams,
              out int coupled_streams,
              byte[] mapping,
              OpusApplication application)
        {
            fixed (byte* mappingPtr = mapping)
            {
                int error;
                IntPtr handle = NativeOpus.opus_multistream_surround_encoder_create(
                    sampleRate,
                    channelCount,
                    mapping_family,
                    out streams,
                    out coupled_streams,
                    mappingPtr,
                    (int)application,
                    out error);
                if (error != OpusError.OPUS_OK)
                {
                    if (handle != IntPtr.Zero)
                    {
                        NativeOpus.opus_multistream_encoder_destroy(handle);
                    }

                    throw new Exception($"Failed to create opus MS encoder: error {error}");
                }

                NativeOpusMultistreamEncoder returnVal = new NativeOpusMultistreamEncoder(handle);
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
        public unsafe int EncodeMultistream(ReadOnlySpan<float> in_pcm, int frame_size, Span<byte> out_data, int max_data_bytes)
        {
            fixed (float* inPtr = in_pcm)
            fixed (byte* outPtr = out_data)
            {
                return NativeOpus.opus_multistream_encode_float(NativeHandle, inPtr, frame_size, outPtr, max_data_bytes);
            }
        }

        /// <inheritdoc/>
        public unsafe int EncodeMultistream(ReadOnlySpan<short> in_pcm, int frame_size, Span<byte> out_data, int max_data_bytes)
        {
            fixed (short* inPtr = in_pcm)
            fixed (byte* outPtr = out_data)
            {
                return NativeOpus.opus_multistream_encode(NativeHandle, inPtr, frame_size, outPtr, max_data_bytes);
            }
        }

        /// <inheritdoc/>
        public void ResetState()
        {
            NativeOpus.opus_multistream_encoder_ctl(NativeHandle, NativeOpus.OPUS_RESET_STATE, 0);
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
        public int Complexity
        {
            get
            {
                int returnVal;
                NativeOpus.opus_multistream_encoder_ctl(NativeHandle, NativeOpus.OPUS_GET_COMPLEXITY_REQUEST, out returnVal);
                return returnVal;
            }
            set
            {
                NativeOpus.opus_multistream_encoder_ctl(NativeHandle, NativeOpus.OPUS_SET_COMPLEXITY_REQUEST, value);
            }
        }

        /// <inheritdoc/>
        public bool UseDTX
        {
            get
            {
                int returnVal;
                NativeOpus.opus_multistream_encoder_ctl(NativeHandle, NativeOpus.OPUS_GET_DTX_REQUEST, out returnVal);
                return returnVal != 0;
            }
            set
            {
                NativeOpus.opus_multistream_encoder_ctl(NativeHandle, NativeOpus.OPUS_SET_DTX_REQUEST, value ? 1 : 0);
            }
        }

        /// <inheritdoc/>
        public int Bitrate
        {
            get
            {
                int returnVal;
                NativeOpus.opus_multistream_encoder_ctl(NativeHandle, NativeOpus.OPUS_GET_BITRATE_REQUEST, out returnVal);
                return returnVal;
            }
            set
            {
                NativeOpus.opus_multistream_encoder_ctl(NativeHandle, NativeOpus.OPUS_SET_BITRATE_REQUEST, value);
            }
        }

        /// <inheritdoc/>
        public OpusMode ForceMode
        {
            set
            {
                NativeOpus.opus_multistream_encoder_ctl(NativeHandle, NativeOpus.OPUS_SET_FORCE_MODE_REQUEST, (int)value);
            }
        }

        /// <inheritdoc/>
        public bool UseVBR
        {
            get
            {
                int returnVal;
                NativeOpus.opus_multistream_encoder_ctl(NativeHandle, NativeOpus.OPUS_GET_VBR_REQUEST, out returnVal);
                return returnVal != 0;
            }
            set
            {
                NativeOpus.opus_multistream_encoder_ctl(NativeHandle, NativeOpus.OPUS_SET_VBR_REQUEST, value ? 1 : 0);
            }
        }

        /// <inheritdoc/>
        public OpusApplication Application
        {
            get
            {
                int returnVal;
                NativeOpus.opus_multistream_encoder_ctl(NativeHandle, NativeOpus.OPUS_GET_APPLICATION_REQUEST, out returnVal);
                return (OpusApplication)returnVal;
            }
            set
            {
                NativeOpus.opus_multistream_encoder_ctl(NativeHandle, NativeOpus.OPUS_SET_APPLICATION_REQUEST, (int)value);
            }
        }

        /// <inheritdoc/>
        public OpusBandwidth Bandwidth
        {
            get
            {
                int returnVal;
                NativeOpus.opus_multistream_encoder_ctl(NativeHandle, NativeOpus.OPUS_GET_BANDWIDTH_REQUEST, out returnVal);
                return (OpusBandwidth)returnVal;
            }
            set
            {
                NativeOpus.opus_multistream_encoder_ctl(NativeHandle, NativeOpus.OPUS_SET_BANDWIDTH_REQUEST, (int)value);
            }
        }

        /// <inheritdoc/>
        public OpusFramesize ExpertFrameDuration
        {
            get
            {
                int returnVal;
                NativeOpus.opus_multistream_encoder_ctl(NativeHandle, NativeOpus.OPUS_GET_EXPERT_FRAME_DURATION_REQUEST, out returnVal);
                return (OpusFramesize)returnVal;
            }
            set
            {
                NativeOpus.opus_multistream_encoder_ctl(NativeHandle, NativeOpus.OPUS_SET_EXPERT_FRAME_DURATION_REQUEST, (int)value);
            }
        }

        /// <inheritdoc/>
        public uint FinalRange
        {
            get
            {
                int returnVal;
                NativeOpus.opus_multistream_encoder_ctl(NativeHandle, NativeOpus.OPUS_GET_FINAL_RANGE_REQUEST, out returnVal);
                return (uint)returnVal;
            }
        }

        /// <inheritdoc/>
        public int Lookahead
        {
            get
            {
                int returnVal;
                NativeOpus.opus_multistream_encoder_ctl(NativeHandle, NativeOpus.OPUS_GET_LOOKAHEAD_REQUEST, out returnVal);
                return returnVal;
            }
        }

        /// <inheritdoc/>
        public int LSBDepth
        {
            get
            {
                int returnVal;
                NativeOpus.opus_multistream_encoder_ctl(NativeHandle, NativeOpus.OPUS_GET_LSB_DEPTH_REQUEST, out returnVal);
                return returnVal;
            }
            set
            {
                NativeOpus.opus_multistream_encoder_ctl(NativeHandle, NativeOpus.OPUS_SET_LSB_DEPTH_REQUEST, value);
            }
        }

        /// <inheritdoc/>
        public OpusBandwidth MaxBandwidth
        {
            get
            {
                int returnVal;
                NativeOpus.opus_multistream_encoder_ctl(NativeHandle, NativeOpus.OPUS_GET_MAX_BANDWIDTH_REQUEST, out returnVal);
                return (OpusBandwidth)returnVal;
            }
            set
            {
                NativeOpus.opus_multistream_encoder_ctl(NativeHandle, NativeOpus.OPUS_SET_MAX_BANDWIDTH_REQUEST, (int)value);
            }
        }

        /// <inheritdoc/>
        public int PacketLossPercent
        {
            get
            {
                int returnVal;
                NativeOpus.opus_multistream_encoder_ctl(NativeHandle, NativeOpus.OPUS_GET_PACKET_LOSS_PERC_REQUEST, out returnVal);
                return returnVal;
            }
            set
            {
                NativeOpus.opus_multistream_encoder_ctl(NativeHandle, NativeOpus.OPUS_SET_PACKET_LOSS_PERC_REQUEST, value);
            }
        }

        /// <inheritdoc/>
        public bool PredictionDisabled
        {
            get
            {
                int returnVal;
                NativeOpus.opus_multistream_encoder_ctl(NativeHandle, NativeOpus.OPUS_GET_PREDICTION_DISABLED_REQUEST, out returnVal);
                return returnVal != 0;
            }
            set
            {
                NativeOpus.opus_multistream_encoder_ctl(NativeHandle, NativeOpus.OPUS_SET_PREDICTION_DISABLED_REQUEST, value ? 1 : 0);
            }
        }
        /// <inheritdoc/>
        public OpusSignal SignalType
        {
            get
            {
                int returnVal;
                NativeOpus.opus_multistream_encoder_ctl(NativeHandle, NativeOpus.OPUS_GET_SIGNAL_REQUEST, out returnVal);
                return (OpusSignal)returnVal;
            }
            set
            {
                NativeOpus.opus_multistream_encoder_ctl(NativeHandle, NativeOpus.OPUS_SET_SIGNAL_REQUEST, (int)value);
            }
        }

        /// <inheritdoc/>
        public bool UseConstrainedVBR
        {
            get
            {
                int returnVal;
                NativeOpus.opus_multistream_encoder_ctl(NativeHandle, NativeOpus.OPUS_GET_VBR_CONSTRAINT_REQUEST, out returnVal);
                return returnVal != 0;
            }
            set
            {
                NativeOpus.opus_multistream_encoder_ctl(NativeHandle, NativeOpus.OPUS_SET_VBR_CONSTRAINT_REQUEST, value ? 1 : 0);
            }
        }

        /// <inheritdoc/>
        public bool UseInbandFEC
        {
            get
            {
                int returnVal;
                NativeOpus.opus_multistream_encoder_ctl(NativeHandle, NativeOpus.OPUS_GET_INBAND_FEC_REQUEST, out returnVal);
                return returnVal != 0;
            }
            set
            {
                NativeOpus.opus_multistream_encoder_ctl(NativeHandle, NativeOpus.OPUS_SET_INBAND_FEC_REQUEST, value ? 1 : 0);
            }
        }
    }
}