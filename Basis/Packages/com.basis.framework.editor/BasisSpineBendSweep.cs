using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using Basis.IK;

namespace Basis.IK.Debugging
{
    // Offline sweep of BasisSpineBendCore (the per-axis spine/upperChest bend distribution). Two passes:
    //  (A) an invariant grid over head pose x squish x anatomy that checks the asymmetric flexion clamp
    //      holds per axis, the rest deadband zeroes the bend (pitch/roll) contribution, the squish
    //      multiplier stays in [1-boost, 1+boost], and PelvicTwistRouting keeps the 25/75 spine/upperChest
    //      yaw split; and
    //  (B) a head-yaw continuity scan ACROSS CENTER with a yawed hips bind -- the documented bug locus
    //      where the spine twist used to snap +/-360 deg at the atan2 branch cut. The bind-cancellation
    //      must keep twist continuous there. Same math as the live DistributeSpineBend.
    public struct BasisSpineBendSweepConfig
    {
        public int HeadGridSteps;   // per-axis head-target offset grid (pass A)
        public int TwistYawSteps;   // head-yaw samples across center (pass B)

        public static BasisSpineBendSweepConfig Default()
        {
            return new BasisSpineBendSweepConfig { HeadGridSteps = 7, TwistYawSteps = 241 };
        }
    }

    public struct BasisSpineBendSweepSummary
    {
        public bool Ok;
        public string Path;
        public int Cases;
        public int NaNCount;
        public float MaxClampOverDeg;     // euler past the asymmetric bound (spine or upperChest)
        public float MaxDeadbandLeakDeg;  // pitch/roll bend present while gated off at rest
        public float MaxSquishErr;        // squish multiplier outside [1-boost, 1+boost]
        public float MaxYawSplitErr;      // PelvicTwistRouting off the 25/75 spine:upperChest split
        public float TwistMaxJumpDeg;     // worst frame-to-frame raw twist jump across center (branch snap)
        public float LookDownTwistMaxJumpDeg; // worst twist jump as the gaze pitches through vertical (look-down snap)
        public float LookDownBendDriftDeg;    // worst forward-bend drift as the gaze pitches (the fade must be twist-only)
        public float MaxCouplingErrDeg;     // bend->twist coupling: euler.y vs coupling * euler.z (forward-facing lean)
        public float MaxCouplingBaselineDeg; // yaw produced at coupling=0 with a forward-facing lean (must be ~0)
        public int Failures;
        public string Error;
    }

    public static class BasisSpineBendSweep
    {
        public const string DefaultFileName = "BasisSpineBendSweep.csv";
        const float k_RestLen = 0.6f;
        const float k_MaxFwd = 40f, k_MaxBack = 15f, k_MaxLat = 25f;
        static readonly float[] k_Boost = { 0f, 0.5f, 1f, 2f };
        static readonly Quaternion[] k_HeadRots =
        {
            Quaternion.identity, Quaternion.Euler(0f, 60f, 0f), Quaternion.Euler(40f, 0f, 0f),
        };

        public static string DefaultPath() => System.IO.Path.Combine(Application.persistentDataPath, DefaultFileName);

