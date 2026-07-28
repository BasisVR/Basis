using NUnit.Framework;
using UnityEngine;
using Basis.IK;

namespace Basis.Tests.IK
{
    /// <summary>
    /// "I have a foot tracker and a knee hint, I push the foot forward, and the knee flips around" gate.
    ///
    /// A knee is a HINGE. It flexes one way -- heel toward the backside -- so the knee always bulges ANTERIOR to
    /// the hip->ankle axis. A knee behind that axis is not merely unnatural, it is anatomically unrepresentable.
    /// That makes it a hard constraint, not a tuning knob, and the guard is written to make the posterior
    /// half-space UNREACHABLE rather than merely unlikely.
    ///
    /// The knee ends up wherever the POLE points, because the solve's SWIVEL step rotates the knee ONTO the pole.
    /// So guarding the pole guards the knee, structurally. The two places a pole can be chosen both get the same
    /// bound, from the same shared clamp:
    ///   - BasisLegSolveCore places the knee on the pole.
    ///   - BasisSwivelSmootherCore can MOVE it afterwards, so it needs the bound too or a lagging filter drags the
    ///     knee back through the joint.
    ///
    /// Why the defect exists at all: the pole is `hint - hip` projected perpendicular to the hip->ankle axis. As
    /// the leg extends that lever arm collapses, so a few millimetres of tracker offset is enough to tip the
    /// projection across the axis and land the knee posterior -- the same pole singularity as the swivel snap
    /// (BasisKneeSwivelConditioningTests), seen from the other side. The BEND step cannot save it either: fed an
    /// over-reaching target it SATURATES at a straight leg rather than refusing, so the only place inversion can be
    /// stopped is at the pole.
    ///
    /// The gates, and the third is what stops this file rotting into a tautology:
    ///   1. The knee is NEVER posterior -- hint swept a full 360deg, at every reach including the singular ones.
    ///   2. Legitimate poses are UNTOUCHED (butterfly splay at its 60deg limit passes through bit-for-bit).
    ///   3. The sweep actually REACHES the inverting region, and the guard is what stops it -- proven by showing
    ///      the raw pole demands a posterior knee on those samples, and that the legacy (unguarded) smoother
    ///      really does invert. Without this, gates 1-2 could pass for entirely unrelated reasons.
    /// </summary>
    public sealed class BasisKneeInversionTests
    {
        const float k_ThighLen = 0.45f;
        const float k_ShinLen = 0.45f;
        const float k_FullReach = k_ThighLen + k_ShinLen;
        const float k_Dt = 1f / 90f;

        // Body frame: hips upright, forward = +Z, right = +X. BendNormal is hips-right, which is what the live rig
        // feeds the leg solve. The solve derives anterior from it as Cross(hip->ankle, BendNormal): with the leg
        // hanging down that is Cross(down, right) = forward. Exactly as the core's own comment claims.
        static readonly Vector3 k_BendNormal = Vector3.right;

        /// <summary>Anterior, as the solver derives it: perpendicular to the leg axis, in front of the leg.</summary>
        static Vector3 Anterior(Vector3 hip, Vector3 ankle)
        {
            Vector3 axis = (ankle - hip).normalized;
            return Vector3.ProjectOnPlane(Vector3.Cross(axis, k_BendNormal), axis).normalized;
        }

        /// <summary>
        /// How far the knee sits from anterior, in degrees, about the leg axis. This is THE quantity under test:
        /// |deg| &lt; 90 == the knee is in front of the leg == dot(kneeDir, anterior) &gt; 0. 180 == inverted.
        /// Returns NaN when the leg is so straight the knee has no measurable lever arm, i.e. when there is no
        /// direction to be wrong about -- the caller skips those rather than asserting on noise.
        /// </summary>
        static float KneeDegFromAnterior(Vector3 hip, Vector3 knee, Vector3 ankle)
        {
            Vector3 axis = (ankle - hip).normalized;
            Vector3 lever = Vector3.ProjectOnPlane(knee - hip, axis);
            if (lever.magnitude < 1e-4f)   // < 0.1 mm of lever: on the axis, no meaningful direction
            {
                return float.NaN;
            }
            return Vector3.SignedAngle(Anterior(hip, ankle), lever, axis);
        }

