using NUnit.Framework;
using UnityEngine;
using Basis.IK;

namespace Basis.Tests.IK
{
    /// <summary>
    /// AN ELBOW CANNOT POINT AT THE SKY.
    ///
    /// The report: "the arms are able to get in rotations that are not possible, I'm still seeing the
    /// occasional arm bending up (so the elbows point to the sky)."
    ///
    /// The KNEE has been hard-guarded into its anatomical half-space for a long time (BasisLegSolveCore:
    /// "a knee behind that axis is not unnatural, it is anatomically unrepresentable"). THE ARM HAD NO SUCH
    /// GUARD AT ALL -- nothing stopped the solver placing the elbow anywhere on its circle, including straight
    /// up. BasisElbowAnatomyCore closes that, and this file is the proof, in both directions:
    ///
    ///   * it makes the impossible pose UNREACHABLE, and
    ///   * it leaves every possible pose BYTE FOR BYTE untouched.
    ///
    /// The second half matters as much as the first. A guard that perturbs legal poses to fix illegal ones is
    /// the wrong trade, and it is how guards end up being deleted by the next person.
    /// </summary>
    public sealed class BasisElbowAnatomyTests
    {
        const float k_Upper = 0.30f;
        const float k_Lower = 0.30f;
        const float k_Arm = k_Upper + k_Lower;
        static readonly Vector3 k_Shoulder = new Vector3(0.17f, 1.40f, 0f);
        static readonly Vector3 k_Up = Vector3.up;

        /// <summary>A geometrically real elbow: on the true circle, at the true bone distances.</summary>
        static Vector3 ElbowAt(Vector3 hand, float swivelDeg)
        {
            Vector3 ac = hand - k_Shoulder;
            float d = ac.magnitude;
            Vector3 axis = ac / d;
            float a = (k_Upper * k_Upper - k_Lower * k_Lower + d * d) / (2f * d);
            float radius = Mathf.Sqrt(Mathf.Max(k_Upper * k_Upper - a * a, 0f));
            Vector3 centre = k_Shoulder + axis * a;

            Vector3 refDown = Vector3.down;
            Vector3 u = (refDown - axis * Vector3.Dot(refDown, axis)).normalized;
            Vector3 v = Vector3.Cross(axis, u);
            float rad = swivelDeg * Mathf.Deg2Rad;
            return centre + radius * (u * Mathf.Cos(rad) + v * Mathf.Sin(rad));
        }

        /// <summary>Applies the guard and returns where the elbow ends up.</summary>
        static Vector3 Guarded(Vector3 hand, Vector3 elbow)
        {
            float sw = BasisElbowAnatomyCore.GuardSwivelRad(k_Shoulder, elbow, hand, k_Up, k_Arm);
            if (sw == 0f) return elbow;
            Vector3 axis = (hand - k_Shoulder).normalized;
            return k_Shoulder + Quaternion.AngleAxis(sw * Mathf.Rad2Deg, axis) * (elbow - k_Shoulder);
        }

        /// <summary>
        /// The hand must be genuinely IN REACH, or the elbow's circle has collapsed to a point on the arm axis
        /// and there is no swivel that could put it anywhere -- so a guard test would "pass" without ever
        /// exercising the guard. That is exactly how the first version of this file reported safety while the
        /// bug it was written for was still live.
        /// </summary>
        static void RequireInReach(Vector3 hand)
        {
            float d = Vector3.Distance(k_Shoulder, hand);
            Assert.Less(d, 0.98f * k_Arm,
                $"the test's own hand is {d:F3} from a {k_Arm:F2} arm -- out of reach, so the elbow has no " +
                "circle to sit on and this test would prove nothing");
            float a = (k_Upper * k_Upper - k_Lower * k_Lower + d * d) / (2f * d);
            float radius = Mathf.Sqrt(Mathf.Max(k_Upper * k_Upper - a * a, 0f));
            Assert.Greater(radius, 0.02f,
                $"the elbow's circle has radius {radius:F4} -- too collapsed for this test to mean anything");
        }

        static float Ceiling(Vector3 hand)
        {
            float handUp = Vector3.Dot(hand - k_Shoulder, k_Up);
            return Mathf.Max(0f, handUp);
        }

        // ============================================================================================
        // 1. THE IMPOSSIBLE MUST BECOME UNREACHABLE.
        // ============================================================================================

