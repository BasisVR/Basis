using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using Basis.IK;

namespace Basis.IK.Debugging
{
    // Offline sweep of BasisLegSolveCore over a 3D grid of foot targets, solved with and
    // without a knee hint. One CSV row per (target, mode). Pure math, no avatar.
    public struct BasisLegIKSweepConfig
    {
        public float UpperLength;
        public float LowerLength;
        public Vector3 RestKneeDir;     // root->knee at rest
        public Vector3 RestShinDir;     // knee->foot at rest
        public Vector3 BendNormal;      // KneeBendPref (hips right)
        public bool IsLeft;

        public Vector3 MinFrac;
        public Vector3 MaxFrac;
        public Vector3Int Steps;

        public Vector3 HintDir;
        public float HintDistanceFrac;

        public static BasisLegIKSweepConfig Default()
        {
            return new BasisLegIKSweepConfig
            {
                UpperLength = 0.42f,
                LowerLength = 0.42f,
                RestKneeDir = new Vector3(0.0f, -0.97f, 0.20f),
                RestShinDir = new Vector3(0.0f, -0.98f, -0.10f),
                BendNormal = new Vector3(1.0f, 0.0f, 0.0f),
                IsLeft = false,
                MinFrac = new Vector3(-0.5f, -1.15f, -0.8f),
                MaxFrac = new Vector3(0.7f, -0.2f, 0.9f),
                Steps = new Vector3Int(27, 27, 27),
                HintDir = new Vector3(0.0f, -0.3f, 1.0f),
                HintDistanceFrac = 0.5f,
            };
        }
    }

    public struct BasisLegIKSweepSummary
    {
        public bool Ok;
        public string Path;
        public int Rows;
        public int Points;
        public int ReachablePoints;
        public float MeanSwivelShiftDeg;
        public float MaxSwivelShiftDeg;
        public float MaxFootErrorReachableM;  // worst foot miss on a reachable target (hint on); the hint must rotate ABOUT the hip->foot axis and preserve reach
        public float MeanHintFollowDeg;       // mean angle: solved knee pole vs the commanded hint pole (well-conditioned reachable poses); the knee must actually go to the hint
        public float MaxHintFollowDeg;
        public int HintFollowSamples;
        public float HintSensP999DegPerCm;    // 99.9th-percentile knee-swivel change per cm of hint jitter (widespread over-sensitivity; density-stable, mirrors the arm gate)
        public float HintSensMaxDegPerCm;
        public int HintSensSamples;
        public string Error;
    }

    public struct BasisLegStraightStanceSummary
    {
        public bool Ok;
        public string Path;
        public string Error;
        public int Samples;
        public float WorstInwardFrac;          // CLEAN: max knee-pole inward (toward-centerline) component over the standing band [-1..1]; a standing knee should be ~forward (inward<=0). >0 = bends inward.
        public float MinForwardFrac;           // CLEAN: smallest forward component; standing knees should stay forward (~+1)
        public float WorstPerturbedInwardFrac; // max inward under a tiny input perturbation (uneven feet / waist tilt)
        public float FlipSensitivity;          // max |inward_perturbed - inward_clean| from that tiny perturbation -- HIGH = a near-singular coin-flip (the asymmetric flicker)
        public float OnsetReach;               // reach where the clean knee first tips inward (NaN none)
    }

    public struct BasisLegStraightStanceTemporalSummary
    {
        public bool Ok;
        public string Path;
        public string Error;
        public int Steps;
        public float WorstStepDeg;        // worst per-frame knee-swivel jump under foot noise = the FAST FLICKER amplitude (the original "flips outward/inward")
        public int Zigzags;               // swivel-velocity sign reversals = how many times it oscillated (flicker frequency)
        public float WorstSwivelRangeDeg; // max-min knee swivel over a run = total WANDER (fast flicker + slow drift; a fade-style fix trades flicker for drift -> still trips here)
        public float AtReach;             // the near-straight reach that produced the worst
    }

    public static class BasisLegIKSweep
    {
        public const string DefaultFileName = "BasisLegIKSweep.csv";

        public static string DefaultPath()
        {
            return System.IO.Path.Combine(Application.persistentDataPath, DefaultFileName);
        }

