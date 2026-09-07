using NUnit.Framework;
using UnityEngine;
using Basis.IK;
namespace Basis.Tests.IK
{
    public class BasisLegHintReachTests
    {
        const float Upper = 0.42f, Lower = 0.43f;
        const float TolM = 1e-4f;   // 0.1 mm
        static readonly Vector3 Hip = new Vector3(0.09f, 0.92f, 0f);
        static BasisLegSolveInput Leg(Vector3 target, Vector3 hint, float weight)
        {
            BasisLegSolveInput i = default;
            i.Root = Hip;
            i.Mid = Hip + new Vector3(0f, -Upper, 0.05f);          // knee slightly forward
            i.Tip = i.Mid + new Vector3(0f, -Lower, -0.05f);
            i.RootRotation = Quaternion.identity;
            i.MidRotation = Quaternion.identity;
            i.TargetPosition = target;
            i.TargetRotation = Quaternion.identity;
            i.TargetOffset = Quaternion.identity;
            i.HintPosition = hint;
            i.HintWeight = weight;
            i.BendNormal = Vector3.right;
            return i;
        }
        [Test]
        public void KneeHint_NeverMovesTheFootOffItsTarget()
        {
            float worst = 0f, worstAtDeg = 0f;

            foreach (float ext in new[] { 0.60f, 0.80f, 0.90f, 0.97f })
            {
                // A reachable target straight below the hip, at `ext` of full extension.
                Vector3 target = Hip + new Vector3(0f, -(Upper + Lower) * ext, 0f), axis = (target - Hip).normalized;
                Vector3 poleRef = Vector3.forward;   // a direction perpendicular to the (vertical) leg axis

                foreach (float weight in new[] { 0.25f, 0.5f, 1f })
                {
                    for (float deg = 0f; deg < 360f; deg += 15f)
                    {
                        Vector3 poleDir = Quaternion.AngleAxis(deg, axis) * poleRef;
                        Vector3 hint = Hip + axis * (Upper * 0.5f) + poleDir * 0.30f;

                        BasisLegSolveCore.Solve(Leg(target, hint, weight), out BasisLegSolveResult r);

                        float err = Vector3.Distance(r.FootSolved, target);
                        if (err > worst) { worst = err; worstAtDeg = deg; }
                    }
                }
            }

            Assert.That(worst, Is.LessThan(TolM), $"the knee hint slid the foot {worst * 1000f:F2} mm off its commanded target (worst at a hint " + $"{worstAtDeg:F0} deg around the leg axis) -- the hint swivel is not reach-preserving");
        }
        [Test]
        public void KneeHint_AntiParallelPole_KeepsTheFootOnTarget()
        {
            Vector3 target = Hip + new Vector3(0f, -(Upper + Lower) * 0.85f, 0f), axis = (target - Hip).normalized;

            // The seeded knee bulges toward +Z, so put the hint's pole at exactly -Z.
            Vector3 hint = Hip + axis * (Upper * 0.5f) + new Vector3(0f, 0f, -0.30f);

            BasisLegSolveCore.Solve(Leg(target, hint, 1f), out BasisLegSolveResult r);

            Assert.That(Vector3.Distance(r.FootSolved, target), Is.LessThan(TolM), $"an anti-parallel knee hint moved the foot {Vector3.Distance(r.FootSolved, target) * 1000f:F2} mm off target");
        }
        [Test]
        public void KneeHint_StillActuallySwivelsTheKnee()
        {
            Vector3 target = Hip + new Vector3(0f, -(Upper + Lower) * 0.85f, 0f), axis = (target - Hip).normalized;
            Vector3 hint = Hip + axis * (Upper * 0.5f) + new Vector3(0.30f, 0f, 0f);   // pull the knee to +X

            BasisLegSolveCore.Solve(Leg(target, hint, 1f), out BasisLegSolveResult r);

            Vector3 pole = Vector3.ProjectOnPlane(r.KneeSolved - Hip, axis).normalized;
            Vector3 want = Vector3.ProjectOnPlane(hint - Hip, axis).normalized;

            Assert.That(Vector3.Angle(pole, want), Is.LessThan(15f), $"the knee ended {Vector3.Angle(pole, want):F1} deg from the hint it was given -- the hint is being ignored");
        }
    }
}
