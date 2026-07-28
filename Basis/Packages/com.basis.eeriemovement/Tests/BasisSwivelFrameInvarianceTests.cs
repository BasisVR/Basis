using NUnit.Framework;
using UnityEngine;
using Basis.IK;

namespace Basis.Tests.IK
{
    /// <summary>
    /// Reference-frame gate for <see cref="BasisSwivelSmootherCore"/> (the shared body of
    /// <see cref="BasisEerieMovement"/>.SmoothElbowSwivel and SmoothKneeSwivel).
    ///
    /// A swivel angle is only meaningful relative to a reference direction. Both smoothers used a WORLD
    /// constant for that reference -- the elbow world-down, the knee world-forward -- while the pole they
    /// measure is a body vector that co-rotates with the player. So a rigid body rotation showed up as a
    /// change in the measured swivel, the One-Euro (1.0-1.5 Hz floor) lagged that phantom change, and the
    /// smoother then actively swung the limb toward a stale, world-locked pole. The knee's comment claims
    /// "damps swivel jitter without lagging a real turn"; with a world reference that claim is false.
    ///
    /// The pre-existing BasisLegTwistSmoothingTests could not see this: it feeds the FILTER a synthetic
    /// swivel signal and proves the filter tracks it. The defect is upstream of the filter -- a turn should
    /// not produce a swivel signal at all. These gates therefore assert on the MEASUREMENT, not the filter.
    ///
    /// The invariant, stated once: for a limb held rigid in the body frame, the measured swivel must be
    /// unchanged under ANY rigid motion of the body (translation, yaw, and -- since the root can tilt via a
    /// play-space flip or a seat/vehicle -- pitch and roll too). Anything else means bulk body motion leaks
    /// into a joint DOF.
    ///
    /// Each invariance gate is paired with a Legacy_* gate that runs the old world-referenced formula and
    /// asserts it VIOLATES the invariant, so the numbers below are measured, not asserted into existence,
    /// and a revert to a world reference fails loudly instead of silently.
    /// </summary>
    public class BasisSwivelFrameInvarianceTests
    {
        const float Dt = 1f / 90f;          // VR frame time
        const float TolDeg = 0.05f;         // float32 noise floor for a well-conditioned SignedAngle; the
                                            // defect these gates catch is 90-180 deg, so this is ~3000x under it
        const float TolMetres = 0.001f;     // 1 mm

        // Leg in BODY-LOCAL coords: hip at origin, foot 0.9 m below, knee splayed forward AND out.
        //
        // The lateral component is load-bearing for the TEST, not the code: Vector3.SignedAngle is acos-based,
        // and acos is ill-conditioned as the angle approaches zero (d(acos)/dx -> inf as x -> 1). A knee placed
        // purely forward sits at a 0 deg baseline swivel, where float32 rounding alone yields ~sqrt(2*1e-7) rad
        // = 0.03 deg of measurement noise -- enough to drown a 0.01 deg gate while saying nothing about the
        // frame. Splaying it to a ~40 deg baseline swivel puts acos in its well-conditioned range.
        static readonly Vector3 LegRoot = new Vector3(0f, 0f, 0f);
        static readonly Vector3 LegMid = new Vector3(0.06f, -0.45f, 0.07f);
        static readonly Vector3 LegTip = new Vector3(0f, -0.90f, 0f);

        // Right arm in BODY-LOCAL coords: shoulder at origin, hand out to the side, elbow hanging down/back.
        static readonly Vector3 ArmRoot = new Vector3(0f, 0f, 0f);
        static readonly Vector3 ArmMid = new Vector3(0.28f, -0.10f, 0.02f);
        static readonly Vector3 ArmTip = new Vector3(0.55f, 0f, 0f);

        static BasisSwivelSmootherInput Leg(Quaternion body, Vector3 translation)
        {
            return Limb(body, translation, LegRoot, LegMid, LegTip, Vector3.forward, Vector3.right,
                        1.5f, 0.20f, 1.0f);
        }

        static BasisSwivelSmootherInput Arm(Quaternion body, Vector3 translation)
        {
            return Limb(body, translation, ArmRoot, ArmMid, ArmTip, Vector3.down, Vector3.zero,
                        BasisSwivelFilterCore.MinCutoffHz, BasisSwivelFilterCore.Beta, BasisSwivelFilterCore.DerivCutoffHz);
        }