        public static BasisLegIKSweepSummary Run(BasisLegIKSweepConfig cfg, string path)
        {
            var summary = new BasisLegIKSweepSummary { Ok = false, Path = path };

            float mirror = cfg.IsLeft ? -1f : 1f;
            float upper = Mathf.Max(1e-4f, cfg.UpperLength);
            float lower = Mathf.Max(1e-4f, cfg.LowerLength);
            float legLen = upper + lower;

            Vector3 hip = Vector3.zero;
            Vector3 kneeDir = Mirror(cfg.RestKneeDir, mirror).normalized;
            Vector3 shinDir = Mirror(cfg.RestShinDir, mirror).normalized;
            if (kneeDir.sqrMagnitude < 1e-8f) kneeDir = Vector3.down;
            if (shinDir.sqrMagnitude < 1e-8f) shinDir = Vector3.down;

            Vector3 restKnee = hip + kneeDir * upper;
            Vector3 restFoot = restKnee + shinDir * lower;

            Vector3 bendNormal = cfg.BendNormal; // NOT mirrored: the rig uses the same hips-right KneeBendPref for both legs (BasisLocalRigDriver)
            if (bendNormal.sqrMagnitude < 1e-8f) bendNormal = Vector3.right;

            Vector3 hintDir = Mirror(cfg.HintDir, mirror).normalized;
            if (hintDir.sqrMagnitude < 1e-8f) hintDir = Vector3.forward;
            Vector3 hintPos = hip + hintDir * (cfg.HintDistanceFrac * legLen);

            int sx = Mathf.Max(1, cfg.Steps.x);
            int sy = Mathf.Max(1, cfg.Steps.y);
            int sz = Mathf.Max(1, cfg.Steps.z);

            int points = 0;
            int reachable = 0;
            double swivelShiftSum = 0.0;
            float swivelShiftMax = 0f;
            int rows = 0;
            float maxFootErrReach = 0f;
            double hintFollowSum = 0.0; float hintFollowMax = 0f; int hintFollowN = 0;
            float hintSensMax = 0f;
            var sensSamples = new List<float>(4096);   // per-pose knee-swivel sensitivity to hint jitter (deg/cm); percentile-gated
            float hintJitterM = 0.005f * legLen;        // ~4mm probe; sensitivity is the local slope, magnitude-independent for a smooth solve

            try
            {
                string dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                using (var w = new StreamWriter(path, false, Encoding.UTF8))
                {
                    w.WriteLine("# BasisLegIKSweep " + System.DateTime.UtcNow.ToString("o"));
                    w.WriteLine("# side=" + (cfg.IsLeft ? "left" : "right") +
                                " upper=" + F(upper) + " lower=" + F(lower) +
                                " bendNormal=(" + F(bendNormal.x) + "," + F(bendNormal.y) + "," + F(bendNormal.z) + ")");
                    w.WriteLine("side,mode,ti,tj,tk,target_x,target_y,target_z,target_dist,leg_len," +
                                "reach_ratio,reachable,hint_on,hint_x,hint_y,hint_z," +
                                "knee_x,knee_y,knee_z,foot_x,foot_y,foot_z,foot_error," +
                                "knee_flex_deg,knee_swivel_deg,axis_source");

                    var sb = new StringBuilder(256);
                    string side = cfg.IsLeft ? "left" : "right";

                    for (int i = 0; i < sx; i++)
                    {
                        float fx = Lerp01(cfg.MinFrac.x, cfg.MaxFrac.x, sx, i) * mirror;
                        for (int j = 0; j < sy; j++)
                        {
                            float fy = Lerp01(cfg.MinFrac.y, cfg.MaxFrac.y, sy, j);
                            for (int k = 0; k < sz; k++)
                            {
                                float fz = Lerp01(cfg.MinFrac.z, cfg.MaxFrac.z, sz, k);
                                Vector3 target = hip + new Vector3(fx, fy, fz) * legLen;
                                points++;

                                BasisLegSolveResult noHint = SolveOne(hip, restKnee, restFoot, target, hintPos, bendNormal, 0f);
                                BasisLegSolveResult hint = SolveOne(hip, restKnee, restFoot, target, hintPos, bendNormal, 1f);

                                bool isReachable = noHint.ReachRatio <= 1f;
                                if (isReachable) reachable++;

                                float swivelNo = Swivel(hip, noHint.FootSolved, noHint.KneeSolved);
                                float swivelHint = Swivel(hip, hint.FootSolved, hint.KneeSolved);
                                if (isReachable && !float.IsNaN(swivelNo) && !float.IsNaN(swivelHint))
                                {
                                    float shift = Mathf.Abs(Mathf.DeltaAngle(swivelNo, swivelHint));
                                    swivelShiftSum += shift;
                                    if (shift > swivelShiftMax) swivelShiftMax = shift;
                                }

                                // --- reach preservation: the hint rotates ABOUT the hip->foot axis, so a reachable
                                // foot must still land on target (clear of the over-flex clamp at reach<~0.17 and the
                                // unreachable region). A miss here means the hint pass disturbed reach.
                                if (isReachable && hint.ReachRatio >= 0.5f && hint.ReachRatio <= 0.97f && hint.FootError > maxFootErrReach)
                                    maxFootErrReach = hint.FootError;

                                // Well-conditioned bent pose, clear of the pole-axis blend zones (near-extension >0.9
                                // and the pole-colinear cone) where the solver intentionally leaves the hint for BendNormal.
                                bool wellCond = isReachable && hint.ReachRatio >= 0.5f && hint.ReachRatio <= 0.9f && hint.KneeAngleDeg < 165f;
                                if (wellCond && !float.IsNaN(swivelHint))
                                {
                                    // Hint follow: the solved knee pole must sit where the hint asked (same hip->foot axis).
                                    float hintSw = Swivel(hip, hint.FootSolved, hintPos);
                                    if (!float.IsNaN(hintSw))
                                    {
                                        float follow = Mathf.Abs(Mathf.DeltaAngle(hintSw, swivelHint));
                                        hintFollowSum += follow; hintFollowN++;
                                        if (follow > hintFollowMax) hintFollowMax = follow;
                                    }

                                    // Hint sensitivity: jitter the hint a few mm in each axis; the knee swivel must not
                                    // whip (deg/cm). A shaky knee tracker over-rotating the pole shows up as widespread high sens.
                                    float maxd = 0f;
                                    for (int ax = 0; ax < 3; ax++)
                                    {
                                        Vector3 d = ax == 0 ? new Vector3(hintJitterM, 0f, 0f)
                                                  : ax == 1 ? new Vector3(0f, hintJitterM, 0f)
                                                            : new Vector3(0f, 0f, hintJitterM);
                                        BasisLegSolveResult pr = SolveOne(hip, restKnee, restFoot, target, hintPos + d, bendNormal, 1f);
                                        float ps = Swivel(hip, pr.FootSolved, pr.KneeSolved);
                                        if (!float.IsNaN(ps)) { float dd = Mathf.Abs(Mathf.DeltaAngle(swivelHint, ps)); if (dd > maxd) maxd = dd; }
                                    }
                                    float sens = maxd / (hintJitterM * 100f); // deg per cm
                                    sensSamples.Add(sens);
                                    if (sens > hintSensMax) hintSensMax = sens;
                                }

                                rows += WriteRow(w, sb, side, "nohint", i, j, k, target, legLen, isReachable, false, Vector3.zero, noHint, swivelNo);
                                rows += WriteRow(w, sb, side, "hint", i, j, k, target, legLen, isReachable, true, hintPos, hint, swivelHint);
                            }
                        }
                    }
                }

                summary.Ok = true;
                summary.Rows = rows;
                summary.Points = points;
                summary.ReachablePoints = reachable;
                summary.MeanSwivelShiftDeg = reachable > 0 ? (float)(swivelShiftSum / reachable) : 0f;
                summary.MaxSwivelShiftDeg = swivelShiftMax;
                summary.MaxFootErrorReachableM = maxFootErrReach;
                summary.MeanHintFollowDeg = hintFollowN > 0 ? (float)(hintFollowSum / hintFollowN) : 0f;
                summary.MaxHintFollowDeg = hintFollowMax;
                summary.HintFollowSamples = hintFollowN;
                summary.HintSensP999DegPerCm = Percentile(sensSamples, 0.999f);
                summary.HintSensMaxDegPerCm = hintSensMax;
                summary.HintSensSamples = sensSamples.Count;
            }
            catch (System.Exception e)
            {
                summary.Ok = false;
                summary.Error = e.Message;
            }

            return summary;
        }

