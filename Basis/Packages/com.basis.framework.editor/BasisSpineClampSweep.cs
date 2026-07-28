using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using Basis.IK;

namespace Basis.IK.Debugging
{
    // Offline invariant sweep of the FBIK spine safety clamps on BasisFullIKConstraintJob
    // (ClampHipsAroundHead, EnforceSpineBendLimit, AntiContortionist, MitigateSpineBuckling, ClampRotation).
    // These are the "never let the torso contort" guards applied around the head/hips each frame, and they
    // encode hard limits ("bend never exceeds maxBendDeg", "distance stays within [min,max]*rest") that
    // nothing checked. Distinct from BasisSpineSweep, which tests the virtual-spine SYNTHESIS on the
    // neck->hips segment. Each clamp is verified to hold its limit, be idempotent (re-clamping a clamped
    // pose is a no-op) and leave an already-valid pose untouched. Pure math, runs off the main thread.
    public struct BasisSpineClampSweepConfig
    {
        public float RestDistance;
        public float MinFactor;
        public float MaxFactor;
        public int VerticalSteps;   // hips below head, 0.15..0.95 m
        public int LateralSteps;    // hips sideways from head, 0..0.8 m

        public static BasisSpineClampSweepConfig Default()
        {
            return new BasisSpineClampSweepConfig
            {
                RestDistance = 0.6f,
                MinFactor = 0.5f,
                MaxFactor = 1.15f,
                VerticalSteps = 11,
                LateralSteps = 11,
            };
        }
    }

    public struct BasisSpineClampSweepSummary
    {
        public bool Ok;
        public string Path;
        public int Cases;
        public int NaNCount;
        public float MaxHipsClampOverErrM;   // ClampHipsAroundHead distance outside [min,max]*rest
        public float MaxHipsClampDirErrDeg;  // ClampHipsAroundHead must keep the head->hips direction
        public float MaxHipsClampIdempotentM;
        public float MaxBendOverDeg;         // EnforceSpineBendLimit leaves a bend > maxBendDeg
        public float MaxBendIdempotentM;
        public float MaxAntiContortDeficitM; // AntiContortionist result closer than its facing-based min
        public float MaxAntiContortIdempotentM;
        public float MaxBucklingHorizM;      // MitigateSpineBuckling moved hips off the vertical axis
        public float MaxBucklingPushErrM;    // push magnitude vs the documented formula
        public float MaxClampRotOverDeg;     // ClampRotation result further than maxAngle from reference
        public float MaxClampRotIdempotentDeg;
        // Deep-crouch / inversion pass: as the head descends to and past hip level, the clamps must keep the
        // hips at or below the head (no flip) and move smoothly (no sideways/up fling).
        public float MaxHipsAboveHeadM;      // ClampHipsAroundHead output above the head (must be ~0)
        public float MaxBendAboveHeadM;      // EnforceSpineBendLimit output above the head (must be ~0)
        public float MaxCrouchStepM;         // worst hips jump per step across the crossing (fling/teleport)
        public float MaxChainAboveHeadM;     // full clamp chain (composed, pipeline order) output above the head (~0)
        public int Failures;
        public string Error;
    }

    public static class BasisSpineClampSweep
    {
        public const string DefaultFileName = "BasisSpineClampSweep.csv";

        // Facing pairs (head yaw, hips yaw) drive AntiContortionist's similarity term and double as the
        // current/reference pair for ClampRotation. Pitches add a non-yaw component.
        static readonly (float headYaw, float headPitch, float hipsYaw, float hipsPitch)[] k_Facings =
        {
            (0f, 0f, 0f, 0f), (0f, 0f, 90f, 0f), (0f, 0f, 180f, 0f),
            (0f, 20f, 45f, 0f), (30f, 0f, -60f, 15f), (0f, -25f, 200f, 0f),
        };
        static readonly float[] k_MaxBend = { 20f, 40f, 60f };
        static readonly float[] k_MaxAngle = { 15f, 45f, 90f };

