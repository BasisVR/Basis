using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using Basis.IK;

namespace Basis.IK.Debugging
{
    // Elbow-trailing guard for the scapulohumeral coupling reduction. With no shoulder tracker the girdle solve
    // swings the upper-arm ROOT by a coupled fraction of the hand swing; the elbow rides that root, so an
    // over-large coupling reads as a floaty / trailing elbow (the regression). This sweeps a trackerless hand
    // raise from rest to overhead and reads AppliedAngleDeg -- exactly how far the shoulder root (and the elbow
    // on it) swings from rest -- at the SHIPPED coupling vs the previous one. It asserts the shipped coupling
    // stays bounded, engages smoothly/monotonically, and swings the root meaningfully LESS than before (the fix).

    public struct BasisShoulderCoupleSweepConfig
    {
        public int SwingSteps;
        public float Reach;            // hand distance in arm-lengths (near full extension = worst-case trail)
        public float ArmLength;
        public float ElevationFactor;
        public float ProtractionFactor;
        public bool IsLeft;
        public float ShippedCouple;    // runtime k_ShoulderCoupleRatio
        public float ShippedMaxDeg;    // runtime k_ShoulderMaxDeg
        public float LegacyCouple;     // the pre-fix value the regression was reported at
        public float LegacyMaxDeg;

        public static BasisShoulderCoupleSweepConfig Default() => new BasisShoulderCoupleSweepConfig
        {
            SwingSteps = 56,
            Reach = 0.95f,
            ArmLength = 0.3f,
            ElevationFactor = 1f,
            ProtractionFactor = 1f,
            IsLeft = false,
            ShippedCouple = 0.4f,
            ShippedMaxDeg = 25f,
            LegacyCouple = 0.8f,
            LegacyMaxDeg = 40f,
        };
    }

    public struct BasisShoulderCoupleSweepSummary
    {
        public bool Ok;
        public string Path;
        public string Error;
        public int Cases;
        public int EngagedCases;
        public int NanCount;
        public float MaxAppliedShippedDeg;   // peak girdle swing the elbow rides, shipped coupling (bounded = not floaty)
        public float MaxAppliedLegacyDeg;     // ...legacy coupling, for the reduction comparison
        public float MinReductionFrac;        // min over engaged swing of (1 - shipped/legacy); >0 proves the fix reduces trail
        public int MonotonicViolations;       // shipped girdle swing must grow smoothly as the hand raises (no pop)
    }

    public static class BasisShoulderCoupleSweep
    {
        public const string DefaultFileName = "BasisShoulderCoupleSweep.csv";
        public static string DefaultPath() => Path.Combine(Application.persistentDataPath, DefaultFileName);

