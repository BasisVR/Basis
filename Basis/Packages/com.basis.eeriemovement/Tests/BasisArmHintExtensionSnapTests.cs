using NUnit.Framework;
using UnityEngine;
using Basis.IK;
namespace Basis.Tests.IK
{
    public class BasisArmHintExtensionSnapTests
    {
        // Right arm, T-pose-ish: shoulder at the origin, arm out along +X, elbow bent slightly back.
        static readonly Vector3 Shoulder = Vector3.zero, Elbow = new Vector3(0.27f, -0.02f, -0.04f);
        static readonly Vector3 Hand = new Vector3(0.53f, -0.04f, 0.02f);
        static float MaxReach => Vector3.Distance(Shoulder, Elbow) + Vector3.Distance(Elbow, Hand);
        const float SnapGate = 30f;
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

            Assert.Less(worst, SnapGate, $"the elbow jumped {worst:F1}x the hand's travel in a single step at extension {at:F4} — " + "the hintFade ramp is on ahProj, but the cliff is on abProj");
        }
        [Test]
        public void ElbowDoesNotSnapAsTheArmFoldsBack_WithATracker()
        {
            float worst = WorstSensitivity(0.999f, 0.95f, LateralHint, true, out float at);

            Assert.Less(worst, SnapGate, $"the elbow jumped {worst:F1}x the hand's travel coming back at extension {at:F4}");
        }
        [Test]
        public void ElbowDoesNotSnapAsTheArmStraightens_WithoutATracker()
        {
            float worst = WorstSensitivity(0.95f, 0.999f, LateralHint, false, out float at);

            Assert.Less(worst, SnapGate, $"the elbow jumped {worst:F1}x the hand's travel in a single step at extension {at:F4}");
        }
        [Test]
        public void ABentArmStillFollowsItsElbowHint()
        {
            BasisArmSolveCore.Solve(ArmAt(0.75f, LateralHint, true), out BasisArmSolveResult withHint);
            BasisArmSolveCore.Solve(ArmAt(0.75f, Elbow, true), out BasisArmSolveResult withRestHint);

            Assert.IsTrue(withHint.HintApplied, "a well-bent arm must still take its hint");

            float lifted = Vector3.Dot(withHint.ElbowSolved - Shoulder, Vector3.up);
            float rest = Vector3.Dot(withRestHint.ElbowSolved - Shoulder, Vector3.up);

            Assert.Greater(lifted - rest, 0.05f,"the raised elbow tracker must actually pull the elbow up toward it");
        }
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
            float chord = Mathf.Sqrt(upper * upper + lower * lower - 2f * upper * lower * Mathf.Cos(BasisArmSolveCore.MaxElbowAngleDeg * Mathf.Deg2Rad));
            float capRho = upper * lower * Mathf.Sin(BasisArmSolveCore.MaxElbowAngleDeg * Mathf.Deg2Rad) / chord;
            Vector3 axis = (lateral.HandSolved - Shoulder).normalized;
            float lever = Vector3.ProjectOnPlane(lateral.ElbowSolved - Shoulder, axis).magnitude;
            Assert.Greater(lever, capRho * 0.75f, $"at full extension the elbow lost its lever arm ({lever * 100f:F2} cm, cap guarantees ~{capRho * 100f:F2} cm) " + "-- it collapsed onto the shoulder->hand axis and became a free-spinning stick");

            Assert.Less(Vector3.Distance(lateral.ElbowSolved, rest.ElbowSolved), 2f * capRho + 0.005f,"the elbow's response to where the tracker sits at full extension is not bounded by the cap circle");
        }
    }
}
