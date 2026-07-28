using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using Basis.IK;

namespace Basis.IK.Debugging
{
    // Offline sweep of BasisShoulderSolveCore over a sphere of upper-arm directions in chest-local
    // space, solved both elbow-driven (true upper-arm direction) and hand-driven (hand-target
    // fallback). Records the scapulohumeral engagement curve and the applied girdle rotation, and
    // aggregates the invariants the gates assert: twist-free, clamped, chest-frame rigid, left/right
    // symmetric, continuous, and that the elbow tracker actually changes a bent-arm result.
    public struct BasisShoulderSweepConfig
    {
        public float ArmLength;          // shoulder->hand rest distance
        public float ElevationFactor;
        public float ProtractionFactor;
        public float CoupleRatio;
        public float MaxShoulderDeg;
        public bool IsLeft;

        public float AzMin, AzMax;       // azimuth around chest-up, from lateral(+X) toward forward(+Z)
        public float ElMin, ElMax;       // elevation above the chest horizontal plane
        public float ReachMin, ReachMax;
        public int AzSteps, ElSteps, ReachSteps;

        public static BasisShoulderSweepConfig Default()
        {
            return new BasisShoulderSweepConfig
            {
                ArmLength = 0.54f,
                ElevationFactor = 0.4f,
                ProtractionFactor = 0.3f,
                CoupleRatio = 0.8f,
                MaxShoulderDeg = 40f,
                IsLeft = false,
                AzMin = -120f, AzMax = 150f,
                ElMin = -70f, ElMax = 130f,
                ReachMin = 0.4f, ReachMax = 1.0f,
                AzSteps = 46, ElSteps = 34, ReachSteps = 5,
            };
        }
    }

    public struct BasisShoulderSweepSummary
    {
        public bool Ok;
        public string Path;
        public int Rows;
        public int Engaged;                  // points where the girdle engages (ComputedWeight > 0)
        public float MaxShoulderAngleDeg;    // worst applied girdle rotation

        public float RestPoseMaxDeg;         // girdle rotation when the arm is at the bind direction (must be ~0)
        public float MaxTwistLeakDeg;        // girdle rotation about the arm axis (must stay ~0)
        public float ClampOvershootDeg;      // worst applied angle beyond the configured clamp
        public float MaxFrameInvarErrDeg;    // rotating the chest frame must rotate the result rigidly
        public float SymmetryMaxErrDeg;      // left applied angle vs mirrored-right applied angle
        public int MonotonicViolations;      // raising the arm must not REDUCE girdle elevation
        public float MaxAdjacentJumpDeg;     // applied-rotation jump between adjacent samples on a smooth arc
        public float NoisyMaxJitterDeg;      // girdle jitter under a noisy elbow tracker
        public float BentArmElbowElevationDeg; // elbow-driven elevation on a bent-arm raise (elbow up, hand low)
        public float BentArmHandElevationDeg;  // hand-driven elevation on the same pose (should stay low)
        public string Error;
    }

    public static class BasisShoulderSweep
    {
        public const string DefaultFileName = "BasisShoulderSweep.csv";

        public static string DefaultPath()
        {
            return System.IO.Path.Combine(Application.persistentDataPath, DefaultFileName);
        }

