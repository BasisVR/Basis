using System;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using Basis.IK;

namespace Basis.Tests.IK
{
    /// <summary>
    /// THE OTHER HALF OF THE ELBOW: a REAL, STRAPPED-ON ELBOW TRACKER.
    ///
    /// ================================================================================================
    /// BasisArmHintAuthorityTests proves the CONSTRUCTED hint (the swivel model) reaches the bone at every
    /// extension. It drives HintIsTracker = false. NOTHING drove HintIsTracker = true through the extension
    /// regime where the arm's authority used to collapse -- so the path a user with a real elbow tracker
    /// actually takes was untested. This file is that path.
    ///
    /// A TRACKER IS NOT AN IDEALISED POLE, AND TESTING IT AS ONE PROVES NOTHING:
    ///
    ///   * IT IS NOT ON THE BONE. A puck is strapped to the OUTSIDE of the limb -- it stands off the joint
    ///     centre by the thickness of arm plus strap plus puck. That stand-off is fixed IN THE LIMB'S FRAME:
    ///     it rides the arm's roll, which is exactly why it carries the swivel signal at all.
    ///
    ///   * IT IS MEASURED, SO IT IS NOISY. Millimetres of tracker noise, and the occasional larger glitch.
    ///
    ///   * AND THE LIVE RIG DOES NOT HAND THE SOLVER THE PUCK. BasisLocalRigDriver writes
    ///     `data.LeftLowerArmPosition = pOut[S_LeftLowerArm]` -- the CALIBRATED LOWER-ARM BONE, with
    ///     ApplyHintBias deliberately NOT applied ("Elbow-tracker conditioning is handled solver-side").
    ///     So a well-calibrated tracker's hint sits ON THE JOINT CENTRE, stand-off ~= 0, and its pole
    ///     COLLAPSES with the arm exactly like the elbow's own does. A test that only ever models a
    ///     comfortable 4 cm stand-off is testing a rig that does not ship.
    ///
    /// So the stand-off is a SWEPT PARAMETER here, from 0 (the calibrated-onto-the-bone case the rig
    /// actually produces) out to 6 cm (a mount with real residual offset), and the tests say which of them
    /// survive.
    /// ================================================================================================
    ///
    /// WHAT THE SOLVER STILL DOES TO A TRACKER, AS OF THIS FILE (read from BasisArmSolveCore, not assumed):
    ///   * hintFade is a hard-coded 1f for EVERY hint -- the abProj fade is GONE for the tracker too.
    ///   * the pole-collapse (world-down) stabilizer is skipped outright: `if (HintWeight && !HintIsTracker)`.
    ///   * HintMaxStepDeg is float.MaxValue from the live job -- no rate limit.
    ///   * the arm's One-Euro (SmoothElbowSwivel) has no caller any more -- no temporal filter.
    ///   * so the ONLY thing left standing between a tracker and the elbow is the hard pole epsilon
    ///     `ahProj.sqrMagnitude > totalLen*totalLen*0.001`, and BasisElbowAnatomyCore at the very end.
    /// Those last two are what this file leans on.
    /// </summary>
    public sealed class BasisArmTrackerHintAuthorityTests
    {
        const float k_Upper = 0.30f;
        const float k_Lower = 0.30f;
        const float k_ArmLen = k_Upper + k_Lower;
        static readonly Vector3 k_Shoulder = new Vector3(0.17f, 1.40f, 0f);
        static readonly Vector3 k_Up = Vector3.up;

        /// <summary>The canonical VR reach: hand out to the side and a little forward.</summary>
        static readonly Vector3 k_ReachDir = new Vector3(0.92f, 0f, 0.39f).normalized;

        /// <summary>An elbow hangs DOWN and a little back. Anatomically legal at every extension used here,
        /// so BasisElbowAnatomyCore stays at exact identity and cannot be mistaken for hint authority.</summary>
        const float k_CommandedSwivel = 30f;

        /// <summary>BasisArmSolveCore admits a hint only when `ahProj.sqrMagnitude > totalLen^2 * 0.001`.
        /// As a fraction of arm length that is sqrt(0.001). It is a HARD BOOLEAN -- there is no ramp.</summary>
        const float k_PoleEpsFrac = 0.0316228f;

        /// <summary>The extensions under test. 50% is a comfortable bend; above ~97% is where the arm's
        /// authority used to collapse and where a VR user actually lives.</summary>
        static readonly float[] k_Reaches = { 0.50f, 0.80f, 0.95f, 0.97f, 0.99f, 0.995f, 0.999f };

        /// <summary>Stand-offs, metres. 0.00 is the calibrated-onto-the-bone case THE LIVE RIG PRODUCES;
        /// 0.03-0.06 is a puck proud of a real arm.</summary>
        static readonly float[] k_StandOffs = { 0.00f, 0.01f, 0.02f, 0.03f, 0.04f, 0.06f };

        // ── geometry ────────────────────────────────────────────────────────────────────────────────

        /// <summary>The swing frame the elbow's circle lives in: the shoulder->hand axis, and an in-plane
        /// basis whose zero is body DOWN. Same convention as BasisArmSwivelModel.BendDirection, so a swivel
        /// angle means the same thing here as it does to the model and to the solver.</summary>
        static void SwingBasis(Vector3 hand, out Vector3 axis, out Vector3 u, out Vector3 v)
        {
            Vector3 sa = hand - k_Shoulder;
            axis = sa.normalized;
            Vector3 refDown = Vector3.down;
            u = (refDown - axis * Vector3.Dot(refDown, axis)).normalized;
            v = Vector3.Cross(axis, u);
        }

