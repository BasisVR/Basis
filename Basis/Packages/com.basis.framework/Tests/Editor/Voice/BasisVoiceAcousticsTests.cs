using Basis.Scripts.Networking;
using Basis.Scripts.Networking.Receivers;
using NUnit.Framework;
using System;
using System.Numerics;
using UnityEngine;

namespace Basis.Tests.Voice
{
    /// <summary>
    /// Locks down the remote-voice acoustic model against the numbers it was fitted
    /// to. Expected values come from the Python model that fitted them, so a drift
    /// here means the C# and the fit have diverged.
    ///
    /// The reference data behind the fit:
    ///  • talker directivity — Chu &amp; Warnock, NRC RR-104
    ///  • distance — direct + diffuse field, i.e. a real talker in a real room
    ///  • listener cone — head/torso diffraction
    /// </summary>
    public class BasisVoiceAcousticsTests
    {
        private const float MinDistance = 0.5f;
        private const float MaxDistance = 25f;
        private const int SampleRate = 48000;

        private static float Db(float linear) => 20f * Mathf.Log10(Mathf.Max(linear, 1e-9f));

        private static float Gain(float d) =>
            BasisVoiceAcoustics.DistanceGain(d, MinDistance, MaxDistance);

        // ─────────────────────────── distance ───────────────────────────

        [TestCase(0.25f, 1.000000f)]
        [TestCase(0.5f, 1.000000f)]
        [TestCase(1f, 0.500000f)]
        [TestCase(2f, 0.250000f)]
        [TestCase(4f, 0.125000f)]
        [TestCase(8f, 0.062500f)]
        [TestCase(16f, 0.031250f)]
        [TestCase(24f, 0.013333f)]
        public void DistanceGain_MatchesTheFittedModel(float distance, float expected)
        {
            Assert.AreEqual(expected, Gain(distance), 1e-4f);
        }

        [Test]
        public void DistanceGain_FollowsInverseDistanceLawInTheNearField()
        {
            // -6 dB per doubling is the whole point: it is the level cue that tells
            // you how far away someone is, and the curve this replaced supplied
            // -0.78 dB between 1 m and 2 m.
            float oneToTwo = Db(Gain(2f)) - Db(Gain(1f));
            Assert.AreEqual(-6f, oneToTwo, 0.75f,
                "near-field slope should track the inverse distance law");

            float halfToOne = Db(Gain(1f)) - Db(Gain(0.5f));
            Assert.AreEqual(-6f, halfToOne, 0.5f);
        }

        [Test]
        public void DistanceGain_KeepsFallingAtEveryDistance()
        {
            // The reverberant floor that used to flatten the curve past the critical
            // distance was removed by request, so the inverse distance law now runs the
            // whole way out and far players keep getting quieter.
            float far = Db(Gain(16f)) - Db(Gain(8f));
            Assert.AreEqual(-6f, far, 0.5f, "level should keep falling past the old reverb distance");
        }

        [Test]
        public void DistanceGain_IsInaudibleByTheCullRadius()
        {
            // The source is hard-stopped at maxDistance and only released at 1.1x
            // that, so the curve has to have already reached silence or the stop
            // clicks. Anything under about -55 dB is inaudible under a live mix.
            Assert.Less(Db(Gain(MaxDistance * 0.995f)), -55f);
            Assert.AreEqual(0f, Gain(MaxDistance), 1e-6f);
            Assert.AreEqual(0f, Gain(MaxDistance * 2f), 1e-6f);
        }

        [Test]
        public void DistanceGain_IsMonotonicallyDecreasing()
        {
            float previous = Gain(0f);
            for (float d = 0f; d <= MaxDistance; d += 0.05f)
            {
                float g = Gain(d);
                Assert.LessOrEqual(g, previous + 1e-5f, $"gain rose at {d} m");
                previous = g;
            }
        }

        [Test]
        public void DistanceGain_ScalesWithTheHearingRange()
        {
            // A user shrinking their hearing range must not also move the metre at
            // which the inverse law starts — the shape is anchored in metres, and
            // only the taper is expressed as a fraction of the range.
            foreach (float range in new[] { 10f, 25f, 60f })
            {
                float g = BasisVoiceAcoustics.DistanceGain(2f, MinDistance, range);
                Assert.AreEqual(0.25f, g, 0.02f, $"2 m gain drifted at {range} m range");
            }
        }