        // q-th percentile (q in [0,1]) of an unsorted sample list; sorts a copy. Empty -> 0. Mirrors the arm
        // sweep's percentile gate: the raw MAX chases pole-collapse boundary outliers that sharpen with grid
        // density (climbs forever), so widespread sensitivity is gated on the percentile, which is density-stable.
        static float Percentile(List<float> xs, float q)
        {
            if (xs == null || xs.Count == 0) return 0f;
            var a = xs.ToArray();
            System.Array.Sort(a);
            int idx = Mathf.Clamp(Mathf.CeilToInt(q * a.Length) - 1, 0, a.Length - 1);
            return a[idx];
        }

        public struct BasisLegIKTrajectorySummary
        {
            public bool Ok;
            public string Path;
            public BasisTrajectoryResult[] Results;
            public float WorstPopDeg;
            public float WorstRoughDeg;
            public float WorstKneeJitterM;        // worst |noisy knee - clean knee| (m), temporal foot-noise run (incl. singular poses)
            public float WorstKneeJitterWellCondM; // same but EXCLUDING kinematic-singular targets -- the typical-pose jitter (singular jitter is unavoidable and upstream-filtered live)
            public float WorstSwivelRoundTripDeg; // worst "out and back" knee-pole swing on smooth motion (the twist/untwist); see MaxRoundTripDeg
            public string WorstRoundTripPath;     // which path produced it
            public string Error;
        }

