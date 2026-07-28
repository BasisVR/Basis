using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Collections;
using UnityEngine;
using Basis.IK;

namespace Basis.IK.Debugging
{
    // Offline sweep of BasisElbowProtectCore over a 3D grid of hand targets against a fixed torso.
    // At every target it runs the production arm solve (lookup bend, no tracker) and then the
    // torso-collision elbow push, recording how hard the push swings the elbow (chicken-wing
    // magnitude), how abruptly it flips across the body (wrong-side snap), and how much the final
    // elbow swivel moves per cm of hand-target motion (twitch). Pure math, no avatar.
    public struct BasisElbowProtectSweepConfig
    {
        // Arm geometry (shoulder-relative rest pose seeds the bend plane, like the arm sweep).
        public float UpperLength;
        public float LowerLength;
        public Vector3 RestElbowDir;
        public Vector3 RestForearmDir;
        public bool IsLeft;             // mirror X

        // Torso capsule, in torso-local space (== world here): +Y up, +Z forward, +X right.
        // Segment endpoints are vertical heights on the Y axis; radii derive from ChestRadiusBase.
        public float HipsHeight;
        public float SpineHeight;
        public float ChestHeight;       // chest segment start (shoulder-out reference)
        public float NeckHeight;        // chest segment end
        public float ChestRadiusBase;
        public float CollisionSkin;
        public float HandRadius;
        public float HandSkin;

        // Shoulder placement on the torso (right side; mirrored for left).
        public float ShoulderSide;      // x offset out from spine
        public float ShoulderHeight;    // y
        public float ShoulderForward;   // z

        // Hand-target grid in torso space around the shoulder, fractions of arm length.
        public Vector3 MinFrac;
        public Vector3 MaxFrac;
        public Vector3Int Steps;

        /// <summary>Search the WHOLE swivel circle instead of only the natural->outDir arc. A grid sweep has
        /// no previous frame, so the anchor is the natural pole (PrevSwivelDeg = 0), which measures the pure
        /// DOMAIN effect with no temporal component -- exactly the comparison the gate wants. Off by default
        /// so the historical sweep numbers stay reproducible.</summary>
        public bool FullCircleSearch;

        public static BasisElbowProtectSweepConfig Default()
        {
            return new BasisElbowProtectSweepConfig
            {
                UpperLength = 0.28f,
                LowerLength = 0.26f,
                RestElbowDir = new Vector3(0.15f, -0.95f, 0.27f),
                RestForearmDir = new Vector3(0.0f, -0.30f, 0.95f),
                IsLeft = false,

                HipsHeight = 0.00f,
                SpineHeight = 0.16f,
                ChestHeight = 0.32f,
                NeckHeight = 0.52f,
                // Production FBIK collider defaults (BasisSettingsDefaults), so absolute numbers
                // match the live rig. Combined upper-arm+chest reach ~0.13 m at the _v3 sizes.
                ChestRadiusBase = 0.055f,
                CollisionSkin = 0.025f,
                HandRadius = 0.01f,
                HandSkin = 0.03f,

                // Just outside the combined radius: the arm clears at rest and only the cross-body
                // reaches drive the upper arm into the torso.
                ShoulderSide = 0.20f,
                ShoulderHeight = 0.46f,
                ShoulderForward = 0.0f,

                // Reach across and around the body so the upper arm sweeps in and out of the torso.
                MinFrac = new Vector3(-1.3f, -1.1f, -0.5f),
                MaxFrac = new Vector3(0.9f, 0.8f, 1.1f),
                Steps = new Vector3Int(75, 75, 63),
            };
        }
    }

    public struct BasisElbowProtectSweepSummary
    {
        public bool Ok;
        public string Path;
        public int Rows;
        public int Points;
        public int ReachablePoints;
        public int EngagedPoints;        // protect produced a swing (upper arm penetrated the torso)
        public int ClearedPoints;        // engaged poses whose pushed elbow actually leaves the torso
        public float MeanResidualPenMm;  // mean residual torso penetration after the push (mm), over engaged
        public float MaxResidualPenMm;
        public int WrongSideFlipCount;   // engaged poses that could not fully clear (CollisionState==2)
        public float MeanSwingDeg;       // mean elbow swing applied over engaged poses (chicken-wing magnitude)
        public float MaxSwingDeg;
        public float MeanProtectShiftDeg;// mean |final - raw| elbow swivel over engaged poses
        public float MaxProtectShiftDeg;
        public int ElbowUpCount;         // engaged poses whose pushed elbow points up (chicken-wing)
        public float MeanSensDegPerCm;   // final elbow swivel deg per cm of hand-target error (twitch)
        public float MaxSensDegPerCm;
        public float Sens99DegPerCm;     // 99.9th-percentile sens over well-conditioned poses (density-stable; ignores boundary-discontinuity outliers the max chases)
        public int JitteryCount;         // reachable poses with sensitivity > 20 deg/cm
        public string Error;
    }