        // ──────────────────────────── shout ────────────────────────────

        // The shipped RAMinDistance default, so these pin what players actually hear
        // rather than only the general shape.
        private const float ShipMin = 1.5f;

        private static float NormalLevel(float d) =>
            BasisVoiceAcoustics.DistanceGain(d, ShipMin, MaxDistance);

        private static float Boost(float d) =>
            BasisVoiceAcoustics.ShoutBoost(d * d, ShipMin, BasisShout.Gain, NormalLevel(d));

        private static float ShoutLevel(float d) => NormalLevel(d) * Boost(d);

        [Test]
        public void ShoutBoost_IsNothingPointBlank()
        {
            // The complaint shout answers is "I cannot hear them from over there", not
            // "they are not loud enough standing on top of me". Someone inside the
            // minimum distance is already at full level and must not be pushed past it.
            Assert.AreEqual(1f, Boost(0f), 1e-5f);
            Assert.AreEqual(1f, Boost(ShipMin), 1e-5f);
            Assert.AreEqual(1f, Boost(ShipMin * 0.5f), 1e-5f);
        }

        [Test]
        public void ShoutBoost_NeverExceedsAPointBlankTalker()
        {
            // The whole safety property: the boost only ever gives back level the
            // rolloff took away, so a shouter at any distance lands at or under what a
            // normal talker standing at the minimum distance already delivers. Nothing
            // shout does can be louder than something the mix already produces.
            float pointBlank = NormalLevel(ShipMin);
            for (float d = 0f; d <= MaxDistance; d += 0.05f)
            {
                Assert.LessOrEqual(ShoutLevel(d), pointBlank + 1e-5f,
                    $"a shouter at {d:F2} m arrived louder than a normal talker at {ShipMin} m");
            }
        }

        [Test]
        public void ShoutBoost_IsWorthRealLevelAtTheDistancesPeopleComplainedAbout()
        {
            // 1.75x applied before the limiter measured under a dB on loud syllables.
            // These are the numbers that have to survive: a clear step at conversational
            // range, the full boost by the time you are across a room.
            Assert.Greater(Db(ShoutLevel(4f)) - Db(NormalLevel(4f)), 6f);
            Assert.Greater(Db(ShoutLevel(8f)) - Db(NormalLevel(8f)), 9f);
            Assert.Greater(Db(ShoutLevel(16f)) - Db(NormalLevel(16f)), 9f);
        }

        [Test]
        public void ShoutBoost_StopsAtTheCap()
        {
            Assert.AreEqual(BasisShout.Gain, Boost(MaxDistance), 1e-4f);
            Assert.LessOrEqual(Boost(MaxDistance * 4f), BasisShout.Gain + 1e-5f);
        }

        [Test]
        public void ShoutBoost_LeavesTheDistanceCueIntact()
        {
            // Restoring all of the loss would hold a shouter at one level everywhere
            // inside the cap, which reads as a voice with no position. The level must
            // still fall as they back away, and the boost must never fall.
            float previousLevel = ShoutLevel(ShipMin);
            float previousBoost = Boost(0f);
            for (float d = ShipMin; d <= MaxDistance * 0.9f; d += 0.05f)
            {
                float level = ShoutLevel(d);
                float boost = Boost(d);
                Assert.LessOrEqual(level, previousLevel + 1e-5f, $"a shouter got louder backing away at {d:F2} m");
                Assert.GreaterOrEqual(boost, previousBoost - 1e-5f, $"boost fell at {d:F2} m");
                previousLevel = level;
                previousBoost = boost;
            }
            Assert.Less(Db(ShoutLevel(16f)) - Db(ShoutLevel(4f)), -3f,
                "a shouter should still audibly recede");
        }