        public static string DefaultPath() => System.IO.Path.Combine(Application.persistentDataPath, DefaultFileName);

        public static BasisSpineClampSweepSummary Run(BasisSpineClampSweepConfig cfg, string path)
        {
            var summary = new BasisSpineClampSweepSummary { Ok = false, Path = path };

            Vector3 head = new Vector3(0f, 1.5f, 0f);
            Vector3 up = Vector3.up;
            float rest = Mathf.Max(1e-3f, cfg.RestDistance);
            float minD = rest * cfg.MinFactor;
            float maxD = rest * cfg.MaxFactor;
            int vs = Mathf.Max(2, cfg.VerticalSteps);
            int ls = Mathf.Max(1, cfg.LateralSteps);

            int cases = 0, nan = 0, fails = 0;
            float mHipsOver = 0f, mHipsDir = 0f, mHipsIdem = 0f, mBendOver = 0f, mBendIdem = 0f;
            float mAntiDef = 0f, mAntiIdem = 0f, mBuckHoriz = 0f, mBuckPush = 0f, mRotOver = 0f, mRotIdem = 0f;
            float mHipsAbove = 0f, mBendAbove = 0f, mCrouchStep = 0f, mChainAbove = 0f;

            try
            {
                string dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                using (var w = new StreamWriter(path, false, Encoding.UTF8))
                {
                    w.WriteLine("# BasisSpineClampSweep " + System.DateTime.UtcNow.ToString("o") +
                                " rest=" + F(rest) + " min=" + F(minD) + " max=" + F(maxD));
                    w.WriteLine("v,lat,facing,maxbend,maxang,hips_over,hips_dir,hips_idem,bend_after,bend_over,bend_idem," +
                                "anti_def,anti_idem,buck_horiz,buck_push,rot_over,rot_idem,fail");

                    var sb = new StringBuilder(224);

                    for (int iv = 0; iv < vs; iv++)
                    {
                        float vDown = Mathf.Lerp(0.15f, 0.95f, iv / (float)(vs - 1));
                        for (int il = 0; il < ls; il++)
                        {
                            float lat = ls <= 1 ? 0f : Mathf.Lerp(0f, 0.8f, il / (float)(ls - 1));
                            Vector3 hips = head + new Vector3(lat, -vDown, 0f);

                            for (int fi = 0; fi < k_Facings.Length; fi++)
                            {
                                var fc = k_Facings[fi];
                                Quaternion headRot = Quaternion.Euler(fc.headPitch, fc.headYaw, 0f);
                                Quaternion hipsRot = Quaternion.Euler(fc.hipsPitch, fc.hipsYaw, 0f);
                                float maxBend = k_MaxBend[(iv + il + fi) % k_MaxBend.Length];
                                float maxAng = k_MaxAngle[(iv + fi) % k_MaxAngle.Length];

                                bool fail = EvaluateCase(head, hips, headRot, hipsRot, up, rest, cfg.MinFactor, cfg.MaxFactor,
                                    minD, maxD, maxBend, maxAng,
                                    out float hipsOver, out float hipsDir, out float hipsIdem,
                                    out float bendAfter, out float bendOver, out float bendIdem,
                                    out float antiDef, out float antiIdem, out float buckHoriz, out float buckPush,
                                    out float rotOver, out float rotIdem, out bool hadNaN);

                                cases++;
                                if (hadNaN) nan++;
                                if (fail) fails++;
                                if (hipsOver > mHipsOver) mHipsOver = hipsOver;
                                if (hipsDir > mHipsDir) mHipsDir = hipsDir;
                                if (hipsIdem > mHipsIdem) mHipsIdem = hipsIdem;
                                if (bendOver > mBendOver) mBendOver = bendOver;
                                if (bendIdem > mBendIdem) mBendIdem = bendIdem;
                                if (antiDef > mAntiDef) mAntiDef = antiDef;
                                if (antiIdem > mAntiIdem) mAntiIdem = antiIdem;
                                if (buckHoriz > mBuckHoriz) mBuckHoriz = buckHoriz;
                                if (buckPush > mBuckPush) mBuckPush = buckPush;
                                if (rotOver > mRotOver) mRotOver = rotOver;
                                if (rotIdem > mRotIdem) mRotIdem = rotIdem;

                                sb.Clear();
                                Append(sb, vDown); Append(sb, lat);
                                sb.Append(fi).Append(',');
                                Append(sb, maxBend); Append(sb, maxAng);
                                Append(sb, hipsOver); Append(sb, hipsDir); Append(sb, hipsIdem);
                                Append(sb, bendAfter); Append(sb, bendOver); Append(sb, bendIdem);
                                Append(sb, antiDef); Append(sb, antiIdem); Append(sb, buckHoriz); Append(sb, buckPush);
                                Append(sb, rotOver); Append(sb, rotIdem);
                                sb.Append(fail ? '1' : '0');
                                w.WriteLine(sb.ToString());
                            }
                        }
                    }

                    // ---- Pass B: deep crouch / inversion ----
                    // Sweep the hips from below the head up THROUGH head height to above it (the head
                    // descending to and past hip level in a deep crouch). The clamps must keep the hips at or
                    // below the head (no flip) and move smoothly across the crossing (no sideways/up fling).
                    w.WriteLine("# Pass B: deep crouch -- hips swept below->above head; clamps must keep hips at/below head, no fling");
                    w.WriteLine("crouch_vy,lat,maxbend,hips_above,bend_above,chain_above,clamp_step,fail");
                    int crouchSteps = 81;
                    foreach (float lat in new[] { 0.1f, 0.25f, 0.45f })
                    {
                        foreach (float maxBend in new[] { 30f, 60f, 90f })
                        {
                            Vector3 prevClamp = Vector3.zero;
                            bool havePrev = false;
                            for (int c = 0; c < crouchSteps; c++)
                            {
                                float vy = Mathf.Lerp(-0.7f, 0.3f, c / (float)(crouchSteps - 1)); // hips.y - head.y
                                Vector3 hipsB = head + new Vector3(lat, vy, 0f);

                                Vector3 hcB = BasisEerieMovement.ClampHipsAroundHead(head, hipsB, rest, cfg.MinFactor, cfg.MaxFactor, up);
                                Vector3 beB = BasisEerieMovement.EnforceSpineBendLimit(head, hipsB, maxBend, up);

                                // Full clamp chain in pipeline order (head forward-facing). Exercises AntiContortionist
                                // and MitigateSpineBuckling in the inverted regime too, and proves the COMPOSED result
                                // the user actually gets keeps the hips at/below the head.
                                Vector3 ch = BasisEerieMovement.AntiContortionist(head, Quaternion.identity, hipsB, Quaternion.identity, rest);
                                ch = BasisEerieMovement.MitigateSpineBuckling(head, Quaternion.identity, ch, rest, up);
                                ch = BasisEerieMovement.EnforceSpineBendLimit(head, ch, maxBend, up);
                                ch = BasisEerieMovement.ClampHipsAroundHead(head, ch, rest, cfg.MinFactor, cfg.MaxFactor, up);

                                bool nanB = !Finite(hcB) || !Finite(beB) || !Finite(ch);
                                float hcAbove = Finite(hcB) ? Vector3.Dot(hcB - head, up) : 0f;
                                float beAbove = Finite(beB) ? Vector3.Dot(beB - head, up) : 0f;
                                float chainAbove = Finite(ch) ? Vector3.Dot(ch - head, up) : 0f;
                                float step = (havePrev && Finite(hcB)) ? (hcB - prevClamp).magnitude : 0f;
                                prevClamp = hcB;
                                havePrev = true;

                                cases++;
                                if (nanB) nan++;
                                if (hcAbove > mHipsAbove) mHipsAbove = hcAbove;
                                if (beAbove > mBendAbove) mBendAbove = beAbove;
                                if (chainAbove > mChainAbove) mChainAbove = chainAbove;
                                if (step > mCrouchStep) mCrouchStep = step;

                                bool failB = nanB || hcAbove > 1e-3f || beAbove > 1e-3f || chainAbove > 1e-3f || step > 0.3f;
                                if (failB) fails++;

                                sb.Clear();
                                Append(sb, vy); Append(sb, lat); Append(sb, maxBend);
                                Append(sb, hcAbove); Append(sb, beAbove); Append(sb, chainAbove); Append(sb, step);
                                sb.Append(failB ? '1' : '0');
                                w.WriteLine(sb.ToString());
                            }
                        }
                    }
                }

                summary.Ok = true;
                summary.Cases = cases;
                summary.NaNCount = nan;
                summary.MaxHipsClampOverErrM = mHipsOver;
                summary.MaxHipsClampDirErrDeg = mHipsDir;
                summary.MaxHipsClampIdempotentM = mHipsIdem;
                summary.MaxBendOverDeg = mBendOver;
                summary.MaxBendIdempotentM = mBendIdem;
                summary.MaxAntiContortDeficitM = mAntiDef;
                summary.MaxAntiContortIdempotentM = mAntiIdem;
                summary.MaxBucklingHorizM = mBuckHoriz;
                summary.MaxBucklingPushErrM = mBuckPush;
                summary.MaxClampRotOverDeg = mRotOver;
                summary.MaxClampRotIdempotentDeg = mRotIdem;
                summary.MaxHipsAboveHeadM = mHipsAbove;
                summary.MaxBendAboveHeadM = mBendAbove;
                summary.MaxCrouchStepM = mCrouchStep;
                summary.MaxChainAboveHeadM = mChainAbove;
                summary.Failures = fails;
            }
            catch (System.Exception e)
            {
                summary.Ok = false;
                summary.Error = e.Message;
            }

            return summary;
        }