    public static class BasisElbowProtectSweep
    {
        public const string DefaultFileName = "BasisElbowProtectSweep.csv";
        const float k_JitterDegPerCm = 20f;

        public static string DefaultPath()
        {
            return System.IO.Path.Combine(Application.persistentDataPath, DefaultFileName);
        }

        public static BasisElbowProtectSweepSummary Run(BasisElbowProtectSweepConfig cfg, string path)
        {
            var summary = new BasisElbowProtectSweepSummary { Ok = false, Path = path };

            float mirror = cfg.IsLeft ? -1f : 1f;
            float upper = Mathf.Max(1e-4f, cfg.UpperLength);
            float lower = Mathf.Max(1e-4f, cfg.LowerLength);
            float armLen = upper + lower;

            Vector3 shoulder = new Vector3(cfg.ShoulderSide * mirror, cfg.ShoulderHeight, cfg.ShoulderForward);
            Vector3 elbowDir = Mirror(cfg.RestElbowDir, mirror).normalized;
            Vector3 forearmDir = Mirror(cfg.RestForearmDir, mirror).normalized;
            if (elbowDir.sqrMagnitude < 1e-8f) elbowDir = Vector3.down;
            if (forearmDir.sqrMagnitude < 1e-8f) forearmDir = Vector3.forward;

            Vector3 restElbow = shoulder + elbowDir * upper;
            Vector3 restHand = restElbow + forearmDir * lower;

            BasisElbowProtectInput tmpl = MakeTemplate(cfg);

            int sx = Mathf.Max(1, cfg.Steps.x);
            int sy = Mathf.Max(1, cfg.Steps.y);
            int sz = Mathf.Max(1, cfg.Steps.z);

            int points = 0, reachable = 0, engaged = 0, wrongFlips = 0, elbowUp = 0, jittery = 0, rows = 0;
            int clearedPoints = 0;
            double swingSum = 0.0, shiftSum = 0.0, sensSum = 0.0, residSum = 0.0;
            float swingMax = 0f, shiftMax = 0f, sensMax = 0f, residMax = 0f;
            int sensN = 0;
            var sensValues = new List<float>(); // well-conditioned sens, for a density-stable percentile

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
                    w.WriteLine("# BasisElbowProtectSweep " + System.DateTime.UtcNow.ToString("o"));
                    w.WriteLine("# side=" + (cfg.IsLeft ? "left" : "right") +
                                " upper=" + F(upper) + " lower=" + F(lower) +
                                " shoulder=(" + F(shoulder.x) + "," + F(shoulder.y) + "," + F(shoulder.z) + ")");
                    w.WriteLine("side,ti,tj,tk,target_x,target_y,target_z,target_dist,arm_len,reach_ratio,reachable," +
                                "elbow_x,elbow_y,elbow_z,hand_x,hand_y,hand_z," +
                                "engaged,collision_state,worst_pen,side_dot,blend_used,swing_deg,elbow_radius," +
                                "desired_x,desired_y,desired_z,swivel_before,swivel_after,protect_shift,elbow_up,sens_deg_per_cm");

                    var sb = new StringBuilder(256);
                    string side = cfg.IsLeft ? "left" : "right";

                    for (int ti = 0; ti < sx; ti++)
                    {
                        float fx = Lerp01(cfg.MinFrac.x, cfg.MaxFrac.x, sx, ti) * mirror;
                        for (int tj = 0; tj < sy; tj++)
                        {
                            float fy = Lerp01(cfg.MinFrac.y, cfg.MaxFrac.y, sy, tj);
                            for (int tk = 0; tk < sz; tk++)
                            {
                                float fz = Lerp01(cfg.MinFrac.z, cfg.MaxFrac.z, sz, tk);
                                Vector3 target = shoulder + new Vector3(fx, fy, fz) * armLen;
                                points++;

                                Evaluate(shoulder, restElbow, restHand, target, tmpl, table, cfg.IsLeft, armLen,
                                    out BasisArmSolveResult arm, out BasisElbowProtectResult protect);

                                bool isReachable = arm.ReachRatio <= 1f;
                                if (isReachable) reachable++;

                                float swivelBefore = Swivel(shoulder, arm.HandSolved, arm.ElbowSolved);
                                float swivelAfter = protect.Engaged
                                    ? Swivel(shoulder, arm.HandSolved, protect.DesiredElbow)
                                    : swivelBefore;
                                float protectShift = (!float.IsNaN(swivelBefore) && !float.IsNaN(swivelAfter))
                                    ? Mathf.Abs(Mathf.DeltaAngle(swivelBefore, swivelAfter)) : float.NaN;

                                bool pushedUp = false;
                                if (protect.Engaged && protect.ElbowRadius > 1e-5f)
                                {
                                    Vector3 perp = (protect.DesiredElbow - protect.ElbowCenter) / protect.ElbowRadius;
                                    pushedUp = Vector3.Dot(perp, Vector3.up) > 0.2f;
                                }

                                float sens = float.NaN;
                                if (isReachable)
                                {
                                    sens = Sensitivity(shoulder, restElbow, restHand, target, tmpl, table, cfg.IsLeft, armLen,
                                        FinalSwivel(shoulder, arm, protect));
                                }

                                if (protect.Engaged)
                                {
                                    engaged++;
                                    if (protect.CollisionState == 2) wrongFlips++;
                                    if (protect.ResidualClearance >= 0f) clearedPoints++;
                                    float residMm = protect.ResidualClearance >= 0f ? 0f : -protect.ResidualClearance * 1000f;
                                    residSum += residMm;
                                    if (residMm > residMax) residMax = residMm;
                                    swingSum += protect.SwingAngleDeg;
                                    if (protect.SwingAngleDeg > swingMax) swingMax = protect.SwingAngleDeg;
                                    if (!float.IsNaN(protectShift))
                                    {
                                        shiftSum += protectShift;
                                        if (protectShift > shiftMax) shiftMax = protectShift;
                                    }
                                    if (pushedUp) elbowUp++;
                                }
                                if (isReachable && !float.IsNaN(sens))
                                {
                                    sensSum += sens;
                                    sensN++;
                                    // Exclude singularities from the MAX, where swivel-per-cm is geometrically
                                    // unbounded (so a denser grid keeps finding higher spikes), like the
                                    // trajectory isSingular: a folded/extended arm (short shoulder->hand lever)
                                    // and the protect's own can't-clear cases (CollisionState 2, already tracked
                                    // by WrongSideFlipCount). What remains is the protect's twitch on cleared,
                                    // well-conditioned poses.
                                    bool wellConditioned = arm.ReachRatio >= 0.40f && arm.ReachRatio <= 0.97f && protect.CollisionState != 2;
                                    if (wellConditioned)
                                    {
                                        sensValues.Add(sens);
                                        if (sens > sensMax) sensMax = sens;
                                        if (sens > k_JitterDegPerCm) jittery++;
                                    }
                                }

                                rows += WriteRow(w, sb, side, ti, tj, tk, target, armLen, arm, isReachable,
                                    protect, swivelBefore, swivelAfter, protectShift, pushedUp, sens);
                            }
                        }
                    }
                }

