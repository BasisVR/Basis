using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using Basis.IK;

namespace Basis.IK.Debugging
{
    // Sweeps the head gaze pitch through its range and records the cervical-lordosis response
    // (neck/upper-chest bend, extreme-region hips/chest offsets, head-pitch clamp). Same math
    // the live ApplyCervicalLordosis runs. Reveals where the head bend goes funky vs pitch.
    public struct BasisHeadSweepConfig
    {
        public float BaseDeg, NeckShare, MaxHeadPitchDeg, ExtremeStartDeg, ExtremeFullDeg, PitchGainDeg;
        public float ExtremeRollForwardMaxDeg, ExtremeRollBackwardMaxDeg;
        public float ExtremeHipsHorizontalMax, ExtremeChestHorizontalMax;
        public float ExtremeHipsDownMax, ExtremeChestDownMax, ExtremeHipsDownLookUp, ExtremeChestDownLookUp;
        public bool HasUpperChest;
        public float Yaw;
        public float PitchMin, PitchMax;
        public int PitchSteps;

        public static BasisHeadSweepConfig Default()
        {
            return new BasisHeadSweepConfig
            {
                BaseDeg = 5f,
                NeckShare = 0.65f,
                MaxHeadPitchDeg = 80f,
                ExtremeStartDeg = 50f,
                ExtremeFullDeg = 80f,
                PitchGainDeg = 8f,
                ExtremeRollForwardMaxDeg = 10f,
                ExtremeRollBackwardMaxDeg = 4f,
                ExtremeHipsHorizontalMax = 0.025f,
                ExtremeChestHorizontalMax = 0.04f,
                ExtremeHipsDownMax = 0.015f,
                ExtremeChestDownMax = 0.025f,
                ExtremeHipsDownLookUp = 0.0005f,
                ExtremeChestDownLookUp = 0.001f,
                HasUpperChest = true,
                Yaw = 0f,
                PitchMin = -110f,
                PitchMax = 110f,
                PitchSteps = 221,
            };
        }
    }

    public struct BasisHeadSweepSummary
    {
        public bool Ok;
        public string Path;
        public int Rows;
        public float MaxNeckDeg;
        public float ClampOnsetPitch;   // first |pitch| where the head clamp engages (nan if never)
        public float ExtremeOnsetPitch; // first |pitch| where extremeFrac > 0
        public string Error;
    }

    public static class BasisHeadSweep
    {
        public const string DefaultFileName = "BasisHeadSweep.csv";

        public static string DefaultPath()
        {
            return System.IO.Path.Combine(Application.persistentDataPath, DefaultFileName);
        }