        [Test]
        public void RolloffCurve_TracksTheAnalyticModel()
        {
            // Unity interpolates the baked keys with cubic Hermite; the keys only
            // approximate the closed form. Bound that approximation error, because
            // this curve is what actually plays.
            AnimationCurve curve = BasisVoiceAcoustics.BuildRolloffCurve(
                MinDistance, MaxDistance);

            float worstDb = 0f;
            for (float d = 0.25f; d < MaxDistance * 0.94f; d += 0.05f)
            {
                float baked = curve.Evaluate(d / MaxDistance);
                float analytic = Gain(d);
                worstDb = Mathf.Max(worstDb, Mathf.Abs(Db(baked) - Db(analytic)));
            }
            Assert.Less(worstDb, 0.5f, $"baked curve drifts {worstDb:F2} dB from the model");
        }

        [Test]
        public void RolloffCurve_EndsAtSilence()
        {
            AnimationCurve curve = BasisVoiceAcoustics.BuildRolloffCurve(
                MinDistance, MaxDistance);
            Assert.AreEqual(0f, curve.Evaluate(1f), 1e-5f);
            Assert.AreEqual(1f, curve.Evaluate(0f), 1e-3f);
        }

        [Test]
        public void RolloffCurve_IsMonotonicAtEveryRangeCombination()
        {
            // Cubic Hermite through sampled keys overshoots around the minDistance
            // kink unless the tangents are clamped, and an overshoot here means
            // backing away from someone briefly makes them LOUDER. Nothing in the
            // analytic model does that, so the baked curve must not either.
            foreach (float minD in new[] { 0.1f, 0.5f, 2f })
            {
                foreach (float maxD in new[] { 5f, 10f, 25f, 60f, 100f })
                {
                    AnimationCurve curve = BasisVoiceAcoustics.BuildRolloffCurve(
                        minD, maxD);
                    float previous = curve.Evaluate(0f);
                    for (float x = 0f; x <= 1f; x += 0.0005f)
                    {
                        float v = curve.Evaluate(x);
                        Assert.LessOrEqual(v, previous + 1e-6f,
                            $"curve rose at x={x:F4} (minD {minD}, maxD {maxD})");
                        previous = v;
                    }
                }
            }
        }

        [Test]
        public void RolloffCurve_StaysAccurateAcrossRangeCombinations()
        {
            foreach (float minD in new[] { 0.1f, 0.5f, 2f })
            {
                foreach (float maxD in new[] { 5f, 10f, 25f, 60f, 100f })
                {
                    AnimationCurve curve = BasisVoiceAcoustics.BuildRolloffCurve(
                        minD, maxD);
                    float worst = 0f;
                    for (float d = minD * 0.5f; d < maxD * 0.94f; d += maxD / 500f)
                    {
                        float analytic = BasisVoiceAcoustics.DistanceGain(d, minD, maxD);
                        worst = Mathf.Max(worst, Mathf.Abs(Db(curve.Evaluate(d / maxD)) - Db(analytic)));
                    }
                    Assert.Less(worst, 0.6f, $"minD {minD}, maxD {maxD} drifts {worst:F2} dB");
                }
            }
        }

        // ─────────────────────── talker directivity ───────────────────────

        [TestCase(0f, 0.0000f)]
        [TestCase(30f, -0.4005f)]
        [TestCase(45f, -1.1513f)]
        [TestCase(60f, -2.3700f)]
        [TestCase(90f, -6.0413f)]
        [TestCase(120f, -10.4437f)]
        [TestCase(135f, -12.4360f)]
        [TestCase(150f, -14.0239f)]
        [TestCase(180f, -15.4000f)]
        public void DirectivityShelf_MatchesTheFittedModel(float degrees, float expectedDb)
        {
            float cos = Mathf.Cos(degrees * Mathf.Deg2Rad);
            Assert.AreEqual(expectedDb, BasisVoiceAcoustics.DirectivityShelfDb(cos), 0.02f);
        }

        [Test]
        public void DirectivityShelf_IsFlatOnAxisAndDeepestBehind()
        {
            Assert.AreEqual(0f, BasisVoiceAcoustics.DirectivityShelfDb(1f), 1e-4f);
            Assert.AreEqual(-BasisVoiceAcoustics.DirectivityShelfMaxDb,
                            BasisVoiceAcoustics.DirectivityShelfDb(-1f), 1e-3f);
            // Out-of-range cosines must clamp, not produce NaN from Pow.
            Assert.AreEqual(0f, BasisVoiceAcoustics.DirectivityShelfDb(5f), 1e-4f);
            Assert.IsFalse(float.IsNaN(BasisVoiceAcoustics.DirectivityShelfDb(-5f)));
        }

