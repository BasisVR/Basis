using NUnit.Framework;
using UnityEngine;
using Basis.IK;

namespace Basis.Tests.IK
{
    /// <summary>
    /// "The leg is very snappy when I move the lower-leg hint" gate.
    ///
    /// A two-bone knee's bend PLANE is set by the pole, but a STRAIGHT leg has no bend plane: the knee sits on
    /// the hip->foot axis and the swivel angle about that axis is undefined. `footHeightOffset` is deliberately
    /// clamped so "the legs fully extend when standing", which parks a standing leg at hip->foot ~= thigh+shin --
    /// i.e. permanently ON that singularity. There the raw swivel is noise thrashing through large angles.
    ///
    /// The trap: <see cref="BasisSwivelFilterCore"/> is a One-Euro, and a One-Euro is SPEED-ADAPTIVE --
    /// `cutoff = minCutoff + beta * |velocity|`. Its premise is "a fast signal is a signal the user MEANT, so
    /// stop filtering it". At a singularity that premise inverts: the noise has a huge velocity, the filter reads
    /// it as intent, opens the cutoff wide, and passes the garbage through unfiltered. A speed-adaptive filter is
    /// ACTIVELY HARMFUL at a singularity -- it amplifies exactly what it was added to damp.
    ///
    /// The fix conditions the filter's responsiveness on the pole's lever arm
    /// (<see cref="BasisSwivelSmootherResult.Conditioning"/> = sin of the angle between the upper bone and the
    /// hip->foot axis; 0 = singular, 1 = fully folded), so beta -> 0 as the leg straightens and the cutoff stops
    /// opening on noise, while a genuinely bent knee keeps full responsiveness.
    ///
    /// Three gates, and the third is the one that stops this file rotting into a tautology:
    ///   1. STRAIGHT is damped.
    ///   2. BENT is responsive (so the fix did not simply glue the knee in place -- deliberate shin motion still tracks).
    ///   3. LEGACY (ConditionOnPole off) is SNAPPY while straight -- proving the guard measures the real defect
    ///      and that a revert fails loudly, rather than the first two gates passing for some unrelated reason.
    /// </summary>
    public sealed class BasisKneeSwivelConditioningTests
    {
        const float k_Dt = 1f / 90f;
        const float k_ThighLen = 0.45f;
        const float k_ShinLen = 0.45f;
        const float k_StepDeg = 60f;   // the hint jumps: a step change is the worst case for a One-Euro

        // The live tracked-knee cutoffs from BasisFullIKConstraintJob. Mirrored deliberately -- if someone retunes
        // them there and not here, this gate should be re-derived, not silently drift.
        const float k_TrackedMinCutoffHz = 1.5f;
        const float k_TrackedBeta = 0.20f;
        const float k_TrackedDerivCutoffHz = 1.0f;

        /// <summary>
        /// Builds a leg whose hip->foot distance is `reach` of full extension, with the knee swivelled
        /// `swivelDeg` about the hip->foot axis away from the body-forward reference.
        ///
        /// Frame: hip at origin, foot straight below it, so the axis is world-down and the reference direction
        /// (body forward, identity body rotation) is world-forward and lies cleanly in the swivel plane.
        /// For thigh == shin this gives conditioning == sqrt(1 - reach^2) analytically, which is what makes the
        /// reach values below map onto known conditioning numbers.
        /// </summary>
        static BasisSwivelSmootherInput MakeLeg(float reach, float swivelDeg, bool conditionOnPole)
        {
            float full = k_ThighLen + k_ShinLen;
            float d = reach * full;

            Vector3 root = Vector3.zero;
            Vector3 tip = new Vector3(0f, -d, 0f);
            Vector3 axis = Vector3.down;

            // Standard two-bone knee placement: distance along the axis, then the perpendicular lever arm.
            float along = (k_ThighLen * k_ThighLen - k_ShinLen * k_ShinLen + d * d) / (2f * d);
            float lever = Mathf.Sqrt(Mathf.Max(0f, k_ThighLen * k_ThighLen - along * along));

            Vector3 refDir = Vector3.forward;
            Vector3 perp = Quaternion.AngleAxis(swivelDeg, axis) * refDir;
            Vector3 mid = root + axis * along + perp * lever;

            return new BasisSwivelSmootherInput
            {
                Root = root,
                Mid = mid,
                Tip = tip,
                BodyRotation = Quaternion.identity,
                ReferenceLocal = Vector3.forward,
                FallbackLocal = Vector3.right,
                Dt = k_Dt,
                MinCutoffHz = k_TrackedMinCutoffHz,
                Beta = k_TrackedBeta,
                DerivCutoffHz = k_TrackedDerivCutoffHz,
                ConditionOnPole = conditionOnPole,
                SingularMinCutoffHz = BasisSwivelFilterCore.MinCutoffHz,
            };
        }