        static BasisSwivelSmootherInput Limb(Quaternion body, Vector3 translation, Vector3 root, Vector3 mid, Vector3 tip,
                                             Vector3 referenceLocal, Vector3 fallbackLocal,
                                             float minCutoffHz, float beta, float derivCutoffHz)
        {
            BasisSwivelSmootherInput i = default;
            i.Root = translation + body * root;
            i.Mid = translation + body * mid;
            i.Tip = translation + body * tip;
            i.BodyRotation = body;
            i.ReferenceLocal = referenceLocal;
            i.FallbackLocal = fallbackLocal;
            i.Dt = Dt;
            i.MinCutoffHz = minCutoffHz;
            i.Beta = beta;
            i.DerivCutoffHz = derivCutoffHz;
            i.State = default;
            i.Seeded = false;
            return i;
        }

        static float RawSwivel(in BasisSwivelSmootherInput i)
        {
            BasisSwivelSmootherCore.Solve(i, out BasisSwivelSmootherResult r);
            return r.RawSwivelDeg;
        }

        /// <summary>
        /// The formula as it stood before the fix: the reference direction is a WORLD constant, so it does
        /// not co-rotate with the body. Kept here (and only here) so the gates below can measure the defect
        /// rather than assert it from memory.
        /// </summary>
        static float LegacyWorldRefSwivel(Vector3 root, Vector3 mid, Vector3 tip, Vector3 worldRef, Vector3 worldFallback)
        {
            Vector3 ac = tip - root;
            Vector3 axis = ac.normalized;
            Vector3 refDir = Vector3.ProjectOnPlane(worldRef, axis);
            if (refDir.sqrMagnitude < 1e-8f && worldFallback.sqrMagnitude > 1e-8f)
            {
                refDir = Vector3.ProjectOnPlane(worldFallback, axis);
            }
            Vector3 pole = Vector3.ProjectOnPlane(mid - root, axis);
            return Vector3.SignedAngle(refDir.normalized, pole, axis);
        }

        // ----------------------------------------------------------------- yaw (the shipped defect)

        /// <summary>
        /// Turning in place must not change the measured knee swivel. This is the bug that shipped: with the
        /// world-forward reference the knee's measured swivel tracked the turn angle almost 1:1.
        /// </summary>
        [Test]
        public void KneeSwivel_InvariantUnderBodyYaw()
        {
            float baseline = RawSwivel(Leg(Quaternion.identity, Vector3.zero));
            for (float yaw = -180f; yaw <= 180f; yaw += 15f)
            {
                float measured = RawSwivel(Leg(Quaternion.Euler(0f, yaw, 0f), Vector3.zero));
                Assert.That(Mathf.Abs(Mathf.DeltaAngle(baseline, measured)), Is.LessThan(TolDeg),
                    $"knee swivel moved {Mathf.DeltaAngle(baseline, measured):F2} deg on a {yaw:F0} deg body yaw; " +
                    "a turn is leaking into the knee DOF");
            }
        }

        [Test]
        public void Legacy_KneeSwivel_TracksTheTurnAngle_WorldForwardReference()
        {
            float baseline = LegacyWorldRefSwivel(LegRoot, LegMid, LegTip, Vector3.forward, Vector3.right);
            float worst = 0f;
            for (float yaw = -180f; yaw <= 180f; yaw += 15f)
            {
                Quaternion body = Quaternion.Euler(0f, yaw, 0f);
                float measured = LegacyWorldRefSwivel(body * LegRoot, body * LegMid, body * LegTip, Vector3.forward, Vector3.right);
                worst = Mathf.Max(worst, Mathf.Abs(Mathf.DeltaAngle(baseline, measured)));
            }
            // The standing leg's axis is ~vertical, so the world-forward reference barely moves while the
            // pole rotates with the body: the error is the turn angle itself, up to a full 180 deg reversal.
            Assert.That(worst, Is.GreaterThan(90f),
                "the legacy world-forward reference is expected to track the turn angle; if this no longer " +
                "holds the defect model is wrong and the fix needs re-deriving");
        }

        [Test]
        public void ElbowSwivel_InvariantUnderBodyYaw()
        {
            float baseline = RawSwivel(Arm(Quaternion.identity, Vector3.zero));
            for (float yaw = -180f; yaw <= 180f; yaw += 15f)
            {
                float measured = RawSwivel(Arm(Quaternion.Euler(0f, yaw, 0f), Vector3.zero));
                Assert.That(Mathf.Abs(Mathf.DeltaAngle(baseline, measured)), Is.LessThan(TolDeg),
                    $"elbow swivel moved {Mathf.DeltaAngle(baseline, measured):F2} deg on a {yaw:F0} deg body yaw");
            }
        }

        // ----------------------------------------------------------------- tilt (the latent defect)