        /// <summary>The circle the elbow is confined to once shoulder and hand are both fixed.</summary>
        static void ElbowCircle(Vector3 hand, out Vector3 centre, out float radius)
        {
            Vector3 sa = hand - k_Shoulder;
            float d = sa.magnitude;
            float a = (k_Upper * k_Upper - k_Lower * k_Lower + d * d) / (2f * d);
            radius = Mathf.Sqrt(Mathf.Max(k_Upper * k_Upper - a * a, 0f));
            centre = k_Shoulder + (sa / d) * a;
        }

        /// <summary>A GEOMETRICALLY REAL elbow at swivel `swivelDeg` -- on the circle, at the bone lengths the
        /// skeleton demands. This is what the ANIMATION hands the solver, and what the solver must rescue.</summary>
        static Vector3 ElbowOnCircle(Vector3 hand, float swivelDeg)
        {
            SwingBasis(hand, out _, out Vector3 u, out Vector3 v);
            ElbowCircle(hand, out Vector3 centre, out float radius);
            float t = swivelDeg * Mathf.Deg2Rad;
            return centre + radius * (u * Mathf.Cos(t) + v * Mathf.Sin(t));
        }

        /// <summary>
        /// ⭐ THE STRAPPED PUCK. Not a pole -- a physical object on the outside of a physical arm.
        ///
        /// It shares the elbow's ANGULAR STATION about the arm (it is strapped to the limb, so it rides the
        /// limb's roll -- that is the entire reason it carries a swivel signal), stands `standOff` metres
        /// proud of the bone, and sits `slide` metres PROXIMAL of the elbow along the arm axis, because a
        /// strap goes round the upper arm and not through the joint.
        ///
        /// Note what `slide` does to the solve: NOTHING. BasisArmSolveCore reads only `ahProj`, the component
        /// of shoulder->hint PERPENDICULAR to the shoulder->hand axis, so sliding the strap up and down the
        /// arm is provably invisible to it. StrapPosition_AlongTheArm_CannotChangeTheCommandedSwivel pins that,
        /// because it means a mount that migrates during play cannot roll the elbow.
        /// </summary>
        static Vector3 StrappedTracker(Vector3 hand, float swivelDeg, float standOff, float slide)
        {
            SwingBasis(hand, out Vector3 axis, out Vector3 u, out Vector3 v);
            ElbowCircle(hand, out Vector3 centre, out float radius);
            float t = swivelDeg * Mathf.Deg2Rad;
            Vector3 outward = u * Mathf.Cos(t) + v * Mathf.Sin(t);
            return centre - axis * slide + outward * (radius + standOff);
        }

        /// <summary>The elbow's swivel about the shoulder->hand axis. This -- not its position -- is what the
        /// pole controls, and what the user reads as "which side is my elbow on".</summary>
        static float SwivelDeg(Vector3 hand, Vector3 elbow)
        {
            SwingBasis(hand, out Vector3 axis, out Vector3 u, out Vector3 v);
            Vector3 p = elbow - k_Shoulder;
            p -= axis * Vector3.Dot(p, axis);
            return Mathf.Atan2(Vector3.Dot(p, v), Vector3.Dot(p, u)) * Mathf.Rad2Deg;
        }

        static float DeltaDeg(float a, float b)
        {
            float d = Mathf.Repeat(a - b + 180f, 360f) - 180f;
            return Mathf.Abs(d);
        }

        // ── noise ───────────────────────────────────────────────────────────────────────────────────

        static float Gauss(System.Random rng)
        {
            double u1 = 1.0 - rng.NextDouble();
            double u2 = rng.NextDouble();
            return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
        }

        /// <summary>Real tracker noise: a millimetre-scale gaussian floor, plus the occasional larger glitch
        /// (a brief occlusion / IMU pop). Both are what a lighthouse puck actually does.</summary>
        static Vector3 TrackerNoise(System.Random rng, float sigmaM, float glitchChance, float glitchM)
        {
            var n = new Vector3(Gauss(rng) * sigmaM, Gauss(rng) * sigmaM, Gauss(rng) * sigmaM);
            if (rng.NextDouble() < glitchChance)
            {
                n += new Vector3(Gauss(rng), Gauss(rng), Gauss(rng)).normalized * glitchM;
            }
            return n;
        }

        // ── the solve input the LIVE JOB builds ─────────────────────────────────────────────────────

        /// <summary>Exactly what BasisFullBodyIK.SolveTwoBoneIKArms passes when an elbow tracker is assigned:
        /// HintIsTracker = true, HintMaxStepDeg = MaxValue (the live rig is stateless and unclamped).</summary>
        static BasisArmSolveInput TrackerInput(Vector3 animElbow, Vector3 target, Vector3 hintPos)
        {
            BasisArmSolveInput i = default;
            i.Shoulder = k_Shoulder;
            i.Elbow = animElbow;
            i.Hand = target;
            i.RootRotation = Quaternion.identity;
            i.MidRotation = Quaternion.identity;
            i.TargetPosition = target;
            i.TargetRotation = Quaternion.identity;
            i.TargetOffset = Quaternion.identity;
            i.HintPosition = hintPos;
            i.HintWeight = true;
            i.HintIsTracker = true;              // ⭐ THE TRACKER PATH
            i.HintMaxStepDeg = float.MaxValue;   // what the live job passes
            i.PlayerUp = Vector3.up;
            return i;
        }

