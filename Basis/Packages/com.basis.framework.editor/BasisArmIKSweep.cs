using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Collections;
using UnityEngine;
using Basis.IK;

namespace Basis.IK.Debugging
{
    // Offline sweep of BasisArmSolveCore over a 3D grid of hand targets, solved with and
    // without an elbow hint tracker. One CSV row per (target, mode). Pure math, no avatar.
    public struct BasisArmIKSweepConfig
    {
        public float UpperLength;
        public float LowerLength;
        public Vector3 RestElbowDir;    // seeds the bend plane
        public Vector3 RestForearmDir;
        public bool IsLeft;             // mirror X

        // Target grid in shoulder-local space, as fractions of total arm length.
        public Vector3 MinFrac;
        public Vector3 MaxFrac;
        public Vector3Int Steps;

        public Vector3 HintDir;
        public float HintDistanceFrac;

        public static BasisArmIKSweepConfig Default()
        {
            return new BasisArmIKSweepConfig
            {
                UpperLength = 0.28f,
                LowerLength = 0.26f,
                RestElbowDir = new Vector3(0.15f, -0.95f, 0.27f),
                RestForearmDir = new Vector3(0.0f, -0.30f, 0.95f),
                IsLeft = false,
                MinFrac = new Vector3(-0.7f, -1.1f, -0.6f),
                MaxFrac = new Vector3(1.2f, 0.7f, 1.15f),
                Steps = new Vector3Int(63, 63, 63),
                HintDir = new Vector3(0.2f, -1.0f, -0.15f),
                HintDistanceFrac = 0.5f,
            };
        }
    }

    public struct BasisArmIKSweepSummary
    {
        public bool Ok;
        public string Path;
        public int Rows;            // total CSV data rows (2 per target point)
        public int Points;          // target points swept
        public int ReachablePoints; // points with reach ratio <= 1
        public float MeanSwivelShiftDeg; // mean |swivel_hint - swivel_nohint| over reachable points
        public float MaxSwivelShiftDeg;
        public float LookupMeanAbsSwivelDeg;
        public int LookupElbowUpCount;   // reachable lookup poses whose pole points up (chicken-wing)
        public int LookupElbowFlipCount;     // forward, non-overhead reaches whose elbow flips hard UP (|swivel|>120) instead of hanging down/back
        public float LookupMeanAlignErrDeg;  // health: mean angle between solved lookup-elbow and the lookup's requested pole (0 = elbow follows it)
        public float LookupMaxAlignErrDeg;   // health: worst such angle
        public float LookupMinElbowAngleDeg; // min solved elbow flexion over reachable poses (must stay >= anatomical min)
        public float LookupMaxElbowAngleDeg; // max solved elbow flexion (must stay <= straight; no hyperextension)
        public float TrackerMeanSensDegPerCm; // elbow swivel deg per cm of tracker position error
        public float TrackerMaxSensDegPerCm;
        public float TrackerSens99DegPerCm; // 99.9th-percentile tracker sens over well-conditioned poses (density-stable; the max chases the pole-collapse singularity, which sharpens forever as the grid densifies)
        public int TrackerJitteryCount;  // reachable poses with sensitivity > 20 deg/cm
        public int TrackerFadedCount;    // reachable poses where the tracker hint is faded (reach>0.9)
        public float TrackerMeanAlignErrDeg; // mean angle between solved elbow and tracker pole (under-follow)
        public float TrackerMaxAlignErrDeg;
        public string Error;
    }

    // Summary of RunTrackerNaturalness: how far a REAL elbow tracker, strapped to the natural arm across
    // mounts / positions / stand-off radii / body sizes, drives the elbow off the no-tracker natural pose.
    public struct BasisArmTrackerNaturalnessSummary
    {
        public bool Ok;
        public string Path;
        public string Error;
        public int Combos;          // placement×size×target solves measured
        public float WorstDevDeg;   // worst |solved swivel - natural swivel| over a strapped tracker
        public float MeanDevDeg;
        public int OverCount;       // combos exceeding the naturalness threshold
        public string WorstWhere;   // descriptor of the worst combo
    }

    // Summary of RunChickenWing: with no elbow tracker, turning the controllers inward (the chicken-wing) must
    // push the derived elbow OUT toward the half-T-pose mark and HARD-CLAMP it there -- it must never cross the
    // halfway line to straight-out-to-the-side. Measured in BasisElbowFlareCore's own swing-plane swivel basis.
    public struct BasisArmChickenWingSummary
    {
        public bool Ok;
        public string Path;
        public string Error;
        public int ReachablePoints;        // chicken-wing target poses swept (reach <= 1)
        public float CapDeg;               // the configured half-T-pose cap, echoed
        public float MaxFullFlareSwivelDeg;// worst |outward elbow swivel| at FULL engagement (must stay <= cap)
        public float WorstOverCapDeg;      // max(0, MaxFullFlareSwivelDeg - cap): how far the worst full flare crosses the cap
        public float MaxRegressDeg;        // worst engagement-0 change vs the plain lookup (must be ~0 -- a no-op)
        public float MeanPushDeg;          // mean outward push (engage 1 - engage 0) for poses naturally inside the cap
        public int PushedOutCount;         // those poses the full flare actually moved further out
        public int PushSamples;            // denominator for MeanPushDeg
    }

    public static class BasisArmIKSweep
    {
        public const string DefaultFileName = "BasisArmIKSweep.csv";

        public static string DefaultPath()
        {
            return System.IO.Path.Combine(Application.persistentDataPath, DefaultFileName);
        }

        public const string TrackerNaturalnessFileName = "BasisArmTrackerNaturalness.csv";
        public static string TrackerNaturalnessDefaultPath() => System.IO.Path.Combine(Application.persistentDataPath, TrackerNaturalnessFileName);