                summary.Ok = true;
                summary.Rows = rows;
                summary.Points = points;
                summary.ReachablePoints = reachable;
                summary.EngagedPoints = engaged;
                summary.ClearedPoints = clearedPoints;
                summary.MeanResidualPenMm = engaged > 0 ? (float)(residSum / engaged) : 0f;
                summary.MaxResidualPenMm = residMax;
                summary.WrongSideFlipCount = wrongFlips;
                summary.MeanSwingDeg = engaged > 0 ? (float)(swingSum / engaged) : 0f;
                summary.MaxSwingDeg = swingMax;
                summary.MeanProtectShiftDeg = engaged > 0 ? (float)(shiftSum / engaged) : 0f;
                summary.MaxProtectShiftDeg = shiftMax;
                summary.ElbowUpCount = elbowUp;
                summary.MeanSensDegPerCm = sensN > 0 ? (float)(sensSum / sensN) : 0f;
                summary.MaxSensDegPerCm = sensMax;
                summary.Sens99DegPerCm = Percentile(sensValues, 0.999f);
                summary.JitteryCount = jittery;
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

        static BasisElbowProtectInput MakeTemplate(BasisElbowProtectSweepConfig cfg)
        {
            return new BasisElbowProtectInput
            {
                // FullCircle only. A grid sweep has no previous frame, so HasPrevSwivel stays FALSE --
                // see its doc comment: a zero-seeded anchor is a pull toward the natural pole, not a
                // neutral start, and it measures the wrong thing.
               //FullCircle = cfg.FullCircleSearch,
                HipsPos = new Vector3(0f, cfg.HipsHeight, 0f),
                SpinePos = new Vector3(0f, cfg.SpineHeight, 0f),
                ChestPos = new Vector3(0f, cfg.ChestHeight, 0f),
                NeckPos = new Vector3(0f, cfg.NeckHeight, 0f),
                HasHips = true,
                HasSpine = true,
                ChestRadiusBase = cfg.ChestRadiusBase,
                CollisionSkin = cfg.CollisionSkin,
                HandRadius = cfg.HandRadius,
                HandSkin = cfg.HandSkin,
                PlayerUp = Vector3.up,
            };
        }

