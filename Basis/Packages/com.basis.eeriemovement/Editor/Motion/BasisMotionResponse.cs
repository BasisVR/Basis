using System;
using UnityEngine;

namespace Basis.IK.Motion
{
    /// <summary>How a blend responds to a known input: its time constant, its overshoot, its lag, and
    /// -- the one that actually matters in VR -- whether any of those change with framerate.</summary>
    public struct BasisStepResponse
    {
        public bool Ok;
        public string Error;
        public float T63Ms;        // time to 63.2%. This IS the time constant; it is what "blend speed" means.
        public float T90Ms;
        public float OvershootPct; // 0 for a well-behaved first-order blend; > 0 means it went past and came back
        public float SettleMs;     // time after which it stays inside +/-2%
        public override string ToString() => $"t63={T63Ms:F1}ms t90={T90Ms:F1}ms overshoot={OvershootPct:F1}% settle={SettleMs:F0}ms";
    }

    /// <summary>Gain and lag at one driving frequency -- one point on a Bode plot.</summary>
    public struct BasisFreqResponse
    {
        public float Hz;
        public float Gain;    // 1.0 = passes the motion through untouched; < 1 = attenuates it
        public float LagMs;
        public float PhaseDeg;
        public override string ToString() => $"{Hz:F2}Hz: gain={Gain:F3} lag={LagMs:F1}ms";
    }

    /// <summary>Result of driving the same blend at every framerate a headset might run at.</summary>
    public struct BasisInvarianceResult
    {
        public bool Ok;
        public string Error;
        public string Label;
        public int[] Fps;
        public float[] T63Ms;         // per rate
        public float WorstDeviation;  // worst disagreement between any two rates, at coincident sample times
        public int WorstFpsA, WorstFpsB;
        public float T63SpreadMs;

