using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using Basis.IK;

namespace Basis.IK.Debugging
{
    // Offline proof that knee-swivel smoothing (BasisFullIKConstraintJob.SmoothKneeSwivel +
    // BasisSwivelFilterCore) fixes "the legs twist when I stand still and straight" without lagging a real
    // turn. Drives the real leg solver (BasisLegSolveCore) at standing extensions with a hips-yaw-jittering
    // bend frame -- the bend normal AND the knee hint yaw together, as both derive from the hips when
    // standing -- and measures the raw knee-swivel (leg roll about the hip->foot axis) excursion, then the
    // same One-Euro pass the live job runs. Lock-step with the live fix via the shared solve + filter cores.
    // BasisIKTestGates.GateLegTwist scores the summary.
    public struct BasisLegTwistSweepConfig
    {
        public float Upper, Lower;   // thigh / shin
        public float HintForward;    // knee hint placed forward of the knee
        public float JitterAmpDeg;   // standing hips-yaw jitter amplitude (zero-mean, high freq)
        public float TurnRateDeg;    // steady yaw rate for the real-turn tracking check
        public float Dt;             // frame time
        public int Steps;            // jitter samples per extension
        public int TurnSteps;        // turn samples
        public float[] Extensions;   // standing extension ratios to test

        public static BasisLegTwistSweepConfig Default()
        {
            return new BasisLegTwistSweepConfig
            {
                Upper = 0.45f,
                Lower = 0.45f,
                HintForward = 0.30f,
                JitterAmpDeg = 4f,
                TurnRateDeg = 60f,
                Dt = 1f / 90f,
                Steps = 270,
                TurnSteps = 180,
                Extensions = new[] { 0.90f, 0.94f, 0.96f, 0.97f, 0.98f, 0.99f, 0.995f },
            };
        }
    }

    public struct BasisLegTwistSweepSummary
    {
        public bool Ok;
        public string Path;
        public int Rows;
        public string Error;

        public float WorstRawP2PDeg;       // worst raw knee-swivel excursion across extensions (the bug magnitude)
        public float WorstSmoothedP2PDeg;  // smoothed excursion at that extension (the fix)
        public float WorstExt;
        public float WorstReductionFrac;   // smoothed/raw at the worst extension

        public float TurnRawChangeDeg;     // knee-swivel change over a real turn (raw)
        public float TurnSmoothChangeDeg;  // ... smoothed (must still track)
        public float TurnMaxLagDeg;        // worst steady lag during the turn
    }

    public static class BasisLegTwistSweep
    {
        public const string DefaultFileName = "BasisLegTwistSweep.csv";
        public static string DefaultPath() => Path.Combine(Application.persistentDataPath, DefaultFileName);

        public static BasisLegTwistSweepSummary Run(BasisLegTwistSweepConfig cfg, string path)
        {
            var s = new BasisLegTwistSweepSummary { Ok = false, Path = path };
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                float worstRaw = 0f, worstSmoothed = 0f, worstExt = 0f;
                int rows = 0;
                using (var w = new StreamWriter(path, false, Encoding.UTF8))
                {
                    w.WriteLine("# BasisLegTwistSweep " + System.DateTime.UtcNow.ToString("o"));
                    w.WriteLine(string.Format(CultureInfo.InvariantCulture, "# standing hips-yaw jitter +/-{0:0.0}deg, foot planted", cfg.JitterAmpDeg));
                    w.WriteLine("ext,rawP2P_deg,smoothedP2P_deg,ratio");
                    var exts = cfg.Extensions ?? BasisLegTwistSweepConfig.Default().Extensions;
                    foreach (float ext in exts)
                    {
                        Drive(cfg, ext, t => Jitter(cfg.JitterAmpDeg, t), cfg.Steps, out float rawP2P, out float smoothP2P, out _, out _, out _);
                        w.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0:0.000},{1:0.000},{2:0.000},{3:0.000}",
                            ext, rawP2P, smoothP2P, smoothP2P / Mathf.Max(rawP2P, 1e-3f)));
                        rows++;
                        if (rawP2P > worstRaw) { worstRaw = rawP2P; worstSmoothed = smoothP2P; worstExt = ext; }
                    }

