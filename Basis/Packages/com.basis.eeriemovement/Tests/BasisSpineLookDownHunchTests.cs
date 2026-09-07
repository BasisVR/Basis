using NUnit.Framework;
using UnityEngine;
using Basis.IK;
namespace Basis.Tests.IK
{
    public class BasisSpineLookDownHunchTests
    {
        // Production defaults, matching BasisFullBodyIK.SetDefaults().
        const float SpineBendPitch = 0.45f, SpineBendYaw = 0.10f, SpineBendRoll = 0.35f;
        const float UpperBendPitch = 0.25f, UpperBendYaw = 0.30f, UpperBendRoll = 0.20f;
        const float SpineMaxForwardDeg = 60f, SpineMaxBackwardDeg = 25f, SpineMaxLateralDeg = 25f;
        const float SquishBoost = 0.5f, BendTwistCoupling = 0.15f, RestLen = 1f;
        // ----------------------------------------------------------------- the mechanism (must hunch)
        [Test]
        public void ForwardHeadSwing_HunchesTheTorso_AtNoTrackerWeights()
        {
            // Without a chest tracker the head-lean pre-bend is live: as the HMD arcs forward (look-down)
            // the spine and upper-chest bend forward, growing monotonically with the gaze. This is the
            // artifact a chest-tracked user sees against an otherwise pinned chest -- pinned here so the
            // suppression test below has a real effect to remove.
            float prevSpine = -1f, prevUpper = -1f;
            for (float gaze = 0f; gaze <= 60f; gaze += 10f)
            {
                var r = Solve(HmdLookDownArc(gaze), yaw: 0f, leanWeights: true);
                Assert.That(r.SpineEuler.x, Is.GreaterThanOrEqualTo(prevSpine - 1e-3f), $"spine forward bend dropped as the gaze went down ({prevSpine:0.00} -> {r.SpineEuler.x:0.00} at {gaze:0} deg).");
                Assert.That(r.UpperEuler.x, Is.GreaterThanOrEqualTo(prevUpper - 1e-3f), $"upper-chest forward bend dropped as the gaze went down ({prevUpper:0.00} -> {r.UpperEuler.x:0.00} at {gaze:0} deg).");
                prevSpine = r.SpineEuler.x; prevUpper = r.UpperEuler.x;
            }

            var full = Solve(HmdLookDownArc(60f), yaw: 0f, leanWeights: true);
            Assert.That(full.SpineEuler.x, Is.GreaterThan(1.5f), $"a forward-swung head produced only {full.SpineEuler.x:0.00} deg of spine hunch; the mechanism under test is gone.");
            Assert.That(full.UpperEuler.x, Is.GreaterThan(0.5f), $"a forward-swung head produced only {full.UpperEuler.x:0.00} deg of upper-chest hunch.");
        }
        // ----------------------------------------------------------------- the fix (chest tracker)
        [Test]
        public void ChestTrackerPath_RemovesTheLookDownHunch()
        {
            // With the lean weights zeroed (DistributeSpineBend's chest-tracker branch) the same forward
            // head swing produces no forward/lateral spine bend at any gaze -- the chest stops hunching.
            for (float gaze = 0f; gaze <= 70f; gaze += 10f)
            {
                var r = Solve(HmdLookDownArc(gaze), yaw: 0f, leanWeights: false);
                Assert.That(Mathf.Abs(r.SpineEuler.x), Is.LessThan(0.05f), $"spine still hunched {r.SpineEuler.x:0.00} deg at {gaze:0} deg gaze with the chest-tracker suppression on.");
                Assert.That(Mathf.Abs(r.UpperEuler.x), Is.LessThan(0.05f), $"upper-chest still hunched {r.UpperEuler.x:0.00} deg at {gaze:0} deg gaze with the chest-tracker suppression on.");
                Assert.That(Mathf.Abs(r.SpineEuler.z), Is.LessThan(0.05f), $"spine lateral bend {r.SpineEuler.z:0.00} deg leaked with the chest-tracker suppression on.");
            }
        }
        [Test]
        public void ChestTrackerPath_StillFollowsHeadFacingTwist()
        {
            // The suppression drops only the position lean (pitch/roll); the head-facing twist (yaw) must
            // survive, so the spine still follows where you look even with a chest tracker.
            var r = Solve(new Vector3(0f, 1f, 0f), yaw: 25f, leanWeights: false);
            Assert.That(r.UpperEuler.y, Is.GreaterThan(1f), $"head-facing twist was lost ({r.UpperEuler.y:0.00} deg) when the lean pre-bend was suppressed.");
        }
        // ----------------------------------------------------------------- roll axis + both fixes together
        [Test]
        public void LateralHeadSwing_RollsTheTorso_AndIsSuppressedWithChestTracker()
        {
            // The roll (lateral lean) axis has the same flaw as pitch: a head swung to the side reads as a
            // sideways torso lean. The chest-tracker suppression must drop it too, so the chest doesn't
            // list sideways when you glance off to the side -- not just the forward hunch.
            Vector3 sideHead = new Vector3(0.16f, 1f, 0f);
            var on = Solve(sideHead, yaw: 0f, leanWeights: true);
            Assert.That(Mathf.Abs(on.SpineEuler.z), Is.GreaterThan(1f), $"a side-swung head did not roll the spine ({on.SpineEuler.z:0.00} deg); the mechanism under test is gone.");

            var off = Solve(sideHead, yaw: 0f, leanWeights: false);
            Assert.That(Mathf.Abs(off.SpineEuler.z), Is.LessThan(0.05f), $"spine still rolled {off.SpineEuler.z:0.00} deg with the chest-tracker suppression on.");
            Assert.That(Mathf.Abs(off.UpperEuler.z), Is.LessThan(0.05f), $"upper-chest still rolled {off.UpperEuler.z:0.00} deg with the chest-tracker suppression on.");
        }
        [Test]
        public void ChestTracker_StraightDownAndTurned_NeitherHunchesNorTwists()
        {
            // Both look-down fixes at once: a chest-tracked user looking straight down with the head turned.
            // The lean suppression kills any forward/lateral hunch AND the vertical-gaze fade kills the
            // facing twist, so the whole spine-bend delta collapses to ~0 -- nothing hunches or snaps at
            // the bottom of the look.
            var r = Solve(new Vector3(0f, 1f, 0f), yaw: 20f, leanWeights: false, rotPitch: 90f);
            Assert.That(r.SpineEuler.magnitude, Is.LessThan(0.1f), $"spine-bend delta {r.SpineEuler.magnitude:0.000} deg is non-trivial looking straight down with a chest tracker.");
            Assert.That(r.UpperEuler.magnitude, Is.LessThan(0.1f), $"upper-chest-bend delta {r.UpperEuler.magnitude:0.000} deg is non-trivial looking straight down with a chest tracker.");
        }
        // ----------------------------------------------------------------- helpers (test scaffolding)
        // The HMD arcs forward (and dips slightly) about the neck as the gaze pitches down; this forward
        // swing is what the position-based pre-bend misreads as a torso lean.
        static Vector3 HmdLookDownArc(float gazeDownDeg)
        {
            float a = Mathf.Deg2Rad * gazeDownDeg;
            return new Vector3(0f, 1f - 0.04f * Mathf.Sin(a), 0.16f * Mathf.Sin(a));
        }
        // Build the spine-bend input. Hips at the origin, chest half-way up, head where the arc puts it.
        // leanWeights=false mirrors DistributeSpineBend's chest-tracker branch (pitch/roll weights zeroed,
        // yaw kept). Head rotation is yaw plus an optional pitch (default 0 isolates the position-driven
        // pre-bend from the twist; a non-zero rotPitch is used to drive the vertical-gaze twist fade too).
        static BasisSpineBendResult Solve(Vector3 head, float yaw, bool leanWeights, float rotPitch = 0f)
        {
            BasisSpineBendInput i;
            i.HipsRot = Quaternion.identity;
            i.HipsPos = Vector3.zero;
            i.ChestPos = new Vector3(0f, 0.5f, 0f);
            i.SmoothedHead = head;
            i.HipsBind = Quaternion.identity;
            i.HeadTargetRot = Quaternion.AngleAxis(yaw, Vector3.up) * Quaternion.AngleAxis(rotPitch, Vector3.right);
            i.SpineMaxForwardDeg = SpineMaxForwardDeg;
            i.SpineMaxBackwardDeg = SpineMaxBackwardDeg;
            i.SpineMaxLateralDeg = SpineMaxLateralDeg;
            i.SpineBendPitch = leanWeights ? SpineBendPitch : 0f;
            i.SpineBendYaw = SpineBendYaw;
            i.SpineBendRoll = leanWeights ? SpineBendRoll : 0f;
            i.UpperBendPitch = leanWeights ? UpperBendPitch : 0f;
            i.UpperBendYaw = UpperBendYaw;
            i.UpperBendRoll = leanWeights ? UpperBendRoll : 0f;
            i.AnatDifferentialStiffness = false;
            i.AnatPelvicTwistRouting = false;
            i.BendTwistCoupling = BendTwistCoupling;
            i.SquishBoost = SquishBoost;
            i.RestLen = RestLen;
            i.HasSpine = true;
            i.HasUpper = true;
            BasisSpineBendCore.Solve(i, out BasisSpineBendResult r);
            return r;
        }
    }
}
