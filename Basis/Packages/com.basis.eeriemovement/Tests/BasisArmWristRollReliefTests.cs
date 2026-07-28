using NUnit.Framework;
using UnityEngine;
using Basis.IK;

namespace Basis.Tests.IK
{
    /// <summary>
    /// Hand ROLL must recruit the upper arm — with or without an elbow tracker.
    ///
    /// THE GAP THIS EXISTS TO CLOSE. The tip is written straight to the target rotation, so every degree of
    /// controller roll used to land in the wrist joint; the twist bones only redistribute it along the
    /// forearm MESH. Nothing upstream read the hand's roll at all — the elbow field model is position-only,
    /// and a hand twisted IN PLACE cannot move a position-only elbow. Twist your hand and the upper arm sat
    /// rigid while the wrist candy-wrapped. A real arm pronates ~80° each way and then ROTATES THE HUMERUS,
    /// which with the hand pinned is exactly the swivel DOF: the elbow swings around its circle.
    ///
    /// WHAT THE RELIEF PROMISES, AND WHAT THESE TESTS HOLD IT TO:
    ///   - the roll is measured against the ANIMATED wrist carried onto the SOLVED forearm, so a neutral
    ///     hand is a structural no-op, and whatever swivel the hint already applied is already relieved
    ///     before it is measured (nothing double-compensates);
    ///   - comfortable roll is the forearm's alone: zero relief below the ramp, then a C1 quadratic ramp
    ///     that reaches slope 1 exactly at the band edge, the excess past it 1:1, capped, and faded to zero
    ///     approaching the ±180° principal-angle seam so the two sides of the same pose meet at zero.
    ///     (A flat in-band share was corpus-refuted: it fed wrist jitter to the elbow as >8 Hz buzz.);
    ///   - NOT on the tracker path: a tracker is the user's real elbow and the real humerus already
    ///     answered — corpus-measured, relieving on top dragged the elbow 3.6-10.1 cm off a TRUE pole;
    ///   - the relief is a swivel about shoulder→hand, so the hand CANNOT leave its target (geometry, not
    ///     tolerance), and the anatomy guard still has the last word on where the elbow may sit.
    ///
    /// The relief's sign is an identity, not a convention (swivelling the arm rolls the carried neutral with
    /// it), so pronation flares the elbow OUT on BOTH arms with no handedness flag — the mirror test below
    /// is what makes that claim falsifiable.
    /// </summary>
    public class BasisArmWristRollReliefTests
    {
        const float UpperLen = 0.30f, ForeLen = 0.30f;
        const float ArmLen = UpperLen + ForeLen;
        static readonly Vector3 BoneAxis = Vector3.right;   // shoulder→elbow in the bone's own frame (T-pose arm)
        static readonly Vector3 Shoulder = Vector3.zero;

        const float Band = BasisArmSolveCore.WristRollComfortDeg;        // 80
        const float Ramp = BasisArmSolveCore.WristRollRampStartDeg;      // 55
        const float Cap = BasisArmSolveCore.WristRollMaxReliefDeg;       // 70

        /// <summary>The relief curve, restated: zero to the ramp, quadratic to the band, linear past it.</summary>
        static float ExpectedRelief(float rollDeg)
        {
            float a = Mathf.Abs(rollDeg);
            float m;
            if (a <= Ramp) m = 0f;
            else if (a <= Band) m = (a - Ramp) * (a - Ramp) / (2f * (Band - Ramp));
            else m = 0.5f * (Band - Ramp) + (a - Band);
            return Mathf.Sign(rollDeg) * Mathf.Min(m, Cap);
        }