        /// <summary>
        /// The elbow's world-down reference happens to survive a YAW (world-down lies on the yaw axis, so it
        /// co-rotates with the projection plane by accident). It does not survive a TILT. The root can tilt:
        /// BasisLocalPlayspaceMover applies a play-space flip and seats/vehicles rotate the root, which is
        /// exactly why the job carries a PlayerUp property at all. Body-relative is correct in both cases.
        /// </summary>
        [Test]
        public void Swivel_InvariantUnderArbitraryBodyRotation()
        {
            float legBase = RawSwivel(Leg(Quaternion.identity, Vector3.zero));
            float armBase = RawSwivel(Arm(Quaternion.identity, Vector3.zero));

            for (float yaw = -150f; yaw <= 150f; yaw += 50f)
            {
                for (float pitch = -60f; pitch <= 60f; pitch += 30f)
                {
                    for (float roll = -60f; roll <= 60f; roll += 30f)
                    {
                        Quaternion body = Quaternion.Euler(pitch, yaw, roll);
                        float leg = RawSwivel(Leg(body, Vector3.zero));
                        float arm = RawSwivel(Arm(body, Vector3.zero));
                        Assert.That(Mathf.Abs(Mathf.DeltaAngle(legBase, leg)), Is.LessThan(TolDeg),
                            $"knee swivel moved on body rotation (yaw {yaw}, pitch {pitch}, roll {roll})");
                        Assert.That(Mathf.Abs(Mathf.DeltaAngle(armBase, arm)), Is.LessThan(TolDeg),
                            $"elbow swivel moved on body rotation (yaw {yaw}, pitch {pitch}, roll {roll})");
                    }
                }
            }
        }

        /// <summary>
        /// Not every tilt exposes the elbow's world-down reference, which is why it survived review. With the
        /// arm out along body-X, a ROLL about world-Z changes the magnitude of the projected world-down but
        /// not its direction -- and refDir is normalized, so the roll cancels and the measurement looks fine.
        /// A PITCH about world-X rotates the arm about its own axis: the pole turns with the body while
        /// world-down stays put, and the full tilt leaks into the swivel. So this gate SEARCHES the tilt
        /// sphere for the worst case rather than trusting one hand-picked rotation.
        /// </summary>
        [Test]
        public void Legacy_ElbowSwivel_BreaksUnderBodyTilt_WorldDownReference()
        {
            float baseline = LegacyWorldRefSwivel(ArmRoot, ArmMid, ArmTip, Vector3.down, Vector3.zero);
            float worst = 0f;
            Vector3 worstTilt = Vector3.zero;

            for (float pitch = -90f; pitch <= 90f; pitch += 15f)
            {
                for (float roll = -90f; roll <= 90f; roll += 15f)
                {
                    Quaternion body = Quaternion.Euler(pitch, 0f, roll);
                    float measured = LegacyWorldRefSwivel(body * ArmRoot, body * ArmMid, body * ArmTip, Vector3.down, Vector3.zero);
                    float dev = Mathf.Abs(Mathf.DeltaAngle(baseline, measured));
                    if (dev > worst) { worst = dev; worstTilt = new Vector3(pitch, 0f, roll); }
                }
            }

            Assert.That(worst, Is.GreaterThan(30f),
                $"the legacy world-down reference is expected to break under a body tilt (worst {worst:F1} deg " +
                $"at pitch/roll {worstTilt.x:F0}/{worstTilt.z:F0}); if it no longer does, the defect model is wrong");
        }

        // ----------------------------------------------------------------- translation

        [Test]
        public void Swivel_InvariantUnderRigidTranslation()
        {
            float legBase = RawSwivel(Leg(Quaternion.identity, Vector3.zero));
            float armBase = RawSwivel(Arm(Quaternion.identity, Vector3.zero));
            // Includes a far-from-origin case: float precision, not the frame, is the only thing that may move.
            foreach (Vector3 t in new[] { new Vector3(3f, 0f, -7f), new Vector3(0f, 12f, 0f), new Vector3(500f, 2f, 500f) })
            {
                Assert.That(Mathf.Abs(Mathf.DeltaAngle(legBase, RawSwivel(Leg(Quaternion.identity, t)))), Is.LessThan(0.05f),
                    $"knee swivel moved on a rigid translation to {t}");
                Assert.That(Mathf.Abs(Mathf.DeltaAngle(armBase, RawSwivel(Arm(Quaternion.identity, t)))), Is.LessThan(0.05f),
                    $"elbow swivel moved on a rigid translation to {t}");
            }
        }

        // ----------------------------------------------------------------- temporal: the visible artifact

