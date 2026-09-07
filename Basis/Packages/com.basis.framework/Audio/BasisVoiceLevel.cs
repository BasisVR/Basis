using UnityEngine;

namespace Basis.Scripts.Audio
{
    /// <summary>
    /// Turns a voice signal's RMS into the 0..1 loudness that avatars and UI animate against,
    /// so the microphone meter, a remote speaker's avatar and anything an avatar author binds
    /// all read the same scale.
    /// <para>The mapping is the one <c>BasisLocalVolumeMeterUI</c> already draws with: RMS to
    /// dBFS, then <see cref="MinDb"/>..<see cref="MaxDb"/> remapped onto 0..1. Linear amplitude
    /// is the wrong scale to hand an avatar — the AGC lands speech near 0.1 RMS, so a linear
    /// parameter would spend a whole conversation between 0.05 and 0.2.</para>
    /// <para>Levels are always measured BEFORE the listener's attenuation — distance, per-player
    /// volume, the directivity cone — so anything driven by them reads the same to everyone in
    /// the room rather than dimming as you walk away, or sitting at 0 for whoever turned the
    /// speaker down.</para>
    /// </summary>
    public static class BasisVoiceLevel
    {
        /// <summary>dBFS mapped to 0.</summary>
        public const float MinDb = -60f;

        /// <summary>dBFS mapped to 1.</summary>
        public const float MaxDb = 0f;

        /// <summary>Time constant for rising toward a louder level.</summary>
        public const float AttackSeconds = 0.06f;

        /// <summary>Time constant for falling toward a quieter level.</summary>
        public const float ReleaseSeconds = 0.20f;

        private const float MinRms = 1e-7f;
        private const float RestRms = 1e-6f;

        private static volatile float _localVoiceRms;

        /// <summary>
        /// RMS of the local player's outgoing voice as everyone else receives it — after the
        /// microphone chain's gain, denoise, gate, AGC and limiter, and 0 while muted or while
        /// no capture device is running. Written by the microphone processing thread.
        /// </summary>
        public static float LocalVoiceRms
        {
            get => _localVoiceRms;
            set => _localVoiceRms = value;
        }

        /// <summary>
        /// Maps a linear RMS amplitude onto 0..1 through the shared dBFS window.
        /// </summary>
        public static float RmsToUnit(float rms)
        {
            // Negated so a NaN — which every comparison answers false to, and which would otherwise
            // ride the log straight into an avatar's material — takes the silent branch.
            if (!(rms > 0f))
            {
                return 0f;
            }

            float db = 20f * Mathf.Log10(Mathf.Max(MinRms, rms));
            return Mathf.Clamp01(Mathf.InverseLerp(MinDb, MaxDb, db));
        }

        /// <summary>
        /// Advances an RMS envelope toward <paramref name="target"/> with the shared attack and
        /// release. Expressed in seconds rather than in blocks so the microphone's 20 ms frames
        /// and an audio callback of any length settle at the same rate.
        /// </summary>
        public static float Follow(float current, float target, float deltaSeconds)
        {
            if (deltaSeconds <= 0f)
            {
                return current;
            }

            // A NaN sample reaching the envelope would stay in it for the rest of the session.
            if (!(target >= 0f))
            {
                target = 0f;
            }

            float tau = target > current ? AttackSeconds : ReleaseSeconds;
            float next = current + (target - current) * (1f - Mathf.Exp(-deltaSeconds / tau));
            return next < RestRms ? 0f : next;
        }
    }
}