        /// <summary>A RIGHT arm reaching `straight`, elbow bulged toward `bulge`, hand exactly on target and
        /// the animated hand riding the forearm (carried wrist local = identity), so TargetRotation == the
        /// carried neutral and any roll applied to it is EXACTLY the twist the relief should measure.</summary>
        static BasisArmSolveInput Pose(Vector3 straight, Vector3 bulge, float halfFlexDeg)
        {
            float f = halfFlexDeg * Mathf.Deg2Rad;
            Vector3 upperDir = (straight * Mathf.Cos(f) + bulge * Mathf.Sin(f)).normalized;
            Vector3 lowerDir = (straight * Mathf.Cos(f) - bulge * Mathf.Sin(f)).normalized;

            BasisArmSolveInput i = default;
            i.Shoulder = Shoulder;
            i.Elbow = Shoulder + upperDir * UpperLen;
            i.Hand = i.Elbow + lowerDir * ForeLen;
            i.RootRotation = Quaternion.FromToRotation(BoneAxis, upperDir);
            i.MidRotation = Quaternion.FromToRotation(BoneAxis, lowerDir);
            i.TipRotation = i.MidRotation;
            i.TargetPosition = i.Hand;
            i.TargetRotation = i.MidRotation;
            i.TargetOffset = Quaternion.identity;
            i.PlayerUp = Vector3.up;
            i.HintMaxStepDeg = float.MaxValue;
            return i;
        }

        static void Roll(ref BasisArmSolveInput i, float rollDeg)
        {
            Vector3 foreDir = (i.Hand - i.Elbow).normalized;
            i.TargetRotation = Quaternion.AngleAxis(rollDeg, foreDir) * i.MidRotation;
        }

        /// <summary>Signed elbow travel around the shoulder→hand circle, animated → solved. This is the ONLY
        /// motion the relief is allowed to produce.</summary>
        static float SwivelDeg(in BasisArmSolveInput i, in BasisArmSolveResult r)
        {
            Vector3 acN = (r.HandSolved - i.Shoulder).normalized;
            Vector3 before = i.Elbow - i.Shoulder;
            before -= acN * Vector3.Dot(before, acN);
            Vector3 after = r.ElbowSolved - i.Shoulder;
            after -= acN * Vector3.Dot(after, acN);
            return Vector3.SignedAngle(before, after, acN);
        }

        /// <summary>The residual roll left in the wrist after the solve: target vs the animated wrist carried
        /// onto the SOLVED forearm — the same quantity the relief measures, recomputed from the outputs.</summary>
        static float ResidualRollDeg(in BasisArmSolveInput i, in BasisArmSolveResult r)
        {
            Quaternion neutral = r.MidRotationSolved * Quaternion.Inverse(i.MidRotation) * i.TipRotation;
            Quaternion rel = i.TargetRotation * Quaternion.Inverse(neutral);
            if (rel.w < 0f) rel = new Quaternion(-rel.x, -rel.y, -rel.z, -rel.w);
            Vector3 foreDir = (r.HandSolved - r.ElbowSolved).normalized;
            Vector3 v = new Vector3(rel.x, rel.y, rel.z);
            return 2f * Mathf.Atan2(Vector3.Dot(v, foreDir), rel.w) * Mathf.Rad2Deg;
        }

        static void AssertHandOnTarget(in BasisArmSolveResult r, string when)
        {
            Assert.That(r.HandError, Is.LessThan(1e-4f),
                $"the relief is a swivel about shoulder→hand, so the hand must stay ON target ({when})");
        }

        [Test]
        public void NeutralRoll_IsAnExactNoOp()
        {
            BasisArmSolveInput i = Pose(Vector3.forward, Vector3.down, 25f);
            BasisArmSolveCore.Solve(i, out BasisArmSolveResult r);

            Assert.That(r.WristReliefDeg, Is.EqualTo(0f), "no roll demanded → the relief must not engage");
            Assert.That(Mathf.Abs(SwivelDeg(i, r)), Is.LessThan(0.01f), "a neutral hand must not move the elbow");
            AssertHandOnTarget(r, "neutral");
        }

        [Test]
        public void ZeroTipRotation_DisablesTheRelief()
        {
            BasisArmSolveInput i = Pose(Vector3.forward, Vector3.down, 25f);
            Roll(ref i, 130f);
            i.TipRotation = default;   // what every offline caller that predates the field passes

            BasisArmSolveCore.Solve(i, out BasisArmSolveResult r);

            Assert.That(r.WristReliefDeg, Is.EqualTo(0f), "a zero TipRotation is the documented off-switch");
            Assert.That(Mathf.Abs(SwivelDeg(i, r)), Is.LessThan(0.01f),
                "with the relief off, a rolled target must not move the elbow (the pre-feature behaviour)");
        }