        // Offline check that a REAL elbow tracker yields a NATURAL elbow across mounts (upper-arm vs forearm),
        // positions along the bone, stand-off radii, and body sizes. A tracker strapped rigidly to the natural
        // (lookup, no-tracker) arm must reproduce that pose -- it must never make the bend LESS natural than no
        // tracker. Solves the tracker case with HintIsTracker=true so it exercises the tracker-trust window in
        // BasisArmSolveCore. The offline twin of
        // BasisElbowDirectionTests.Elbow_TrackerReproducesNaturalBend_AcrossPlacementsAndSizes.
        public static BasisArmTrackerNaturalnessSummary RunTrackerNaturalness(BasisArmIKSweepConfig cfg, string path)
        {
            var s = new BasisArmTrackerNaturalnessSummary { Ok = false, Path = path };
            float mirror = cfg.IsLeft ? -1f : 1f;
            bool isLeft = cfg.IsLeft;
            Vector3 shoulder = Vector3.zero;
            Vector3 restElbowDir = Mirror(cfg.RestElbowDir, mirror).normalized;
            Vector3 restForearmDir = Mirror(cfg.RestForearmDir, mirror).normalized;
            if (restElbowDir.sqrMagnitude < 1e-8f) restElbowDir = Vector3.down;
            if (restForearmDir.sqrMagnitude < 1e-8f) restForearmDir = Vector3.forward;

            NativeArray<Vector3> table = default;
            try
            {
                string dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                table = new NativeArray<Vector3>(BasisArmBendLookup.GenerateDefaultTable(), Allocator.Persistent);

                int combos = 0, over = 0;
                double devSum = 0.0;
                float worst = 0f; string worstWhere = "";

                var sizes = new (float up, float lo)[] { (0.22f, 0.20f), (0.28f, 0.26f), (0.34f, 0.30f), (0.32f, 0.22f) };
                var dirs = new (Vector3 d, float r)[]
                {
                    (new Vector3(0.05f, -0.05f, 1f), 0.82f),    // straight forward
                    (new Vector3(0.55f, -0.10f, 0.70f), 0.85f), // forward + out to the side
                    (new Vector3(0.10f, 0.40f, 0.80f), 0.85f),  // up-forward (raised reach)
                    (new Vector3(-0.35f, -0.10f, 0.65f), 0.80f),// across the body
                    (new Vector3(0.10f, -0.55f, 0.70f), 0.82f), // low-forward
                };
                float[] fracs = { 0.4f, 0.5f, 0.6f };       // realistic mid-bone mounts
                float[] radii = { 0.05f, 0.07f, 0.09f };    // realistic strap stand-off

                using (var w = new StreamWriter(path, false, Encoding.UTF8))
                {
                    w.WriteLine("# BasisArmTrackerNaturalness " + System.DateTime.UtcNow.ToString("o") + " side=" + (isLeft ? "left" : "right"));
                    w.WriteLine("upper,lower,target_x,target_y,target_z,reach,mount,frac,radius_cm,nat_swivel,solved_swivel,dev_deg");
                    var sb = new StringBuilder(160);
                    foreach (var size in sizes)
                    {
                        float upper = size.up, lower = size.lo, armLen = upper + lower;
                        Vector3 restElbow = shoulder + restElbowDir * upper;
                        Vector3 restHand = restElbow + restForearmDir * lower;
                        foreach (var dr in dirs)
                        {
                            Vector3 ldir = dr.d; ldir.x *= mirror;
                            Vector3 target = shoulder + ldir.normalized * (dr.r * armLen);
                            Vector3 bend = ComputeLookupBend(table, target - shoulder, armLen, isLeft);
                            Vector3 lookupHint = shoulder + 0.5f * armLen * bend;
                            BasisArmSolveResult nat = SolveOne(shoulder, restElbow, restHand, target, lookupHint, true, false);
                            Vector3 axis = nat.HandSolved - shoulder;
                            if (axis.sqrMagnitude < 1e-8f) continue;
                            axis.Normalize();
                            Vector3 natPole = Vector3.ProjectOnPlane(nat.ElbowSolved - shoulder, axis);
                            if (natPole.sqrMagnitude < 1e-6f) continue;
                            Vector3 poleDir = natPole.normalized;
                            float natSwivel = Swivel(shoulder, nat.HandSolved, nat.ElbowSolved);
                            if (float.IsNaN(natSwivel)) continue;

                            foreach (bool upperMount in new[] { true, false })
                            foreach (float frac in fracs)
                            foreach (float radius in radii)
                            {
                                Vector3 bonePoint = upperMount
                                    ? Vector3.Lerp(shoulder, nat.ElbowSolved, frac)
                                    : Vector3.Lerp(nat.ElbowSolved, nat.HandSolved, frac);
                                Vector3 trackerPos = bonePoint + poleDir * radius;
                                BasisArmSolveResult sv = SolveOne(shoulder, restElbow, restHand, target, trackerPos, true, true);
                                float sw = Swivel(shoulder, sv.HandSolved, sv.ElbowSolved);
                                if (float.IsNaN(sw)) continue;
                                float dev = Mathf.Abs(Mathf.DeltaAngle(natSwivel, sw));
                                combos++; devSum += dev;
                                if (dev > BasisIKTestGates.ArmTrackerMaxDevDeg) over++;
                                if (dev > worst) { worst = dev; worstWhere = $"size({upper:0.00},{lower:0.00}) reach{dr.r:0.00} mount={(upperMount ? "upper" : "fore")} frac{frac:0.00} r{radius * 100f:0}cm"; }
                                sb.Clear();
                                sb.Append(F(upper)).Append(',').Append(F(lower)).Append(',');
                                sb.Append(F(target.x)).Append(',').Append(F(target.y)).Append(',').Append(F(target.z)).Append(',').Append(F(dr.r)).Append(',');
                                sb.Append(upperMount ? "upper" : "fore").Append(',').Append(F(frac)).Append(',').Append(F(radius * 100f)).Append(',');
                                sb.Append(F(natSwivel)).Append(',').Append(F(sw)).Append(',').Append(F(dev));
                                w.WriteLine(sb.ToString());
                            }
                        }
                    }
                }

                s.Ok = true;
                s.Combos = combos;
                s.WorstDevDeg = worst;
                s.MeanDevDeg = combos > 0 ? (float)(devSum / combos) : 0f;
                s.OverCount = over;
                s.WorstWhere = worstWhere;
            }
            catch (System.Exception e) { s.Ok = false; s.Error = e.Message; }
            finally { if (table.IsCreated) table.Dispose(); }
            return s;
        }

