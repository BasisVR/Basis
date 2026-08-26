using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using Basis.IK;
namespace Basis.Tests.IK
{
    public class BasisElbowFieldTests
    {
        const float k_ArmLen = 0.60f;
        static float3 Hand(float outward, float up, float fwd) => new float3(outward, up, fwd);
        static float3 Bend(float3 tip, out float cond) => BasisElbowFieldModel.BendDirection(tip, BasisElbowFieldModel.Elbow(tip), out cond);
        static float3 ElbowOnCircle(float3 tip)
        {
            float3 bend = Bend(tip, out _);
            float d = math.clamp(math.length(tip), 1e-6f, 1f - 1e-6f), along = d * 0.5f;
            float rho = math.sqrt(math.max(0.25f - along * along, 0f));
            return math.normalize(tip) * along + bend * rho;
        }
        [Test]
        public void TheBend_IsAlwaysUnit_AndPerpendicularToTheArm_EvenBeyondReach()
        {
            var rng = new System.Random(20260715);

            for (int i = 0; i < 20000; i++)
            {
                // out to 3x reach: a tall user on a short avatar is outside the fit box on EVERY frame.
                float3 tip = new float3((float)(rng.NextDouble() * 6.0 - 3.0), (float)(rng.NextDouble() * 6.0 - 3.0), (float)(rng.NextDouble() * 6.0 - 3.0));
                if (math.length(tip) < 1e-3f) continue;

                float3 bend = Bend(tip, out float cond);

                Assert.IsTrue(math.all(math.isfinite(bend)), $"bend must be finite at {tip}");
                Assert.IsTrue(math.isfinite(cond), $"conditioning must be finite at {tip}");
                Assert.AreEqual(1f, math.length(bend), 2e-3f, $"bend must be UNIT at {tip}");
                Assert.AreEqual(0f, math.dot(math.normalize(tip), bend), 2e-3f, $"bend must be PERPENDICULAR to the shoulder->hand axis at {tip} -- it is the elbow's " +"circle, and a hint off that circle is what the deleted fades existed to drag back");
            }
        }
        [Test]
        public void TheElbow_DoesNotFlip_WhenTheArmHangsUnderTheShoulder()
        {
            const int steps = 400;
            const float gate = 3.0f;

            foreach (float reach in new[] { 0.40f, 0.55f, 0.62f, 0.75f, 0.90f })
            {
                float worst = 0f;
                float3 prevElbow = default;

                for (int s = 0; s <= steps; s++)
                {
                    // walk the hand straight THROUGH the vertical: fore-aft, directly beneath the shoulder.
                    float fwd = Mathf.Lerp(-0.25f, 0.25f, s / (float)steps);
                    float3 tip = Hand(0.03f, -reach, fwd), elbow = ElbowOnCircle(tip);
                    if (s > 0)
                    {
                        float handStep = math.distance(tip, Hand(0.03f, -reach, Mathf.Lerp(-0.25f, 0.25f, (s - 1) / (float)steps)));
                        float elbowStep = math.distance(elbow, prevElbow);
                        if (handStep > 1e-6f) worst = math.max(worst, elbowStep / handStep);
                    }
                    prevElbow = elbow;
                }

                Assert.Less(worst, gate, $"the elbow moved {worst:F1}x the hand's motion at reach {reach:F2}, sweeping the hand " + "fore-aft directly under the shoulder. That is the flip: the swivel model this replaced " + "measured its angle from a reference that VANISHES when the arm hangs vertical, and it " +"swung the elbow 49 deg / 19.6 cm across 3 cm of this very sweep.");
            }
        }
        [Test]
        public void TheElbow_TracksItsHand_AcrossTheWholeWorkspace()
        {
            var rng = new System.Random(4242);
            const float step = 0.005f / k_ArmLen;   // a 5 mm hand move on a 0.6 m arm
            int over = 0, n = 0;
            float worst = 0f;
            float3 worstAt = default;

            for (int i = 0; i < 40000; i++)
            {
                float3 tip = new float3((float)(rng.NextDouble() * 2.0 - 1.0), (float)(rng.NextDouble() * 2.0 - 1.0), (float)(rng.NextDouble() * 2.0 - 1.0));
                float r = math.length(tip);
                if (r > 0.98f || r < 0.15f) continue;
                // the hand cannot be inside the torso; that region is not reachable and not worth gating.
                if (tip.x < -0.22f && math.abs(tip.y) < 0.45f && math.abs(tip.z) < 0.30f) continue;

                float3 e0 = ElbowOnCircle(tip);
                for (int k = 0; k < 3; k++)
                {
                    float3 d = float3.zero; d[k] = step;
                    float3 t2 = tip + d;
                    if (math.length(t2) > 1f) continue;
                    float g = math.distance(ElbowOnCircle(t2), e0) / step;
                    if (g > worst) { worst = g; worstAt = tip; }
                    if (g > 8f) over++;
                }
                n++;
            }

            // 8x is generous -- a human elbow tracks at 0.5-1.5x. The shipped model exceeded 20x on 1.67% of
            // this same workspace and peaked at 119x.
            Assert.Less(over / (float)math.max(n, 1), 0.01f, $"{100f * over / math.max(n, 1):F2}% of the workspace moves the elbow more than 8x the hand " + $"(worst {worst:F0}x at {worstAt}). That reads as a flip.");
        }
        [Test]
        public void TheElbow_StaysUnderTheHigherOfShoulderAndHand()
        {
            var rng = new System.Random(777);
            const float margin = 0.15f;   // BasisElbowAnatomyCore's hard margin; it is the backstop for the rest
            int n = 0, bad = 0;
            float worst = 0f;

            for (int i = 0; i < 40000; i++)
            {
                float3 tip = new float3((float)(rng.NextDouble() * 2.0 - 1.0), (float)(rng.NextDouble() * 2.0 - 1.0), (float)(rng.NextDouble() * 2.0 - 1.0));
                float r = math.length(tip);
                if (r > 0.98f || r < 0.15f) continue;
                if (tip.x < -0.22f && math.abs(tip.y) < 0.45f && math.abs(tip.z) < 0.30f) continue;

                float ceiling = math.max(0f, tip.y) + margin, h = ElbowOnCircle(tip).y;
                if (h > ceiling) { bad++; worst = math.max(worst, h - ceiling); }
                n++;
            }

            Assert.Less(bad / (float)math.max(n, 1), 0.01f, $"{100f * bad / math.max(n, 1):F2}% of the reachable workspace puts the elbow above BOTH the " + $"shoulder and the hand (worst {worst * 100f * k_ArmLen:F1} cm over). A human arm will not do " +"that -- the humerus does not go there.");
        }
        [Test]
        public void TheModel_Saturates_WhenTheControllerIsBeyondTheAvatarsReach()
        {
            var rng = new System.Random(31337);

            for (int i = 0; i < 2000; i++)
            {
                float3 dir = math.normalize(new float3((float)(rng.NextDouble() * 2.0 - 1.0), (float)(rng.NextDouble() * 2.0 - 1.0), (float)(rng.NextDouble() * 2.0 - 1.0)));
                if (!math.all(math.isfinite(dir))) continue;

                float3 b1 = Bend(dir * 1.0f, out _), b2 = Bend(dir * 4.0f, out _);

                Assert.AreEqual(0f, math.distance(b1, b2), 1e-4f, "two out-of-reach targets in the SAME direction must give the SAME elbow -- the domain " + "clamp must bind, because beyond the fit box the model is not being asked a question it " +"can answer");
            }
        }
        [Test]
        public void TheElbows_Mirror_LeftToRight()
        {
            BasisSwivelFrame frame = BasisSwivelHintCore.BuildFrame(new Vector3(-0.17f, 1.40f, 0f), new Vector3(0.17f, 1.40f, 0f), new Vector3(0f, 1.25f, 0f), new Vector3(0f, 1.50f, 0f));

            var rng = new System.Random(99);
            for (int i = 0; i < 2000; i++)
            {
                Vector3 off = new Vector3((float)(rng.NextDouble() * 1.2 - 0.6), (float)(rng.NextDouble() * 1.2 - 0.6), (float)(rng.NextDouble() * 1.2 - 0.6));
                if (off.sqrMagnitude < 0.02f) continue;

                Vector3 rSh = new Vector3(0.17f, 1.40f, 0f), lSh = new Vector3(-0.17f, 1.40f, 0f);
                Vector3 mirrored = new Vector3(-off.x, off.y, off.z);

                Assert.IsTrue(BasisSwivelHintCore.ArmHint(frame, rSh, rSh + off, k_ArmLen, false, out Vector3 hintR, out float condR));
                Assert.IsTrue(BasisSwivelHintCore.ArmHint(frame, lSh, lSh + mirrored, k_ArmLen, true, out Vector3 hintL, out float condL));

                Vector3 poleR = hintR - rSh, poleL = hintL - lSh;
                Assert.AreEqual(-poleL.x, poleR.x, 1e-4f, "the elbows' OUTWARD offset must mirror");
                Assert.AreEqual(poleL.y, poleR.y, 1e-4f, "the elbows' height must match");
                Assert.AreEqual(poleL.z, poleR.z, 1e-4f, "the elbows' forward offset must match");
                Assert.AreEqual(condL, condR, 1e-4f, "the conditioning must be identical");
            }
        }
        [Test]
        public void TheElbow_GoesWhereAHumanElbowGoes()
        {
            // arm hanging at your side: the elbow is below the shoulder and points BACK, not down. It cannot
            // point down -- the hand is already down there. The old reference direction assumed otherwise, and
            // that assumption is exactly where it became undefined.
            float3 e = ElbowOnCircle(Hand(0.10f, -0.95f, 0.05f));
            Assert.Less(e.y, 0f, "arm at your side: the elbow hangs below the shoulder");
            Assert.Less(e.z, 0.15f, "arm at your side: the elbow points BACK, not forward");

            // hand on your hip, elbow bent: the elbow swings OUT and BACK.
            e = ElbowOnCircle(Hand(0.10f, -0.60f, 0.05f));
            Assert.Greater(e.x, 0f, "hand on hip: the elbow goes OUT");
            Assert.Less(e.z, 0f, "hand on hip: the elbow goes BACK");
            Assert.Less(e.y, 0f, "hand on hip: the elbow stays below the shoulder");

            // reaching forward: the elbow trails the hand, below and behind it.
            e = ElbowOnCircle(Hand(0.10f, -0.10f, 0.90f));
            Assert.Less(e.y, 0f, "reaching forward: the elbow stays below the shoulder");
            Assert.Greater(e.z, 0f, "reaching forward: the elbow follows the hand forward");

            // hand overhead: the elbow really IS above the shoulder here (71.9% of such frames in the corpus).
            e = ElbowOnCircle(Hand(0.10f, 0.90f, 0.15f));
            Assert.Greater(e.y, 0f, "hand overhead: the elbow rises above the shoulder, as a human's does");
            Assert.Less(e.y, 0.90f, "hand overhead: but it stays below the HAND");
        }
        [Test]
        public void NothingDegenerate_ProducesNaN()
        {
            float3[] nasty =
            {
                float3.zero,
                new float3(0f, -1f, 0f),        // exactly vertical: the old reference frame's zero
                new float3(0f, 1f, 0f),
                new float3(1e-20f, 0f, 0f),
                new float3(0f, -1e-20f, 0f),
                new float3(-1f, 0f, 0f),
                new float3(0f, 0f, 1f),
            };

            foreach (float3 tip in nasty)
            {
                float3 bend = Bend(tip, out _);
                Assert.IsTrue(math.all(math.isfinite(bend)), $"bend went non-finite at {tip}");
                Assert.AreEqual(1f, math.length(bend), 2e-3f, $"bend must stay unit even at {tip}");
            }

            // and the hint layer must refuse a NaN target at the door rather than pass it to a bone
            BasisSwivelFrame frame = BasisSwivelHintCore.BuildFrame(new Vector3(-0.17f, 1.40f, 0f), new Vector3(0.17f, 1.40f, 0f), new Vector3(0f, 1.25f, 0f), new Vector3(0f, 1.50f, 0f));
            Vector3 nan = new Vector3(float.NaN, 0f, 0f);
            Assert.IsFalse(BasisSwivelHintCore.ArmHint(frame, Vector3.zero, nan, k_ArmLen, false, out _, out _),"a NaN hand target must be refused, not solved on");
        }
    }
}