        [Test]
        public void DirectivityShelf_NeverBrightensAndOnlyDeepens()
        {
            float previous = 0f;
            for (int deg = 0; deg <= 180; deg++)
            {
                float shelf = BasisVoiceAcoustics.DirectivityShelfDb(Mathf.Cos(deg * Mathf.Deg2Rad));
                Assert.LessOrEqual(shelf, 1e-4f, "directivity must never boost");
                Assert.LessOrEqual(shelf, previous + 1e-4f);
                previous = shelf;
            }
        }

        // ──────────────────────── listener cone ────────────────────────

        [Test]
        public void ListenerCone_IsTransparentInsideTheCone()
        {
            for (int deg = 0; deg <= 74; deg += 2)
            {
                BasisVoiceAcoustics.ListenerConeTerms(deg * Mathf.Deg2Rad, 150f, 60f,
                                                      out float broadband, out float shelf);
                Assert.AreEqual(1f, broadband, 1e-5f);
                Assert.AreEqual(0f, shelf, 1e-5f);
            }
        }

        [Test]
        public void ListenerCone_DisabledAtThreeSixty()
        {
            BasisVoiceAcoustics.ListenerConeTerms(Mathf.PI, 360f, 60f,
                                                  out float broadband, out float shelf);
            Assert.AreEqual(1f, broadband, 1e-6f);
            Assert.AreEqual(0f, shelf, 1e-6f);
        }

        [Test]
        public void ListenerCone_TotalAttenuationStillMatchesTheSlider()
        {
            // The point of splitting the cone into a broadband term and a shelf is
            // that it changes the SPECTRUM, not the loudness. Whatever percentage
            // the user dialled in must still be what arrives, speech-weighted.
            foreach (float dampen in new[] { 25f, 40f, 60f, 75f, 90f })
            {
                BasisVoiceAcoustics.ListenerConeTerms(Mathf.PI, 150f, dampen,
                                                      out float broadband, out float shelfDb);
                float delivered = Db(broadband)
                    + SpeechWeightedDb(BasisVoiceAcoustics.HeadShadowCornerHz, shelfDb);
                float requested = Db(1f - dampen / 100f);
                Assert.AreEqual(requested, delivered, 0.25f,
                    $"cone at {dampen}% delivers {delivered:F2} dB, slider says {requested:F2} dB");
            }
        }

        [Test]
        public void ListenerCone_ShelfIsCappedSoItStaysAVoiceNotALowpass()
        {
            for (float dampen = 1f; dampen <= 95f; dampen += 1f)
            {
                BasisVoiceAcoustics.ListenerConeTerms(Mathf.PI, 150f, dampen,
                                                      out _, out float shelfDb);
                Assert.GreaterOrEqual(shelfDb, BasisVoiceAcoustics.ConeMaxShelfDb - 1e-4f);
                Assert.LessOrEqual(shelfDb, 0f);
            }
        }

        [Test]
        public void ListenerCone_BroadbandGradientStaysBelowTheFaderThreshold()
        {
            // The artefact being fixed: at the shipping 75 % the broadband term moved
            // 1.91 dB per 10 deg of head rotation, which reads as a fader tracking
            // your head. Under ~1.5 dB/10 deg it reads as a change of scene instead.
            float worst = 0f;
            const float stepDeg = 0.25f;
            float previous = 0f;
            for (float deg = 0f; deg <= 180f; deg += stepDeg)
            {
                BasisVoiceAcoustics.ListenerConeTerms(deg * Mathf.Deg2Rad, 150f, 60f,
                                                      out float broadband, out _);
                float current = Db(broadband);
                if (deg > 0f)
                {
                    worst = Mathf.Max(worst, Mathf.Abs(current - previous) / stepDeg * 10f);
                }
                previous = current;
            }
            Assert.Less(worst, 1.5f, $"cone broadband gradient is {worst:F2} dB/10deg");
        }