        /// <summary>
        /// The hand must be genuinely in reach AND the elbow's circle must be big enough to have a DIRECTION,
        /// or "which side is the elbow on" is not a question and the assertion below is vacuous. This is the
        /// guard that a previous test in this area did not have -- it put the hand 0.652 m from a 0.60 m arm,
        /// there was no elbow circle at all, and it reported safety while the bug was live.
        /// </summary>
        static void RequireInReach(Vector3 hand)
        {
            float d = Vector3.Distance(k_Shoulder, hand);
            Assert.Less(d, k_ArmLen,
                $"the test's own hand is {d:F4} m from a {k_ArmLen:F2} m arm -- OUT OF REACH, so there is no " +
                "elbow circle and this test would prove nothing");
            ElbowCircle(hand, out _, out float radius);
            Assert.Greater(radius, 0.005f,
                $"the elbow's circle has collapsed to radius {radius * 1000f:F2} mm -- too small for its " +
                "DIRECTION to be meaningful, so this test would prove nothing");
        }

        // ════════════════════════════════════════════════════════════════════════════════════════════
        // 1. AUTHORITY -- the question this file was opened to answer.
        // ════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// ⭐ DOES A REAL ELBOW TRACKER STILL REACH THE BONE NEAR FULL EXTENSION?
        ///
        /// Same shape as the model's authority test, on the path a tracker actually takes. The arm's ANIMATED
        /// pose starts the elbow 180 deg round its circle from where the tracker says it is -- which is what an
        /// idle animation hands a fully-extended arm -- and the solver must drag it all the way across, at every
        /// extension and at every realistic stand-off.
        ///
        /// If this fails at 0.999 the tracker path has the SAME authority bug the model path had, and a user
        /// with an elbow tracker loses their elbow exactly where they spend most of their time.
        /// </summary>
        [Test]
        public void AStrappedTracker_LandsTheElbowOnItsPole_AtEveryExtension_AndStandOff()
        {
            var failures = new StringBuilder();

            foreach (float standOff in k_StandOffs)
            {
                foreach (float reach in k_Reaches)
                {
                    Vector3 target = k_Shoulder + k_ReachDir * (reach * k_ArmLen);
                    RequireInReach(target);

                    Vector3 puck = StrappedTracker(target, k_CommandedSwivel, standOff, 0.05f);
                    Vector3 animElbow = ElbowOnCircle(target, k_CommandedSwivel + 180f);   // wrong side, real bone lengths

                    BasisArmSolveCore.Solve(TrackerInput(animElbow, target, puck), out BasisArmSolveResult r);

                    // The hand first. Everything else is meaningless if the hand is not on its target.
                    Assert.AreEqual(0f, Vector3.Distance(r.HandSolved, target), 2e-3f,
                        $"the hand must reach its target (standOff {standOff * 100f:F0} cm, reach {reach:P1})");

                    float got = SwivelDeg(r.HandSolved, r.ElbowSolved);
                    float off = DeltaDeg(got, k_CommandedSwivel);

                    if (off >= 5f || !r.HintApplied)
                    {
                        failures.AppendLine(
                            $"  standOff {standOff * 100f,2:F0} cm  reach {reach,6:P1}  ->  elbow {off,6:F1} deg off the " +
                            $"tracker's pole  (applied={r.HintApplied}, fade={r.HintFade:F3}, " +
                            $"poleNorm={r.HintProjMag / k_ArmLen:F4}, eps={k_PoleEpsFrac:F4})");
                    }
                }
            }

            if (failures.Length > 0)
            {
                Assert.Fail(
                    "A REAL ELBOW TRACKER DOES NOT REACH THE ELBOW.\n" +
                    "The solver left the elbow on the pose the ANIMATION put it in (180 deg from the tracker) " +
                    "instead of on the pole the user's own tracker commanded:\n" + failures +
                    "\n`applied=False` means BasisArmSolveCore's hard pole epsilon " +
                    "(ahProj^2 > totalLen^2 * 0.001) rejected the hint outright -- the tracker was not faded, " +
                    "it was DROPPED.");
            }
        }

