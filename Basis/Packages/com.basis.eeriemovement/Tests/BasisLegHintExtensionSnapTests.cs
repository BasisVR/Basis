using NUnit.Framework;
using UnityEngine;
using Basis.IK;
namespace Basis.Tests.IK
{
    public class BasisLegHintExtensionSnapTests
    {
        // A leg in a normal standing-ish rest pose: knee tracked slightly forward of the hip→ankle line.
        static readonly Vector3 Hip = Vector3.zero, Knee = new Vector3(0f, -0.40f, 0.12f);
        static readonly Vector3 Ankle = new Vector3(0f, -0.80f, 0.02f);
        // The knee bends in the sagittal plane, so the bend axis is medio-lateral.
        static readonly Vector3 BendNormal = Vector3.right;
        static float MaxReach => Vector3.Distance(Hip, Knee) + Vector3.Distance(Knee, Ankle);
        const float SnapGate = 30f;
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

            Assert.Less(worst, SnapGate, $"the knee jumped {worst:F1}x the foot's travel in a single step at extension {at:F4} — " + "that is the hint being switched off rather than faded out");
        }
        [Test]
        public void KneeDoesNotSnapAsTheLegFoldsBack()
        {
            float worst = WorstSensitivity(0.999f, 0.95f, LateralHint, out float at);

            Assert.Less(worst, SnapGate, $"the knee jumped {worst:F1}x the foot's travel coming back at extension {at:F4}");
        }
        [Test]
        public void ABentLegStillFollowsItsKneeHint()
        {
            BasisLegSolveCore.Solve(LegAt(0.75f, LateralHint), out BasisLegSolveResult withHint);
            BasisLegSolveCore.Solve(LegAt(0.75f, Knee), out BasisLegSolveResult withRestHint);

            Assert.IsTrue(withHint.HintApplied, "a well-bent leg must still take its hint");

            Vector3 axis = Vector3.down;
            float lateral = Vector3.Dot(withHint.KneeSolved - Hip, Vector3.right);
            float restLateral = Vector3.Dot(withRestHint.KneeSolved - Hip, Vector3.right);

            Assert.Greater(lateral - restLateral, 0.05f,"the laterally-placed knee tracker must actually pull the knee out to the side");
        }
        [Test]
        public void AtFullExtensionTheKneeKeepsABoundedLeverArm()
        {
            BasisLegSolveCore.Solve(LegAt(0.9999f, LateralHint), out BasisLegSolveResult lateral);
            BasisLegSolveCore.Solve(LegAt(0.9999f, Knee), out BasisLegSolveResult forward);

            // rho at the cap, from the two segment lengths -- the radius of the residual knee circle.
            float upper = Vector3.Distance(Hip, Knee), lower = Vector3.Distance(Knee, Ankle);
            float chord = Mathf.Sqrt(upper * upper + lower * lower - 2f * upper * lower * Mathf.Cos(BasisLegSolveCore.MaxKneeInteriorDeg * Mathf.Deg2Rad));
            float capRho = upper * lower * Mathf.Sin(BasisLegSolveCore.MaxKneeInteriorDeg * Mathf.Deg2Rad) / chord;

            // The cap holds a real lever arm off the hip->ankle axis: no degenerate pole, no free-spinning stick.
            Vector3 axis = (lateral.FootSolved - Hip).normalized;
            float lever = Vector3.ProjectOnPlane(lateral.KneeSolved - Hip, axis).magnitude;
            Assert.Greater(lever, capRho * 0.75f, $"at full extension the knee lost its lever arm ({lever * 100f:F2} cm, cap guarantees ~{capRho * 100f:F2} cm) " + "-- it has collapsed onto the axis and become a free-spinning stick");

            // ... and its dependence on where the tracker sits is BOUNDED by that small circle -- a hint on the
            // far side of a ~2 cm circle can move the knee at most a circle diameter, never an unbounded snap.
            Assert.Less(Vector3.Distance(lateral.KneeSolved, forward.KneeSolved), 2f * capRho + 0.005f,"the knee's response to the hint at full extension is not bounded by the cap circle");
        }
    }
}
