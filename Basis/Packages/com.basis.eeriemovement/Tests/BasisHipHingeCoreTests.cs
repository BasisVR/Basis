using System.Collections.Generic;
using System.IO;
using Basis.IK.Mocap;
using NUnit.Framework;
using UnityEngine;
using Basis.IK;

namespace Basis.Tests.IK
{
    using BasisMotionClip = Basis.IK.Mocap.BasisMotionClip;

    /// <summary>
    /// The hip hinge -- the pelvis' share of a bend-over (lumbopelvic rhythm). Past StartDeg of head-over-hips
    /// forward lean the pelvis follows the lean (PelvisFollowSlope ~1.0), eased toward MaxAddDeg. The curve is
    /// FITTED to real humans; the corpus test at the bottom checks it actually reproduces them.
    ///
    /// ⚠️ GATING lives in the JOB, not this core: BasisFullBodyIK.SolveSpine calls ApplyHipHinge ONLY when
    /// there is no hip tracker -- a tracked pelvis is the user's own and must feed straight through.
    /// </summary>
    public sealed class BasisHipHingeCoreTests
    {
        // hips at origin, head leaned `deg` forward (+Z) off vertical (+Y). => the core reads lean == deg.
        static BasisHipHingeInput Lean(float deg, float start = 30f, float maxAdd = 50f)
        {
            float r = deg * Mathf.Deg2Rad;
            return new BasisHipHingeInput
            {
                HipsPos = Vector3.zero,
                HeadPos = new Vector3(0f, Mathf.Cos(r), Mathf.Sin(r)),
                HipsRot = Quaternion.identity,
                PlayerUp = Vector3.up,
                StartDeg = start,
                MaxAddDeg = maxAdd,
            };
        }

        [Test]
        public void BelowStart_TheHingeDoesNothing()
        {
            BasisHipHingeCore.Solve(Lean(20f, start: 30f), out BasisHipHingeResult r);
            Assert.IsFalse(r.Applied, "hinge fired below the start angle");
            Assert.AreEqual(0f, r.AddDeg, 1e-4f);
            Assert.AreEqual(0f, Quaternion.Angle(Quaternion.identity, r.HipsRot), 1e-4f);
        }

        [Test]
        public void PastStart_ThePelvisFollowsTheLeanOneForOne()
        {
            // lean 50, start 30, cap 50 -> excess 20 (below the 0.8*cap=40 soft point) -> pelvis takes ALL 20.
            // (The OLD model took half -- 10. The corpus says the pelvis takes essentially all of it.)
            BasisHipHingeCore.Solve(Lean(50f, start: 30f, maxAdd: 50f), out BasisHipHingeResult r);
            Assert.IsTrue(r.Applied);
            Assert.AreEqual(20f, r.AddDeg, 0.2f, "the pelvis is not following the lean one-for-one");
            Assert.AreEqual(50f, r.LeanDeg, 0.1f);
        }

        [Test]
        public void ApproachingTheCap_ItEasesAndNeverReachesIt()
        {
            // lean 85, start 30, cap 50 -> excess 55, past the soft point -> eased to ~46, strictly under 50.
            BasisHipHingeCore.Solve(Lean(85f, start: 30f, maxAdd: 50f), out BasisHipHingeResult r);
            Assert.Greater(r.AddDeg, 40f, "the deep-bend tilt collapsed");
            Assert.Less(r.AddDeg, 50f, "the tilt reached or passed its asymptote (should ease, never touch)");
        }

        [Test]
        public void ItWorksPastNinetyDegrees_HeadBelowTheHips()
        {
            // ⭐ THE FIX. A toe-touch puts the head BELOW the hips, where a real pelvis is MAXIMALLY tilted.
            // The old model bailed on `upDot <= 0` and gave zero here -- weakest exactly where the fold is
            // deepest. Now it keeps going.
            var i = new BasisHipHingeInput
            {
                HipsPos = Vector3.zero,
                HeadPos = new Vector3(0f, -0.2f, 0.9f),   // head below hip height
                HipsRot = Quaternion.identity,
                PlayerUp = Vector3.up,
                StartDeg = 45f,
                MaxAddDeg = 50f,
            };
            BasisHipHingeCore.Solve(i, out BasisHipHingeResult r);
            Assert.IsTrue(r.Applied, "the hinge switched off with the head below the hips (the old bug)");
            Assert.Greater(r.LeanDeg, 90f, "lean should read past 90 deg for a head below the hips");
            Assert.Greater(r.AddDeg, 25f, "a deep toe-touch should tilt the pelvis a lot");
        }

        [Test]
        public void ThePelvisPitchesForward_NotBackward()
        {
            BasisHipHingeCore.Solve(Lean(70f, start: 30f), out BasisHipHingeResult r);
            Vector3 fwd = r.HipsRot * Vector3.forward;
            Assert.Less(fwd.y, -0.05f, $"the pelvis pitched the wrong way: forward axis = {fwd}");
            Vector3 up = r.HipsRot * Vector3.up;
            Assert.Greater(up.z, 0.05f, "the pelvis top did not rotate toward the lean direction");
        }

        [Test]
        public void MaxAddZero_IsANoOp()
        {
            BasisHipHingeCore.Solve(Lean(90f, start: 30f, maxAdd: 0f), out BasisHipHingeResult r);
            Assert.IsFalse(r.Applied);
            Assert.AreEqual(0f, Quaternion.Angle(Quaternion.identity, r.HipsRot), 1e-4f);
        }