        /// <summary>
        /// A leg at `reach` of full extension with the ankle straight below the hip, plus a knee hint parked on the
        /// knee's own circle at `hintDeg` around the leg axis (0 = anterior).
        ///
        /// The hint sits at the knee's OWN lever radius on purpose -- that is where a real knee tracker is, and it
        /// is what makes the sweep bite: the solve only distrusts a hint once it closes on the leg axis
        /// (k_PoleColinearSin = sin 30deg), and a hint at the knee radius clears that threshold at bent reaches, so
        /// the pole is taken at face value. Park the hint further out and the existing colinearity blend would mask
        /// the defect; park it further in and the blend rescues it for unrelated reasons. Neither would test this.
        /// </summary>
        static BasisLegSolveInput MakeLeg(float reach, float hintDeg)
        {
            float d = reach * k_FullReach;
            Vector3 hip = Vector3.zero;
            Vector3 ankle = new Vector3(0f, -d, 0f);
            Vector3 axis = Vector3.down;

            float along = (k_ThighLen * k_ThighLen - k_ShinLen * k_ShinLen + d * d) / (2f * d);
            float lever = Mathf.Sqrt(Mathf.Max(0f, k_ThighLen * k_ThighLen - along * along));

            // Seed the FK pose with the knee anterior. The solve must be free to move it; a pose that already
            // agrees with the answer would let a broken guard pass by simply doing nothing.
            Vector3 knee = hip + axis * along + Vector3.forward * lever;

            // Hint on the knee's circle, swept about the leg axis.
            float hintRadius = Mathf.Max(lever, 0.05f);   // floor it so a dead-straight leg still has a hint to sweep
            Vector3 hintDir = Quaternion.AngleAxis(hintDeg, axis) * Vector3.forward;
            Vector3 hint = hip + axis * along + hintDir * hintRadius;

            return new BasisLegSolveInput
            {
                Root = hip,
                Mid = knee,
                Tip = ankle,
                RootRotation = Quaternion.identity,
                MidRotation = Quaternion.identity,
                TargetPosition = ankle,          // foot already on target: isolate the SWIVEL, which is what inverts
                TargetRotation = Quaternion.identity,
                TargetOffset = Quaternion.identity,
                HintPosition = hint,
                HintWeight = 1f,
                BendNormal = k_BendNormal,
            };
        }

        // Reaches spanning the whole range, deliberately INCLUDING the singular tail. A threshold you never cross
        // is a branch you never test -- the cervical clamp passed its own test for exactly that reason.
        static readonly float[] k_Reaches = { 0.50f, 0.70f, 0.85f, 0.95f, 0.99f, 0.999f, 0.9999f };

        // ── 1. THE INVARIANT ────────────────────────────────────────────────────────────────────────────

        [Test]
        public void Knee_IsNeverPosterior_HintSweptFullCircle_AtEveryReach()
        {
            foreach (float reach in k_Reaches)
            {
                for (int hintDeg = 0; hintDeg < 360; hintDeg += 3)
                {
                    BasisLegSolveInput input = MakeLeg(reach, hintDeg);
                    BasisLegSolveCore.Solve(input, out BasisLegSolveResult r);

                    float kneeDeg = KneeDegFromAnterior(input.Root, r.KneeSolved, r.FootSolved);
                    if (float.IsNaN(kneeDeg))
                    {
                        continue;   // leg dead straight: no lever, nothing to invert
                    }

                    Assert.Less(Mathf.Abs(kneeDeg), 90f,
                        $"knee went POSTERIOR (bent backwards through the joint) at reach {reach}, hint {hintDeg}deg: " +
                        $"knee sits {kneeDeg:F1}deg from anterior");
                }
            }
        }

