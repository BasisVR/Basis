using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using Basis.IK;

namespace Basis.Tests.IK
{
    /// <summary>
    /// Chicken-wing elbow-flare regression tests (no elbow tracker). Exercise the pure, stream-free
    /// <see cref="BasisElbowFlareCore"/> directly -- the same Core the live rig
    /// (BasisFullIKConstraintJob.ComputeArmBendFromLookup) and the offline BasisArmIKSweep.RunChickenWing
    /// harness call -- and verify the user-felt properties:
    ///
    ///   1. PUSH OUT   -- turning the controllers inward pushes the derived elbow OUT toward the half-T-pose
    ///                    mark (the requested "push the elbows out").
    ///   2. HARD CLAMP -- a committed chicken-wing never crosses the halfway line to straight-out-to-the-side
    ///                    (the requested "should never exceed the halfway 90 deg mark"), and never wings UP.
    ///   3. NO-OP OFF  -- with no inward roll the flare is an exact no-op, so normal reaches are untouched.
    ///
    /// Swivel is measured in BasisElbowFlareCore's own basis: 0 deg = elbow straight down, +cap = out toward
    /// the body's outward side. Engagement is the chicken-wing amount (the live rig derives it from the
    /// controller roll; here it is passed explicitly so the clamp/push geometry is checked deterministically).
    /// </summary>
    public class BasisElbowFlareTests
    {
        const float Upper = 0.28f;
        const float Lower = 0.26f;
        const float ArmLen = Upper + Lower;
        const float Cap = 45f; // half-T-pose mark; matches the runtime ElbowFlareMaxDeg default

        static readonly Vector3 Shoulder = Vector3.zero;
        static readonly Vector3 Outward = Vector3.right; // right arm: away-from-body = +X
        static readonly Vector3 Up = Vector3.up;

        static Vector3 RestElbow => Shoulder + new Vector3(0.15f, -0.95f, 0.27f).normalized * Upper;
        static Vector3 RestHand => RestElbow + new Vector3(0f, -0.30f, 0.95f).normalized * Lower;

        NativeArray<Vector3> _table;

        [OneTimeSetUp]
        public void OneTimeSetUp() => _table = new NativeArray<Vector3>(BasisArmBendLookup.GenerateDefaultTable(), Allocator.Persistent);

        [OneTimeTearDown]
        public void OneTimeTearDown() { if (_table.IsCreated) _table.Dispose(); }

        // ------------------------------------------------------------------ core clamp / push / no-op

        [Test]
        public void Flare_FullEngagement_LandsExactlyAtCap()
        {
            // Whatever the natural pole, a full chicken-wing lands the elbow exactly at +cap: a tucked elbow is
            // pushed OUT to the half-T-pose mark, a wide one (or one winging up) is pulled IN to it.
            Vector3 axis = Vector3.forward; // forearm pointing straight ahead
            foreach (float s0 in new[] { -60f, -40f, -10f, 0f, 15f, 30f, 44f, 60f, 80f, 120f })
            {
                Vector3 bend = PoleAt(s0, axis);
                Vector3 flared = BasisElbowFlareCore.ApplyFlare(bend, axis, Outward, Up, 1f, Cap);
                float sw = SwivelOf(flared, axis);
                Assert.That(sw, Is.EqualTo(Cap).Within(0.5f),
                    $"natural swivel {s0:0} deg: full flare must land at +{Cap} deg (out to the half-T-pose mark), got {sw:0.0}.");
            }
        }

        [Test]
        public void Flare_ZeroEngagement_IsExactNoOp()
        {
            Vector3 axis = Vector3.forward;
            foreach (float s0 in new[] { -50f, 0f, 25f, 55f, 90f })
            {
                Vector3 bend = PoleAt(s0, axis);
                Vector3 flared = BasisElbowFlareCore.ApplyFlare(bend, axis, Outward, Up, 0f, Cap);
                Assert.That((flared - bend).magnitude, Is.LessThan(1e-5f),
                    $"engagement 0 must be an exact no-op (natural swivel {s0:0}), but the bend moved {(flared - bend).magnitude:0.0000}.");
            }
        }

