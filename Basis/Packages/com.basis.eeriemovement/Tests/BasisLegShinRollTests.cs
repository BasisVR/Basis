using NUnit.Framework;
using UnityEngine;
using Basis.IK;

namespace Basis.Tests.IK
{
    /// <summary>
    /// Gate for the tracker shin roll in <see cref="BasisLegSolveCore"/>.
    ///
    /// THE DEFECT. BEND/AIM/SWIVEL spend all three DOF on knee and ankle POSITION, so the shin's rotation about
    /// its own long axis is a by-product of the swivel that places the knee -- the leg has no tibial-rotation DOF.
    /// Cross a foot over the other leg (hip external rotation + ~30-40 deg of real tibial rotation on a flexed
    /// knee) and that rotation has nowhere to go but the ankle, whose axial ROM is ~5-10 deg. The result is the
    /// snapped-looking ankle the user reported. A lower-leg tracker is strapped to the shin and MEASURES the
    /// rotation; the solver was using only its position.
    ///
    /// WHY THE INPUT IS A BONE ROTATION, NOT THE RAW TRACKER. A calf strap's clock angle around the limb is
    /// arbitrary, and the lower-leg role gets no Recalibrated* rotation offset (those cover only head, hips,
    /// chest, feet, toes, shoulders). Comparing a raw tracker rotation against the shin would bake the strap
    /// angle in as a constant bogus roll. The rig driver maps it through BasisLimbRollStore's calibration
    /// reference first, so <see cref="BasisLegSolveInput.HintRotation"/> is the tracker-implied SHIN BONE
    /// rotation. StrapClockAngle_Cancels below is the proof that this is what makes the feature safe.
    ///
    /// A PURE ROLL MOVES NO JOINT: the knee pivots on the shin axis and the ankle lies on it, so the knee stays
    /// on the tracker pole and the foot stays on target. The position gates structurally cannot see this, which
    /// is exactly why it is allowed (same argument as BasisArmSolveCore's MidPostRoll).
    /// </summary>
    public class BasisLegShinRollTests
    {
        const float Thigh = 0.42f, Shin = 0.42f;
        const float TibialDeg = 35f;
        const float TrueAnkleDeg = 10f;

        static Vector3 Hip(float side) => new Vector3(side * 0.09f, 0.90f, 0f);
        static Vector3 KneeRest(float side) => new Vector3(side * 0.09f, 0.90f - Thigh, 0f);
        static Vector3 FootRest(float side) => new Vector3(side * 0.09f, 0.90f - Thigh - Shin, 0f);
        static Vector3 FootCrossed(float side) => new Vector3(side * -0.12f, 0.45f, 0.05f);
        static Vector3 KneeHint(float side) => new Vector3(side * 0.20f, 0.52f, 0.20f);

        static BasisLegSolveInput Leg(float side)
        {
            BasisLegSolveInput i = default;
            i.Root = Hip(side);
            i.Mid = KneeRest(side);
            i.Tip = FootRest(side);
            i.RootRotation = Quaternion.identity;
            i.MidRotation = Quaternion.identity;
            i.TargetPosition = FootCrossed(side);
            i.TargetRotation = Quaternion.identity;
            i.TargetOffset = Quaternion.identity;
            i.HintPosition = KneeHint(side);
            i.HintWeight = 1f;
            i.HintDistrust = 0f;
            i.BendNormal = new Vector3(side, 0f, 0f);
            return i;
        }

