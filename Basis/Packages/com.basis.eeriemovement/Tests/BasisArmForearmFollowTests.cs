using NUnit.Framework;
using UnityEngine;
using Basis.IK;
namespace Basis.Tests.IK
{
    public class BasisArmForearmFollowTests
    {
        const float UpperLen = 0.30f, ForeLen = 0.30f;
        static readonly Vector3 BoneAxis = Vector3.right, Shoulder = Vector3.zero;
        const float KeepFrac = BasisArmSolveCore.WristKeepFrac;          // 0.15
        const float KeepMax = BasisArmSolveCore.WristKeepMaxDeg;         // 15
        const float RollCap = BasisArmSolveCore.TrackerForearmRollMaxDeg; // 120
        static BasisArmSolveInput Pose(Vector3 straight, Vector3 bulge, float halfFlexDeg)
        {
            float f = halfFlexDeg * Mathf.Deg2Rad;
            Vector3 upperDir = (straight * Mathf.Cos(f) + bulge * Mathf.Sin(f)).normalized;
            Vector3 lowerDir = (straight * Mathf.Cos(f) - bulge * Mathf.Sin(f)).normalized;
            BasisArmSolveInput i = default;
            i.Shoulder = Shoulder;
            i.Elbow = Shoulder + upperDir * UpperLen;
            i.Hand = i.Elbow + lowerDir * ForeLen;
            i.RootRotation = Quaternion.FromToRotation(BoneAxis, upperDir);
            i.MidRotation = Quaternion.FromToRotation(BoneAxis, lowerDir);
            i.TipRotation = i.MidRotation;
            i.TargetPosition = i.Hand;
            i.TargetRotation = i.MidRotation;
            i.TargetOffset = Quaternion.identity;
            i.PlayerUp = Vector3.up;
            i.HintMaxStepDeg = float.MaxValue;
            i.ForearmFollowWeight = 1f;
            return i;
        }
        static void Roll(ref BasisArmSolveInput i, float rollDeg)
        {
            Vector3 foreDir = (i.Hand - i.Elbow).normalized;
            i.TargetRotation = Quaternion.AngleAxis(rollDeg, foreDir) * i.MidRotation;
        }
        static float ResidualRollDeg(in BasisArmSolveInput i, in BasisArmSolveResult r)
        {
            Quaternion neutral = r.MidRotationSolved * Quaternion.Inverse(i.MidRotation) * i.TipRotation;
            Quaternion rel = (i.TargetRotation * i.TargetOffset) * Quaternion.Inverse(neutral);
            if (rel.w < 0f) rel = new Quaternion(-rel.x, -rel.y, -rel.z, -rel.w);
            Vector3 foreDir = (r.HandSolved - r.ElbowSolved).normalized, v = new Vector3(rel.x, rel.y, rel.z);
            return 2f * Mathf.Atan2(Vector3.Dot(v, foreDir), rel.w) * Mathf.Rad2Deg;
        }
        [Test]
        public void FollowOff_IsTheLegacyContract_AndTheBreachItLeaves()
        {
            BasisArmSolveInput i = Pose(Vector3.forward, Vector3.down, 25f);
            Roll(ref i, 120f);
            i.ForearmFollowWeight = 0f;

            BasisArmSolveCore.Solve(i, out BasisArmSolveResult r);

            Assert.That(r.ForearmRollDeg, Is.EqualTo(0f), "weight 0 must decline: no forearm roll on the no-tracker path");
            Assert.That(r.MidPostRoll.w, Is.EqualTo(1f), "MidPostRoll must stay identity at weight 0");
            Assert.That(Mathf.Abs(ResidualRollDeg(i, r)), Is.GreaterThan(25f), "anti-vacuity: without the follow the wrist really is left carrying an inhuman axial roll — " +"if this stops failing-by-design, the sweep below is no longer measuring anything");
            Assert.That(Mathf.Abs(r.WristResidualDeg - ResidualRollDeg(i, r)), Is.LessThan(0.5f),"the published residual must agree with the recomputed one");
        }
        [Test]
        public void Follow_TheWristKeepsOnlyItsCarpalShare()
        {
            for (float demand = 10f; demand <= 130f; demand += 10f)
            {
                foreach (float sign in new[] { 1f, -1f })
                {
                    BasisArmSolveInput i = Pose(Vector3.forward, Vector3.down, 25f);
                    Roll(ref i, sign * demand);

                    BasisArmSolveCore.Solve(i, out BasisArmSolveResult r);

                    float resid = Mathf.Abs(ResidualRollDeg(i, r));
                    Assert.That(resid, Is.LessThan(KeepMax + 0.5f), $"demand {sign * demand:F0}: the wrist kept {resid:F1} deg — past the carpal ceiling");
                    Assert.That(r.HandError, Is.LessThan(1e-4f), "a pure roll cannot move the hand");
                }
            }
        }
        [Test]
        public void Follow_BelowTheReliefRamp_TheShareIsProportional()
        {
            foreach (float demand in new[] { 10f, 20f, 30f, 40f, 50f })
            {
                BasisArmSolveInput i = Pose(Vector3.forward, Vector3.down, 25f);
                Roll(ref i, demand);

                BasisArmSolveCore.Solve(i, out BasisArmSolveResult r);

                Assert.That(Mathf.Abs(ResidualRollDeg(i, r)), Is.EqualTo(KeepFrac * demand).Within(0.75f), $"below the relief ramp the wrist's share is proportional co-activation ({KeepFrac:P0}), " +"not a threshold handoff — motor-control studies show joints co-rotate from the first degree");
                Assert.That(r.WristReliefDeg, Is.EqualTo(0f), "the swivel relief must still not stir in-band");
            }
        }
        [Test]
        public void Follow_IsAPureRoll_ElbowAndHandBitIdenticalToFollowOff()
        {
            foreach (float demand in new[] { 25f, 70f, 120f })
            {
                BasisArmSolveInput on = Pose(Vector3.forward, Vector3.down, 25f);
                Roll(ref on, demand);
                BasisArmSolveInput off = on;
                off.ForearmFollowWeight = 0f;

                BasisArmSolveCore.Solve(on, out BasisArmSolveResult rOn);
                BasisArmSolveCore.Solve(off, out BasisArmSolveResult rOff);

                Assert.That(Vector3.Distance(rOn.ElbowSolved, rOff.ElbowSolved), Is.LessThan(1e-6f), $"demand {demand:F0}: the follow moved the ELBOW — it is no longer a pure roll");
                Assert.That(Vector3.Distance(rOn.HandSolved, rOff.HandSolved), Is.LessThan(1e-6f), $"demand {demand:F0}: the follow moved the HAND");
                Assert.That(Quaternion.Angle(rOn.TipRotation, rOff.TipRotation), Is.LessThan(1e-4f), $"demand {demand:F0}: the follow changed the hand's rotation target — the 2026-07-23 " +"standing constraint (never move the hand off its rotation target) is breached");
                Assert.That(Quaternion.Angle(rOn.RootRotationSolved, rOff.RootRotationSolved), Is.LessThan(1e-4f), $"demand {demand:F0}: the follow leaked into the humerus");
            }
        }
        [Test]
        public void Follow_TopsUpTheTrackerBlend_TheWristStopsCarryingTheLeftover()
        {
            BasisArmSolveInput i = Pose(Vector3.forward, Vector3.down, 25f);
            i.HintWeight = true;
            i.HintIsTracker = true;
            i.HintPosition = i.Elbow;
            Vector3 foreDir = (i.Hand - i.Elbow).normalized;
            i.HintRotation = Quaternion.AngleAxis(60f, foreDir) * i.MidRotation;
            i.HasHintRotation = true;
            Roll(ref i, 80f);

            BasisArmSolveCore.Solve(i, out BasisArmSolveResult r);

            // blend = 60 + 0.5*(80-60) = 70; leftover 10; wrist keeps 0.15*10 = 1.5; forearm 78.5.
            float blended = 60f + BasisArmSolveCore.TrackerRollHandBlend * (80f - 60f), leftover = 80f - blended;
            float expected = blended + (leftover - KeepFrac * leftover);
            Assert.That(r.ForearmRollDeg, Is.EqualTo(expected).Within(0.5f),"the tracker's measurement stays the base; the follow only tops up what the wrist cannot hold");
            Assert.That(Mathf.Abs(ResidualRollDeg(i, r)), Is.LessThan(KeepFrac * leftover + 0.5f),"the leftover the blend used to abandon in the wrist must now be carried by the forearm");
            Assert.That(Vector3.Distance(r.ElbowSolved, i.Elbow), Is.LessThan(1e-5f),"still a pure roll: the elbow stays on the tracker's pole");
        }
        [Test]
        public void Follow_PositionOnlyTracker_TheForearmStillFollowsTheHand()
        {
            BasisArmSolveInput i = Pose(Vector3.forward, Vector3.down, 25f);
            i.HintWeight = true;
            i.HintIsTracker = true;
            i.HintPosition = i.Elbow;   // HintRotation stays zero: a position-only puck
            Roll(ref i, 80f);

            BasisArmSolveCore.Solve(i, out BasisArmSolveResult r);

            Assert.That(r.ForearmRollDeg, Is.EqualTo(80f - KeepFrac * 80f).Within(0.5f),"a puck with no usable rotation must not strand the hand's roll in the wrist");
            Assert.That(Vector3.Distance(r.ElbowSolved, i.Elbow), Is.LessThan(1e-5f),"still a pure roll: the elbow stays on the measured pole");
        }
        [Test]
        public void Follow_ForearmCeiling_TheCapBindsAndTheRestStaysInTheWrist()
        {
            BasisArmSolveInput i = Pose(Vector3.forward, Vector3.down, 25f);
            Roll(ref i, 150f);

            BasisArmSolveCore.Solve(i, out BasisArmSolveResult r);

            Assert.That(Mathf.Abs(r.ForearmRollDeg), Is.LessThanOrEqualTo(RollCap + 1e-3f),"the forearm has its own anatomical ceiling — the follow may not spin it past the cap");
            Assert.That(r.HandError, Is.LessThan(1e-4f), "the overflow is dropped, never bought with the hand");
        }
        [Test]
        public void Follow_SeamWindow_ReleasesContinuously_AndIsSilentAtTheSeam()
        {
            BasisArmSolveInput probe = Pose(Vector3.forward, Vector3.down, 25f);
            Roll(ref probe, 179.5f);
            BasisArmSolveCore.Solve(probe, out BasisArmSolveResult atSeam);
            Assert.That(Mathf.Abs(atSeam.ForearmRollDeg), Is.LessThan(0.75f),"at the ±180 seam the follow must have released completely — a bound there cannot be continuous");

            float prev = float.NaN, worstStep = 0f;
            for (float demand = 140f; demand <= 179f; demand += 0.5f)
            {
                BasisArmSolveInput i = Pose(Vector3.forward, Vector3.down, 25f);
                Roll(ref i, demand);
                BasisArmSolveCore.Solve(i, out BasisArmSolveResult r);

                if (!float.IsNaN(prev))
                {
                    worstStep = Mathf.Max(worstStep, Mathf.Abs(r.ForearmRollDeg - prev));
                }
                prev = r.ForearmRollDeg;
            }
            Assert.That(worstStep, Is.LessThan(8f), $"the seam release must be a fade, not a cliff (worst step {worstStep:F1} deg per 0.5 deg of demand; " +"the release window intentionally sheds its roll over 155→178, same shape as the relief's wrap fade)");
        }
    }
}
