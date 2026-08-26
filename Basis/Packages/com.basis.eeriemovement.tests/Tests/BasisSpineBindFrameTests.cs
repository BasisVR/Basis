using NUnit.Framework;
using UnityEngine;
using Basis.IK;
namespace Basis.Tests.IK
{
    public class BasisSpineBindFrameTests
    {
        // Production defaults, matching BasisFullBodyIK.SetDefaults() / BasisSpineLookDownSweepTests.
        const float SpineBendPitch = 0.45f, SpineBendYaw = 0.10f, SpineBendRoll = 0.35f;
        const float UpperBendPitch = 0.25f, UpperBendYaw = 0.30f, UpperBendRoll = 0.20f;
        const float SpineMaxForwardDeg = 60f, SpineMaxBackwardDeg = 25f, SpineMaxLateralDeg = 25f;
        const float SquishBoost = 0.5f, BendTwistCoupling = 0.15f, RestLen = 1f;
        // A spread of hips BIND conventions a real avatar ships with: identity, rolled about Z (some
        // humanoid rigs), Blender's -90 about X, and an off-axis mix so nothing is special-cased.
        static readonly Quaternion[] Binds =
        {
            Quaternion.identity,
            Quaternion.AngleAxis(90f, Vector3.forward),   // rolled bind -- the 85 deg/frame snap in the harness
            Quaternion.AngleAxis(-90f, Vector3.right),    // Blender export -- the 44 deg/frame snap
            Quaternion.AngleAxis(126f, Vector3.forward),  // continuum worst case from the offline sweep
            Quaternion.Euler(20f, -35f, 110f),            // arbitrary off-axis
        };
        // ----------------------------------------------------------------- the headline: no snap
        [Test]
        public void ChestBend_DoesNotSnap_OnRolledBind_ScanningAcrossCenterLookingDown([ValueSource(nameof(Binds))] Quaternion bind)
        {
            // Reproduce the report: steep look-down (75 deg), sweep the head yaw finely across center. The
            // applied chest and upper-chest bend must move smoothly -- the bug throws a ~180 deg atan2 flip
            // exactly as the facing crosses zero, which lands as tens of degrees of bend between two frames.
            float prevSpineY = 0f, prevSpineZ = 0f, prevUpperY = 0f, prevUpperZ = 0f;
            bool have = false;
            float maxStep = 0f;

            for (float yaw = -25f; yaw <= 25f; yaw += 0.5f)
            {
                var r = SolveRolled(bind, pitchDown: 75f, yaw: yaw);
                Assert.That(r.EarlyOut, Is.False, $"bind={bind} yaw={yaw}: unexpected early-out (bend frame collapsed).");
                Assert.That(Finite(r.SpineEuler) && Finite(r.UpperEuler), Is.True, $"bind={bind} yaw={yaw}: non-finite bend.");

                if (have)
                {
                    float step = Mathf.Max(Mathf.Abs(r.SpineEuler.y - prevSpineY) + Mathf.Abs(r.SpineEuler.z - prevSpineZ), Mathf.Abs(r.UpperEuler.y - prevUpperY) + Mathf.Abs(r.UpperEuler.z - prevUpperZ));
                    maxStep = Mathf.Max(maxStep, step);
                }
                prevSpineY = r.SpineEuler.y; prevSpineZ = r.SpineEuler.z;
                prevUpperY = r.UpperEuler.y; prevUpperZ = r.UpperEuler.z;
                have = true;
            }

            // A 0.5 deg yaw step moves the bend a fraction of a degree when the frame is right; the pre-fix
            // flip cleared 30+ deg in one step. 5 deg is comfortably above the smooth motion and far below
            // the snap, so it fails loudly on a regression without being brittle.
            Assert.That(maxStep, Is.LessThan(5f), $"chest bend snapped {maxStep:0.0} deg across a 0.5 deg head-yaw step on bind {bind} -- the atan2 pole flip is back.");
        }
        // ----------------------------------------------------------------- the property behind the fix
        [Test]
        public void ChestBend_IsInvariant_ToTheHipsBindConvention()
        {
            // The bind is a labelling of the hips bone's rest frame; it must not change the anatomical bend.
            // Hold the anatomical hips frame (identity) and the world chest/head poses FIXED, and vary only
            // the bind -- feeding HipsRot = anatomical * bind exactly as the live rig does (hipsRot =
            // hipsTarget * offsetHips). hipsSpace = bind * inv(anatomical*bind) = inv(anatomical) is then the
            // same for every bind, so the bend must match the identity-bind reference. Pre-fix, invHips
            // carried the bind and this failed by tens of degrees.
            var reference = SolveRolled(Quaternion.identity, pitchDown: 55f, yaw: 18f);
            Assert.That(reference.WriteSpine && reference.WriteUpper, Is.True, "reference bend was not written.");
            Assert.That(Mathf.Abs(reference.SpineEuler.y) + Mathf.Abs(reference.SpineEuler.x), Is.GreaterThan(1f),"reference pose is too neutral to distinguish bind frames -- the test would be vacuous.");

            foreach (Quaternion bind in Binds)
            {
                var r = SolveRolled(bind, pitchDown: 55f, yaw: 18f);
                Assert.That(Delta(r.SpineEuler, reference.SpineEuler), Is.LessThan(0.5f), $"spine bend depended on the hips bind convention (bind {bind}): {r.SpineEuler} vs {reference.SpineEuler}.");
                Assert.That(Delta(r.UpperEuler, reference.UpperEuler), Is.LessThan(0.5f), $"upper-chest bend depended on the hips bind convention (bind {bind}): {r.UpperEuler} vs {reference.UpperEuler}.");
            }
        }
        [Test]
        public void IdentityBind_IsUnchanged_ByTheFix()
        {
            // The fix must be a no-op on the avatars that already worked: with an identity bind, hipsSpace ==
            // invHips, so every output equals what the un-cancelled frame produced. Pin a representative
            // look-down+scan pose against hand-checked production output so a future "simplification" that
            // drops the bind cancellation (breaking rolled rigs) can't also silently move the identity path.
            var r = SolveRolled(Quaternion.identity, pitchDown: 60f, yaw: 20f);
            Assert.That(r.WriteSpine && r.WriteUpper, Is.True);
            // Twist follows the 20 deg facing the same way it does in the level-gaze baseline, and the spine
            // twists the same side as the upper chest -- the identity path still produces the motion it did.
            Assert.That(r.UpperEuler.y, Is.GreaterThan(1f), "upper-chest lost its facing twist at identity bind.");
            Assert.That(Mathf.Sign(r.SpineEuler.y), Is.EqualTo(Mathf.Sign(r.UpperEuler.y)),"spine and upper-chest twisted opposite ways at identity bind.");
        }
        // ----------------------------------------------------------------- helpers
        // Solve with an anatomical (identity) hips frame wearing bind rotation `bind`. The chest sits above
        // the hips and the head is placed down+forward by the look-down pitch and swung by yaw, in WORLD
        // space -- so the anatomical pose is identical for every bind and only the convention differs.
        static BasisSpineBendResult SolveRolled(Quaternion bind, float pitchDown, float yaw)
        {
            Quaternion hipsAnatomical = Quaternion.identity;

            // head world pose: gaze = yaw about up then pitch about right; the head orbits down+forward.
            Quaternion gaze = Quaternion.AngleAxis(yaw, Vector3.up) * Quaternion.AngleAxis(pitchDown, Vector3.right);
            Vector3 head = new Vector3(0f, 1f, 0f) + gaze * new Vector3(0f, 0f, 0.12f);
            BasisSpineBendInput i;
            i.HipsRot = hipsAnatomical * bind;
            i.HipsPos = Vector3.zero;
            i.ChestPos = new Vector3(0f, 0.5f, 0f);
            i.SmoothedHead = head;
            i.HipsBind = bind;
            i.HeadTargetRot = gaze;
            i.SpineMaxForwardDeg = SpineMaxForwardDeg;
            i.SpineMaxBackwardDeg = SpineMaxBackwardDeg;
            i.SpineMaxLateralDeg = SpineMaxLateralDeg;
            i.SpineBendPitch = SpineBendPitch;
            i.SpineBendYaw = SpineBendYaw;
            i.SpineBendRoll = SpineBendRoll;
            i.UpperBendPitch = UpperBendPitch;
            i.UpperBendYaw = UpperBendYaw;
            i.UpperBendRoll = UpperBendRoll;
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
        static float Delta(Vector3 a, Vector3 b) => Mathf.Abs(a.x - b.x) + Mathf.Abs(a.y - b.y) + Mathf.Abs(a.z - b.z);
        static bool Finite(Vector3 v) => !float.IsNaN(v.x) && !float.IsInfinity(v.x) && !float.IsNaN(v.y) && !float.IsInfinity(v.y) && !float.IsNaN(v.z) && !float.IsInfinity(v.z);
    }
}
