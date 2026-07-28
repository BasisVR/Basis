using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using Basis.IK;

namespace Basis.IK.Debugging
{
    // Offline sweep of BasisTwistSolveCore.ShapeReachStep and the graded, orientation-independent spine CCD it
    // drives. Three passes:
    //  (A) decomposition invariants -- twistKeep=1/swingScale=1 reconstructs the rotation exactly; twistKeep=0
    //      removes all rotation about the axis; swingScale=0 removes all rotation perpendicular to it; the
    //      twist blends linearly; no NaN.
    //  (B) a faithful offline replica of SolveSequentialSpineIK on a FORWARD-PITCHED chain, reaching a head
    //      swept laterally across center, run UPRIGHT and LYING DOWN (the whole problem rigidly rotated; the
    //      twist axis = the body's hips-up). It asserts: keep=1 reproduces the corkscrew; the graded shape
    //      (rigid lumbar -> free cervical, stiffer mid-thoracic swing) cuts the peak twist, stiffens the
    //      lumbar specifically, stays continuous across center, and still reaches; and the upright and supine
    //      results MATCH (the body-relative axis is orientation-independent -- works lying down).
    //  (C) negative control: the supine reach relaxed about WORLD up instead of body up leaves the corkscrew
    //      intact -- proof the hips-up axis is the part that makes lying down work.
    public struct BasisSpineTwistSweepConfig
    {
        public int InvariantCases;
        public int LeanSteps;
        public float LumbarKeep;
        public float CervicalKeep;

        public static BasisSpineTwistSweepConfig Default() =>
            new BasisSpineTwistSweepConfig { InvariantCases = 4000, LeanSteps = 161, LumbarKeep = 0.25f, CervicalKeep = 0.9f };
    }

    public struct BasisSpineTwistSummary
    {
        public bool Ok;
        public string Path;
        public string Error;
        public int Cases;
        public int NaNCount;
        // Pass A -- ShapeReachStep invariants
        public float MaxIdentityErrDeg;
        public float MaxResidualTwistDeg;
        public float MaxResidualSwingDeg;
        public float MaxBlendErrDeg;
        public float MaxSwingBlendErrDeg;    // swingScale scales the swing angle linearly (the even-curvature primitive)
        // Pass B -- graded CCD (worst over both orientations unless noted)
        public float ReproMaxTwistDeg;       // keep=1 baseline peak axial twist (the corkscrew must reproduce)
        public float GradedMaxTwistDeg;      // graded peak axial twist
        public float GradedMaxReachErrM;     // graded reach gap (reach preserved)
        public float GradedMaxJumpDeg;       // graded across-center continuity
        public float OrientationSpreadDeg;   // |upright graded twist - supine graded twist| (invariance => ~0)
        public float ReproLumbarTwistDeg;    // lumbar joint twist at full lean, keep=1
        public float GradedLumbarTwistDeg;   // lumbar joint twist at full lean, graded (must drop -- the grading)
        public float GradedCervicalTwistDeg; // cervical joint twist at full lean, graded (kept free)
        // Even curvature: the mid-thoracic SWING (bend) with the stiffening vs without -- stiffening must not
        // increase the mid joint's bend (it redistributes the curve toward the flexible lumbar/cervical).
        public float GradedMidBendDeg;       // mid (chest) joint bend at full lean, thoracic stiffening ON
        public float UniformMidBendDeg;      // mid (chest) joint bend at full lean, stiffening OFF
        // Pass C -- negative control
        public float WorldUpSupineTwistDeg;  // supine peak twist (about body axis) when relaxed about WORLD up
        public float TestedLumbarKeep, TestedCervicalKeep;
        public int Failures;
    }

    public static class BasisSpineTwistSweep
    {
        public const string DefaultFileName = "BasisSpineTwistSweep.csv";
        public static string DefaultPath() => Path.Combine(Application.persistentDataPath, DefaultFileName);

