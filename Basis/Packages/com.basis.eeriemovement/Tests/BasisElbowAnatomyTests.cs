using NUnit.Framework;
using UnityEngine;
using Basis.IK;
namespace Basis.Tests.IK
{
    public sealed class BasisElbowAnatomyTests
    {
        const float upper = 0.30f, lower = 0.30f, arm = upper + lower;
        static readonly Vector3 shoulder = new Vector3(0.17f, 1.40f, 0f), k_Up = Vector3.up;
        static Vector3 ElbowAt(Vector3 hand, float swivelDeg)
        {
            Vector3 ac = hand - shoulder;
            float d = ac.magnitude;
            Vector3 axis = ac / d;
            float a = (upper * upper - lower * lower + d * d) / (2f * d);
            float radius = Mathf.Sqrt(Mathf.Max(upper * upper - a * a, 0f));
            Vector3 centre = shoulder + axis * a, refDown = Vector3.down;
            Vector3 u = (refDown - axis * Vector3.Dot(refDown, axis)).normalized, v = Vector3.Cross(axis, u);
            float rad = swivelDeg * Mathf.Deg2Rad;
            return centre + radius * (u * Mathf.Cos(rad) + v * Mathf.Sin(rad));
        }
        static Vector3 Guarded(Vector3 hand, Vector3 elbow)
        {
            float sw = BasisElbowAnatomyCore.GuardSwivelRad(shoulder, elbow, hand, k_Up, arm);
            if (sw == 0f) return elbow;
            Vector3 axis = (hand - shoulder).normalized;
            return shoulder + Quaternion.AngleAxis(sw * Mathf.Rad2Deg, axis) * (elbow - shoulder);
        }
        static void RequireInReach(Vector3 hand)
        {
            float d = Vector3.Distance(shoulder, hand);
            Assert.Less(d, 0.98f * arm, $"the test's own hand is {d:F3} from a {arm:F2} arm -- out of reach, so the elbow has no " +"circle to sit on and this test would prove nothing");
            float a = (upper * upper - lower * lower + d * d) / (2f * d);
            float radius = Mathf.Sqrt(Mathf.Max(upper * upper - a * a, 0f));
            Assert.Greater(radius, 0.02f, $"the elbow's circle has radius {radius:F4} -- too collapsed for this test to mean anything");
        }
        static float Ceiling(Vector3 hand)
        {
            float handUp = Vector3.Dot(hand - shoulder, k_Up);
            return Mathf.Max(0f, handUp);
        }
        // ============================================================================================
        // 1. THE IMPOSSIBLE MUST BECOME UNREACHABLE.
        // ============================================================================================
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
                Vector3 hand = shoulder + offset;
                RequireInReach(hand);

                // The elbow, driven to the very top of its circle: pointing at the sky.
                Vector3 sky = ElbowAt(hand, 180f);
                float skyH = Vector3.Dot(sky - shoulder, k_Up);
                Assert.Greater(skyH, Ceiling(hand), $"the test must start from a pose that is BOTH reachable AND illegal ({what}); the top of " + $"the elbow's circle here is {skyH:F3} against a ceiling of {Ceiling(hand):F3}. If the " +"circle cannot clear the ceiling, the guard has nothing to do and this proves nothing.");

                Vector3 got = Guarded(hand, sky);
                float h = Vector3.Dot(got - shoulder, k_Up);
                float hard = Ceiling(hand) + BasisElbowAnatomyCore.HardMarginFracLimb * arm;
                Assert.Less(h, hard, $"an elbow commanded at the sky ({what}) must be pulled back inside the anatomical " + $"envelope -- it ended at {h:F3}, the hard limit is {hard:F3}");