        [TestCase(40f)]
        [TestCase(-40f)]
        public void ComfortableRoll_IsTheForearmsAlone(float rollDeg)
        {
            BasisArmSolveInput i = Pose(Vector3.forward, Vector3.down, 25f);
            Roll(ref i, rollDeg);

            BasisArmSolveCore.Solve(i, out BasisArmSolveResult r);

            Assert.That(r.WristTwistDeg, Is.EqualTo(rollDeg).Within(0.5f), "the measured roll IS the applied roll here");
            Assert.That(r.WristReliefDeg, Is.EqualTo(0f), "below the ramp the humerus must not stir");
            Assert.That(Mathf.Abs(SwivelDeg(i, r)), Is.LessThan(0.01f),
                "comfortable roll is rendered by the forearm and wrist alone — the corpus refuted a flat share");
            AssertHandOnTarget(r, $"roll {rollDeg}");
        }

        [TestCase(70f)]
        [TestCase(-70f)]
        [TestCase(120f)]
        [TestCase(-120f)]
        public void PastTheRamp_TheHumerusRecruits_OnTheCurve(float rollDeg)
        {
            BasisArmSolveInput i = Pose(Vector3.forward, Vector3.down, 25f);
            Roll(ref i, rollDeg);

            BasisArmSolveCore.Solve(i, out BasisArmSolveResult r);

            Assert.That(SwivelDeg(i, r), Is.EqualTo(ExpectedRelief(rollDeg)).Within(1f),
                "the humerus recruits on the ramp-then-linear curve, in the roll's own direction");
            Assert.That(Mathf.Abs(ResidualRollDeg(i, r)), Is.LessThan(Mathf.Abs(rollDeg)),
                "recruiting the humerus must RELIEVE the wrist, not just move the elbow");
            AssertHandOnTarget(r, $"roll {rollDeg}");
        }

        [Test]
        public void BeyondTheBand_TheWristComesBackInsideIt()
        {
            BasisArmSolveInput i = Pose(Vector3.forward, Vector3.down, 25f);
            Roll(ref i, 120f);

            BasisArmSolveCore.Solve(i, out BasisArmSolveResult r);

            Assert.That(Mathf.Abs(ResidualRollDeg(i, r)), Is.LessThan(Band),
                "the whole point: the wrist must be brought back inside what a forearm can render");
            AssertHandOnTarget(r, "roll 120");
        }

        /// <summary>A tracker is the user's REAL elbow: twist your hand and the tracker moves — the
        /// compensation arrives through the measurement. Relieving on top of it was corpus-measured to drag
        /// the elbow 3.6-10.1 cm off a pole it had been HANDED, so on this path the relief stands down.</summary>
        [Test]
        public void Tracker_TheMeasuredElbowIsNeverSecondGuessed()
        {
            BasisArmSolveInput i = Pose(Vector3.forward, Vector3.down, 25f);
            i.HintWeight = true;
            i.HintIsTracker = true;
            i.HintPosition = i.Elbow;   // the tracker agrees with the animated elbow

            foreach (float roll in new[] { 40f, 140f, -140f })
            {
                Roll(ref i, roll);
                BasisArmSolveCore.Solve(i, out BasisArmSolveResult r);
                Assert.That(r.WristReliefDeg, Is.EqualTo(0f), $"relief must stand down for a tracker (roll {roll})");
                Assert.That(Mathf.Abs(SwivelDeg(i, r)), Is.LessThan(0.1f),
                    $"a measured elbow is not to be second-guessed (roll {roll})");
                AssertHandOnTarget(r, $"tracker roll {roll}");
            }
        }

