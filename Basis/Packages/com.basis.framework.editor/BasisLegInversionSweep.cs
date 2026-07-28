using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using Basis.IK;

namespace Basis.IK.Debugging
{
    // Offline sweep that flags INHUMAN knee poses -- the knee bending backward when the hint/pole role
    // inverts (a leg tracker driving the knee hint behind the leg). A human knee is always anterior to
    // the hip->ankle line; "inverted" = the solved knee sits posterior to that line. Two passes, one CSV:
    //   hint   : fixed representative foot targets, hint direction swept over a sphere -> at what hint
    //            error does the knee flip backward, and does it flip inside the usable hint cone.
    //   target : nominal forward hint, foot target swept over the grid -> reachable targets that still
    //            produce a backward knee (an inhuman pose under good tracking).
    // Pure math on BasisLegSolveCore, no avatar -- same solver the live rig runs.
    public struct BasisLegInversionConfig
    {
        public BasisLegIKSweepConfig Base;
        public int HintAzSteps;     // hint azimuth samples around the vertical axis
        public int HintElSteps;     // hint elevation samples (up/down tilt)
        public float SafeConeDeg;   // hint within this angle of nominal must never invert the knee

        public static BasisLegInversionConfig Default()
        {
            return new BasisLegInversionConfig
            {
                Base = BasisLegIKSweepConfig.Default(),
                HintAzSteps = 72,
                HintElSteps = 27,
                SafeConeDeg = 60f,
            };
        }
    }

    public struct BasisLegInversionSummary
    {
        public bool Ok;
        public string Path;
        public string Error;
        public int Rows;

        public int HintSamples;         // hint-stress poses classified
        public int HintInverted;        // of those, backward-knee
        public int SingularSamples;     // hint near the leg axis (pole singularity) -- excluded from the gate
        public int SingularInversions;  // of those, backward-knee (informational: does a bad pole still invert)
        public int SafeConeSamples;     // well-conditioned hints within SafeConeDeg of nominal
        public int SafeConeInversions;  // inversions among those (should be 0)
        public float OnsetDeviationDeg; // smallest well-conditioned hint deviation (deg) that inverted (NaN if none)

        public int TargetReachable;     // good-hint grid poses that are reachable + well-formed
        public int TargetInversions;    // of those, backward-knee (should be 0)

        public float WorstSwivelDeg;    // worst |knee swivel from forward| seen (180 = straight back)

        public float MinKneeFlexDeg;    // smallest solved knee interior angle (flexion-limit pass); the clamp should hold it >= MinKneeInteriorDeg
        public int FlexClampSamples;    // pull-in targets that fall inside the limit (clamp should engage); 0 = not exercised
    }

    public struct BasisLegInversionTemporalSummary
    {
        public bool Ok;
        public string Path;
        public string Error;
        public int Steps;
        public int Crossings;        // good-hint, smooth motion: knee entries into inversion (should be 0)
        public float MinFwdFrac;     // most-inverted fwd_frac on the good-hint paths (>=0 = always anterior)
        public int NoisyCrossings;   // same under per-frame hint jitter (a shaky pole tracker) -- informational
        public float NoisyMinFwdFrac;
    }

    public struct BasisLegCrouchSummary
    {
        public bool Ok;
        public string Path;
        public string Error;
        public int Steps;
        public int Scenarios;
        public int PosteriorInversions; // good-hint crouch steps with the knee bent backward (should be 0)
        public int Snaps;               // good-hint crouch steps where the knee swivel jumped >90deg vs the previous step (the "random shoot up" -- a transient pole flip)
        public int Episodes;            // entries into the inverted/snapped state
        public float WorstSwivelJumpDeg;// largest single-step swivel jump on a bent leg
        public float WorstSwivelDeg;    // worst |knee swivel from forward|
        public float MinFwdFrac;        // most-posterior knee on the crouch paths
        public float OnsetCrouchFrac;   // hip-height fraction (1=stand, ~0.25=deep) where the first flip appeared (NaN none)
        public string WorstScenario;
    }

    public static class BasisLegInversionSweep
    {
        public const string DefaultFileName = "BasisLegInversionSweep.csv";

        // A hint within this angle of the leg axis (parallel OR anti-parallel) is a pole singularity: the
        // bend plane is ill-defined there. Excluded from the gate, like the trajectory scans exclude their
        // kinematic singularities. Wider than the solver's blend cone so the boundary isn't double-counted.
        const float k_SingularCos = 0.866f; // cos(30deg)