        // Mirrors the arm trajectory/temporal tests for the knee (same two-bone-pole-hint structure). Catches
        // knee pole flips (pop on smooth motion) and -- the temporal run -- whether foot-target noise gets
        // amplified into knee jitter the way the elbow did. The leg uses a FIXED bend-normal hint, so we
        // expect far less than the arm; this proves it rather than assuming.
        //
        // It also measures the POLE ROUND-TRIP (WorstSwivelRoundTripDeg): with both a knee hint and a moving
        // foot target present, a smooth foot path can make the knee pole swing out and rotate BACK to where it
        // was -- the "knee twists then untwists" artifact -- as the solver's pole-axis blends (pole-colinear /
        // near-extension to the bend-normal) ramp in and out. That excursion can spread over many frames so
        // each per-step delta is small and WorstPopDeg misses it; the round-trip metric catches the out-and-back.
        public static BasisLegIKTrajectorySummary RunTrajectories(BasisLegIKSweepConfig cfg, float noise, float dt, bool temporal, string path)
        {
            var summary = new BasisLegIKTrajectorySummary { Ok = false, Path = path };
            float mirror = cfg.IsLeft ? -1f : 1f;
            float upper = Mathf.Max(1e-4f, cfg.UpperLength);
            float lower = Mathf.Max(1e-4f, cfg.LowerLength);
            float legLen = upper + lower;
            Vector3 hip = Vector3.zero;
            Vector3 kneeDir = Mirror(cfg.RestKneeDir, mirror).normalized;
            Vector3 shinDir = Mirror(cfg.RestShinDir, mirror).normalized;
            if (kneeDir.sqrMagnitude < 1e-8f) kneeDir = Vector3.down;
            if (shinDir.sqrMagnitude < 1e-8f) shinDir = Vector3.down;
            Vector3 restKnee = hip + kneeDir * upper;
            Vector3 restFoot = restKnee + shinDir * lower;
            Vector3 bendNormal = cfg.BendNormal; // NOT mirrored: the rig uses the same hips-right KneeBendPref for both legs (BasisLocalRigDriver)
            if (bendNormal.sqrMagnitude < 1e-8f) bendNormal = Vector3.right;
            Vector3 hintDir = Mirror(cfg.HintDir, mirror).normalized;
            if (hintDir.sqrMagnitude < 1e-8f) hintDir = Vector3.forward;
            Vector3 hintPos = hip + hintDir * (cfg.HintDistanceFrac * legLen);

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
                bool IsSingular(Vector3 t) { float rr = (t - hip).magnitude / legLen; return rr < 0.35f || rr > 0.97f; }

                var results = new BasisTrajectoryResult[names.Length];
                float worstPop = 0f, worstRough = 0f, worstJitterM = 0f, worstJitterWC = 0f, worstExcursion = 0f;
                string worstExcursionPath = "none";
                using (var w = new StreamWriter(path, false, Encoding.UTF8))
                {
                    w.WriteLine("# BasisLegIK" + (temporal ? "Temporal" : "Trajectory") + " " + System.DateTime.UtcNow.ToString("o") +
                                " side=" + (cfg.IsLeft ? "left" : "right") + " noise=" + F(noise) + " dt=" + F(dt));
                    w.WriteLine("path,step,target_x,target_y,target_z,knee_swivel_deg,knee_x,knee_y,knee_z,jitter_m");
                    var sb = new StringBuilder(160);
                    for (int pi = 0; pi < names.Length; pi++)
                    {
                        Vector3[] pts = paths[pi];
                        Vector3 nKnee = restKnee, nFoot = restFoot, cKnee = restKnee, cFoot = restFoot;
                        var rng = new System.Random(7000 + pi);
                        float maxJump = 0f, prev = float.NaN, maxJitter = 0f, maxJitterWC = 0f;
                        double roughSum = 0.0; int roughN = 0, pops = 0; float sdPrev = float.NaN;
                        var swClean = new float[pts.Length];  // clean (noise-free) knee swivel per step, for the round-trip metric
                        var swValid = new bool[pts.Length];   // false at NaN / kinematic-singular targets (excluded from the round-trip)
                        for (int s = 0; s < pts.Length; s++)
                        {
                            Vector3 cleanTgt = pts[s];
                            Vector3 noisyTgt = cleanTgt;
                            if (noise > 0f)
                            {
                                noisyTgt += new Vector3((float)(rng.NextDouble() * 2.0 - 1.0),
                                    (float)(rng.NextDouble() * 2.0 - 1.0), (float)(rng.NextDouble() * 2.0 - 1.0)) * noise;
                            }
                            Vector3 kneeOut, footOut, kneeClean, footClean;
                            if (temporal)
                            {
                                BasisLegSolveResult cr = SolveOne(hip, cKnee, cFoot, cleanTgt, hintPos, bendNormal, 1f);
                                cKnee = cr.KneeSolved; cFoot = cr.FootSolved;
                                BasisLegSolveResult nr = SolveOne(hip, nKnee, nFoot, noisyTgt, hintPos, bendNormal, 1f);
                                nKnee = nr.KneeSolved; nFoot = nr.FootSolved;
                                kneeOut = nr.KneeSolved; footOut = nr.FootSolved;
                                kneeClean = cr.KneeSolved; footClean = cr.FootSolved;
                                float jit = (nKnee - cKnee).magnitude;
                                if (jit > maxJitter) maxJitter = jit;
                                if (!IsSingular(cleanTgt) && jit > maxJitterWC) maxJitterWC = jit;
                            }
                            else
                            {
                                BasisLegSolveResult r = SolveOne(hip, restKnee, restFoot, noisyTgt, hintPos, bendNormal, 1f);
                                kneeOut = r.KneeSolved; footOut = r.FootSolved;
                                // The round-trip is a clean-motion property; if this run injects target noise,
                                // solve the noise-free target separately for the swivel series (else reuse it).
                                if (noise > 0f)
                                {
                                    BasisLegSolveResult rc = SolveOne(hip, restKnee, restFoot, cleanTgt, hintPos, bendNormal, 1f);
                                    kneeClean = rc.KneeSolved; footClean = rc.FootSolved;
                                }
                                else { kneeClean = r.KneeSolved; footClean = r.FootSolved; }
                            }
                            float sw = Swivel(hip, footOut, kneeOut);
                            swClean[s] = Swivel(hip, footClean, kneeClean);
                            swValid[s] = !float.IsNaN(swClean[s]) && !IsSingular(cleanTgt);
                            sb.Clear();
                            sb.Append(names[pi]).Append(',').Append(s).Append(',');
                            Append(sb, cleanTgt.x); Append(sb, cleanTgt.y); Append(sb, cleanTgt.z);
                            Append(sb, sw);
                            Append(sb, kneeOut.x); Append(sb, kneeOut.y); sb.Append(F(kneeOut.z)).Append(',').Append(F(maxJitter));
                            w.WriteLine(sb.ToString());

                            bool sing = IsSingular(cleanTgt) || (s > 0 && IsSingular(pts[s - 1]));
                            if (!float.IsNaN(sw) && !float.IsNaN(prev) && !sing)
                            {
                                float sd = Mathf.DeltaAngle(prev, sw); float d = Mathf.Abs(sd);
                                if (d > maxJump) maxJump = d;
                                if (d > 8f) pops++;
                                if (!float.IsNaN(sdPrev)) { roughSum += Mathf.Abs(sd - sdPrev); roughN++; }
                                sdPrev = sd;
                            }
                            else sdPrev = float.NaN;
                            prev = sw;
                        }
                        results[pi] = new BasisTrajectoryResult
                        {
                            Name = names[pi], Steps = pts.Length, CleanMaxJumpDeg = maxJump, Pops = pops,
                            CleanRoughDeg = roughN > 0 ? (float)(roughSum / roughN) : 0f,
                        };
                        if (maxJump > worstPop) worstPop = maxJump;
                        if (results[pi].CleanRoughDeg > worstRough) worstRough = results[pi].CleanRoughDeg;
                        if (maxJitter > worstJitterM) worstJitterM = maxJitter;
                        if (maxJitterWC > worstJitterWC) worstJitterWC = maxJitterWC;
                        float excursion = MaxRoundTripDeg(swClean, swValid, pts.Length);
                        if (excursion > worstExcursion) { worstExcursion = excursion; worstExcursionPath = names[pi]; }
                    }
                }
                summary.Ok = true; summary.Results = results; summary.WorstPopDeg = worstPop;
                summary.WorstRoughDeg = worstRough; summary.WorstKneeJitterM = worstJitterM;
                summary.WorstKneeJitterWellCondM = worstJitterWC;
                summary.WorstSwivelRoundTripDeg = worstExcursion; summary.WorstRoundTripPath = worstExcursionPath;
            }
            catch (System.Exception e) { summary.Ok = false; summary.Error = e.Message; }
            return summary;
        }