        [Test]
        public void ListenerCone_ShipsShallowerThanTheOldFlatFader()
        {
            // Non-vacuous guard: reproduce the old behaviour and show the new one is
            // measurably gentler in the term that was audible as a fader.
            BasisVoiceAcoustics.ListenerConeTerms(Mathf.PI, 150f, 60f, out float broadband, out _);
            float oldBroadbandDb = Db(1f - 75f / 100f);   // shipping: all of it, broadband
            Assert.Greater(Db(broadband), oldBroadbandDb + 3f);
        }

        // ───────────────── shelf: analytic vs the actual filter ─────────────────

        /// <summary>Magnitude of one shelf at <paramref name="hz"/>, from the closed
        /// form <c>H(z) = g + (1-g)·LP(z)</c> the filter implements.</summary>
        private static float ShelfResponseDb(float cornerHz, float shelfDb, float hz)
        {
            float a = BasisVoiceAcoustics.ShelfCoefficient(cornerHz, SampleRate);
            float g = Mathf.Pow(10f, shelfDb / 20f);
            double w = 2.0 * Math.PI * hz / SampleRate;
            Complex z = Complex.Exp(new Complex(0, -w));
            Complex lp = a / (Complex.One - (1.0 - a) * z);
            Complex h = g + (1.0 - g) * lp;
            return 20f * Mathf.Log10((float)h.Magnitude);
        }

        /// <summary>Speech-weighted broadband loss of a shelf, over the octave bands
        /// and long-term-average-speech weights the model was fitted with.</summary>
        private static float SpeechWeightedDb(float cornerHz, float shelfDb)
        {
            float[] bands = { 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f };
            float[] weights = { 0.2262f, 0.5687f, 0.4517f, 0.1429f, 0.0452f, 0.0143f, 0.0029f };
            float total = 0f, sum = 0f;
            for (int i = 0; i < bands.Length; i++)
            {
                total += weights[i];
                sum += weights[i] * Mathf.Pow(10f, ShelfResponseDb(cornerHz, shelfDb, bands[i]) / 10f);
            }
            return 10f * Mathf.Log10(sum / total);
        }

        [TestCase(100f, -0.1637f)]
        [TestCase(250f, -0.9279f)]
        [TestCase(500f, -2.8478f)]
        [TestCase(1000f, -6.3734f)]
        [TestCase(2000f, -10.2366f)]
        [TestCase(4000f, -12.7639f)]
        [TestCase(8000f, -13.7801f)]
        public void ShelfClosedForm_MatchesTheFittedResponse(float hz, float expectedDb)
        {
            Assert.AreEqual(expectedDb,
                ShelfResponseDb(BasisVoiceAcoustics.DirectivityCornerHz, -15.4f, hz), 0.05f);
        }

        [Test]
        public void ToneShaper_ReproducesTheClosedFormResponse()
        {
            // The whole model assumes the audio-thread filter really has the transfer
            // function everything was fitted against. Measure it with sine probes.
            const float shelfDb = -12f;
            foreach (float hz in new[] { 120f, 500f, 1500f, 4000f })
            {
                float measured = MeasureShelfDb(hz, shelfDb, 0f);
                float expected = ShelfResponseDb(BasisVoiceAcoustics.DirectivityCornerHz, shelfDb, hz);
                Assert.AreEqual(expected, measured, 0.35f, $"directivity shelf at {hz} Hz");
            }

            foreach (float hz in new[] { 120f, 1500f, 6000f })
            {
                float measured = MeasureShelfDb(hz, 0f, shelfDb);
                float expected = ShelfResponseDb(BasisVoiceAcoustics.HeadShadowCornerHz, shelfDb, hz);
                Assert.AreEqual(expected, measured, 0.35f, $"head shadow shelf at {hz} Hz");
            }
        }