        public static string DefaultPath()
        {
            return System.IO.Path.Combine(Application.persistentDataPath, DefaultFileName);
        }

        // Representative reachable, clearly-bent foot targets (fractions of leg length, x mirrored per side).
        // step-back is the worst case for a pole flip: the foot is behind, so a bad hint sends the knee back.
        static readonly Vector3[] k_StressTargets =
        {
            new Vector3(0.15f, -0.78f, 0.45f),  // squat: foot forward + down
            new Vector3(0.10f, -0.55f, 0.55f),  // lift:  foot up + forward
            new Vector3(0.10f, -0.90f, -0.30f), // step-back: foot behind
            new Vector3(0.45f, -0.85f, 0.12f),  // out: foot to the side
        };
        static readonly string[] k_StressNames = { "squat", "lift", "step-back", "side" };

        public static BasisLegInversionSummary Run(BasisLegInversionConfig cfg, string path)
        {
            var summary = new BasisLegInversionSummary { Ok = false, Path = path };
            BasisLegIKSweepConfig b = cfg.Base;

            float mirror = b.IsLeft ? -1f : 1f;
            float upper = Mathf.Max(1e-4f, b.UpperLength);
            float lower = Mathf.Max(1e-4f, b.LowerLength);
            float legLen = upper + lower;

            Vector3 hip = Vector3.zero;
            Vector3 kneeDir = Mirror(b.RestKneeDir, mirror).normalized;
            Vector3 shinDir = Mirror(b.RestShinDir, mirror).normalized;
            if (kneeDir.sqrMagnitude < 1e-8f) kneeDir = Vector3.down;
            if (shinDir.sqrMagnitude < 1e-8f) shinDir = Vector3.down;
            Vector3 restKnee = hip + kneeDir * upper;
            Vector3 restFoot = restKnee + shinDir * lower;

            Vector3 bendNormal = b.BendNormal; // NOT mirrored: the rig uses the same hips-right KneeBendPref for both legs (BasisLocalRigDriver)
            if (bendNormal.sqrMagnitude < 1e-8f) bendNormal = Vector3.right;

            Vector3 nominalDir = Mirror(b.HintDir, mirror).normalized;
            if (nominalDir.sqrMagnitude < 1e-8f) nominalDir = Vector3.forward;
            float hintDist = b.HintDistanceFrac * legLen;
            Vector3 nominalHint = hip + nominalDir * hintDist;

            int azN = Mathf.Max(1, cfg.HintAzSteps);
            int elN = Mathf.Max(1, cfg.HintElSteps);

            int hintSamples = 0, hintInverted = 0, safeSamples = 0, safeInversions = 0;
            int singularSamples = 0, singularInversions = 0;
            int targetReachable = 0, targetInversions = 0, rows = 0;
            float onset = float.NaN, worstSwivel = 0f;
            float minKneeFlex = 999f;
            int flexClampSamples = 0;
            float minFlexReach = Mathf.Sqrt(Mathf.Max(0f, upper * upper + lower * lower
                - 2f * upper * lower * Mathf.Cos(BasisLegSolveCore.MinKneeInteriorDeg * Mathf.Deg2Rad)));

            try
            {
                string dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                using (var w = new StreamWriter(path, false, Encoding.UTF8))
                {
                    w.WriteLine("# BasisLegInversionSweep " + System.DateTime.UtcNow.ToString("o"));
                    w.WriteLine("# side=" + (b.IsLeft ? "left" : "right") + " upper=" + F(upper) + " lower=" + F(lower) +
                                " safeCone=" + F(cfg.SafeConeDeg) +
                                " nominalHint=(" + F(nominalDir.x) + "," + F(nominalDir.y) + "," + F(nominalDir.z) + ")");
                    w.WriteLine("mode,label,a,b,c,dev_deg,hint_x,hint_y,hint_z,target_x,target_y,target_z," +
                                "reach,reachable,knee_x,knee_y,knee_z,flex_deg,swivel_deg,fwd_frac,singular,inverted");

                    var sb = new StringBuilder(256);

                    // --- pass A: hint-stress (the tracker-inverts-the-pole case) ---
                    for (int t = 0; t < k_StressTargets.Length; t++)
                    {
                        Vector3 frac = k_StressTargets[t];
                        Vector3 target = hip + new Vector3(frac.x * mirror, frac.y, frac.z) * legLen;
                        Vector3 legAxis = (target - hip).normalized;
                        for (int ai = 0; ai < azN; ai++)
                        {
                            float az = (azN <= 1) ? 0f : Mathf.Lerp(-180f, 180f, ai / (float)azN); // [-180,180)
                            for (int ei = 0; ei < elN; ei++)
                            {
                                float el = (elN <= 1) ? 0f : Mathf.Lerp(-80f, 80f, ei / (float)(elN - 1));
                                Vector3 hdir = HintDir(az, el, mirror);
                                Vector3 hint = hip + hdir * hintDist;
                                float dev = Vector3.Angle(hdir, nominalDir);
                                bool singular = IsPoleSingular(hdir, legAxis);

                                BasisLegSolveResult r = SolveOne(hip, restKnee, restFoot, target, hint, bendNormal);
                                bool inv = ClassifyInverted(hip, r.KneeSolved, r.FootSolved, legLen,
                                    out float flex, out float swivel, out float fwd);
                                bool reachable = r.ReachRatio <= 1f;

                                hintSamples++;
                                if (singular)
                                {
                                    singularSamples++;
                                    if (inv) singularInversions++;
                                }
                                else
                                {
                                    // The gate lives on well-conditioned hints: a non-degenerate pole must
                                    // never bend the knee backward inside the usable cone.
                                    if (inv) hintInverted++;
                                    bool inSafe = dev <= cfg.SafeConeDeg;
                                    if (inSafe) safeSamples++;
                                    if (inv)
                                    {
                                        if (inSafe) safeInversions++;
                                        if (float.IsNaN(onset) || dev < onset) onset = dev;
                                    }
                                }
                                worstSwivel = TrackWorst(worstSwivel, swivel);

                                rows += WriteRow(w, sb, "hint", k_StressNames[t], ai, ei, 0, dev, hint, target,
                                    r, reachable, flex, swivel, fwd, singular, inv);
                            }
                        }
                    }

                    // --- pass B: good (nominal) hint, sweep the foot target over the grid ---
                    int sx = Mathf.Max(1, b.Steps.x), sy = Mathf.Max(1, b.Steps.y), sz = Mathf.Max(1, b.Steps.z);
                    for (int i = 0; i < sx; i++)
                    {
                        float fx = Lerp01(b.MinFrac.x, b.MaxFrac.x, sx, i) * mirror;
                        for (int j = 0; j < sy; j++)
                        {
                            float fy = Lerp01(b.MinFrac.y, b.MaxFrac.y, sy, j);
                            for (int k = 0; k < sz; k++)
                            {
                                float fz = Lerp01(b.MinFrac.z, b.MaxFrac.z, sz, k);
                                Vector3 target = hip + new Vector3(fx, fy, fz) * legLen;
                                Vector3 legAxis = (target - hip).normalized;
                                bool singular = IsPoleSingular(nominalDir, legAxis);

                                BasisLegSolveResult r = SolveOne(hip, restKnee, restFoot, target, nominalHint, bendNormal);
                                bool inv = ClassifyInverted(hip, r.KneeSolved, r.FootSolved, legLen,
                                    out float flex, out float swivel, out float fwd);

                                // A good hint must keep reachable, non-singular, bent poses human.
                                bool wellFormed = !singular && r.ReachRatio >= 0.4f && r.ReachRatio <= 0.97f && flex < 175f;
                                if (wellFormed)
                                {
                                    targetReachable++;
                                    if (inv) targetInversions++;
                                }
                                worstSwivel = TrackWorst(worstSwivel, swivel);

                                rows += WriteRow(w, sb, "target", "grid", i, j, k, float.NaN, nominalHint, target,
                                    r, r.ReachRatio <= 1f, flex, swivel, fwd, singular, inv);
                            }
                        }
                    }

                    // --- pass C: flexion limit. Pull the foot straight in toward the hip along a natural tuck
                    // direction; the solved knee must stop at MinKneeInteriorDeg, never folding the calf through
                    // the thigh. With the clamp minFlex holds at the limit; without it the interior collapses
                    // toward 0 as the foot nears the hip.
                    Vector3 tuckDir = new Vector3(0.1f * mirror, -0.6f, 0.5f).normalized;
                    const int flexSteps = 48;
                    for (int s = 0; s < flexSteps; s++)
                    {
                        float t = Mathf.Lerp(0.03f, 0.7f, s / (float)(flexSteps - 1));
                        Vector3 target = hip + tuckDir * (t * legLen);
                        if (t * legLen < minFlexReach) flexClampSamples++;
                        BasisLegSolveResult r = SolveOne(hip, restKnee, restFoot, target, nominalHint, bendNormal);
                        bool inv = ClassifyInverted(hip, r.KneeSolved, r.FootSolved, legLen,
                            out float flex, out float swivel, out float fwd);
                        if (r.ReachRatio <= 1f && flex < minKneeFlex) minKneeFlex = flex;
                        worstSwivel = TrackWorst(worstSwivel, swivel);
                        rows += WriteRow(w, sb, "flex", "tuck", s, 0, 0, float.NaN, nominalHint, target,
                            r, r.ReachRatio <= 1f, flex, swivel, fwd, false, inv);
                    }
                }

                summary.Ok = true;
                summary.Rows = rows;
                summary.HintSamples = hintSamples;
                summary.HintInverted = hintInverted;
                summary.SingularSamples = singularSamples;
                summary.SingularInversions = singularInversions;
                summary.SafeConeSamples = safeSamples;
                summary.SafeConeInversions = safeInversions;
                summary.OnsetDeviationDeg = onset;
                summary.TargetReachable = targetReachable;
                summary.TargetInversions = targetInversions;
                summary.WorstSwivelDeg = worstSwivel;
                summary.MinKneeFlexDeg = minKneeFlex;
                summary.FlexClampSamples = flexClampSamples;
            }
            catch (System.Exception e)
            {
                summary.Ok = false;
                summary.Error = e.Message;
            }

            return summary;
        }

