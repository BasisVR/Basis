using UnityEngine;

namespace Basis.Scripts.Networking.Receivers
{
    /// <summary>
    /// The acoustic model for a remote player's voice: how loud they are at a
    /// distance, how their mouth radiates, and how much of them the listener's
    /// own head is in the way of.
    ///
    /// Everything here is pure math over floats so it can be unit tested without
    /// the audio engine. The numbers come from fitting three published data sets:
    /// Chu &amp; Warnock (NRC RR-104) for talker directivity, the direct+diffuse
    /// field split for distance, and head/torso diffraction for the listener cone.
    ///
    /// What each piece replaces:
    ///  • <see cref="DistanceGain"/> replaces a hand-drawn rolloff curve that was
    ///    flat to ~6 m and then fell off a cliff. It measured 8.3 dB RMS away from
    ///    a real talker over 0.5-15 m and gave only 21 % of the real level change
    ///    between 1 m and 4 m, which is why everyone sounded equally close.
    ///  • <see cref="DirectivityShelfDb"/> supplies the spectral tilt that Steam
    ///    Audio's dipole cannot: a real mouth turned away loses ~12 dB more at
    ///    4 kHz than at 250 Hz, while a broadband dipole only gets quieter.
    ///  • <see cref="ListenerConeTerms"/> keeps the cone-of-influence feature but
    ///    spends most of it above <see cref="HeadShadowCornerHz"/>, so a talker
    ///    behind you reads as occluded rather than as a fader being pulled.
    /// </summary>
    public static class BasisVoiceAcoustics
    {
        // ─────────────────────────── distance ───────────────────────────

        /// <summary>
        /// Distance past which level stops falling, in metres. Physically this is
        /// the critical distance: the radius at which a room's reverberant field
        /// matches the talker's direct field. Inside it you get the full inverse
        /// distance law; outside it level flattens, which is both what a real room
        /// does and what keeps far-away players audible.
        /// </summary>
        public const float DefaultReverberantDistance = 4f;

        /// <summary>
        /// Fraction of the hearing range at which the curve starts tapering to
        /// zero. Purely so the hearing-range cull is silent — the source is
        /// hard-stopped at maxDistance and released at 1.1x that, so the curve has
        /// to already be inaudible there. 0.95 puts the level at the cull radius
        /// near -58 dB while leaving the rest of the range undistorted.
        /// </summary>
        public const float DistanceTaperStart = 0.95f;

        /// <summary>
        /// Voice level at <paramref name="distance"/>, 0..1, as a real talker in a
        /// real room: the direct field summed in energy with the reverberant field,
        /// normalised so anything inside <paramref name="minDistance"/> is unity.
        /// </summary>
        /// <param name="distance">Listener-to-mouth distance in metres.</param>
        /// <param name="minDistance">Radius inside which level stops rising.</param>
        /// <param name="reverberantDistance">Radius past which level stops falling.</param>
        /// <param name="maxDistance">Hearing range; the curve reaches zero here.</param>
        public static float DistanceGain(float distance, float minDistance,
                                         float reverberantDistance, float maxDistance)
        {
            minDistance = Mathf.Max(0.01f, minDistance);
            reverberantDistance = Mathf.Max(minDistance, reverberantDistance);
            maxDistance = Mathf.Max(minDistance, maxDistance);

            float reverberant = minDistance / reverberantDistance;
            float direct = minDistance / Mathf.Max(distance, minDistance);
            float gain = Mathf.Sqrt(direct * direct + reverberant * reverberant)
                       / Mathf.Sqrt(1f + reverberant * reverberant);

            float x = Mathf.Min(distance, maxDistance) / maxDistance;
            if (x > DistanceTaperStart)
            {
                float t = (x - DistanceTaperStart) / (1f - DistanceTaperStart);
                float fade = 1f - t;
                gain *= fade * fade;
            }
            return gain;
        }

