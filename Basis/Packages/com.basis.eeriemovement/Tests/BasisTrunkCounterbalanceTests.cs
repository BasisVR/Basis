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
    /// Postural counterbalance -- the pelvis travels back as the trunk folds forward so the whole-body COM
    /// stays over the feet. The model is one multiply (shift = Gain * the trunk's horizontal projection);
    /// these tests pin that it is the RIGHT multiply, that it cannot be moved by a gaze, and that the corpus
    /// agrees with the gain the segment masses predict.
    ///
    /// ⚠️ GATING lives in the JOB, not this core: BasisFullBodyIK.SolveSpine applies it ONLY when there is no
    /// hip tracker -- a tracked pelvis is measured and must feed straight through.
    /// </summary>
    public sealed class BasisTrunkCounterbalanceTests
    {
        const float k_TrunkLen = 0.5f;
        const float k_MaxShift = 0.25f;

        // hips at the origin, neck folded `deg` off vertical (+Y) toward +Z.
        static BasisTrunkCounterbalanceInput Fold(float deg, float gain = BasisTrunkCounterbalanceCore.DerivedGain,
                                                  float maxShift = k_MaxShift, float trunkLen = k_TrunkLen)
        {
            float r = deg * Mathf.Deg2Rad;
            return new BasisTrunkCounterbalanceInput
            {
                HipsPos = Vector3.zero,
                NeckCue = new Vector3(0f, Mathf.Cos(r), Mathf.Sin(r)) * trunkLen,
                PlayerUp = Vector3.up,
                Gain = gain,
                MaxShift = maxShift,
            };
        }

        [Test]
        public void Upright_NothingMoves()
        {
            BasisTrunkCounterbalanceCore.Solve(Fold(0f), out BasisTrunkCounterbalanceResult r);
            Assert.IsFalse(r.Applied, "the counterbalance fired on an upright trunk");
            Assert.AreEqual(0f, r.ShiftMeters, 1e-5f);
            Assert.AreEqual(0f, r.FlexionFrac, 1e-4f);
            Assert.AreEqual(0f, Vector3.Distance(Vector3.zero, r.HipsPos), 1e-6f);
        }

        [Test]
        public void GainZero_IsExactlyTheIdentity()
        {
            // The off switch must be bit-identical, not merely small -- same contract as every other IK toggle.
            var input = Fold(70f, gain: 0f);
            BasisTrunkCounterbalanceCore.Solve(input, out BasisTrunkCounterbalanceResult r);
            Assert.IsFalse(r.Applied);
            Assert.AreEqual(0f, Vector3.Distance(input.HipsPos, r.HipsPos), 0f, "gain 0 moved the hips");
        }

        [Test]
        public void TheShiftIsGainTimesTheTrunksHorizontalTravel()
        {
            // THE model claim: the pelvis answers a fixed fraction of how far the neck went forward.
            foreach (float deg in new[] { 20f, 35f, 45f })
            {
                BasisTrunkCounterbalanceCore.Solve(Fold(deg), out BasisTrunkCounterbalanceResult r);
                float horizMag = k_TrunkLen * Mathf.Sin(deg * Mathf.Deg2Rad);
                float expected = BasisTrunkCounterbalanceCore.DerivedGain * horizMag;
                Assert.Less(expected, k_MaxShift * 0.8f, $"{deg} deg is past the soft point; pick a shallower angle");
                Assert.AreEqual(expected, r.ShiftMeters, 1e-4f, $"wrong shift at {deg} deg");
            }
        }

        [Test]
        public void TheShiftOpposesTheFoldAndIsPurelyHorizontal()
        {
            BasisTrunkCounterbalanceCore.Solve(Fold(60f), out BasisTrunkCounterbalanceResult r);
            Assert.IsTrue(r.Applied);
            // folded toward +Z, so the pelvis must travel -Z, and must not gain or lose height doing it.
            Assert.Less(r.HipsPos.z, 0f, "the pelvis moved the same way as the fold");
            Assert.AreEqual(0f, r.HipsPos.y, 1e-5f, "the counterbalance changed the pelvis height");
            Assert.AreEqual(0f, r.HipsPos.x, 1e-5f, "the counterbalance introduced lateral drift");
        }

        [Test]
        public void ItAnswersOffSagittalFoldsInTheirOwnDirection()
        {
            // A forward-LEFT fold must be answered back-RIGHT. A model built on the pelvis' forward axis
            // would only handle pure sagittal bends (and would drag in the bind-frame problem).
            var input = Fold(50f);
            Quaternion yaw = Quaternion.AngleAxis(37f, Vector3.up);
            input.NeckCue = yaw * input.NeckCue;
            BasisTrunkCounterbalanceCore.Solve(input, out BasisTrunkCounterbalanceResult r);

            Vector3 leanDir = yaw * Vector3.forward;
            Vector3 shift = r.HipsPos - input.HipsPos;
            Assert.AreEqual(-1f, Vector3.Dot(shift.normalized, leanDir), 1e-4f,
                "the shift is not anti-parallel to the fold direction");
        }

        [Test]
        public void FlexionFracIsSineOfFlexion_AndZeroWhenTheTermIsOff()
        {
            foreach (float deg in new[] { 15f, 45f, 90f, 120f })
            {
                BasisTrunkCounterbalanceCore.Solve(Fold(deg), out BasisTrunkCounterbalanceResult on);
                Assert.AreEqual(Mathf.Sin(deg * Mathf.Deg2Rad), on.FlexionFrac, 1e-4f, $"FlexionFrac wrong at {deg} deg");

                // Off must switch off the CROUCH FADE too, not just the shift -- otherwise "gain 0" would
                // still silently change the crouch sit-back and the no-op contract would be a lie.
                BasisTrunkCounterbalanceCore.Solve(Fold(deg, gain: 0f), out BasisTrunkCounterbalanceResult off);
                Assert.AreEqual(0f, off.FlexionFrac, 0f, $"FlexionFrac leaked at {deg} deg with the term disabled");
            }
        }

        [Test]
        public void ItSaturatesBelowTheCapAndNeverReachesIt()
        {
            for (float deg = 0f; deg <= 110f; deg += 5f)
            {
                BasisTrunkCounterbalanceCore.Solve(Fold(deg, gain: 2f), out BasisTrunkCounterbalanceResult r);
                Assert.Less(r.ShiftMeters, k_MaxShift, $"shift reached the cap at {deg} deg (a hard clamp pops)");
            }
        }

        [Test]
        public void SaturationAddsNoStepOfItsOwn()
        {
            // The derivative step at a hard clamp IS the pop. The unsaturated function's own steepest 1-deg
            // step is gain*trunkLen*Deg2Rad, and saturation only ever REDUCES the slope -- so bounding the
            // observed step by that is the real assertion. A flat threshold would just be measuring sin's
            // own slope at whatever gain the test happened to pick.
            const float gain = 2f;
            float bound = gain * k_TrunkLen * Mathf.Deg2Rad * 1.05f;
            float prev = 0f;
            for (float deg = 0f; deg <= 130f; deg += 1f)
            {
                BasisTrunkCounterbalanceCore.Solve(Fold(deg, gain: gain), out BasisTrunkCounterbalanceResult r);
                Assert.LessOrEqual(Mathf.Abs(r.ShiftMeters - prev), bound,
                    $"shift stepped {Mathf.Abs(r.ShiftMeters - prev):F5} m at {deg} deg, past the unsaturated bound {bound:F5}");
                prev = r.ShiftMeters;
            }
        }

        [Test]
        public void PastHorizontal_TheShiftEasesBackOff()
        {
            // A real toe-touch hangs the trunk nearly straight down, so its forward lever arm shrinks again.
            // sin() gives that for free; assert it rather than leaving it as an accident of the formula.
            BasisTrunkCounterbalanceCore.Solve(Fold(90f, gain: 0.2f), out BasisTrunkCounterbalanceResult at90);
            BasisTrunkCounterbalanceCore.Solve(Fold(125f, gain: 0.2f), out BasisTrunkCounterbalanceResult at125);
            Assert.Less(at125.ShiftMeters, at90.ShiftMeters, "the shift did not ease off past horizontal");
            Assert.Greater(at125.ShiftMeters, 0f, "the shift collapsed entirely past horizontal");
        }

        [Test]
        public void ItIsEquivariant_TheAnswerRidesTheFrame()
        {
            // Positions only, no bone axes, so this should hold exactly. BasisFullBodyIK's clamps have shipped
            // frame bugs precisely where this was never asserted -- see the space-conformance suite.
            var input = Fold(65f);
            input.HipsPos = new Vector3(0.3f, 1.1f, -0.7f);
            input.NeckCue += input.HipsPos;
            BasisTrunkCounterbalanceCore.Solve(input, out BasisTrunkCounterbalanceResult flat);

            Quaternion R = Quaternion.Euler(23f, -51f, 17f);
            Vector3 pivot = new Vector3(-2f, 0.4f, 3f);
            var moved = input;
            moved.HipsPos = R * (input.HipsPos - pivot) + pivot;
            moved.NeckCue = R * (input.NeckCue - pivot) + pivot;
            moved.PlayerUp = R * input.PlayerUp;
            BasisTrunkCounterbalanceCore.Solve(moved, out BasisTrunkCounterbalanceResult rotated);

            Vector3 expected = R * (flat.HipsPos - pivot) + pivot;
            Assert.AreEqual(0f, Vector3.Distance(expected, rotated.HipsPos), 1e-4f, "not equivariant");
            Assert.AreEqual(flat.ShiftMeters, rotated.ShiftMeters, 1e-5f);
        }

        [Test]
        public void AGazeCannotMoveThePelvis()
        {
            // The whole reason the input is a NECK cue and not the head target. Replicates
            // BasisFullBodyIK.ComputeNeckCue exactly: orbit the head about a fixed neck by any rotation and
            // the reconstructed cue -- and therefore the pelvis -- must not move at all.
            Vector3 neck = new Vector3(0f, 1.4f, 0.15f);
            Vector3 headRest = new Vector3(0f, 1.65f, 0.05f);
            Quaternion headRestRot = Quaternion.identity;
            // tposeHeadToNeckLocal == inv(headRot) * (neck - head), captured at rest.
            Vector3 headToNeckLocal = Quaternion.Inverse(headRestRot) * (neck - headRest);

            Vector3 baseline = Vector3.zero;
            foreach (float gaze in new[] { 0f, -20f, -45f, -70f, -90f, 30f })
            {
                Quaternion q = Quaternion.AngleAxis(gaze, Vector3.right);
                Quaternion headRot = q * headRestRot;
                Vector3 headPos = neck + q * (headRest - neck);       // the head ORBITS the neck
                Vector3 cue = headPos + headRot * headToNeckLocal;    // == ComputeNeckCue

                var input = new BasisTrunkCounterbalanceInput
                {
                    HipsPos = new Vector3(0f, 0.95f, 0f),
                    NeckCue = cue,
                    PlayerUp = Vector3.up,
                    Gain = BasisTrunkCounterbalanceCore.DerivedGain,
                    MaxShift = k_MaxShift,
                };
                BasisTrunkCounterbalanceCore.Solve(input, out BasisTrunkCounterbalanceResult r);
                if (gaze == 0f) baseline = r.HipsPos;
                Assert.AreEqual(0f, Vector3.Distance(baseline, r.HipsPos), 1e-5f,
                    $"gazing {gaze} deg moved the pelvis -- the cue is contaminated");
            }
        }

        // ---------------------------------------------------------------------------------------------
        // THE CORPUS: do real humans actually counterweight a fold by ~0.38 of the trunk's forward travel?

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

        [Test]
        public void TheCorpusAgreesWithTheGainTheSegmentMassesPredict()
        {
            // Regress the pelvis' setback (behind the mid-foot, along the fold direction) against the trunk's
            // horizontal travel: the model says setback = Gain * travel, so the SLOPE is the gain.
            //
            // ⚠️ PER CLIP, then the median across clips. Pooling every frame into one fit is a Simpson's trap
            // -- each subject has their own body size and their own standing hips-over-midfoot offset, so the
            // BETWEEN-clip variance in the intercept swamps the WITHIN-clip slope being asked about. Measured:
            // pooled came out NEGATIVE (-0.12) while every single per-flexion bucket was positive. Per-clip
            // gives median 0.63.
            //
            // Frames are filtered to someone actually STANDING ON THEIR FEET -- "trunk pitched > 25 deg" in
            // CMU also catches lying down, sitting and rolling, where the mid-foot is not a base of support
            // and the measure is meaningless.
            //
            // Mirror-safe by construction: every quantity is a magnitude or a dot product between two vectors
            // from the SAME frame, so the loader's X-mirror to Unity's left hand cannot flip a sign here (the
            // handedness trap that has bitten the corpus checks before).
            //
            // ⚠️ THIS IS A VETO, NOT A FIT. The measured IQR straddles zero ([-0.04, 0.84] over 25 clips):
            // CMU contains very few deliberate standing forward-bends, so it can reject an absurd constant but
            // cannot set one. The shipped gain stays the DERIVED 0.38 (segment masses), which is the
            // conservative end of this band -- fitting to the corpus here would encode "did not" as "cannot",
            // the same mistake the spine ROM notes warn about.
            List<BasisMotionClip> clips = LoadCorpus();
            const float minFlexDeg = 25f;
            const int minFramesPerClip = 60;
            var slopes = new List<float>();
            int nTotal = 0;

            foreach (BasisMotionClip c in clips)
            {
                if (!c.Has(BasisMocapJoint.Neck) || !c.Has(BasisMocapJoint.LeftFoot) || !c.Has(BasisMocapJoint.RightFoot))
                    continue;

                // this subject's standing scale, from the tallest hips-over-feet in the clip
                float standH = 0f;
                for (int f = 0; f < c.FrameCount; f++)
                {
                    Vector3 h = c.Get(f, BasisMocapJoint.Hips).Position;
                    Vector3 mf = (c.Get(f, BasisMocapJoint.LeftFoot).Position +
                                  c.Get(f, BasisMocapJoint.RightFoot).Position) * 0.5f;
                    standH = Mathf.Max(standH, h.y - mf.y);
                }
                if (standH < 0.3f) continue;

                double sx = 0, sy = 0, sxx = 0, sxy = 0;
                int n = 0;
                for (int f = 0; f < c.FrameCount; f++)
                {
                    Vector3 hips = c.Get(f, BasisMocapJoint.Hips).Position;
                    Vector3 neck = c.Get(f, BasisMocapJoint.Neck).Position;
                    Vector3 lf = c.Get(f, BasisMocapJoint.LeftFoot).Position;
                    Vector3 rf = c.Get(f, BasisMocapJoint.RightFoot).Position;
                    Vector3 midFoot = (lf + rf) * 0.5f;

                    // standing: pelvis still well above the feet, both feet at roughly one level
                    if (hips.y - midFoot.y < 0.75f * standH) continue;
                    if (Mathf.Abs(lf.y - rf.y) > 0.25f * standH) continue;

                    Vector3 trunk = neck - hips;
                    if (trunk.magnitude < 1e-3f) continue;
                    Vector3 horizontal = new Vector3(trunk.x, 0f, trunk.z);
                    float travel = horizontal.magnitude;
                    float flexDeg = Mathf.Atan2(travel, trunk.y) * Mathf.Rad2Deg;
                    if (flexDeg < minFlexDeg || travel < 1e-3f) continue;

                    float setback = Vector3.Dot(hips - midFoot, -(horizontal / travel));
                    sx += travel; sy += setback; sxx += (double)travel * travel; sxy += (double)travel * setback;
                    n++;
                }

                if (n < minFramesPerClip) continue;
                double denom = n * sxx - sx * sx;
                if (System.Math.Abs(denom) < 1e-9) continue;
                slopes.Add((float)((n * sxy - sx * sy) / denom));
                nTotal += n;
            }

            Assert.GreaterOrEqual(slopes.Count, 8, "too few usable clips to fit a gain");
            slopes.Sort();
            float slope = slopes[slopes.Count / 2];
            float derived = BasisTrunkCounterbalanceCore.DerivedGain;

            Debug.Log($"[trunk counterbalance] corpus pelvis setback vs trunk travel: per-clip slope median " +
                      $"{slope:F3} (IQR [{slopes[slopes.Count / 4]:F3}, {slopes[(3 * slopes.Count) / 4]:F3}]), " +
                      $"derived {derived:F2}, {nTotal} standing folded frames from {slopes.Count} clips");

            Assert.Greater(slope, 0.10f, $"the corpus shows no pelvis setback at all (slope {slope:F3}) -- axis/units issue?");
            Assert.Less(slope, 1.2f, $"implausible corpus slope {slope:F3}");
            Assert.AreEqual(slope, derived, 0.35f,
                $"the derived gain {derived:F2} is outside the band the corpus supports (slope {slope:F3})");
        }
    }
}
