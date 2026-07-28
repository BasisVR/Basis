using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using Basis.IK;

namespace Basis.IK.Debugging
{
    // Stability sweep of BasisChestSpringCore over hz x damping x fps. The chest-follow spring uses
    // implicit Euler precisely so it never diverges at high stiffness / low fps (omega*dt >> 1), where
    // explicit Euler explodes into NaN and poisons the rig. This drives a unit step response per config
    // and proves: it never blows up (the claim), well-damped configs settle on the target, and
    // critically/over-damped configs do not overshoot. Forward Euler is run alongside for contrast only.
    public struct BasisChestSpringSweepConfig
    {
        public float SettleSeconds;

        public static BasisChestSpringSweepConfig Default()
        {
            return new BasisChestSpringSweepConfig { SettleSeconds = 3f };
        }
    }

    public struct BasisChestSpringSweepSummary
    {
        public bool Ok;
        public string Path;
        public int Configs;
        public int DivergedCount;          // implicit step blew up (NaN/Inf or |pos| > 1e3)
        public int ExplicitDivergedCount;  // same configs under forward Euler (context only, not gated)
        public float MaxFinalErrSettling;  // |pos - target| at end, over well-settling configs (hz>=1, damping>=0.5)
        public float MaxOverdampedOvershoot; // overshoot past target for damping>=1 (should be ~0)
        public float MaxAbsPos;            // worst |pos| among non-diverged configs
        public int Failures;
        public string Error;
    }

    public static class BasisChestSpringSweep
    {
        public const string DefaultFileName = "BasisChestSpringSweep.csv";
        const float k_DivergeBound = 1e3f; // step target is 1; anything past this has blown up

        static readonly float[] k_Hz = { 0.5f, 1f, 2f, 5f, 10f, 20f, 50f };
        static readonly float[] k_Damping = { 0.3f, 0.7f, 1f, 1.5f, 2f };
        static readonly float[] k_Fps = { 8f, 15f, 30f, 60f, 72f, 90f, 144f };

        public static string DefaultPath() => System.IO.Path.Combine(Application.persistentDataPath, DefaultFileName);

        public static BasisChestSpringSweepSummary Run(BasisChestSpringSweepConfig cfg, string path)
        {
            var summary = new BasisChestSpringSweepSummary { Ok = false, Path = path };
            float settle = Mathf.Max(0.1f, cfg.SettleSeconds);

            int configs = 0, diverged = 0, explDiverged = 0, fails = 0;
            float maxFinal = 0f, maxOver = 0f, maxAbs = 0f;

            try
            {
                string dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                using (var w = new StreamWriter(path, false, Encoding.UTF8))
                {
                    w.WriteLine("# BasisChestSpringSweep " + System.DateTime.UtcNow.ToString("o") + " settle=" + F(settle) + "s");
                    w.WriteLine("hz,damping,fps,steps,final_err,overshoot,max_abs,implicit_diverged,explicit_diverged,fail");
                    var sb = new StringBuilder(160);

                    foreach (float hz in k_Hz)
                    {
                        foreach (float damping in k_Damping)
                        {
                            foreach (float fps in k_Fps)
                            {
                                float dt = 1f / fps;
                                int steps = Mathf.Max(1, Mathf.RoundToInt(settle * fps));
                                Vector3 target = new Vector3(1f, 0f, 0f);

                                // Implicit (production) integrator.
                                Vector3 pos = Vector3.zero, vel = Vector3.zero;
                                bool impDiv = false;
                                float overshoot = 0f, absMax = 0f;
                                for (int n = 0; n < steps; n++)
                                {
                                    BasisChestSpringCore.Step(pos, vel, target, dt, hz, damping, out pos, out vel);
                                    if (!Finite(pos) || !Finite(vel) || Mathf.Abs(pos.x) > k_DivergeBound) { impDiv = true; break; }
                                    if (pos.x - target.x > overshoot) overshoot = pos.x - target.x;
                                    float a = Mathf.Abs(pos.x);
                                    if (a > absMax) absMax = a;
                                }
                                float finalErr = impDiv ? float.NaN : Mathf.Abs(pos.x - target.x);

                                // Forward Euler on the same config -- contrast only, shows where explicit blows up.
                                bool expDiv = ForwardEulerDiverges(target.x, dt, hz, damping, steps);

                                bool settling = hz >= 1f && damping >= 0.5f;
                                bool overdamped = damping >= 1f;

                                configs++;
                                if (impDiv) diverged++;
                                if (expDiv) explDiverged++;
                                if (!impDiv)
                                {
                                    if (absMax > maxAbs) maxAbs = absMax;
                                    if (settling && finalErr > maxFinal) maxFinal = finalErr;
                                    if (overdamped && overshoot > maxOver) maxOver = overshoot;
                                }

                                bool fail = impDiv || (settling && finalErr > 0.05f) || (overdamped && overshoot > 0.1f);
                                if (fail) fails++;

                                sb.Clear();
                                Append(sb, hz); Append(sb, damping); Append(sb, fps);
                                sb.Append(steps).Append(',');
                                Append(sb, finalErr); Append(sb, overshoot); Append(sb, absMax);
                                sb.Append(impDiv ? '1' : '0').Append(',');
                                sb.Append(expDiv ? '1' : '0').Append(',');
                                sb.Append(fail ? '1' : '0');
                                w.WriteLine(sb.ToString());
                            }
                        }
                    }
                }

                summary.Ok = true;
                summary.Configs = configs;
                summary.DivergedCount = diverged;
                summary.ExplicitDivergedCount = explDiverged;
                summary.MaxFinalErrSettling = maxFinal;
                summary.MaxOverdampedOvershoot = maxOver;
                summary.MaxAbsPos = maxAbs;
                summary.Failures = fails;
            }
            catch (System.Exception e)
            {
                summary.Ok = false;
                summary.Error = e.Message;
            }

            return summary;
        }

        // Forward (explicit) Euler integration of the same damped spring; returns true if it blows up.
        // Not the production path -- only run to document the stability margin implicit Euler buys.
        static bool ForwardEulerDiverges(float target, float dt, float hz, float damping, int steps)
        {
            float omega = 2f * Mathf.PI * hz;
            float omegaSq = omega * omega;
            float twoOmegaDamping = 2f * omega * Mathf.Max(0f, damping);
            float pos = 0f, vel = 0f;
            for (int n = 0; n < steps; n++)
            {
                float accel = omegaSq * (target - pos) - twoOmegaDamping * vel;
                pos += dt * vel;
                vel += dt * accel;
                if (float.IsNaN(pos) || float.IsInfinity(pos) || Mathf.Abs(pos) > k_DivergeBound) return true;
            }
            return false;
        }

        static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        static bool Finite(Vector3 v) => Finite(v.x) && Finite(v.y) && Finite(v.z);

        static void Append(StringBuilder sb, float v) { sb.Append(F(v)).Append(','); }

        static string F(float v)
        {
            if (float.IsNaN(v)) return "nan";
            return v.ToString("0.######", CultureInfo.InvariantCulture);
        }
    }
}