        /// <summary>
        /// Reverberant share of the total energy at <paramref name="distance"/>,
        /// i.e. the correct <c>AudioSource.reverbZoneMix</c>. Rises from near zero
        /// at the talker's mouth to ~1 across the room, crossing 0 dB direct-to-
        /// reverberant exactly at <paramref name="reverberantDistance"/>.
        /// The shipping value was a flat 1.0, i.e. maximum room at zero distance.
        /// </summary>
        public static float ReverbSend(float distance, float minDistance, float reverberantDistance)
        {
            minDistance = Mathf.Max(0.01f, minDistance);
            reverberantDistance = Mathf.Max(minDistance, reverberantDistance);

            float reverberant = minDistance / reverberantDistance;
            float direct = minDistance / Mathf.Max(distance, minDistance);
            return Mathf.Clamp01(reverberant / Mathf.Sqrt(direct * direct + reverberant * reverberant));
        }

        /// <summary>Keys spent on the power-law stretch between the two kinks.</summary>
        private const int CurveLogKeys = 22;
        /// <summary>Keys spent on the taper.</summary>
        private const int CurveTaperKeys = 8;

        /// <summary>
        /// Where to place keys along the normalised distance axis. Not a uniform or
        /// quadratic grid: the curve is a power law between minDistance and the taper
        /// start, so keys go geometric in distance there (equal ratios, hence equal
        /// curvature per key), with explicit keys on both kinks. A fixed grid leaves
        /// the minDistance corner unsampled once the hearing range is large — at a
        /// 100 m range a quadratic grid put no key between 0.42 m and 0.94 m, right
        /// across the bend, and the baked curve drifted 0.72 dB there.
        /// </summary>
        private static int SampleAxis(float[] xs, float minDistance, float maxDistance)
        {
            float kink = Mathf.Min(minDistance / maxDistance, DistanceTaperStart * 0.5f);
            int n = 0;
            xs[n++] = 0f;
            xs[n++] = kink;
            for (int i = 1; i <= CurveLogKeys; i++)
            {
                float t = i / (float)CurveLogKeys;
                xs[n++] = kink * Mathf.Pow(DistanceTaperStart / kink, t);
            }
            for (int i = 1; i <= CurveTaperKeys; i++)
            {
                xs[n++] = DistanceTaperStart + (1f - DistanceTaperStart) * (i / (float)CurveTaperKeys);
            }

            // Drop coincident samples; a zero-width interval would divide by zero in
            // the tangent clamp below.
            int write = 1;
            for (int i = 1; i < n; i++)
            {
                if (xs[i] > xs[write - 1] + 1e-7f)
                {
                    xs[write++] = Mathf.Min(xs[i], 1f);
                }
            }
            return write;
        }

        /// <summary>
        /// Bakes a monotone function of normalised distance into an AnimationCurve.
        ///
        /// Tangents come from central differences, then get the Fritsch-Carlson
        /// monotonicity clamp. Without the clamp the cubic overshoots around the
        /// minDistance kink and the baked curve is <em>not</em> monotonic — meaning
        /// backing away from someone could briefly make them louder, which nothing
        /// in the analytic model does.
        /// </summary>
        private static AnimationCurve Bake(System.Func<float, float> gainAtDistance,
                                           float minDistance, float maxDistance,
                                           bool endsAtSilence)
        {
            var xs = new float[CurveLogKeys + CurveTaperKeys + 4];
            int n = SampleAxis(xs, minDistance, maxDistance);

            var vs = new float[n];
            var ms = new float[n];
            const float h = 1e-4f;
            for (int i = 0; i < n; i++)
            {
                vs[i] = gainAtDistance(xs[i] * maxDistance);
                float lo = Mathf.Max(0f, xs[i] - h);
                float hi = Mathf.Min(1f, xs[i] + h);
                ms[i] = (gainAtDistance(hi * maxDistance) - gainAtDistance(lo * maxDistance)) / (hi - lo);
            }
            if (endsAtSilence)
            {
                vs[n - 1] = 0f;
                ms[n - 1] = Mathf.Min(0f, ms[n - 1]);
            }

            for (int i = 0; i < n; i++)
            {
                float before = i > 0 ? (vs[i] - vs[i - 1]) / (xs[i] - xs[i - 1]) : 0f;
                float after = i < n - 1 ? (vs[i + 1] - vs[i]) / (xs[i + 1] - xs[i]) : 0f;
                float lo = i > 0 ? before : after;
                float hi = i < n - 1 ? after : before;

                if (lo == 0f || hi == 0f || (lo > 0f) != (hi > 0f))
                {
                    ms[i] = 0f;   // flat spot or turning point: a flat tangent cannot overshoot
                }
                else
                {
                    float limit = 3f * Mathf.Min(Mathf.Abs(lo), Mathf.Abs(hi));
                    ms[i] = Mathf.Clamp(ms[i], -limit, limit);
                    if ((ms[i] > 0f) != (lo > 0f)) ms[i] = 0f;
                }
            }

            var keys = new Keyframe[n];
            for (int i = 0; i < n; i++)
            {
                keys[i] = new Keyframe(xs[i], vs[i], ms[i], ms[i]);
            }
            return new AnimationCurve(keys);
        }