        public const string StraightStanceFileName = "BasisLegStraightStance.csv";
        public static string StraightStancePath() => System.IO.Path.Combine(Application.persistentDataPath, StraightStanceFileName);

        // "Standing straight up": near-full-extension, near-vertical leg -- the user's report (knees bend INWARD,
        // asymmetrically). This is the singular region the other gates EXCLUDE, so nothing caught it. Here we do
        // NOT exclude it: drive the standing pose (tracked foot ~under the hip, the foot-driver forward knee hint)
        // across the reach band where the solver's extension blend hands the bend axis to BendNormal (hipsRight),
        // and measure the knee pole's INWARD component -- both CLEAN and under a TINY input perturbation (uneven
        // feet / a few degrees of waist roll+yaw that tilt hipsRight). A standing knee must point forward; a high
        // FlipSensitivity = a near-singular coin-flip, which is exactly the "one side, can't tell" asymmetry.
        public static BasisLegStraightStanceSummary RunStraightStance(BasisLegIKSweepConfig cfg, string path)
        {
            var summary = new BasisLegStraightStanceSummary { Ok = false, Path = path };
            float mirror = cfg.IsLeft ? -1f : 1f;
            float upper = Mathf.Max(1e-4f, cfg.UpperLength);
            float lower = Mathf.Max(1e-4f, cfg.LowerLength);
            float legLen = upper + lower;
            Vector3 hip = Vector3.zero;
            Vector3 kneeDir = Mirror(cfg.RestKneeDir, mirror).normalized;
            Vector3 shinDir = Mirror(cfg.RestShinDir, mirror).normalized;
            if (kneeDir.sqrMagnitude < 1e-8f) kneeDir = Vector3.down;
            if (shinDir.sqrMagnitude < 1e-8f) shinDir = Vector3.down;
            Vector3 restKnee = hip + kneeDir * upper;
            Vector3 restFoot = restKnee + shinDir * lower;
            Vector3 bendNormal = cfg.BendNormal; // hipsRight, NOT mirrored (same as the rig)
            if (bendNormal.sqrMagnitude < 1e-8f) bendNormal = Vector3.right;

            // inward = toward the body centerline for THIS leg (right leg's outward is +X, so inward is -X; mirrored for left)
            Vector3 inwardDir = (-mirror) * Vector3.right;

            const int steps = 48;
            const float reachLo = 0.86f, reachHi = 0.995f; // standing band: above ~0.9 the extension blend engages, near 1.0 = locked
            const float footOutFrac = 0.06f;               // foot slightly to its own side (stance)
            const float footFwdFrac = 0.05f;               // foot slightly forward of the hip

            float worstInward = -1f, minForward = 1f, worstPertInward = -1f, flipSens = 0f, onset = float.NaN;
            int samples = 0;

            try
            {
                string dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                using (var w = new StreamWriter(path, false, Encoding.UTF8))
                {
                    w.WriteLine("# BasisLegStraightStance " + System.DateTime.UtcNow.ToString("o") +
                                " side=" + (cfg.IsLeft ? "left" : "right") + " upper=" + F(upper) + " lower=" + F(lower));
                    w.WriteLine("reach,interior_deg,inward_frac,forward_frac,pert_worst_inward,flip_sens");
                    var sb = new StringBuilder(128);

                    Vector3[] footPerts =
                    {
                        new Vector3(0.004f * legLen, 0f, 0f), new Vector3(-0.004f * legLen, 0f, 0f),
                        new Vector3(0f, 0f, 0.004f * legLen), new Vector3(0f, 0f, -0.004f * legLen),
                    };
                    Quaternion[] bnPerts =
                    {
                        Quaternion.AngleAxis(3f, Vector3.forward), Quaternion.AngleAxis(-3f, Vector3.forward),
                        Quaternion.AngleAxis(3f, Vector3.up), Quaternion.AngleAxis(-3f, Vector3.up),
                    };

                    for (int s = 0; s < steps; s++)
                    {
                        float r = Mathf.Lerp(reachLo, reachHi, s / (float)(steps - 1));
                        Vector3 footDir = new Vector3(footOutFrac * mirror, -1f, footFwdFrac).normalized;
                        Vector3 foot = hip + footDir * (r * legLen);
                        Vector3 hint = (hip + foot) * 0.5f + Vector3.forward * (upper * 0.4f) + (mirror * Vector3.right) * (0.01f * legLen);

                        float inwardClean = SolveInward(hip, restKnee, restFoot, foot, hint, bendNormal, inwardDir, out float fwdClean, out float interior);
                        samples++;
                        if (inwardClean > worstInward) worstInward = inwardClean;
                        if (fwdClean < minForward) minForward = fwdClean;
                        if (inwardClean > 0.2f && float.IsNaN(onset)) onset = r;

                        // Tiny perturbations: uneven foot (lateral / fwd-back) + a few degrees of waist roll/yaw tilting hipsRight.
                        float pertWorst = inwardClean;
                        for (int fp = 0; fp < footPerts.Length; fp++)
                        {
                            Vector3 foot2 = foot + footPerts[fp];
                            Vector3 hint2 = (hip + foot2) * 0.5f + Vector3.forward * (upper * 0.4f) + (mirror * Vector3.right) * (0.01f * legLen);
                            float inw = SolveInward(hip, restKnee, restFoot, foot2, hint2, bendNormal, inwardDir, out _, out _);
                            if (inw > pertWorst) pertWorst = inw;
                        }
                        for (int bp = 0; bp < bnPerts.Length; bp++)
                        {
                            Vector3 bn2 = bnPerts[bp] * bendNormal;
                            float inw = SolveInward(hip, restKnee, restFoot, foot, hint, bn2, inwardDir, out _, out _);
                            if (inw > pertWorst) pertWorst = inw;
                        }
                        if (pertWorst > worstPertInward) worstPertInward = pertWorst;
                        float sens = pertWorst - inwardClean;
                        if (sens > flipSens) flipSens = sens;

                        sb.Clear();
                        Append(sb, r); Append(sb, interior); Append(sb, inwardClean); Append(sb, fwdClean); Append(sb, pertWorst); sb.Append(F(sens));
                        w.WriteLine(sb.ToString());
                    }
                }
                summary.Ok = true; summary.Samples = samples;
                summary.WorstInwardFrac = worstInward; summary.MinForwardFrac = minForward;
                summary.WorstPerturbedInwardFrac = worstPertInward; summary.FlipSensitivity = flipSens;
                summary.OnsetReach = onset;
            }
            catch (System.Exception e) { summary.Ok = false; summary.Error = e.Message; }
            return summary;
        }