        /// <summary>
        /// ...AND IT MUST NOT COST REACH. A swivel about the shoulder->hand axis cannot move the hand, because
        /// the hand lies ON that axis. Structural, so it must hold at every stand-off, every extension, and
        /// under noise -- pinned so nobody buys the elbow back by trading the hand away.
        /// </summary>
        [Test]
        public void AStrappedTracker_NeverMovesTheHandOffItsTarget_EvenUnderNoise()
        {
            var rng = new System.Random(20260714);
            float worst = 0f;

            for (int t = 0; t < 2000; t++)
            {
                float reach = Mathf.Lerp(0.40f, 0.999f, (float)rng.NextDouble());
                var dir = new Vector3(
                    (float)(rng.NextDouble() * 2 - 1),
                    (float)(rng.NextDouble() * 2 - 1),
                    (float)(rng.NextDouble() * 2 - 1));
                if (dir.sqrMagnitude < 1e-4f) continue;
                dir.Normalize();

                Vector3 target = k_Shoulder + dir * (reach * k_ArmLen);
                float swivel = (float)(rng.NextDouble() * 360.0 - 180.0);
                float standOff = Mathf.Lerp(0f, 0.06f, (float)rng.NextDouble());

                Vector3 puck = StrappedTracker(target, swivel, standOff, 0.05f)
                             + TrackerNoise(rng, 0.001f, 0.02f, 0.02f);
                Vector3 animElbow = ElbowOnCircle(target, swivel + 137f);

                BasisArmSolveCore.Solve(TrackerInput(animElbow, target, puck), out BasisArmSolveResult r);

                float err = Vector3.Distance(r.HandSolved, target);
                if (err > worst) worst = err;

                Assert.IsTrue(float.IsFinite(r.ElbowSolved.x) && float.IsFinite(r.ElbowSolved.y) && float.IsFinite(r.ElbowSolved.z),
                    $"the elbow must stay finite (iter {t}, reach {reach:P1}, standOff {standOff * 100f:F1} cm)");
            }

            Assert.Less(worst, 2e-3f,
                $"a tracker hint moved the hand up to {worst * 1000f:F2} mm off its target. The hint swivel is " +
                "about the shoulder->hand axis BY NAME, and the hand lies on that axis, so this is structurally " +
                "impossible unless the named-axis swivel has been replaced by a FromToRotation again.");
        }

        /// <summary>
        /// A strap MIGRATES. It slides up and down the upper arm during play, and it must not roll the elbow
        /// when it does. The solver reads only the pole's PERPENDICULAR component, so this is structural -- but
        /// it is exactly the kind of structure a "fix" silently breaks, so it is pinned.
        /// </summary>
        [Test]
        public void StrapPosition_AlongTheArm_CannotChangeTheCommandedSwivel()
        {
            Vector3 target = k_Shoulder + k_ReachDir * (0.97f * k_ArmLen);
            RequireInReach(target);
            Vector3 animElbow = ElbowOnCircle(target, k_CommandedSwivel + 180f);

            float first = float.NaN;
            foreach (float slide in new[] { -0.02f, 0f, 0.03f, 0.06f, 0.10f })
            {
                Vector3 puck = StrappedTracker(target, k_CommandedSwivel, 0.04f, slide);
                BasisArmSolveCore.Solve(TrackerInput(animElbow, target, puck), out BasisArmSolveResult r);
                float got = SwivelDeg(r.HandSolved, r.ElbowSolved);

                if (float.IsNaN(first)) first = got;
                Assert.Less(DeltaDeg(got, first), 0.5f,
                    $"sliding the strap {slide * 100f:F0} cm along the arm rolled the elbow to {got:F2} deg " +
                    $"(from {first:F2} deg). A strap that migrates during play must not move the elbow.");
            }
        }

        // ════════════════════════════════════════════════════════════════════════════════════════════
        // 2. THE SURVIVING CLIFF -- the hard pole epsilon, which is the ONLY gate a tracker still meets.
        // ════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// ⭐ THE HINT MUST ACTUALLY BE APPLIED.
        ///
        /// The abProj FADE is gone (hintFade is a hard 1f). But `ahProj.sqrMagnitude > totalLen^2 * 0.001`
        /// SURVIVES, and it is a BOOLEAN, not a ramp -- full hint on one side of it, NO hint at all on the
        /// other. That is precisely the shape of the cliff that project_basis_ik_hint_extension_snap was
        /// written about, and it is still standing on the quantity a tracker cannot keep clear of zero.
        ///
        /// A tracker's pole is `hint - shoulder` projected off the arm axis. For the hint THE LIVE RIG ACTUALLY
        /// SENDS -- the calibrated lower-arm BONE, i.e. the joint centre, stand-off ~= 0 -- that projection IS
        /// the elbow's own collapsing lever arm. So it goes to zero as the arm straightens, crosses the epsilon,
        /// and the tracker is dropped in a single frame.
        ///
        /// A stand-off is the only thing that keeps it clear. This test says how much you need.
        /// </summary>
        [Test]
        public void ATrackerHint_IsNotDroppedByThePoleEpsilon_AtAnyExtension()
        {
            var dropped = new StringBuilder();

            foreach (float standOff in k_StandOffs)
            {
                foreach (float reach in k_Reaches)
                {
                    Vector3 target = k_Shoulder + k_ReachDir * (reach * k_ArmLen);
                    Vector3 puck = StrappedTracker(target, k_CommandedSwivel, standOff, 0.05f);
                    Vector3 animElbow = ElbowOnCircle(target, k_CommandedSwivel + 180f);

                    BasisArmSolveCore.Solve(TrackerInput(animElbow, target, puck), out BasisArmSolveResult r);

                    if (!r.HintApplied)
                    {
                        dropped.AppendLine(
                            $"  standOff {standOff * 100f,2:F0} cm  reach {reach,6:P1}  ->  DROPPED " +
                            $"(poleNorm {r.HintProjMag / k_ArmLen:F4} < eps {k_PoleEpsFrac:F4})");
                    }
                }
            }

            if (dropped.Length > 0)
            {
                Assert.Fail(
                    "THE TRACKER'S HINT WAS DROPPED OUTRIGHT by BasisArmSolveCore's hard pole epsilon.\n" +
                    "Not faded -- DROPPED, in one frame, leaving the elbow wherever the animation put it:\n" + dropped +
                    "\nThis is a BOOLEAN gate on ahProj. The abProj fade next to it was replaced by a ramp " +
                    "precisely because a boolean on a collapsing quantity is a snap; this one was left alone, " +
                    "and for a hint calibrated onto the joint centre -- which is what BasisLocalRigDriver sends -- " +
                    "ahProj collapses just as surely.");
            }
        }