        public static BasisShoulderSweepSummary Run(BasisShoulderSweepConfig cfg, string path)
        {
            var s = new BasisShoulderSweepSummary { Ok = false, Path = path };

            float armLen = Mathf.Max(1e-4f, cfg.ArmLength);
            Vector3 shoulder = Vector3.zero;

            int saz = Mathf.Max(1, cfg.AzSteps);
            int sel = Mathf.Max(1, cfg.ElSteps);
            int sr = Mathf.Max(1, cfg.ReachSteps);

            int rows = 0, engaged = 0;
            float maxAngle = 0f, maxTwist = 0f, clampOvershoot = 0f;

            try
            {
                string outDir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
                {
                    Directory.CreateDirectory(outDir);
                }

                using (var w = new StreamWriter(path, false, Encoding.UTF8))
                {
                    w.WriteLine("# BasisShoulderSweep " + System.DateTime.UtcNow.ToString("o"));
                    w.WriteLine("# side=" + (cfg.IsLeft ? "left" : "right") + " armLen=" + F(armLen) +
                                " elev=" + F(cfg.ElevationFactor) + " prot=" + F(cfg.ProtractionFactor) +
                                " couple=" + F(cfg.CoupleRatio) + " maxDeg=" + F(cfg.MaxShoulderDeg));
                    w.WriteLine("side,mode,ai,ej,rk,az,el,reach,dir_x,dir_y,dir_z," +
                                "swing_deg,applied_deg,elevation_deg,protraction_deg,crossbody_deg,engage,twist_leak_deg,reach_ratio,driver_elbow");

                    var sb = new StringBuilder(224);
                    string side = cfg.IsLeft ? "left" : "right";

                    for (int ai = 0; ai < saz; ai++)
                    {
                        float az = Lerp01(cfg.AzMin, cfg.AzMax, saz, ai);
                        for (int ej = 0; ej < sel; ej++)
                        {
                            float el = Lerp01(cfg.ElMin, cfg.ElMax, sel, ej);
                            Vector3 dir = DirFromAzEl(az, el, cfg.IsLeft);
                            for (int rk = 0; rk < sr; rk++)
                            {
                                float reach = Lerp01(cfg.ReachMin, cfg.ReachMax, sr, rk);
                                foreach (bool elbow in s_modes)
                                {
                                    BasisShoulderSolveInput input = BuildInput(cfg, shoulder, dir, reach, elbow, Quaternion.identity);
                                    BasisShoulderSolveCore.Solve(input, out BasisShoulderSolveResult res);

                                    if (res.ComputedWeight > 1e-3f) engaged++;
                                    if (res.AppliedAngleDeg > maxAngle) maxAngle = res.AppliedAngleDeg;
                                    if (res.TwistLeakDeg > maxTwist) maxTwist = res.TwistLeakDeg;
                                    float over = res.AppliedAngleDeg - cfg.MaxShoulderDeg;
                                    if (over > clampOvershoot) clampOvershoot = over;

                                    sb.Clear();
                                    sb.Append(side).Append(',').Append(elbow ? "elbow" : "hand").Append(',');
                                    sb.Append(ai).Append(',').Append(ej).Append(',').Append(rk).Append(',');
                                    Append(sb, az); Append(sb, el); Append(sb, reach);
                                    Append(sb, dir.x); Append(sb, dir.y); Append(sb, dir.z);
                                    Append(sb, res.SwingAngleDeg); Append(sb, res.AppliedAngleDeg);
                                    Append(sb, res.Elevation); Append(sb, res.Protraction); Append(sb, res.CrossBodyContrib);
                                    Append(sb, res.ComputedWeight); Append(sb, res.TwistLeakDeg); Append(sb, res.ReachRatio);
                                    sb.Append(res.DriverIsElbow ? '1' : '0');
                                    w.WriteLine(sb.ToString());
                                    rows++;
                                }
                            }
                        }
                    }
                }

                s.Rows = rows;
                s.Engaged = engaged;
                s.MaxShoulderAngleDeg = maxAngle;
                s.MaxTwistLeakDeg = maxTwist;
                s.ClampOvershootDeg = clampOvershoot;
                s.RestPoseMaxDeg = RestPoseScan(cfg, shoulder, armLen);
                s.MonotonicViolations = MonotonicScan(cfg, shoulder);
                s.MaxFrameInvarErrDeg = FrameInvarianceScan(cfg, shoulder);
                s.SymmetryMaxErrDeg = SymmetryScan(cfg, shoulder);
                s.MaxAdjacentJumpDeg = ContinuityScan(cfg, shoulder);
                s.NoisyMaxJitterDeg = NoisyElbowScan(cfg, shoulder);
                BentArmScan(cfg, shoulder, out s.BentArmElbowElevationDeg, out s.BentArmHandElevationDeg);

                s.Ok = true;
            }
            catch (System.Exception e)
            {
                s.Ok = false;
                s.Error = e.Message;
            }

            return s;
        }

        static readonly bool[] s_modes = { true, false };