        public const string StraightStanceTemporalFileName = "BasisLegStraightStanceTemporal.csv";
        public static string StraightStanceTemporalPath() => System.IO.Path.Combine(Application.persistentDataPath, StraightStanceTemporalFileName);

        // STATEFUL straight-stance flicker test -- the honest model of the user's "standing legs flip outward/
        // inward" report, which the stateless RunStraightStance can't see (it re-seeds forward each step, so a
        // fix that merely LETS GO of the knee looks clean there -- exactly how the hint-fade passed it). Hold the
        // foot at a near-straight reach, add per-frame foot-tracker noise, feed the previous frame's knee/foot
        // back in (like the live rig), and watch the knee swivel:
        //   WorstStepDeg        = fast per-frame oscillation (the visible flicker)
        //   WorstSwivelRangeDeg = total wander over the run (flicker AND slow drift -- a fade-style fix that
        //                         stops the flicker by un-anchoring the knee shows up here as drift, not a pass)
        public static BasisLegStraightStanceTemporalSummary RunStraightStanceTemporal(BasisLegIKSweepConfig cfg, float noise, string path)
        {
            var summary = new BasisLegStraightStanceTemporalSummary { Ok = false, Path = path };
            float mirror = cfg.IsLeft ? -1f : 1f;
            float upper = Mathf.Max(1e-4f, cfg.UpperLength);
            float lower = Mathf.Max(1e-4f, cfg.LowerLength);
            float legLen = upper + lower;
            Vector3 hip = Vector3.zero;
            Vector3 kneeDir = Mirror(cfg.RestKneeDir, mirror).normalized;
            Vector3 shinDir = Mirror(cfg.RestShinDir, mirror).normalized;
            if (kneeDir.sqrMagnitude < 1e-8f) kneeDir = Vector3.down;
            if (shinDir.sqrMagnitude < 1e-8f) shinDir = Vector3.down;
            Vector3 restKnee = hip + kneeDir * upper;
            Vector3 restFoot = restKnee + shinDir * lower;
            Vector3 bendNormal = cfg.BendNormal;
            if (bendNormal.sqrMagnitude < 1e-8f) bendNormal = Vector3.right;

            float[] reaches = { 0.95f, 0.965f, 0.98f, 0.99f };
            const int frames = 200;

            float worstStep = 0f, worstRange = 0f, atReach = 0f;
            int worstZig = 0, totalSteps = 0;

            try
            {
                string dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                using (var w = new StreamWriter(path, false, Encoding.UTF8))
                {
                    w.WriteLine("# BasisLegStraightStanceTemporal " + System.DateTime.UtcNow.ToString("o") +
                                " side=" + (cfg.IsLeft ? "left" : "right") + " noise=" + F(noise));
                    w.WriteLine("reach,frame,foot_x,foot_y,foot_z,knee_swivel_deg,step_deg");
                    var sb = new StringBuilder(128);

                    for (int ri = 0; ri < reaches.Length; ri++)
                    {
                        float r = reaches[ri];
                        Vector3 footDir = new Vector3(0.06f * mirror, -1f, 0.05f).normalized;
                        Vector3 baseFoot = hip + footDir * (r * legLen);
                        Vector3 knee = restKnee, foot = restFoot;
                        var rng = new System.Random(13000 + ri);
                        float prevSw = float.NaN, prevD = float.NaN, minSw = float.MaxValue, maxSw = -float.MaxValue, maxStep = 0f;
                        int zig = 0;
                        for (int s = 0; s < frames; s++)
                        {
                            Vector3 noisyFoot = baseFoot;
                            if (noise > 0f)
                            {
                                noisyFoot += new Vector3((float)(rng.NextDouble() * 2.0 - 1.0),
                                    (float)(rng.NextDouble() * 2.0 - 1.0), (float)(rng.NextDouble() * 2.0 - 1.0)) * noise;
                            }
                            // foot-driver-style knee hint, recomputed from the (noisy) foot each frame as the live rig does
                            Vector3 hint = (hip + noisyFoot) * 0.5f + Vector3.forward * (upper * 0.4f) + (mirror * Vector3.right) * (0.01f * legLen);
                            BasisLegSolveResult res = SolveOne(hip, knee, foot, noisyFoot, hint, bendNormal, 1f);
                            knee = res.KneeSolved; foot = res.FootSolved;
                            float sw = Swivel(hip, foot, knee);
                            float step = 0f;
                            if (!float.IsNaN(sw))
                            {
                                if (sw < minSw) minSw = sw;
                                if (sw > maxSw) maxSw = sw;
                                if (!float.IsNaN(prevSw))
                                {
                                    float d = Mathf.DeltaAngle(prevSw, sw);
                                    step = Mathf.Abs(d);
                                    if (step > maxStep) maxStep = step;
                                    if (!float.IsNaN(prevD) && d * prevD < 0f && step > 1f && Mathf.Abs(prevD) > 1f) zig++;
                                    prevD = d;
                                }
                                prevSw = sw;
                                totalSteps++;
                            }
                            sb.Clear();
                            Append(sb, r); sb.Append(s).Append(',');
                            Append(sb, noisyFoot.x); Append(sb, noisyFoot.y); Append(sb, noisyFoot.z);
                            Append(sb, sw); sb.Append(F(step));
                            w.WriteLine(sb.ToString());
                        }
                        float range = (maxSw > -float.MaxValue && minSw < float.MaxValue) ? (maxSw - minSw) : 0f;
                        if (maxStep > worstStep) { worstStep = maxStep; atReach = r; }
                        if (range > worstRange) worstRange = range;
                        if (zig > worstZig) worstZig = zig;
                    }
                }
                summary.Ok = true; summary.Steps = totalSteps;
                summary.WorstStepDeg = worstStep; summary.Zigzags = worstZig;
                summary.WorstSwivelRangeDeg = worstRange; summary.AtReach = atReach;
            }
            catch (System.Exception e) { summary.Ok = false; summary.Error = e.Message; }
            return summary;
        }