        [Test]
        public void Knee_StaysOnTarget_WhileGuarded()
        {
            // The guard steers the POLE, which is a pure rotation about the hip->ankle axis -- and the ankle lies ON
            // that axis, so it cannot move. If a "fix" for inversion also pulled the foot off its tracker it would
            // be no fix at all: in FBT the foot is the one thing that must be exact.
            //
            // Measured perpendicular to the hip->target ray, not as raw distance: the MAX-EXTENSION cap
            // (BasisLegSolveCore.MaxKneeInteriorDeg) intentionally lets the foot fall a fraction of a mm short
            // RADIALLY just past full reach, which is a slide straight along the ray (zero perpendicular error).
            // The guard's tangential fidelity -- the thing this test owns -- is what the perpendicular metric isolates.
            foreach (float reach in k_Reaches)
            {
                for (int hintDeg = 0; hintDeg < 360; hintDeg += 15)
                {
                    BasisLegSolveInput input = MakeLeg(reach, hintDeg);
                    BasisLegSolveCore.Solve(input, out BasisLegSolveResult r);

                    Vector3 dir = input.TargetPosition - input.Root;
                    float perp = dir.sqrMagnitude < 1e-12f ? 0f
                        : Vector3.ProjectOnPlane(r.FootSolved - input.Root, dir.normalized).magnitude;
                    Assert.Less(perp, 1e-4f,
                        $"foot slid {perp * 1000f:F2} mm sideways off its target ray at reach {reach}, hint {hintDeg}deg");
                }
            }
        }

        // ── 2. LEGITIMATE POSES ARE UNTOUCHED ───────────────────────────────────────────────────────────

        [Test]
        public void Guard_IsIdentity_ForEveryLegitimatePose()
        {
            // Hip external rotation tops out ~45-50deg and the butterfly/cobbler splay is hard-clamped to
            // DefaultMaxOpenDeg. If the guard so much as touched those, it would be trading one artefact for
            // another -- so assert BIT-FOR-BIT identity, not "close enough".
            float butterfly = BasisButterflyKneeCore.DefaultMaxOpenDeg;
            Assert.Less(butterfly, BasisLegSolveCore.KneeAnteriorSoftDeg,
                "butterfly splay must sit strictly inside the guard's free band, or a legal pose gets compressed");

            float[] legal = { 0f, 15f, -15f, 45f, -45f, butterfly, -butterfly, BasisLegSolveCore.KneeAnteriorSoftDeg };
            foreach (float deg in legal)
            {
                float outDeg = BasisLegSolveCore.ClampKneeSwivelDeg(
                    deg, BasisLegSolveCore.KneeAnteriorSoftDeg, BasisLegSolveCore.KneeAnteriorHardDeg);

                Assert.AreEqual(deg, outDeg, 0f, $"guard perturbed a legitimate {deg}deg pose");
            }
        }

        [Test]
        public void Guard_BarelyTouches_ADeadLateralKnee()
        {
            // 90deg is NOT an inversion -- it is a knee pointing dead lateral, which is legal, and the codebase
            // already contracts to honour it: BasisLegHintReachTests.KneeHint_StillActuallySwivelsTheKnee parks the
            // hint at exactly +X (90deg off anterior) and demands the knee follow to within 15deg.
            //
            // This gate is the reason KneeAnteriorSoftDeg is 85 and not something tidier and lower. A guard that
            // bites early would bend that legal pose by double digits to fix an illegal one, quietly eating the
            // other test's entire tolerance budget. Pin the perturbation so nobody "tightens" the guard and breaks
            // a contract two files away without hearing about it here first.
            float outDeg = BasisLegSolveCore.ClampKneeSwivelDeg(
                90f, BasisLegSolveCore.KneeAnteriorSoftDeg, BasisLegSolveCore.KneeAnteriorHardDeg);

            float perturbation = 90f - outDeg;
            Assert.Greater(perturbation, 0f, "a dead-lateral knee must still be pulled inside the half-space");
            Assert.Less(perturbation, 5f,
                $"guard bent a LEGAL lateral knee by {perturbation:F1}deg -- BasisLegHintReachTests only allows 15deg " +
                "of total error and it needs that budget for the solve, not for us");
        }