        /// <summary>
        /// Seeds at swivel 0, then holds the hint at a step-changed swivel for `frames` and returns how much of
        /// that step the SMOOTHED swivel actually covered, 0..1. This is exactly what "snappy" means: 1 == the
        /// knee teleported to the new pole in no time at all.
        /// </summary>
        static float StepResponse(float reach, bool conditionOnPole, int frames)
        {
            BasisSwivelSmootherInput seed = MakeLeg(reach, 0f, conditionOnPole);
            BasisSwivelSmootherCore.Solve(seed, out BasisSwivelSmootherResult seeded);
            Assert.IsTrue(seeded.Seeded, "seed frame must establish filter state");

            BasisSwivelFilterState state = seeded.State;
            float raw = 0f;
            float smooth = 0f;

            for (int f = 0; f < frames; f++)
            {
                BasisSwivelSmootherInput step = MakeLeg(reach, k_StepDeg, conditionOnPole);
                step.State = state;
                step.Seeded = true;

                BasisSwivelSmootherCore.Solve(step, out BasisSwivelSmootherResult r);
                Assert.IsTrue(r.WriteState, "post-seed frames must advance the filter");

                state = r.State;
                raw = r.RawSwivelDeg;
                smooth = r.SmoothSwivelDeg;
            }

            // Fraction of the raw step that the smoothed output has travelled. Guard the denominator so a broken
            // geometry build cannot silently divide by ~0 and hand back a meaningless ratio.
            Assert.Greater(Mathf.Abs(raw), 1f, "the step change must actually register in the raw swivel");
            return Mathf.Clamp01(Mathf.Abs(smooth) / Mathf.Abs(raw));
        }

        [Test]
        public void Conditioning_IsZeroAtFullExtension_AndOneWhenFolded()
        {
            // The property the whole fix rests on. Cross the range rather than sampling one comfortable point --
            // a threshold you never cross is a branch you never test.
            BasisSwivelSmootherCore.Solve(MakeLeg(0.9999f, 30f, true), out BasisSwivelSmootherResult straight);
            BasisSwivelSmootherCore.Solve(MakeLeg(0.85f, 30f, true), out BasisSwivelSmootherResult bent);
            BasisSwivelSmootherCore.Solve(MakeLeg(0.05f, 30f, true), out BasisSwivelSmootherResult folded);

            Assert.Less(straight.Conditioning, 0.05f,
                $"a straight leg sits on the pole singularity; conditioning should collapse (got {straight.Conditioning:F4})");
            Assert.That(bent.Conditioning, Is.EqualTo(Mathf.Sqrt(1f - 0.85f * 0.85f)).Within(0.02f),
                "for thigh == shin, conditioning is exactly sqrt(1 - reach^2)");
            Assert.Greater(folded.Conditioning, 0.9f,
                $"a folded knee has a full lever arm and a well-defined bend plane (got {folded.Conditioning:F4})");
        }

