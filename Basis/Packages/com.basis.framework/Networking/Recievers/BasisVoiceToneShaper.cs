using Unity.Burst;
using UnityEngine;

namespace Basis.Scripts.Networking.Receivers
{
    [BurstCompile]
    internal static unsafe class BasisVoiceToneShaperKernel
    {
        [BurstCompile]
        public static void Shelve(float* data, int channels, int frames, float directivityCoeff, float headShadowCoeff, float directivityGain, float headShadowGain, float directivityStep, float headShadowStep, float* directivityLpState, float* headShadowLpState)
        {
            float directivityLp = *directivityLpState, headShadowLp = *headShadowLpState;
            int index = 0;
            for (int f = 0; f < frames; f++)
            {
                float x = data[index];
                directivityLp += directivityCoeff * (x - directivityLp);
                float y = directivityLp + directivityGain * (x - directivityLp);
                headShadowLp += headShadowCoeff * (y - headShadowLp);
                y = headShadowLp + headShadowGain * (y - headShadowLp);
                for (int c = 0; c < channels; c++) data[index++] = y;
                directivityGain += directivityStep;
                headShadowGain += headShadowStep;
            }
            *directivityLpState = directivityLp;
            *headShadowLpState = headShadowLp;
        }
    }

    [BurstCompile]
    internal static unsafe class BasisVoiceOutputKernel
    {
        [BurstCompile]
        public static void Stats(float* src, int frames, double* sumSq, float* peak)
        {
            double s = 0.0;
            float p = 0f;
            for (int f = 0; f < frames; f++)
            {
                float v = src[f];
                float m = v < 0f ? -v : v;
                if (m > p) p = m;
                s += (double)v * v;
            }
            *sumSq = s;
            *peak = p;
        }

        [BurstCompile]
        public static void GainWrite(float* src, float* dst, int frames, int channels, float gain, float gainStep, float ceiling, float ceilingStep, float norm, float normStep, float* envelope, float fadeRamp, float* lastWritten)
        {
            float env = *envelope, last = *lastWritten;
            int idx = 0;
            for (int f = 0; f < frames; f++)
            {
                if (env < 1f) { env += fadeRamp; if (env > 1f) env = 1f; }
                float sample = BasisAudioReceiver.SoftLimit(src[f] * norm * gain * env, ceiling);
                for (int c = 0; c < channels; c++) dst[idx++] = sample;
                last = sample;
                gain += gainStep;
                ceiling += ceilingStep;
                norm += normStep;
            }
            *envelope = env;
            *lastWritten = last;
        }

        [BurstCompile]
        public static void FadeOut(float* dst, int frames, int channels, float* envelope, float last, float fadeRamp)
        {
            float env = *envelope;
            int idx = 0;
            for (int f = 0; f < frames; f++)
            {
                env -= fadeRamp;
                if (env < 0f) env = 0f;
                float sample = last * env;
                for (int c = 0; c < channels; c++) dst[idx++] = sample;
            }
            *envelope = env;
        }
    }

    /// <summary>
    /// Two cascaded one-pole high shelves applied to a remote voice on the audio
    /// thread: one for the talker's mouth directivity, one for the listener's own
    /// head being in the way. Together they carry the frequency-dependent part of
    /// the spatial model — the part Unity's distance rolloff and Steam Audio's
    /// broadband dipole structurally cannot express.
    ///
    /// Each shelf is <c>H(z) = g + (1-g)·LP(z)</c>: unity below the corner,
    /// exactly <c>g</c> above it, 4 ops per sample and no allocation. Both shelf
    /// gains are ramped across the callback, because the angles driving them move
    /// every time either head turns and a per-callback step would zipper.
    /// </summary>
    public sealed class BasisVoiceToneShaper
    {
        private float _directivityLp;
        private float _headShadowLp;

        private float _directivityCoeff;
        private float _headShadowCoeff;
        private int _coeffRate = -1;