        // Per-frame feedback drive that watches the knee cross from anterior to posterior MID-MOTION -- the
        // transient flip the per-point grid can't see. Clean pass = fixed good hint on smooth foot paths
        // (must never invert -> gated); noisy pass = per-frame hint jitter (a shaky pole tracker) -> reported.
        public static BasisLegInversionTemporalSummary RunTemporal(BasisLegInversionConfig cfg, float hintNoise, string path)
        {
            var summary = new BasisLegInversionTemporalSummary { Ok = false, Path = path };
            BasisLegIKSweepConfig b = cfg.Base;

            float mirror = b.IsLeft ? -1f : 1f;
            float upper = Mathf.Max(1e-4f, b.UpperLength);
            float lower = Mathf.Max(1e-4f, b.LowerLength);
            float legLen = upper + lower;
            Vector3 hip = Vector3.zero;
            Vector3 kneeDir = Mirror(b.RestKneeDir, mirror).normalized;
            Vector3 shinDir = Mirror(b.RestShinDir, mirror).normalized;
            if (kneeDir.sqrMagnitude < 1e-8f) kneeDir = Vector3.down;
            if (shinDir.sqrMagnitude < 1e-8f) shinDir = Vector3.down;
            Vector3 restKnee = hip + kneeDir * upper;
            Vector3 restFoot = restKnee + shinDir * lower;
            Vector3 bendNormal = b.BendNormal; // NOT mirrored: the rig uses the same hips-right KneeBendPref for both legs (BasisLocalRigDriver)
            if (bendNormal.sqrMagnitude < 1e-8f) bendNormal = Vector3.right;
            Vector3 nominalDir = Mirror(b.HintDir, mirror).normalized;
            if (nominalDir.sqrMagnitude < 1e-8f) nominalDir = Vector3.forward;
            Vector3 nominalHint = hip + nominalDir * (b.HintDistanceFrac * legLen);

            int crossings = 0, noisyCrossings = 0, steps = 0;
            float minFwd = 1f, noisyMinFwd = 1f;

            try
            {
                string dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                Vector3 F3(float fx, float fy, float fz) => hip + new Vector3(fx * mirror, fy, fz) * legLen;
                string[] names = { "step", "lift-knee", "swing-side", "circle" };
                Vector3[][] paths =
                {
                    BasisIKTrajectoryScan.Line(F3(0.10f, -0.95f, -0.50f), F3(0.10f, -0.95f, 0.60f), 160),
                    BasisIKTrajectoryScan.Line(F3(0.10f, -1.10f, 0.10f), F3(0.10f, -0.35f, 0.40f), 160),
                    BasisIKTrajectoryScan.Line(F3(0.50f, -0.90f, 0.20f), F3(-0.40f, -0.90f, 0.20f), 160),
                    BasisIKTrajectoryScan.Circle(F3(0.10f, -0.80f, 0.20f), new Vector3(mirror, 0f, 0f), new Vector3(0f, 0f, 1f), 0.35f * legLen, 200),
                };

                using (var w = new StreamWriter(path, false, Encoding.UTF8))
                {
                    w.WriteLine("# BasisLegInversionTemporal " + System.DateTime.UtcNow.ToString("o") +
                                " side=" + (b.IsLeft ? "left" : "right") + " hintNoise=" + F(hintNoise));
                    w.WriteLine("path,step,mode,target_x,target_y,target_z,hint_x,hint_y,hint_z,knee_x,knee_y,knee_z,fwd_frac,inverted");
                    var sb = new StringBuilder(160);

                    for (int pi = 0; pi < names.Length; pi++)
                    {
                        Vector3[] pts = paths[pi];
                        Vector3 cKnee = restKnee, cFoot = restFoot, nKnee = restKnee, nFoot = restFoot;
                        var rng = new System.Random(9000 + pi);
                        float prevClean = float.NaN, prevNoisy = float.NaN;
                        for (int s = 0; s < pts.Length; s++)
                        {
                            Vector3 target = pts[s];
                            steps++;

                            BasisLegSolveResult cr = SolveOne(hip, cKnee, cFoot, target, nominalHint, bendNormal);
                            cKnee = cr.KneeSolved; cFoot = cr.FootSolved;
                            ClassifyInverted(hip, cr.KneeSolved, cr.FootSolved, legLen, out float flexC, out _, out float fwdC);
                            if (flexC < k_StraightFlexDeg && fwdC < minFwd) minFwd = fwdC; // bent poses only -- straight legs have no side
                            if (!float.IsNaN(prevClean) && prevClean >= k_InvertedFwdFrac && fwdC < k_InvertedFwdFrac) crossings++;
                            prevClean = fwdC;
                            WriteTemporalRow(w, sb, names[pi], s, "clean", target, nominalHint, cr.KneeSolved, fwdC);

                            Vector3 noisyHint = nominalHint;
                            if (hintNoise > 0f)
                            {
                                noisyHint += new Vector3((float)(rng.NextDouble() * 2.0 - 1.0),
                                    (float)(rng.NextDouble() * 2.0 - 1.0), (float)(rng.NextDouble() * 2.0 - 1.0)) * hintNoise;
                            }
                            BasisLegSolveResult nr = SolveOne(hip, nKnee, nFoot, target, noisyHint, bendNormal);
                            nKnee = nr.KneeSolved; nFoot = nr.FootSolved;
                            ClassifyInverted(hip, nr.KneeSolved, nr.FootSolved, legLen, out float flexN, out _, out float fwdN);
                            if (flexN < k_StraightFlexDeg && fwdN < noisyMinFwd) noisyMinFwd = fwdN;
                            if (!float.IsNaN(prevNoisy) && prevNoisy >= k_InvertedFwdFrac && fwdN < k_InvertedFwdFrac) noisyCrossings++;
                            prevNoisy = fwdN;
                            WriteTemporalRow(w, sb, names[pi], s, "noisy", target, noisyHint, nr.KneeSolved, fwdN);
                        }
                    }
                }

                summary.Ok = true;
                summary.Steps = steps;
                summary.Crossings = crossings;
                summary.MinFwdFrac = minFwd;
                summary.NoisyCrossings = noisyCrossings;
                summary.NoisyMinFwdFrac = noisyMinFwd;
            }
            catch (System.Exception e) { summary.Ok = false; summary.Error = e.Message; }
            return summary;
        }

