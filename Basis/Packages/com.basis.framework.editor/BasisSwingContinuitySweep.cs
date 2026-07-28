using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using Basis.IK;

namespace Basis.IK.Debugging
{
    // Temporal sweep of BasisSwingContinuityCore (the collision-gated elbow/knee swing rate limiter).
    // Drives scripted frame sequences through the real stateful core and checks its contract: while easing
    // a collision pop the swing never moves faster than rate*dt, it converges to the target, free-air motion
    // (no collision change) is accepted instantly, and a target teleport re-seeds instantly. The chain is
    // modeled abstractly (fixed root->tip axis, the elbow on the swivel circle). Same logic the live job runs.
    public struct BasisSwingContinuitySweepConfig
    {
        public float Fps;
        public float RateDegPerSec;
        public float JumpDeg;     // collision-driven desired-swivel jump
        public int Frames;

        public static BasisSwingContinuitySweepConfig Default()
        {
            return new BasisSwingContinuitySweepConfig
            {
                Fps = 90f,
                RateDegPerSec = 120f,
                JumpDeg = 80f,
                Frames = 200,
            };
        }
    }

    public struct BasisSwingContinuitySweepSummary
    {
        public bool Ok;
        public string Path;
        public int Steps;
        public int NaNCount;
        public float MaxEasingStepDeg;    // largest swing step on an easing frame (must be <= rate*dt)
        public int RateLimitViolations;   // easing frames exceeding rate*dt
        public bool Converged;            // collision-step scenario reached the target
        public int ConvergeFrames;        // frames to converge after the collision change
        public float FreeAirMaxLagDeg;    // lag behind the target with no collision (must be ~0, instant)
        public bool TeleportAccepted;     // a target teleport re-seeds instantly
        public int Failures;
        public string Error;
    }

    public static class BasisSwingContinuitySweep
    {
        public const string DefaultFileName = "BasisSwingContinuitySweep.csv";
        const float k_R = 0.2f;       // elbow radius off the axis
        const float k_MidZ = 0.25f;   // elbow position along the axis
        const float k_L = 0.5f;       // root->tip length
        const float k_ConvergeTolDeg = 1f;

        public static string DefaultPath() => System.IO.Path.Combine(Application.persistentDataPath, DefaultFileName);

        public static BasisSwingContinuitySweepSummary Run(BasisSwingContinuitySweepConfig cfg, string path)
        {
            var summary = new BasisSwingContinuitySweepSummary { Ok = false, Path = path };
            float fps = Mathf.Max(5f, cfg.Fps);
            float dt = 1f / fps;
            float rate = Mathf.Max(1f, cfg.RateDegPerSec);
            float maxStep = rate * dt;
            int frames = Mathf.Max(40, cfg.Frames);
            int F = Mathf.Clamp(frames / 10, 5, frames - 10); // collision onset frame

            int nan = 0, rateViol = 0, fails = 0, convergeFrames = -1;
            float maxEasingStep = 0f, freeAirLag = 0f;
            bool converged = false, teleportAccepted = false;

            try
            {
                string d = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(d) && !Directory.Exists(d)) Directory.CreateDirectory(d);

                using (var w = new StreamWriter(path, false, Encoding.UTF8))
                {
                    w.WriteLine("# BasisSwingContinuitySweep " + System.DateTime.UtcNow.ToString("o") +
                                " fps=" + F2(fps) + " rate=" + F2(rate) + " maxStep=" + F2(maxStep) + " jump=" + F2(cfg.JumpDeg));
                    w.WriteLine("scenario,frame,collided,desired,applied,step,easing,fail");
                    var sb = new StringBuilder(120);

                    // Scenario 1: collision step. Arms at F, desired jumps; must rate-limit then converge.
                    {
                        var st = Fresh();
                        float prevApplied = 0f; bool havePrev = false;
                        for (int i = 0; i < frames; i++)
                        {
                            int collided = i < F ? 0 : 1;
                            float desired = i < F ? 0f : cfg.JumpDeg;
                            bool easing = StepSim(ref st, desired, collided, Vector3.zero, rate, dt, out float applied, out bool nanHit);
                            if (nanHit) { nan++; break; }

                            float step = havePrev ? Mathf.Abs(Mathf.DeltaAngle(prevApplied, applied)) : 0f;
                            bool viol = easing && step > maxStep + 0.5f;
                            if (easing && step > maxEasingStep) maxEasingStep = step;
                            if (viol) rateViol++;
                            if (i >= F && !converged && Mathf.Abs(Mathf.DeltaAngle(applied, desired)) < k_ConvergeTolDeg)
                            {
                                converged = true; convergeFrames = i - F;
                            }
                            prevApplied = applied; havePrev = true;
                            Row(w, sb, "step", i, collided, desired, applied, step, easing, viol);
                        }
                    }

                    // Scenario 2: free air. No collision change -> the swing must follow instantly (no lag).
                    {
                        var st = Fresh();
                        for (int i = 0; i < frames; i++)
                        {
                            float desired = Mathf.Lerp(0f, 100f, i / (float)(frames - 1));
                            bool easing = StepSim(ref st, desired, 0, Vector3.zero, rate, dt, out float applied, out bool nanHit);
                            if (nanHit) { nan++; break; }
                            float lag = Mathf.Abs(Mathf.DeltaAngle(applied, desired));
                            if (lag > freeAirLag) freeAirLag = lag;
                            Row(w, sb, "freeair", i, 0, desired, applied, 0f, easing, false);
                        }
                    }

                    // Scenario 3: teleport mid-ease. The target jump past the chain-length threshold re-seeds.
                    {
                        var st = Fresh();
                        int teleAt = F + 5;
                        for (int i = 0; i < frames; i++)
                        {
                            int collided = i < F ? 0 : 1;
                            float desired = i < F ? 0f : cfg.JumpDeg;
                            Vector3 tgt = i < teleAt ? Vector3.zero : new Vector3(1f, 0f, 0f);
                            bool easing = StepSim(ref st, desired, collided, tgt, rate, dt, out float applied, out bool nanHit);
                            if (nanHit) { nan++; break; }
                            if (i == teleAt) teleportAccepted = Mathf.Abs(Mathf.DeltaAngle(applied, desired)) < k_ConvergeTolDeg;
                            Row(w, sb, "teleport", i, collided, desired, applied, 0f, easing, false);
                        }
                    }
                }

                if (!converged) fails++;
                if (rateViol > 0) fails++;
                if (!teleportAccepted) fails++;
                if (freeAirLag > 0.5f) fails++;

                summary.Ok = true;
                summary.Steps = frames;
                summary.NaNCount = nan;
                summary.MaxEasingStepDeg = maxEasingStep;
                summary.RateLimitViolations = rateViol;
                summary.Converged = converged;
                summary.ConvergeFrames = convergeFrames;
                summary.FreeAirMaxLagDeg = freeAirLag;
                summary.TeleportAccepted = teleportAccepted;
                summary.Failures = fails;
            }
            catch (System.Exception e)
            {
                summary.Ok = false;
                summary.Error = e.Message;
            }

