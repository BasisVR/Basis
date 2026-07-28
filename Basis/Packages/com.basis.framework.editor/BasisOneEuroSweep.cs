using System.Globalization;
using System.IO;
using System.Text;
using Basis.Scripts.Drivers;
using UnityEngine;

namespace Basis.IK.Debugging
{
    // Temporal sweep of the general-purpose One-Euro filter (BasisOneEuroFilterVector3 / BasisOneEuroFilterQuaternion) --
    // the smoothing the live rig runs on tracker/target inputs before they reach every IK solver. The dedicated
    // swivel sweep only covers the scalar swivel filter; this covers the Vector3/Quaternion filter the rest of
    // the stack depends on. Drives the same scenarios a low-pass must survive: a step (first-order = no
    // overshoot, converges), a constant-rate ramp (bounded steady-state lag), a held value + jitter (output
    // smoother than input), and a smooth sinusoid (must not add per-step stepping). The quaternion variant also
    // must stay unit and reject rotational jitter.
    public struct BasisOneEuroSweepConfig
    {
        public float Fps;
        public float Seconds;
        public float MinCutoff;          // Hz -- filter floor cutoff
        public float Beta;               // speed coefficient (adaptive cutoff slope)
        public float DCutoff;            // Hz -- derivative cutoff
        public float NoiseMeters;        // std of jitter injected in the vec3 noise-hold scenario
        public float NoiseDeg;           // std of jitter injected in the quat noise-hold scenario
        public float RampMetersPerSec;

        public static BasisOneEuroSweepConfig Default()
        {
            // Filter tuning mirrors the BasisOneEuroFilter class defaults (minCutoff=1, beta=0, dCutoff=1): a 1 Hz
            // first-order low-pass with no speed adaptation. Representative of the live filter; the window can
            // sweep the three tuning knobs to explore the latency/jitter tradeoff.
            return new BasisOneEuroSweepConfig
            {
                Fps = 90f,
                Seconds = 4f,
                MinCutoff = 1.0f,
                Beta = 0.0f,
                DCutoff = 1.0f,
                NoiseMeters = 0.01f,
                NoiseDeg = 3f,
                RampMetersPerSec = 1.0f,
            };
        }
    }

    public struct BasisOneEuroSweepSummary
    {
        public bool Ok;
        public string Path;
        public int Steps;
        public int NaNCount;
        public float MaxStepOvershootM;     // smoothed vec3 output past a step target (m)
        public float StepFinalErrM;         // |smooth - target| after settling (m)
        public float MaxRampLagM;           // steady-state lag tracking a constant-rate ramp (m)
        public float NoiseRejectRatio;      // vec3 output roughness / input roughness under jitter (<1 = smoother)
        public float MaxGlideJitterM;       // vec3 output 2nd-difference on a smooth sinusoid (added stepping)
        public float QuatNoiseRejectRatio;  // quaternion angular roughness out/in under jitter (<1 = smoother)
        public float QuatMaxGlideJitterDeg; // quaternion output 2nd-difference on a smooth yaw sinusoid (deg)
        public float QuatMaxNonUnit;        // worst |length(q)-1| of the filtered quaternion (should stay ~0)
        public string Error;
    }

    public static class BasisOneEuroSweep
    {
        public const string DefaultFileName = "BasisOneEuroSweep.csv";

        public static string DefaultPath() => System.IO.Path.Combine(Application.persistentDataPath, DefaultFileName);