        static bool EvaluateCase(Vector3 head, Vector3 hips, Quaternion headRot, Quaternion hipsRot, Vector3 up,
            float rest, float minFactor, float maxFactor, float minD, float maxD, float maxBend, float maxAng,
            out float hipsOver, out float hipsDir, out float hipsIdem,
            out float bendAfter, out float bendOver, out float bendIdem,
            out float antiDef, out float antiIdem, out float buckHoriz, out float buckPush,
            out float rotOver, out float rotIdem, out bool hadNaN)
        {
            hipsOver = hipsDir = hipsIdem = bendAfter = bendOver = bendIdem = 0f;
            antiDef = antiIdem = buckHoriz = buckPush = rotOver = rotIdem = 0f;
            hadNaN = false;

            // ClampHipsAroundHead: distance to head clamped to [minD,maxD] along the head->hips ray.
            Vector3 hc = BasisEerieMovement.ClampHipsAroundHead(head, hips, rest, minFactor, maxFactor, up);
            if (!Finite(hc)) hadNaN = true;
            else
            {
                float d = (hc - head).magnitude;
                hipsOver = Mathf.Max(Relu(minD - d), Relu(d - maxD));
                hipsDir = Vector3.Angle((hips - head).normalized, (hc - head).normalized);
                Vector3 hc2 = BasisEerieMovement.ClampHipsAroundHead(head, hc, rest, minFactor, maxFactor, up);
                if (!Finite(hc2)) hadNaN = true; else hipsIdem = (hc2 - hc).magnitude;
            }

            // EnforceSpineBendLimit: the head->hips bend may not exceed maxBend (configs always have a
            // vertical component, so the clamp is always applicable).
            Vector3 be = BasisEerieMovement.EnforceSpineBendLimit(head, hips, maxBend, up);
            if (!Finite(be)) hadNaN = true;
            else
            {
                bendAfter = BendAngle(head, be, up);
                bendOver = Relu(bendAfter - maxBend);
                Vector3 be2 = BasisEerieMovement.EnforceSpineBendLimit(head, be, maxBend, up);
                if (!Finite(be2)) hadNaN = true; else bendIdem = (be2 - be).magnitude;
            }

            // AntiContortionist: never closer than the facing-similarity minimum distance.
            Vector3 ac = BasisEerieMovement.AntiContortionist(head, headRot, hips, hipsRot, rest);
            if (!Finite(ac)) hadNaN = true;
            else
            {
                float sim = Vector3.Dot(headRot * Vector3.forward, hipsRot * Vector3.forward);
                float minDistFactor = Mathf.Lerp(0.2f, 0.85f, Mathf.Clamp01((sim + 1f) * 0.5f));
                float minDist = rest * minDistFactor;
                antiDef = Relu(minDist - (ac - head).magnitude);
                Vector3 ac2 = BasisEerieMovement.AntiContortionist(head, headRot, ac, hipsRot, rest);
                if (!Finite(ac2)) hadNaN = true; else antiIdem = (ac2 - ac).magnitude;
            }

            // MitigateSpineBuckling: pushes hips straight down (along playerUp only) by the compression
            // formula; an uncompressed pose is untouched.
            Vector3 mb = BasisEerieMovement.MitigateSpineBuckling(head, hipsRot, hips, rest, up);
            if (!Finite(mb)) hadNaN = true;
            else
            {
                Vector3 moved = mb - hips;
                buckHoriz = (moved - up * Vector3.Dot(moved, up)).magnitude;
                float curDist = (hips - head).magnitude;
                float expectPush = 0f;
                if (curDist < rest && curDist > 1e-5f)
                {
                    float tension = Mathf.Clamp01(Vector3.Dot(hipsRot * Vector3.up, (head - hips).normalized));
                    float compression = 1f - (curDist / rest);
                    expectPush = compression * tension * rest * 0.5f;
                }
                buckPush = Mathf.Abs(Vector3.Dot(moved, -up) - expectPush);
            }

            // ClampRotation: result is at most maxAng from the reference; current/reference taken from the
            // facing pair so the clamp sees a real, often-large delta.
            Quaternion rc = BasisEerieMovement.ClampRotation(hipsRot, headRot, maxAng);
            if (!FiniteQ(rc)) hadNaN = true;
            else
            {
                rotOver = Relu(Quaternion.Angle(headRot, rc) - maxAng);
                Quaternion rc2 = BasisEerieMovement.ClampRotation(rc, headRot, maxAng);
                if (!FiniteQ(rc2)) hadNaN = true; else rotIdem = Quaternion.Angle(rc, rc2);
            }

            bool fail = hadNaN
                || hipsOver > 1e-4f || hipsDir > 0.1f || hipsIdem > 1e-4f
                || bendOver > 0.1f || bendIdem > 1e-4f
                || antiDef > 1e-4f || antiIdem > 1e-4f
                || buckHoriz > 1e-4f || buckPush > 1e-4f
                || rotOver > 0.1f || rotIdem > 0.1f;
            return fail;
        }

        // Bend of head->hips measured the way EnforceSpineBendLimit defines it: lateral vs vertical (-up).
        static float BendAngle(Vector3 head, Vector3 hips, Vector3 up)
        {
            Vector3 diff = hips - head;
            float vDot = Vector3.Dot(diff, -up);
            Vector3 vert = -up * vDot;
            Vector3 lat = diff - vert;
            return Mathf.Atan2(lat.magnitude, Mathf.Abs(vDot)) * Mathf.Rad2Deg;
        }

        static float Relu(float v) => v > 0f ? v : 0f;
        static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        static bool Finite(Vector3 v) => Finite(v.x) && Finite(v.y) && Finite(v.z);
        static bool FiniteQ(Quaternion q) => Finite(q.x) && Finite(q.y) && Finite(q.z) && Finite(q.w);

        static void Append(StringBuilder sb, float v) { sb.Append(F(v)).Append(','); }

        static string F(float v)
        {
            if (float.IsNaN(v)) return "nan";
            return v.ToString("0.######", CultureInfo.InvariantCulture);
        }
    }
}