        // Chicken-wing flare check (no elbow tracker). For hand targets drawn in toward the body -- the
        // chicken-wing region -- sweep the flare engagement 0..1 and assert the derived elbow is pushed OUT but
        // HARD-CLAMPED at the half-T-pose mark (capDeg of swivel off straight-down). Drives BasisElbowFlareCore
        // with explicit engagement; the live rig derives engagement from the controller roll but the
        // clamp/push geometry is identical (the same Core), so this verifies the part the user feels.
        public static BasisArmChickenWingSummary RunChickenWing(BasisArmIKSweepConfig cfg, float capDeg, string path)
        {
            var s = new BasisArmChickenWingSummary { Ok = false, Path = path, CapDeg = capDeg };
            float mirror = cfg.IsLeft ? -1f : 1f;
            bool isLeft = cfg.IsLeft;
            float upper = Mathf.Max(1e-4f, cfg.UpperLength);
            float lower = Mathf.Max(1e-4f, cfg.LowerLength);
            float armLen = upper + lower;
            Vector3 shoulder = Vector3.zero;
            Vector3 elbowDir = Mirror(cfg.RestElbowDir, mirror).normalized;
            Vector3 forearmDir = Mirror(cfg.RestForearmDir, mirror).normalized;
            if (elbowDir.sqrMagnitude < 1e-8f) elbowDir = Vector3.down;
            if (forearmDir.sqrMagnitude < 1e-8f) forearmDir = Vector3.forward;
            Vector3 restElbow = shoulder + elbowDir * upper;
            Vector3 restHand = restElbow + forearmDir * lower;
            Vector3 outward = isLeft ? Vector3.left : Vector3.right; // away-from-body side in this identity frame
            Vector3 up = Vector3.up;

            // Chicken-wing hand targets: drawn in toward the centerline, in front, belly..chest height, moderate
            // reach (where the elbow pole is well-conditioned, so the hint is followed and the clamp is real).
            float[] fxs = { -0.35f, -0.15f, 0.05f };
            float[] fys = { -0.40f, -0.15f, 0.10f, 0.30f };
            float[] fzs = { 0.35f, 0.55f };
            float[] engages = { 0f, 0.25f, 0.5f, 0.75f, 1f };

            int reachable = 0;
            float maxFull = 0f, worstOver = 0f, maxRegress = 0f;
            double pushSum = 0.0; int pushN = 0, pushedOut = 0;

            NativeArray<Vector3> table = default;
            try
            {
                string dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                table = new NativeArray<Vector3>(BasisArmBendLookup.GenerateDefaultTable(), Allocator.Persistent);

                using (var w = new StreamWriter(path, false, Encoding.UTF8))
                {
                    w.WriteLine("# BasisArmChickenWing " + System.DateTime.UtcNow.ToString("o") + " side=" + (isLeft ? "left" : "right") + " cap=" + F(capDeg));
                    w.WriteLine("target_x,target_y,target_z,reach,engage,flared_swivel,over_cap");
                    var sb = new StringBuilder(160);

                    foreach (float fx in fxs)
                    foreach (float fy in fys)
                    foreach (float fz in fzs)
                    {
                        Vector3 target = shoulder + new Vector3(fx * mirror, fy, fz) * armLen;
                        float reach = (target - shoulder).magnitude / armLen;
                        if (reach > 1f) continue; // unreachable; skip
                        reachable++;

                        Vector3 natBend = ComputeLookupBend(table, target - shoulder, armLen, isLeft);
                        float swivelAt0 = float.NaN, swivelAt1 = float.NaN;

                        foreach (float r in engages)
                        {
                            Vector3 flared = BasisElbowFlareCore.ApplyFlare(natBend, target - shoulder, outward, up, r, capDeg);
                            Vector3 hint = shoulder + 0.5f * armLen * flared;
                            BasisArmSolveResult res = SolveOne(shoulder, restElbow, restHand, target, hint, true);
                            float sw = OutwardSwivel(shoulder, res.HandSolved, res.ElbowSolved, outward, up);

                            if (r <= 0f)
                            {
                                swivelAt0 = sw;
                                // Regression: engagement 0 must reproduce the plain (un-flared) lookup solve exactly.
                                Vector3 plainHint = shoulder + 0.5f * armLen * natBend;
                                BasisArmSolveResult plain = SolveOne(shoulder, restElbow, restHand, target, plainHint, true);
                                float swPlain = OutwardSwivel(shoulder, plain.HandSolved, plain.ElbowSolved, outward, up);
                                if (!float.IsNaN(sw) && !float.IsNaN(swPlain))
                                    maxRegress = Mathf.Max(maxRegress, Mathf.Abs(Mathf.DeltaAngle(sw, swPlain)));
                            }
                            if (r >= 1f && !float.IsNaN(sw))
                            {
                                swivelAt1 = sw;
                                float mag = Mathf.Abs(sw);
                                if (mag > maxFull) maxFull = mag;
                                if (mag - capDeg > worstOver) worstOver = mag - capDeg;
                            }

                            sb.Clear();
                            sb.Append(F(target.x)).Append(',').Append(F(target.y)).Append(',').Append(F(target.z)).Append(',');
                            sb.Append(F(reach)).Append(',').Append(F(r)).Append(',').Append(F(sw)).Append(',');
                            sb.Append(F(float.IsNaN(sw) ? float.NaN : Mathf.Max(0f, Mathf.Abs(sw) - capDeg)));
                            w.WriteLine(sb.ToString());
                        }

                        // Push-out: a pose whose natural elbow sits clearly INSIDE the cap must move further OUT
                        // (toward +cap) at full engagement. Poses already at/over the cap get pulled IN, so they
                        // are excluded from the push metric (they exercise the clamp, not the push).
                        if (!float.IsNaN(swivelAt0) && !float.IsNaN(swivelAt1) && swivelAt0 < capDeg - 5f)
                        {
                            float push = swivelAt1 - swivelAt0;
                            pushSum += push; pushN++;
                            if (push > 1f) pushedOut++;
                        }
                    }
                }

                s.Ok = true;
                s.ReachablePoints = reachable;
                s.MaxFullFlareSwivelDeg = maxFull;
                s.WorstOverCapDeg = Mathf.Max(0f, worstOver);
                s.MaxRegressDeg = maxRegress;
                s.MeanPushDeg = pushN > 0 ? (float)(pushSum / pushN) : 0f;
                s.PushedOutCount = pushedOut;
                s.PushSamples = pushN;
            }
            catch (System.Exception e) { s.Ok = false; s.Error = e.Message; }
            finally { if (table.IsCreated) table.Dispose(); }
            return s;
        }

        // Signed elbow swivel in the (down = 0 deg, outward = +90 deg) plane perpendicular to shoulder->hand:
        // + = out to the body's outward side, - = across the body, +-180 = up. Mirrors BasisElbowFlareCore's
        // basis so the clamp is measured in the exact frame it is applied.
        static float OutwardSwivel(Vector3 shoulder, Vector3 hand, Vector3 elbow, Vector3 outwardDir, Vector3 up)
        {
            Vector3 axis = hand - shoulder;
            if (axis.sqrMagnitude < 1e-8f) return float.NaN;
            axis.Normalize();
            Vector3 downPole = Vector3.ProjectOnPlane(-up, axis);
            if (downPole.sqrMagnitude < 1e-8f) return float.NaN;
            downPole.Normalize();
            Vector3 outPole = Vector3.ProjectOnPlane(outwardDir, axis);
            outPole -= downPole * Vector3.Dot(outPole, downPole);
            if (outPole.sqrMagnitude < 1e-8f) return float.NaN;
            outPole.Normalize();
            Vector3 pole = Vector3.ProjectOnPlane(elbow - shoulder, axis);
            if (pole.sqrMagnitude < 1e-8f) return float.NaN;
            return Mathf.Atan2(Vector3.Dot(pole, outPole), Vector3.Dot(pole, downPole)) * Mathf.Rad2Deg;
        }