        public static BasisOneEuroSweepSummary Run(BasisOneEuroSweepConfig cfg, string path)
        {
            var summary = new BasisOneEuroSweepSummary { Ok = false, Path = path };
            float fps = Mathf.Max(5f, cfg.Fps);
            float dt = 1f / fps;
            int n = Mathf.Max(8, Mathf.RoundToInt(cfg.Seconds * fps));
            int half = n / 2;

            int nan = 0;
            float stepOvershoot = 0f, stepFinal = 0f, rampLag = 0f, glide = 0f;
            float inRough = 0f, outRough = 0f;
            float quatInRough = 0f, quatOutRough = 0f, quatGlide = 0f, quatNonUnit = 0f;

            try
            {
                string d = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(d) && !Directory.Exists(d)) Directory.CreateDirectory(d);

                using (var w = new StreamWriter(path, false, Encoding.UTF8))
                {
                    w.WriteLine("# BasisOneEuroSweep " + System.DateTime.UtcNow.ToString("o") +
                                " fps=" + F(fps) + " minCutoff=" + F(cfg.MinCutoff) + " beta=" + F(cfg.Beta) +
                                " dCutoff=" + F(cfg.DCutoff) + " noiseM=" + F(cfg.NoiseMeters) + " noiseDeg=" + F(cfg.NoiseDeg));
                    w.WriteLine("scenario,step,input,smooth");
                    var sb = new StringBuilder(96);

                    // Vec3 step: 0 -> 1 m along X at the midpoint. First-order low-pass: no overshoot, converges.
                    {
                        var f = new BasisOneEuroFilterVector3(cfg.MinCutoff, cfg.Beta, cfg.DCutoff);
                        const float hi = 1f;
                        Vector3 outV = Vector3.zero;
                        for (int i = 0; i < n; i++)
                        {
                            Vector3 input = new Vector3(i < half ? 0f : hi, 0f, 0f);
                            outV = f.Filter(input, i * (double)dt);
                            if (!Finite(outV)) { nan++; break; }
                            if (i >= half)
                            {
                                float over = outV.x - hi;
                                if (over > stepOvershoot) stepOvershoot = over;
                            }
                            WriteRow(w, sb, "step", i, input.x, outV.x);
                        }
                        stepFinal = Mathf.Abs(outV.x - hi);
                    }

                    // Vec3 ramp: constant rate along X. Steady-state lag is bounded.
                    {
                        var f = new BasisOneEuroFilterVector3(cfg.MinCutoff, cfg.Beta, cfg.DCutoff);
                        for (int i = 0; i < n; i++)
                        {
                            float x = cfg.RampMetersPerSec * (i * dt);
                            Vector3 input = new Vector3(x, 0f, 0f);
                            Vector3 outV = f.Filter(input, i * (double)dt);
                            if (!Finite(outV)) { nan++; break; }
                            if (i >= half)
                            {
                                float lag = Mathf.Abs(x - outV.x);
                                if (lag > rampLag) rampLag = lag;
                            }
                            WriteRow(w, sb, "ramp", i, x, outV.x);
                        }
                    }

                    // Vec3 noise hold: held value + jitter. Output must be smoother than the input.
                    {
                        var rng = new System.Random(12345);
                        var f = new BasisOneEuroFilterVector3(cfg.MinCutoff, cfg.Beta, cfg.DCutoff);
                        float prevIn = 0f, prevPrevIn = 0f, prevOut = 0f, prevPrevOut = 0f;
                        double inAcc = 0.0, outAcc = 0.0; int rough = 0;
                        for (int i = 0; i < n; i++)
                        {
                            float x = (float)(rng.NextDouble() * 2.0 - 1.0) * cfg.NoiseMeters;
                            Vector3 outV = f.Filter(new Vector3(x, 0f, 0f), i * (double)dt);
                            if (!Finite(outV)) { nan++; break; }
                            if (i >= 2)
                            {
                                inAcc += Mathf.Abs(x - 2f * prevIn + prevPrevIn);
                                outAcc += Mathf.Abs(outV.x - 2f * prevOut + prevPrevOut);
                                rough++;
                            }
                            prevPrevIn = prevIn; prevIn = x;
                            prevPrevOut = prevOut; prevOut = outV.x;
                            WriteRow(w, sb, "noise", i, x, outV.x);
                        }
                        inRough = rough > 0 ? (float)(inAcc / rough) : 0f;
                        outRough = rough > 0 ? (float)(outAcc / rough) : 0f;
                    }

                    // Vec3 smooth sinusoid: the filter must not add stepping (2nd-diff) as the input glides.
                    {
                        var f = new BasisOneEuroFilterVector3(cfg.MinCutoff, cfg.Beta, cfg.DCutoff);
                        float prevOut = 0f, prevPrevOut = 0f;
                        const float amp = 0.5f, freq = 0.5f;
                        for (int i = 0; i < n; i++)
                        {
                            float x = amp * Mathf.Sin(2f * Mathf.PI * freq * (i * dt));
                            Vector3 outV = f.Filter(new Vector3(x, 0f, 0f), i * (double)dt);
                            if (!Finite(outV)) { nan++; break; }
                            if (i >= 2)
                            {
                                float j = Mathf.Abs(outV.x - 2f * prevOut + prevPrevOut);
                                if (j > glide) glide = j;
                            }
                            prevPrevOut = prevOut; prevOut = outV.x;
                            WriteRow(w, sb, "sine", i, x, outV.x);
                        }
                    }

                    // Quat noise hold: yaw jitter about Y around identity. Filtered rotation must be smoother and unit.
                    {
                        var rng = new System.Random(54321);
                        var f = new BasisOneEuroFilterQuaternion(cfg.MinCutoff, cfg.Beta, cfg.DCutoff);
                        float prevIn = 0f, prevPrevIn = 0f, prevOut = 0f, prevPrevOut = 0f;
                        double inAcc = 0.0, outAcc = 0.0; int rough = 0;
                        for (int i = 0; i < n; i++)
                        {
                            float yaw = (float)(rng.NextDouble() * 2.0 - 1.0) * cfg.NoiseDeg;
                            Quaternion q = Quaternion.AngleAxis(yaw, Vector3.up);
                            Quaternion outQ = f.Filter(q, i * dt);
                            if (!Finite(outQ)) { nan++; break; }
                            float oy = SignedYaw(outQ);
                            float nonUnit = Mathf.Abs(QuatLength(outQ) - 1f);
                            if (nonUnit > quatNonUnit) quatNonUnit = nonUnit;
                            if (i >= 2)
                            {
                                inAcc += Mathf.Abs(yaw - 2f * prevIn + prevPrevIn);
                                outAcc += Mathf.Abs(oy - 2f * prevOut + prevPrevOut);
                                rough++;
                            }
                            prevPrevIn = prevIn; prevIn = yaw;
                            prevPrevOut = prevOut; prevOut = oy;
                            WriteRow(w, sb, "quatNoise", i, yaw, oy);
                        }
                        quatInRough = rough > 0 ? (float)(inAcc / rough) : 0f;
                        quatOutRough = rough > 0 ? (float)(outAcc / rough) : 0f;
                    }

                    // Quat smooth sinusoid: yaw glide about Y. Must not add per-step stepping.
                    {
                        var f = new BasisOneEuroFilterQuaternion(cfg.MinCutoff, cfg.Beta, cfg.DCutoff);
                        float prevOut = 0f, prevPrevOut = 0f;
                        const float amp = 25f, freq = 0.5f;
                        for (int i = 0; i < n; i++)
                        {
                            float yaw = amp * Mathf.Sin(2f * Mathf.PI * freq * (i * dt));
                            Quaternion q = Quaternion.AngleAxis(yaw, Vector3.up);
                            Quaternion outQ = f.Filter(q, i * dt);
                            if (!Finite(outQ)) { nan++; break; }
                            float oy = SignedYaw(outQ);
                            float nonUnit = Mathf.Abs(QuatLength(outQ) - 1f);
                            if (nonUnit > quatNonUnit) quatNonUnit = nonUnit;
                            if (i >= 2)
                            {
                                float j = Mathf.Abs(oy - 2f * prevOut + prevPrevOut);
                                if (j > quatGlide) quatGlide = j;
                            }
                            prevPrevOut = prevOut; prevOut = oy;
                            WriteRow(w, sb, "quatSine", i, yaw, oy);
                        }
                    }
                }

                summary.Ok = true;
                summary.Steps = n;
                summary.NaNCount = nan;
                summary.MaxStepOvershootM = stepOvershoot;
                summary.StepFinalErrM = stepFinal;
                summary.MaxRampLagM = rampLag;
                summary.NoiseRejectRatio = inRough > 1e-6f ? outRough / inRough : 0f;
                summary.MaxGlideJitterM = glide;
                summary.QuatNoiseRejectRatio = quatInRough > 1e-6f ? quatOutRough / quatInRough : 0f;
                summary.QuatMaxGlideJitterDeg = quatGlide;
                summary.QuatMaxNonUnit = quatNonUnit;
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

        static float SignedYaw(Quaternion q) => Mathf.DeltaAngle(0f, q.eulerAngles.y);
        static float QuatLength(Quaternion q) => Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);

        static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        static bool Finite(Vector3 v) => Finite(v.x) && Finite(v.y) && Finite(v.z);
        static bool Finite(Quaternion q) => Finite(q.x) && Finite(q.y) && Finite(q.z) && Finite(q.w);

        static string F(float v)
        {
            if (float.IsNaN(v)) return "nan";
            return v.ToString("0.######", CultureInfo.InvariantCulture);
        }
    }
}