        /// <summary>
        /// Bakes <see cref="DistanceGain"/> into the <c>AnimationCurve</c> Unity wants
        /// for <see cref="AudioRolloffMode.Custom"/>. Unity evaluates that curve at
        /// <c>distance / maxDistance</c> and ignores <c>minDistance</c> entirely, which
        /// is why minDistance has to be baked in here to mean anything at all.
        /// </summary>
        public static AnimationCurve BuildRolloffCurve(float minDistance,
                                                       float reverberantDistance,
                                                       float maxDistance)
        {
            maxDistance = Mathf.Max(0.01f, maxDistance);
            float clampedMax = maxDistance;
            return Bake(d => DistanceGain(d, minDistance, reverberantDistance, clampedMax),
                        minDistance, maxDistance, endsAtSilence: true);
        }

        /// <summary>
        /// Same treatment for <see cref="AudioSourceCurveType.ReverbZoneMix"/>.
        /// </summary>
        public static AnimationCurve BuildReverbMixCurve(float minDistance,
                                                         float reverberantDistance,
                                                         float maxDistance)
        {
            maxDistance = Mathf.Max(0.01f, maxDistance);
            return Bake(d => ReverbSend(d, minDistance, reverberantDistance),
                        minDistance, maxDistance, endsAtSilence: false);
        }

        // ─────────────────────── talker directivity ───────────────────────

        /// <summary>
        /// Corner of the mouth-directivity shelf. Speech radiates near
        /// omnidirectionally below this and progressively more forward above it.
        /// </summary>
        public const float DirectivityCornerHz = 500f;

        /// <summary>Shelf depth with the talker facing directly away.</summary>
        public const float DirectivityShelfMaxDb = 15.4f;

        /// <summary>
        /// Exponent on <c>(1 - cos θ)/2</c>. Fitted, not chosen: it is what makes
        /// the shelf track the measured 4 kHz-vs-250 Hz tilt across all angles.
        /// </summary>
        public const float DirectivityShapePower = 1.35f;

        /// <summary>
        /// Steam Audio dipole weight that pairs with the shelf. Lower than the old
        /// broadband-only 0.25 because the shelf now carries part of the loss; the
        /// two together land on the measured broadband directivity, which 0.25
        /// alone overshot by up to 2 dB behind the talker.
        /// </summary>
        public const float DipoleWeight = 0.10f;

        /// <summary>
        /// High-shelf depth, in dB, for a talker whose mouth axis is
        /// <paramref name="cosOffAxis"/> away from the listener direction.
        /// Zero facing you, <see cref="DirectivityShelfMaxDb"/> facing away.
        /// </summary>
        /// <param name="cosOffAxis">cos of the angle between mouth forward and the
        /// direction from mouth to listener. +1 = talking straight at you.</param>
        public static float DirectivityShelfDb(float cosOffAxis)
        {
            float u = Mathf.Clamp01((1f - Mathf.Clamp(cosOffAxis, -1f, 1f)) * 0.5f);
            return -DirectivityShelfMaxDb * Mathf.Pow(u, DirectivityShapePower);
        }

        // ──────────────────────── listener cone ────────────────────────

        /// <summary>
        /// Corner of the listener-cone shelf. A head only shadows sound above
        /// ka ≈ 1, which for a 9 cm head is about here — well above the mouth
        /// directivity corner, so the two get separate shelves.
        /// </summary>
        public const float HeadShadowCornerHz = 1500f;

        /// <summary>
        /// Deepest shelf the cone may apply. Past roughly this a source stops
        /// sounding like a person behind you and starts sounding like a low-pass
        /// filter, so whatever the cone still owes gets paid broadband instead.
        /// </summary>
        public const float ConeMaxShelfDb = -12f;