        /// <summary>
        /// ⭐ THE REPORTED BUG. Hand at chest height, elbow commanded STRAIGHT UP -- the pose the user is
        /// seeing. The guard must pull it back under the ceiling, and must not move the hand doing it.
        /// </summary>
        [Test]
        public void AnElbowPointedAtTheSky_IsPulledBackUnderTheCeiling()
        {
            // ⚠ THE ELBOW CAN ONLY REACH THE SKY WITH THE ARM OUT TO THE SIDE, and that is not a quirk of the
            // test -- it is the geometry, and it agrees with the corpus. Drop your hand low and close and the
            // TOP of the elbow's whole circle still sits below your shoulder: the pose is unreachable, so
            // there is nothing to guard. (My first version of this test commanded "elbow at the sky" for a
            // hand at the waist, where the circle's summit is -0.010 -- it was asserting on a pose that cannot
            // exist.) The reachable-and-illegal case is the CHICKEN WING: hand out to the side, elbow up.
            // That is the pose the user is actually seeing.
            (Vector3 offset, string what)[] chickenWings =
            {
                (new Vector3(0.45f,  0.00f, 0.10f), "arm straight out to the side, hand at shoulder height"),
                (new Vector3(0.42f, -0.08f, 0.15f), "arm out to the side, hand just below the shoulder"),
                (new Vector3(0.40f, -0.12f, 0.20f), "arm out and slightly forward, hand below the shoulder"),
                (new Vector3(0.35f,  0.05f, 0.28f), "arm out and forward, hand at shoulder height"),
                (new Vector3(0.30f,  0.10f, 0.30f), "arm out and forward, hand above the shoulder"),
            };

            foreach ((Vector3 offset, string what) in chickenWings)
            {
                Vector3 hand = k_Shoulder + offset;
                RequireInReach(hand);

                // The elbow, driven to the very top of its circle: pointing at the sky.
                Vector3 sky = ElbowAt(hand, 180f);
                float skyH = Vector3.Dot(sky - k_Shoulder, k_Up);
                Assert.Greater(skyH, Ceiling(hand),
                    $"the test must start from a pose that is BOTH reachable AND illegal ({what}); the top of " +
                    $"the elbow's circle here is {skyH:F3} against a ceiling of {Ceiling(hand):F3}. If the " +
                    "circle cannot clear the ceiling, the guard has nothing to do and this proves nothing.");

                Vector3 got = Guarded(hand, sky);

                float h = Vector3.Dot(got - k_Shoulder, k_Up);
                float hard = Ceiling(hand) + BasisElbowAnatomyCore.HardMarginFracLimb * k_Arm;
                Assert.Less(h, hard,
                    $"an elbow commanded at the sky ({what}) must be pulled back inside the anatomical " +
                    $"envelope -- it ended at {h:F3}, the hard limit is {hard:F3}");

                // ...and it must still be a real elbow: the bone lengths are not negotiable.
                Assert.AreEqual(k_Upper, Vector3.Distance(k_Shoulder, got), 1e-3f, "upper-arm length must be preserved");
                Assert.AreEqual(k_Lower, Vector3.Distance(got, hand), 1e-3f, "forearm length must be preserved");
            }
        }

        /// <summary>THE HAND DOES NOT MOVE. The correction is a swivel about the shoulder->hand axis, and the
        /// hand lies on that axis -- so this is geometry, not a tolerance. Pinned so nobody ever "fixes" the
        /// elbow by trading the hand away.</summary>
        [Test]
        public void TheGuard_NeverMovesTheHand_AtAnyExtension()
        {
            var rng = new System.Random(90210);
            for (int t = 0; t < 400; t++)
            {
                float reach = Mathf.Lerp(0.35f, 0.999f, (float)rng.NextDouble());
                var dir = new Vector3(
                    (float)(rng.NextDouble() * 2 - 1),
                    (float)(rng.NextDouble() * 2 - 1),
                    (float)(rng.NextDouble() * 2 - 1));
                if (dir.sqrMagnitude < 1e-4f) continue;
                Vector3 hand = k_Shoulder + dir.normalized * (reach * k_Arm);

                float swivel = (float)(rng.NextDouble() * 360.0 - 180.0);
                Vector3 elbow = ElbowAt(hand, swivel);
                Vector3 got = Guarded(hand, elbow);

                Assert.AreEqual(k_Upper, Vector3.Distance(k_Shoulder, got), 1e-3f,
                    $"upper-arm length must survive the guard (iter {t})");
                Assert.AreEqual(k_Lower, Vector3.Distance(got, hand), 1e-3f,
                    $"the guard must not move the hand: forearm length changed (iter {t}, reach {reach:P0})");
            }
        }