        // The girdle delta must vanish when the arm sits at the bind direction (no change to the
        // authored shoulder pose), across reaches and both drive modes.
        static float RestPoseScan(BasisShoulderSweepConfig cfg, Vector3 shoulder, float armLen)
        {
            float worst = 0f;
            Vector3 restDir = RestDir(cfg.IsLeft);
            foreach (bool elbow in s_modes)
            for (int rk = 0; rk < 5; rk++)
            {
                float reach = Mathf.Lerp(0.5f, 1.0f, rk / 4f);
                BasisShoulderSolveInput input = BuildInputDir(cfg, shoulder, restDir, reach, elbow, Quaternion.identity);
                BasisShoulderSolveCore.Solve(input, out BasisShoulderSolveResult res);
                if (res.AppliedAngleDeg > worst) worst = res.AppliedAngleDeg;
            }
            return worst;
        }

        // Raising the arm in the abduction / forward-flexion plane (up the side or front, within the
        // physical range -- not past vertical, not cross-body, where elevation legitimately trades with
        // protraction) must never REDUCE the girdle elevation below the clamp: that would read as the
        // shoulder dropping while the arm goes up.
        static int MonotonicScan(BasisShoulderSweepConfig cfg, Vector3 shoulder)
        {
            int violations = 0;
            foreach (float az in new[] { 0f, 45f, 90f })
            foreach (bool elbow in s_modes)
            {
                float prevElev = -1f, prevApplied = -1f;
                for (int e = 0; e <= 60; e++)
                {
                    float el = Mathf.Lerp(0f, 88f, e / 60f); // raising from the bind horizontal to (just below) overhead
                    Vector3 dir = DirFromAzEl(az, el, cfg.IsLeft);
                    BasisShoulderSolveInput input = BuildInputDir(cfg, shoulder, dir, 0.9f, elbow, Quaternion.identity);
                    BasisShoulderSolveCore.Solve(input, out BasisShoulderSolveResult res);
                    // Once the clamp saturates the girdle, uniform scaling can trade elevation for
                    // protraction -- only the below-clamp raising region must be monotonic.
                    bool belowClamp = res.AppliedAngleDeg < cfg.MaxShoulderDeg - 1f && prevApplied < cfg.MaxShoulderDeg - 1f;
                    if (prevElev >= 0f && belowClamp && res.Elevation < prevElev - 0.75f) violations++;
                    prevElev = res.Elevation;
                    prevApplied = res.AppliedAngleDeg;
                }
            }
            return violations;
        }

        // Rotating the chest frame and the arm together must rotate the result rigidly and leave the
        // engagement shares unchanged (the solve is expressed entirely in the chest frame).
        static float FrameInvarianceScan(BasisShoulderSweepConfig cfg, Vector3 shoulder)
        {
            float worst = 0f;
            Quaternion frame = Quaternion.Euler(17f, 40f, -23f);
            foreach (var azel in s_sampleAzEl)
            {
                Vector3 dir = DirFromAzEl(azel.x, azel.y, cfg.IsLeft);
                BasisShoulderSolveInput baseInput = BuildInputDir(cfg, shoulder, dir, 0.9f, true, Quaternion.identity);
                BasisShoulderSolveCore.Solve(baseInput, out BasisShoulderSolveResult b);

                BasisShoulderSolveInput rotInput = BuildInputDir(cfg, shoulder, dir, 0.9f, true, frame);
                BasisShoulderSolveCore.Solve(rotInput, out BasisShoulderSolveResult rr);

                float err = Quaternion.Angle(frame * b.ShoulderRotation, rr.ShoulderRotation);
                err = Mathf.Max(err, Mathf.Abs(b.SwingAngleDeg - rr.SwingAngleDeg));
                err = Mathf.Max(err, Mathf.Abs(b.AppliedAngleDeg - rr.AppliedAngleDeg));
                if (err > worst) worst = err;
            }
            return worst;
        }

