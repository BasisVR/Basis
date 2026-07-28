using NUnit.Framework;
using UnityEngine;
using Basis.IK;

namespace Basis.Tests.IK
{
    /// <summary>
    /// The elbow-hint snap at full extension (<see cref="BasisArmSolveCore"/>) — the arm's copy of the leg bug
    /// pinned by <see cref="BasisLegHintExtensionSnapTests"/>.
    ///
    /// The arm LOOKS like it already handles this: it computes a `hintFade` ramp. But that ramp is keyed on
    /// <c>ahProj</c>, the HINT's stand-off from the shoulder→hand axis, which a real strapped-on elbow tracker
    /// keeps well clear of zero. The condition that actually admits the hint also demands
    /// <c>abProj.sqrMagnitude > totalLen^2 * 0.001</c> — and <c>abProj</c> is the ELBOW's own lever arm, which
    /// sweeps continuously to zero as the arm straightens. So the ramp fades a quantity that never collapses,
    /// while a boolean cliff sits on the one that does.
    ///
    /// The HintIsTracker floor makes it worse rather than better: it pins projNorm to 0.30, so `hintFade` is a
    /// flat 1.0 right up to the instant the abProj cliff trips. The elbow therefore falls from FULL hint to NONE
    /// in a single step — a bigger discontinuity than the leg's.
    ///
    /// Measured the same way: sweep the hand through the singularity and watch elbow travel per unit of hand
    /// travel. A pole switched off in one step teleports the elbow around the bend circle while the hand has
    /// barely moved. See BasisLegHintExtensionSnapTests for the full rationale.
    /// </summary>
    public class BasisArmHintExtensionSnapTests
    {
        // Right arm, T-pose-ish: shoulder at the origin, arm out along +X, elbow bent slightly back.
        static readonly Vector3 Shoulder = Vector3.zero;
        static readonly Vector3 Elbow = new Vector3(0.27f, -0.02f, -0.04f);
        static readonly Vector3 Hand = new Vector3(0.53f, -0.04f, 0.02f);

        static float MaxReach => Vector3.Distance(Shoulder, Elbow) + Vector3.Distance(Elbow, Hand);

        const float SnapGate = 30f;

        /// <summary>
        /// An elbow tracker whose pole sits well off the plane the arm would otherwise bend in — the ordinary
        /// case of a strap that has rotated on the limb. If the hint happened to agree with the rest bend the
        /// snap would be invisible and the test would pass while the bug survived.
        /// </summary>
        static Vector3 LateralHint => new Vector3(0.27f, 0.16f, -0.06f);

        static BasisArmSolveInput ArmAt(float extensionRatio, Vector3 hint, bool hintIsTracker)
        {
            return new BasisArmSolveInput
            {
                Shoulder = Shoulder,
                Elbow = Elbow,
                Hand = Hand,
                RootRotation = Quaternion.identity,
                MidRotation = Quaternion.identity,
                TargetPosition = Shoulder + Vector3.right * (extensionRatio * MaxReach),
                TargetRotation = Quaternion.identity,
                TargetOffset = Quaternion.identity,
                HintPosition = hint,
                HintWeight = true,
                PlayerUp = Vector3.up,
                HintMaxStepDeg = float.MaxValue,   // offline: no rate limit, so the raw discontinuity is visible
                HintIsTracker = hintIsTracker,
            };
        }

        static float WorstSensitivity(float from, float to, Vector3 hint, bool hintIsTracker, out float worstAt)
        {
            const int Steps = 400;
            float worst = 0f;
            worstAt = from;

            BasisArmSolveCore.Solve(ArmAt(from, hint, hintIsTracker), out BasisArmSolveResult previous);
            Vector3 previousElbow = previous.ElbowSolved;

            for (int s = 1; s <= Steps; s++)
            {
                float ratio = Mathf.Lerp(from, to, s / (float)Steps);
                BasisArmSolveCore.Solve(ArmAt(ratio, hint, hintIsTracker), out BasisArmSolveResult now);

                float handTravel = Mathf.Abs(to - from) / Steps * MaxReach;
                float elbowTravel = Vector3.Distance(now.ElbowSolved, previousElbow);
                float sensitivity = elbowTravel / Mathf.Max(handTravel, 1e-6f);

                if (sensitivity > worst)
                {
                    worst = sensitivity;
                    worstAt = ratio;
                }
                previousElbow = now.ElbowSolved;
            }
            return worst;
        }

