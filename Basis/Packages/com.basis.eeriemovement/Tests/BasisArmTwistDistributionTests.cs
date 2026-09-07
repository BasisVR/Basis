using NUnit.Framework;
using UnityEngine;
using Basis.IK;
namespace Basis.Tests.IK
{
    public class BasisArmTwistDistributionTests
    {
        const float BoneLen = 0.26f;
        static readonly Vector3 Axis = Vector3.forward;
        [Test]
        public void SegmentPositionFraction_MatchesBonePlacement()
        {
            for (float p = 0.05f; p <= 0.95f; p += 0.05f)
            {
                float got = BasisTwistSolveCore.SegmentPositionFraction(Vector3.zero, Axis * BoneLen, Axis * (p * BoneLen));
                Assert.That(got, Is.EqualTo(p).Within(1e-4f), $"position fraction wrong for a bone at {p:0.00} along the segment.");
            }
            // A perpendicular stand-off projects onto the segment; positions outside the segment clamp to [0,1].
            Assert.That(BasisTwistSolveCore.SegmentPositionFraction(Vector3.zero, Axis * BoneLen, Axis * (0.5f * BoneLen) + Vector3.up * 0.1f), Is.EqualTo(0.5f).Within(1e-3f), "perpendicular offset should not change the along-segment fraction.");
            Assert.That(BasisTwistSolveCore.SegmentPositionFraction(Vector3.zero, Axis * BoneLen, -Axis * 0.5f), Is.EqualTo(0f), "before the parent clamps to 0.");
            Assert.That(BasisTwistSolveCore.SegmentPositionFraction(Vector3.zero, Axis * BoneLen, Axis * (2f * BoneLen)), Is.EqualTo(1f), "past the child clamps to 1.");
        }
        [Test]
        public void TwistDistributesEvenly_AcrossBonePositions()
        {
            // Position-proportional share (distribution strength 1) puts the twist bone exactly on the linear
            // gradient, so the parent->bone and bone->child spans twist at the SAME rate (concentration ~1, no
            // pile-up) for any bone placement -- the fix for "roll piles up at one joint".
            float worstConc = 1f, worstEvenErr = 0f, worstAt = 0f;
            for (float p = 0.1f; p <= 0.9f; p += 0.1f)
            {
                float eff = BasisTwistSolveCore.SegmentPositionFraction(Vector3.zero, Axis * BoneLen, Axis * (p * BoneLen)); // strength 1
                foreach (float roll in new[] { 30f, 75f, 120f, 160f })
                {
                    float bone = SolveBoneRoll(eff, roll), effMeas = bone / roll;
                    float rate1 = effMeas / p;            // twist rate over parent->bone vs the even ideal (1)
                    float rate2 = (1f - effMeas) / (1f - p); // twist rate over bone->child vs the even ideal (1)
                    float conc = Mathf.Max(rate1, rate2);
                    if (conc > worstConc) { worstConc = conc; worstAt = p; }
                    worstEvenErr = Mathf.Max(worstEvenErr, Mathf.Abs(bone - p * roll));
                }
            }
            Assert.That(worstConc, Is.LessThan(1.25f), $"twist piles up (concentration {worstConc:0.00}x the even rate at p={worstAt:0.0}) -- distribution is not position-proportional.");
            Assert.That(worstEvenErr, Is.LessThan(2f), $"twist bone sits {worstEvenErr:0.0} deg off the linear (even) gradient.");
        }
        [Test]
        public void FixedFraction_PilesUpAtWristEndBone_WhichIsWhyEvenIsNeeded()
        {
            // Documents the failure the position-proportional share fixes: a FIXED 0.5 fraction on a bone near
            // the wrist (p = 0.9) leaves the last tenth of the bone carrying ~5x the twist rate -- the candy-wrap.
            // If this ever stops over-twisting, the even-distribution guard above is no longer meaningful.
            const float p = 0.9f;
            float bone = SolveBoneRoll(0.5f, 120f);          // old behavior: fixed fraction regardless of position
            float effMeas = bone / 120f;
            float wristSpanRate = (1f - effMeas) / (1f - p); // bone->child twist rate vs the even ideal (1)
            Assert.That(wristSpanRate, Is.GreaterThan(2f), $"expected a fixed-fraction wrist-end bone to over-twist its short span (got {wristSpanRate:0.0}x the even rate).");
        }
        // A rig that authors BOTH the helper and the driving child off-axis from the arm bone -- hands posed
        // palm-down under the forearm, helpers exported carrying their own roll. Neither is live twist.
        static readonly Quaternion TwistBind = Quaternion.AngleAxis(35f, Axis);
        static readonly Quaternion ChildBind = Quaternion.AngleAxis(-50f, Axis);
        [Test]
        public void BindPose_LeavesTwistBoneOnItsAuthoredRotation()
        {
            // Nothing has moved off the T-pose, so the helper must land exactly where the rig authored it.
            // Reading raw locals instead put it at parent * roll-share, throwing the authored rotation away
            // -- a constant wrong twist on every frame, worst on the upper arm where it reads as the whole
            // limb being rolled.
            Quaternion parent = Quaternion.Euler(15f, -40f, 70f);
            // child sitting on its bind
            bool apply = BasisTwistSolveCore.Solve(parent, parent * ChildBind, parent * (Axis * BoneLen), 0.5f, ChildBind, TwistBind, out Quaternion twistWorld, out _, out float twistAngleDeg);

            Assert.That(apply, Is.True, "bind pose should still resolve a twist write.");
            Assert.That(twistAngleDeg, Is.EqualTo(0f).Within(0.01f), $"authored child roll read as live twist ({twistAngleDeg:0.00} deg with nothing moving).");
            Assert.That(Quaternion.Angle(twistWorld, parent * TwistBind), Is.LessThan(0.01f),"twist bone left its authored rotation in a clean T-pose.");
        }
        [Test]
        public void LiveRoll_IsMeasuredFromBind_AndComposesOntoIt()
        {
            // Same off-axis rig, now genuinely rolled: the recovered angle is the departure from bind, and
            // the helper takes its share of that measured from its own authored rotation.
            const float Roll = 60f, Fraction = 0.5f;
            Quaternion parent = Quaternion.Euler(-25f, 12f, 5f);
            bool apply = BasisTwistSolveCore.Solve(parent, parent * Quaternion.AngleAxis(Roll, Axis) * ChildBind, parent * (Axis * BoneLen), Fraction, ChildBind, TwistBind, out Quaternion twistWorld, out _, out float twistAngleDeg);

            Assert.That(apply, Is.True);
            Assert.That(twistAngleDeg, Is.EqualTo(Roll).Within(0.01f),"recovered twist should be the roll past bind, with the authored roll cancelled out.");
            Assert.That(Quaternion.Angle(twistWorld, parent * TwistBind), Is.EqualTo(Roll * Fraction).Within(0.01f),"helper's share should be measured from its authored rotation, not from the parent.");
        }
        [Test]
        public void UnbakedBinds_FallBackToIdentity()
        {
            // Zero-quaternion bind fields are what every call site that predates bind cancellation (sweeps,
            // equivariance) still passes. Those must keep the raw behaviour rather than normalising a zero
            // into NaN.
            const float Roll = 40f;
            bool apply = BasisTwistSolveCore.Solve(Quaternion.identity, Quaternion.AngleAxis(Roll, Axis), Axis * BoneLen, 1f, default, default, out Quaternion twistWorld, out _, out float twistAngleDeg);

            Assert.That(apply, Is.True);
            Assert.That(twistAngleDeg, Is.EqualTo(Roll).Within(0.01f), "zero bind quaternions should read as identity.");
            Assert.That(IsFinite(twistWorld), Is.True, "zero bind quaternion normalised into a non-finite rotation.");
        }
        static bool IsFinite(Quaternion q) => !(float.IsNaN(q.x) || float.IsNaN(q.y) || float.IsNaN(q.z) || float.IsNaN(q.w) || float.IsInfinity(q.x) || float.IsInfinity(q.y) || float.IsInfinity(q.z) || float.IsInfinity(q.w));
        // One twist solve: child rolled by 'roll' about the bone axis; returns the twist bone's roll magnitude.
        static float SolveBoneRoll(float fraction, float roll)
        {
            bool apply = BasisTwistSolveCore.Solve(Quaternion.identity, Quaternion.AngleAxis(roll, Axis), Axis * BoneLen, fraction, default, default, out Quaternion twistWorld, out _, out _);
            return apply ? Quaternion.Angle(Quaternion.identity, twistWorld) : 0f;
        }
    }
}