        private float _directivityGain = 1f;
        private float _headShadowGain = 1f;
        private bool _primed;

        /// <summary>Clears the filter state. Call whenever the source is
        /// re-enabled, so a new utterance does not inherit the tail of the
        /// previous one's delay line.</summary>
        public void Reset()
        {
            _directivityLp = 0f;
            _headShadowLp = 0f;
            _primed = false;
        }

        private void EnsureCoefficients(int sampleRate)
        {
            if (_coeffRate == sampleRate) return;
            _coeffRate = sampleRate;
            _directivityCoeff = BasisVoiceAcoustics.ShelfCoefficient(
                BasisVoiceAcoustics.DirectivityCornerHz, sampleRate);
            _headShadowCoeff = BasisVoiceAcoustics.ShelfCoefficient(
                BasisVoiceAcoustics.HeadShadowCornerHz, sampleRate);
        }

        /// <summary>
        /// Filters <paramref name="data"/> in place. The buffer is interleaved but
        /// every channel holds the same mono voice sample, so one filter pass runs
        /// over channel 0 and the result is written back across the frame.
        /// </summary>
        /// <param name="data">Interleaved output buffer.</param>
        /// <param name="channels">Channels in <paramref name="data"/>.</param>
        /// <param name="frames">Frames to process.</param>
        /// <param name="sampleRate">Output device rate.</param>
        /// <param name="directivityShelfDb">Mouth-directivity shelf depth, &lt;= 0.</param>
        /// <param name="headShadowShelfDb">Listener-cone shelf depth, &lt;= 0.</param>
        public void Process(float[] data, int channels, int frames, int sampleRate,
                            float directivityShelfDb, float headShadowShelfDb)
        {
            if (data == null || channels <= 0 || frames <= 0) return;
            EnsureCoefficients(sampleRate);

            float targetDirectivity = DbToGain(directivityShelfDb);
            float targetHeadShadow = DbToGain(headShadowShelfDb);

            if (!_primed)
            {
                _directivityGain = targetDirectivity;
                _headShadowGain = targetHeadShadow;
                _primed = true;
            }

            // Nothing to do when both shelves are flat and neither is mid-ramp:
            // the filter is an identity, and skipping keeps the common case
            // (everyone facing everyone, cone off) free. State is still cleared so
            // a later engage cannot leak a stale tail.
            if (targetDirectivity >= 0.999f && targetHeadShadow >= 0.999f
                && _directivityGain >= 0.999f && _headShadowGain >= 0.999f)
            {
                _directivityLp = 0f;
                _headShadowLp = 0f;
                return;
            }

            if ((long)frames * channels > data.Length) frames = data.Length / channels;
            float directivityStep = (targetDirectivity - _directivityGain) / frames;
            float headShadowStep = (targetHeadShadow - _headShadowGain) / frames;
            unsafe
            {
                fixed (float* p = data) fixed (float* dLp = &_directivityLp, hLp = &_headShadowLp)
                    BasisVoiceToneShaperKernel.Shelve(p, channels, frames, _directivityCoeff, _headShadowCoeff, _directivityGain, _headShadowGain, directivityStep, headShadowStep, dLp, hLp);
            }
            _directivityGain = targetDirectivity;
            _headShadowGain = targetHeadShadow;
        }

        private static float DbToGain(float db)
        {
            // NaN check first, and deliberately as !(db < 0) so NaN falls through to
            // unity gain. A NaN reaching the shelf would poison the one-pole state,
            // and a filter whose state is NaN never recovers — that voice is dead for
            // the rest of the session. The shelf depths come from a Burst job doing
            // pow() under FloatMode.Fast, which is exactly the kind of place a NaN
            // can appear from a degenerate input.
            if (!(db < 0f)) return 1f;
            if (db < -80f) db = -80f;
            return Mathf.Pow(10f, db / 20f);
        }
    }
}