        public static BasisShoulderCoupleSweepSummary Run(BasisShoulderCoupleSweepConfig cfg, string path)
        {
            var s = new BasisShoulderCoupleSweepSummary { Ok = false, Path = path };
            int steps = Mathf.Max(4, cfg.SwingSteps);
            Vector3 shoulder = Vector3.zero;
            Quaternion chest = Quaternion.identity;
            float prevShipped = float.NaN;

            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                using (var w = new StreamWriter(path, false, Encoding.UTF8))
                {
                    w.WriteLine("# BasisShoulderCoupleSweep " + System.DateTime.UtcNow.ToString("o")
                        + " shipped=" + F(cfg.ShippedCouple) + "/" + F(cfg.ShippedMaxDeg)
                        + " legacy=" + F(cfg.LegacyCouple) + "/" + F(cfg.LegacyMaxDeg));
                    w.WriteLine("elevationDeg,appliedShipped,appliedLegacy,reductionFrac");
                    var sb = new StringBuilder(96);

                    for (int i = 0; i < steps; i++)
                    {
                        // Raise the (trackerless) hand from just below the bind plane up to overhead.
                        float elevDeg = Mathf.Lerp(-10f, 100f, i / (float)(steps - 1));
                        Vector3 handDir = DirFromAzEl(0f, elevDeg, cfg.IsLeft);
                        Vector3 hand = shoulder + (chest * handDir) * (cfg.Reach * cfg.ArmLength);

                        float appliedShipped = AppliedAngle(cfg, shoulder, hand, chest, cfg.ShippedCouple, cfg.ShippedMaxDeg);
                        float appliedLegacy = AppliedAngle(cfg, shoulder, hand, chest, cfg.LegacyCouple, cfg.LegacyMaxDeg);

                        if (IsBad(appliedShipped) || IsBad(appliedLegacy)) s.NanCount++;
                        if (appliedShipped > s.MaxAppliedShippedDeg) s.MaxAppliedShippedDeg = appliedShipped;
                        if (appliedLegacy > s.MaxAppliedLegacyDeg) s.MaxAppliedLegacyDeg = appliedLegacy;

                        float reductionFrac = 0f;
                        if (appliedLegacy > 3f) // engaged: girdle is doing real work, comparison is meaningful
                        {
                            reductionFrac = 1f - appliedShipped / appliedLegacy;
                            if (s.EngagedCases == 0 || reductionFrac < s.MinReductionFrac) s.MinReductionFrac = reductionFrac;
                            s.EngagedCases++;
                        }

                        if (!float.IsNaN(prevShipped) && appliedShipped < prevShipped - 0.25f) s.MonotonicViolations++;
                        prevShipped = appliedShipped;
                        s.Cases++;

                        sb.Clear();
                        sb.Append(F(elevDeg)).Append(',').Append(F(appliedShipped)).Append(',').Append(F(appliedLegacy)).Append(',').Append(F(reductionFrac));
                        w.WriteLine(sb.ToString());
                    }
                }
                s.Ok = true;
            }
            catch (System.Exception e)
            {
                s.Ok = false;
                s.Error = e.Message;
            }
            return s;
        }

        static float AppliedAngle(BasisShoulderCoupleSweepConfig cfg, Vector3 shoulder, Vector3 hand, Quaternion chest, float couple, float maxDeg)
        {
            BasisShoulderSolveInput input = default;
            input.ShoulderPos = shoulder;
            input.HandTargetPos = hand;
            input.ElbowPos = shoulder;
            input.HasElbow = false;
            input.HasShoulderTracker = false;
            input.ChestRot = chest;
            input.TposeChestRot = Quaternion.identity;
            input.TposeShoulderRot = Quaternion.identity;
            input.TposeArmDirWorld = RestDir(cfg.IsLeft);
            input.TposeArmLength = cfg.ArmLength;
            input.ElevationFactor = cfg.ElevationFactor;
            input.ProtractionFactor = cfg.ProtractionFactor;
            input.CoupleRatio = couple;
            input.MaxShoulderDeg = maxDeg;
            input.TrackerFinal = Quaternion.identity;
            input.IsLeft = cfg.IsLeft;
            BasisShoulderSolveCore.Solve(input, out BasisShoulderSolveResult r);
            return r.Apply ? r.AppliedAngleDeg : 0f;
        }

        static Vector3 RestDir(bool isLeft) => new Vector3(isLeft ? -1f : 1f, -0.05f, 0.05f).normalized;

        static Vector3 DirFromAzEl(float azDeg, float elDeg, bool isLeft)
        {
            float er = elDeg * Mathf.Deg2Rad, ar = azDeg * Mathf.Deg2Rad;
            float ch = Mathf.Cos(er);
            float x = Mathf.Cos(ar) * ch;
            float y = Mathf.Sin(er);
            float z = Mathf.Sin(ar) * ch;
            if (isLeft) x = -x;
            return new Vector3(x, y, z).normalized;
        }

        static bool IsBad(float v) => float.IsNaN(v) || float.IsInfinity(v);

        static string F(float v) => float.IsNaN(v) ? "nan" : v.ToString("0.######", CultureInfo.InvariantCulture);
    }
}