        /// <summary>Drives a steady sine through the shaper and returns the settled
        /// gain in dB, as the ratio of output to input RMS over the final block.
        /// RMS rather than peak: at 6 kHz there are only eight samples per cycle, so
        /// the filter's phase shift moves the true peak off the sample grid and a
        /// peak reading under-reports by up to 0.7 dB.</summary>
        private static float MeasureShelfDb(float hz, float directivityDb, float headShadowDb)
        {
            var shaper = new BasisVoiceToneShaper();
            const int channels = 2;
            const int blocks = 40;
            const int frames = 1024;
            var buffer = new float[frames * channels];

            double inputSq = 0, outputSq = 0;
            int phase = 0;
            for (int b = 0; b < blocks; b++)
            {
                for (int f = 0; f < frames; f++)
                {
                    float s = Mathf.Sin(2f * Mathf.PI * hz * phase++ / SampleRate);
                    for (int c = 0; c < channels; c++) buffer[f * channels + c] = s;
                    if (b == blocks - 1) inputSq += (double)s * s;
                }
                shaper.Process(buffer, channels, frames, SampleRate, directivityDb, headShadowDb);

                if (b == blocks - 1)
                {
                    for (int f = 0; f < frames; f++)
                    {
                        double v = buffer[f * channels];
                        outputSq += v * v;
                    }
                }
            }
            return Db((float)Math.Sqrt(outputSq / Math.Max(inputSq, 1e-12)));
        }

        [Test]
        public void ToneShaper_IsIdentityWhenBothShelvesAreFlat()
        {
            var shaper = new BasisVoiceToneShaper();
            var buffer = new float[512];
            var expected = new float[512];
            for (int i = 0; i < buffer.Length; i++)
            {
                buffer[i] = expected[i] = Mathf.Sin(i * 0.07f);
            }
            shaper.Process(buffer, 1, buffer.Length, SampleRate, 0f, 0f);
            for (int i = 0; i < buffer.Length; i++)
            {
                Assert.AreEqual(expected[i], buffer[i], 1e-6f);
            }
        }

        [Test]
        public void ToneShaper_WritesEveryChannel()
        {
            var shaper = new BasisVoiceToneShaper();
            const int channels = 2;
            const int frames = 256;
            var buffer = new float[frames * channels];
            for (int f = 0; f < frames; f++)
            {
                float s = Mathf.Sin(f * 0.3f);
                buffer[f * channels] = s;
                buffer[f * channels + 1] = s;
            }
            shaper.Process(buffer, channels, frames, SampleRate, -10f, -6f);
            for (int f = 0; f < frames; f++)
            {
                Assert.AreEqual(buffer[f * channels], buffer[f * channels + 1], 1e-7f,
                    "both channels must carry the same shaped mono voice");
            }
        }

        [Test]
        public void ToneShaper_NeverBoosts()
        {
            var shaper = new BasisVoiceToneShaper();
            const int frames = 4096;
            var buffer = new float[frames];
            var rng = new System.Random(1234);
            float inputPeak = 0f;
            for (int i = 0; i < frames; i++)
            {
                buffer[i] = (float)(rng.NextDouble() * 2.0 - 1.0);
                inputPeak = Mathf.Max(inputPeak, Mathf.Abs(buffer[i]));
            }
            shaper.Process(buffer, 1, frames, SampleRate, -15.4f, -12f);
            for (int i = 0; i < frames; i++)
            {
                Assert.LessOrEqual(Mathf.Abs(buffer[i]), inputPeak + 1e-4f);
            }
        }

        [Test]
        public void ToneShaper_SurvivesNonFiniteShelfDepths()
        {
            // A NaN shelf depth would poison the one-pole state, and a filter whose
            // state is NaN never recovers — that voice would be dead for the session.
            var shaper = new BasisVoiceToneShaper();
            var buffer = new float[256];
            for (int i = 0; i < buffer.Length; i++) buffer[i] = Mathf.Sin(i * 0.2f);

            shaper.Process(buffer, 1, buffer.Length, SampleRate, float.NaN, float.NaN);
            foreach (float v in buffer) Assert.IsFalse(float.IsNaN(v), "NaN shelf leaked into the output");

            shaper.Process(buffer, 1, buffer.Length, SampleRate, float.PositiveInfinity, -12f);
            foreach (float v in buffer) Assert.IsFalse(float.IsNaN(v));

            // ...and the shaper still works afterwards.
            for (int i = 0; i < buffer.Length; i++) buffer[i] = Mathf.Sin(i * 0.2f);
            shaper.Process(buffer, 1, buffer.Length, SampleRate, -6f, 0f);
            foreach (float v in buffer)
            {
                Assert.IsFalse(float.IsNaN(v));
                Assert.LessOrEqual(Mathf.Abs(v), 1.01f);
            }
        }

