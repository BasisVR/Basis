using NUnit.Framework;
using UnityEngine;
using Basis.IK;

namespace Basis.Tests.IK
{
    /// <summary>
    /// The knee-hint snap at full extension (<see cref="BasisLegSolveCore"/>).
    ///
    /// Scenario: a user with a KNEE/lower-leg tracker straightens their leg — standing up, or simply loading the
    /// leg. As the leg approaches straight, the knee closes on the hip→foot axis and the pole vector degenerates:
    /// its lever arm goes to zero, so it stops carrying usable direction. The solver has to stop steering by it.
    ///
    /// The bug is HOW it stopped. `SolveLegs` gated the hint on `abProj.sqrMagnitude > maxReach^2 * 0.001` — a
    /// boolean cliff. `abProj` IS the bend radius, so it sweeps continuously to zero as the leg straightens, and
    /// the hint went from fully applied to not applied at all between two adjacent frames. The knee jumped
    /// straight from wherever the tracker was pointing it to wherever the fixed BendNormal points — a visible
    /// snap, on both the way out and the way back. The arm solver already ramps this (`hintFade` in
    /// <see cref="BasisArmSolveCore"/>); the leg never got one.
    ///
    /// HOW THIS IS TESTED. A snap is a discontinuity, so it cannot be caught by checking any single pose — every
    /// individual frame looks perfectly reasonable. It only exists BETWEEN frames. So sweep the foot target
    /// through the singularity in fine steps and measure the KNEE TRAVEL PER MILLIMETRE OF FOOT TRAVEL. Smooth
    /// solving keeps that ratio bounded; a hint that switches off in one step shows up as a single enormous
    /// spike, because the knee teleports around the bend circle while the foot has barely moved.
    ///
    /// The knee genuinely does move faster than the foot near full extension — the bend radius collapses like
    /// sqrt(maxReach - d), so the ratio climbs to ~12 on its own. That is geometry, not a bug. The snap sits an
    /// order of magnitude above it, so the gate below separates them cleanly.
    /// </summary>
    public class BasisLegHintExtensionSnapTests
    {
        // A leg in a normal standing-ish rest pose: knee tracked slightly forward of the hip→ankle line.
        static readonly Vector3 Hip = Vector3.zero;
        static readonly Vector3 Knee = new Vector3(0f, -0.40f, 0.12f);
        static readonly Vector3 Ankle = new Vector3(0f, -0.80f, 0.02f);

        // The knee bends in the sagittal plane, so the bend axis is medio-lateral.
        static readonly Vector3 BendNormal = Vector3.right;

        static float MaxReach => Vector3.Distance(Hip, Knee) + Vector3.Distance(Knee, Ankle);

        /// <summary>
        /// Knee travel per unit of foot travel. Anything a solver does smoothly stays low; a pole that switches
        /// off in a single step teleports the knee around the bend circle and spikes.
        /// </summary>
        const float SnapGate = 30f;

        /// <summary>
        /// A knee tracker whose pole sits well off the plane the fixed BendNormal bends in. This is the case that
        /// actually bites: feet turned out, or any leg the user is holding with a bit of rotation. If the hint and
        /// the BendNormal happened to agree the snap would be invisible, and the test would pass while the bug
        /// stayed — so the two are deliberately pulled apart.
        /// </summary>
        static Vector3 LateralHint => new Vector3(0.16f, -0.40f, 0.02f);

        static BasisLegSolveInput LegAt(float extensionRatio, Vector3 hint)
        {
            return new BasisLegSolveInput
            {
                Root = Hip,
                Mid = Knee,
                Tip = Ankle,
                RootRotation = Quaternion.identity,
                MidRotation = Quaternion.identity,
                TargetPosition = Hip + Vector3.down * (extensionRatio * MaxReach),
                TargetRotation = Quaternion.identity,
                TargetOffset = Quaternion.identity,
                HintPosition = hint,
                HintWeight = 1f,
                BendNormal = BendNormal,
            };
        }

        /// <summary>
        /// Sweeps the foot from <paramref name="from"/> to <paramref name="to"/> and returns the worst knee
        /// travel per unit of foot travel seen between two adjacent steps.
        /// </summary>
        static float WorstSensitivity(float from, float to, Vector3 hint, out float worstAt)
        {
            const int Steps = 400;
            float worst = 0f;
            worstAt = from;

            BasisLegSolveCore.Solve(LegAt(from, hint), out BasisLegSolveResult previous);
            Vector3 previousKnee = previous.KneeSolved;

            for (int s = 1; s <= Steps; s++)
            {
                float ratio = Mathf.Lerp(from, to, s / (float)Steps);
                BasisLegSolveCore.Solve(LegAt(ratio, hint), out BasisLegSolveResult now);

                float footTravel = Mathf.Abs(to - from) / Steps * MaxReach;
                float kneeTravel = Vector3.Distance(now.KneeSolved, previousKnee);
                float sensitivity = kneeTravel / Mathf.Max(footTravel, 1e-6f);

                if (sensitivity > worst)
                {
                    worst = sensitivity;
                    worstAt = ratio;
                }
                previousKnee = now.KneeSolved;
            }
            return worst;
        }

