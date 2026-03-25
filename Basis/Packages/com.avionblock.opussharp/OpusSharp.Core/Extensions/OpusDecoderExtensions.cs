using System;

namespace OpusSharp.Core.Extensions
{
    /// <summary>
    /// Contains the <see cref="OpusDecoder"/> helper extensions.
    /// </summary>
    /// <remarks>OPUS_SET_COMPLEXITY and OPUS_GET_COMPLEXITY have not been added since they aren't documented yet in the opus documentation.</remarks>
    public static class OpusDecoderExtensions
    {
        /// <summary>
        /// Sets gain for an opus decoder.
        /// </summary>
        /// <param name="decoder">The decoder state.</param>
        /// <param name="gain">The gain to set.</param>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public static void SetGain(this OpusDecoder decoder, short gain)
        {
            decoder.Ctl(DecoderCTL.OPUS_SET_GAIN, gain);
        }

        /// <summary>
        /// Gets the gain for an opus decoder.
        /// </summary>
        /// <param name="decoder">The decoder state.</param>
        /// <returns>The gain for the opus decoder.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public static int GetGain(this OpusDecoder decoder)
        {
            var gain = 0;
            decoder.Ctl(DecoderCTL.OPUS_GET_GAIN, ref gain);
            return gain;
        }

        /// <summary>
        /// Gets the duration (in samples) of the last packet successfully decoded or concealed.
        /// </summary>
        /// <param name="decoder">The decoder state.</param>
        /// <returns>The last packet duration (in samples).</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public static int GetLastPacketDuration(this OpusDecoder decoder)
        {
            var lastPacketDuration = 0;
            decoder.Ctl(DecoderCTL.OPUS_GET_LAST_PACKET_DURATION, ref lastPacketDuration);
            return lastPacketDuration;
        }

        /// <summary>
        /// Gets the pitch of the last decoded frame, if available.
        /// </summary>
        /// <param name="decoder">The decoder state.</param>
        /// <returns>The pitch of the last decoded frame if available.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public static int GetPitch(this OpusDecoder decoder)
        {
            var pitch = 0;
            decoder.Ctl(DecoderCTL.OPUS_GET_PITCH, ref pitch);
            return pitch;
        }
        
        /// <summary>
        /// Enables or disables the decoder's OSCE BWE module.
        /// </summary>
        /// <param name="decoder">The decoder state.</param>
        /// <param name="enabled">Whether to enable or disable the OSCE BWE module.</param>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public static void SetOsceBwe(this OpusDecoder decoder, bool enabled)
        {
            decoder.Ctl(DecoderCTL.OPUS_SET_OSCE_BWE, enabled ? 1 : 0);
        }

        /// <summary>
        /// Determines whether the decoder's OSCE BWE module is enabled or not.
        /// </summary>
        /// <param name="decoder">The decoder state.</param>
        /// <returns>Whether the OSCE BWE module is enabled or not.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public static bool GetOsceBwe(this OpusDecoder decoder)
        {
            var value = 0;
            decoder.Ctl(DecoderCTL.OPUS_GET_OSCE_BWE, ref value);
            return value == 1;
        }
        
        /// <summary>
        /// Sets whether the decoder should ignore extensions in the padding area.
        /// </summary>
        /// <param name="decoder">The decoder state.</param>
        /// <param name="disabled">Whether to disable all found extensions in the padding area.</param>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public static void SetIgnoreExtensions(this OpusDecoder decoder, bool disabled)
        {
            decoder.Ctl(DecoderCTL.OPUS_SET_IGNORE_EXTENSIONS, disabled ? 1 : 0);
        }

        /// <summary>
        /// Determines whether the decoder is ignoring extensions found in the padding area.
        /// </summary>
        /// <param name="decoder">The decoder state.</param>
        /// <returns>Whether the decoder is ignoring extensions.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public static bool GetIgnoreExtensions(this OpusDecoder decoder)
        {
            var value = 0;
            decoder.Ctl(DecoderCTL.OPUS_GET_IGNORE_EXTENSIONS, ref value);
            return value == 1;
        }

        /// <summary>
        /// Resets the codec state to be equivalent to a freshly initialized state.
        /// </summary>
        /// <param name="decoder">The decoder state.</param>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public static void Reset(this OpusDecoder decoder)
        {
            decoder.Ctl(GenericCTL.OPUS_RESET_STATE);
        }

        /// <summary>
        /// Gets the final state of the codec's entropy coder.
        /// </summary>
        /// <param name="decoder">The decoder state.</param>
        /// <returns>The final state of the codec's entropy coder.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public static uint GetFinalRange(this OpusDecoder decoder)
        {
            var finalRange = 0u;
            decoder.Ctl(GenericCTL.OPUS_GET_FINAL_RANGE, ref finalRange);
            return finalRange;
        }

        /// <summary>
        /// Gets the decoder's last bandpass.
        /// </summary>
        /// <param name="decoder">The decoder state.</param>
        /// <returns>The decoder's last bandpass.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public static OpusPredefinedValues GetBandwidth(this OpusDecoder decoder)
        {
            var bandPass = 0;
            decoder.Ctl(GenericCTL.OPUS_GET_BANDWIDTH, ref bandPass);
            return (OpusPredefinedValues)bandPass;
        }

        /// <summary>
        /// Gets the sampling rate the decoder was initialized with.
        /// </summary>
        /// <param name="decoder">The decoder state.</param>
        /// <returns>The decoder's configured sample rate.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public static int GetSampleRate(this OpusDecoder decoder)
        {
            var sampleRate = 0;
            decoder.Ctl(GenericCTL.OPUS_GET_SAMPLE_RATE, ref sampleRate);
            return sampleRate;
        }

        /// <summary>
        /// If set to true, disables the use of phase inversion for intensity stereo, improving the quality of mono down-mixes, but slightly reducing normal stereo quality.
        /// </summary>
        /// <param name="decoder">The decoder state.</param>
        /// <param name="disabled">Whether to disable or not.</param>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public static void SetPhaseInversionDisabled(this OpusDecoder decoder, bool disabled)
        {
            decoder.Ctl(GenericCTL.OPUS_SET_PHASE_INVERSION_DISABLED, disabled ? 1 : 0);
        }

        /// <summary>
        /// Gets the decoder's configured phase inversion status.
        /// </summary>
        /// <param name="decoder">The decoder state.</param>
        /// <returns>Whether the phase inversion is disabled or not.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public static bool GetPhaseInversionDisabled(this OpusDecoder decoder)
        {
            var disabled = 0;
            decoder.Ctl(GenericCTL.OPUS_GET_PHASE_INVERSION_DISABLED, ref disabled);
            return disabled == 1;
        }
    }
}