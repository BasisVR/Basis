using Basis.IK;
using NUnit.Framework;

namespace Basis.Tests.Fitting
{
    public sealed class BasisSeatTargetPlanTests
    {
        private static BasisEerieMovement Planned(in BasisEerieFrameFacts facts)
        {
            BasisEerieMovement job = default;
            job.plan.leftArm.has = true;
            job.plan.rightArm.has = true;
            job.plan.leftLeg.has = true;
            job.plan.rightLeg.has = true;
            BasisEeriePlanner.Frame(ref job, in facts);
            return job;
        }

        [Test]
        public void ATargetedHand_RunsTheArmSolveOnDesktop()
        {
            BasisEerieMovement job = Planned(new BasisEerieFrameFacts { leftHandWeight = 1f });
            Assert.IsTrue(job.plan.leftArm.solve, "a targeted hand needs the arm solve");
            Assert.AreEqual(1f, job.plan.leftArm.weight);
            Assert.IsFalse(job.plan.rightArm.solve, "an untargeted desktop hand stays out of the solve");
        }

        [Test]
        public void ATargetedFoot_UsesTheTrackerPathAndItsRotation()
        {
            BasisEerieMovement job = Planned(new BasisEerieFrameFacts { leftFootTracked = true, leftLegTracked = true });
            Assert.AreEqual(BasisEerieSource.Tracker, job.plan.leftLeg.target);
            Assert.IsTrue(job.plan.leftLeg.solve);
            Assert.IsFalse(job.plan.leftLeg.preserveTip, "the target must set the foot rotation");
            Assert.AreEqual(BasisEerieSource.None, job.plan.rightLeg.target, "feet must be targeted independently");
        }
    }
}