        public const string CrouchFileName = "BasisLegCrouchSweep.csv";
        public static string CrouchPath() => System.IO.Path.Combine(Application.persistentDataPath, CrouchFileName);

        // Foot placement relative to the hip's XZ (fractions of legLen): x lateral (mirrored), z forward.
        // VR crouch plants the foot (foot IK) ~under/slightly-forward and lowers the hips -> the leg axis goes
        // near-vertical at depth, the worst case for a pole flip ("leg shoots up inverted").
        static readonly Vector3[] k_CrouchFoot =
        {
            new Vector3(0.02f, 0f, 0.04f),  // under: foot ~directly below the hip (vertical leg axis at depth)
            new Vector3(0.04f, 0f, 0.18f),  // near-under, slight forward (typical VR crouch)
            new Vector3(0.06f, 0f, 0.34f),  // forward: foot well ahead (squat)
            new Vector3(0.06f, 0f, -0.22f), // back: foot behind the hip
            new Vector3(0.38f, 0f, 0.06f),  // wide
            new Vector3(0.28f, 0f, 0.30f),  // fwd-wide
        };
        static readonly string[] k_CrouchFootNames = { "under", "near-under", "forward", "back", "wide", "fwd-wide" };

        // Lower the hips from standing to a deep crouch (foot planted) and back, feeding the previous solved
        // knee/foot so transient pole flips show up -- the per-point grid can't see a mid-crouch snap. A good
        // forward hint must keep the knee anterior with no swivel snap through the whole range.
        public static BasisLegCrouchSummary RunCrouch(BasisLegInversionConfig cfg, string path)
        {
            var summary = new BasisLegCrouchSummary { Ok = false, Path = path };
            BasisLegIKSweepConfig b = cfg.Base;
            float mirror = b.IsLeft ? -1f : 1f;
            float upper = Mathf.Max(1e-4f, b.UpperLength);
            float lower = Mathf.Max(1e-4f, b.LowerLength);
            float legLen = upper + lower;
            Vector3 bendNormal = b.BendNormal;
            if (bendNormal.sqrMagnitude < 1e-8f) bendNormal = Vector3.right;
            Vector3 hintDir = Mirror(b.HintDir, mirror).normalized;
            if (hintDir.sqrMagnitude < 1e-8f) hintDir = Vector3.forward;

            int steps = 0, posterior = 0, snaps = 0, episodes = 0, scenarios = 0;
            float worstJump = 0f, worstSwivel = 0f, minFwd = 1f, onset = float.NaN;
            string worstScenario = "";

            const int downSteps = 140;
            const float standFrac = 0.97f, deepFrac = 0.25f;

            try
            {
                string dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                using (var w = new StreamWriter(path, false, Encoding.UTF8))
                {
                    w.WriteLine("# BasisLegCrouchSweep " + System.DateTime.UtcNow.ToString("o") +
                                " side=" + (b.IsLeft ? "left" : "right") + " upper=" + F(upper) + " lower=" + F(lower));
                    w.WriteLine("scenario,step,crouch_frac,hip_y,foot_x,foot_z,knee_x,knee_y,knee_z,flex_deg,swivel_deg,swivel_jump,fwd_frac,inverted");
                    var sb = new StringBuilder(200);

                    for (int si = 0; si < k_CrouchFoot.Length; si++)
                    {
                        scenarios++;
                        Vector3 fo = k_CrouchFoot[si];
                        Vector3 foot = new Vector3(fo.x * mirror, 0f, fo.z) * legLen;
                        Vector3 hip0 = new Vector3(0f, standFrac * legLen, 0f);
                        Vector3 cKnee = hip0 + Vector3.Normalize(foot - hip0) * upper;
                        Vector3 cFoot = foot;
                        float prevSwivel = float.NaN;
                        bool prevInv = false;
                        int total = downSteps * 2;
                        for (int s = 0; s < total; s++)
                        {
                            float u = (s < downSteps) ? s / (float)(downSteps - 1) : 1f - (s - downSteps) / (float)(downSteps - 1);
                            float crouchFrac = Mathf.Lerp(standFrac, deepFrac, u);
                            Vector3 hip = new Vector3(0f, crouchFrac * legLen, 0f);
                            Vector3 mid = (hip + foot) * 0.5f;
                            Vector3 hint = mid + hintDir * (upper * 0.5f);

                            BasisLegSolveResult r = SolveOne(hip, cKnee, cFoot, foot, hint, bendNormal);
                            cKnee = r.KneeSolved; cFoot = r.FootSolved;
                            ClassifyInverted(hip, r.KneeSolved, r.FootSolved, legLen, out float flex, out float swivel, out float fwd);

                            // Pole offset: how far the knee sits off the hip->foot line. Near the axis (a
                            // near-straight or deeply-tucked leg) the swivel is undefined, so its noise isn't a
                            // real flip -- only treat a swivel jump as a snap when the pole is well-defined.
                            Vector3 chord = foot - hip;
                            Vector3 kneeRel = r.KneeSolved - hip;
                            float chordSq = chord.sqrMagnitude;
                            Vector3 poleOff = chordSq > 1e-10f ? kneeRel - chord * (Vector3.Dot(kneeRel, chord) / chordSq) : kneeRel;
                            bool bent = flex < 160f && poleOff.magnitude > 0.06f * legLen && !float.IsNaN(swivel);
                            float jump = (bent && !float.IsNaN(prevSwivel)) ? Mathf.Abs(Mathf.DeltaAngle(prevSwivel, swivel)) : 0f;
                            bool post = bent && fwd < k_InvertedFwdFrac;
                            bool snap = jump > 90f;
                            bool inv = post || snap;

                            steps++;
                            if (post) posterior++;
                            if (snap) snaps++;
                            if (inv && !prevInv) { episodes++; if (float.IsNaN(onset)) onset = crouchFrac; }
                            if (jump > worstJump) { worstJump = jump; worstScenario = k_CrouchFootNames[si]; }
                            worstSwivel = TrackWorst(worstSwivel, swivel);
                            if (bent && fwd < minFwd) minFwd = fwd;
                            prevInv = inv;
                            prevSwivel = bent ? swivel : float.NaN;

                            sb.Clear();
                            sb.Append(k_CrouchFootNames[si]).Append(',').Append(s).Append(',');
                            Append(sb, crouchFrac); Append(sb, hip.y);
                            Append(sb, foot.x); Append(sb, foot.z);
                            Append(sb, r.KneeSolved.x); Append(sb, r.KneeSolved.y); Append(sb, r.KneeSolved.z);
                            Append(sb, flex); Append(sb, swivel); Append(sb, jump); Append(sb, fwd);
                            sb.Append(inv ? '1' : '0');
                            w.WriteLine(sb.ToString());
                        }
                    }
                }
                summary.Ok = true; summary.Steps = steps; summary.Scenarios = scenarios;
                summary.PosteriorInversions = posterior; summary.Snaps = snaps; summary.Episodes = episodes;
                summary.WorstSwivelJumpDeg = worstJump; summary.WorstSwivelDeg = worstSwivel;
                summary.MinFwdFrac = minFwd; summary.OnsetCrouchFrac = onset; summary.WorstScenario = worstScenario;
            }
            catch (System.Exception e) { summary.Ok = false; summary.Error = e.Message; }
            return summary;
        }

