using Basis.IK;
using NUnit.Framework;
using UnityEngine;
namespace Basis.Tests.IK
{
    public class BasisEeriePlannerTests
    {
        const float dt = 0.01f;
        static BasisEerieFrameFacts Ready() => new BasisEerieFrameFacts { footSimReady = true, footIKSetting = true, upright = true, deltaTime = dt };
        [Test]
        public void FootIK_RampsTheBlendInOnlyAfterTheStationaryDelay()
        {
            BasisEerieFrameFacts facts = Ready();
            float timer = 0f, left = 0f, right = 0f;
            int frames = 0;
            while (left <= 0f && frames < 100)
            {
                BasisEeriePlanner.FootIK(ref facts, ref timer, ref left, ref right, out _, out _);
                frames++;
            }
            Assert.That(frames, Is.EqualTo(Mathf.CeilToInt(BasisEeriePlanner.stationaryDelaySeconds / dt)).Within(1), "the sim must not take the feet until the player has been still for the delay");
            Assert.That(left, Is.EqualTo(BasisEeriePlanner.footIKBlendInSpeed * dt).Within(1e-5f), "the first ramp step is one blend-in step");
            Assert.That(facts.leftFootSim, Is.EqualTo(left), "the facts carry the new blend");
        }
        [Test]
        public void FootIK_MovingResetsTheStationaryClock()
        {
            BasisEerieFrameFacts facts = Ready();
            facts.moving = true;
            float timer = 0.2f, left = 0f, right = 0f;
            BasisEeriePlanner.FootIK(ref facts, ref timer, ref left, ref right, out _, out _);
            Assert.That(timer, Is.EqualTo(0f));
            Assert.That(left, Is.EqualTo(0f), "a moving player keeps the animated feet");
        }
        [Test]
        public void FootIK_ATrackedLegIsForcedOffAndTheOtherStillSimulates()
        {
            BasisEerieFrameFacts facts = Ready();
            facts.leftLegTracked = true;
            float timer = 1f, left = 0.7f, right = 0.7f;
            BasisEeriePlanner.FootIK(ref facts, ref timer, ref left, ref right, out bool simulate, out _);
            Assert.That(left, Is.EqualTo(0f), "a tracked leg never blends toward the sim");
            Assert.That(right, Is.GreaterThan(0.7f), "the untracked leg keeps ramping");
            Assert.That(simulate, Is.True, "one untracked leg is enough to run the sim");
            facts.rightLegTracked = true;
            BasisEeriePlanner.FootIK(ref facts, ref timer, ref left, ref right, out simulate, out _);
            Assert.That(simulate, Is.False, "two tracked legs never run the sim");
        }
        [Test]
        public void FootIK_ReengageFiresOnTheRisingEdgeOnly()
        {
            BasisEerieFrameFacts facts = Ready();
            float timer = 1f, left = 0f, right = 0f;
            BasisEeriePlanner.FootIK(ref facts, ref timer, ref left, ref right, out _, out bool first);
            BasisEeriePlanner.FootIK(ref facts, ref timer, ref left, ref right, out _, out bool second);
            Assert.That(first, Is.True);
            Assert.That(second, Is.False);
        }
        [Test]
        public void FootIK_ProneNotUprightOrSettingOff_BlendsTheSimOut()
        {
            for (int gate = 0; gate < 3; gate++)
            {
                BasisEerieFrameFacts facts = Ready();
                if (gate == 0) facts.prone = true;
                else if (gate == 1) facts.upright = false;
                else facts.footIKSetting = false;
                float timer = 1f, left = 1f, right = 1f;
                BasisEeriePlanner.FootIK(ref facts, ref timer, ref left, ref right, out _, out _);
                Assert.That(left, Is.EqualTo(1f - BasisEeriePlanner.footIKBlendOutSpeed * dt).Within(1e-5f), $"gate {gate} must blend the sim out");
            }
        }
        [Test]
        public void Frame_ShoulderMode_SolveBeatsTrackerBeatsNone()
        {
            var job = new BasisEerieMovement { shoulderSolveEnabled = true };
            job.plan.hasLeftShoulder = true;
            BasisEeriePlanner.Frame(ref job, new BasisEerieFrameFacts { leftShoulderTracked = true });
            Assert.That(job.plan.leftShoulder, Is.EqualTo(BasisEerieShoulderMode.Solve));
            job.shoulderSolveEnabled = false;
            BasisEeriePlanner.Frame(ref job, new BasisEerieFrameFacts { leftShoulderTracked = true });
            Assert.That(job.plan.leftShoulder, Is.EqualTo(BasisEerieShoulderMode.Tracker));
            BasisEeriePlanner.Frame(ref job, default);
            Assert.That(job.plan.leftShoulder, Is.EqualTo(BasisEerieShoulderMode.None));
            Assert.That(job.plan.rightShoulder, Is.EqualTo(BasisEerieShoulderMode.None), "an unbound shoulder is never solved or driven");
        }
        [Test]
        public void Frame_ToeSurfaceBend_NeedsASimDrivenFootAndARealBend()
        {
            var job = new BasisEerieMovement();
            job.plan.hasLeftToe = true;
            job.plan.leftLeg.has = true;
            BasisEerieFrameFacts facts = new BasisEerieFrameFacts { footSimReady = true, leftFootSim = 1f, leftToeBend = true };
            BasisEeriePlanner.Frame(ref job, facts);
            Assert.That(job.plan.leftLeg.target, Is.EqualTo(BasisEerieSource.Sim));
            Assert.That(job.plan.leftToeSurface, Is.True);
            Assert.That(job.plan.leftToeDriven, Is.False);
            facts.leftToeBend = false;
            BasisEeriePlanner.Frame(ref job, facts);
            Assert.That(job.plan.leftToeSurface, Is.False, "no bend, no surface pass");
            facts.leftToeBend = true;
            facts.leftLegTracked = true;
            BasisEeriePlanner.Frame(ref job, facts);
            Assert.That(job.plan.leftToeSurface, Is.False, "a tracked foot is never surface-bent");
            facts.leftToeTracked = true;
            BasisEeriePlanner.Frame(ref job, facts);
            Assert.That(job.plan.leftToeDriven, Is.True);
        }
        [Test]
        public void Frame_ElbowProtect_ATrackedElbowNeedsTheCollideTrackedElbowSetting()
        {
            var job = new BasisEerieMovement { collisionsEnabled = true, protectElbow = true };
            job.plan.hasChest = job.plan.hasNeck = true;
            BasisEeriePlanner.Frame(ref job, new BasisEerieFrameFacts { leftHandWeight = 1f });
            Assert.That(job.plan.leftArm.elbowProtect, Is.True);
            BasisEeriePlanner.Frame(ref job, new BasisEerieFrameFacts { leftHandWeight = 1f, leftElbowTracked = true });
            Assert.That(job.plan.leftArm.elbowProtect, Is.False, "a tracker-placed elbow is left alone unless collideTrackedElbow is on");
            job.collideTrackedElbow = true;
            BasisEeriePlanner.Frame(ref job, new BasisEerieFrameFacts { leftHandWeight = 1f, leftElbowTracked = true });
            Assert.That(job.plan.leftArm.elbowProtect, Is.True);
            job.plan.hasNeck = false;
            BasisEeriePlanner.Frame(ref job, new BasisEerieFrameFacts { leftHandWeight = 1f, leftElbowTracked = true });
            Assert.That(job.plan.leftArm.elbowProtect, Is.False, "no neck, no chest capsule to protect against");
        }
        [Test]
        public void Frame_HintRoll_NeedsATrackedJointAndACalibratedRoll()
        {
            var job = new BasisEerieMovement();
            BasisEeriePlanner.Frame(ref job, new BasisEerieFrameFacts { leftElbowTracked = true, leftKneeTracked = true });
            Assert.That(job.plan.leftArm.hintRoll, Is.False);
            Assert.That(job.plan.leftLeg.hintRoll, Is.False);
            BasisEeriePlanner.Frame(ref job, new BasisEerieFrameFacts { leftElbowTracked = true, leftElbowRoll = true, leftKneeRoll = true });
            Assert.That(job.plan.leftArm.hintRoll, Is.True);
            Assert.That(job.plan.leftLeg.hintRoll, Is.False, "roll data without a knee tracker is never applied");
        }
        [Test]
        public void Frame_ZeroOffsetsAndUpBecomeIdentityAndUnit()
        {
            var job = new BasisEerieMovement { playerUp = new Vector3(0f, 3f, 0f) };
            BasisEeriePlanner.Frame(ref job, default);
            Assert.That(job.offsetRotationHips, Is.EqualTo(Quaternion.identity));
            Assert.That(job.playerUp, Is.EqualTo(Vector3.up));
            job.playerUp = Vector3.zero;
            BasisEeriePlanner.Frame(ref job, default);
            Assert.That(job.playerUp, Is.EqualTo(Vector3.up));
        }
    }
}