        public override string ToString()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(Label).Append(": dev=").Append(WorstDeviation.ToString("F4"))
              .Append(" (").Append(WorstFpsA).Append(" vs ").Append(WorstFpsB).Append("Hz)")
              .Append(" t63spread=").Append(T63SpreadMs.ToString("F1")).Append("ms  [");
            for (int i = 0; i < Fps.Length; i++)
                sb.Append(Fps[i]).Append("Hz:").Append(T63Ms[i].ToString("F1")).Append(i < Fps.Length - 1 ? "  " : "");
            return sb.Append(']').ToString();
        }
    }

    /// <summary>Runs one blend for `frames` steps at timestep `dt`, returning its value after each step.
    /// Sample i is the state AFTER the (i+1)-th update, i.e. at t = (i+1)*dt.</summary>
    public delegate float[] BasisBlendSampler(float dt, int frames);

    /// <summary>
    /// The "is it blending at the correct speed" engine.
    ///
    /// A blend's speed is only meaningful if it is the SAME speed on every machine. Basis ships to
    /// headsets running 72, 80, 90, 120 and 144 Hz, and to desktop at whatever the monitor does. A
    /// smoother written as
    ///
    ///     t = saturate(dt * speed);  x = lerp(x, target, t);          // WRONG
    ///
    /// has a time constant that is a function of the user's GPU. Written as
    ///
    ///     t = 1 - exp(-rate * dt);   x = lerp(x, target, t);          // RIGHT
    ///
    /// it does not. The two look nearly identical at any single framerate, which is exactly why the
    /// wrong one survives code review, and why this needs to be a permanent gate rather than a thing
    /// someone notices once.
    ///
    /// The wrong form also has a second, nastier property: `saturate` clamps at dt*speed >= 1, so a
    /// blend with speed 40 stops blending AT ALL below 40 fps -- it snaps, every frame, and the
    /// smoothing silently ceases to exist exactly when the framerate is bad enough to need it most.
    ///
    /// ---------------------------------------------------------------------------------------------
    /// HOW Invariance() COMPARES, AND WHY IT IS FUSSY ABOUT IT:
    ///
    /// The obvious implementation -- resample every curve onto a fine common grid and diff them --
    /// FAILS THE CORRECT BLENDS. Interpolating a curve across a corner (MoveTowards saturating at 1.0,
    /// say) manufactures an error that has nothing to do with framerate, and the test then reports the
    /// good code as broken. That happened on the first attempt, and a test that cries wolf on correct
    /// code is worse than no test: it gets muted.
    ///
    /// So Invariance() compares two rates ONLY at sample times they genuinely share. For rates fa and
    /// fb, both have a sample at every multiple of 1/gcd(fa,fb) seconds. 72 and 90 coincide at 18 Hz;
    /// 120 and 144 at 24 Hz. Exact indices, zero interpolation, zero manufactured error.
    /// ---------------------------------------------------------------------------------------------
    /// </summary>
    public static class BasisMotionResponse
    {
        /// <summary>The rates Basis actually ships at. Deliberately NOT including 30: a 33 ms frame
        /// cannot resolve a 25 ms time constant, so a "disagreement" there is the observer, not the
        /// code, and folding it in would flag every correct blend.</summary>
        public static readonly int[] VrFrameRates = { 72, 80, 90, 120, 144 };

        /// <summary>A correct blend holds its step response to well under this across all VR rates.
        /// Measured: the exp/MoveTowards forms in this codebase land at 0.0003-0.006; the four
        /// saturate(dt*speed) sites in the virtual spine land at 0.031-0.077. A 5x margin either side.</summary>
        public const float MaxFramerateDeviation = 0.015f;

        // ------------------------------------------------------------------ step response

        public static BasisStepResponse Step(float[] y, float dt, float target = 1f)
        {
            var r = new BasisStepResponse { T63Ms = float.NaN, T90Ms = float.NaN, SettleMs = float.NaN };
            if (y == null || y.Length < 4) { r.Error = "need >= 4 samples"; return r; }

            r.T63Ms = CrossTime(y, dt, 0.632f * target);
            r.T90Ms = CrossTime(y, dt, 0.90f * target);

            float max = float.NegativeInfinity;
            for (int i = 0; i < y.Length; i++) if (y[i] > max) max = y[i];
            r.OvershootPct = target > 1e-9f && max > target ? (max - target) / target * 100f : 0f;

            float band = 0.02f * Mathf.Abs(target);
            r.SettleMs = float.NaN;
            for (int i = y.Length - 1; i >= 0; i--)
            {
                if (Mathf.Abs(y[i] - target) > band)
                {
                    r.SettleMs = i + 1 < y.Length ? (i + 2) * dt * 1000f : float.NaN;
                    break;
                }
                if (i == 0) r.SettleMs = dt * 1000f;
            }

            r.Ok = true;
            return r;
        }

        /// <summary>Time the signal first reaches `level`, interpolated between samples. Interpolated
        /// because at 72 Hz a frame is 14 ms, and reading a 20 ms time constant off the sample grid
        /// alone quantises it into uselessness.</summary>
        static float CrossTime(float[] y, float dt, float level)
        {
            for (int i = 0; i < y.Length; i++)
            {
                if (y[i] < level) continue;
                if (i == 0) return dt * 1000f;
                float y0 = y[i - 1], y1 = y[i];
                float f = y1 > y0 ? (level - y0) / (y1 - y0) : 0f;
                return (i + f) * dt * 1000f;
            }
            return float.NaN;
        }

        // ------------------------------------------------------------------ frequency response

        /// <summary>Gain and lag at one frequency, by projecting both signals onto sin/cos at that
        /// frequency. The first half of the record is discarded as filter transient.
        ///
        /// This is how lag gets measured, and it is deliberately NOT inferred from arbitrary motion:
        /// cross-correlating a smoothed signal against its source cannot recover a filter's group
        /// delay, because a smoothed signal is a different SHAPE, not a shifted copy. Attempting it
        /// reported 0 ms for a 150 ms filter. Drive it with a sinusoid and the answer is exact.</summary>
        public static BasisFreqResponse Sine(float[] input, float[] output, float dt, float hz)
        {
            var r = new BasisFreqResponse { Hz = hz, Gain = float.NaN, LagMs = float.NaN, PhaseDeg = float.NaN };
            int n = Mathf.Min(input?.Length ?? 0, output?.Length ?? 0);
            if (n < 32 || hz <= 0f) return r;

            int start = n / 2;
            double os = 0, oc = 0, ins = 0, inc = 0;
            int m = 0;
            for (int i = start; i < n; i++)
            {
                double t = (i + 1) * dt;
                double sn = Math.Sin(2 * Math.PI * hz * t), cs = Math.Cos(2 * Math.PI * hz * t);
                os += output[i] * sn; oc += output[i] * cs;
                ins += input[i] * sn; inc += input[i] * cs;
                m++;
            }
            if (m == 0) return r;
            os /= m; oc /= m; ins /= m; inc /= m;

            double outMag = Math.Sqrt(os * os + oc * oc);
            double inMag = Math.Sqrt(ins * ins + inc * inc);
            if (inMag < 1e-12) return r;

            r.Gain = (float)(outMag / inMag);
            double phase = Math.Atan2(oc, os) - Math.Atan2(inc, ins);
            while (phase > Math.PI) phase -= 2 * Math.PI;
            while (phase < -Math.PI) phase += 2 * Math.PI;
            r.PhaseDeg = (float)(phase * Mathf.Rad2Deg);
            r.LagMs = (float)(-phase / (2 * Math.PI * hz) * 1000.0);
            return r;
        }

        // ------------------------------------------------------------------ framerate invariance

        public static BasisInvarianceResult Invariance(
            BasisBlendSampler run, float durationSec, string label, int[] fps = null)
        {
            fps ??= VrFrameRates;
            var r = new BasisInvarianceResult
            {
                Label = label,
                Fps = (int[])fps.Clone(),
                T63Ms = new float[fps.Length],
                WorstDeviation = 0f,
            };

            var curves = new float[fps.Length][];
            for (int i = 0; i < fps.Length; i++)
            {
                float dt = 1f / fps[i];
                int frames = Mathf.Max(4, Mathf.RoundToInt(durationSec * fps[i]));
                curves[i] = run(dt, frames);
                if (curves[i] == null || curves[i].Length < 4)
                {
                    r.Error = $"sampler returned {curves[i]?.Length ?? 0} frames at {fps[i]}Hz";
                    return r;
                }
                r.T63Ms[i] = Step(curves[i], dt).T63Ms;
            }

            float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
            foreach (float t in r.T63Ms)
            {
                if (float.IsNaN(t)) continue;
                lo = Mathf.Min(lo, t); hi = Mathf.Max(hi, t);
            }
            r.T63SpreadMs = float.IsInfinity(lo) ? float.NaN : hi - lo;

            // Compare every pair only where they genuinely share a sample time (see class docs).
            for (int a = 0; a < fps.Length; a++)
            {
                for (int b = a + 1; b < fps.Length; b++)
                {
                    int g = Gcd(fps[a], fps[b]);
                    int strideA = fps[a] / g, strideB = fps[b] / g;
                    int ticks = Mathf.Min(curves[a].Length / strideA, curves[b].Length / strideB);
                    for (int k = 1; k <= ticks; k++)
                    {
                        int ia = k * strideA - 1, ib = k * strideB - 1;
                        if (ia >= curves[a].Length || ib >= curves[b].Length) break;
                        float d = Mathf.Abs(curves[a][ia] - curves[b][ib]);
                        if (d > r.WorstDeviation)
                        {
                            r.WorstDeviation = d;
                            r.WorstFpsA = fps[a];
                            r.WorstFpsB = fps[b];
                        }
                    }
                }
            }

            r.Ok = true;
            return r;
        }

        static int Gcd(int a, int b)
        {
            while (b != 0) { (a, b) = (b, a % b); }
            return a;
        }

        // ------------------------------------------------------------------ the two blend forms

        /// <summary>The framerate-CORRECT exponential blend. `rate` is a true rate (1/s); the time
        /// constant is exactly 1/rate at every framerate.</summary>
        public static float ExpAlpha(float rate, float dt) => 1f - Mathf.Exp(-Mathf.Max(0f, rate) * dt);

        /// <summary>The framerate-DEPENDENT form, kept here ONLY so tests can drive it and prove the
        /// gate fails it. Never call this from shipping code.</summary>
        public static float LegacySaturateAlpha(float speed, float dt) => Mathf.Clamp01(dt * Mathf.Max(0f, speed));
    }
}
