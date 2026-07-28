using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using Basis.IK;

namespace Basis.IK.Debugging
{
    // Validates the tracker-driven knee bend normal (BasisTrackerBendNormalCore) against the fixed hips-right
    // KneeBendPref it replaces. The flip the forward-pole offset was patching comes from a leg whose shin is
    // YAWED away from the body sagittal plane (foot turned out, seated leg) while the bend plane stays glued to
    // hips-right: the knee then bends in the wrong plane and, as the pole degenerates near extension, snaps.
    // The fix rides the bend normal on the lower-leg tracker so the plane follows the shin. Pure math.
    //
    // Two claims, the first two deterministic (the core's contract), the third the solver payoff:
    //   parity   : at the calibration pose (yaw 0) the tracker normal must equal the fixed normal -- a no-op.
    //   tracking : as the shin yaws, the tracker normal stays on the shin's medial axis; the fixed one drifts
    //              off it by the yaw angle (this is what made the knee bend in the wrong plane).
    //   plane    : with the pole degenerate (the snap case) the tracker normal keeps the solved knee in the
    //              leg's own forward plane far better than the fixed normal -- reported for insight.
    public struct BasisBendNormalSweepConfig
    {
        public float UpperLength;
        public float LowerLength;
        public int YawSteps;        // shin-yaw samples, symmetric about 0 (odd -> samples the calibration pose exactly)
        public float MaxYawDeg;     // largest shin yaw away from the body sagittal plane
        public int ExtSteps;        // leg-extension samples for the degenerate-pole plane-error report

        public static BasisBendNormalSweepConfig Default()
        {
            return new BasisBendNormalSweepConfig
            {
                UpperLength = 0.42f,
                LowerLength = 0.42f,
                YawSteps = 25,
                MaxYawDeg = 60f,
                ExtSteps = 16,
            };
        }
    }

    public struct BasisBendNormalSweepSummary
    {
        public bool Ok;
        public string Path;
        public string Error;
        public int Steps;

        public float ParityMaxDeg;             // tracker vs fixed normal at the calibration pose (yaw 0); must be ~0 (a no-op there)
        public float WorstTrackingErrNewDeg;   // worst angle: tracker normal off the shin's medial axis across yaw; ~0 (it rides the shin)
        public float WorstTrackingErrOldDeg;   // worst angle: fixed hips-right normal off the shin's medial axis; ~MaxYaw (it ignores the shin)
        public float WorstNormalNewPlaneErrDeg;// degenerate pole: worst tracker-normal knee plane error vs the leg's forward (reported)
        public float WorstNormalOldPlaneErrDeg;// degenerate pole: worst fixed-normal knee plane error vs the leg's forward (reported)
        public int PlaneSamples;
    }

    public static class BasisBendNormalSweep
    {
        public const string DefaultFileName = "BasisBendNormalSweep.csv";
        public static string DefaultPath() => System.IO.Path.Combine(Application.persistentDataPath, DefaultFileName);

        const float k_StraightFlexDeg = 170f; // a straighter knee has no defined bend side
        const float k_BentFlexDeg = 10f;      // a fully folded knee is degenerate too