        public static BasisHeadSweepSummary Run(BasisHeadSweepConfig cfg, string path)
        {
            var s = new BasisHeadSweepSummary { Ok = false, Path = path };
            int steps = Mathf.Max(2, cfg.PitchSteps);
            float maxNeck = 0f;
            float clampOnset = float.NaN, extremeOnset = float.NaN;

            try
            {
                string dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                using (var w = new StreamWriter(path, false, Encoding.UTF8))
                {
                    w.WriteLine("# BasisHeadSweep " + System.DateTime.UtcNow.ToString("o") +
                                " hasUpperChest=" + cfg.HasUpperChest + " yaw=" + F(cfg.Yaw));
                    w.WriteLine("pitch_cmd_deg,head_pitch_input_deg,head_pitch_clamped_deg,clamped," +
                                "signed_pitch,extreme_frac,lordosis_deg,neck_deg,upperchest_deg,bend_deg,extreme_roll_deg," +
                                "hips_fwd_m,hips_down_m,chest_fwd_m,chest_down_m,early_out");

                    var sb = new StringBuilder(192);
                    for (int p = 0; p < steps; p++)
                    {
                        float pitch = Mathf.Lerp(cfg.PitchMin, cfg.PitchMax, p / (float)(steps - 1));
                        Quaternion headRot = Quaternion.AngleAxis(cfg.Yaw, Vector3.up) * Quaternion.AngleAxis(pitch, Vector3.right);

                        BasisCervicalInput input;
                        input.BaseDeg = cfg.BaseDeg;
                        input.NeckShare = cfg.NeckShare;
                        input.MaxHeadPitchDeg = cfg.MaxHeadPitchDeg;
                        input.ExtremeStartDeg = cfg.ExtremeStartDeg;
                        input.ExtremeFullDeg = cfg.ExtremeFullDeg;
                        input.ExtremeRollForwardMaxDeg = cfg.ExtremeRollForwardMaxDeg;
                        input.ExtremeRollBackwardMaxDeg = cfg.ExtremeRollBackwardMaxDeg;
                        input.ExtremeHipsHorizontalMax = cfg.ExtremeHipsHorizontalMax;
                        input.ExtremeChestHorizontalMax = cfg.ExtremeChestHorizontalMax;
                        input.ExtremeHipsDownMax = cfg.ExtremeHipsDownMax;
                        input.ExtremeChestDownMax = cfg.ExtremeChestDownMax;
                        input.ExtremeHipsDownLookUp = cfg.ExtremeHipsDownLookUp;
                        input.ExtremeChestDownLookUp = cfg.ExtremeChestDownLookUp;
                        input.PitchGainDeg = Mathf.Max(0f, cfg.PitchGainDeg);
                        input.ReferenceUp = Vector3.up;
                        input.HeadTargetRot = headRot;
                        input.HasUpperChest = cfg.HasUpperChest;

                        BasisCervicalSolveCore.Solve(input, out BasisCervicalResult r);

                        bool clamped = r.HeadPitchInputDeg != r.HeadPitchClampedDeg;
                        if (clamped && float.IsNaN(clampOnset)) clampOnset = Mathf.Abs(pitch);
                        if (r.ExtremeFrac > 0f && float.IsNaN(extremeOnset)) extremeOnset = Mathf.Abs(pitch);
                        if (Mathf.Abs(r.NeckDeg) > maxNeck) maxNeck = Mathf.Abs(r.NeckDeg);

                        sb.Clear();
                        Append(sb, pitch);
                        Append(sb, r.HeadPitchInputDeg); Append(sb, r.HeadPitchClampedDeg);
                        sb.Append(clamped ? '1' : '0').Append(',');
                        Append(sb, r.SignedPitch); Append(sb, r.ExtremeFrac);
                        Append(sb, r.LordosisDeg); Append(sb, r.NeckDeg); Append(sb, r.UpperChestLordosisDeg);
                        Append(sb, r.BhDeg); Append(sb, r.ExtremeRollDeg);
                        Append(sb, r.HipsForwardAmount); Append(sb, r.HipsDownAmount);
                        Append(sb, r.ChestForwardAmount); Append(sb, r.ChestDownAmount);
                        sb.Append(r.EarlyOut ? '1' : '0');
                        w.WriteLine(sb.ToString());
                    }
                }

                s.Ok = true;
                s.Rows = steps;
                s.MaxNeckDeg = maxNeck;
                s.ClampOnsetPitch = clampOnset;
                s.ExtremeOnsetPitch = extremeOnset;
            }
            catch (System.Exception e)
            {
                s.Ok = false;
                s.Error = e.Message;
            }

            return s;
        }

        public struct BasisHeadTrajectorySummary
        {
            public bool Ok;
            public string Path;
            public BasisTrajectoryResult[] Results;
            public float WorstPopDeg;
            public float WorstRoughDeg;
            public string Error;
        }