        static readonly Vector3 k_RestDir = new Vector3(0f, 1f, 0.45f).normalized; // ~24 deg forward pitch
        static readonly float[] k_CumLen = { 0.60f, 0.52f, 0.36f, 0.18f, 0f };     // head, neck, chest, spine, hips
        const float k_MaxLeanDeg = 18f;
        const int k_MaxIters = 30;
        const float k_CcdRelax = 0.8f;
        const float k_ThoracicBendStiffen = 0.3f; // mirror of BasisFullIKConstraintJob.k_ThoracicBendStiffen

        public static BasisSpineTwistSummary Run(BasisSpineTwistSweepConfig cfg, string path)
        {
            var s = new BasisSpineTwistSummary
            {
                Ok = false,
                Path = path,
                TestedLumbarKeep = Mathf.Clamp01(cfg.LumbarKeep),
                TestedCervicalKeep = Mathf.Clamp01(cfg.CervicalKeep),
            };
            int invCases = Mathf.Max(1, cfg.InvariantCases);
            int steps = Mathf.Max(3, cfg.LeanSteps);

            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                using (var w = new StreamWriter(path, false, Encoding.UTF8))
                {
                    w.WriteLine("# BasisSpineTwistSweep " + System.DateTime.UtcNow.ToString("o"));
                    w.WriteLine("kind,p0,p1,identity_err,residual_twist,residual_swing,blend_err,twist,reach_err,jump,fail");
                    var sb = new StringBuilder(192);

                    // ---- Pass A: ShapeReachStep decomposition invariants ----
                    for (int i = 0; i < invCases; i++)
                    {
                        var rng = new System.Random(9100 + i);
                        Vector3 axis = RandUnit(rng);
                        Quaternion delta = Quaternion.AngleAxis(Mathf.Lerp(2f, 120f, (float)rng.NextDouble()), RandUnit(rng));

                        float identErr = Quaternion.Angle(BasisTwistSolveCore.ShapeReachStep(delta, axis, 1f, 1f), delta);

                        Quaternion noTwist = BasisTwistSolveCore.ShapeReachStep(delta, axis, 0f, 1f);
                        float residualTwist = Mathf.Abs(SignedTwistDeg(BasisTwistSolveCore.ExtractTwist(noTwist, axis), axis));

                        Quaternion noSwing = BasisTwistSolveCore.ShapeReachStep(delta, axis, 1f, 0f);
                        float residualSwing = Quaternion.Angle(noSwing, BasisTwistSolveCore.ExtractTwist(noSwing, axis));

                        float fullTwist = Mathf.Abs(SignedTwistDeg(BasisTwistSolveCore.ExtractTwist(delta, axis), axis));
                        float keepT = Mathf.Lerp(0.1f, 0.9f, (float)rng.NextDouble());
                        Quaternion part = BasisTwistSolveCore.ShapeReachStep(delta, axis, keepT, 1f);
                        float keptTwist = Mathf.Abs(SignedTwistDeg(BasisTwistSolveCore.ExtractTwist(part, axis), axis));
                        float blendErr = Mathf.Abs(keptTwist - keepT * fullTwist);

                        // Swing blend: scaling the swing by swingScale (twistKeep=1) scales its angle linearly.
                        float fullSwingAng = Quaternion.Angle(Quaternion.identity, delta * Quaternion.Inverse(BasisTwistSolveCore.ExtractTwist(delta, axis)));
                        float swingScaleT = Mathf.Lerp(0.1f, 0.9f, (float)rng.NextDouble());
                        Quaternion partS = BasisTwistSolveCore.ShapeReachStep(delta, axis, 1f, swingScaleT);
                        float partSwingAng = Quaternion.Angle(Quaternion.identity, partS * Quaternion.Inverse(BasisTwistSolveCore.ExtractTwist(partS, axis)));
                        float swingBlendErr = Mathf.Abs(partSwingAng - swingScaleT * fullSwingAng);

                        bool nan = !Finite(identErr) || !Finite(residualTwist) || !Finite(residualSwing) || !Finite(blendErr) || !Finite(swingBlendErr);
                        if (nan) s.NaNCount++;
                        s.MaxIdentityErrDeg = Mathf.Max(s.MaxIdentityErrDeg, identErr);
                        s.MaxResidualTwistDeg = Mathf.Max(s.MaxResidualTwistDeg, residualTwist);
                        s.MaxResidualSwingDeg = Mathf.Max(s.MaxResidualSwingDeg, residualSwing);
                        s.MaxBlendErrDeg = Mathf.Max(s.MaxBlendErrDeg, blendErr);
                        s.MaxSwingBlendErrDeg = Mathf.Max(s.MaxSwingBlendErrDeg, swingBlendErr);
                        s.Cases++;

                        bool fail = nan || identErr > 0.1f || residualTwist > 0.1f || residualSwing > 0.1f || blendErr > 0.5f || swingBlendErr > 0.5f;
                        if (fail) s.Failures++;
                        if (fail || i < 2)
                        {
                            sb.Clear(); sb.Append("inv,"); Append(sb, i); Append(sb, keepT);
                            Append(sb, identErr); Append(sb, residualTwist); Append(sb, residualSwing); Append(sb, blendErr);
                            Append(sb, 0f); Append(sb, 0f); Append(sb, 0f);
                            sb.Append(fail ? '1' : '0'); w.WriteLine(sb.ToString());
                        }
                    }

                    // ---- Pass B: graded CCD lateral lean, upright and lying down ----
                    bool bnan = false;
                    Quaternion[] orient = { Quaternion.identity, Quaternion.AngleAxis(90f, Vector3.right) }; // vertical, horizontal (supine)
                    string[] orientName = { "upright", "supine" };
                    float[] gradedTwistByOrient = new float[orient.Length];

                    float reproMax = 0f, reproLumbar = 0f;
                    float gradedMax = 0f, gradedReach = 0f, gradedJump = 0f, gradedLumbar = 0f, gradedCervical = 0f, gradedMidUpright = 0f;
                    for (int o = 0; o < orient.Length; o++)
                    {
                        Quaternion Q = orient[o];
                        Vector3 bodyUp = Q * Vector3.up;

                        LeanResult b = RunLean(Q, bodyUp, bodyUp, 1f, 1f, 0f, steps, w, sb, orientName[o] + "_repro", ref bnan);
                        reproMax = Mathf.Max(reproMax, b.MaxTwist);
                        reproLumbar = Mathf.Max(reproLumbar, b.LumbarTwist);

                        LeanResult g = RunLean(Q, bodyUp, bodyUp, s.TestedLumbarKeep, s.TestedCervicalKeep, k_ThoracicBendStiffen, steps, w, sb, orientName[o] + "_graded", ref bnan);
                        gradedMax = Mathf.Max(gradedMax, g.MaxTwist);
                        gradedReach = Mathf.Max(gradedReach, g.MaxReachErr);
                        gradedJump = Mathf.Max(gradedJump, g.MaxJump);
                        gradedLumbar = Mathf.Max(gradedLumbar, g.LumbarTwist);
                        gradedCervical = Mathf.Max(gradedCervical, g.CervicalTwist);
                        gradedTwistByOrient[o] = g.MaxTwist;
                        if (o == 0) gradedMidUpright = g.MidBendDeg;
                    }

                    // ---- Pass C: negative control -- supine, relaxed about WORLD up, measured about body up ----
                    LeanResult nc = RunLean(orient[1], Vector3.up, orient[1] * Vector3.up,
                        s.TestedLumbarKeep, s.TestedCervicalKeep, k_ThoracicBendStiffen, steps, w, sb, "supine_worldup", ref bnan);

                    // Even curvature: the same graded twist but with the mid-thoracic stiffening OFF, to confirm
                    // the stiffening redistributes the bend away from the mid joint (does not increase it).
                    LeanResult gu = RunLean(Quaternion.identity, Vector3.up, Vector3.up,
                        s.TestedLumbarKeep, s.TestedCervicalKeep, 0f, steps, w, sb, "upright_uniform", ref bnan);

                    if (bnan) s.NaNCount++;
                    s.ReproMaxTwistDeg = reproMax;
                    s.GradedMaxTwistDeg = gradedMax;
                    s.GradedMaxReachErrM = gradedReach;
                    s.GradedMaxJumpDeg = gradedJump;
                    s.OrientationSpreadDeg = Mathf.Abs(gradedTwistByOrient[0] - gradedTwistByOrient[1]);
                    s.ReproLumbarTwistDeg = reproLumbar;
                    s.GradedLumbarTwistDeg = gradedLumbar;
                    s.GradedCervicalTwistDeg = gradedCervical;
                    s.WorldUpSupineTwistDeg = nc.MaxTwist;
                    s.GradedMidBendDeg = gradedMidUpright;
                    s.UniformMidBendDeg = gu.MidBendDeg;
                    s.Cases += orient.Length * 2 * steps + 2 * steps;
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

        struct LeanResult { public float MaxTwist, MaxJump, MaxReachErr, LumbarTwist, CervicalTwist, MidBendDeg; }

        // Warm-started lateral lean on the reach sphere, body rigidly oriented by Q, twist relaxed about
        // 'relaxAxis' and measured about 'measureAxis' (these differ only for the world-up negative control).
        // Returns peak |axial twist| / reach gap / across-center jump, plus the lumbar (root) and cervical
        // (tip) joint twists at full lean for the grading check.
        static LeanResult RunLean(Quaternion Q, Vector3 relaxAxis, Vector3 measureAxis,
            float lumbarKeep, float cervicalKeep, float swingStiffen, int steps, StreamWriter w, StringBuilder sb, string tag, ref bool nan)
        {
            int n = k_CumLen.Length;
            var pos = new Vector3[n];
            var rot = new Quaternion[n];
            for (int i = 0; i < n; i++) { pos[i] = Q * (k_RestDir * k_CumLen[i]); rot[i] = Q; }
            float restLen = k_CumLen[0];

            float maxTwist = 0f, maxJump = 0f, maxReachErr = 0f, prevSigned = 0f;
            float lumbarTwist = 0f, cervicalTwist = 0f, midBend = 0f;
            bool havePrev = false;

            for (int p = 0; p < steps; p++)
            {
                float phi = Mathf.Lerp(-k_MaxLeanDeg, k_MaxLeanDeg, p / (float)(steps - 1));
                // Lateral lean about the body forward axis, on the reach sphere (constant hips->head length).
                Vector3 leanDir = Q * (Quaternion.AngleAxis(phi, Vector3.forward) * k_RestDir);
                Vector3 target = leanDir * restLen;
                SolveChain(pos, rot, target, relaxAxis, lumbarKeep, cervicalKeep, swingStiffen);

                float stepMaxTwist = 0f, chestSigned = 0f, rootSigned = 0f, tipSigned = 0f, chestSwingAng = 0f;
                for (int j = 1; j <= n - 2; j++)
                {
                    Quaternion local = rot[j] * Quaternion.Inverse(Q); // rotation relative to rest orientation Q
                    float signed = SignedTwistDeg(BasisTwistSolveCore.ExtractTwist(local, measureAxis), measureAxis);
                    stepMaxTwist = Mathf.Max(stepMaxTwist, Mathf.Abs(signed));
                    if (j == 2)
                    {
                        chestSigned = signed;
                        // swing (bend) of the mid joint = the rotation left after removing the twist about the axis.
                        Quaternion swingPart = local * Quaternion.Inverse(BasisTwistSolveCore.ExtractTwist(local, measureAxis));
                        chestSwingAng = Quaternion.Angle(Quaternion.identity, swingPart);
                    }
                    if (j == n - 2) rootSigned = Mathf.Abs(signed); // spine / lumbar
                    if (j == 1) tipSigned = Mathf.Abs(signed);      // neck / cervical
                }
                float reachErr = Vector3.Distance(pos[0], target);
                if (!Finite(stepMaxTwist) || !Finite(reachErr)) nan = true;

                float jump = havePrev ? Mathf.Abs(chestSigned - prevSigned) : 0f;
                prevSigned = chestSigned; havePrev = true;

                maxTwist = Mathf.Max(maxTwist, stepMaxTwist);
                maxJump = Mathf.Max(maxJump, jump);
                maxReachErr = Mathf.Max(maxReachErr, reachErr);
                if (p == steps - 1) { lumbarTwist = rootSigned; cervicalTwist = tipSigned; midBend = chestSwingAng; }

                sb.Clear(); sb.Append(tag).Append(',');
                Append(sb, phi); Append(sb, 0f);
                Append(sb, 0f); Append(sb, 0f); Append(sb, 0f); Append(sb, 0f);
                Append(sb, stepMaxTwist); Append(sb, reachErr); Append(sb, jump);
                sb.Append('0'); w.WriteLine(sb.ToString());
            }

            return new LeanResult { MaxTwist = maxTwist, MaxJump = maxJump, MaxReachErr = maxReachErr, LumbarTwist = lumbarTwist, CervicalTwist = cervicalTwist, MidBendDeg = midBend };
        }

        // Faithful replica of SolveSequentialSpineIK: hips (last) fixed, head (0) the effector; rotate
        // spine..neck root-side -> tip-side, minimal-arc per joint, shaped by ShapeReachStep with the same
        // lumbar->cervical twist grading + mid-thoracic swing stiffening as the live solver, under-relaxed.
        static void SolveChain(Vector3[] pos, Quaternion[] rot, Vector3 target, Vector3 axis,
            float lumbarKeep, float cervicalKeep, float swingStiffen)
        {
            int n = pos.Length;
            const int tip = 0, firstJoint = 1;
            int lastJoint = n - 2;
            float jointSpan = Mathf.Max(1, lastJoint - firstJoint);
            const float eps = 1e-10f;

            for (int iter = 0; iter < k_MaxIters; iter++)
            {
                if ((target - pos[tip]).sqrMagnitude < 1e-10f) break;
                for (int i = lastJoint; i >= firstJoint; i--)
                {
                    Vector3 cur = pos[tip] - pos[i];
                    Vector3 tgt = target - pos[i];
                    if (cur.sqrMagnitude < eps || tgt.sqrMagnitude < eps) continue;

                    Quaternion delta = Quaternion.FromToRotation(cur, tgt);
                    float t = (i - firstJoint) / jointSpan;
                    float keep = Mathf.Lerp(cervicalKeep, lumbarKeep, t);
                    float swingScale = 1f - swingStiffen * (1f - Mathf.Abs(2f * t - 1f));
                    delta = BasisTwistSolveCore.ShapeReachStep(delta, axis, keep, swingScale);
                    delta = Quaternion.Slerp(Quaternion.identity, delta, k_CcdRelax);
                    RotateJoint(pos, rot, i, delta);
                }
            }
        }

        static void RotateJoint(Vector3[] pos, Quaternion[] rot, int i, Quaternion delta)
        {
            Vector3 pivot = pos[i];
            for (int j = 0; j <= i; j++)
            {
                rot[j] = delta * rot[j];
                if (j < i) pos[j] = pivot + delta * (pos[j] - pivot);
            }
        }

        // Signed rotation angle (deg) of 'tw' about 'axisUnit'. The spine twists stay well under 180 deg, so
        // the |w|->angle + dot->sign form is exact and continuous (a side flip across center => a jump).
        static float SignedTwistDeg(Quaternion tw, Vector3 axisUnit)
        {
            float ang = 2f * Mathf.Acos(Mathf.Clamp(Mathf.Abs(tw.w), 0f, 1f)) * Mathf.Rad2Deg;
            float dot = tw.x * axisUnit.x + tw.y * axisUnit.y + tw.z * axisUnit.z;
            return ang * (dot >= 0f ? 1f : -1f);
        }

        static Vector3 RandUnit(System.Random r)
        {
            Vector3 v = new Vector3((float)(r.NextDouble() * 2.0 - 1.0), (float)(r.NextDouble() * 2.0 - 1.0), (float)(r.NextDouble() * 2.0 - 1.0));
            if (v.sqrMagnitude < 1e-6f) v = Vector3.up;
            return v.normalized;
        }

        static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        static void Append(StringBuilder sb, float v) { sb.Append(F(v)).Append(','); }
        static void Append(StringBuilder sb, int v) { sb.Append(v).Append(','); }

        static string F(float v)
        {
            if (float.IsNaN(v)) return "nan";
            return v.ToString("0.######", CultureInfo.InvariantCulture);
        }
    }
}