                    Drive(cfg, 0.97f, t => cfg.TurnRateDeg * t, cfg.TurnSteps, out _, out _, out float turnRaw, out float turnSmooth, out float turnLag);
                    w.WriteLine(string.Format(CultureInfo.InvariantCulture, "# real turn @ {0:0}deg/s, ext 0.97", cfg.TurnRateDeg));
                    w.WriteLine("turn_raw_change_deg,turn_smooth_change_deg,turn_max_lag_deg");
                    w.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0:0.000},{1:0.000},{2:0.000}", turnRaw, turnSmooth, turnLag));
                    rows++;

                    s.TurnRawChangeDeg = turnRaw;
                    s.TurnSmoothChangeDeg = turnSmooth;
                    s.TurnMaxLagDeg = turnLag;
                }

                s.Rows = rows;
                s.WorstRawP2PDeg = worstRaw;
                s.WorstSmoothedP2PDeg = worstSmoothed;
                s.WorstExt = worstExt;
                s.WorstReductionFrac = worstSmoothed / Mathf.Max(worstRaw, 1e-3f);
                s.Ok = true;
            }
            catch (System.Exception e)
            {
                s.Ok = false;
                s.Error = e.Message;
            }
            return s;
        }

        static float Jitter(float amp, float t) => amp * (0.6f * Mathf.Sin(2f * Mathf.PI * 5f * t) + 0.4f * Mathf.Sin(2f * Mathf.PI * 11f * t + 1.3f));

        // Solve the standing leg over a yawing bend frame, run the live One-Euro on the output knee swivel,
        // and return raw/smoothed peak-to-peak swivel plus the start->end change and worst steady lag.
        static void Drive(BasisLegTwistSweepConfig cfg, float ratio, System.Func<float, float> yawDeg, int steps,
            out float rawP2P, out float smoothP2P, out float rawChange, out float smoothChange, out float maxLag)
        {
            float maxReach = cfg.Upper + cfg.Lower;
            Vector3 foot = Vector3.zero;
            Vector3 hip = new Vector3(0f, ratio * maxReach, 0f);
            Vector3 knee = RestKnee(hip, foot, cfg.Upper, cfg.Lower);

            float rawMin = float.PositiveInfinity, rawMax = float.NegativeInfinity;
            float smMin = float.PositiveInfinity, smMax = float.NegativeInfinity;
            float raw0 = 0f, rawN = 0f, sm0 = 0f, smN = 0f;
            maxLag = 0f;
            BasisSwivelFilterState st = default;
            bool seeded = false;
            int skip = Mathf.Min(45, steps / 4);

            for (int i = 0; i < steps; i++)
            {
                float t = i * cfg.Dt;
                Quaternion yaw = Quaternion.AngleAxis(yawDeg(t), Vector3.up);

                BasisLegSolveInput li = default;
                li.Root = hip;
                li.Mid = knee;
                li.Tip = foot;
                li.RootRotation = Quaternion.identity;
                li.MidRotation = Quaternion.identity;
                li.TargetPosition = foot;
                li.TargetRotation = Quaternion.identity;
                li.TargetOffset = Quaternion.identity;
                li.HintPosition = knee + (yaw * Vector3.forward) * cfg.HintForward;
                li.HintWeight = 1f;
                li.BendNormal = yaw * Vector3.right;

                BasisLegSolveCore.Solve(li, out BasisLegSolveResult r);
                float swivel = ComputeSwivel(hip, r.KneeSolved, foot);
                if (!seeded) { st = BasisSwivelFilterCore.Seed(swivel); seeded = true; }
                else st = BasisSwivelFilterCore.Step(st, swivel, cfg.Dt);

                if (i == skip) { raw0 = swivel; sm0 = st.Smooth; }
                rawN = swivel; smN = st.Smooth;
                if (i >= skip)
                {
                    rawMin = Mathf.Min(rawMin, swivel); rawMax = Mathf.Max(rawMax, swivel);
                    smMin = Mathf.Min(smMin, st.Smooth); smMax = Mathf.Max(smMax, st.Smooth);
                    if (t > 0.5f) maxLag = Mathf.Max(maxLag, Mathf.Abs(swivel - st.Smooth));
                }
            }
            rawP2P = (rawMax >= rawMin) ? rawMax - rawMin : 0f;
            smoothP2P = (smMax >= smMin) ? smMax - smMin : 0f;
            rawChange = rawN - raw0;
            smoothChange = smN - sm0;
        }

        // Knee swivel about the hip->foot axis, referenced off forward -- identical to the live SmoothKneeSwivel.
        static float ComputeSwivel(Vector3 hip, Vector3 knee, Vector3 foot)
        {
            Vector3 ac = foot - hip;
            if (ac.sqrMagnitude < 1e-8f) return 0f;
            Vector3 axis = ac.normalized;
            Vector3 refDir = Vector3.forward - axis * Vector3.Dot(Vector3.forward, axis);
            if (refDir.sqrMagnitude < 1e-8f) refDir = Vector3.right - axis * Vector3.Dot(Vector3.right, axis);
            Vector3 pole = (knee - hip);
            pole -= axis * Vector3.Dot(pole, axis);
            if (refDir.sqrMagnitude < 1e-8f || pole.sqrMagnitude < 1e-8f) return 0f;
            return Vector3.SignedAngle(refDir.normalized, pole, axis);
        }

        static Vector3 RestKnee(Vector3 hip, Vector3 foot, float upper, float lower)
        {
            float maxReach = upper + lower;
            Vector3 chord = foot - hip;
            float d = Mathf.Min(chord.magnitude, maxReach * 0.999f);
            Vector3 along = chord.normalized;
            float proj = (upper * upper + d * d - lower * lower) / (2f * d);
            float h = Mathf.Sqrt(Mathf.Max(0f, upper * upper - proj * proj));
            Vector3 perp = Vector3.Cross(along, Vector3.right).normalized;
            if (Vector3.Dot(perp, Vector3.forward) < 0f) perp = -perp;
            return hip + along * proj + perp * h;
        }
    }
}
