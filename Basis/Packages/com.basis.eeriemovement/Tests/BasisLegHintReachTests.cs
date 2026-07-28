using NUnit.Framework;
using UnityEngine;
using Basis.IK;

namespace Basis.Tests.IK
{
    /// <summary>
    /// Reach-preservation gate for the knee hint (<see cref="BasisLegSolveCore"/>).
    ///
    /// ⚠️ THESE GATES ARE RED. They reproduce a real, unfixed defect; they are not a regression.
    ///
    /// The foot is a COMMANDED target: whatever the knee hint does to the pole, the foot must stay exactly where
    /// it was told to be. It does not. Worst measured: 145 mm on a synthetic sweep (hint 180 deg around the leg
    /// axis), and 27 mm on real human motion (CMU 02_01) -- a visible slide on a foot tracker, and ~25x the
    /// "0.13% of leg length" previously believed to be the worst case.
    ///
    /// ROOT CAUSE -- it is the BEND step, not the hint step. `deltaR` rotates the shin about the knee by an angle
    /// that TriangleAngle picks so the hip->ankle distance comes out equal to the target distance. That derivation
    /// is only valid if the rotation axis is PERPENDICULAR to the hip-knee-ankle plane. The leg's axis is
    /// Cross(hint, bc), and it is then slerped toward BendNormal by the pole-colinear guard and again by the
    /// near-full-extension guard. A blended axis is not perpendicular to that plane, so the interior angle at the
    /// knee does not land where the maths assumed, |ac| != the target distance, and the follow-up rootDelta can
    /// only correct the DIRECTION to the target, never the LENGTH. The arm papers over exactly this with a
    /// 12-iteration bisection that backs the hint off until the hand is on target again; the leg has no such pass.
    ///
    /// THE FIX (not attempted here -- BasisLegSolveCore has a revert history and this is a real rewrite, not a
    /// one-liner): place the knee analytically. Use the pole to fix the bend PLANE, then solve the knee's position
    /// in that plane by trigonometry from |ab|, |bc| and the target distance. That hits the target exactly, in
    /// every configuration, with no bisection.
    ///
    /// The sweep visits the hint all the way around the leg axis so the anti-parallel case is actually exercised,
    /// rather than assuming it never comes up. It does: it is the worst case.
    /// </summary>
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

        /// <summary>
        /// Sweep the hint a full 360 degrees around the leg axis, at several extensions and hint weights. The foot
        /// must land on its target every single time. The 180 degree sample is the one that used to break.
        /// </summary>
        [Test]
        public void KneeHint_NeverMovesTheFootOffItsTarget()
        {
            float worst = 0f;
            float worstAtDeg = 0f;

            foreach (float ext in new[] { 0.60f, 0.80f, 0.90f, 0.97f })
            {
                // A reachable target straight below the hip, at `ext` of full extension.
                Vector3 target = Hip + new Vector3(0f, -(Upper + Lower) * ext, 0f);
                Vector3 axis = (target - Hip).normalized;
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

            Assert.That(worst, Is.LessThan(TolM),
                $"the knee hint slid the foot {worst * 1000f:F2} mm off its commanded target (worst at a hint " +
                $"{worstAtDeg:F0} deg around the leg axis) -- the hint swivel is not reach-preserving");
        }

        /// <summary>
        /// The degenerate case on its own, stated plainly: a hint pointing exactly OPPOSITE the current knee pole.
        /// This is what collapsed FromToRotation's axis and threw the foot off target.
        /// </summary>
        [Test]
        public void KneeHint_AntiParallelPole_KeepsTheFootOnTarget()
        {
            Vector3 target = Hip + new Vector3(0f, -(Upper + Lower) * 0.85f, 0f);
            Vector3 axis = (target - Hip).normalized;

            // The seeded knee bulges toward +Z, so put the hint's pole at exactly -Z.
            Vector3 hint = Hip + axis * (Upper * 0.5f) + new Vector3(0f, 0f, -0.30f);

            BasisLegSolveCore.Solve(Leg(target, hint, 1f), out BasisLegSolveResult r);

            Assert.That(Vector3.Distance(r.FootSolved, target), Is.LessThan(TolM),
                $"an anti-parallel knee hint moved the foot {Vector3.Distance(r.FootSolved, target) * 1000f:F2} mm off target");
        }

        /// <summary>
        /// Reach preservation must not have been bought by ignoring the hint. The knee still has to follow it.
        /// </summary>
        [Test]
        public void KneeHint_StillActuallySwivelsTheKnee()
        {
            Vector3 target = Hip + new Vector3(0f, -(Upper + Lower) * 0.85f, 0f);
            Vector3 axis = (target - Hip).normalized;
            Vector3 hint = Hip + axis * (Upper * 0.5f) + new Vector3(0.30f, 0f, 0f);   // pull the knee to +X

            BasisLegSolveCore.Solve(Leg(target, hint, 1f), out BasisLegSolveResult r);

            Vector3 pole = Vector3.ProjectOnPlane(r.KneeSolved - Hip, axis).normalized;
            Vector3 want = Vector3.ProjectOnPlane(hint - Hip, axis).normalized;

            Assert.That(Vector3.Angle(pole, want), Is.LessThan(15f),
                $"the knee ended {Vector3.Angle(pole, want):F1} deg from the hint it was given -- the hint is being ignored");
        }
    }
}