        /// <summary>
        /// The end-to-end symptom. Turn on the spot at 180 deg/s (a brisk snap-turn) with the leg held rigid
        /// in the body frame. The smoother must leave the knee exactly where the body put it. With a world
        /// reference the phantom swivel change is lagged by the One-Euro and the smoother SWINGS the knee
        /// toward a stale pole -- the knee visibly drags behind the turn.
        /// </summary>
        [Test]
        public void KneeSwivel_PureTurn_DoesNotDragTheKnee()
        {
            const float turnRateDegPerSec = 180f;
            BasisSwivelFilterState state = default;
            bool seeded = false;
            float worstDriftM = 0f;

            for (int frame = 0; frame < 120; frame++)
            {
                float yaw = turnRateDegPerSec * frame * Dt;
                Quaternion body = Quaternion.Euler(0f, yaw, 0f);

                BasisSwivelSmootherInput i = Leg(body, Vector3.zero);
                i.State = state;
                i.Seeded = seeded;

                BasisSwivelSmootherCore.Solve(i, out BasisSwivelSmootherResult r);
                if (r.WriteState) { state = r.State; seeded = r.Seeded; }
                if (!r.Valid) continue;

                // Recover the smoother's desired knee in BODY-LOCAL space and compare against the rigid input.
                Vector3 desiredLocal = Quaternion.Inverse(body) * r.DesiredMid;
                worstDriftM = Mathf.Max(worstDriftM, Vector3.Distance(desiredLocal, LegMid));
            }

            Assert.That(worstDriftM, Is.LessThan(TolMetres),
                $"the knee drifted {worstDriftM * 100f:F2} cm in the body frame during a pure {turnRateDegPerSec:F0} deg/s turn; " +
                "the smoother is dragging it toward a stale pole");
        }

        /// <summary>
        /// The fix must not simply freeze the joint. A genuine shin swivel (the user rotating their own leg
        /// about the hip->foot axis, body still) must still come through -- attenuated by the One-Euro at
        /// first, then tracking. Guards the obvious over-correction.
        /// </summary>
        [Test]
        public void KneeSwivel_RealSwivelMotion_StillTracks()
        {
            const float swivelRateDegPerSec = 60f;
            BasisSwivelFilterState state = default;
            bool seeded = false;
            float initialSwivel = 0f;
            float lastSmooth = 0f;

            for (int frame = 0; frame < 90; frame++)
            {
                float swivel = swivelRateDegPerSec * frame * Dt;
                // Rotate the knee about the hip->foot axis (body still): a real swivel, not a body turn.
                // Root and tip both lie on that axis, so only the pole moves -- the limb stays consistent.
                Vector3 mid = Quaternion.AngleAxis(swivel, (LegTip - LegRoot).normalized) * LegMid;

                BasisSwivelSmootherInput i = Leg(Quaternion.identity, Vector3.zero);
                i.Mid = mid;
                i.State = state;
                i.Seeded = seeded;

                BasisSwivelSmootherCore.Solve(i, out BasisSwivelSmootherResult r);
                if (frame == 0) initialSwivel = r.RawSwivelDeg;
                if (r.WriteState) { state = r.State; seeded = r.Seeded; }
                lastSmooth = r.SmoothSwivelDeg;
            }

            // The knee starts at a non-zero baseline swivel (see LegMid), so measure the DELTA it tracked.
            float commanded = swivelRateDegPerSec * 89 * Dt;
            float tracked = Mathf.Abs(Mathf.DeltaAngle(initialSwivel, lastSmooth));
            Assert.That(tracked, Is.GreaterThan(0.75f * commanded),
                $"a real {swivelRateDegPerSec:F0} deg/s shin swivel only reached {tracked:F1} of {commanded:F1} deg; " +
                "the smoother is over-damping deliberate motion");
        }

        /// <summary>
        /// Steady state is a no-op: a limb held perfectly still must not be moved by the smoother at all.
        /// Guards against the filter injecting motion of its own.
        /// </summary>
        [Test]
        public void Swivel_HeldStill_DoesNotMoveTheJoint()
        {
            BasisSwivelFilterState state = default;
            bool seeded = false;
            float worstM = 0f;

            for (int frame = 0; frame < 60; frame++)
            {
                BasisSwivelSmootherInput i = Leg(Quaternion.Euler(0f, 37f, 0f), new Vector3(2f, 0f, 5f));
                i.State = state;
                i.Seeded = seeded;

                BasisSwivelSmootherCore.Solve(i, out BasisSwivelSmootherResult r);
                if (r.WriteState) { state = r.State; seeded = r.Seeded; }
                if (!r.Valid) continue;

                worstM = Mathf.Max(worstM, Vector3.Distance(r.DesiredMid, i.Mid));
            }

            Assert.That(worstM, Is.LessThan(TolMetres),
                $"the smoother moved a stationary knee by {worstM * 1000f:F2} mm");
        }
    }
}