        /// <summary>
        /// ⭐ AND A BOOLEAN GATE ON A COLLAPSING QUANTITY IS A SNAP. This proves it is one.
        ///
        /// The methodology is this repo's own, from project_basis_ik_hint_extension_snap: a snap is INVISIBLE
        /// in any single pose -- every individual frame looks perfectly reasonable, and it only exists BETWEEN
        /// frames. So sweep the hand through the singularity in fine steps and measure ELBOW TRAVEL PER UNIT OF
        /// HAND TRAVEL. The elbow legitimately outruns the hand near extension (the bend radius collapses like
        /// sqrt(maxReach - d), which climbs the ratio to ~12 on its own); the measured snaps were 294.7x
        /// (elbow) and 356x (knee). A gate of 30 separates geometry from a pole being switched off.
        ///
        /// That is exactly what the abProj cliff was, and exactly what was fixed by ramping it. THE ahProj
        /// CLIFF NEXT TO IT WAS NEVER RAMPED -- it is still `ahProj.sqrMagnitude > totalLen^2 * 0.001`, a hard
        /// boolean -- and for the joint-centre hint the live rig sends, ahProj collapses through it.
        /// </summary>
        [Test]
        public void ATrackerHint_DoesNotSnapTheElbow_AsTheArmStraightensThroughThePoleEpsilon()
        {
            const int steps = 400;
            const float k_SnapGate = 30f;   // the repo's own calibrated gate: geometry ~12x, measured snaps ~300x

            // Straighten the arm right through the epsilon. At standOff = 0 -- the hint calibrated ONTO the
            // joint centre, which is what BasisLocalRigDriver sends -- the pole crosses sqrt(0.001)*armLen
            // (18.97 mm) at about 99.8% extension.
            const float from = 0.9900f;
            const float to = 0.9998f;

            Vector3 prevElbow = Vector3.zero, prevHand = Vector3.zero;
            float worst = 0f, worstAt = 0f;
            bool appliedBefore = false, sawFlip = false;
            float flipAt = 0f;

            for (int s = 0; s <= steps; s++)
            {
                float reach = Mathf.Lerp(from, to, s / (float)steps);
                Vector3 target = k_Shoulder + k_ReachDir * (reach * k_ArmLen);

                Vector3 puck = StrappedTracker(target, k_CommandedSwivel, 0f, 0.05f);   // ON the joint centre
                Vector3 animElbow = ElbowOnCircle(target, k_CommandedSwivel + 180f);    // the idle animation's bend

                BasisArmSolveCore.Solve(TrackerInput(animElbow, target, puck), out BasisArmSolveResult r);

                if (s > 0)
                {
                    float handTravel = Vector3.Distance(r.HandSolved, prevHand);
                    float elbowTravel = Vector3.Distance(r.ElbowSolved, prevElbow);
                    if (handTravel > 1e-9f)
                    {
                        float ratio = elbowTravel / handTravel;
                        if (ratio > worst) { worst = ratio; worstAt = reach; }
                    }
                    if (r.HintApplied != appliedBefore && !sawFlip)
                    {
                        sawFlip = true;
                        flipAt = reach;
                    }
                }

                appliedBefore = r.HintApplied;
                prevElbow = r.ElbowSolved;
                prevHand = r.HandSolved;
            }

            TestContext.WriteLine(
                $"\n  pole-epsilon sweep, standOff = 0 (hint on the joint centre, as the live rig sends it):" +
                $"\n    worst elbow travel / hand travel = {worst:F1}x at reach {worstAt:P2}" +
                $"\n    HintApplied flipped: {(sawFlip ? $"YES, at reach {flipAt:P2}" : "no")}\n");

            Assert.Less(worst, k_SnapGate,
                $"THE ELBOW SNAPS. It travelled {worst:F0}x the hand's distance in a single step at " +
                $"{worstAt:P2} extension" + (sawFlip ? $", where HintApplied flipped (reach {flipAt:P2})" : "") +
                ".\nGeometry alone gets to ~12x near extension; a pole switched off between two frames is what " +
                "gets to hundreds. BasisArmSolveCore's hint gate `ahProj.sqrMagnitude > totalLen*totalLen*0.001` " +
                "is a HARD BOOLEAN on a quantity that collapses as the arm straightens -- the same shape as the " +
                "abProj cliff that was ramped, on the lever arm right next to it, still unramped.");
        }