        [Test]
        public void Guard_Saturates_StrictlyInsideTheHalfSpace()
        {
            // The asymptote is what makes dot(kneeDir, anterior) > 0 hold by CONSTRUCTION rather than by a
            // tolerance: the limit is never reached, so it can never be crossed. Push it well past 180 to prove the
            // bound is not merely "big enough for the inputs I happened to try".
            float[] beyond = { 71f, 90f, 120f, 179f, 180f, 250f, 3600f };
            foreach (float deg in beyond)
            {
                float outDeg = BasisLegSolveCore.ClampKneeSwivelDeg(
                    deg, BasisLegSolveCore.KneeAnteriorSoftDeg, BasisLegSolveCore.KneeAnteriorHardDeg);

                Assert.Less(Mathf.Abs(outDeg), BasisLegSolveCore.KneeAnteriorHardDeg, $"guard failed to bound {deg}deg");
                Assert.Less(Mathf.Abs(outDeg), 90f, $"{deg}deg escaped the anterior half-space");

                // Side is checked against the WRAPPED input, because the guard's argument is a DIRECTION and not a
                // distance along a line: 250deg and -110deg are the same place, so the correct output for 250 is
                // negative. (This assert used to compare against Mathf.Sign(deg) unwrapped, which quietly required
                // the guard to be a function on the LINE -- one of the two things that pinned the +-180 branch cut
                // in place. See ClampKneeSwivelDeg. Every live caller passes a SignedAngle, already in range.)
                float wrapped = deg - 360f * Mathf.Floor((deg + 180f) / 360f);
                if (Mathf.Abs(wrapped) > 1e-3f && Mathf.Abs(Mathf.Abs(wrapped) - 180f) > 1e-3f)
                {
                    Assert.AreEqual(Mathf.Sign(wrapped), Mathf.Sign(outDeg), $"guard flipped the side of a {deg}deg swivel");
                }
            }
        }

        [Test]
        public void Guard_IsContinuous_AllTheWayAroundTheCircle()
        {
            // A guard that snaps trades a flip for a jerk. The clamp is built with slope exactly 1 where it takes
            // over, so there is no derivative step at the boundary -- walk across it in fine increments and prove no
            // sample jumps.
            //
            // ⚠️ THIS TEST USED TO ASSERT MONOTONICITY, AND THAT ASSERTION WAS ITSELF THE BUG.
            //
            // The guard's argument is an angle on a CIRCLE, so -180 and +180 are the same knee direction and the
            // sweep has to close. Mirror symmetry makes the guard odd, so f(-180) = -f(180); closing the circle
            // makes f(-180) = f(180); together those force f(180) = 0. A guard that is ALSO monotone on [0,180]
            // would then have to be identically zero there -- so it could not be the identity below the soft edge.
            // Monotone-on-the-line and continuous-on-the-circle are therefore INCOMPATIBLE, and requiring the first
            // is what forced clamp(+179.9) = +89.3 against clamp(-179.9) = -89.3: a 178.6deg output flip for a
            // fifth of a degree of real motion. Measured 714x knee gain in the solve core and 698x through the
            // smoother, and it is the "knees click rotationally" report.
            //
            // So assert what the comment above always MEANT -- no sample jumps -- and assert it the whole way
            // round, which is the property that actually shows up in a headset.
            const float step = 0.1f;
            const float maxStep = 1.0f;    // slope <= 1 rising; ~6.7 falling inside the narrow taper window

            float prev = BasisLegSolveCore.ClampKneeSwivelDeg(-180f, BasisLegSolveCore.KneeAnteriorSoftDeg, BasisLegSolveCore.KneeAnteriorHardDeg);
            for (float deg = -180f + step; deg <= 180f + step; deg += step)
            {
                float sample = deg > 180f ? deg - 360f : deg;   // wrap past the top so the circle closes
                float cur = BasisLegSolveCore.ClampKneeSwivelDeg(
                    sample, BasisLegSolveCore.KneeAnteriorSoftDeg, BasisLegSolveCore.KneeAnteriorHardDeg);

                Assert.Less(Mathf.Abs(cur - prev), maxStep,
                    $"guard jumped at {sample:F1}deg ({prev:F3} -> {cur:F3}) -- that is a visible pop");
                prev = cur;
            }

            // And the join itself, stated directly rather than left to the sweep's step size.
            float atPlus = BasisLegSolveCore.ClampKneeSwivelDeg(179.9f, BasisLegSolveCore.KneeAnteriorSoftDeg, BasisLegSolveCore.KneeAnteriorHardDeg);
            float atMinus = BasisLegSolveCore.ClampKneeSwivelDeg(-179.9f, BasisLegSolveCore.KneeAnteriorSoftDeg, BasisLegSolveCore.KneeAnteriorHardDeg);
            Assert.Less(Mathf.Abs(atPlus - atMinus), 1f,
                $"the guard is discontinuous across the posterior ray: clamp(+179.9) = {atPlus:F2}, " +
                $"clamp(-179.9) = {atMinus:F2}. Those are the same knee direction, so that difference is a click.");
        }