            return summary;
        }

        // Runs one core step for a desired swivel angle; returns whether it eased and the applied swivel.
        static bool StepSim(ref BasisSwingContinuityState st, float desiredDeg, int collided, Vector3 targetPos,
            float rate, float dt, out float appliedDeg, out bool nanHit)
        {
            Vector3 a = Vector3.zero;
            Vector3 c = new Vector3(0f, 0f, k_L);
            Vector3 b = BFromSwivel(desiredDeg);

            BasisSwingContinuityCore.Step(st, a, b, c, targetPos, collided, rate, dt, out BasisSwingContinuityResult r);
            nanHit = false;
            if (!r.Valid)
            {
                appliedDeg = desiredDeg;
                return false;
            }
            st = r.State;
            if (r.ApplySwing)
            {
                if (!Finite(r.NewDir)) { nanHit = true; appliedDeg = desiredDeg; return false; }
                appliedDeg = Mathf.Atan2(r.NewDir.y, r.NewDir.x) * Mathf.Rad2Deg;
                return true;
            }
            appliedDeg = desiredDeg; // free-air / reseed / within-step: accepted as-is
            return false;
        }

        static Vector3 BFromSwivel(float deg)
        {
            float r = deg * Mathf.Deg2Rad;
            return new Vector3(k_R * Mathf.Cos(r), k_R * Mathf.Sin(r), k_MidZ);
        }

        static BasisSwingContinuityState Fresh()
        {
            return new BasisSwingContinuityState { LastDir = Vector3.zero, LastAxis = Vector3.zero, LastTarget = Vector3.zero, SmoothState = 0, Seeded = false };
        }

        static void Row(StreamWriter w, StringBuilder sb, string scn, int frame, int collided, float desired, float applied, float step, bool easing, bool fail)
        {
            sb.Clear();
            sb.Append(scn).Append(',').Append(frame).Append(',').Append(collided).Append(',');
            sb.Append(F2(desired)).Append(',').Append(F2(applied)).Append(',').Append(F2(step)).Append(',');
            sb.Append(easing ? '1' : '0').Append(',').Append(fail ? '1' : '0');
            w.WriteLine(sb.ToString());
        }

        static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        static bool Finite(Vector3 v) => Finite(v.x) && Finite(v.y) && Finite(v.z);

        static string F2(float v)
        {
            if (float.IsNaN(v)) return "nan";
            return v.ToString("0.######", CultureInfo.InvariantCulture);
        }
    }
}