        // ════════════════════════════════════════════════════════════════════════════════════════════
        // 3. NOISE -- is keeping the fade actually right for a tracker? MEASURE IT.
        // ════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// ⭐ THE FADE'S ONLY JUSTIFICATION, PUT ON THE SCALES.
        ///
        /// The stated reason a tracker keeps its fade is "a measured hint has noise to amplify". That is a
        /// QUANTITATIVE claim and it has never been quantified, so here it is.
        ///
        /// The solve rotates the elbow's lever arm ONTO the pole, so the elbow's ANGLE is the pole's angle and
        /// a tracker error `d` at pole radius `R` becomes an angular error d/R. R collapses as the arm
        /// straightens, so the ANGLE is amplified without bound -- that is the fade's case, and it is true.
        ///
        /// But the elbow's POSITION is `r * angle`, where `r` is the elbow's OWN lever arm -- and that collapses
        /// at the same rate. So the amplified angle is multiplied by a vanishing radius, and what the user
        /// actually SEES need not blow up at all.
        ///
        /// This measures both, and the assertion is on the one that reaches the eye: the elbow's POSITION must
        /// not jitter materially more than the tracker itself does. If that holds, the fade was protecting
        /// against a quantity that geometry already drives to zero, and removing it costs nothing real.
        /// </summary>
        [Test]
        public void TrackerNoise_IsNotAmplifiedIntoTheElbowsPosition()
        {
            const float sigmaM = 0.001f;    // 1 mm RMS -- a lighthouse puck's static noise floor
            const int frames = 600;

            // ⭐ THE GATE, AND IT IS TIGHT ON PURPOSE.
            //
            // The elbow's positional jitter is `elbowR * (noise / poleR)`, and for a puck strapped OUTSIDE the
            // arm poleR = elbowR + standOff >= elbowR. So the ratio is elbowR/poleR <= 1 BY CONSTRUCTION: the
            // elbow can never jitter more than the tracker does, at any extension. That is the whole reason the
            // fade is unnecessary, and it means the honest gate is ~1, not "some large number".
            //
            // My first draft of this test gated at 3x. The bound is 1.0. A 3x gate could NEVER have fired --
            // it would have passed forever while proving nothing, which is the exact failure this suite has
            // been bitten by twice. 1.15 leaves room for float and for the guard, and nothing else.
            const float k_MaxAmplification = 1.15f;

            var report = new StringBuilder();
            var failures = new StringBuilder();
            int live = 0;
            float maxSwivelSd = 0f;

            report.AppendLine();
            report.AppendLine("  TRACKER NOISE -> ELBOW, sigma = 1.0 mm, no glitches, 600 frames, HintIsTracker = true");
            report.AppendLine("  standOff  reach     elbowR    poleR     swivel sd    elbow pos sd    pos amplification");
            report.AppendLine("  --------  ------    ------    ------    ---------    ------------    -----------------");

            foreach (float standOff in k_StandOffs)
            {
                foreach (float reach in k_Reaches)
                {
                    Vector3 target = k_Shoulder + k_ReachDir * (reach * k_ArmLen);
                    ElbowCircle(target, out _, out float elbowR);
                    Vector3 animElbow = ElbowOnCircle(target, k_CommandedSwivel + 180f);
                    Vector3 puck0 = StrappedTracker(target, k_CommandedSwivel, standOff, 0.05f);

                    var rng = new System.Random(1234567);
                    double sSw = 0, sSw2 = 0;
                    Vector3 meanPos = Vector3.zero;
                    var elbows = new Vector3[frames];
                    int applied = 0;
                    float poleR = 0f;

                    for (int f = 0; f < frames; f++)
                    {
                        Vector3 puck = puck0 + TrackerNoise(rng, sigmaM, 0f, 0f);
                        BasisArmSolveCore.Solve(TrackerInput(animElbow, target, puck), out BasisArmSolveResult r);

                        if (r.HintApplied) applied++;
                        poleR = r.HintProjMag;

                        elbows[f] = r.ElbowSolved;
                        meanPos += r.ElbowSolved;

                        float sw = SwivelDeg(r.HandSolved, r.ElbowSolved);
                        sSw += sw;
                        sSw2 += (double)sw * sw;
                    }

                    meanPos /= frames;
                    double varSw = sSw2 / frames - (sSw / frames) * (sSw / frames);
                    float sdSw = (float)Math.Sqrt(Math.Max(varSw, 0.0));

                    double sPos2 = 0;
                    for (int f = 0; f < frames; f++)
                    {
                        sPos2 += (elbows[f] - meanPos).sqrMagnitude;
                    }
                    float sdPos = (float)Math.Sqrt(sPos2 / frames);   // RMS 3D displacement, metres
                    float amp = sdPos / sigmaM;

                    bool hintLive = applied == frames;
                    report.AppendLine(
                        $"  {standOff * 100f,5:F0} cm  {reach,6:P1}   {elbowR * 1000f,6:F1}mm  {poleR * 1000f,6:F1}mm  " +
                        $"{sdSw,8:F2} deg   {sdPos * 1000f,9:F2} mm    {amp,10:F3}x" +
                        (hintLive ? "" : $"   [DROPPED {frames - applied}/{frames} frames]"));

                    // A DROPPED hint jitters by exactly zero. That is not a pass -- it is the authority bug,
                    // and ATrackerHint_IsNotDroppedByThePoleEpsilon owns it. Judge only the live ones, and
                    // count them, so this test cannot pass by having measured nothing at all.
                    if (!hintLive)
                    {
                        continue;
                    }

                    live++;
                    if (sdSw > maxSwivelSd) maxSwivelSd = sdSw;

                    if (amp > k_MaxAmplification)
                    {
                        failures.AppendLine(
                            $"  standOff {standOff * 100f:F0} cm, reach {reach:P1}: the elbow jitters " +
                            $"{sdPos * 1000f:F2} mm from a {sigmaM * 1000f:F1} mm tracker -- {amp:F2}x amplification " +
                            $"(elbowR {elbowR * 1000f:F1} mm / poleR {poleR * 1000f:F1} mm)");
                    }
                }
            }

            TestContext.WriteLine(report.ToString());

            // ── NON-VACUITY. Without these two, a solver that ignored the tracker entirely would score a
            // perfect 0.00x amplification and this test would congratulate it.
            Assert.Greater(live, 20,
                $"only {live} of {k_StandOffs.Length * k_Reaches.Length} configurations had a LIVE hint -- " +
                "this test measured almost nothing and its pass means nothing");
            Assert.Greater(maxSwivelSd, 0.1f,
                $"the elbow's swivel never moved by more than {maxSwivelSd:F3} deg under a 1 mm noisy tracker. " +
                "The elbow is not following the tracker at all, so a low amplification score is meaningless.");

            if (failures.Length > 0)
            {
                Assert.Fail(
                    "TRACKER NOISE IS AMPLIFIED INTO THE ELBOW'S POSITION:\n" + failures +
                    "\nThe fade's stated justification was exactly this -- 'a measured hint has noise to " +
                    "amplify' -- and on this evidence it was right. Note WHICH lever arm matters: the gain is " +
                    "noise/poleR, so the fade belongs on the HINT's lever arm (ahProj), never on the elbow's " +
                    "own (abProj), which is the quantity the original bug faded and which does not appear in " +
                    "the gain at all.");
            }
        }

