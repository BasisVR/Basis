using System;
using Basis.IK.Motion;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.IK
{
    /// <summary>
    /// Guards the MEASURING INSTRUMENTS, not the IK.
    ///
    /// Every other file in this folder asks "is the solver right". This one asks "is the ruler
    /// straight", and it exists because during calibration THREE of these metrics silently lied, each
    /// in a way that would have shipped a gate that passed broken motion and failed good motion:
    ///
    ///   * Differentiating a raw joint path measured the mocap's optical noise, not the human. Jerk
    ///     amplifies frequency f by (2*pi*f)^3, so a sub-millimetre 30 Hz wobble dominated the third
    ///     derivative completely. A real elbow read 23100; through a 6 Hz low-pass it reads 1171.
    ///   * The zero-phase filter's startup transient ate the residual: the first 8% of a clip read
    ///     108 mm of "jitter" where the middle 84% read 1.5 mm.
    ///   * An unnormalised cross-correlation always peaks at zero lag, so it cheerfully reported 0 ms
    ///     of delay for a 150 ms filter.
    ///
    /// So the instruments get tested against signals whose answer is known in closed form, and a
    /// couple of them get tested against the WRONG implementation too, to prove the test can tell.
    /// </summary>
    public sealed class BasisMotionAnalyzerTests
    {
        const float k_Fs = 120f;
        const float k_Dt = 1f / 120f;

        // ---------------------------------------------------------------- filter

        [Test]
        public void LowPass_HasUnityGainAtDc()
        {
            // A Butterworth with the DC gain slightly off silently rescales every position it touches,
            // which turns into a phantom offset error nobody can find.
            var x = new float[256];
            for (int i = 0; i < x.Length; i++) x[i] = 3.7f;

            float[] y = BasisMotionSignal.LowPass(x, k_Dt, 6f);
            for (int i = 0; i < y.Length; i++)
                Assert.AreEqual(3.7f, y[i], 1e-4f, $"DC gain is not 1 at sample {i}");
        }

        [Test]
        public void LowPass_IsZeroPhase()
        {
            // The whole point of running the filter forwards AND backwards. If it introduced lag of its
            // own, every lag number this harness produces would be the analyser's, not the solver's.
            //
            // Measure the phase properly rather than comparing peak positions. The first version of this
            // test took the argmax of a 1.5 Hz sine over a 300-sample window -- which contains FOUR peaks,
            // all of height 1.0 to within a float ulp. argmax then picked whichever one happened to win in
            // the last bit: sample 100 for the input, 340 for the output. It reported a 2-second "phase
            // shift" from a filter that has none. Comparing argmax across a multi-period window is not a
            // phase measurement, it is a coin toss.
            var x = new float[1024];
            for (int i = 0; i < x.Length; i++) x[i] = Mathf.Sin(2f * Mathf.PI * 1.5f * (i + 1) * k_Dt);

            float[] y = BasisMotionSignal.LowPass(x, k_Dt, 6f);
            BasisFreqResponse r = BasisMotionResponse.Sine(x, y, k_Dt, 1.5f);

            Assert.AreEqual(0f, r.LagMs, 1f,
                $"the filter shifted the signal by {r.LagMs:F2} ms -- it is not zero-phase, and every lag " +
                "number this harness reports would be the analyser's own");
            Assert.AreEqual(1f, r.Gain, 0.05f,
                $"1.5 Hz is well inside a 6 Hz passband; amplitude should survive (gain {r.Gain:F3})");
        }

        [Test]
        public void LowPass_LeavesNoEdgeTransient_OnASignalWithAStrongTrend()
        {
            // THE TRAP, as a test. A joint in motion has a big end-to-end trend. Zero-pad the filter and
            // it gets slammed with a step at each boundary and rings for tens of samples -- and because
            // the ringing is at the EDGES, any RMS taken over the whole window is dominated by it.
            // Measured on the real corpus before the fix: edge 108 mm, middle 1.5 mm. Same clip.
            //
            // The signal here is a pure ramp plus a slow sine: it is entirely inside the 6 Hz passband,
            // so a correct filter reproduces it and the residual is ~0 EVERYWHERE, edges included.
            var x = new float[300];
            for (int i = 0; i < x.Length; i++)
                x[i] = 2.0f * i / x.Length + 0.3f * Mathf.Sin(2f * Mathf.PI * 1f * i * k_Dt);

            float[] y = BasisMotionSignal.LowPass(x, k_Dt, 6f);
            var res = new float[x.Length];
            for (int i = 0; i < x.Length; i++) res[i] = x[i] - y[i];

            int edge = x.Length * 8 / 100;
            float edgeRms = Mathf.Max(
                BasisMotionSignal.Rms(res, 0, edge),
                BasisMotionSignal.Rms(res, x.Length - edge, x.Length));
            float midRms = BasisMotionSignal.Rms(res, edge, x.Length - edge);

            Assert.Less(edgeRms, 0.01f, $"edge transient: {edgeRms:F5} -- the padding is not absorbing it");
            Assert.Less(edgeRms, 20f * Mathf.Max(midRms, 1e-6f),
                $"edge RMS {edgeRms:F5} dwarfs middle RMS {midRms:F5}; any metric taken over the whole " +
                "window is measuring the filter's startup, not the motion");
        }

        // ---------------------------------------------------------------- derivatives

        [Test]
        public void Derivative_OfAKnownQuadratic_IsExactInTheInterior()
        {
            // p(t) = 5t^2  ->  v(t) = 10t. A central difference is EXACT for a quadratic, so any error
            // here is an indexing or dt bug, not numerics.
            var p = new Vector3[200];
            for (int i = 0; i < p.Length; i++)
            {
                float t = i * k_Dt;
                p[i] = new Vector3(5f * t * t, 0f, 0f);
            }

            Vector3[] v = BasisMotionSignal.Derivative(p, k_Dt);
            for (int i = 1; i < p.Length - 1; i++)
                Assert.AreEqual(10f * i * k_Dt, v[i].x, 1e-3f, $"velocity wrong at {i}");
        }

        // ---------------------------------------------------------------- FFT

        [Test]
        public void Fft_MatchesANaiveDft()
        {
            const int n = 64;
            var re = new float[n];
            var im = new float[n];
            var rng = new System.Random(1234);
            var src = new float[n];
            for (int i = 0; i < n; i++) { src[i] = (float)(rng.NextDouble() * 2 - 1); re[i] = src[i]; }

            BasisMotionSpectrum.Fft(re, im);

            for (int k = 0; k < n; k++)
            {
                double dr = 0, di = 0;
                for (int t = 0; t < n; t++)
                {
                    double a = -2.0 * Math.PI * k * t / n;
                    dr += src[t] * Math.Cos(a);
                    di += src[t] * Math.Sin(a);
                }
                Assert.AreEqual(dr, re[k], 1e-3, $"FFT real part differs from DFT at bin {k}");
                Assert.AreEqual(di, im[k], 1e-3, $"FFT imag part differs from DFT at bin {k}");
            }
        }

        // ---------------------------------------------------------------- SPARC

        [Test]
        public void Sparc_OnAMinimumJerkReach_MatchesTheLiteratureValue()
        {
            // THE CALIBRATION STANDARD. A minimum-jerk reach is the maximally-smooth movement there is,
            // and Balasubramanian et al. (2015) put its spectral arc length at about -1.4. If this
            // number moves, SPARC is broken -- not the motion under test. Everything else in this
            // harness is calibrated relative to it.
            Vector3[] p = BasisMotionSignal.MinJerk(Vector3.zero, new Vector3(0.4f, 0.1f, 0f), 120);
            float[] speed = BasisMotionSignal.Magnitude(BasisMotionSignal.Derivative(p, k_Dt));

            float sparc = BasisMotionSpectrum.Sparc(speed, k_Dt);
            Assert.AreEqual(-1.4f, sparc, 0.15f,
                $"SPARC of a min-jerk reach is {sparc:F3}, literature says ~-1.4. The metric is wrong.");
        }

        [Test]
        public void Sparc_RanksAJitteredReachWorseThanACleanOne()
        {
            Vector3[] clean = BasisMotionSignal.MinJerk(Vector3.zero, new Vector3(0.4f, 0.1f, 0f), 120);
            var noisy = new Vector3[clean.Length];
            var rng = new System.Random(7);
            for (int i = 0; i < clean.Length; i++)
                noisy[i] = clean[i] + new Vector3(
                    (float)(rng.NextDouble() - 0.5) * 0.004f,
                    (float)(rng.NextDouble() - 0.5) * 0.004f,
                    (float)(rng.NextDouble() - 0.5) * 0.004f);

            float a = BasisMotionSpectrum.Sparc(BasisMotionSignal.Magnitude(BasisMotionSignal.Derivative(clean, k_Dt)), k_Dt);
            float b = BasisMotionSpectrum.Sparc(BasisMotionSignal.Magnitude(BasisMotionSignal.Derivative(noisy, k_Dt)), k_Dt);
            Assert.Less(b, a, $"SPARC did not rank the jittered reach ({b:F3}) as worse than the clean one ({a:F3})");
        }

        // ---------------------------------------------------------------- the two-sided lesson

        [Test]
        public void Jerk_IsAFloorNotACeiling_BecauseOverSmoothingCollapsesIt()
        {
            // The single most important fact about this harness, pinned as a test.
            //
            // Smoothness metrics are ONE-SIDED: they punish rough and REWARD mush. Over-smooth a real
            // human arm and it scores BETTER than the human on every smoothness metric there is. So a
            // gate written as `jerk <= ceiling` passes exactly the dead, laggy, robotic motion we are
            // trying to eliminate. Jerk must be gated as a FLOOR as well.
            //
            // If someone ever "fixes" the naturalness gates by making them one-sided, this fails.
            Vector3[] human = SyntheticReachSequence(600, seed: 3);
            Vector3[] mush = OneFilter(human, tauSeconds: 0.15f, dt: k_Dt);

            var h = BasisMotionQuality.Analyze(human, 0.6f, k_Dt, "human");
            var m = BasisMotionQuality.Analyze(mush, 0.6f, k_Dt, "over-smoothed");

            Assert.Less(m.JerkPerLimb, h.JerkPerLimb * 0.5f,
                $"over-smoothing should collapse jerk far below the source ({m.JerkPerLimb:F0} vs {h.JerkPerLimb:F0})");
            Assert.Greater(m.Sparc, h.Sparc,
                $"over-smoothed motion should score BETTER on SPARC ({m.Sparc:F2} vs {h.Sparc:F2}) -- that is " +
                "precisely why SPARC alone cannot be trusted as a naturalness gate");
        }

        [Test]
        public void ShapeDistance_CatchesOverSmoothing_WhichEverySmoothnessMetricRewards()
        {
            // The metric that closes the hole the test above opens. Over-smoothed motion has the WRONG
            // DISTRIBUTION of energy across frequency even though it is "smoother", and spectral shape
            // distance sees that. Jitter, which smoothness metrics DO catch, barely moves it -- the two
            // metrics are sensitive to opposite failures, which is exactly the point of carrying both.
            Vector3[] human = SyntheticReachSequence(600, seed: 4);
            Vector3[] mush = OneFilter(human, 0.15f, k_Dt);

            var rng = new System.Random(11);
            var buzzy = new Vector3[human.Length];
            for (int i = 0; i < human.Length; i++)
                buzzy[i] = human[i] + new Vector3(
                    (float)(rng.NextDouble() - 0.5) * 0.003f,
                    (float)(rng.NextDouble() - 0.5) * 0.003f,
                    (float)(rng.NextDouble() - 0.5) * 0.003f);

            var mushS = BasisMotionQuality.Analyze(mush, 0.6f, k_Dt, "mush", reference: human);
            var buzzS = BasisMotionQuality.Analyze(buzzy, 0.6f, k_Dt, "buzz", reference: human);

            Assert.Greater(mushS.ShapeDistance, 3f * buzzS.ShapeDistance,
                $"shape distance should react far more strongly to over-smoothing ({mushS.ShapeDistance:F3}) " +
                $"than to added jitter ({buzzS.ShapeDistance:F3}) -- it is the anti-mush metric");
            Assert.Greater(buzzS.JitterFracLimb, mushS.JitterFracLimb,
                "and the jitter metric must react the other way round, or the two are measuring the same thing");
        }

        [Test]
        public void PopStats_FindsASingleFrameTeleport_AndIgnoresSmoothMotion()
        {
            Vector3[] smooth = BasisMotionSignal.MinJerk(Vector3.zero, new Vector3(0.5f, 0f, 0f), 200);
            var s1 = BasisMotionQuality.Analyze(smooth, 0.6f, k_Dt, "smooth");
            Assert.AreEqual(0, s1.Pops, "smooth motion must not register a pop");

            var popped = (Vector3[])smooth.Clone();
            for (int i = 120; i < popped.Length; i++) popped[i] += new Vector3(0f, 0.08f, 0f);
            var s2 = BasisMotionQuality.Analyze(popped, 0.6f, k_Dt, "popped");
            Assert.GreaterOrEqual(s2.Pops, 1, "an 8 cm single-frame teleport must register as a pop");
            Assert.AreEqual(119, s2.WorstPopFrame, 2, "and it must report WHERE");
        }

        // ---------------------------------------------------------------- response

        [Test]
        public void Step_OfAKnownExponential_RecoversItsTimeConstant()
        {
            // tau = 1/rate by construction, so t63 must come back as 1/rate. This is what makes "blend
            // speed" a measurable quantity rather than a feeling.
            const float rate = 8f;
            var y = new float[120];
            float x = 0f;
            for (int i = 0; i < y.Length; i++)
            {
                x += BasisMotionResponse.ExpAlpha(rate, k_Dt) * (1f - x);
                y[i] = x;
            }

            BasisStepResponse r = BasisMotionResponse.Step(y, k_Dt);
            Assert.AreEqual(1000f / rate, r.T63Ms, 3f, $"t63 should be 1/rate = {1000f / rate:F1} ms, got {r.T63Ms:F1}");
            Assert.AreEqual(0f, r.OvershootPct, 0.01f, "a first-order blend cannot overshoot");
        }

        [Test]
        public void Sine_RecoversTheAnalyticLagOfAOnePoleFilter()
        {
            // Closed-form check. For y[n] = y[n-1] + a*(x[n] - y[n-1]), H(e^jw) = a / (1 - (1-a)e^-jw),
            // so the phase -- and hence the lag -- is exactly computable. If Sine() disagrees with the
            // algebra, Sine() is wrong.
            const float rate = 10f, hz = 1.5f;
            float a = BasisMotionResponse.ExpAlpha(rate, k_Dt);

            var inp = new float[1200];
            var outp = new float[1200];
            float x = 0f;
            for (int i = 0; i < inp.Length; i++)
            {
                inp[i] = Mathf.Sin(2f * Mathf.PI * hz * (i + 1) * k_Dt);
                x += a * (inp[i] - x);
                outp[i] = x;
            }

            BasisFreqResponse r = BasisMotionResponse.Sine(inp, outp, k_Dt, hz);

            double w = 2 * Math.PI * hz * k_Dt;
            double phase = -Math.Atan2((1 - a) * Math.Sin(w), 1 - (1 - a) * Math.Cos(w));
            float expectedLagMs = (float)(-phase / (2 * Math.PI * hz) * 1000.0);
            double expectedGain = a / Math.Sqrt(1 + (1 - a) * (1 - a) - 2 * (1 - a) * Math.Cos(w));

            Assert.AreEqual(expectedLagMs, r.LagMs, 2f, $"lag should be {expectedLagMs:F1} ms, got {r.LagMs:F1}");
            Assert.AreEqual(expectedGain, r.Gain, 0.02, $"gain should be {expectedGain:F3}, got {r.Gain:F3}");
        }

        // ---------------------------------------------------------------- framerate invariance

        [Test]
        public void Invariance_PassesTheExponentialForm()
        {
            // The GUARDRAIL. This is the form the codebase's correct blends use, and if the invariance
            // gate ever fails it, the gate is broken and will be muted -- which is how a good test dies.
            BasisInvarianceResult r = BasisMotionResponse.Invariance(
                (dt, frames) => Chase(dt, frames, BasisMotionResponse.ExpAlpha(40f, dt)), 0.6f, "exp(40)");

            Assert.IsTrue(r.Ok, r.Error);
            Assert.Less(r.WorstDeviation, BasisMotionResponse.MaxFramerateDeviation,
                $"1 - exp(-rate*dt) IS framerate-independent; the gate must not flag it. {r}");
            Assert.Less(r.T63SpreadMs, 3f, $"and its time constant must be the same on every headset. {r}");
        }

        [Test]
        public void Invariance_PassesMoveTowards()
        {
            // The other correct form in the tree (footIKBlendWeight). Linear, exactly framerate-
            // independent. Included because the FIRST version of this gate failed it -- it resampled
            // onto a fine grid and interpolated across MoveTowards' saturation corner, manufacturing an
            // error that had nothing to do with framerate. Invariance() now compares only at sample
            // times two rates genuinely share, and this test is what holds it to that.
            BasisInvarianceResult r = BasisMotionResponse.Invariance((dt, frames) =>
            {
                var y = new float[frames];
                float x = 0f;
                for (int i = 0; i < frames; i++) { x = Mathf.MoveTowards(x, 1f, 20f * dt); y[i] = x; }
                return y;
            }, 0.6f, "MoveTowards(20)");

            Assert.IsTrue(r.Ok, r.Error);
            Assert.Less(r.WorstDeviation, BasisMotionResponse.MaxFramerateDeviation,
                $"MoveTowards is framerate-independent; the gate must not flag it. {r}");
        }

        [Test]
        public void Invariance_FailsTheSaturateForm_SoTheGateCannotRotIntoATautology()
        {
            // The paired negative. Without this, a bug that made Invariance() always return zero would
            // leave every other invariance test passing and nobody would know the gate had died.
            //
            // saturate(dt * speed) is the form that shipped in BasisLocalVirtualSpineDriver. Its time
            // constant is a function of the user's GPU, and above dt*speed = 1 it stops blending at all.
            BasisInvarianceResult r = BasisMotionResponse.Invariance(
                (dt, frames) => Chase(dt, frames, BasisMotionResponse.LegacySaturateAlpha(40f, dt)), 0.6f, "saturate(40)");

            Assert.IsTrue(r.Ok, r.Error);
            Assert.Greater(r.WorstDeviation, BasisMotionResponse.MaxFramerateDeviation * 2f,
                $"the gate FAILED TO CATCH the framerate-dependent form. It is no longer measuring anything. {r}");
        }

        [Test]
        public void SaturateForm_GetsLAGGIER_AsFramerateRises()
        {
            // The perverse fingerprint, pinned so nobody "optimises" the fix away later. With
            // saturate(dt*speed), a smaller dt means a smaller alpha means a SLOWER filter: the better
            // your headset, the more your neck lags. 11 ms at 72 Hz, 18 ms at 144 Hz. Nobody would ever
            // guess that from reading the line.
            float Lag(int fps)
            {
                float dt = 1f / fps;
                int n = (int)(10f / 1.0f * fps);
                var inp = new float[n];
                var outp = new float[n];
                float x = 0f;
                for (int i = 0; i < n; i++)
                {
                    inp[i] = Mathf.Sin(2f * Mathf.PI * 1.0f * (i + 1) * dt);
                    x += BasisMotionResponse.LegacySaturateAlpha(40f, dt) * (inp[i] - x);
                    outp[i] = x;
                }
                return BasisMotionResponse.Sine(inp, outp, dt, 1.0f).LagMs;
            }

            float lag72 = Lag(72), lag144 = Lag(144);
            Assert.Greater(lag144, lag72 * 1.3f,
                $"saturate(dt*speed) must get LAGGIER at higher framerate (72Hz:{lag72:F1}ms 144Hz:{lag144:F1}ms) -- " +
                "if this stops being true the legacy form has been silently corrected somewhere");
        }

        // ---------------------------------------------------------------- helpers

        static float[] Chase(float dt, int frames, float alpha)
        {
            var y = new float[frames];
            float x = 0f;
            for (int i = 0; i < frames; i++) { x += alpha * (1f - x); y[i] = x; }
            return y;
        }

        static Vector3[] OneFilter(Vector3[] p, float tauSeconds, float dt)
        {
            float a = 1f - Mathf.Exp(-dt / tauSeconds);
            var o = new Vector3[p.Length];
            o[0] = p[0];
            for (int i = 1; i < p.Length; i++) o[i] = o[i - 1] + a * (p[i] - o[i - 1]);
            return o;
        }

        /// <summary>A chain of minimum-jerk reaches to random targets -- a stand-in for a limb doing
        /// ordinary voluntary motion, with a realistic amount of jerk in it. Deterministic per seed.</summary>
        static Vector3[] SyntheticReachSequence(int frames, int seed)
        {
            var rng = new System.Random(seed);
            var p = new Vector3[frames];
            Vector3 cur = Vector3.zero;
            int i = 0;
            while (i < frames)
            {
                int len = Mathf.Min(frames - i, 50 + rng.Next(70));
                var next = new Vector3(
                    (float)(rng.NextDouble() - 0.5) * 0.5f,
                    (float)(rng.NextDouble() - 0.5) * 0.4f,
                    (float)(rng.NextDouble() - 0.5) * 0.5f);
                Vector3[] seg = BasisMotionSignal.MinJerk(cur, next, len);
                Array.Copy(seg, 0, p, i, len);
                cur = next;
                i += len;
            }
            return p;
        }
    }
}