        public static BasisBendNormalSweepSummary Run(BasisBendNormalSweepConfig cfg, string path)
        {
            var summary = new BasisBendNormalSweepSummary { Ok = false, Path = path };
            float upper = Mathf.Max(1e-4f, cfg.UpperLength);
            float lower = Mathf.Max(1e-4f, cfg.LowerLength);
            float legLen = upper + lower;
            Vector3 hip = Vector3.zero;

            // Calibration: the tracker faces forward (identity) and the known-good world normal is hips-right.
            // Capture stores it in the tracker frame so Resolve can rebuild it from any later tracker rotation.
            Vector3 worldNormalCalib = Vector3.right;
            Vector3 localAxis = BasisTrackerBendNormalCore.CaptureLocalAxis(Quaternion.identity, worldNormalCalib);

            int yawN = Mathf.Max(3, cfg.YawSteps);
            int extN = Mathf.Max(2, cfg.ExtSteps);
            int steps = 0, planeSamples = 0;
            float parity = 0f, trackNew = 0f, trackOld = 0f, planeNew = 0f, planeOld = 0f;

            try
            {
                string dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                using (var w = new StreamWriter(path, false, Encoding.UTF8))
                {
                    w.WriteLine("# BasisBendNormalSweep " + System.DateTime.UtcNow.ToString("o") +
                                " upper=" + F(upper) + " lower=" + F(lower) + " maxYaw=" + F(cfg.MaxYawDeg));
                    w.WriteLine("yaw_deg,ext,flex_deg,defined,track_old,track_new,plane_err_old,plane_err_new");
                    var sb = new StringBuilder(160);

                    for (int yi = 0; yi < yawN; yi++)
                    {
                        float yaw = Mathf.Lerp(-cfg.MaxYawDeg, cfg.MaxYawDeg, yi / (float)(yawN - 1));
                        bool atCalib = Mathf.Abs(yaw) < 0.5f;
                        Quaternion Q = Quaternion.AngleAxis(yaw, Vector3.up);

                        Vector3 legFwd = Q * Vector3.forward;
                        Vector3 legMedial = Q * Vector3.right; // the shin's true medial-lateral (hinge) axis once yawed
                        Vector3 oldNormal = worldNormalCalib;  // fixed hips-right -- ignores the yaw
                        Vector3 newNormal = BasisTrackerBendNormalCore.ResolveWorldNormal(Q, localAxis, worldNormalCalib);

                        float tOld = Vector3.Angle(oldNormal, legMedial);
                        float tNew = Vector3.Angle(newNormal, legMedial);
                        if (tOld > trackOld) trackOld = tOld;
                        if (tNew > trackNew) trackNew = tNew;
                        if (atCalib) parity = Mathf.Max(parity, Vector3.Angle(oldNormal, newNormal));

                        Vector3 restKnee = hip + (Q * new Vector3(0f, -0.97f, 0.20f)).normalized * upper;
                        Vector3 restFoot = restKnee + (Q * new Vector3(0f, -0.98f, -0.10f)).normalized * lower;
                        Vector3 footDir = (Q * new Vector3(0f, -0.8f, 0.45f)).normalized; // forward+down -> a clearly bent knee

                        for (int ei = 0; ei < extN; ei++)
                        {
                            float ext = Mathf.Lerp(0.45f, 0.9f, ei / (float)(extN - 1));
                            Vector3 target = hip + footDir * (ext * legLen);

                            // No pole (HintWeight 0): the bend normal alone decides the plane -- the degenerate
                            // case the fixed normal handled wrong. Hint is unused but must be a valid value.
                            var normOld = SolveBN(hip, restKnee, restFoot, target, restKnee, 0f, oldNormal);
                            var normNew = SolveBN(hip, restKnee, restFoot, target, restKnee, 0f, newNormal);

                            float flex = AngleDeg(hip - normNew.KneeSolved, normNew.FootSolved - normNew.KneeSolved);
                            float peOld = PlaneErr(hip, normOld.FootSolved, normOld.KneeSolved, legFwd, out bool dOld);
                            float peNew = PlaneErr(hip, normNew.FootSolved, normNew.KneeSolved, legFwd, out bool dNew);

                            bool bent = flex > k_BentFlexDeg && flex < k_StraightFlexDeg;
                            if (bent && dOld && peOld > planeOld) planeOld = peOld;
                            if (bent && dNew && peNew > planeNew) planeNew = peNew;
                            if (bent && (dOld || dNew)) planeSamples++;

                            steps++;
                            sb.Clear();
                            Append(sb, yaw); Append(sb, ext); Append(sb, flex);
                            sb.Append((bent && (dOld || dNew)) ? "1," : "0,");
                            Append(sb, tOld); Append(sb, tNew); Append(sb, peOld);
                            sb.Append(F(peNew));
                            w.WriteLine(sb.ToString());
                        }
                    }
                }

                summary.Ok = true;
                summary.Steps = steps;
                summary.ParityMaxDeg = parity;
                summary.WorstTrackingErrNewDeg = trackNew;
                summary.WorstTrackingErrOldDeg = trackOld;
                summary.WorstNormalNewPlaneErrDeg = planeNew;
                summary.WorstNormalOldPlaneErrDeg = planeOld;
                summary.PlaneSamples = planeSamples;
            }
            catch (System.Exception e)
            {
                summary.Ok = false;
                summary.Error = e.Message;
            }

            return summary;
        }

        static BasisLegSolveResult SolveBN(Vector3 hip, Vector3 knee, Vector3 foot, Vector3 target, Vector3 hint, float hintWeight, Vector3 bendNormal)
        {
            BasisLegSolveInput input = default;
            input.Root = hip;
            input.Mid = knee;
            input.Tip = foot;
            input.RootRotation = Quaternion.identity;
            input.MidRotation = Quaternion.identity;
            input.TargetPosition = target;
            input.TargetRotation = Quaternion.identity;
            input.HintPosition = hint;
            input.HintWeight = hintWeight;
            input.TargetOffset = Quaternion.identity;
            input.BendNormal = bendNormal;
            BasisLegSolveCore.Solve(input, out BasisLegSolveResult r);
            return r;
        }

        // Angle of the solved knee pole off the leg's forward direction, both projected onto the plane
        // perpendicular to the hip->foot axis. 0 = the knee sits forward in the leg's own plane.
        static float PlaneErr(Vector3 hip, Vector3 foot, Vector3 knee, Vector3 fwdRef, out bool defined)
        {
            defined = false;
            Vector3 axis = foot - hip;
            if (axis.sqrMagnitude < 1e-8f) return 0f;
            axis.Normalize();
            Vector3 refv = Vector3.ProjectOnPlane(fwdRef, axis);
            Vector3 pole = Vector3.ProjectOnPlane(knee - hip, axis);
            if (refv.sqrMagnitude < 1e-8f || pole.sqrMagnitude < 1e-8f) return 0f;
            defined = true;
            return Vector3.Angle(refv, pole);
        }

        static float AngleDeg(Vector3 from, Vector3 to)
        {
            float denom = Mathf.Sqrt(from.sqrMagnitude * to.sqrMagnitude);
            if (denom < 1e-5f) return 0f;
            return Mathf.Acos(Mathf.Clamp(Vector3.Dot(from, to) / denom, -1f, 1f)) * Mathf.Rad2Deg;
        }

        static void Append(StringBuilder sb, float v) { sb.Append(F(v)).Append(','); }

        static string F(float v)
        {
            if (float.IsNaN(v)) return "nan";
            return v.ToString("0.######", CultureInfo.InvariantCulture);
        }
    }
}