        [Test]
        public void ElbowDoesNotSnapAsTheArmStraightens_WithATracker()
        {
            float worst = WorstSensitivity(0.95f, 0.999f, LateralHint, true, out float at);

            Assert.Less(worst, SnapGate,
                $"the elbow jumped {worst:F1}x the hand's travel in a single step at extension {at:F4} — "
                + "the hintFade ramp is on ahProj, but the cliff is on abProj");
        }

        [Test]
        public void ElbowDoesNotSnapAsTheArmFoldsBack_WithATracker()
        {
            float worst = WorstSensitivity(0.999f, 0.95f, LateralHint, true, out float at);

            Assert.Less(worst, SnapGate,
                $"the elbow jumped {worst:F1}x the hand's travel coming back at extension {at:F4}");
        }

        /// <summary>Without a tracker the pole is lookup-derived, but the abProj cliff is in the shared path.</summary>
        [Test]
        public void ElbowDoesNotSnapAsTheArmStraightens_WithoutATracker()
        {
            float worst = WorstSensitivity(0.95f, 0.999f, LateralHint, false, out float at);

            Assert.Less(worst, SnapGate,
                $"the elbow jumped {worst:F1}x the hand's travel in a single step at extension {at:F4}");
        }

        /// <summary>
        /// The fade must not become a way to quietly ignore elbow trackers. A bent arm is exactly where the pole
        /// is well-conditioned, and there the hint has to be followed.
        /// </summary>
        [Test]
        public void ABentArmStillFollowsItsElbowHint()
        {
            BasisArmSolveCore.Solve(ArmAt(0.75f, LateralHint, true), out BasisArmSolveResult withHint);
            BasisArmSolveCore.Solve(ArmAt(0.75f, Elbow, true), out BasisArmSolveResult withRestHint);

            Assert.IsTrue(withHint.HintApplied, "a well-bent arm must still take its hint");

            float lifted = Vector3.Dot(withHint.ElbowSolved - Shoulder, Vector3.up);
            float rest = Vector3.Dot(withRestHint.ElbowSolved - Shoulder, Vector3.up);

            Assert.Greater(lifted - rest, 0.05f,
                "the raised elbow tracker must actually pull the elbow up toward it");
        }

        /// <summary>At the singularity the pole carries no direction, so the tracker must stop mattering.</summary>
        [Test]
        public void AtFullExtensionTheElbowKeepsABoundedLeverArm()
        {
            // SUPERSEDED ASSERTION. This used to demand the hint stop mattering at full extension, on the premise
            // that the pole degenerates (rho -> 0). BasisArmSolveCore.MaxElbowAngleDeg (170) now caps the arm a
            // few degrees short of straight, so the elbow ALWAYS keeps a lever arm and the hint keeps placing it
            // -- which is exactly what BasisArmTrackerHintAuthorityTests.AStrappedTracker_LandsTheElbowOnItsPole
            // now requires at every extension. So the guarantee flips to the stronger one: the elbow is never on
            // the axis (never a free-spinning stick), and its response to where the tracker sits is BOUNDED by
            // the small cap circle rather than either degenerate-zero OR an unbounded snap. Mirror of
            // BasisLegHintExtensionSnapTests.AtFullExtensionTheKneeKeepsABoundedLeverArm.
            BasisArmSolveCore.Solve(ArmAt(0.9999f, LateralHint, true), out BasisArmSolveResult lateral);
            BasisArmSolveCore.Solve(ArmAt(0.9999f, Elbow, true), out BasisArmSolveResult rest);

            float upper = Vector3.Distance(Shoulder, Elbow), lower = Vector3.Distance(Elbow, Hand);
            float chord = Mathf.Sqrt(upper * upper + lower * lower
                - 2f * upper * lower * Mathf.Cos(BasisArmSolveCore.MaxElbowAngleDeg * Mathf.Deg2Rad));
            float capRho = upper * lower * Mathf.Sin(BasisArmSolveCore.MaxElbowAngleDeg * Mathf.Deg2Rad) / chord;

            Vector3 axis = (lateral.HandSolved - Shoulder).normalized;
            float lever = Vector3.ProjectOnPlane(lateral.ElbowSolved - Shoulder, axis).magnitude;
            Assert.Greater(lever, capRho * 0.75f,
                $"at full extension the elbow lost its lever arm ({lever * 100f:F2} cm, cap guarantees ~{capRho * 100f:F2} cm) "
                + "-- it collapsed onto the shoulder->hand axis and became a free-spinning stick");

            Assert.Less(Vector3.Distance(lateral.ElbowSolved, rest.ElbowSolved), 2f * capRho + 0.005f,
                "the elbow's response to where the tracker sits at full extension is not bounded by the cap circle");
        }
    }
}