        [Test]
        public void ToneShaper_HandlesDegenerateBuffers()
        {
            var shaper = new BasisVoiceToneShaper();
            Assert.DoesNotThrow(() => shaper.Process(null, 2, 64, SampleRate, -6f, -6f));
            Assert.DoesNotThrow(() => shaper.Process(new float[8], 0, 8, SampleRate, -6f, -6f));
            Assert.DoesNotThrow(() => shaper.Process(new float[8], 1, 0, SampleRate, -6f, -6f));
        }

        [Test]
        public void ToneShaper_ResetClearsTheDelayLine()
        {
            // A source that goes idle and comes back must not inherit the tail of the
            // previous utterance — that is an audible click on every resume.
            var shaper = new BasisVoiceToneShaper();
            var loud = new float[256];
            for (int i = 0; i < loud.Length; i++) loud[i] = 0.9f;
            shaper.Process(loud, 1, loud.Length, SampleRate, -15.4f, -12f);

            shaper.Reset();

            var silence = new float[256];
            shaper.Process(silence, 1, silence.Length, SampleRate, -15.4f, -12f);
            for (int i = 0; i < silence.Length; i++)
            {
                Assert.AreEqual(0f, silence[i], 1e-6f, "stale filter state leaked past Reset");
            }
        }

        [Test]
        public void ToneShaper_RampsRatherThanSteppingWhenTheAngleChanges()
        {
            // Both heads move every frame. A per-callback step in shelf gain is a
            // discontinuity in the middle of a waveform, i.e. zipper noise.
            var shaper = new BasisVoiceToneShaper();
            const int frames = 512;
            var block = new float[frames];
            for (int i = 0; i < frames; i++) block[i] = 1f;   // DC: isolates gain, not filtering

            shaper.Process(block, 1, frames, SampleRate, 0f, 0f);

            for (int i = 0; i < frames; i++) block[i] = 1f;
            shaper.Process(block, 1, frames, SampleRate, -15.4f, -12f);

            // DC passes both shelves at unity, so the ramp shows up as the transient
            // while the one-pole states charge — but never as a single-sample jump.
            for (int i = 1; i < frames; i++)
            {
                Assert.Less(Mathf.Abs(block[i] - block[i - 1]), 0.2f,
                    $"discontinuity of {block[i] - block[i - 1]:F3} at sample {i}");
            }
        }

        // ───────────────── regression guard: the old curve ─────────────────

        [Test]
        public void LegacyCurve_IsMeasurablyWorseThanTheModel()
        {
            // Keeps the other tests honest: if the shipping curve had already been
            // close to a real talker, every assertion above would be vacuous.
            var legacy = new AnimationCurve(
                new Keyframe(0.036f, 1f, -2.214f, -2.214f),
                new Keyframe(0.239f, 0.575f, -2.305f, -2.305f),
                new Keyframe(0.372f, 0.328f, -1.068f, -1.068f),
                new Keyframe(0.621f, 0.144f, -0.515f, -0.515f),
                new Keyframe(1f, 0f, -0.031f, -0.031f));

            double legacySq = 0, modelSq = 0;
            int n = 0;
            for (float d = 0.5f; d <= 15f; d += 0.25f)
            {
                // Reference: the analytic inverse distance law, which is what the model
                // IS — so this measures how far the hand-drawn curve sits from it.
                float reference = Db(Gain(d));
                double legacyErr = Db(legacy.Evaluate(d / MaxDistance)) - reference;
                double modelErr = Db(BuildAndEvaluate(d)) - reference;
                legacySq += legacyErr * legacyErr;
                modelSq += modelErr * modelErr;
                n++;
            }
            float legacyRms = Mathf.Sqrt((float)(legacySq / n));
            float modelRms = Mathf.Sqrt((float)(modelSq / n));

            Assert.Greater(legacyRms, 5f, "legacy curve should be far from a real talker");
            Assert.Less(modelRms, 0.5f, "baked model curve should sit on it");
        }

        private static float BuildAndEvaluate(float d)
        {
            AnimationCurve curve = BasisVoiceAcoustics.BuildRolloffCurve(
                MinDistance, MaxDistance);
            return curve.Evaluate(d / MaxDistance);
        }
    }
}