        // Solve the standing pose and return the knee pole's INWARD fraction (dot with inwardDir), plus its
        // forward fraction and the solved knee interior angle. Pole = knee offset perpendicular to hip->foot.
        static float SolveInward(Vector3 hip, Vector3 restKnee, Vector3 restFoot, Vector3 foot, Vector3 hint, Vector3 bendNormal, Vector3 inwardDir, out float forwardFrac, out float interiorDeg)
        {
            BasisLegSolveResult r = SolveOne(hip, restKnee, restFoot, foot, hint, bendNormal, 1f);
            interiorDeg = r.KneeAngleDeg;
            Vector3 chord = foot - hip;
            Vector3 kneeRel = r.KneeSolved - hip;
            float cs = chord.sqrMagnitude;
            Vector3 pole = cs > 1e-10f ? kneeRel - chord * (Vector3.Dot(kneeRel, chord) / cs) : kneeRel;
            if (pole.sqrMagnitude < 1e-10f) { forwardFrac = 0f; return 0f; }
            pole.Normalize();
            forwardFrac = Vector3.Dot(pole, Vector3.forward);
            return Vector3.Dot(pole, inwardDir);
        }

        static BasisLegSolveResult SolveOne(Vector3 hip, Vector3 knee, Vector3 foot, Vector3 target, Vector3 hint, Vector3 bendNormal, float hintWeight)
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