        // The left shoulder must mirror the right: same applied angle and shares for mirrored directions.
        static float SymmetryScan(BasisShoulderSweepConfig cfg, Vector3 shoulder)
        {
            float worst = 0f;
            var right = cfg; right.IsLeft = false;
            var left = cfg; left.IsLeft = true;
            foreach (var azel in s_sampleAzEl)
            foreach (bool elbow in s_modes)
            {
                Vector3 dr = DirFromAzEl(azel.x, azel.y, false);
                Vector3 dl = DirFromAzEl(azel.x, azel.y, true);
                BasisShoulderSolveCore.Solve(BuildInputDir(right, shoulder, dr, 0.9f, elbow, Quaternion.identity), out var R);
                BasisShoulderSolveCore.Solve(BuildInputDir(left, shoulder, dl, 0.9f, elbow, Quaternion.identity), out var L);
                float err = Mathf.Abs(R.AppliedAngleDeg - L.AppliedAngleDeg);
                err = Mathf.Max(err, Mathf.Abs(R.Elevation - L.Elevation));
                err = Mathf.Max(err, Mathf.Abs(R.Protraction - L.Protraction));
                if (err > worst) worst = err;
            }
            return worst;
        }

        // Smooth arm motion must give a smooth girdle: no jump in the applied rotation between adjacent
        // samples along azimuth and elevation arcs.
        static float ContinuityScan(BasisShoulderSweepConfig cfg, Vector3 shoulder)
        {
            float worst = 0f;
            // Azimuth sweep at several elevations.
            foreach (float el in new[] { -30f, 10f, 50f, 100f })
                worst = Mathf.Max(worst, ArcJump(cfg, shoulder, -120f, 150f, el, true, axisAzimuth: true));
            // Elevation sweep at several azimuths.
            foreach (float az in new[] { -60f, 0f, 60f, 120f })
                worst = Mathf.Max(worst, ArcJump(cfg, shoulder, -70f, 130f, az, true, axisAzimuth: false));
            return worst;
        }

        static float ArcJump(BasisShoulderSweepConfig cfg, Vector3 shoulder, float from, float to, float fixedAngle, bool elbow, bool axisAzimuth)
        {
            float worst = 0f;
            Quaternion prev = Quaternion.identity; bool has = false;
            for (int t = 0; t <= 200; t++)
            {
                float v = Mathf.Lerp(from, to, t / 200f);
                Vector3 dir = axisAzimuth ? DirFromAzEl(v, fixedAngle, cfg.IsLeft) : DirFromAzEl(fixedAngle, v, cfg.IsLeft);
                BasisShoulderSolveCore.Solve(BuildInputDir(cfg, shoulder, dir, 0.9f, elbow, Quaternion.identity), out var res);
                if (has) worst = Mathf.Max(worst, Quaternion.Angle(prev, res.ShoulderRotation));
                prev = res.ShoulderRotation; has = true;
            }
            return worst;
        }

        // A jittery elbow tracker (the upper-arm direction wobbles) must not make the girdle jitter.
        static float NoisyElbowScan(BasisShoulderSweepConfig cfg, Vector3 shoulder)
        {
            float worst = 0f;
            var rng = new System.Random(7777);
            foreach (var azel in s_sampleAzEl)
            {
                Vector3 dir = DirFromAzEl(azel.x, azel.y, cfg.IsLeft);
                BasisShoulderSolveCore.Solve(BuildInputDir(cfg, shoulder, dir, 0.9f, true, Quaternion.identity), out var clean);
                for (int n = 0; n < 24; n++)
                {
                    Vector3 noise = new Vector3(
                        (float)(rng.NextDouble() * 2 - 1),
                        (float)(rng.NextDouble() * 2 - 1),
                        (float)(rng.NextDouble() * 2 - 1)) * 0.03f; // 3 cm elbow jitter
                    BasisShoulderSolveInput input = BuildInput(cfg, shoulder, (dir + noise).normalized, 0.9f, true, Quaternion.identity);
                    BasisShoulderSolveCore.Solve(input, out var noisy);
                    worst = Mathf.Max(worst, Quaternion.Angle(clean.ShoulderRotation, noisy.ShoulderRotation));
                }
            }
            return worst;
        }