        static void WriteTemporalRow(StreamWriter w, StringBuilder sb, string pathName, int step, string mode,
            Vector3 target, Vector3 hint, Vector3 knee, float fwd)
        {
            sb.Clear();
            sb.Append(pathName).Append(',').Append(step).Append(',').Append(mode).Append(',');
            Append(sb, target.x); Append(sb, target.y); Append(sb, target.z);
            Append(sb, hint.x); Append(sb, hint.y); Append(sb, hint.z);
            Append(sb, knee.x); Append(sb, knee.y); Append(sb, knee.z);
            Append(sb, fwd);
            sb.Append(fwd < k_InvertedFwdFrac ? '1' : '0');
            w.WriteLine(sb.ToString());
        }

        // Knee posterior to the hip->ankle line = inverted. fwdFrac is the signed forward component of the
        // knee's perpendicular offset from that line, in [-1,1]: +1 fully anterior (human), -1 fully behind.
        // The boolean is conservative (< -0.5 = more than 120deg off forward, clearly behind -- not merely
        // lateral) so the gate flags only real backward-bends; the continuous fwdFrac column carries the
        // borderline poses for analysis.
        const float k_InvertedFwdFrac = -0.5f;
        const float k_StraightFlexDeg = 170f; // knee straighter than this has no defined bend side