        /// <summary>Sweep the elbow all the way round its circle: NO angle may end up above the hard limit.
        /// This is the "unreachable, not merely discouraged" property -- the same status the knee's guard has.
        /// </summary>
        [Test]
        public void NoPointOnTheCircle_CanEndUpAboveTheHardLimit()
        {
            foreach (float handY in new[] { -0.30f, -0.15f, 0f, 0.20f })
            {
                Vector3 hand = k_Shoulder + new Vector3(0.22f, handY, 0.30f);
                RequireInReach(hand);   // no circle, no test -- see the note above
                float hard = Ceiling(hand) + BasisElbowAnatomyCore.HardMarginFracLimb * k_Arm;

                for (float sw = -180f; sw < 180f; sw += 2f)
                {
                    Vector3 got = Guarded(hand, ElbowAt(hand, sw));
                    float h = Vector3.Dot(got - k_Shoulder, k_Up);
                    Assert.Less(h, hard + 1e-4f,
                        $"swivel {sw:F0} deg with hand y={handY:F2} escaped the envelope: elbow at {h:F3}, limit {hard:F3}");
                }
            }
        }

        // ============================================================================================
        // 2. THE POSSIBLE MUST BE UNTOUCHED -- exactly, not approximately.
        // ============================================================================================

        /// <summary>
        /// ⭐ THE OTHER HALF OF THE PROOF. Every pose a real human actually held must pass through the guard
        /// BIT FOR BIT. The margins were chosen from the corpus (worst real violation: 0.015 arm lengths; the
        /// soft margin is 0.05) precisely so this holds, and it is asserted rather than assumed.
        ///
        /// The poses below are the corpus's own measured extremes, taken from the envelope table in
        /// BasisElbowAnatomyCore: arms down, hands at waist, chest, face, and overhead -- each at the tightest
        /// headroom that bin produced.
        /// </summary>
        [Test]
        public void EveryPoseARealHumanHolds_PassesThroughUntouched()
        {
            // hand offset from shoulder (x out, y up, z fwd), and the elbow's swivel -- the natural, measured
            // human bend for that pose.
            (Vector3 hand, float swivel, string what)[] poses =
            {
                (new Vector3(0.05f, -0.55f,  0.02f),   5f, "arms hanging at the sides"),
                (new Vector3(0.12f, -0.42f,  0.18f),  20f, "hands at the waist"),
                (new Vector3(0.18f, -0.20f,  0.42f),  35f, "hands at the chest, reaching forward"),
                (new Vector3(0.28f, -0.05f,  0.38f),  55f, "hands at the chest, out to the side"),
                (new Vector3(0.20f,  0.15f,  0.35f),  70f, "hands up at the face"),
                (new Vector3(0.15f,  0.40f,  0.20f),  95f, "hands overhead"),
                (new Vector3(0.40f, -0.10f,  0.10f),  60f, "arm straight out to the side"),
                (new Vector3(0.05f, -0.30f, -0.25f),  15f, "hand behind the hip"),
            };

            foreach ((Vector3 offset, float swivel, string what) in poses)
            {
                Vector3 hand = k_Shoulder + offset;
                Vector3 elbow = ElbowAt(hand, swivel);

                float sw = BasisElbowAnatomyCore.GuardSwivelRad(k_Shoulder, elbow, hand, k_Up, k_Arm);
                Assert.AreEqual(0f, sw,
                    $"the guard must be the EXACT identity on a pose a human actually holds ({what}); " +
                    $"it returned {sw * Mathf.Rad2Deg:F3} deg. If this fires, the margins are too tight and " +
                    "the guard is bending legal poses to fix illegal ones.");
            }
        }

        /// <summary>The guard is SCALE-FREE: the margins are fractions of the arm, so a child avatar and a
        /// giant get the same posture, not the same centimetres.</summary>
        [Test]
        public void TheGuard_IsScaleFree()
        {
            Vector3 handOff = new Vector3(0.25f, -0.10f, 0.40f);
            Vector3 hand = k_Shoulder + handOff;
            Vector3 elbow = ElbowAt(hand, 175f);   // illegal: near the top of the circle

            float small = BasisElbowAnatomyCore.GuardSwivelRad(k_Shoulder, elbow, hand, k_Up, k_Arm);

            // The same pose on an avatar twice the size: every length doubles, so the SWIVEL must be identical.
            const float s = 2f;
            Vector3 shoulder2 = k_Shoulder;
            Vector3 hand2 = shoulder2 + handOff * s;
            Vector3 elbow2 = shoulder2 + (elbow - k_Shoulder) * s;
            float big = BasisElbowAnatomyCore.GuardSwivelRad(shoulder2, elbow2, hand2, k_Up, k_Arm * s);

            Assert.AreEqual(small, big, 1e-4f,
                "the same posture on a bigger avatar must produce the same corrective swivel");
        }

        // ============================================================================================
        // 3. IT MUST NOT BLOW UP.
        // ============================================================================================

