using NUnit.Framework;
using UnityEngine;
using Basis.IK;

namespace Basis.Tests.IK
{
    /// <summary>
    /// The knee half-space guard's ANTERIOR reference must be BODY-frame, never limb-frame.
    ///
    /// THE DEFECT. <see cref="BasisLegSolveInput.BendNormal"/> did double duty: the no-hint fallback pole AND the
    /// anterior reference for the guard. With FBIKTrackerBendNormal (default ON) the rig driver derives it from
    /// the lower-leg tracker's live rotation, so "anterior" rode the user's SHIN. Tibial rotation alone then
    /// swept the guard's bite point across a stationary knee hint. Measured on the shipping solver, hint parked
    /// at a legal 70 deg and never moved: the guard engaged at 20 deg of tibial rotation and dragged the knee
    /// from 70.00 to 29.09 deg by 60 deg -- 41 deg of knee travel from an input that did not move.
    ///
    /// The user's report was "the hint role for legs is slow to respond and goes across a snapping point": once
    /// inside the guard's band the gain collapses (1.00 -> 0.47 at 90 deg, 0.04 by 105 deg, 0.00 by 150 deg) and
    /// at 180 deg the pole is antiparallel and the sign flips, a 178.6 deg step.
    ///
    /// The compression itself is CORRECT and deliberate -- KneeAnteriorHardDeg is strictly under 90 so the
    /// posterior half-space is unreachable, and a circle cannot be mapped continuously into an open arc while
    /// preserving which side the knee is on. So the fix is not to widen or smooth the guard: it is to stop the
    /// reference moving. The tracker-derived plane stays exactly right for the POLE.
    /// </summary>
    public class BasisKneeAnteriorReferenceTests
    {
        const float Thigh = 0.42f, Shin = 0.42f;
        static readonly Vector3 Hip = new Vector3(-0.09f, 0.92f, 0f);

        static BasisLegSolveInput Leg(Vector3 foot, Vector3 hint, Vector3 bendNormal, Vector3 anterior)
        {
            BasisLegSolveInput i = default;
            i.Root = Hip;
            i.Mid = Hip + new Vector3(0f, -Thigh, 0.02f);
            i.Tip = Hip + new Vector3(0f, -Thigh - Shin, 0f);
            i.RootRotation = Quaternion.identity;
            i.MidRotation = Quaternion.identity;
            i.TargetPosition = foot;
            i.TargetRotation = Quaternion.identity;
            i.TargetOffset = Quaternion.identity;
            i.HintPosition = hint;
            i.HintWeight = 1f;
            i.HintIsTracker = true;
            i.BendNormal = bendNormal;
            i.AnteriorNormal = anterior;
            return i;
        }

        /// <summary>
        /// Anterior for the leg's own frame. Building this from Cross(axis, up) is DEGENERATE for a near-vertical
        /// leg and silently places azimuth 0 behind the knee, which parks a whole sweep in the posterior
        /// half-space and makes the guard appear to fire everywhere. That mistake produced two confident, wrong
        /// findings while this was being diagnosed.
        /// </summary>
        static void LegFrame(Vector3 axis, out Vector3 anterior, out Vector3 lateral)
        {
            anterior = Vector3.ProjectOnPlane(Vector3.forward, axis);
            if (anterior.sqrMagnitude < 1e-8f) anterior = Vector3.ProjectOnPlane(Vector3.up, axis);
            anterior = anterior.normalized;
            lateral = Vector3.Cross(axis, anterior).normalized;
        }

        static float Az(Vector3 v, Vector3 axis, Vector3 refDir)
        {
            Vector3 p = Vector3.ProjectOnPlane(v, axis);
            if (p.sqrMagnitude < 1e-12f) return 0f;
            Vector3 r = Vector3.ProjectOnPlane(refDir, axis).normalized;
            Vector3 t = Vector3.Cross(axis, r);
            return Mathf.Atan2(Vector3.Dot(p, t), Vector3.Dot(p, r)) * Mathf.Rad2Deg;
        }

        static void Fixture(out Vector3 foot, out Vector3 axis, out Vector3 ant, out Vector3 hint)
        {
            const float reach = 0.90f;
            float legLen = Thigh + Shin;
            foot = new Vector3(Hip.x, Hip.y - legLen * reach, 0.02f);
            axis = (foot - Hip).normalized;
            LegFrame(axis, out ant, out Vector3 lat);

            const float hintAz = 70f;   // legal: well inside the 85 deg free band
            Vector3 dir = Mathf.Cos(hintAz * Mathf.Deg2Rad) * ant + Mathf.Sin(hintAz * Mathf.Deg2Rad) * lat;
            hint = Hip + axis * (legLen * reach * 0.5f) + dir * 0.26f;
        }

