using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using Basis.IK;

namespace Basis.IK.Debugging
{
    // Temporal sweep of BasisSwivelFilterCore (the One-Euro elbow-swivel low-pass). Drives the filter with
    // scripted swivel signals and measures the properties the filter exists for: it must not overshoot a
    // step, must converge to a held value, must be smoother than a noisy input (jitter rejection), and must
    // not add per-step stepping on a smooth glide. Same filter the live SmoothElbowSwivel runs.
    public struct BasisSwivelFilterSweepConfig
    {
        public float Fps;
        public float Seconds;
        public float NoiseDeg;     // std-dev of the jitter injected in the noise-hold scenario
        public float RampDegPerSec;

        public static BasisSwivelFilterSweepConfig Default()
        {
            return new BasisSwivelFilterSweepConfig
            {
                Fps = 90f,
                Seconds = 4f,
                NoiseDeg = 3f,
                RampDegPerSec = 90f,
            };
        }
    }

    public struct BasisSwivelFilterSweepSummary
    {
        public bool Ok;
        public string Path;
        public int Steps;
        public int NaNCount;
        public float MaxStepOvershootDeg;  // smoothed output past a step target
        public float StepFinalErrDeg;      // |smooth - target| after settling
        public float MaxRampLagDeg;        // steady-state lag tracking a constant-rate ramp
        public float NoiseRejectRatio;     // output roughness / input roughness under jitter (<1 = smoother)
        public float MaxGlideJitterDeg;    // output 2nd-difference on a smooth sinusoid (added stepping)
        public string Error;
    }

    public static class BasisSwivelFilterSweep
    {
        public const string DefaultFileName = "BasisSwivelFilterSweep.csv";

        public static string DefaultPath() => System.IO.Path.Combine(Application.persistentDataPath, DefaultFileName);

        public static BasisSwivelFilterSweepSummary Run(BasisSwivelFilterSweepConfig cfg, string path)
        {
            var summary = new BasisSwivelFilterSweepSummary { Ok = false, Path = path };
            float fps = Mathf.Max(5f, cfg.Fps);
            float dt = 1f / fps;
            int n = Mathf.Max(8, Mathf.RoundToInt(cfg.Seconds * fps));
            int half = n / 2;

            int nan = 0;
            float stepOvershoot = 0f, stepFinal = 0f, rampLag = 0f, glide = 0f;
            float inRough = 0f, outRough = 0f;

            try
            {
                string d = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(d) && !Directory.Exists(d)) Directory.CreateDirectory(d);

                using (var w = new StreamWriter(path, false, Encoding.UTF8))
                {
                    w.WriteLine("# BasisSwivelFilterSweep " + System.DateTime.UtcNow.ToString("o") +
                                " fps=" + F(fps) + " noise=" + F(cfg.NoiseDeg) + " ramp=" + F(cfg.RampDegPerSec));
                    w.WriteLine("scenario,step,input,smooth");
                    var sb = new StringBuilder(96);

                    // Step: 0 -> 30 at the midpoint. First-order low-pass: no overshoot, converges.
                    {
                        const float lo = 0f, hi = 30f;
                        var s = BasisSwivelFilterCore.Seed(lo);
                        for (int i = 0; i < n; i++)
                        {
                            float input = i < half ? lo : hi;
                            s = BasisSwivelFilterCore.Step(s, input, dt);
                            if (!Finite(s.Smooth)) { nan++; break; }
                            if (i >= half)
                            {
                                float over = s.Smooth - hi;
                                if (over > stepOvershoot) stepOvershoot = over;
                            }
                            WriteRow(w, sb, "step", i, input, s.Smooth);
                        }
                        stepFinal = Mathf.Abs(s.Smooth - hi);
                    }

                    // Ramp: constant rate. Steady-state lag is bounded (the adaptive cutoff opens with speed).
                    {
                        var s = BasisSwivelFilterCore.Seed(0f);
                        for (int i = 0; i < n; i++)
                        {
                            float input = cfg.RampDegPerSec * (i * dt);
                            s = BasisSwivelFilterCore.Step(s, input, dt);
                            if (!Finite(s.Smooth)) { nan++; break; }
                            if (i >= half)
                            {
                                float lag = Mathf.Abs(input - s.Smooth);
                                if (lag > rampLag) rampLag = lag;
                            }
                            WriteRow(w, sb, "ramp", i, input, s.Smooth);
                        }
                    }

                    // Noise hold: held value + jitter. Output must be smoother than the input.
                    {
                        var rng = new System.Random(12345);
                        var s = BasisSwivelFilterCore.Seed(0f);
                        float prevIn = 0f, prevPrevIn = 0f, prevOut = 0f, prevPrevOut = 0f;
                        double inAcc = 0.0, outAcc = 0.0; int rough = 0;
                        for (int i = 0; i < n; i++)
                        {
                            float input = (float)(rng.NextDouble() * 2.0 - 1.0) * cfg.NoiseDeg;
                            s = BasisSwivelFilterCore.Step(s, input, dt);
                            if (!Finite(s.Smooth)) { nan++; break; }
                            if (i >= 2)
                            {
                                inAcc += Mathf.Abs(input - 2f * prevIn + prevPrevIn);
                                outAcc += Mathf.Abs(s.Smooth - 2f * prevOut + prevPrevOut);
                                rough++;
                            }
                            prevPrevIn = prevIn; prevIn = input;
                            prevPrevOut = prevOut; prevOut = s.Smooth;
                            WriteRow(w, sb, "noise", i, input, s.Smooth);
                        }
                        inRough = rough > 0 ? (float)(inAcc / rough) : 0f;
                        outRough = rough > 0 ? (float)(outAcc / rough) : 0f;
                    }

                    // Smooth sinusoid: the filter must not add stepping (2nd-diff) as the input glides.
                    {
                        var s = BasisSwivelFilterCore.Seed(0f);
                        float prevOut = 0f, prevPrevOut = 0f;
                        const float amp = 25f, freq = 0.5f;
                        for (int i = 0; i < n; i++)
                        {
                            float input = amp * Mathf.Sin(2f * Mathf.PI * freq * (i * dt));
                            s = BasisSwivelFilterCore.Step(s, input, dt);
                            if (!Finite(s.Smooth)) { nan++; break; }
                            if (i >= 2)
                            {
                                float j = Mathf.Abs(s.Smooth - 2f * prevOut + prevPrevOut);
                                if (j > glide) glide = j;
                            }
                            prevPrevOut = prevOut; prevOut = s.Smooth;
                            WriteRow(w, sb, "sine", i, input, s.Smooth);
                        }
                    }
                }

                summary.Ok = true;
                summary.Steps = n;
                summary.NaNCount = nan;
                summary.MaxStepOvershootDeg = stepOvershoot;
                summary.StepFinalErrDeg = stepFinal;
                summary.MaxRampLagDeg = rampLag;
                summary.NoiseRejectRatio = inRough > 1e-6f ? outRough / inRough : 0f;
                summary.MaxGlideJitterDeg = glide;
            }
            catch (System.Exception e)
            {
                summary.Ok = false;
                summary.Error = e.Message;
            }

            return summary;
        }

        static void WriteRow(StreamWriter w, StringBuilder sb, string scenario, int step, float input, float smooth)
        {
            sb.Clear();
            sb.Append(scenario).Append(',').Append(step).Append(',');
            sb.Append(F(input)).Append(',').Append(F(smooth));
            w.WriteLine(sb.ToString());
        }

        static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        static string F(float v)
        {
            if (float.IsNaN(v)) return "nan";
            return v.ToString("0.######", CultureInfo.InvariantCulture);
        }
    }
}