        [Test]
        public void Flare_PushesElbowOut_MonotonicallyTowardCap()
        {
            // A tucked elbow (natural swivel well inside the cap) is pushed progressively OUT as the chicken-wing
            // engages, reaching the cap at full engagement and never overshooting it.
            Vector3 axis = Vector3.forward;
            Vector3 bend = PoleAt(12f, axis); // elbow mostly down, slightly out
            float prev = float.NegativeInfinity;
            foreach (float r in new[] { 0f, 0.25f, 0.5f, 0.75f, 1f })
            {
                float sw = SwivelOf(BasisElbowFlareCore.ApplyFlare(bend, axis, Outward, Up, r, Cap), axis);
                Assert.That(sw, Is.GreaterThanOrEqualTo(prev - 0.5f), $"swivel must not move back inward as the wing engages (r={r}, sw={sw:0.0}).");
                Assert.That(sw, Is.LessThanOrEqualTo(Cap + 0.5f), $"swivel must never cross the cap (r={r}, sw={sw:0.0}).");
                prev = sw;
            }
            float full = SwivelOf(BasisElbowFlareCore.ApplyFlare(bend, axis, Outward, Up, 1f, Cap), axis);
            Assert.That(full - 12f, Is.GreaterThan(5f), "full chicken-wing must push the tucked elbow meaningfully OUT.");
        }

        [Test]
        public void Flare_NeverCrossesCap_OnceCommitted()
        {
            // Once the chicken-wing is committed (engagement >= the cap-ramp end of 0.3), the elbow must never
            // cross the half-T-pose mark, for ANY natural pole -- this is the "won't feel right past halfway" rule.
            Vector3 axis = Vector3.forward;
            for (float s0 = -90f; s0 <= 110f; s0 += 10f)
            {
                Vector3 bend = PoleAt(s0, axis);
                for (float r = 0.3f; r <= 1f + 1e-4f; r += 0.1f)
                {
                    float sw = SwivelOf(BasisElbowFlareCore.ApplyFlare(bend, axis, Outward, Up, r, Cap), axis);
                    Assert.That(Mathf.Abs(sw), Is.LessThanOrEqualTo(Cap + 0.5f),
                        $"committed chicken-wing crossed the cap: natural {s0:0}, engage {r:0.0}, swivel {sw:0.0}.");
                }
            }
        }

        [Test]
        public void Flare_EndToEnd_SolvedElbowStaysWithinCap()
        {
            // Integration: feed the flared pole as a hint through the real two-bone solve and confirm the SOLVED
            // elbow respects the cap (the solve follows the well-conditioned clamped hint). Mirrors RunChickenWing.
            foreach (Vector3 target in ChickenWingTargets())
            {
                Vector3 bend = LookupBend(target - Shoulder);
                Vector3 flared = BasisElbowFlareCore.ApplyFlare(bend, target - Shoulder, Outward, Up, 1f, Cap);
                Vector3 hint = Shoulder + 0.5f * ArmLen * flared;
                BasisArmSolveResult r = SolveOne(Shoulder, RestElbow, RestHand, target, hint, true, float.MaxValue);
                if (r.ReachRatio > 1f) continue;
                float sw = SwivelOf(r.ElbowSolved - Shoulder, r.HandSolved - Shoulder);
                Assert.That(Mathf.Abs(sw), Is.LessThanOrEqualTo(Cap + 8f),
                    $"solved chicken-wing elbow at {target} reached {sw:0.0} deg, past the {Cap} deg half-T-pose cap (+8 tol).");
            }
        }

        // ------------------------------------------------------------------ roll -> engagement derivation

        [Test]
        public void RollEngagement_ZeroWhenNeutral_PositiveWhenRolledIn()
        {
            Vector3 axis = Vector3.forward;
            // Neutral: the controller's up-axis is the body up -> no chicken-wing.
            float neutral = BasisElbowFlareCore.RollEngagement01(Quaternion.identity, axis, Outward, Up, 1f, 70f);
            Assert.That(neutral, Is.EqualTo(0f).Within(1e-3f), "a level controller must read zero chicken-wing engagement.");

            // Rolled inward: roll the controller about the forearm so its up-axis tilts toward the outward side.
            Quaternion rolledIn = Quaternion.AngleAxis(-35f, axis);
            float engaged = BasisElbowFlareCore.RollEngagement01(rolledIn, axis, Outward, Up, 1f, 70f);
            Assert.That(engaged, Is.GreaterThan(0.2f), "rolling the controller inward must produce a positive chicken-wing engagement.");
        }