        static bool ClassifyInverted(Vector3 hip, Vector3 knee, Vector3 foot, float legLen,
            out float flexDeg, out float swivelDeg, out float fwdFrac)
        {
            flexDeg = AngleDeg(hip - knee, foot - knee);
            swivelDeg = Swivel(hip, foot, knee);

            Vector3 chord = foot - hip;
            Vector3 kneeRel = knee - hip;
            float cs = chord.sqrMagnitude;
            Vector3 off = cs > 1e-10f ? kneeRel - chord * (Vector3.Dot(kneeRel, chord) / cs) : kneeRel;
            float offMag = off.magnitude;
            // A near-straight leg has no meaningful bend side: the offset is tiny and its DIRECTION is
            // noise, so "backward" is undefined. Guard by both flex and offset, or it false-flags inversions
            // on the extended segments of a swept path.
            if (flexDeg > k_StraightFlexDeg || offMag < 0.02f * legLen) { fwdFrac = 0f; return false; }
            fwdFrac = Vector3.Dot(off, Vector3.forward) / offMag;
            return fwdFrac < k_InvertedFwdFrac;
        }

        // Pole within k_SingularCos of the leg axis (parallel or anti-parallel) -> ill-defined bend plane.
        static bool IsPoleSingular(Vector3 hintDir, Vector3 legAxisDir)
        {
            return Mathf.Abs(Vector3.Dot(hintDir, legAxisDir)) > k_SingularCos;
        }