        [Test]
        public void DegenerateAndNaNInputs_DeclineRatherThanCorrupt()
        {
            Vector3 hand = k_Shoulder + new Vector3(0.25f, -0.10f, 0.40f);

            // A straight arm: the circle has collapsed, there is no swivel that moves the elbow anywhere.
            Vector3 straightHand = k_Shoulder + new Vector3(k_Arm, 0f, 0f);
            Vector3 straightElbow = k_Shoulder + new Vector3(k_Upper, 0f, 0f);
            Assert.AreEqual(0f, BasisElbowAnatomyCore.GuardSwivelRad(k_Shoulder, straightElbow, straightHand, k_Up, k_Arm),
                "a straight arm has nothing to guard");

            // Hand on top of the shoulder: no axis.
            Assert.AreEqual(0f, BasisElbowAnatomyCore.GuardSwivelRad(k_Shoulder, ElbowAt(hand, 90f), k_Shoulder, k_Up, k_Arm),
                "a zero-length arm axis must decline");

            // Arm pointing straight UP: the elbow's circle is horizontal, so no swivel changes its height.
            Vector3 upHand = k_Shoulder + new Vector3(0f, 0.55f, 0f);
            Assert.AreEqual(0f, BasisElbowAnatomyCore.GuardSwivelRad(k_Shoulder, ElbowAt(upHand, 40f), upHand, k_Up, k_Arm),
                "a vertical arm axis makes the constraint inexpressible as a swivel; it must decline, not guess");

            // NaN in, zero out. A NaN transform PERSISTS in Unity -- the arm would never recover.
            var nan = new Vector3(float.NaN, 0f, 0f);
            Assert.AreEqual(0f, BasisElbowAnatomyCore.GuardSwivelRad(k_Shoulder, nan, hand, k_Up, k_Arm));
            Assert.AreEqual(0f, BasisElbowAnatomyCore.GuardSwivelRad(k_Shoulder, ElbowAt(hand, 90f), nan, k_Up, k_Arm));
            Assert.AreEqual(0f, BasisElbowAnatomyCore.GuardSwivelRad(k_Shoulder, ElbowAt(hand, 90f), hand, nan, k_Arm));
            Assert.AreEqual(0f, BasisElbowAnatomyCore.GuardSwivelRad(k_Shoulder, ElbowAt(hand, 90f), hand, k_Up, float.NaN));
            Assert.AreEqual(0f, BasisElbowAnatomyCore.GuardSwivelRad(k_Shoulder, ElbowAt(hand, 90f), hand, k_Up, 0f));
        }

        // ============================================================================================
        // 4. END TO END, THROUGH THE REAL SOLVER.
        // ============================================================================================

        /// <summary>
        /// The guard lives at the END of BasisArmSolveCore, so nothing -- not a hint, not a tracker, not the
        /// pole-collapse stabilizer, not the animated pose the solve began from -- can leave the arm outside
        /// the envelope. Drive the solver with a hint that DEMANDS a sky-pointing elbow and check it cannot
        /// deliver one.
        /// </summary>
        [Test]
        public void TheSolver_RefusesASkyPointingHint_AndKeepsTheHandOnTarget()
        {
            Vector3 hand = k_Shoulder + new Vector3(0.25f, -0.15f, 0.42f);

            // A hint demanding the elbow at the top of its circle.
            Vector3 skyElbow = ElbowAt(hand, 180f);
            Vector3 skyHint = k_Shoulder + (skyElbow - k_Shoulder).normalized * (0.5f * k_Arm);

            BasisArmSolveInput i = default;
            i.Shoulder = k_Shoulder;
            i.Elbow = ElbowAt(hand, 20f);     // a sane animated start
            i.Hand = hand;
            i.RootRotation = Quaternion.identity;
            i.MidRotation = Quaternion.identity;
            i.TargetPosition = hand;
            i.TargetRotation = Quaternion.identity;
            i.TargetOffset = Quaternion.identity;
            i.HintPosition = skyHint;
            i.HintWeight = true;
            i.HintIsTracker = false;
            i.HintMaxStepDeg = float.MaxValue;
            i.PlayerUp = k_Up;

            BasisArmSolveCore.Solve(i, out BasisArmSolveResult r);

            float h = Vector3.Dot(r.ElbowSolved - k_Shoulder, k_Up);
            float hard = Ceiling(hand) + BasisElbowAnatomyCore.HardMarginFracLimb * k_Arm;

            Assert.Less(h, hard,
                $"even handed a hint that explicitly demands a sky-pointing elbow, the solver must not deliver " +
                $"one -- it ended at {h:F3} against a hard limit of {hard:F3}");
            Assert.AreEqual(0f, Vector3.Distance(r.HandSolved, hand), 2e-3f,
                "and the hand must still be exactly on its target: the guard is a swivel about the " +
                "shoulder->hand axis, so it cannot move the hand");
        }
    }
}