        /// <summary>
        /// Share of the requested cone attenuation aimed at the shelf. 0.6 is the
        /// deepest split whose leftover broadband term stays under ~1.2 dB per 10°
        /// of head rotation — above that the cone is audible as a fader tracking
        /// your head instead of as a change of scene.
        /// </summary>
        public const float ConeHighFrequencyShare = 0.60f;

        /// <summary>
        /// Speech-weighted broadband loss a <see cref="ConeMaxShelfDb"/> shelf at
        /// <see cref="HeadShadowCornerHz"/> actually delivers. The shelf sits above
        /// most of the speech energy, so it can only ever move this much broadband;
        /// subtracting it keeps the user's dampening slider meaning exactly what it
        /// says. Linear interpolation over the shelf range is within 0.12 dB of the
        /// exact octave-band integral.
        /// </summary>
        public const float ConeShelfBroadbandDb = -0.42f;

        /// <summary>
        /// Splits the cone-of-influence attenuation into a broadband multiplier and
        /// a high-shelf depth. The two together attenuate by exactly what
        /// <paramref name="dampenPercent"/> asks for; only the spectrum changes.
        /// </summary>
        /// <param name="offAxisRadians">Angle between listener forward and the
        /// direction to the talker.</param>
        /// <param name="coneAngleDegrees">Full cone angle; 360 disables the cone.</param>
        /// <param name="dampenPercent">Attenuation at 180°, 1..95.</param>
        /// <param name="broadband">Broadband multiplier, 0..1.</param>
        /// <param name="shelfDb">High-shelf depth in dB, &lt;= 0.</param>
        public static void ListenerConeTerms(float offAxisRadians, float coneAngleDegrees,
                                             float dampenPercent,
                                             out float broadband, out float shelfDb)
        {
            broadband = 1f;
            shelfDb = 0f;
            if (coneAngleDegrees >= 360f) return;

            float half = coneAngleDegrees * 0.5f * Mathf.Deg2Rad;
            if (offAxisRadians <= half) return;

            float minVolume = 1f - Mathf.Clamp(dampenPercent, 1f, 95f) / 100f;
            float totalDb = 20f * Mathf.Log10(Mathf.Max(1e-4f, minVolume));
            float t = Mathf.Clamp01((offAxisRadians - half) / (Mathf.PI - half));
            float wantDb = totalDb * (t * t * (3f - 2f * t));

            shelfDb = Mathf.Clamp(wantDb * ConeHighFrequencyShare, ConeMaxShelfDb, 0f);
            float deliveredDb = ConeShelfBroadbandDb * (shelfDb / ConeMaxShelfDb);
            broadband = Mathf.Pow(10f, Mathf.Min(0f, wantDb - deliveredDb) / 20f);
        }

        // ──────────────────────────── shelf ────────────────────────────

        /// <summary>
        /// One-pole coefficient for a shelf cornering at <paramref name="cornerHz"/>.
        /// </summary>
        public static float ShelfCoefficient(float cornerHz, int sampleRate)
        {
            if (sampleRate <= 0) return 1f;
            return 1f - Mathf.Exp(-2f * Mathf.PI * cornerHz / sampleRate);
        }

        /// <summary>Speech-weighted broadband loss of the directivity shelf at full depth.</summary>
        private const float DirectivityShelfBroadbandDb = -1.86f;

        /// <summary>
        /// Speech-weighted broadband equivalent of the two shelves, as a linear
        /// multiplier. <b>For meters and debug readouts only</b> — the shelves are
        /// filters, not gains, and this collapses their whole response to one number
        /// so a level display can show what the listener actually loses. Linear in
        /// shelf dB, which is within 0.62 dB of the exact octave-band integral over
        /// the directivity shelf's range and 0.12 dB over the cone's.
        /// </summary>
        public static float ShelfBroadbandGain(float directivityShelfDb, float coneShelfDb)
        {
            float db = DirectivityShelfBroadbandDb
                       * Mathf.Clamp01(directivityShelfDb / -DirectivityShelfMaxDb)
                     + ConeShelfBroadbandDb
                       * Mathf.Clamp01(coneShelfDb / ConeMaxShelfDb);
            return Mathf.Pow(10f, db / 20f);
        }
    }
}