        /// <summary>
        /// A GLITCH IS NOT NOISE. A lighthouse puck occasionally jumps a centimetre or two -- occlusion, an IMU
        /// pop, a reflection. The solve is stateless and unclamped (HintMaxStepDeg = MaxValue from the live
        /// job), so nothing between the puck and the bone can absorb that: whatever the glitch commands, the
        /// elbow does, that frame.
        ///
        /// This does not assert the glitch is filtered -- it is not, and pretending otherwise would be a test
        /// that passes while proving nothing. It asserts the two things that must survive one anyway: the hand
        /// stays exactly on target, and the elbow stays inside the anatomical envelope.
        /// </summary>
        [Test]
        public void ATrackerGlitch_MovesTheElbowButBreaksNeitherTheHandNorTheAnatomy()
        {
            var rng = new System.Random(99);
            Vector3 target = k_Shoulder + k_ReachDir * (0.98f * k_ArmLen);
            RequireInReach(target);
            Vector3 animElbow = ElbowOnCircle(target, k_CommandedSwivel + 180f);
            Vector3 puck0 = StrappedTracker(target, k_CommandedSwivel, 0.04f, 0.05f);

            float ceiling = Mathf.Max(Vector3.Dot(target - k_Shoulder, k_Up), 0f);
            float hardCeiling = ceiling + BasisElbowAnatomyCore.HardMarginFracLimb * k_ArmLen;

            for (int f = 0; f < 500; f++)
            {
                Vector3 puck = puck0 + TrackerNoise(rng, 0.001f, 0.10f, 0.03f);   // 10% of frames get a 3 cm pop
                BasisArmSolveCore.Solve(TrackerInput(animElbow, target, puck), out BasisArmSolveResult r);

                Assert.AreEqual(0f, Vector3.Distance(r.HandSolved, target), 2e-3f,
                    $"a tracker glitch threw the hand off its target (frame {f})");

                float h = Vector3.Dot(r.ElbowSolved - k_Shoulder, k_Up);
                Assert.LessOrEqual(h, hardCeiling + 1e-4f,
                    $"a tracker glitch put the elbow {(h - ceiling) * 100f:F1} cm above the ceiling (frame {f}). " +
                    "BasisElbowAnatomyCore runs LAST precisely so a mis-strapped or glitching tracker cannot do " +
                    "this -- it guards the OUTCOME, not the hint.");
            }
        }