        [Test]
        public void RollEngagement_GainDisablesAndFlips()
        {
            Vector3 axis = Vector3.forward;
            Quaternion rolledIn = Quaternion.AngleAxis(-35f, axis);
            Assert.That(BasisElbowFlareCore.RollEngagement01(rolledIn, axis, Outward, Up, 0f, 70f), Is.EqualTo(0f),
                "gain 0 must disable the flare entirely.");
            Assert.That(BasisElbowFlareCore.RollEngagement01(rolledIn, axis, Outward, Up, -1f, 70f), Is.EqualTo(0f),
                "a negative gain must flip the roll direction (this roll no longer engages).");
        }

        // ------------------------------------------------------------------ helpers

        static Vector3[] ChickenWingTargets() => new[]
        {
            Frac(-0.30f, -0.20f, 0.45f),
            Frac(-0.15f, 0.10f, 0.45f),
            Frac(-0.30f, 0.20f, 0.55f),
            Frac(0.05f, -0.35f, 0.55f),
            Frac(-0.20f, -0.40f, 0.40f),
        };

        static Vector3 Frac(float fx, float fy, float fz) => Shoulder + new Vector3(fx, fy, fz) * ArmLen;

        Vector3 LookupBend(Vector3 shoulderToHand) => BasisArmBendLookup.SampleTrilinear(_table, shoulderToHand / ArmLen).normalized;

        // BasisElbowFlareCore's swing-plane basis, replicated so the tests measure in the exact frame the flare
        // is applied: downPole = straight down (swivel 0), outPole = out to the body's outward side (swivel +90).
        static void SwingBasis(Vector3 axisRaw, out Vector3 axis, out Vector3 downPole, out Vector3 outPole)
        {
            axis = axisRaw.normalized;
            downPole = Vector3.ProjectOnPlane(-Up, axis).normalized;
            outPole = Vector3.ProjectOnPlane(Outward, axis);
            outPole -= downPole * Vector3.Dot(outPole, downPole);
            outPole.Normalize();
        }

        // A unit bend pole at the given swivel (deg) in the swing plane about axisRaw.
        static Vector3 PoleAt(float deg, Vector3 axisRaw)
        {
            SwingBasis(axisRaw, out _, out Vector3 d, out Vector3 o);
            float r = deg * Mathf.Deg2Rad;
            return (d * Mathf.Cos(r) + o * Mathf.Sin(r)).normalized;
        }

        // Signed swivel of a pole about axisRaw, in the same basis (+ = out, - = across, +-180 = up).
        static float SwivelOf(Vector3 pole, Vector3 axisRaw)
        {
            SwingBasis(axisRaw, out Vector3 axis, out Vector3 d, out Vector3 o);
            Vector3 p = Vector3.ProjectOnPlane(pole, axis);
            if (p.sqrMagnitude < 1e-10f) return 0f;
            return Mathf.Atan2(Vector3.Dot(p, o), Vector3.Dot(p, d)) * Mathf.Rad2Deg;
        }

        // Mirror of BasisArmIKSweep.SolveOne: drive BasisArmSolveCore with identity rest rotations (positions are
        // unaffected by them) so the result is pure geometry.
        static BasisArmSolveResult SolveOne(Vector3 shoulder, Vector3 elbow, Vector3 hand,
            Vector3 target, Vector3 hint, bool hintOn, float maxStep)
        {
            BasisArmSolveInput input = default;
            input.Shoulder = shoulder;
            input.Elbow = elbow;
            input.Hand = hand;
            input.RootRotation = Quaternion.identity;
            input.MidRotation = Quaternion.identity;
            input.TargetPosition = target;
            input.TargetRotation = Quaternion.identity;
            input.HintPosition = hint;
            input.HintWeight = hintOn;
            input.HintIsTracker = false;
            input.TargetOffset = Quaternion.identity;
            input.PlayerUp = Vector3.up;
            input.HintMaxStepDeg = maxStep;
            BasisArmSolveCore.Solve(input, out BasisArmSolveResult r);
            return r;
        }
    }
}