        [Test]
        public void Guard_SendsNaN_ToAnterior_NotThroughTheJoint()
        {
            // NaN fails every ordered comparison, so a guard shaped `if (bad < limit) reject` declares NaN valid and
            // feeds it to the bone -- and a NaN'd transform PERSISTS in Unity: the leg dies and never recovers. This
            // codebase has been bitten by that exact shape three separate times. Reject-unless-good, always.
            float outDeg = BasisLegSolveCore.ClampKneeSwivelDeg(
                float.NaN, BasisLegSolveCore.KneeAnteriorSoftDeg, BasisLegSolveCore.KneeAnteriorHardDeg);

            Assert.IsFalse(float.IsNaN(outDeg), "NaN swivel passed straight through the guard");
            Assert.AreEqual(0f, outDeg, 1e-6f, "a NaN swivel must fall back to anterior");
        }

        // ── 3. ANTI-TAUTOLOGY: the sweep really does reach the inverting region ──────────────────────────

        [Test]
        public void Sweep_ActuallyDemandsAnInvertedKnee_AndTheGuardIsWhatStopsIt()
        {
            // Without this, gate 1 could be passing because the sweep never asked for an inversion in the first
            // place. Prove the input genuinely demands a posterior knee -- the raw pole (what the solve would follow
            // unguarded) points behind the leg -- and that the guard is what fired.
            int demandedInversion = 0;
            int guardFired = 0;

            foreach (float reach in k_Reaches)
            {
                for (int hintDeg = 0; hintDeg < 360; hintDeg += 3)
                {
                    BasisLegSolveInput input = MakeLeg(reach, hintDeg);

                    Vector3 axis = (input.Tip - input.Root).normalized;
                    Vector3 rawPole = Vector3.ProjectOnPlane(input.HintPosition - input.Root, axis);
                    if (rawPole.magnitude < 1e-4f)
                    {
                        continue;
                    }

                    float rawDeg = Vector3.SignedAngle(Anterior(input.Root, input.Tip), rawPole, axis);
                    if (Mathf.Abs(rawDeg) <= 90f)
                    {
                        continue;   // this hint was not asking for an inversion
                    }

                    demandedInversion++;

                    BasisLegSolveCore.Solve(input, out BasisLegSolveResult r);
                    if (r.AxisSource == 5)
                    {
                        guardFired++;
                    }
                }
            }

            Assert.Greater(demandedInversion, 100,
                "the sweep never asked for a posterior knee -- gate 1 is vacuous and proves nothing");
            Assert.Greater(guardFired, 0, "the anterior guard never fired on an inverting hint");
        }

        [Test]
        public void LegacySmoother_Inverts_WhereTheGuardedOneDoesNot()
        {
            // The smoother's own anti-tautology. It runs AFTER the solve and rebuilds the knee at the smoothed
            // swivel, so it can put the knee anywhere it likes -- guarded, it cannot. Prove the guard is load
            // bearing here too, so a revert fails loudly with a reason rather than the suite quietly going green.
            const float posteriorDeg = 170f;   // a hint demanding a near-fully-inverted knee

            BasisSwivelSmootherInput legacy = MakeSmoother(0.85f, posteriorDeg, guard: false);
            BasisSwivelSmootherCore.Solve(legacy, out BasisSwivelSmootherResult legacyR);

            BasisSwivelSmootherInput guarded = MakeSmoother(0.85f, posteriorDeg, guard: true);
            BasisSwivelSmootherCore.Solve(guarded, out BasisSwivelSmootherResult guardedR);

            // Seed frames both establish state from the (guarded or raw) swivel.
            Assert.Greater(Mathf.Abs(legacyR.SmoothSwivelDeg), 90f,
                "legacy smoother did NOT invert -- this gate is not measuring the real defect");
            Assert.Less(Mathf.Abs(guardedR.SmoothSwivelDeg), 90f,
                "guarded smoother let the knee through the joint");
            Assert.IsTrue(guardedR.AnteriorGuardApplied, "guard did not fire on an inverting swivel");
            Assert.IsFalse(legacyR.AnteriorGuardApplied, "legacy path must not report the guard");
        }