        public struct BasisElbowProtectTrajectorySummary
        {
            public bool Ok;
            public string Path;
            public BasisTrajectoryResult[] Results;
            public float WorstPopDeg;
            public float WorstRoughDeg;
            public string Error;
        }

        // Per-frame trajectory scan: drag the hand along realistic continuous paths through the engaged
        // zone and measure how smoothly the elbow follows -- the temporal dimension the grid sweep can't
        // see. Pops = discontinuities crossed on smooth motion; rough/zigzag = tracking-noise jitter.
        public static BasisElbowProtectTrajectorySummary RunTrajectories(BasisElbowProtectSweepConfig cfg, float noise, string path)
        {
            var summary = new BasisElbowProtectTrajectorySummary { Ok = false, Path = path };
            float mirror = cfg.IsLeft ? -1f : 1f;
            float upper = Mathf.Max(1e-4f, cfg.UpperLength);
            float lower = Mathf.Max(1e-4f, cfg.LowerLength);
            float armLen = upper + lower;

            Vector3 shoulder = new Vector3(cfg.ShoulderSide * mirror, cfg.ShoulderHeight, cfg.ShoulderForward);
            Vector3 elbowDir = Mirror(cfg.RestElbowDir, mirror).normalized;
            Vector3 forearmDir = Mirror(cfg.RestForearmDir, mirror).normalized;
            if (elbowDir.sqrMagnitude < 1e-8f) elbowDir = Vector3.down;
            if (forearmDir.sqrMagnitude < 1e-8f) forearmDir = Vector3.forward;
            Vector3 restElbow = shoulder + elbowDir * upper;
            Vector3 restHand = restElbow + forearmDir * lower;
            BasisElbowProtectInput tmpl = MakeTemplate(cfg);

            NativeArray<Vector3> table = default;
            try
            {
                string dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                table = new NativeArray<Vector3>(BasisArmBendLookup.GenerateDefaultTable(), Allocator.Persistent); // Persistent (not Temp): the sweep may run on a background thread (parallel Run All)
                NativeArray<Vector3> tbl = table;
                bool isLeft = cfg.IsLeft;

                System.Func<Vector3, float> eval = target =>
                {
                    Evaluate(shoulder, restElbow, restHand, target, tmpl, tbl, isLeft, armLen,
                        out BasisArmSolveResult arm, out BasisElbowProtectResult pr);
                    if (arm.ReachRatio > 1f) return float.NaN;
                    Vector3 elbow = pr.Engaged ? pr.DesiredElbow : arm.ElbowSolved;
                    return Swivel(shoulder, arm.HandSolved, elbow);
                };

                Vector3 F3(float fx, float fy, float fz)
                {
                    return shoulder + new Vector3(fx * mirror, fy, fz) * armLen;
                }

                // Folded/extended arm = kinematic singularity (elbow pole ill-defined), where a smooth hand
                // path produces an unavoidable swivel pop. Report those separately instead of failing on them
                // -- the same isSingular exclusion the leg/arm/head trajectory scans use; this one lacked it.
                // 0.40 (not the leg's 0.35) matches this sweep's folded-sens cutoff -- the protect push keeps
                // the pole sensitive a bit longer.
                System.Func<Vector3, bool> isSingular = target =>
                {
                    float rr = (target - shoulder).magnitude / armLen;
                    return rr < 0.40f || rr > 0.97f;
                };

                string[] pathNames = { "across-chest", "up-centerline", "reach-up-across", "tuck-circle" };
                Vector3[][] pathPts =
                {
                    BasisIKTrajectoryScan.Line(F3(0.40f, -0.04f, 0.19f),  F3(-0.85f, -0.04f, 0.19f), 160),
                    BasisIKTrajectoryScan.Line(F3(-0.26f, -0.63f, 0.22f), F3(-0.26f, 0.52f, 0.22f), 160),
                    BasisIKTrajectoryScan.Line(F3(0.26f, -0.63f, 0.11f),  F3(-0.56f, 0.11f, 0.22f), 160),
                    BasisIKTrajectoryScan.Circle(F3(-0.28f, -0.07f, 0.28f), new Vector3(mirror, 0f, 0f), new Vector3(0f, 1f, 0f), 0.37f * armLen, 200),
                };

                var results = new BasisTrajectoryResult[pathNames.Length];
                using (var w = new StreamWriter(path, false, Encoding.UTF8))
                {
                    w.WriteLine("# BasisElbowProtectTrajectory " + System.DateTime.UtcNow.ToString("o") +
                                " noise=" + F(noise));
                    w.WriteLine("path,step,target_x,target_y,target_z,swivel_deg");
                    var sb = new StringBuilder(128);
                    for (int pi = 0; pi < pathNames.Length; pi++)
                    {
                        results[pi] = BasisIKTrajectoryScan.Scan(pathNames[pi], pathPts[pi], eval, noise, 12345 + pi, isSingular: isSingular);
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

        // Production no-tracker path: lookup bend -> hint -> arm solve, then the protect push.
        static void Evaluate(Vector3 shoulder, Vector3 restElbow, Vector3 restHand, Vector3 target,
            BasisElbowProtectInput tmpl, NativeArray<Vector3> table, bool isLeft, float armLen,
            out BasisArmSolveResult arm, out BasisElbowProtectResult protect)
        {
            Vector3 lookupBend = ComputeLookupBend(table, target - shoulder, armLen, isLeft);
            Vector3 hintPos = shoulder + 0.5f * armLen * lookupBend;
            arm = SolveArm(shoulder, restElbow, restHand, target, hintPos);

            BasisElbowProtectInput epi = tmpl;
            epi.Shoulder = shoulder;
            epi.Elbow = arm.ElbowSolved;
            epi.Hand = arm.HandSolved;
            BasisElbowProtectCore.Solve(epi, out protect);
        }

        // Degrees the final (post-protect) elbow swivel moves per 1 cm of lateral hand-target error.
        // High values = the elbow will look jittery/unstable for tiny hand-tracking noise.
        static float Sensitivity(Vector3 shoulder, Vector3 restElbow, Vector3 restHand, Vector3 target,
            BasisElbowProtectInput tmpl, NativeArray<Vector3> table, bool isLeft, float armLen, float baseFinalSwivel)
        {
            if (float.IsNaN(baseFinalSwivel)) return float.NaN;
            Vector3 ac = target - shoulder;
            if (ac.sqrMagnitude < 1e-8f) return float.NaN;
            Vector3 dir = Vector3.Cross(ac, Vector3.up);
            if (dir.sqrMagnitude < 1e-8f) dir = Vector3.Cross(ac, Vector3.right);
            if (dir.sqrMagnitude < 1e-8f) return float.NaN;
            dir.Normalize();
            const float eps = 0.01f; // 1 cm

            Evaluate(shoulder, restElbow, restHand, target + dir * eps, tmpl, table, isLeft, armLen,
                out BasisArmSolveResult arm2, out BasisElbowProtectResult protect2);
            float s2 = FinalSwivel(shoulder, arm2, protect2);
            if (float.IsNaN(s2)) return float.NaN;
            return Mathf.Abs(Mathf.DeltaAngle(baseFinalSwivel, s2));
        }

        static float FinalSwivel(Vector3 shoulder, BasisArmSolveResult arm, BasisElbowProtectResult protect)
        {
            Vector3 elbow = protect.Engaged ? protect.DesiredElbow : arm.ElbowSolved;
            return Swivel(shoulder, arm.HandSolved, elbow);
        }

        static Vector3 ComputeLookupBend(NativeArray<Vector3> table, Vector3 shoulderToHand, float armLen, bool isLeft)
        {
            Vector3 localPos = shoulderToHand / armLen;
            if (isLeft) localPos.x = -localPos.x;
            Vector3 localBend = BasisArmBendLookup.SampleTrilinear(table, localPos);
            if (isLeft) localBend.x = -localBend.x;
            return localBend.normalized;
        }

        static BasisArmSolveResult SolveArm(Vector3 shoulder, Vector3 elbow, Vector3 hand, Vector3 target, Vector3 hint)
        {
            BasisArmSolveInput input = default;
            input.Shoulder = shoulder;
            input.Elbow = elbow;
            input.Hand = hand;
            input.RootRotation = Quaternion.identity;
            input.MidRotation = Quaternion.identity;
            input.TargetPosition = target;
            input.TargetRotation = Quaternion.identity;
            input.HintPosition = hint;
            input.HintWeight = true;
            input.HintIsTracker = false;
            input.TargetOffset = Quaternion.identity;
            input.PlayerUp = Vector3.up;
            input.HintMaxStepDeg = float.MaxValue;
            BasisArmSolveCore.Solve(input, out BasisArmSolveResult r);
            return r;
        }

        // Signed elbow swivel around the shoulder->hand axis, measured from straight-down.
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

        static int WriteRow(StreamWriter w, StringBuilder sb, string side, int ti, int tj, int tk,
            Vector3 target, float armLen, BasisArmSolveResult arm, bool reachable,
            BasisElbowProtectResult protect, float swivelBefore, float swivelAfter, float protectShift, bool pushedUp, float sens)
        {
            sb.Clear();
            sb.Append(side).Append(',');
            sb.Append(ti).Append(',').Append(tj).Append(',').Append(tk).Append(',');
            Append(sb, target.x); Append(sb, target.y); Append(sb, target.z);
            Append(sb, arm.TargetDistance); Append(sb, armLen);
            Append(sb, arm.ReachRatio);
            sb.Append(reachable ? '1' : '0').Append(',');
            Append(sb, arm.ElbowSolved.x); Append(sb, arm.ElbowSolved.y); Append(sb, arm.ElbowSolved.z);
            Append(sb, arm.HandSolved.x); Append(sb, arm.HandSolved.y); Append(sb, arm.HandSolved.z);
            sb.Append(protect.Engaged ? '1' : '0').Append(',');
            sb.Append(protect.CollisionState).Append(',');
            Append(sb, protect.WorstPenetration);
            Append(sb, protect.SideDot);
            Append(sb, protect.BlendUsed);
            Append(sb, protect.SwingAngleDeg);
            Append(sb, protect.ElbowRadius);
            Append(sb, protect.DesiredElbow.x); Append(sb, protect.DesiredElbow.y); Append(sb, protect.DesiredElbow.z);
            Append(sb, swivelBefore);
            Append(sb, swivelAfter);
            Append(sb, protectShift);
            sb.Append(pushedUp ? '1' : '0').Append(',');
            AppendLast(sb, sens);
            w.WriteLine(sb.ToString());
            return 1;
        }

        static void Append(StringBuilder sb, float v) { sb.Append(F(v)).Append(','); }
        static void AppendLast(StringBuilder sb, float v) { sb.Append(F(v)); }

        // q-th percentile (0..1) of the values -- density-stable, unlike the raw max which chases the
        // sharpening boundary-discontinuity outliers as the grid densifies.
        static float Percentile(List<float> values, float q)
        {
            if (values == null || values.Count == 0) return 0f;
            values.Sort();
            int idx = Mathf.Clamp(Mathf.CeilToInt(q * values.Count) - 1, 0, values.Count - 1);
            return values[idx];
        }

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
