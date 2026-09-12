using NUnit.Framework;
using UnityEngine;
using Basis.IK;
namespace Basis.Tests.IK
{
    public class BasisArmForearmRollTests
    {
        const float upper = 0.28f, lower = 0.26f, keepFrac = BasisArmSolveCore.WristKeepFrac, keepMax = BasisArmSolveCore.WristKeepMaxDeg, cap = BasisArmSolveCore.ForearmRollMaxDeg;
        static readonly Vector3 shoulder = new Vector3(0.18f, 1.40f, 0f), chestHand = new Vector3(0.22f, 1.22f, 0.34f);
        static float Expected(float demand)
        {
            float magnitude = Mathf.Abs(demand), roll = magnitude - Mathf.Min(keepFrac * magnitude, keepMax);
            return (demand < 0f ? -1f : 1f) * Mathf.Min(roll, cap);
        }
        static float Roll(float demandDeg, Vector3 target) => Roll(demandDeg, target, out _, out _, out _);
        static float Roll(float demandDeg, Vector3 target, out BasisArmSolveInput i, out BasisArmSolveResult r, out Quaternion lowerRot)
        {
            i = BasisArmSolveInput.Defaults(false);
            i.Shoulder = shoulder;
            i.RestElbow = i.Shoulder + new Vector3(upper, 0f, 0f);
            i.RestHand = i.RestElbow + new Vector3(lower, 0f, 0f);
            i.RestHandRotation = Quaternion.identity;
            i.TargetPosition = target;
            i.TargetRotation = Quaternion.identity;
            i.HasHead = true;
            i.HeadPosition = new Vector3(0f, 1.62f, 0f);
            BasisArmState state = default;
            BasisArmSolveCore.Solve(i, ref state, out r);
            Assert.That(r.Valid, Is.True);
            BasisArmSolveCore.Pose(i, r, Quaternion.identity, Quaternion.identity, out _, out lowerRot);
            i.TargetRotation = Quaternion.AngleAxis(demandDeg, (r.Hand - r.Elbow).normalized) * lowerRot;
            return BasisArmSolveCore.ForearmRoll(i, r, lowerRot, Quaternion.identity);
        }
        [Test]
        public void AHandOnItsNeutral_DemandsNoRoll()
        {
            Assert.That(Roll(0f, chestHand), Is.EqualTo(0f).Within(0.01f), "an untwisted hand must leave the forearm alone");
        }
        [TestCase(10f)]
        [TestCase(25f)]
        [TestCase(60f)]
        [TestCase(90f)]
        [TestCase(-45f)]
        [TestCase(-100f)]
        public void TheForearmCarriesTheTwist_TheWristKeepsOnlyItsMeasuredShare(float demand)
        {
            float roll = Roll(demand, chestHand);
            Assert.That(roll, Is.EqualTo(Expected(demand)).Within(0.3f), $"the forearm must carry {demand:F0} deg of hand twist minus the wrist's own share");
            float residual = demand - roll;
            Assert.That(Mathf.Abs(residual), Is.LessThanOrEqualTo(keepMax + 0.3f), $"the wrist was left carrying {residual:F1} deg of axial twist");
            Assert.That(Mathf.Abs(residual), Is.GreaterThan(0f), "the carpus carries a real share, it is not driven to zero");
            Assert.That(roll * demand, Is.GreaterThan(0f), "the forearm must roll the way the hand is twisted");
        }
        [Test]
        public void TheForearmHasItsOwnCeiling_AndTheOverflowStaysInTheWrist()
        {
            float roll = Roll(150f, chestHand);
            Assert.That(Mathf.Abs(roll), Is.EqualTo(cap).Within(0.01f), "past its ceiling the forearm must stop, not keep spinning");
            Assert.That(150f - roll, Is.EqualTo(30f).Within(0.5f), "the overflow beyond the ceiling is what the wrist is left with");
        }
        [Test]
        public void TheSeamReleasesAsAFade_AndIsSilentAtTheSeam()
        {
            Assert.That(Mathf.Abs(Roll(179.5f, chestHand)), Is.LessThan(0.75f), "at the plus-minus 180 seam the follow must have released completely");
            float previous = Roll(140f, chestHand), worst = 0f;
            for (float demand = 140.5f; demand <= 179f; demand += 0.5f)
            {
                float roll = Roll(demand, chestHand);
                worst = Mathf.Max(worst, Mathf.Abs(roll - previous));
                previous = roll;
            }
            Assert.That(worst, Is.LessThan(8f), $"the seam release must be a fade, not a cliff ({worst:F1} deg per 0.5 deg of demand)");
        }
        [Test]
        public void TheRollIsPure_TheElbowAndTheHandDoNotMove()
        {
            foreach (float demand in new[] { 30f, 75f, 140f })
            {
                float roll = Roll(demand, chestHand, out BasisArmSolveInput i, out BasisArmSolveResult r, out Quaternion lowerRot);
                Vector3 offset = i.RestHand - i.RestElbow;
                Vector3 hand = r.Elbow + lowerRot * offset, rolled = r.Elbow + Quaternion.AngleAxis(roll, (r.Hand - r.Elbow).normalized) * lowerRot * offset;
                Assert.That(Vector3.Distance(hand, rolled), Is.LessThan(1e-4f), $"demand {demand:F0}: the roll moved the hand, so it is not a pure roll about the forearm");
                Assert.That(Vector3.Distance(hand, r.Hand), Is.LessThan(1e-4f), "the posed forearm must already put the hand on the solved position");
            }
        }
        [Test]
        public void EveryReachAndTwist_LeavesTheWristInsideItsHumanShare()
        {
            float worst = 0f, worstDemand = 0f;
            for (int e = -60; e <= 60; e += 30)
                for (int a = -120; a <= 120; a += 40)
                    for (int reach = 0; reach < 3; reach++)
                        foreach (float demand in new[] { -120f, -60f, 25f, 95f })
                        {
                            float el = e * Mathf.Deg2Rad, az = a * Mathf.Deg2Rad;
                            Vector3 dir = new Vector3(Mathf.Cos(el) * Mathf.Sin(az), Mathf.Sin(el), Mathf.Cos(el) * Mathf.Cos(az));
                            Vector3 target = shoulder + dir * ((0.45f + 0.2f * reach) * (upper + lower));
                            float residual = Mathf.Abs(demand - Roll(demand, target));
                            if (residual > worst) { worst = residual; worstDemand = demand; }
                        }
            Assert.That(worst, Is.LessThanOrEqualTo(keepMax + 0.5f), $"somewhere in the reach envelope the wrist was left with {worst:F1} deg of twist (demand {worstDemand:F0})");
        }
    }
}