        /// <summary>Twist of q about axis (swing-twist), degrees, principal branch.</summary>
        static float Twist(Quaternion q, Vector3 axis)
        {
            float s = q.x * axis.x + q.y * axis.y + q.z * axis.z;
            float c = q.w;
            if (c < 0f) { s = -s; c = -c; }
            return 2f * Mathf.Atan2(s, c) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// Solve once with the feature off to learn the solver's shin frame, synthesise the two tracker readings
        /// from it (a real crossed leg carries tibial rotation the foot then inherits), and solve again with the
        /// shin roll enabled.
        /// </summary>
        static void Scenario(float side, float tibialDeg, float trueAnkleDeg, bool hintIsTracker,
                             out BasisLegSolveResult off, out BasisLegSolveResult on,
                             out float ankleBefore, out float ankleAfter)
        {
            BasisLegSolveCore.Solve(Leg(side), out off);

            Vector3 axis = (off.FootSolved - off.KneeSolved).normalized;
            Quaternion neutral = off.MidRotationSolved;   // animated mid and tip are identity here
            Quaternion tibial = Quaternion.AngleAxis(tibialDeg, axis);

            Quaternion shinTracker = tibial * off.MidRotationSolved;
            Quaternion footTracker = Quaternion.AngleAxis(trueAnkleDeg, axis) * tibial * neutral;

            ankleBefore = Twist(footTracker * Quaternion.Inverse(neutral), axis);

            BasisLegSolveInput i = Leg(side);
            i.TargetRotation = footTracker;
            i.HintRotation = shinTracker;
            i.HintIsTracker = hintIsTracker;
            BasisLegSolveCore.Solve(i, out on);

            Vector3 axisOn = (on.FootSolved - on.KneeSolved).normalized;
            ankleAfter = Twist(on.TipRotation * Quaternion.Inverse(on.MidRotationSolved), axisOn);
        }

        [Test]
        public void CrossedFoot_LeavesTheAnkleHoldingOnlyItsTrueAngle([Values(-1f, 1f)] float side)
        {
            Scenario(side, TibialDeg, TrueAnkleDeg, true, out _, out BasisLegSolveResult on,
                     out float before, out float after);

            Assert.That(Mathf.Abs(before), Is.GreaterThan(30f),
                $"the defect must reproduce: the shipping solver demands {before:F1} deg of ankle axial twist");
            Assert.That(after, Is.EqualTo(TrueAnkleDeg).Within(0.5f), $"after the shin roll the ankle should hold only its true angle, got {after:F1} deg");
            Assert.That(on.ShinRollDeg, Is.EqualTo(TibialDeg).Within(0.5f), $"the shin should absorb the tibial rotation, got {on.ShinRollDeg:F1} deg");
        }

        [Test]
        public void TheRollMovesNoJoint([Values(-1f, 1f)] float side)
        {
            Scenario(side, TibialDeg, TrueAnkleDeg, true, out BasisLegSolveResult off, out BasisLegSolveResult on, out _, out _);

            Assert.That(Vector3.Distance(off.KneeSolved, on.KneeSolved), Is.EqualTo(0f).Within(1e-6f), "knee left the tracker pole");
            Assert.That(Vector3.Distance(off.FootSolved, on.FootSolved), Is.EqualTo(0f).Within(1e-6f), "foot left its target");
            Assert.That(on.ReachRatio, Is.EqualTo(off.ReachRatio).Within(1e-6f), "reach changed");
            Assert.That(on.FootError, Is.LessThan(1e-4f), "foot is off target");
        }

        /// <summary>
        /// A tracker is rigidly strapped to the shin: trackerRot(t) = shinRot(t) * K, with K fixed and ARBITRARY.
        /// Calibration stores Qref = Inverse(trackerRot_calib) * boneRot_calib = Inverse(K), so
        /// trackerRot_live * Qref recovers the true shin rotation for ANY K. Without that mapping the strap's
        /// clock angle would appear as a constant shin roll -- one visible artifact traded for another.
        /// </summary>
        [Test]
        public void StrapClockAngle_Cancels([Values(0f, 47f, 137f, -98f)] float clockDeg,
                                            [Values(0f, 30f)] float tibialDeg)
        {
            const float side = -1f;

            BasisLegSolveInput calibIn = Leg(side);
            calibIn.TargetPosition = FootRest(side);
            calibIn.HintPosition = KneeRest(side) + new Vector3(0f, 0f, 0.10f);
            BasisLegSolveCore.Solve(calibIn, out BasisLegSolveResult calib);

            Vector3 calibAxis = (calib.FootSolved - calib.KneeSolved).normalized;
            Quaternion K = Quaternion.AngleAxis(clockDeg, calibAxis) * Quaternion.AngleAxis(23f, Vector3.forward);
            Quaternion qRef = Quaternion.Inverse(calib.MidRotationSolved * K) * calib.MidRotationSolved;

            BasisLegSolveCore.Solve(Leg(side), out BasisLegSolveResult liveOff);
            Vector3 axis = (liveOff.FootSolved - liveOff.KneeSolved).normalized;

            Quaternion trueShin = Quaternion.AngleAxis(tibialDeg, axis) * liveOff.MidRotationSolved;

            BasisLegSolveInput i = Leg(side);
            i.HintRotation = trueShin * K * qRef;
            i.HintIsTracker = true;
            BasisLegSolveCore.Solve(i, out BasisLegSolveResult on);

            Assert.That(on.ShinRollDeg, Is.EqualTo(tibialDeg).Within(0.5f),
                $"a {clockDeg:F0} deg strap clock angle must cancel; got {on.ShinRollDeg:F2} deg for a {tibialDeg:F0} deg tibial rotation");
        }

        [Test]
        public void FeatureOff_IsAnExactNoOp()
        {
            foreach (float side in new[] { -1f, 1f })
            {
                BasisLegSolveCore.Solve(Leg(side), out BasisLegSolveResult r);
                Assert.That(r.MidPostRoll.x, Is.EqualTo(Quaternion.identity.x));
                Assert.That(r.MidPostRoll.w, Is.EqualTo(Quaternion.identity.w), "MidPostRoll must be identity, never the zero quaternion");
                Assert.That(r.ShinRollDeg, Is.EqualTo(0f));
            }
        }

        [Test]
        public void ModelPole_IsNotRolled()
        {
            Scenario(-1f, TibialDeg, TrueAnkleDeg, hintIsTracker: false,
                     out _, out BasisLegSolveResult on, out _, out _);

            Assert.That(on.ShinRollDeg, Is.EqualTo(0f), "an invented pole is not a measurement and must not roll the shin");
            Assert.That(on.MidPostRoll.w, Is.EqualTo(1f));
        }

        [Test]
        public void WreckedCalibration_IsCapped()
        {
            Scenario(-1f, 170f, 0f, true, out _, out BasisLegSolveResult on, out _, out _);

            Assert.That(Mathf.Abs(on.ShinRollDeg), Is.LessThanOrEqualTo(BasisLegSolveCore.TrackerShinRollMaxDeg + 1e-3f),
                "a slipped strap or stale calibration must produce a bounded wrong shin, not an unbounded one");
        }

        [Test]
        public void NaNAndZeroHintRotation_TakeTheSafePath()
        {
            BasisLegSolveInput nan = Leg(-1f);
            nan.HintIsTracker = true;
            nan.HintRotation = new Quaternion(float.NaN, float.NaN, float.NaN, float.NaN);
            BasisLegSolveCore.Solve(nan, out BasisLegSolveResult rNaN);

            Assert.That(rNaN.MidPostRoll.w, Is.EqualTo(1f), "a NaN hint rotation must not reach the shin");
            Assert.That(float.IsNaN(rNaN.KneeSolved.x) || float.IsNaN(rNaN.FootSolved.x), Is.False, "NaN reached the bones");

            BasisLegSolveInput zero = Leg(-1f);
            zero.HintIsTracker = true;
            zero.HintRotation = new Quaternion(0f, 0f, 0f, 0f);
            BasisLegSolveCore.Solve(zero, out BasisLegSolveResult rZero);

            Assert.That(rZero.MidPostRoll.w, Is.EqualTo(1f), "the zero quaternion is the feature-off sentinel");
        }

        [Test]
        public void AcrossTheTibialSweep_TheAnkleKeepsItsTrueAngleAndTheFootStaysPut(
            [Values(-40f, -30f, -20f, -10f, 0f, 10f, 20f, 30f, 40f)] float tibialDeg)
        {
            const float trueAnkle = 8f;
            Scenario(-1f, tibialDeg, trueAnkle, true, out _, out BasisLegSolveResult on, out _, out float after);

            Assert.That(after, Is.EqualTo(trueAnkle).Within(0.5f), $"ankle residual drifted at {tibialDeg:F0} deg of tibial rotation");
            Assert.That(on.FootError, Is.LessThan(1e-4f), "foot left its target");
        }
    }
}