        public static BasisSpineBendSweepSummary Run(BasisSpineBendSweepConfig cfg, string path)
        {
            var summary = new BasisSpineBendSweepSummary { Ok = false, Path = path };

            Vector3 hips = Vector3.zero;
            int gs = Mathf.Max(2, cfg.HeadGridSteps);
            int ts = Mathf.Max(3, cfg.TwistYawSteps);

            int cases = 0, nan = 0, fails = 0;
            float mClamp = 0f, mLeak = 0f, mSquish = 0f, mSplit = 0f, mTwistJump = 0f;
            float mCoupling = 0f, mCouplingBase = 0f, mLookDownJump = 0f, mLookDownBendDrift = 0f;

            try
            {
                string dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                using (var w = new StreamWriter(path, false, Encoding.UTF8))
                {
                    w.WriteLine("# BasisSpineBendSweep " + System.DateTime.UtcNow.ToString("o"));
                    w.WriteLine("kind,p0,p1,p2,p3,bend_gate,squish,twistY,spine_over,upper_over,deadband_leak,squish_err,yaw_split,twist_jump,fail");
                    var sb = new StringBuilder(192);

                    // ---- Pass A: invariant grid (hips/bind identity) ----
                    Quaternion hipsRot = Quaternion.identity;
                    Vector3 chestPos = hips + Vector3.up * 0.2f;
                    for (int bi = 0; bi < k_Boost.Length; bi++)
                    {
                        float boost = k_Boost[bi];
                        for (int anat = 0; anat < 4; anat++)
                        {
                            bool diff = (anat & 1) != 0;
                            bool pelvic = (anat & 2) != 0;
                            for (int hr = 0; hr < k_HeadRots.Length; hr++)
                            {
                                Quaternion headRot = k_HeadRots[hr];
                                for (int ix = 0; ix < gs; ix++)
                                {
                                    float ox = Mathf.Lerp(-0.5f, 0.5f, ix / (float)(gs - 1));
                                    for (int iy = 0; iy < gs; iy++)
                                    {
                                        float oy = Mathf.Lerp(-0.5f, 0.5f, iy / (float)(gs - 1));
                                        for (int iz = 0; iz < gs; iz++)
                                        {
                                            float oz = Mathf.Lerp(-0.5f, 0.5f, iz / (float)(gs - 1));
                                            Vector3 head = hips + Vector3.up * k_RestLen + new Vector3(ox, oy, oz);

                                            BasisSpineBendInput input = MakeInput(hipsRot, hips, chestPos, head,
                                                Quaternion.identity, headRot, boost, diff, pelvic, k_MaxLat);
                                            BasisSpineBendCore.Solve(input, out BasisSpineBendResult r);

                                            bool hadNaN = false;
                                            float spineOver = 0f, upperOver = 0f, leak = 0f;
                                            if (r.WriteSpine)
                                            {
                                                if (!Finite(r.SpineEuler)) hadNaN = true;
                                                spineOver = ClampOver(r.SpineEuler);
                                                if (r.BendGate <= 0f) leak = Mathf.Max(leak, Mathf.Abs(r.SpineEuler.x) + Mathf.Abs(r.SpineEuler.z));
                                            }
                                            if (r.WriteUpper)
                                            {
                                                if (!Finite(r.UpperEuler)) hadNaN = true;
                                                upperOver = ClampOver(r.UpperEuler);
                                                if (r.BendGate <= 0f) leak = Mathf.Max(leak, Mathf.Abs(r.UpperEuler.x) + Mathf.Abs(r.UpperEuler.z));
                                            }
                                            if (float.IsNaN(r.SquishMult) || float.IsNaN(r.TwistY)) hadNaN = true;

                                            float lo = Mathf.Max(0f, 1f - boost), hi = 1f + boost;
                                            float squishErr = Relu(lo - r.SquishMult) + Relu(r.SquishMult - hi);

                                            float yawSplit = 0f;
                                            if (pelvic)
                                            {
                                                float tot = r.SpineYawEff + r.UpperYawEff;
                                                if (tot > 1e-4f) yawSplit = Mathf.Abs(r.UpperYawEff - 3f * r.SpineYawEff);
                                            }

                                            cases++;
                                            if (hadNaN) nan++;
                                            float clampOver = Mathf.Max(spineOver, upperOver);
                                            if (clampOver > mClamp) mClamp = clampOver;
                                            if (leak > mLeak) mLeak = leak;
                                            if (squishErr > mSquish) mSquish = squishErr;
                                            if (yawSplit > mSplit) mSplit = yawSplit;

                                            bool fail = hadNaN || clampOver > 0.05f || leak > 0.05f || squishErr > 1e-3f || yawSplit > 1e-3f;
                                            if (fail) fails++;

                                            sb.Clear();
                                            sb.Append("grid,");
                                            Append(sb, boost); Append(sb, anat); Append(sb, hr); Append(sb, ox);
                                            Append(sb, r.BendGate); Append(sb, r.SquishMult); Append(sb, r.TwistY);
                                            Append(sb, spineOver); Append(sb, upperOver); Append(sb, leak);
                                            Append(sb, squishErr); Append(sb, yawSplit); Append(sb, 0f);
                                            sb.Append(fail ? '1' : '0');
                                            w.WriteLine(sb.ToString());
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // ---- Pass B: twist continuity across center, bind == hips (rig-correct), incl. yawed bind ----
                    foreach (float bindYaw in new[] { 0f, 180f })
                    {
                        Quaternion bindRot = Quaternion.Euler(0f, bindYaw, 0f);
                        Vector3 chest = hips + Vector3.up * 0.2f;
                        Vector3 head = hips + Vector3.up * k_RestLen; // above hips -> isolate twist
                        float prevTwist = 0f; bool havePrev = false;

                        for (int pi = 0; pi < ts; pi++)
                        {
                            float phi = Mathf.Lerp(-120f, 120f, pi / (float)(ts - 1));
                            Quaternion headRot = Quaternion.Euler(0f, phi, 0f);
                            BasisSpineBendInput input = MakeInput(bindRot, hips, chest, head, bindRot, headRot,
                                0f, false, false, 90f); // maxLat 90 so raw twist isn't the limiter; weights 1
                            BasisSpineBendCore.Solve(input, out BasisSpineBendResult r);

                            bool hadNaN = float.IsNaN(r.TwistY) || float.IsInfinity(r.TwistY);
                            float jump = 0f;
                            if (havePrev && !hadNaN) jump = Mathf.Abs(r.TwistY - prevTwist);
                            prevTwist = r.TwistY; havePrev = true;

                            cases++;
                            if (hadNaN) nan++;
                            if (jump > mTwistJump) mTwistJump = jump;
                            bool fail = hadNaN || jump > 30f;
                            if (fail) fails++;

                            sb.Clear();
                            sb.Append("twist,");
                            Append(sb, bindYaw); Append(sb, phi); Append(sb, 0f); Append(sb, 0f);
                            Append(sb, r.BendGate); Append(sb, r.SquishMult); Append(sb, r.TwistY);
                            Append(sb, 0f); Append(sb, 0f); Append(sb, 0f); Append(sb, 0f); Append(sb, 0f);
                            Append(sb, jump);
                            sb.Append(fail ? '1' : '0');
                            w.WriteLine(sb.ToString());
                        }
                    }

                    // ---- Pass C: bend->twist coupling (a lateral lean adds a little same-side axial rotation) ----
                    // Head faces forward (no facing-twist) and the hips bind is identity, so euler.y is ENTIRELY
                    // the coupling term: e.y == coupling * e.z. Small lateral offsets keep both within the lateral
                    // clamp so the relationship is exact; coupling=0 must leave euler.y at zero.
                    foreach (float coupling in new[] { 0f, 0.15f, 0.3f })
                    {
                        Vector3 chestC = hips + Vector3.up * 0.2f;
                        foreach (float ox in new[] { 0.08f, 0.16f, 0.24f })
                        {
                            Vector3 head = hips + Vector3.up * k_RestLen + new Vector3(ox, 0f, 0f);
                            BasisSpineBendInput input = MakeInput(Quaternion.identity, hips, chestC, head,
                                Quaternion.identity, Quaternion.identity, 0f, false, false, 90f);
                            input.BendTwistCoupling = coupling;
                            BasisSpineBendCore.Solve(input, out BasisSpineBendResult r);

                            float spineErr = r.WriteSpine ? Mathf.Abs(r.SpineEuler.y - coupling * r.SpineEuler.z) : 0f;
                            float upperErr = r.WriteUpper ? Mathf.Abs(r.UpperEuler.y - coupling * r.UpperEuler.z) : 0f;
                            float err = Mathf.Max(spineErr, upperErr);
                            float baseY = coupling == 0f
                                ? Mathf.Max(r.WriteSpine ? Mathf.Abs(r.SpineEuler.y) : 0f, r.WriteUpper ? Mathf.Abs(r.UpperEuler.y) : 0f)
                                : 0f;
                            bool hadNaN = (r.WriteSpine && !Finite(r.SpineEuler)) || (r.WriteUpper && !Finite(r.UpperEuler));

                            cases++;
                            if (hadNaN) nan++;
                            if (err > mCoupling) mCoupling = err;
                            if (baseY > mCouplingBase) mCouplingBase = baseY;
                            bool fail = hadNaN || err > 0.05f || baseY > 0.05f;
                            if (fail) fails++;

                            sb.Clear();
                            sb.Append("couple,");
                            Append(sb, coupling); Append(sb, ox); Append(sb, 0f); Append(sb, 0f);
                            Append(sb, r.BendGate); Append(sb, r.SquishMult); Append(sb, r.TwistY);
                            Append(sb, r.WriteSpine ? r.SpineEuler.y : 0f); Append(sb, r.WriteSpine ? r.SpineEuler.z : 0f);
                            Append(sb, err); Append(sb, baseY); Append(sb, 0f); Append(sb, 0f);
                            sb.Append(fail ? '1' : '0');
                            w.WriteLine(sb.ToString());
                        }
                    }

                    // ---- Pass D: look-down twist continuity (head PITCH across vertical) ----
                    // The facing azimuth (twistY) is singular at vertical gaze -- looking straight down
                    // collapses the head-forward's horizontal projection, so the azimuth flips ~180 deg
                    // across the pole and snapped the chest/upperChest sideways. The fade must keep the
                    // applied twist continuous as the gaze pitches through straight-down. (Pass B covers
                    // the same branch cut for horizontal turning; this is the look-DOWN axis.)
                    {
                        Vector3 chestD = hips + Vector3.up * 0.2f;
                        Vector3 headD = hips + Vector3.up * k_RestLen; // above hips -> isolate twist
                        foreach (float yaw in new[] { 0f, 20f, -20f })
                        {
                            float prevTwist = 0f; bool havePrev = false;
                            for (int pi = 0; pi < ts; pi++)
                            {
                                float phi = Mathf.Lerp(-95f, 95f, pi / (float)(ts - 1));
                                Quaternion headRot = Quaternion.AngleAxis(yaw, Vector3.up) * Quaternion.AngleAxis(phi, Vector3.right);
                                BasisSpineBendInput input = MakeInput(Quaternion.identity, hips, chestD, headD,
                                    Quaternion.identity, headRot, 0f, false, false, 90f); // weights 1, maxLat 90: read the raw twist
                                BasisSpineBendCore.Solve(input, out BasisSpineBendResult r);

                                bool hadNaN = float.IsNaN(r.TwistY) || float.IsInfinity(r.TwistY);
                                float jump = 0f;
                                if (havePrev && !hadNaN) jump = Mathf.Abs(r.TwistY - prevTwist);
                                prevTwist = r.TwistY; havePrev = true;

                                cases++;
                                if (hadNaN) nan++;
                                if (jump > mLookDownJump) mLookDownJump = jump;
                                bool fail = hadNaN || jump > 30f;
                                if (fail) fails++;

                                sb.Clear();
                                sb.Append("lookdown,");
                                Append(sb, yaw); Append(sb, phi); Append(sb, 0f); Append(sb, 0f);
                                Append(sb, r.BendGate); Append(sb, r.SquishMult); Append(sb, r.TwistY);
                                Append(sb, 0f); Append(sb, 0f); Append(sb, 0f); Append(sb, 0f); Append(sb, 0f);
                                Append(sb, jump);
                                sb.Append(fail ? '1' : '0');
                                w.WriteLine(sb.ToString());
                            }
                        }
                    }

                    // ---- Pass E: look-down fade isolation (the fade damps only the twist, not the lean) ----
                    // Hold the head leaned forward (a real forward bend) and pitch the head ROTATION through
                    // vertical, which drives the twist fade. The forward/lateral bend is position-derived, so
                    // it must not move while the twist fades; drift is the worst forward-bend deviation. A
                    // regression that let the fade touch the bend would hunch/un-hunch the spine on look-down.
                    {
                        Vector3 chestE = hips + Vector3.up * 0.2f;
                        Vector3 headE = hips + Vector3.up * k_RestLen + Vector3.forward * 0.3f;
                        float baseBendX = 0f; bool haveBase = false;
                        for (int pi = 0; pi < ts; pi++)
                        {
                            float phi = Mathf.Lerp(0f, 95f, pi / (float)(ts - 1));
                            Quaternion headRot = Quaternion.AngleAxis(20f, Vector3.up) * Quaternion.AngleAxis(phi, Vector3.right);
                            BasisSpineBendInput input = MakeInput(Quaternion.identity, hips, chestE, headE,
                                Quaternion.identity, headRot, 0.5f, false, false, k_MaxLat);
                            BasisSpineBendCore.Solve(input, out BasisSpineBendResult r);

                            float bendX = r.WriteSpine ? r.SpineEuler.x : 0f;
                            if (!haveBase) { baseBendX = bendX; haveBase = true; }
                            float drift = Mathf.Abs(bendX - baseBendX);

                            bool hadNaN = (r.WriteSpine && !Finite(r.SpineEuler)) || float.IsNaN(r.TwistY);
                            cases++;
                            if (hadNaN) nan++;
                            if (drift > mLookDownBendDrift) mLookDownBendDrift = drift;
                            bool fail = hadNaN || drift > 0.5f;
                            if (fail) fails++;

                            sb.Clear();
                            sb.Append("fadeiso,");
                            Append(sb, phi); Append(sb, bendX); Append(sb, 0f); Append(sb, 0f);
                            Append(sb, r.BendGate); Append(sb, r.SquishMult); Append(sb, r.TwistY);
                            Append(sb, drift); Append(sb, 0f); Append(sb, 0f); Append(sb, 0f); Append(sb, 0f);
                            Append(sb, 0f);
                            sb.Append(fail ? '1' : '0');
                            w.WriteLine(sb.ToString());
                        }
                    }
                }

                summary.Ok = true;
                summary.Cases = cases;
                summary.NaNCount = nan;
                summary.MaxClampOverDeg = mClamp;
                summary.MaxDeadbandLeakDeg = mLeak;
                summary.MaxSquishErr = mSquish;
                summary.MaxYawSplitErr = mSplit;
                summary.TwistMaxJumpDeg = mTwistJump;
                summary.LookDownTwistMaxJumpDeg = mLookDownJump;
                summary.LookDownBendDriftDeg = mLookDownBendDrift;
                summary.MaxCouplingErrDeg = mCoupling;
                summary.MaxCouplingBaselineDeg = mCouplingBase;
                summary.Failures = fails;
            }
            catch (System.Exception e)
            {
                summary.Ok = false;
                summary.Error = e.Message;
            }

            return summary;
        }

        static BasisSpineBendInput MakeInput(Quaternion hipsRot, Vector3 hipsPos, Vector3 chestPos, Vector3 head,
            Quaternion bind, Quaternion headRot, float boost, bool diff, bool pelvic, float maxLat)
        {
            BasisSpineBendInput input;
            input.HipsRot = hipsRot;
            input.HipsPos = hipsPos;
            input.ChestPos = chestPos;
            input.SmoothedHead = head;
            input.HipsBind = bind;
            input.HeadTargetRot = headRot;
            input.SpineMaxForwardDeg = k_MaxFwd;
            input.SpineMaxBackwardDeg = k_MaxBack;
            input.SpineMaxLateralDeg = maxLat;
            input.SpineBendPitch = 1f;
            input.SpineBendYaw = 1f;
            input.SpineBendRoll = 1f;
            input.UpperBendPitch = 1f;
            input.UpperBendYaw = 1f;
            input.UpperBendRoll = 1f;
            input.AnatDifferentialStiffness = diff;
            input.AnatPelvicTwistRouting = pelvic;
            input.BendTwistCoupling = 0f;
            input.SquishBoost = boost;
            input.RestLen = k_RestLen;
            input.HasSpine = true;
            input.HasUpper = true;
            return input;
        }

        // Only summarised for pass A (which uses k_MaxLat); pass B euler is never fed here.
        static float ClampOver(Vector3 e)
        {
            float ox = e.x > 0f ? Relu(e.x - k_MaxFwd) : Relu(-k_MaxBack - e.x);
            float oy = Relu(Mathf.Abs(e.y) - k_MaxLat);
            float oz = Relu(Mathf.Abs(e.z) - k_MaxLat);
            return Mathf.Max(ox, Mathf.Max(oy, oz));
        }

        static float Relu(float v) => v > 0f ? v : 0f;
        static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        static bool Finite(Vector3 v) => Finite(v.x) && Finite(v.y) && Finite(v.z);

        static void Append(StringBuilder sb, float v) { sb.Append(F(v)).Append(','); }

        static string F(float v)
        {
            if (float.IsNaN(v)) return "nan";
            return v.ToString("0.######", CultureInfo.InvariantCulture);
        }
    }
}