        // dir from azimuth (around vertical) + elevation; az=0,el=0 -> +Z (forward). x mirrored per side.
        static Vector3 HintDir(float azDeg, float elDeg, float mirror)
        {
            float a = azDeg * Mathf.Deg2Rad, e = elDeg * Mathf.Deg2Rad;
            float ce = Mathf.Cos(e);
            Vector3 d = new Vector3(ce * Mathf.Sin(a) * mirror, Mathf.Sin(e), ce * Mathf.Cos(a));
            return d.sqrMagnitude < 1e-8f ? new Vector3(0f, 0f, 1f) : d.normalized;
        }

        static BasisLegSolveResult SolveOne(Vector3 hip, Vector3 knee, Vector3 foot, Vector3 target, Vector3 hint, Vector3 bendNormal)
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
            input.HintWeight = 1f;
            input.TargetOffset = Quaternion.identity;
            input.BendNormal = bendNormal;
            BasisLegSolveCore.Solve(input, out BasisLegSolveResult r);
            return r;
        }

        // Signed knee swivel around the hip->foot axis, measured from straight-forward.
        static float Swivel(Vector3 hip, Vector3 foot, Vector3 knee)
        {
            Vector3 axis = foot - hip;
            if (axis.sqrMagnitude < 1e-8f) return float.NaN;
            axis.Normalize();
            Vector3 refVec = Vector3.ProjectOnPlane(Vector3.forward, axis);
            Vector3 poleVec = Vector3.ProjectOnPlane(knee - hip, axis);
            if (refVec.sqrMagnitude < 1e-8f || poleVec.sqrMagnitude < 1e-8f) return float.NaN;
            return Vector3.SignedAngle(refVec, poleVec, axis);
        }