        /// <summary>The tracker is strapped to the forearm, so its ROTATION carries real pronation. The
        /// forearm rolls to the blend of that measurement and the hand's demand, so the wrist keeps only a
        /// real wrist's residual instead of pinching — and because a roll about the forearm's own long axis
        /// moves no joint, the elbow stays exactly on the tracker's pole while it happens.</summary>
        [Test]
        public void Tracker_ForearmRollsWithTheMeasurement_AndTheWristStopsPinching()
        {
            BasisArmSolveInput i = Pose(Vector3.forward, Vector3.down, 25f);
            i.HintWeight = true;
            i.HintIsTracker = true;
            i.HintPosition = i.Elbow;
            Vector3 foreDir = (i.Hand - i.Elbow).normalized;
            i.HintRotation = Quaternion.AngleAxis(60f, foreDir) * i.MidRotation;   // the tracker measures 60° of pronation
            Roll(ref i, 80f);                                                      // the hand demands 80°

            BasisArmSolveCore.Solve(i, out BasisArmSolveResult r);

            float expected = 60f + BasisArmSolveCore.TrackerRollHandBlend * (80f - 60f);   // 70
            Assert.That(r.ForearmRollDeg, Is.EqualTo(expected).Within(0.5f),
                "the forearm rolls to the blend of the tracker's measured roll and the hand's demand");
            Assert.That(Mathf.Abs(ResidualRollDeg(i, r)), Is.LessThan(80f - expected + 0.5f),
                "the wrist must keep only the residual — that is the pinch, relieved");
            Assert.That(Vector3.Distance(r.ElbowSolved, i.Elbow), Is.LessThan(1e-5f),
                "a pure forearm roll may not move the elbow off the tracker's pole");
            Assert.That(r.WristReliefDeg, Is.EqualTo(0f), "the roll must not smuggle the swivel relief back in");
            AssertHandOnTarget(r, "tracker forearm roll");
        }

        [Test]
        public void Tracker_MeasuredRollAlone_WhenTheHandFeedIsAbsent()
        {
            BasisArmSolveInput i = Pose(Vector3.forward, Vector3.down, 25f);
            i.HintWeight = true;
            i.HintIsTracker = true;
            i.HintPosition = i.Elbow;
            Vector3 foreDir = (i.Hand - i.Elbow).normalized;
            i.HintRotation = Quaternion.AngleAxis(-45f, foreDir) * i.MidRotation;
            i.TipRotation = default;   // no animated wrist feed: the hand gets no say

            BasisArmSolveCore.Solve(i, out BasisArmSolveResult r);

            Assert.That(r.ForearmRollDeg, Is.EqualTo(-45f).Within(0.5f),
                "with no hand feed the forearm follows the measurement outright");
            Assert.That(Vector3.Distance(r.ElbowSolved, i.Elbow), Is.LessThan(1e-5f),
                "still a pure roll: the elbow stays on the pole");
        }

        [Test]
        public void Tracker_NoHintRotation_NoForearmRoll()
        {
            BasisArmSolveInput i = Pose(Vector3.forward, Vector3.down, 25f);
            i.HintWeight = true;
            i.HintIsTracker = true;
            i.HintPosition = i.Elbow;
            Roll(ref i, 80f);   // HintRotation stays zero — the documented off-switch

            BasisArmSolveCore.Solve(i, out BasisArmSolveResult r);

            Assert.That(r.ForearmRollDeg, Is.EqualTo(0f), "a zero HintRotation must disable the forearm roll");
            Assert.That(Quaternion.Angle(r.MidPostRoll, Quaternion.identity), Is.LessThan(1e-4f),
                "MidPostRoll must be a valid identity for the runtime to multiply through");
        }

        /// <summary>Pronation must flare the elbow toward the OUTWARD side on BOTH arms. The core has no
        /// handedness input, so this is asserted by mirroring the entire problem through the YZ plane and
        /// requiring the answer to mirror with it — the test that catches any hidden world-axis dependence.</summary>
        [Test]
        public void MirroredArm_MirrorsTheRelief()
        {
            BasisArmSolveInput right = Pose(Vector3.forward, Vector3.down, 25f);
            Roll(ref right, 120f);
            BasisArmSolveCore.Solve(right, out BasisArmSolveResult rR);

            BasisArmSolveInput left = right;
            left.Shoulder = MirrorV(right.Shoulder);
            left.Elbow = MirrorV(right.Elbow);
            left.Hand = MirrorV(right.Hand);
            left.TargetPosition = MirrorV(right.TargetPosition);
            left.HintPosition = MirrorV(right.HintPosition);
            left.PlayerUp = MirrorV(right.PlayerUp);
            left.RootRotation = MirrorQ(right.RootRotation);
            left.MidRotation = MirrorQ(right.MidRotation);
            left.TipRotation = MirrorQ(right.TipRotation);
            left.TargetRotation = MirrorQ(right.TargetRotation);
            left.TargetOffset = MirrorQ(right.TargetOffset);
            left.HintRotation = MirrorQ(right.HintRotation);
            BasisArmSolveCore.Solve(left, out BasisArmSolveResult rL);

            Vector3 expected = MirrorV(rR.ElbowSolved);
            Assert.That(Vector3.Distance(rL.ElbowSolved, expected), Is.LessThan(1e-4f),
                "a mirrored problem must produce the mirrored elbow — pronation flares OUT on both arms");

            // And on the right arm the flare really is OUTWARD (+X for a right arm reaching +Z from an
            // elbow-down rest): the anatomical direction, pinned in absolute terms exactly once.
            Assert.That(rR.ElbowSolved.x, Is.GreaterThan(right.Elbow.x + 0.05f),
                "pronating past the band must flare a right elbow toward +X (out), not across the body");
        }