        // Per-frame trajectory scan of the cervical solve along a continuous head-pitch nod. Measures
        // whether the neck / upper-chest bend (which roots the arm chain) jitters as pitch moves -- a
        // jittery head bend propagates down the spine into the shoulder and reads as arm jitter. Pops =
        // discontinuities crossed on a smooth nod; rough/zigzag = sensitivity to head-tracking pitch
        // noise. noiseDeg is in DEGREES of head pitch (not metres).
        public static BasisHeadTrajectorySummary RunTrajectories(BasisHeadSweepConfig cfg, float noiseDeg, string path)
        {
            var summary = new BasisHeadTrajectorySummary { Ok = false, Path = path };

            BasisCervicalResult SolveAt(float pitch)
            {
                Quaternion headRot = Quaternion.AngleAxis(cfg.Yaw, Vector3.up) * Quaternion.AngleAxis(pitch, Vector3.right);
                BasisCervicalInput input;
                input.BaseDeg = cfg.BaseDeg;
                input.NeckShare = cfg.NeckShare;
                input.MaxHeadPitchDeg = cfg.MaxHeadPitchDeg;
                input.ExtremeStartDeg = cfg.ExtremeStartDeg;
                input.ExtremeFullDeg = cfg.ExtremeFullDeg;
                input.ExtremeRollForwardMaxDeg = cfg.ExtremeRollForwardMaxDeg;
                input.ExtremeRollBackwardMaxDeg = cfg.ExtremeRollBackwardMaxDeg;
                input.ExtremeHipsHorizontalMax = cfg.ExtremeHipsHorizontalMax;
                input.ExtremeChestHorizontalMax = cfg.ExtremeChestHorizontalMax;
                input.ExtremeHipsDownMax = cfg.ExtremeHipsDownMax;
                input.ExtremeChestDownMax = cfg.ExtremeChestDownMax;
                input.ExtremeHipsDownLookUp = cfg.ExtremeHipsDownLookUp;
                input.ExtremeChestDownLookUp = cfg.ExtremeChestDownLookUp;
                input.PitchGainDeg = Mathf.Max(0f, cfg.PitchGainDeg);
                input.ReferenceUp = Vector3.up;
                input.HeadTargetRot = headRot;
                input.HasUpperChest = cfg.HasUpperChest;
                BasisCervicalSolveCore.Solve(input, out BasisCervicalResult r);
                return r;
            }

            try
            {
                string dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                // Continuous nod from a look-down to an extreme look-up: covers the extreme-region onset
                // and the head-pitch clamp, where any kink would live.
                Vector3[] nod = BasisIKTrajectoryScan.Line(new Vector3(-60f, 0f, 0f), new Vector3(90f, 0f, 0f), 200);

                var results = new BasisTrajectoryResult[3];
                results[0] = BasisIKTrajectoryScan.Scan("neck-deg", nod, v => SolveAt(v.x).NeckDeg, noiseDeg, 31);
                results[1] = BasisIKTrajectoryScan.Scan("upperchest-deg", nod, v => SolveAt(v.x).UpperChestLordosisDeg, noiseDeg, 32);
                results[2] = BasisIKTrajectoryScan.Scan("extreme-roll-deg", nod, v => SolveAt(v.x).ExtremeRollDeg, noiseDeg, 33);

                using (var w = new StreamWriter(path, false, Encoding.UTF8))
                {
                    w.WriteLine("# BasisHeadTrajectory " + System.DateTime.UtcNow.ToString("o") +
                                " yaw=" + F(cfg.Yaw) + " noiseDeg=" + F(noiseDeg) + " hasUpperChest=" + cfg.HasUpperChest);
                    w.WriteLine("step,pitch_deg,neck_deg,upperchest_deg,lordosis_deg,extreme_roll_deg,extreme_frac,chest_fwd_m,chest_down_m");
                    var sb = new StringBuilder(160);
                    for (int s = 0; s < nod.Length; s++)
                    {
                        float pitch = nod[s].x;
                        BasisCervicalResult r = SolveAt(pitch);
                        sb.Clear();
                        sb.Append(s).Append(',');
                        Append(sb, pitch);
                        Append(sb, r.NeckDeg); Append(sb, r.UpperChestLordosisDeg); Append(sb, r.LordosisDeg);
                        Append(sb, r.ExtremeRollDeg); Append(sb, r.ExtremeFrac);
                        Append(sb, r.ChestForwardAmount); sb.Append(F(r.ChestDownAmount));
                        w.WriteLine(sb.ToString());
                    }
                }

                float worstPop = 0f, worstRough = 0f;
                for (int i = 0; i < results.Length; i++)
                {
                    if (results[i].CleanMaxJumpDeg > worstPop) worstPop = results[i].CleanMaxJumpDeg;
                    if (results[i].NoisyRoughDeg > worstRough) worstRough = results[i].NoisyRoughDeg;
                }
                summary.Ok = true;
                summary.Results = results;
                summary.WorstPopDeg = worstPop;
                summary.WorstRoughDeg = worstRough;
            }
            catch (System.Exception e)
            {
                summary.Ok = false;
                summary.Error = e.Message;
            }
            return summary;
        }

        static void Append(StringBuilder sb, float v) { sb.Append(F(v)).Append(','); }

        static string F(float v)
        {
            if (float.IsNaN(v)) return "nan";
            return v.ToString("0.######", CultureInfo.InvariantCulture);
        }
    }
}