        static float TrackWorst(float worst, float swivel)
        {
            float a = Mathf.Abs(swivel);
            return (!float.IsNaN(a) && a > worst) ? a : worst;
        }

        static float AngleDeg(Vector3 from, Vector3 to)
        {
            float denom = Mathf.Sqrt(from.sqrMagnitude * to.sqrMagnitude);
            if (denom < 1e-5f) return 0f;
            float c = Mathf.Clamp(Vector3.Dot(from, to) / denom, -1f, 1f);
            return Mathf.Acos(c) * Mathf.Rad2Deg;
        }

        static int WriteRow(StreamWriter w, StringBuilder sb, string mode, string label, int a, int bIdx, int c,
            float dev, Vector3 hint, Vector3 target, BasisLegSolveResult r, bool reachable,
            float flex, float swivel, float fwd, bool singular, bool inverted)
        {
            sb.Clear();
            sb.Append(mode).Append(',').Append(label).Append(',');
            sb.Append(a).Append(',').Append(bIdx).Append(',').Append(c).Append(',');
            Append(sb, dev);
            Append(sb, hint.x); Append(sb, hint.y); Append(sb, hint.z);
            Append(sb, target.x); Append(sb, target.y); Append(sb, target.z);
            Append(sb, r.ReachRatio);
            sb.Append(reachable ? '1' : '0').Append(',');
            Append(sb, r.KneeSolved.x); Append(sb, r.KneeSolved.y); Append(sb, r.KneeSolved.z);
            Append(sb, flex);
            Append(sb, swivel);
            Append(sb, fwd);
            sb.Append(singular ? '1' : '0').Append(',');
            sb.Append(inverted ? '1' : '0');
            w.WriteLine(sb.ToString());
            return 1;
        }

        static void Append(StringBuilder sb, float v) { sb.Append(F(v)).Append(','); }

        static string F(float v)
        {
            if (float.IsNaN(v)) return "nan";
            return v.ToString("0.######", CultureInfo.InvariantCulture);
        }

        static Vector3 Mirror(Vector3 v, float mirror) { return new Vector3(v.x * mirror, v.y, v.z); }

        static float Lerp01(float min, float max, int steps, int idx)
        {
            if (steps <= 1) return 0.5f * (min + max);
            return Mathf.Lerp(min, max, idx / (float)(steps - 1));
        }
    }
}