        [Test]
        public void Continuity_AcrossTheBandEdge_AndTheWrapSeam()
        {
            float prev = 0f;
            bool seeded = false;
            for (float roll = 60f; roll <= 100f; roll += 1f)
            {
                BasisArmSolveInput i = Pose(Vector3.forward, Vector3.down, 25f);
                Roll(ref i, roll);
                BasisArmSolveCore.Solve(i, out BasisArmSolveResult r);
                float s = SwivelDeg(i, r);
                if (seeded)
                {
                    Assert.That(Mathf.Abs(s - prev), Is.LessThan(1.5f),
                        $"crossing the band edge must not step the elbow (roll {roll})");
                }
                AssertHandOnTarget(r, $"sweep roll {roll}");
                prev = s; seeded = true;
            }

            seeded = false;
            for (float roll = 150f; roll <= 179.5f; roll += 0.5f)
            {
                BasisArmSolveInput i = Pose(Vector3.forward, Vector3.down, 25f);
                Roll(ref i, roll);
                BasisArmSolveCore.Solve(i, out BasisArmSolveResult r);
                float s = SwivelDeg(i, r);
                Assert.That(Mathf.Abs(s), Is.LessThanOrEqualTo(Cap + 0.5f), "the relief must respect its cap");
                if (seeded)
                {
                    Assert.That(Mathf.Abs(s - prev), Is.LessThan(3f),
                        $"the wrap fade must be a fade, not a step (roll {roll})");
                }
                prev = s; seeded = true;
            }

            // The seam itself: +179.5° and -179.5° are one hand pose. Both must answer ~zero.
            BasisArmSolveInput a = Pose(Vector3.forward, Vector3.down, 25f);
            Roll(ref a, 179.5f);
            BasisArmSolveCore.Solve(a, out BasisArmSolveResult ra);
            BasisArmSolveInput b = Pose(Vector3.forward, Vector3.down, 25f);
            Roll(ref b, -179.5f);
            BasisArmSolveCore.Solve(b, out BasisArmSolveResult rb);
            Assert.That(Mathf.Abs(SwivelDeg(a, ra)), Is.LessThan(1f), "the +180 side of the seam must fade to zero");
            Assert.That(Mathf.Abs(SwivelDeg(b, rb)), Is.LessThan(1f), "the -180 side of the seam must fade to zero");
        }

        /// <summary>The guard still owns the outcome: start the elbow OUT (horizontal) so a big pronation
        /// flare heads for the sky, and require the anatomy ceiling to hold anyway.</summary>
        [Test]
        public void TheAnatomyGuard_StillHasTheLastWord()
        {
            BasisArmSolveInput i = Pose(Vector3.forward, Vector3.right, 25f);   // elbow bulged OUT, hand at shoulder height
            Roll(ref i, 150f);

            BasisArmSolveCore.Solve(i, out BasisArmSolveResult r);

            Assert.That(r.WristReliefDeg, Is.GreaterThan(0f), "the flare this test guards against must actually be demanded");
            float ceiling = Mathf.Max(i.Shoulder.y, r.HandSolved.y);
            Assert.That(r.ElbowSolved.y, Is.LessThan(ceiling + BasisElbowAnatomyCore.HardMarginFracLimb * ArmLen),
                "however hard the wrist asks, the elbow may not cross the anatomy ceiling's asymptote");
            AssertHandOnTarget(r, "guarded flare");
        }

        static Vector3 MirrorV(Vector3 v) => new Vector3(-v.x, v.y, v.z);
        static Quaternion MirrorQ(Quaternion q) => new Quaternion(q.x, -q.y, -q.z, q.w);