        [Test]
        public void Saturate_IsLinearThenAsymptotic()
        {
            Assert.AreEqual(10f, BasisHipHingeCore.Saturate(10f, 50f), 1e-4f, "below soft must be exact (linear)");
            Assert.AreEqual(40f, BasisHipHingeCore.Saturate(40f, 50f), 1e-3f, "at the soft point (0.8*cap)");
            for (float x = 40f; x < 2000f; x += 13f)
            {
                float y = BasisHipHingeCore.Saturate(x, 50f);
                Assert.Less(y, 50f, $"Saturate({x}) = {y} reached its cap");
                Assert.GreaterOrEqual(y, 40f, $"Saturate({x}) = {y} fell below the soft point");
            }
            Assert.AreEqual(0f, BasisHipHingeCore.Saturate(-5f, 50f), 1e-4f, "negative clamps to 0");
        }

        // ---------------------------------------------------------------------------------------------
        // THE CORPUS: does the fitted curve actually reproduce real human pelvis tilt?

        static List<BasisMotionClip> LoadCorpus()
        {
            var clips = new List<BasisMotionClip>();
            foreach (string dir in new[] { Path.GetFullPath("Packages/com.basis.framework/Tests/MocapCorpus~"),
                                           Path.GetFullPath("Packages/com.basis.framework/Tests/MocapCorpus~/posture") })
            {
                if (!Directory.Exists(dir)) continue;
                foreach (string f in Directory.GetFiles(dir, "*.bvh"))
                    if (BasisBvhLoader.TryLoad(f, out BasisMotionClip c, out _)) clips.Add(c);
            }
            if (clips.Count == 0) Assert.Ignore("no corpus");
            return clips;
        }

        // The old model, measured on the same frames so the comparison is apples-to-apples: half the excess
        // past 30, capped at 15, and OFF once the head passes hip height (upDot <= 0 <=> lean >= 90).
        static float OldModel(float leanDeg)
        {
            if (leanDeg >= 90f) return 0f;
            return Mathf.Min(Mathf.Max(leanDeg - 30f, 0f) * 0.5f, 15f);
        }

        [Test]
        public void TheFittedCurve_TracksRealDeepBendsFarBetterThanTheOldModel()
        {
            // WHERE THE CHANGE MATTERS is the DEEP bend (lean > 60): that is where the old model flat-lined at
            // its 15 deg cap and then switched off entirely past 90, while a real pelvis rotates to 40-53 deg.
            // Shallow leans are excluded because there both models give ~the same small tilt, so a median over
            // them is a wash and says nothing. The absolute error keeps a ~6 deg floor (a squat and a
            // waist-bend share a lean but not a pelvis -- one curve cannot separate them), so this proves the
            // RELATIVE win, on the same frames against the same ground truth (baseline offsets hit both equally).
            List<BasisMotionClip> clips = LoadCorpus();
            const float start = 40f, cap = 52f;   // the shipped defaults
            const float deep = 60f;
            var errNew = new List<float>();
            var errOld = new List<float>();
            var realDeep = new List<float>();

            foreach (BasisMotionClip c in clips)
            {
                var leans = new float[c.FrameCount];
                var tilts = new float[c.FrameCount];
                for (int f = 0; f < c.FrameCount; f++)
                {
                    BasisMocapPose hips = c.Get(f, BasisMocapJoint.Hips);
                    BasisMocapPose head = c.Get(f, BasisMocapJoint.Head);
                    Vector3 chord = head.Position - hips.Position;
                    float hm = new Vector3(chord.x, 0f, chord.z).magnitude;
                    leans[f] = hm < 1e-3f ? 0f : Mathf.Atan2(hm, chord.y) * Mathf.Rad2Deg;
                    Vector3 hfwd = hips.Rotation * Vector3.forward;
                    tilts[f] = Mathf.Asin(Mathf.Clamp(-hfwd.y, -1f, 1f)) * Mathf.Rad2Deg;
                }
                var upright = new List<float>();
                for (int f = 0; f < c.FrameCount; f++) if (leans[f] < 15f) upright.Add(tilts[f]);
                float baseline = Median(upright);

                for (int f = 0; f < c.FrameCount; f++)
                {
                    if (leans[f] <= deep) continue;
                    float realTilt = tilts[f] - baseline;
                    var input = new BasisHipHingeInput
                    {
                        HipsPos = c.Get(f, BasisMocapJoint.Hips).Position,
                        HeadPos = c.Get(f, BasisMocapJoint.Head).Position,
                        HipsRot = Quaternion.identity, PlayerUp = Vector3.up, StartDeg = start, MaxAddDeg = cap,
                    };
                    BasisHipHingeCore.Solve(input, out BasisHipHingeResult r);
                    errNew.Add(Mathf.Abs(r.AddDeg - realTilt));
                    errOld.Add(Mathf.Abs(OldModel(leans[f]) - realTilt));
                    realDeep.Add(realTilt);
                }
            }

            Assert.Greater(errNew.Count, 300, "too few deep-bend frames to trust");
            float mNew = Median(errNew), mOld = Median(errOld), realMed = Median(realDeep);
            Debug.Log($"[hip hinge] deep bends (lean>{deep}): real pelvis tilt median {realMed:F1} deg; " +
                      $"model error NEW {mNew:F2} vs OLD {mOld:F2} deg ({100f * (1f - mNew / mOld):F0}% better), {errNew.Count} frames");
            // sanity: the corpus really does tilt the pelvis a lot at deep bends (else the loader lost it)
            Assert.Greater(realMed, 20f, $"deep-bend real pelvis tilt only {realMed:F0} deg -- expected 30+; loader/axis issue");
            Assert.Less(mNew, mOld * 0.7f,
                $"the fitted curve ({mNew:F1} deg) is not clearly better than the old model ({mOld:F1} deg) at deep bends");
        }

        static float Median(List<float> xs)
        {
            if (xs.Count == 0) return 0f;
            xs.Sort();
            return xs[xs.Count / 2];
        }
    }
}