        [Test]
        public void TibialRotation_DoesNotMoveAStationaryKnee([Values(-60f, -40f, -20f, 20f, 40f, 60f)] float tibialDeg)
        {
            Fixture(out Vector3 foot, out Vector3 axis, out Vector3 ant, out Vector3 hint);

            BasisLegSolveCore.Solve(Leg(foot, hint, Vector3.right, Vector3.right), out BasisLegSolveResult base_);
            float baseAz = Az(base_.KneeSolved - Hip, axis, ant);

            // The pole still rides the shin tracker, as BasisTrackerBendNormalCore intends...
            Vector3 rolledNormal = Quaternion.AngleAxis(tibialDeg, axis) * Vector3.right;
            // ...but anterior stays body-frame.
            BasisLegSolveCore.Solve(Leg(foot, hint, rolledNormal, Vector3.right), out BasisLegSolveResult r);
            float az = Az(r.KneeSolved - Hip, axis, ant);

            Assert.That(r.AxisSource, Is.Not.EqualTo(5),
                $"{tibialDeg:F0} deg of tibial rotation must not push a legal knee into the anterior guard");
            Assert.That(az, Is.EqualTo(baseAz).Within(0.05f),
                $"the knee moved {Mathf.Abs(az - baseAz):F2} deg on a hint that never moved");
        }

        /// <summary>Without a body-frame reference the defect is present — this is the anti-tautology check.</summary>
        [Test]
        public void WithoutTheBodyFrameReference_TheDefectIsPresent()
        {
            Fixture(out Vector3 foot, out Vector3 axis, out Vector3 ant, out Vector3 hint);

            BasisLegSolveCore.Solve(Leg(foot, hint, Vector3.right, Vector3.right), out BasisLegSolveResult base_);
            float baseAz = Az(base_.KneeSolved - Hip, axis, ant);

            Vector3 rolledNormal = Quaternion.AngleAxis(-60f, axis) * Vector3.right;
            // AnteriorNormal zero == the legacy behaviour: the guard falls back to BendNormal.
            BasisLegSolveCore.Solve(Leg(foot, hint, rolledNormal, Vector3.zero), out BasisLegSolveResult legacy);
            float legacyAz = Az(legacy.KneeSolved - Hip, axis, ant);

            Assert.That(Mathf.Abs(legacyAz - baseAz), Is.GreaterThan(20f),
                "if this stops reproducing, the fix above is no longer being tested by anything");
        }

        /// <summary>A zero AnteriorNormal must leave every pre-existing caller bit-identical.</summary>
        [Test]
        public void ZeroAnteriorNormal_FallsBackToBendNormal()
        {
            Fixture(out Vector3 foot, out _, out _, out Vector3 hint);

            BasisLegSolveCore.Solve(Leg(foot, hint, Vector3.right, Vector3.zero), out BasisLegSolveResult fallback);
            BasisLegSolveCore.Solve(Leg(foot, hint, Vector3.right, Vector3.right), out BasisLegSolveResult explicit_);

            Assert.That(Vector3.Distance(fallback.KneeSolved, explicit_.KneeSolved), Is.EqualTo(0f).Within(1e-6f));
            Assert.That(fallback.AxisSource, Is.EqualTo(explicit_.AxisSource));
        }

        /// <summary>
        /// The guard is still a hard constraint: a genuinely posterior hint must still be pulled anterior. The
        /// body-frame reference changes WHERE anterior is, never WHETHER the guard applies.
        /// </summary>
        [Test]
        public void AGenuinelyPosteriorHint_IsStillGuarded()
        {
            Fixture(out Vector3 foot, out Vector3 axis, out Vector3 ant, out _);
            LegFrame(axis, out _, out Vector3 lat);

            float legLen = Thigh + Shin;
            Vector3 dir = Mathf.Cos(160f * Mathf.Deg2Rad) * ant + Mathf.Sin(160f * Mathf.Deg2Rad) * lat;
            Vector3 posteriorHint = Hip + axis * (legLen * 0.45f) + dir * 0.26f;

            BasisLegSolveCore.Solve(Leg(foot, posteriorHint, Vector3.right, Vector3.right), out BasisLegSolveResult r);
            float az = Az(r.KneeSolved - Hip, axis, ant);

            Assert.That(r.AxisSource, Is.EqualTo(5), "a 160 deg hint is posterior and the guard must engage");
            Assert.That(Mathf.Abs(az), Is.LessThan(BasisLegSolveCore.KneeAnteriorHardDeg),
                "the knee must stay strictly inside the anterior half-space");
        }
    }
}