        [Test]
        public void GuardedSmoother_NeverLetsTheKneeBehindTheLeg_UnderAFullSweep()
        {
            // Drive the filter across a full revolution of the hint, carrying state, so this covers the settling
            // behaviour and not just single frames. A low-pass of values inside the bound stays inside it -- that is
            // WHY the guard is applied to the filter's INPUT and not merely its output.
            BasisSwivelSmootherInput seed = MakeSmoother(0.85f, 0f, guard: true);
            BasisSwivelSmootherCore.Solve(seed, out BasisSwivelSmootherResult seeded);
            BasisSwivelFilterState state = seeded.State;

            for (int hintDeg = 0; hintDeg < 720; hintDeg += 5)
            {
                BasisSwivelSmootherInput step = MakeSmoother(0.85f, hintDeg % 360, guard: true);
                step.State = state;
                step.Seeded = true;

                BasisSwivelSmootherCore.Solve(step, out BasisSwivelSmootherResult r);
                state = r.State;

                Assert.Less(Mathf.Abs(r.SmoothSwivelDeg), 90f,
                    $"smoothed knee went posterior at hint {hintDeg}deg ({r.SmoothSwivelDeg:F1}deg from anterior)");
            }
        }

        [Test]
        public void ArmPath_IsUntouched_WhenTheGuardIsOff()
        {
            // The elbow has the same inversion problem, but its reference is body-DOWN and an elbow points BACK, not
            // forward -- a forward-anterior half-space would clamp it into the wrong hemisphere entirely. So the arm
            // deliberately keeps the legacy path, and that must be exactly, bit-for-bit legacy.
            BasisSwivelSmootherInput off = MakeSmoother(0.85f, 170f, guard: false);
            BasisSwivelSmootherCore.Solve(off, out BasisSwivelSmootherResult r);

            Assert.IsFalse(r.AnteriorGuardApplied, "guard leaked into a path that opted out");
            Assert.AreEqual(r.RawSwivelDeg, r.SmoothSwivelDeg, 1e-3f,
                "seed frame must pass the raw swivel through untouched when the guard is off");
        }

        /// <summary>Smoother input for a leg at `reach`, knee parked `swivelDeg` around the axis from anterior.</summary>
        static BasisSwivelSmootherInput MakeSmoother(float reach, float swivelDeg, bool guard)
        {
            float d = reach * k_FullReach;
            Vector3 root = Vector3.zero;
            Vector3 tip = new Vector3(0f, -d, 0f);
            Vector3 axis = Vector3.down;

            float along = (k_ThighLen * k_ThighLen - k_ShinLen * k_ShinLen + d * d) / (2f * d);
            float lever = Mathf.Sqrt(Mathf.Max(0f, k_ThighLen * k_ThighLen - along * along));
            Vector3 perp = Quaternion.AngleAxis(swivelDeg, axis) * Vector3.forward;

            return new BasisSwivelSmootherInput
            {
                Root = root,
                Mid = root + axis * along + perp * lever,
                Tip = tip,
                BodyRotation = Quaternion.identity,
                ReferenceLocal = Vector3.forward,
                FallbackLocal = Vector3.right,
                Dt = k_Dt,
                MinCutoffHz = 1.5f,
                Beta = 0.20f,
                DerivCutoffHz = 1.0f,
                ConditionOnPole = true,
                SingularMinCutoffHz = BasisSwivelFilterCore.MinCutoffHz,
                GuardAnteriorHalfSpace = guard,
                AnteriorSoftDeg = BasisLegSolveCore.KneeAnteriorSoftDeg,
                AnteriorHardDeg = BasisLegSolveCore.KneeAnteriorHardDeg,
            };
        }
    }
}