        [Test]
        public void KneeDoesNotSnapAsTheLegStraightens()
        {
            float worst = WorstSensitivity(0.95f, 0.999f, LateralHint, out float at);

            Assert.Less(worst, SnapGate,
                $"the knee jumped {worst:F1}x the foot's travel in a single step at extension {at:F4} — "
                + "that is the hint being switched off rather than faded out");
        }

        /// <summary>Straightening and folding back must behave the same; a one-way cliff reads as a snap on return.</summary>
        [Test]
        public void KneeDoesNotSnapAsTheLegFoldsBack()
        {
            float worst = WorstSensitivity(0.999f, 0.95f, LateralHint, out float at);

            Assert.Less(worst, SnapGate,
                $"the knee jumped {worst:F1}x the foot's travel coming back at extension {at:F4}");
        }

        /// <summary>
        /// The fade must not become a way to quietly ignore knee trackers. A leg with a real bend in it is exactly
        /// where the pole is well-conditioned, and there the hint has to be followed at full strength.
        /// </summary>
        [Test]
        public void ABentLegStillFollowsItsKneeHint()
        {
            BasisLegSolveCore.Solve(LegAt(0.75f, LateralHint), out BasisLegSolveResult withHint);
            BasisLegSolveCore.Solve(LegAt(0.75f, Knee), out BasisLegSolveResult withRestHint);

            Assert.IsTrue(withHint.HintApplied, "a well-bent leg must still take its hint");

            Vector3 axis = Vector3.down;
            float lateral = Vector3.Dot(withHint.KneeSolved - Hip, Vector3.right);
            float restLateral = Vector3.Dot(withRestHint.KneeSolved - Hip, Vector3.right);

            Assert.Greater(lateral - restLateral, 0.05f,
                "the laterally-placed knee tracker must actually pull the knee out to the side");
        }

        /// <summary>
        /// At full extension the knee's response to the hint must be BOUNDED, not a snap. The old solver let the
        /// leg lock dead straight, so the pole degenerated (rho -> 0) and the knee HAD to stop following the hint
        /// entirely -- this test used to assert exactly that. BasisLegSolveCore.MaxKneeInteriorDeg now caps the
        /// leg a few degrees short of straight, so the knee keeps a small but non-zero lever arm and rides a
        /// small bend circle instead of a degenerate point. The anti-snap property is preserved (the circle
        /// shrinks CONTINUOUSLY to that cap radius -- see the two KneeDoesNotSnap tests), but now the guarantee
        /// is the stronger one: the knee is never on the axis, so it is never a free-spinning stick.
        /// </summary>
        [Test]
        public void AtFullExtensionTheKneeKeepsABoundedLeverArm()
        {
            BasisLegSolveCore.Solve(LegAt(0.9999f, LateralHint), out BasisLegSolveResult lateral);
            BasisLegSolveCore.Solve(LegAt(0.9999f, Knee), out BasisLegSolveResult forward);

            // rho at the cap, from the two segment lengths -- the radius of the residual knee circle.
            float upper = Vector3.Distance(Hip, Knee), lower = Vector3.Distance(Knee, Ankle);
            float chord = Mathf.Sqrt(upper * upper + lower * lower
                - 2f * upper * lower * Mathf.Cos(BasisLegSolveCore.MaxKneeInteriorDeg * Mathf.Deg2Rad));
            float capRho = upper * lower * Mathf.Sin(BasisLegSolveCore.MaxKneeInteriorDeg * Mathf.Deg2Rad) / chord;

            // The cap holds a real lever arm off the hip->ankle axis: no degenerate pole, no free-spinning stick.
            Vector3 axis = (lateral.FootSolved - Hip).normalized;
            float lever = Vector3.ProjectOnPlane(lateral.KneeSolved - Hip, axis).magnitude;
            Assert.Greater(lever, capRho * 0.75f,
                $"at full extension the knee lost its lever arm ({lever * 100f:F2} cm, cap guarantees ~{capRho * 100f:F2} cm) "
                + "-- it has collapsed onto the axis and become a free-spinning stick");

            // ... and its dependence on where the tracker sits is BOUNDED by that small circle -- a hint on the
            // far side of a ~2 cm circle can move the knee at most a circle diameter, never an unbounded snap.
            Assert.Less(Vector3.Distance(lateral.KneeSolved, forward.KneeSolved), 2f * capRho + 0.005f,
                "the knee's response to the hint at full extension is not bounded by the cap circle");
        }
    }
}