        /// <summary>
        /// An elbow tracker is rigid on the forearm: trackerRot(t) = foreRot(t) * K, with K the strap offset.
        /// K's clock angle around the limb is ARBITRARY and carries no pronation information, so it must not
        /// reach the forearm. Calibration stores Qref = Inverse(trackerWorld) * boneWorld = Inverse(K), and the
        /// rig driver feeds trackerRot_live * Qref, which recovers the true forearm for any K.
        ///
        /// Measured before the mapping was added: a 90 deg strap angle produced 80.6 deg of forearm roll at zero
        /// pronation, swinging the wrist residual from +26.4 to -54.2 deg. LowerArm has no Recalibrated* offset,
        /// so nothing else in the pipeline was cancelling it.
        /// </summary>
        /// The invariant is CLOCK-INDEPENDENCE, not an absolute roll: unlike the leg (which rolls the shin
        /// straight to the tracker), the arm deliberately blends the measured roll with the hand's demand at
        /// TrackerRollHandBlend, so the applied roll is a blend and asserting a raw pronation figure would be
        /// testing the blend constant rather than the calibration.
        [Test]
        public void ElbowStrapClockAngle_DoesNotReachTheForearm([Values(0f, 25f)] float pronationDeg)
        {
            BasisArmSolveInput baseline = Pose(Vector3.down, Vector3.forward, 20f);
            baseline.HintPosition = baseline.Elbow;
            baseline.HintWeight = true;
            baseline.HintIsTracker = true;
            BasisArmSolveCore.Solve(baseline, out BasisArmSolveResult off);

            Vector3 foreAxis = (off.HandSolved - off.ElbowSolved).normalized;
            Quaternion trueFore = Quaternion.AngleAxis(pronationDeg, foreAxis) * off.MidRotationSolved;

            float reference = float.NaN;
            foreach (float clockDeg in new[] { 0f, 30f, 90f, -60f })
            {
                Quaternion K = Quaternion.AngleAxis(clockDeg, foreAxis) * Quaternion.AngleAxis(17f, Vector3.right);
                Quaternion qRef = Quaternion.Inverse(off.MidRotationSolved * K) * off.MidRotationSolved;

                BasisArmSolveInput i = baseline;
                i.HintRotation = (trueFore * K) * qRef;
                BasisArmSolveCore.Solve(i, out BasisArmSolveResult on);

                if (float.IsNaN(reference)) reference = on.ForearmRollDeg;

                Assert.That(on.ForearmRollDeg, Is.EqualTo(reference).Within(0.5f),
                    $"a {clockDeg:F0} deg strap clock angle changed the forearm roll ({on.ForearmRollDeg:F2} vs {reference:F2}) " +
                    $"-- the mounting angle carries no pronation information and must cancel");
            }

            // And the roll must still RESPOND to genuine pronation, or the calibration has simply killed it.
            if (pronationDeg > 0f)
            {
                Assert.That(Mathf.Abs(reference), Is.GreaterThan(1f),
                    "real pronation must still reach the forearm after the mapping");
            }
            else
            {
                Assert.That(Mathf.Abs(reference), Is.LessThan(1f),
                    "with no pronation and only a strap offset, nothing should reach the forearm");
            }
        }

        /// <summary>
        /// A user who never calibrated has no BasisLimbRollStore entry, so the rig driver feeds the zero
        /// quaternion and the feature must switch itself off. Before the mapping the driver fed a raw unit
        /// quaternion unconditionally, so this path could not exist.
        /// </summary>
        [Test]
        public void NoCalibrationReference_DisablesTheTrackerForearmRoll()
        {
            BasisArmSolveInput i = Pose(Vector3.down, Vector3.forward, 20f);
            i.HintPosition = i.Elbow;
            i.HintWeight = true;
            i.HintIsTracker = true;
            i.HintRotation = default;

            BasisArmSolveCore.Solve(i, out BasisArmSolveResult r);

            Assert.That(r.ForearmRollDeg, Is.EqualTo(0f), "an uncalibrated tracker must not roll the forearm");
            Assert.That(r.MidPostRoll.w, Is.EqualTo(1f), "MidPostRoll must be identity, never the zero quaternion");
        }
    }
}