        // The headline win: a bent arm (elbow raised out/up, hand hanging low near the body). The elbow
        // tracker must elevate the shoulder from the true upper-arm direction; the hand-target fallback,
        // seeing only the low hand, must NOT (its elevation stays small). Returns both elevations.
        static void BentArmScan(BasisShoulderSweepConfig cfg, Vector3 shoulder, out float elbowElevation, out float handElevation)
        {
            float upper = cfg.ArmLength * 0.5f;
            Vector3 elbowDir = DirFromAzEl(35f, 75f, cfg.IsLeft);     // elbow up and out
            Vector3 elbowPos = shoulder + elbowDir * upper;
            Vector3 handPos = shoulder + DirFromAzEl(20f, -75f, cfg.IsLeft) * (cfg.ArmLength * 0.55f); // hand low

            BasisShoulderSolveInput elbowIn = BuildRaw(cfg, shoulder, elbowPos, handPos, hasElbow: true, Quaternion.identity);
            BasisShoulderSolveCore.Solve(elbowIn, out var e);
            elbowElevation = e.Elevation;

            BasisShoulderSolveInput handIn = BuildRaw(cfg, shoulder, elbowPos, handPos, hasElbow: false, Quaternion.identity);
            BasisShoulderSolveCore.Solve(handIn, out var h);
            handElevation = h.Elevation;
        }

        // ---- input construction -------------------------------------------------------------------

        // Driver position taken straight from a chest-local direction at `reach` arm-lengths. In elbow
        // mode the direction is the elbow; in hand mode it is the hand target.
        static BasisShoulderSolveInput BuildInput(BasisShoulderSweepConfig cfg, Vector3 shoulder, Vector3 chestLocalDir, float reach, bool elbow, Quaternion chest)
        {
            Vector3 worldDir = chest * chestLocalDir;
            Vector3 driver = shoulder + worldDir * (reach * cfg.ArmLength);
            return BuildRaw(cfg, shoulder, elbow ? driver : shoulder, elbow ? shoulder : driver, elbow, chest);
        }

        static BasisShoulderSolveInput BuildInputDir(BasisShoulderSweepConfig cfg, Vector3 shoulder, Vector3 chestLocalDir, float reach, bool elbow, Quaternion chest)
        {
            return BuildInput(cfg, shoulder, chestLocalDir, reach, elbow, chest);
        }

        static BasisShoulderSolveInput BuildRaw(BasisShoulderSweepConfig cfg, Vector3 shoulder, Vector3 elbowPos, Vector3 handPos, bool hasElbow, Quaternion chest)
        {
            BasisShoulderSolveInput input = default;
            input.ShoulderPos = shoulder;
            input.HandTargetPos = handPos;
            input.ElbowPos = elbowPos;
            input.HasElbow = hasElbow;
            input.HasShoulderTracker = false;
            input.ChestRot = chest;
            input.TposeChestRot = Quaternion.identity;
            input.TposeShoulderRot = Quaternion.identity;
            input.TposeArmDirWorld = RestDir(cfg.IsLeft);
            input.TposeArmLength = cfg.ArmLength;
            input.ElevationFactor = cfg.ElevationFactor;
            input.ProtractionFactor = cfg.ProtractionFactor;
            input.CoupleRatio = cfg.CoupleRatio;
            input.MaxShoulderDeg = cfg.MaxShoulderDeg;
            input.TrackerFinal = Quaternion.identity;
            input.IsLeft = cfg.IsLeft;
            return input;
        }

        // Rest (bind) upper-arm direction in chest-local space: out to the side, slightly down/forward.
        static Vector3 RestDir(bool isLeft)
        {
            return new Vector3(isLeft ? -1f : 1f, -0.05f, 0.05f).normalized;
        }

        // Chest-local direction from azimuth (lateral->forward around chest-up) and elevation (above the
        // horizontal plane). az=0 is straight out to the side (the bind direction's plane).
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

        static readonly Vector2[] s_sampleAzEl =
        {
            new Vector2(0f, 0f), new Vector2(0f, 60f), new Vector2(45f, 30f), new Vector2(90f, 10f),
            new Vector2(90f, 70f), new Vector2(120f, 40f), new Vector2(-60f, -20f), new Vector2(30f, 100f),
            new Vector2(60f, -40f), new Vector2(135f, 20f),
        };

        static void Append(StringBuilder sb, float v) { sb.Append(F(v)).Append(','); }

        static string F(float v)
        {
            if (float.IsNaN(v)) return "nan";
            return v.ToString("0.######", CultureInfo.InvariantCulture);
        }

        static float Lerp01(float min, float max, int steps, int idx)
        {
            if (steps <= 1) return 0.5f * (min + max);
            return Mathf.Lerp(min, max, idx / (float)(steps - 1));
        }
    }
}