        // ════════════════════════════════════════════════════════════════════════════════════════════
        // 4. THE ANATOMY GUARD MUST COVER A BAD TRACKER.
        // ════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// ⭐ A MIS-STRAPPED TRACKER CANNOT PUT THE ELBOW ABOVE THE SHOULDER.
        ///
        /// The tracker path skips the pole-collapse stabilizer entirely (`if (HintWeight && !HintIsTracker)`),
        /// takes no fade, takes no rate limit and takes no One-Euro. So a tracker mounted upside down, rotated
        /// round the arm, or simply strapped to the wrong limb has NOTHING standing between it and the bone --
        /// except BasisElbowAnatomyCore, which runs last and guards the OUTCOME.
        ///
        /// So it must hold here. Command the elbow STRAIGHT UP (the chicken wing the user reported) from a
        /// tracker, and the elbow must still come to rest at or below the higher of shoulder and hand.
        /// </summary>
        [Test]
        public void AMisStrappedTracker_CannotLiftTheElbowAboveTheAnatomicalCeiling()
        {
            var offenders = new StringBuilder();

            // Reaches where the hand is at or below the shoulder, so the ceiling is the SHOULDER and an elbow
            // commanded upward is genuinely illegal. (Hand held high legitimately lifts the elbow -- 71.9% of
            // those corpus frames do -- so it is not a violation and is not tested as one.)
            (Vector3 dir, string what)[] cases =
            {
                (new Vector3(0.95f,  0.00f, 0.20f).normalized, "arm out to the side, hand at shoulder height"),
                (new Vector3(0.90f, -0.20f, 0.30f).normalized, "arm out and forward, hand below the shoulder"),
                (new Vector3(0.40f, -0.85f, 0.30f).normalized, "arm down and forward, hand at the hip"),
                (new Vector3(0.20f, -0.30f, 0.93f).normalized, "arm forward, hand below the shoulder"),
            };

            foreach ((Vector3 dir, string what) in cases)
            {
                foreach (float reach in new[] { 0.60f, 0.80f, 0.90f, 0.95f, 0.97f })
                {
                    Vector3 target = k_Shoulder + dir * (reach * k_ArmLen);
                    RequireInReach(target);

                    // The tracker commands the elbow STRAIGHT UP: swivel 180 deg from body-down.
                    Vector3 puck = StrappedTracker(target, 180f, 0.04f, 0.05f);
                    Vector3 animElbow = ElbowOnCircle(target, 0f);   // animation has it hanging correctly

                    BasisArmSolveCore.Solve(TrackerInput(animElbow, target, puck), out BasisArmSolveResult r);

                    Assert.AreEqual(0f, Vector3.Distance(r.HandSolved, target), 2e-3f,
                        $"the guard must not cost reach ({what}, {reach:P0})");

                    float ceiling = Mathf.Max(Vector3.Dot(target - k_Shoulder, k_Up), 0f);
                    float h = Vector3.Dot(r.ElbowSolved - k_Shoulder, k_Up);
                    float over = h - ceiling;
                    float hardMargin = BasisElbowAnatomyCore.HardMarginFracLimb * k_ArmLen;

                    if (over > hardMargin + 1e-4f)
                    {
                        offenders.AppendLine(
                            $"  {what}, {reach:P0}: elbow {over * 100f:F1} cm above the ceiling " +
                            $"(hard limit {hardMargin * 100f:F1} cm)");
                    }
                }
            }

            if (offenders.Length > 0)
            {
                Assert.Fail(
                    "A TRACKER COMMANDED THE ELBOW ABOVE THE SHOULDER AND THE SOLVER OBEYED:\n" + offenders +
                    "\nBasisElbowAnatomyCore is documented to guard the OUTCOME precisely so that a " +
                    "mis-strapped elbow tracker cannot do this. On the tracker path it is the ONLY guard left " +
                    "standing -- no fade, no stabilizer, no rate limit, no filter -- so if it does not hold " +
                    "here, nothing does.");
            }
        }

        // ════════════════════════════════════════════════════════════════════════════════════════════
        // 5. INSTRUMENTATION -- the numbers, printed, so a claim about this path can be checked.
        // ════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Not a gate -- a MEASUREMENT. Prints what the solver actually grants a tracker across the extension
        /// range, so the next person to change this does not have to guess (which, on this file, has a track
        /// record of being wrong five times in a row).
        /// </summary>
        [Test]
        public void Instrument_WhatTheSolverGrantsATracker()
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("  ══ WHAT BasisArmSolveCore GRANTS A TRACKER (HintIsTracker = true) ══");
            sb.AppendLine($"  arm {k_ArmLen:F2} m, pole epsilon = {k_PoleEpsFrac:F4} * armLen = {k_PoleEpsFrac * k_ArmLen * 1000f:F1} mm");
            sb.AppendLine();
            sb.AppendLine("  standOff  reach     elbowR    poleR     fade    applied   elbow off pole   swivel gain");
            sb.AppendLine("  --------  ------    ------    ------    ----    -------   --------------   -----------");

            foreach (float standOff in k_StandOffs)
            {
                foreach (float reach in k_Reaches)
                {
                    Vector3 target = k_Shoulder + k_ReachDir * (reach * k_ArmLen);
                    ElbowCircle(target, out _, out float elbowR);
                    Vector3 puck = StrappedTracker(target, k_CommandedSwivel, standOff, 0.05f);
                    Vector3 animElbow = ElbowOnCircle(target, k_CommandedSwivel + 180f);

                    BasisArmSolveCore.Solve(TrackerInput(animElbow, target, puck), out BasisArmSolveResult r);

                    float got = SwivelDeg(r.HandSolved, r.ElbowSolved);
                    float off = DeltaDeg(got, k_CommandedSwivel);

                    // d(elbow swivel) / d(tracker displacement perpendicular to the pole), deg per mm.
                    // This IS the noise amplification, measured rather than argued.
                    SwingBasis(target, out Vector3 axis, out Vector3 u, out Vector3 v);
                    float t = k_CommandedSwivel * Mathf.Deg2Rad;
                    Vector3 tangent = -u * Mathf.Sin(t) + v * Mathf.Cos(t);   // perpendicular to the pole, in-plane
                    Vector3 nudged = puck + tangent * 0.001f;                 // 1 mm sideways on the puck
                    BasisArmSolveCore.Solve(TrackerInput(animElbow, target, nudged), out BasisArmSolveResult rn);
                    float gain = DeltaDeg(SwivelDeg(rn.HandSolved, rn.ElbowSolved), got);

                    sb.AppendLine(
                        $"  {standOff * 100f,5:F0} cm  {reach,6:P1}   {elbowR * 1000f,6:F1}mm  {r.HintProjMag * 1000f,6:F1}mm  " +
                        $"{r.HintFade,5:F2}   {(r.HintApplied ? "  yes  " : "  NO   "),-7}   {off,10:F1} deg   {gain,7:F2} deg/mm");
                }
                sb.AppendLine();
            }

            TestContext.WriteLine(sb.ToString());
            Assert.Pass();
        }
    }
}