        // Largest QUICK round-trip ("out and back") knee-pole swivel excursion on one path: the pole rotates
        // away from a value and rotates BACK to it within a short window. We split the path into maximal valid
        // runs (non-NaN, non-singular target) and score each run; a fold/extension singularity breaks the run
        // rather than faking an excursion. The window matters: a slow whole-motion swivel swing (e.g. the knee
        // naturally rotating as the foot orbits the circle path) is EXPECTED kinematics, not the bug, so we only
        // count excursions that complete WITHIN ~W steps -- the user's "quickly rotating and then rotating back".
        // A monotonic rotation (the knee moving to a new pose and STAYING) scores 0. Measured on the clean
        // (noise-free) swivel, since the solver's pole-axis blends are deterministic, like WorstPopDeg.
        static float MaxRoundTripDeg(float[] sw, bool[] valid, int n)
        {
            int window = Mathf.Clamp(n / 8, 5, 30); // "quick": full out-and-back inside ~W path steps (foot speed is per full-path step)
            float worst = 0f;
            int i = 0;
            while (i < n)
            {
                if (!valid[i]) { i++; continue; }
                int j = i;
                while (j < n && valid[j]) j++;   // run [i, j)
                float p = RunProminence(sw, i, j, window);
                if (p > worst) worst = p;
                i = j;
            }
            return worst;
        }

        // Windowed local prominence over one valid run. The swivel is first UNWRAPPED via DeltaAngle (so the
        // +/-180 deg seam can't fake a jump) into a continuous signal cum[]; smooth motion never steps past
        // 180 deg between valid samples, so cum is the true accumulated pole rotation. At each sample j we look
        // only +/-W steps: a hump scores min(rise from the recent left low, fall to the near right low) and a
        // dip the mirror. min() is the part that actually came BACK; the +/-W bound is what makes it "quick" --
        // a swing that takes much longer than W to reverse barely rises within the window and scores ~0.
        // O(m*W), m,W both small.
        static float RunProminence(float[] sw, int lo, int hi, int w)
        {
            int m = hi - lo;
            if (m < 3) return 0f;

            var cum = new float[m];
            cum[0] = 0f;
            for (int k = 1; k < m; k++)
                cum[k] = cum[k - 1] + Mathf.DeltaAngle(sw[lo + k - 1], sw[lo + k]);

            float worst = 0f;
            for (int j = 0; j < m; j++)
            {
                int a = Mathf.Max(0, j - w), b = Mathf.Min(m - 1, j + w);
                float lMin = cum[j], lMax = cum[j], rMin = cum[j], rMax = cum[j];
                for (int i = a; i <= j; i++) { if (cum[i] < lMin) lMin = cum[i]; if (cum[i] > lMax) lMax = cum[i]; }
                for (int k = j; k <= b; k++) { if (cum[k] < rMin) rMin = cum[k]; if (cum[k] > rMax) rMax = cum[k]; }
                float up = Mathf.Min(cum[j] - lMin, cum[j] - rMin);   // rose into j AND falls back -> hump
                float down = Mathf.Min(lMax - cum[j], rMax - cum[j]); // dipped into j AND recovers -> valley
                float p = up > down ? up : down;
                if (p > worst) worst = p;
            }
            return worst;
        }

        static int WriteRow(StreamWriter w, StringBuilder sb, string side, string mode, int i, int j, int k,
            Vector3 target, float legLen, bool reachable, bool hintOn, Vector3 hint, BasisLegSolveResult r, float swivel)
        {
            sb.Clear();
            sb.Append(side).Append(',').Append(mode).Append(',');
            sb.Append(i).Append(',').Append(j).Append(',').Append(k).Append(',');
            Append(sb, target.x); Append(sb, target.y); Append(sb, target.z);
            Append(sb, r.TargetDistance); Append(sb, legLen);
            Append(sb, r.ReachRatio);
            sb.Append(reachable ? '1' : '0').Append(',');
            sb.Append(hintOn ? '1' : '0').Append(',');
            Append(sb, hint.x); Append(sb, hint.y); Append(sb, hint.z);
            Append(sb, r.KneeSolved.x); Append(sb, r.KneeSolved.y); Append(sb, r.KneeSolved.z);
            Append(sb, r.FootSolved.x); Append(sb, r.FootSolved.y); Append(sb, r.FootSolved.z);
            Append(sb, r.FootError);
            Append(sb, r.KneeAngleDeg);
            Append(sb, swivel);
            sb.Append(r.AxisSource);
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