        public static BasisArmIKSweepSummary Run(BasisArmIKSweepConfig cfg, string path)
        {
            var summary = new BasisArmIKSweepSummary { Ok = false, Path = path };

            float mirror = cfg.IsLeft ? -1f : 1f;
            float upper = Mathf.Max(1e-4f, cfg.UpperLength);
            float lower = Mathf.Max(1e-4f, cfg.LowerLength);
            float armLen = upper + lower;

            Vector3 shoulder = Vector3.zero;
            Vector3 elbowDir = Mirror(cfg.RestElbowDir, mirror).normalized;
            Vector3 forearmDir = Mirror(cfg.RestForearmDir, mirror).normalized;
            if (elbowDir.sqrMagnitude < 1e-8f) elbowDir = Vector3.down;
            if (forearmDir.sqrMagnitude < 1e-8f) forearmDir = Vector3.forward;

            Vector3 restElbow = shoulder + elbowDir * upper;
            Vector3 restHand = restElbow + forearmDir * lower;

            Vector3 hintDir = Mirror(cfg.HintDir, mirror).normalized;
            if (hintDir.sqrMagnitude < 1e-8f) hintDir = Vector3.down;
            Vector3 hintPos = shoulder + hintDir * (cfg.HintDistanceFrac * armLen);

            int sx = Mathf.Max(1, cfg.Steps.x);
            int sy = Mathf.Max(1, cfg.Steps.y);
            int sz = Mathf.Max(1, cfg.Steps.z);

            int points = 0;
            int reachable = 0;
            double swivelShiftSum = 0.0;
            float swivelShiftMax = 0f;
            double lookupAbsSwivelSum = 0.0;
            int lookupElbowUp = 0;
            int lookupElbowFlip = 0;          // reachable lookup poses whose elbow ends up off the requested pole
            double lookupAlignSum = 0.0;
            int lookupAlignN = 0;
            float lookupAlignMax = 0f;
            float lookupMinElbow = 999f, lookupMaxElbow = -999f;
            double trackerSensSum = 0.0;
            int trackerSensN = 0;
            float trackerSensMax = 0f;
            var trackerSensValues = new List<float>(); // well-conditioned tracker sens, for a density-stable percentile
            int trackerJittery = 0;   // tracker poses with sensitivity > 20 deg/cm
            int trackerFaded = 0;     // tracker poses where the hint is partially/fully faded out
            double trackerAlignSum = 0.0;
            int trackerAlignN = 0;
            float trackerAlignMax = 0f;
            int rows = 0;

            NativeArray<Vector3> table = default;
            try
            {
                string dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                table = new NativeArray<Vector3>(BasisArmBendLookup.GenerateDefaultTable(), Allocator.Persistent); // Persistent (not Temp): the sweep may run on a background thread (parallel Run All)

                using (var w = new StreamWriter(path, false, Encoding.UTF8))
                {
                    w.WriteLine("# BasisArmIKSweep " + System.DateTime.UtcNow.ToString("o"));
                    w.WriteLine("# side=" + (cfg.IsLeft ? "left" : "right") +
                                " upper=" + F(upper) + " lower=" + F(lower) +
                                " hint=(" + F(hintPos.x) + "," + F(hintPos.y) + "," + F(hintPos.z) + ")");
                    w.WriteLine("side,mode,ti,tj,tk,target_x,target_y,target_z,target_dist,arm_len," +
                                "reach_ratio,reachable,hint_on,hint_x,hint_y,hint_z," +
                                "elbow_x,elbow_y,elbow_z,hand_x,hand_y,hand_z,hand_error," +
                                "elbow_flex_deg,swivel_deg,axis_source,hint_fade,bend_x,bend_y,bend_z," +
                                "hint_proj,arm_proj,tracker_sens_deg_per_cm,tracker_align_err_deg");

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
                                Vector3 target = shoulder + new Vector3(fx, fy, fz) * armLen;
                                points++;

                                BasisArmSolveResult noHint = SolveOne(shoulder, restElbow, restHand, target, hintPos, false);
                                BasisArmSolveResult hint = SolveOne(shoulder, restElbow, restHand, target, hintPos, true, true);

                                Vector3 lookupBend = ComputeLookupBend(table, target - shoulder, armLen, cfg.IsLeft);
                                Vector3 lookupHintPos = shoulder + 0.5f * armLen * lookupBend;
                                BasisArmSolveResult lookup = SolveOne(shoulder, restElbow, restHand, target, lookupHintPos, true);

                                bool isReachable = noHint.ReachRatio <= 1f;
                                if (isReachable) reachable++;

                                float swivelNo = Swivel(shoulder, noHint.HandSolved, noHint.ElbowSolved);
                                float swivelHint = Swivel(shoulder, hint.HandSolved, hint.ElbowSolved);
                                float swivelLookup = Swivel(shoulder, lookup.HandSolved, lookup.ElbowSolved);

                                // Tracker jitter: how far the elbow swings per cm of tracker position error.
                                float sensHint = TrackerSensitivity(shoulder, restElbow, restHand, target, hintPos, swivelHint, true);
                                float sensLookup = TrackerSensitivity(shoulder, restElbow, restHand, target, lookupHintPos, swivelLookup, false);
                                // Tracker follow: angle between solved elbow pole and where the tracker says (0 = perfect follow).
                                float alignHint = TrackerAlignErr(shoulder, target, hint.ElbowSolved, hintPos);
                                float alignLookup = TrackerAlignErr(shoulder, target, lookup.ElbowSolved, lookupHintPos);

                                if (isReachable && !float.IsNaN(swivelNo) && !float.IsNaN(swivelHint))
                                {
                                    float shift = Mathf.Abs(Mathf.DeltaAngle(swivelNo, swivelHint));
                                    swivelShiftSum += shift;
                                    if (shift > swivelShiftMax) swivelShiftMax = shift;
                                }
                                if (isReachable)
                                {
                                    if (!float.IsNaN(swivelLookup)) lookupAbsSwivelSum += Mathf.Abs(swivelLookup);
                                    if (lookupBend.y > 0.2f) lookupElbowUp++;
                                    // Health: mean/max angle the solved elbow sits off the requested lookup pole.
                                    if (!float.IsNaN(alignLookup))
                                    {
                                        lookupAlignSum += alignLookup;
                                        lookupAlignN++;
                                        if (alignLookup > lookupAlignMax) lookupAlignMax = alignLookup;
                                    }
                                    // The headline bug: on a forward, non-overhead, EXTENDED reach the elbow
                                    // swivels hard UP (|swivel|>120) instead of hanging down/back -- "elbow in
                                    // front / wrong side". Folded reaches (reach<0.55, hand near the body) can
                                    // legitimately raise the elbow, so they are excluded -- the fixed lookup is
                                    // clean (0) on the extended region at every density. (align-vs-hint over-counts:
                                    // a correct down elbow still reads >90 from the down-BACK hint.)
                                    bool fwdExtended = (target.z - shoulder.z) > 0.2f * armLen
                                        && (target.y - shoulder.y) < 0.4f * armLen
                                        && lookup.ReachRatio > 0.55f;
                                    if (fwdExtended && !float.IsNaN(swivelLookup) && Mathf.Abs(swivelLookup) > 120f) lookupElbowFlip++;
                                    // Anatomical elbow flexion: the solved angle must stay in human range.
                                    if (lookup.ElbowAngleDeg < lookupMinElbow) lookupMinElbow = lookup.ElbowAngleDeg;
                                    if (lookup.ElbowAngleDeg > lookupMaxElbow) lookupMaxElbow = lookup.ElbowAngleDeg;
                                    if (!float.IsNaN(sensHint))
                                    {
                                        trackerSensSum += sensHint;
                                        trackerSensN++;
                                        // The MAX is singularity-dominated: where the elbow pole collapses
                                        // (near-straight arm / hint along the arm axis) the swivel-per-cm is
                                        // geometrically unbounded, so a denser grid keeps finding higher spikes.
                                        // Exclude those from the max/jittery -- like the trajectory scanner's
                                        // isSingular -- so the gate tracks well-conditioned jitter, not geometry.
                                        bool poleWellConditioned = hint.ArmProjMag > 0.12f * armLen && hint.HintProjMag > 0.12f * armLen;
                                        if (poleWellConditioned)
                                        {
                                            trackerSensValues.Add(sensHint);
                                            if (sensHint > trackerSensMax) trackerSensMax = sensHint;
                                            if (sensHint > 20f) trackerJittery++;
                                        }
                                    }
                                    if (hint.HintFade < 0.999f) trackerFaded++;
                                    if (!float.IsNaN(alignHint))
                                    {
                                        trackerAlignSum += alignHint;
                                        trackerAlignN++;
                                        if (alignHint > trackerAlignMax) trackerAlignMax = alignHint;
                                    }
                                }

                                rows += WriteRow(w, sb, side, "nohint", i, j, k, target, armLen, isReachable, false, Vector3.zero, noHint, swivelNo, Vector3.zero, float.NaN, float.NaN);
                                rows += WriteRow(w, sb, side, "hint", i, j, k, target, armLen, isReachable, true, hintPos, hint, swivelHint, hintDir, sensHint, alignHint);
                                rows += WriteRow(w, sb, side, "lookup", i, j, k, target, armLen, isReachable, true, lookupHintPos, lookup, swivelLookup, lookupBend, sensLookup, alignLookup);
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
                summary.LookupMeanAbsSwivelDeg = reachable > 0 ? (float)(lookupAbsSwivelSum / reachable) : 0f;
                summary.LookupElbowUpCount = lookupElbowUp;
                summary.LookupElbowFlipCount = lookupElbowFlip;
                summary.LookupMeanAlignErrDeg = lookupAlignN > 0 ? (float)(lookupAlignSum / lookupAlignN) : 0f;
                summary.LookupMaxAlignErrDeg = lookupAlignMax;
                summary.LookupMinElbowAngleDeg = lookupMinElbow;
                summary.LookupMaxElbowAngleDeg = lookupMaxElbow;
                summary.TrackerMeanSensDegPerCm = trackerSensN > 0 ? (float)(trackerSensSum / trackerSensN) : 0f;
                summary.TrackerMaxSensDegPerCm = trackerSensMax;
                summary.TrackerSens99DegPerCm = Percentile(trackerSensValues, 0.999f);
                summary.TrackerJitteryCount = trackerJittery;
                summary.TrackerFadedCount = trackerFaded;
                summary.TrackerMeanAlignErrDeg = trackerAlignN > 0 ? (float)(trackerAlignSum / trackerAlignN) : 0f;
                summary.TrackerMaxAlignErrDeg = trackerAlignMax;
            }
            catch (System.Exception e)
            {
                summary.Ok = false;
                summary.Error = e.Message;
            }
            finally
            {
                if (table.IsCreated) table.Dispose();
            }

            return summary;
        }

        public struct BasisArmIKTrajectorySummary
        {
            public bool Ok;
            public string Path;
            public BasisTrajectoryResult[] Results;
            public float WorstPopDeg;
            public float WorstRoughDeg;
            public float WorstElbowJitterM;  // worst |noisy elbow - clean elbow| in metres (temporal runs)
            public float WorstSwivelRangeDeg; // worst per-path (max-min) elbow-swivel excursion (temporal); a full-extension pole flip swings the elbow far even when the per-frame rate limit keeps each step tiny
            public string Error;
        }

        // Per-frame trajectory scan of the production (lookup-bend, no tracker) elbow swivel along
        // continuous hand paths -- catches lookup-table discontinuities / pole flips that the per-point
        // grid steps over. Pops = jumps on smooth motion; rough/zigzag = tracking-noise jitter.
        public static BasisArmIKTrajectorySummary RunTrajectories(BasisArmIKSweepConfig cfg, float noise, string path)
        {
            var summary = new BasisArmIKTrajectorySummary { Ok = false, Path = path };
            float mirror = cfg.IsLeft ? -1f : 1f;
            float upper = Mathf.Max(1e-4f, cfg.UpperLength);
            float lower = Mathf.Max(1e-4f, cfg.LowerLength);
            float armLen = upper + lower;

            Vector3 shoulder = Vector3.zero;
            Vector3 elbowDir = Mirror(cfg.RestElbowDir, mirror).normalized;
            Vector3 forearmDir = Mirror(cfg.RestForearmDir, mirror).normalized;
            if (elbowDir.sqrMagnitude < 1e-8f) elbowDir = Vector3.down;
            if (forearmDir.sqrMagnitude < 1e-8f) forearmDir = Vector3.forward;
            Vector3 restElbow = shoulder + elbowDir * upper;
            Vector3 restHand = restElbow + forearmDir * lower;
            bool isLeft = cfg.IsLeft;

            NativeArray<Vector3> table = default;
            try
            {
                string dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                table = new NativeArray<Vector3>(BasisArmBendLookup.GenerateDefaultTable(), Allocator.Persistent); // Persistent (not Temp): the sweep may run on a background thread (parallel Run All)
                NativeArray<Vector3> tbl = table;

                System.Func<Vector3, float> eval = target =>
                {
                    Vector3 lookupBend = ComputeLookupBend(tbl, target - shoulder, armLen, isLeft);
                    Vector3 hint = shoulder + 0.5f * armLen * lookupBend;
                    BasisArmSolveResult r = SolveOne(shoulder, restElbow, restHand, target, hint, true);
                    if (r.ReachRatio > 1f) return float.NaN;
                    return Swivel(shoulder, r.HandSolved, r.ElbowSolved);
                };

                Vector3 F3(float fx, float fy, float fz)
                {
                    return shoulder + new Vector3(fx * mirror, fy, fz) * armLen;
                }
                // A point at a FIXED extension (reach * armLen) in the (fx,fy,fz) direction -- the seed for
                // the full-extension arcs, where the elbow pole collapses onto the shoulder->hand axis.
                Vector3 Sphere(float fx, float fy, float fz, float reach)
                {
                    Vector3 d = new Vector3(fx * mirror, fy, fz);
                    float m = d.magnitude;
                    d = m > 1e-6f ? d / m : Vector3.forward;
                    return shoulder + d * (reach * armLen);
                }

                // "ext-*" hold the hand near full extension (reach ~0.95, well-conditioned but nearly straight)
                // and sweep its DIRECTION so the elbow pole has to rotate right where it is most ill-defined --
                // the "extend the arm out fully and it flips" case the moderate-reach paths above never enter.
                // "back-*" do the same BEHIND the body (z<0): there the arm axis, the lookup bend and the
                // forearm all point backward, so Cross(ab,bc) AND the hint/target axis fallbacks all collapse
                // and the bend-plane axis thrashes -- the "fully stretched backward, flips rapidly" case.
                string[] pathNames = { "across", "vertical", "reach-up-across", "circle", "ext-across", "ext-vertical", "ext-reach", "fwd-swing", "fwd-lower", "back-swing", "back-lower", "back-reach" };
                Vector3[][] pathPts =
                {
                    BasisIKTrajectoryScan.Line(F3(0.70f, -0.20f, 0.40f), F3(-0.70f, -0.20f, 0.40f), 160),
                    BasisIKTrajectoryScan.Line(F3(0.10f, -0.80f, 0.30f), F3(0.10f, 0.70f, 0.30f), 160),
                    BasisIKTrajectoryScan.Line(F3(0.60f, -0.60f, 0.20f), F3(-0.50f, 0.50f, 0.30f), 160),
                    BasisIKTrajectoryScan.Circle(F3(0f, -0.10f, 0.40f), new Vector3(mirror, 0f, 0f), new Vector3(0f, 1f, 0f), 0.50f * armLen, 200),
                    BasisIKTrajectoryScan.Arc(shoulder, Sphere(0.75f, -0.15f, 0.70f, 0.95f), Sphere(-0.75f, -0.15f, 0.70f, 0.95f), 160),
                    BasisIKTrajectoryScan.Arc(shoulder, Sphere(0.10f, -0.65f, 0.60f, 0.95f), Sphere(0.10f, 0.55f, 0.60f, 0.95f), 160),
                    // Radial push straight out to (near) full extension -- the elbow pole collapses at the far
                    // end (>0.985 reported as the singularity below); the band up to it must not flip.
                    BasisIKTrajectoryScan.Line(Sphere(0.12f, -0.18f, 1.0f, 0.55f), Sphere(0.12f, -0.18f, 1.0f, 0.99f), 160),
                    // Fully extended in FRONT, swung in azimuth from out-to-the-side to straight-forward --
                    // the forward twin of back-swing, where the user reports the straight-forward flip.
                    BasisIKTrajectoryScan.Arc(shoulder, Sphere(0.85f, -0.20f, 0.30f, 0.92f), Sphere(0.10f, -0.20f, 0.85f, 0.92f), 160),
                    // Fully extended in front, lowered from level-forward to down-forward.
                    BasisIKTrajectoryScan.Arc(shoulder, Sphere(0.45f, -0.05f, 0.80f, 0.92f), Sphere(0.30f, -0.75f, 0.55f, 0.92f), 160),
                    // Fully extended BEHIND, swung in azimuth from out-to-the-side back to straight-behind --
                    // the hand crosses behind the shoulder line where the backward pole reorganizes (flip).
                    BasisIKTrajectoryScan.Arc(shoulder, Sphere(0.85f, -0.20f, -0.30f, 0.92f), Sphere(0.10f, -0.20f, -0.85f, 0.92f), 160),
                    // Fully extended behind, lowered from level-back to down-back.
                    BasisIKTrajectoryScan.Arc(shoulder, Sphere(0.45f, -0.05f, -0.80f, 0.92f), Sphere(0.30f, -0.75f, -0.55f, 0.92f), 160),
                    // Radial push straight BACK to near full extension -- the "stretch the arm out behind you" move.
                    BasisIKTrajectoryScan.Line(Sphere(0.30f, -0.25f, -0.95f, 0.5f), Sphere(0.30f, -0.25f, -0.95f, 0.96f), 160),
                };

                // At true full extension (arm dead straight) the pole is genuinely undefined, so a stateless
                // solve must flip there -- the live rate limiter handles it (see RunTemporal / the NUnit
                // sweeps). Report those samples as the singularity, not a gate-failing pop, the way the leg /
                // elbow-protect scans do; the ext arcs stay at ~0.95 so the gate still fails on a flip in the
                // well-conditioned extended band (the real bug).
                System.Func<Vector3, bool> isSingular = t => (t - shoulder).magnitude > 0.985f * armLen;

                var results = new BasisTrajectoryResult[pathNames.Length];
                using (var w = new StreamWriter(path, false, Encoding.UTF8))
                {
                    w.WriteLine("# BasisArmIKTrajectory " + System.DateTime.UtcNow.ToString("o") +
                                " side=" + (cfg.IsLeft ? "left" : "right") + " noise=" + F(noise) +
                                " upper=" + F(upper) + " lower=" + F(lower));
                    w.WriteLine("path,step,target_x,target_y,target_z,swivel_deg");
                    var sb = new StringBuilder(128);
                    for (int pi = 0; pi < pathNames.Length; pi++)
                    {
                        results[pi] = BasisIKTrajectoryScan.Scan(pathNames[pi], pathPts[pi], eval, noise, 4242 + pi, isSingular: isSingular);
                        Vector3[] pts = pathPts[pi];
                        for (int s = 0; s < pts.Length; s++)
                        {
                            Vector3 t = pts[s];
                            float sw = eval(t);
                            sb.Clear();
                            sb.Append(pathNames[pi]).Append(',').Append(s).Append(',');
                            sb.Append(F(t.x)).Append(',').Append(F(t.y)).Append(',').Append(F(t.z)).Append(',').Append(F(sw));
                            w.WriteLine(sb.ToString());
                        }
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
            finally
            {
                if (table.IsCreated) table.Dispose();
            }
            return summary;
        }

        // Per-frame TEMPORAL drive: feeds the PREVIOUS frame's solved pose back in (the live bend plane keys
        // off it) with the live rate limit (540 deg/s). targetNoise jitters the HAND target each frame
        // (controller noise -> moves BOTH the lookup hint and the IK target: the no-tracker case); hintNoise
        // jitters the hint alone (an elbow tracker). A clean reference sim runs in lockstep, so
        // WorstElbowJitterM = worst |noisy elbow - clean elbow| in METRES: how far a small input wobble
        // throws the elbow (the "0.1 m jitter"). Pass dt = 1/refresh.
        public static BasisArmIKTrajectorySummary RunTemporal(BasisArmIKSweepConfig cfg, float hintNoise, float targetNoise, float dt, string path)
        {
            var summary = new BasisArmIKTrajectorySummary { Ok = false, Path = path };
            float mirror = cfg.IsLeft ? -1f : 1f;
            float upper = Mathf.Max(1e-4f, cfg.UpperLength);
            float lower = Mathf.Max(1e-4f, cfg.LowerLength);
            float armLen = upper + lower;
            Vector3 shoulder = Vector3.zero;
            Vector3 elbowDir = Mirror(cfg.RestElbowDir, mirror).normalized;
            Vector3 forearmDir = Mirror(cfg.RestForearmDir, mirror).normalized;
            if (elbowDir.sqrMagnitude < 1e-8f) elbowDir = Vector3.down;
            if (forearmDir.sqrMagnitude < 1e-8f) forearmDir = Vector3.forward;
            Vector3 restElbow = shoulder + elbowDir * upper;
            Vector3 restHand = restElbow + forearmDir * lower;
            bool isLeft = cfg.IsLeft;
            float maxStep = 540f * Mathf.Max(1e-4f, dt);

            NativeArray<Vector3> table = default;
            try
            {
                string dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                table = new NativeArray<Vector3>(BasisArmBendLookup.GenerateDefaultTable(), Allocator.Persistent); // Persistent (not Temp): the sweep may run on a background thread (parallel Run All)
                NativeArray<Vector3> tbl = table;

                Vector3 F3(float fx, float fy, float fz) => shoulder + new Vector3(fx * mirror, fy, fz) * armLen;
                // Hand at a FIXED extension (reach * armLen); seeds the full-extension arcs (see RunTrajectories).
                Vector3 Sphere(float fx, float fy, float fz, float reach)
                {
                    Vector3 d = new Vector3(fx * mirror, fy, fz);
                    float m = d.magnitude;
                    d = m > 1e-6f ? d / m : Vector3.forward;
                    return shoulder + d * (reach * armLen);
                }
                // ext-* sweep the hand at ~0.95 reach (nearly straight): the live rate limiter must keep the
                // elbow from snapping AND from slewing the whole way around -- the full-extension pole flip.
                // back-* do it BEHIND the body, where the bend-plane axis fallbacks all collapse (rapid flip).
                string[] names = { "across", "vertical", "reach-up-across", "circle", "ext-across", "ext-vertical", "ext-reach", "fwd-swing", "fwd-lower", "back-swing", "back-lower", "back-reach" };
                Vector3[][] paths =
                {
                    BasisIKTrajectoryScan.Line(F3(0.70f, -0.20f, 0.40f), F3(-0.70f, -0.20f, 0.40f), 160),
                    BasisIKTrajectoryScan.Line(F3(0.10f, -0.80f, 0.30f), F3(0.10f, 0.70f, 0.30f), 160),
                    BasisIKTrajectoryScan.Line(F3(0.60f, -0.60f, 0.20f), F3(-0.50f, 0.50f, 0.30f), 160),
                    BasisIKTrajectoryScan.Circle(F3(0f, -0.10f, 0.40f), new Vector3(mirror, 0f, 0f), new Vector3(0f, 1f, 0f), 0.50f * armLen, 200),
                    BasisIKTrajectoryScan.Arc(shoulder, Sphere(0.75f, -0.15f, 0.70f, 0.95f), Sphere(-0.75f, -0.15f, 0.70f, 0.95f), 160),
                    BasisIKTrajectoryScan.Arc(shoulder, Sphere(0.10f, -0.65f, 0.60f, 0.95f), Sphere(0.10f, 0.55f, 0.60f, 0.95f), 160),
                    // Push straight out to near full extension: as the arm straightens the elbow pole collapses,
                    // and the live rate-limited feed must keep the elbow tucked instead of slewing around.
                    BasisIKTrajectoryScan.Line(Sphere(0.12f, -0.18f, 1.0f, 0.55f), Sphere(0.12f, -0.18f, 1.0f, 0.99f), 160),
                    // Fully extended in FRONT, swung in azimuth side -> straight-forward (the forward flip region).
                    BasisIKTrajectoryScan.Arc(shoulder, Sphere(0.85f, -0.20f, 0.30f, 0.92f), Sphere(0.10f, -0.20f, 0.85f, 0.92f), 160),
                    // Fully extended in front, lowered from level-forward to down-forward.
                    BasisIKTrajectoryScan.Arc(shoulder, Sphere(0.45f, -0.05f, 0.80f, 0.92f), Sphere(0.30f, -0.75f, 0.55f, 0.92f), 160),
                    // Fully extended BEHIND, swung in azimuth side -> straight-behind (crosses the flip region).
                    BasisIKTrajectoryScan.Arc(shoulder, Sphere(0.85f, -0.20f, -0.30f, 0.92f), Sphere(0.10f, -0.20f, -0.85f, 0.92f), 160),
                    // Fully extended behind, lowered from level-back to down-back.
                    BasisIKTrajectoryScan.Arc(shoulder, Sphere(0.45f, -0.05f, -0.80f, 0.92f), Sphere(0.30f, -0.75f, -0.55f, 0.92f), 160),
                    // Radial push straight BACK to near full extension -- "stretch the arm out behind you".
                    BasisIKTrajectoryScan.Line(Sphere(0.30f, -0.25f, -0.95f, 0.5f), Sphere(0.30f, -0.25f, -0.95f, 0.96f), 160),
                };
                var results = new BasisTrajectoryResult[names.Length];
                float worstPop = 0f, worstRough = 0f, worstJitterM = 0f, worstSwivelRange = 0f;
                using (var w = new StreamWriter(path, false, Encoding.UTF8))
                {
                    w.WriteLine("# BasisArmIKTemporal " + System.DateTime.UtcNow.ToString("o") +
                                " side=" + (isLeft ? "left" : "right") + " dt=" + F(dt) +
                                " rateDegPerFrame=" + F(maxStep) + " hintNoise=" + F(hintNoise) + " targetNoise=" + F(targetNoise));
                    w.WriteLine("path,step,target_x,target_y,target_z,swivel_deg,elbow_x,elbow_y,elbow_z,jitter_m");
                    var sb = new StringBuilder(160);
                    for (int pi = 0; pi < names.Length; pi++)
                    {
                        Vector3[] pts = paths[pi];
                        Vector3 nElbow = restElbow, nHand = restHand; // noisy feedback state
                        Vector3 cElbow = restElbow, cHand = restHand; // clean reference feedback state
                        var rng = new System.Random(9000 + pi);
                        float maxJump = 0f, prev = float.NaN, sdPrev = float.NaN, maxJitter = 0f;
                        float swMin = float.PositiveInfinity, swMax = float.NegativeInfinity; // swivel excursion over the path
                        double roughSum = 0.0; int roughN = 0, pops = 0;
                        for (int s = 0; s < pts.Length; s++)
                        {
                            Vector3 cleanTarget = pts[s];
                            Vector3 noisyTarget = cleanTarget;
                            if (targetNoise > 0f)
                            {
                                noisyTarget += new Vector3((float)(rng.NextDouble() * 2.0 - 1.0),
                                    (float)(rng.NextDouble() * 2.0 - 1.0), (float)(rng.NextDouble() * 2.0 - 1.0)) * targetNoise;
                            }
                            Vector3 hintNoiseVec = hintNoise > 0f
                                ? new Vector3((float)(rng.NextDouble() * 2.0 - 1.0), (float)(rng.NextDouble() * 2.0 - 1.0),
                                    (float)(rng.NextDouble() * 2.0 - 1.0)) * hintNoise
                                : Vector3.zero;

                            Vector3 cleanElbow = SolveStep(tbl, shoulder, ref cElbow, ref cHand, cleanTarget, Vector3.zero, armLen, isLeft, maxStep);
                            Vector3 noisyElbow = SolveStep(tbl, shoulder, ref nElbow, ref nHand, noisyTarget, hintNoiseVec, armLen, isLeft, maxStep);
                            float sw = Swivel(shoulder, nHand, noisyElbow);
                            float jitter = (noisyElbow - cleanElbow).magnitude;
                            if (jitter > maxJitter) maxJitter = jitter;
                            if (!float.IsNaN(sw)) { if (sw < swMin) swMin = sw; if (sw > swMax) swMax = sw; }

                            sb.Clear();
                            sb.Append(names[pi]).Append(',').Append(s).Append(',');
                            sb.Append(F(cleanTarget.x)).Append(',').Append(F(cleanTarget.y)).Append(',').Append(F(cleanTarget.z)).Append(',').Append(F(sw)).Append(',');
                            sb.Append(F(noisyElbow.x)).Append(',').Append(F(noisyElbow.y)).Append(',').Append(F(noisyElbow.z)).Append(',').Append(F(jitter));
                            w.WriteLine(sb.ToString());

                            if (!float.IsNaN(sw) && !float.IsNaN(prev))
                            {
                                float sd = Mathf.DeltaAngle(prev, sw);
                                float d = Mathf.Abs(sd);
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
                        float swivelRange = swMax >= swMin ? swMax - swMin : 0f;
                        if (swivelRange > worstSwivelRange) worstSwivelRange = swivelRange;
                    }
                }
                summary.Ok = true; summary.Results = results; summary.WorstPopDeg = worstPop;
                summary.WorstRoughDeg = worstRough; summary.WorstElbowJitterM = worstJitterM;
                summary.WorstSwivelRangeDeg = worstSwivelRange;
            }
            catch (System.Exception e) { summary.Ok = false; summary.Error = e.Message; }
            finally { if (table.IsCreated) table.Dispose(); }
            return summary;
        }

        // One temporal feedback step: lookup hint (+ optional hint noise) -> solve from the carried pose to
        // target with the live rate limit; advances the carried elbow/hand and returns the solved elbow.
        static Vector3 SolveStep(NativeArray<Vector3> tbl, Vector3 shoulder, ref Vector3 curElbow, ref Vector3 curHand,
            Vector3 target, Vector3 hintNoiseVec, float armLen, bool isLeft, float maxStep)
        {
            Vector3 lookupBend = ComputeLookupBend(tbl, target - shoulder, armLen, isLeft);
            Vector3 hint = shoulder + 0.5f * armLen * lookupBend + hintNoiseVec;
            BasisArmSolveInput input = default;
            input.Shoulder = shoulder; input.Elbow = curElbow; input.Hand = curHand;
            input.RootRotation = Quaternion.identity; input.MidRotation = Quaternion.identity;
            input.TargetPosition = target; input.TargetRotation = Quaternion.identity;
            input.HintPosition = hint; input.HintWeight = true; input.HintIsTracker = false; input.TargetOffset = Quaternion.identity;
            input.PlayerUp = Vector3.up; input.HintMaxStepDeg = maxStep;
            BasisArmSolveCore.Solve(input, out BasisArmSolveResult r);
            curElbow = r.ElbowSolved; curHand = r.HandSolved;
            return r.ElbowSolved;
        }

        // Degrees of elbow swivel produced per 1 cm of lateral tracker position error at this pose.
        // High values = a tracker here will look jittery/unstable for tiny tracking noise.
        static float TrackerSensitivity(Vector3 shoulder, Vector3 elbow, Vector3 hand, Vector3 target, Vector3 hintPos, float baseSwivel, bool hintIsTracker)
        {
            if (float.IsNaN(baseSwivel)) return float.NaN;
            Vector3 ac = target - shoulder;
            if (ac.sqrMagnitude < 1e-8f) return float.NaN;
            Vector3 dir = Vector3.Cross(ac, Vector3.up);
            if (dir.sqrMagnitude < 1e-8f) dir = Vector3.Cross(ac, Vector3.right);
            if (dir.sqrMagnitude < 1e-8f) return float.NaN;
            dir.Normalize();
            const float eps = 0.01f; // 1 cm
            BasisArmSolveResult p = SolveOne(shoulder, elbow, hand, target, hintPos + dir * eps, true, hintIsTracker);
            float s2 = Swivel(shoulder, p.HandSolved, p.ElbowSolved);
            if (float.IsNaN(s2)) return float.NaN;
            return Mathf.Abs(Mathf.DeltaAngle(baseSwivel, s2));
        }

        // Angle between where the elbow actually landed and where the tracker pole points,
        // both on the swing plane perpendicular to shoulder->hand. 0 = elbow follows the tracker.
        static float TrackerAlignErr(Vector3 shoulder, Vector3 target, Vector3 solvedElbow, Vector3 hintPos)
        {
            Vector3 ac = target - shoulder;
            if (ac.sqrMagnitude < 1e-8f) return float.NaN;
            ac.Normalize();
            Vector3 solvedPole = Vector3.ProjectOnPlane(solvedElbow - shoulder, ac);
            Vector3 hintPole = Vector3.ProjectOnPlane(hintPos - shoulder, ac);
            if (solvedPole.sqrMagnitude < 1e-8f || hintPole.sqrMagnitude < 1e-8f) return float.NaN;
            return Vector3.Angle(solvedPole, hintPole);
        }

        static Vector3 ComputeLookupBend(NativeArray<Vector3> table, Vector3 shoulderToHand, float armLen, bool isLeft)
        {
            Vector3 localPos = shoulderToHand / armLen;
            if (isLeft) localPos.x = -localPos.x;
            Vector3 localBend = BasisArmBendLookup.SampleTrilinear(table, localPos);
            if (isLeft) localBend.x = -localBend.x;
            return localBend.normalized;
        }

        static BasisArmSolveResult SolveOne(Vector3 shoulder, Vector3 elbow, Vector3 hand, Vector3 target, Vector3 hint, bool hintOn, bool hintIsTracker = false)
        {
            BasisArmSolveInput input = default;
            input.Shoulder = shoulder;
            input.Elbow = elbow;
            input.Hand = hand;
            input.RootRotation = Quaternion.identity; // does not affect solved positions
            input.MidRotation = Quaternion.identity;
            input.TargetPosition = target;
            input.TargetRotation = Quaternion.identity;
            input.HintPosition = hint;
            input.HintWeight = hintOn;
            input.HintIsTracker = hintIsTracker;
            input.TargetOffset = Quaternion.identity;
            input.PlayerUp = Vector3.up;
            input.HintMaxStepDeg = float.MaxValue;
            BasisArmSolveCore.Solve(input, out BasisArmSolveResult r);
            return r;
        }

        // Signed elbow swivel around the shoulder->hand axis, measured from straight-down.
        // This is the headline "is the elbow where a human's would be" metric.
        static float Swivel(Vector3 shoulder, Vector3 hand, Vector3 elbow)
        {
            Vector3 axis = hand - shoulder;
            if (axis.sqrMagnitude < 1e-8f) return float.NaN;
            axis.Normalize();
            Vector3 refVec = Vector3.ProjectOnPlane(Vector3.down, axis);
            Vector3 poleVec = Vector3.ProjectOnPlane(elbow - shoulder, axis);
            if (refVec.sqrMagnitude < 1e-8f || poleVec.sqrMagnitude < 1e-8f) return float.NaN;
            return Vector3.SignedAngle(refVec, poleVec, axis);
        }

        static int WriteRow(StreamWriter w, StringBuilder sb, string side, string mode, int i, int j, int k,
            Vector3 target, float armLen, bool reachable, bool hintOn, Vector3 hint, BasisArmSolveResult r, float swivel, Vector3 bendDir, float trackerSens, float trackerAlign)
        {
            sb.Clear();
            sb.Append(side).Append(',').Append(mode).Append(',');
            sb.Append(i).Append(',').Append(j).Append(',').Append(k).Append(',');
            Append(sb, target.x); Append(sb, target.y); Append(sb, target.z);
            Append(sb, r.TargetDistance); Append(sb, armLen);
            Append(sb, r.ReachRatio);
            sb.Append(reachable ? '1' : '0').Append(',');
            sb.Append(hintOn ? '1' : '0').Append(',');
            Append(sb, hint.x); Append(sb, hint.y); Append(sb, hint.z);
            Append(sb, r.ElbowSolved.x); Append(sb, r.ElbowSolved.y); Append(sb, r.ElbowSolved.z);
            Append(sb, r.HandSolved.x); Append(sb, r.HandSolved.y); Append(sb, r.HandSolved.z);
            Append(sb, r.HandError);
            Append(sb, r.ElbowAngleDeg);
            Append(sb, swivel);
            sb.Append(r.AxisSource).Append(',');
            Append(sb, r.HintFade);
            Append(sb, bendDir.x); Append(sb, bendDir.y); Append(sb, bendDir.z);
            Append(sb, r.HintProjMag); Append(sb, r.ArmProjMag); Append(sb, trackerSens); AppendLast(sb, trackerAlign);
            w.WriteLine(sb.ToString());
            return 1;
        }

        static void Append(StringBuilder sb, float v) { sb.Append(F(v)).Append(','); }
        static void AppendLast(StringBuilder sb, float v) { sb.Append(F(v)); }

        static string F(float v)
        {
            if (float.IsNaN(v)) return "nan";
            return v.ToString("0.######", CultureInfo.InvariantCulture);
        }

        // q-th percentile (0..1) -- density-stable, unlike the raw max which chases the sharpening
        // pole-collapse singularity (near-straight arm / hint along the arm axis) as the grid densifies.
        static float Percentile(List<float> values, float q)
        {
            if (values == null || values.Count == 0) return 0f;
            values.Sort();
            int idx = Mathf.Clamp(Mathf.CeilToInt(q * values.Count) - 1, 0, values.Count - 1);
            return values[idx];
        }

        static Vector3 Mirror(Vector3 v, float mirror) { return new Vector3(v.x * mirror, v.y, v.z); }

        static float Lerp01(float min, float max, int steps, int idx)
        {
            if (steps <= 1) return 0.5f * (min + max);
            return Mathf.Lerp(min, max, idx / (float)(steps - 1));
        }
    }
}