                // ...and it must still be a real elbow: the bone lengths are not negotiable.
                Assert.AreEqual(upper, Vector3.Distance(shoulder, got), 1e-3f, "upper-arm length must be preserved");
                Assert.AreEqual(lower, Vector3.Distance(got, hand), 1e-3f, "forearm length must be preserved");
            }
        }
        [Test]
        public void TheGuard_NeverMovesTheHand_AtAnyExtension()
        {
            var rng = new System.Random(90210);
            for (int t = 0; t < 400; t++)
            {
                float reach = Mathf.Lerp(0.35f, 0.999f, (float)rng.NextDouble());
                var dir = new Vector3((float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1));
                if (dir.sqrMagnitude < 1e-4f) continue;
                Vector3 hand = shoulder + dir.normalized * (reach * arm);
                float swivel = (float)(rng.NextDouble() * 360.0 - 180.0);
                Vector3 elbow = ElbowAt(hand, swivel), got = Guarded(hand, elbow);

                Assert.AreEqual(upper, Vector3.Distance(shoulder, got), 1e-3f, $"upper-arm length must survive the guard (iter {t})");
                Assert.AreEqual(lower, Vector3.Distance(got, hand), 1e-3f, $"the guard must not move the hand: forearm length changed (iter {t}, reach {reach:P0})");
            }
        }
        [Test]
        public void NoPointOnTheCircle_CanEndUpAboveTheHardLimit()
        {
            foreach (float handY in new[] { -0.30f, -0.15f, 0f, 0.20f })
            {
                Vector3 hand = shoulder + new Vector3(0.22f, handY, 0.30f);
                RequireInReach(hand);   // no circle, no test -- see the note above
                float hard = Ceiling(hand) + BasisElbowAnatomyCore.HardMarginFracLimb * arm;

                for (float sw = -180f; sw < 180f; sw += 2f)
                {
                    Vector3 got = Guarded(hand, ElbowAt(hand, sw));
                    float h = Vector3.Dot(got - shoulder, k_Up);
                    Assert.Less(h, hard + 1e-4f, $"swivel {sw:F0} deg with hand y={handY:F2} escaped the envelope: elbow at {h:F3}, limit {hard:F3}");
                }
            }
        }
        // ============================================================================================
        // 2. THE POSSIBLE MUST BE UNTOUCHED -- exactly, not approximately.
        // ============================================================================================
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
                Vector3 hand = shoulder + offset, elbow = ElbowAt(hand, swivel);
                float sw = BasisElbowAnatomyCore.GuardSwivelRad(shoulder, elbow, hand, k_Up, arm);
                Assert.AreEqual(0f, sw, $"the guard must be the EXACT identity on a pose a human actually holds ({what}); " + $"it returned {sw * Mathf.Rad2Deg:F3} deg. If this fires, the margins are too tight and " +"the guard is bending legal poses to fix illegal ones.");
            }
        }
        [Test]
        public void TheGuard_IsScaleFree()
        {
            Vector3 handOff = new Vector3(0.25f, -0.10f, 0.40f), hand = shoulder + handOff;
            Vector3 elbow = ElbowAt(hand, 175f);   // illegal: near the top of the circle

            float small = BasisElbowAnatomyCore.GuardSwivelRad(shoulder, elbow, hand, k_Up, arm);

            // The same pose on an avatar twice the size: every length doubles, so the SWIVEL must be identical.
            const float s = 2f;
            Vector3 shoulder2 = shoulder, hand2 = shoulder2 + handOff * s, elbow2 = shoulder2 + (elbow - shoulder) * s;
            float big = BasisElbowAnatomyCore.GuardSwivelRad(shoulder2, elbow2, hand2, k_Up, arm * s);

            Assert.AreEqual(small, big, 1e-4f,"the same posture on a bigger avatar must produce the same corrective swivel");
        }
        // ============================================================================================
        // 3. IT MUST NOT BLOW UP.
        // ============================================================================================
        [Test]
        public void DegenerateAndNaNInputs_DeclineRatherThanCorrupt()
        {
            Vector3 hand = shoulder + new Vector3(0.25f, -0.10f, 0.40f);

            // A straight arm: the circle has collapsed, there is no swivel that moves the elbow anywhere.
            Vector3 straightHand = shoulder + new Vector3(arm, 0f, 0f);
            Vector3 straightElbow = shoulder + new Vector3(upper, 0f, 0f);
            Assert.AreEqual(0f, BasisElbowAnatomyCore.GuardSwivelRad(shoulder, straightElbow, straightHand, k_Up, arm),"a straight arm has nothing to guard");

            // Hand on top of the shoulder: no axis.
            Assert.AreEqual(0f, BasisElbowAnatomyCore.GuardSwivelRad(shoulder, ElbowAt(hand, 90f), shoulder, k_Up, arm),"a zero-length arm axis must decline");

            // Arm pointing straight UP: the elbow's circle is horizontal, so no swivel changes its height.
            Vector3 upHand = shoulder + new Vector3(0f, 0.55f, 0f);
            Assert.AreEqual(0f, BasisElbowAnatomyCore.GuardSwivelRad(shoulder, ElbowAt(upHand, 40f), upHand, k_Up, arm),"a vertical arm axis makes the constraint inexpressible as a swivel; it must decline, not guess");

            // NaN in, zero out. A NaN transform PERSISTS in Unity -- the arm would never recover.
            var nan = new Vector3(float.NaN, 0f, 0f);
            Assert.AreEqual(0f, BasisElbowAnatomyCore.GuardSwivelRad(shoulder, nan, hand, k_Up, arm));
            Assert.AreEqual(0f, BasisElbowAnatomyCore.GuardSwivelRad(shoulder, ElbowAt(hand, 90f), nan, k_Up, arm));
            Assert.AreEqual(0f, BasisElbowAnatomyCore.GuardSwivelRad(shoulder, ElbowAt(hand, 90f), hand, nan, arm));
            Assert.AreEqual(0f, BasisElbowAnatomyCore.GuardSwivelRad(shoulder, ElbowAt(hand, 90f), hand, k_Up, float.NaN));
            Assert.AreEqual(0f, BasisElbowAnatomyCore.GuardSwivelRad(shoulder, ElbowAt(hand, 90f), hand, k_Up, 0f));
        }
        // ============================================================================================
        // 4. END TO END, THROUGH THE REAL SOLVER.
        // ============================================================================================
        [Test]
        public void TheSolver_RefusesASkyPointingHint_AndKeepsTheHandOnTarget()
        {
            Vector3 hand = shoulder + new Vector3(0.25f, -0.15f, 0.42f);

            // A hint demanding the elbow at the top of its circle.
            Vector3 skyElbow = ElbowAt(hand, 180f);
            Vector3 skyHint = shoulder + (skyElbow - shoulder).normalized * (0.5f * arm);
            BasisArmSolveInput i = default;
            i.Shoulder = shoulder;
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

            float h = Vector3.Dot(r.ElbowSolved - shoulder, k_Up);
            float hard = Ceiling(hand) + BasisElbowAnatomyCore.HardMarginFracLimb * arm;

            Assert.Less(h, hard, $"even handed a hint that explicitly demands a sky-pointing elbow, the solver must not deliver " + $"one -- it ended at {h:F3} against a hard limit of {hard:F3}");
            Assert.AreEqual(0f, Vector3.Distance(r.HandSolved, hand), 2e-3f, "and the hand must still be exactly on its target: the guard is a swivel about the " +"shoulder->hand axis, so it cannot move the hand");
        }
    }
}