        [Test]
        public void StepChange_IsDamped_WhenLegIsStraight()
        {
            // reach 0.999 == a standing leg, which is where the user actually is when the knee snaps.
            float response = StepResponse(0.999f, conditionOnPole: true, frames: 1);

            Assert.Less(response, 0.40f,
                $"a hint step at full extension must NOT be tracked -- the swivel there is noise, not signal " +
                $"(covered {response:P0} of the step in one frame)");
        }

        [Test]
        public void StepChange_StaysResponsive_WhenKneeIsBent()
        {
            // The fix must not simply glue the knee. At a genuinely bent knee the hint is real information and
            // deliberate shin motion has to track, or we have traded a snap for a lag.
            float response = StepResponse(0.85f, conditionOnPole: true, frames: 1);

            Assert.Greater(response, 0.55f,
                $"a bent knee has a well-conditioned bend plane; the hint is real and must be tracked " +
                $"(covered only {response:P0} of the step in one frame)");
        }

        [Test]
        public void BentKnee_IsStrictlyMoreResponsiveThanStraightLeg()
        {
            // The monotonic property, stated directly: responsiveness must rise with conditioning.
            float straight = StepResponse(0.999f, conditionOnPole: true, frames: 1);
            float bent = StepResponse(0.85f, conditionOnPole: true, frames: 1);

            Assert.Greater(bent, straight + 0.25f,
                $"responsiveness must scale with how much the pole is worth (straight {straight:P0}, bent {bent:P0})");
        }

        [Test]
        public void Legacy_Unconditioned_SnapsAtFullExtension()
        {
            // THE ANTI-TAUTOLOGY GATE. Without conditioning, a One-Euro reads the singularity's noise velocity as
            // intent, throws its cutoff wide open (beta 0.20 * ~353 deg/s => ~72 Hz), and passes the garbage
            // straight through. That IS the snap. If this ever stops failing-loudly on a revert, the gates above
            // have stopped measuring anything.
            float legacy = StepResponse(0.999f, conditionOnPole: false, frames: 1);
            float conditioned = StepResponse(0.999f, conditionOnPole: true, frames: 1);

            Assert.Greater(legacy, 0.70f,
                $"the legacy filter is expected to snap at full extension -- that is the defect (got {legacy:P0}). " +
                "If this now passes, the One-Euro was retuned and the conditioning gates must be re-derived.");
            Assert.Less(conditioned, legacy - 0.35f,
                $"conditioning must materially damp the snap (legacy {legacy:P0} vs conditioned {conditioned:P0})");
        }

        [Test]
        public void ElbowPath_IsBitwiseUnchanged_WhenConditioningIsOff()
        {
            // The OFF path must reproduce the raw One-Euro exactly. The arm has since opted in
            // (k_ConditionElbowSwivelOnPole), so this no longer guards the live elbow -- it guards the REVERT: flip
            // that switch back and the elbow returns to the legacy filter bit-for-bit, with no residue left behind.
            // Arm tweaks have been reverted in this repo before, so the way back has to be provably exact.
            BasisSwivelSmootherInput a = MakeLeg(0.95f, 0f, conditionOnPole: false);
            BasisSwivelSmootherCore.Solve(a, out BasisSwivelSmootherResult seeded);

            BasisSwivelSmootherInput b = MakeLeg(0.95f, k_StepDeg, conditionOnPole: false);
            b.State = seeded.State;
            b.Seeded = true;
            BasisSwivelSmootherCore.Solve(b, out BasisSwivelSmootherResult off);

            BasisSwivelFilterState expected = BasisSwivelFilterCore.Step(
                seeded.State, off.RawSwivelDeg, k_Dt, k_TrackedMinCutoffHz, k_TrackedBeta, k_TrackedDerivCutoffHz);

            Assert.That(off.SmoothSwivelDeg, Is.EqualTo(expected.Smooth).Within(1e-5f),
                "with ConditionOnPole off the core must reproduce the raw One-Euro exactly");
        }
    }
}
