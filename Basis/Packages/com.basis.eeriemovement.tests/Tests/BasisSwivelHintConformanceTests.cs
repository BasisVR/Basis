using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using Basis.IK;
namespace Basis.Tests.IK
{
    public class BasisSwivelHintConformanceTests
    {
        const float tol = 1e-4f;
        // A mocap-like rig: T-posed, upright, facing +Z, at world identity.
        static readonly Vector3 leftUpperArm = new Vector3(-0.17f, 1.40f, 0f);
        static readonly Vector3 rightUpperArm = new Vector3(0.17f, 1.40f, 0f), k_Chest = new Vector3(0f, 1.25f, 0f);
        static readonly Vector3 k_Neck = new Vector3(0f, 1.50f, 0f), leftUpperLeg = new Vector3(-0.09f, 0.92f, 0f);
        static readonly Vector3 rightUpperLeg = new Vector3(0.09f, 0.92f, 0f), k_Hips = new Vector3(0f, 0.95f, 0f);
        const float k_ArmLen = 0.60f, legLen = 0.85f;
        static BasisSwivelFrame ArmFrame() => BasisSwivelHintCore.BuildFrame(leftUpperArm, rightUpperArm, k_Chest, k_Neck);
        static BasisSwivelFrame LegFrame() => BasisSwivelHintCore.BuildFrame(leftUpperLeg, rightUpperLeg, k_Hips, k_Chest);
        static float3 HarnessArmLocal(Vector3 shoulder, Vector3 hand, float armLen, bool isLeft)
        {
            Vector3 bUp = (k_Neck - k_Chest).normalized, bRight = rightUpperArm - leftUpperArm;
            bRight = (bRight - bUp * Vector3.Dot(bRight, bUp)).normalized;
            Vector3 bFwd = Vector3.Cross(bRight, bUp), bOut = isLeft ? -bRight : bRight, s2h = hand - shoulder;
            return new float3(Vector3.Dot(s2h, bOut) / armLen, Vector3.Dot(s2h, bUp) / armLen, Vector3.Dot(s2h, bFwd) / armLen);
        }
        static void AssertClose(float3 a, float3 b, string what)
        {
            Assert.AreEqual(a.x, b.x, tol, what + ".x");
            Assert.AreEqual(a.y, b.y, tol, what + ".y");
            Assert.AreEqual(a.z, b.z, tol, what + ".z");
        }
        [Test]
        public void ArmFeatures_MatchTheFitPipeline_OnAMocapShapedRig()
        {
            BasisSwivelFrame frame = ArmFrame();
            Assert.IsTrue(frame.Valid, "the T-posed reference rig must produce a valid frame");

            var rng = new System.Random(20260714);
            for (int t = 0; t < 200; t++)
            {
                bool isLeft = (t & 1) == 0;
                Vector3 shoulder = isLeft ? leftUpperArm : rightUpperArm;
                Vector3 hand = shoulder + RandomInBall(rng, 0.95f * k_ArmLen);

                BasisSwivelHintCore.Features(frame, shoulder, hand, k_ArmLen, isLeft, out float3 local);
                AssertClose(HarnessArmLocal(shoulder, hand, k_ArmLen, isLeft), local, $"tipLocal (iter {t}, {(isLeft ? "L" : "R")})");
            }
        }
        [Test]
        public void TheModel_RefusesToExtrapolate_WhenTheControllerIsBeyondTheAvatarsReach()
        {
            BasisSwivelFrame frame = ArmFrame();
            var rng = new System.Random(31337);

            for (int t = 0; t < 400; t++)
            {
                bool isLeft = (t & 1) == 0;
                Vector3 shoulder = isLeft ? leftUpperArm : rightUpperArm;

                // 1.0x to 3.0x the avatar's arm length: a tall user on a short avatar, a lunge, a mis-scaled
                // calibration. All of these happen, and they happen constantly.
                float over = Mathf.Lerp(1.0f, 3.0f, (float)rng.NextDouble());
                Vector3 dir = RandomInBall(rng, 1f).normalized, hand = shoulder + dir * (over * k_ArmLen);

                Assert.IsTrue(BasisSwivelHintCore.ArmHint(frame, shoulder, hand, k_ArmLen, isLeft, out Vector3 hint, out float conf), $"an out-of-reach target must still produce a hint (x{over:F2} reach)");
                Assert.IsTrue(float.IsFinite(conf) && conf > 0f, "confidence must stay finite and positive");

                // The hint must stay EXACTLY on the elbow's circle -- half an arm off the shoulder,
                // perpendicular to the limb axis. Not "roughly": the whole design rests on it.
                Assert.AreEqual(0.5f * k_ArmLen, Vector3.Distance(hint, shoulder), 1e-3f, $"the hint must stay half an arm-length off the shoulder even at x{over:F2} reach");

                Vector3 axis = (hand - shoulder).normalized;
                Assert.AreEqual(0f, Vector3.Dot(axis, (hint - shoulder).normalized), 1e-3f, $"the hint must stay on the elbow's circle even at x{over:F2} reach");

                // ...and the clamp must actually BIND. Past the domain the answer must STOP CHANGING with
                // distance, because the model is no longer being asked a question it is able to answer.
                Vector3 farther = shoulder + dir * (4f * k_ArmLen);
                Assert.IsTrue(BasisSwivelHintCore.ArmHint(frame, shoulder, farther, k_ArmLen, isLeft, out Vector3 hint2, out _));
                Assert.AreEqual(0f, Vector3.Distance(hint, hint2), 1e-3f, "beyond the fit domain the model must SATURATE, not keep extrapolating -- two targets in " +"the same direction, both out of reach, must give the same elbow");
            }
        }
        [Test]
        public void TheElbow_HangsBelowTheShoulder_InReachAndBeyondIt()
        {
            BasisSwivelFrame frame = ArmFrame();

            foreach (float reach in new[] { 0.3f, 0.6f, 0.9f, 1.0f, 1.5f, 2.5f })
            {
                foreach (bool isLeft in new[] { false, true })
                {
                    Vector3 shoulder = isLeft ? leftUpperArm : rightUpperArm;
                    float side = isLeft ? -1f : 1f;
                    Vector3 dir = new Vector3(side * 0.92f, 0f, 0.39f).normalized;   // out to the side, a little forward
                    Vector3 hand = shoulder + dir * (reach * k_ArmLen);

                    Assert.IsTrue(BasisSwivelHintCore.ArmHint(frame, shoulder, hand, k_ArmLen, isLeft, out Vector3 hint, out _));

                    Assert.Less(hint.y, shoulder.y, $"the derived elbow must hang BELOW the shoulder on a lateral reach -- " + $"{(isLeft ? "LEFT" : "RIGHT")} arm at x{reach:F1} reach put it at y={hint.y:F3} " + $"against a shoulder at y={shoulder.y:F3}.");
                }
            }
        }
        [Test]
        public void LegHint_StaysOnTheCircle_AtEveryExtension_AndBeyond()
        {
            BasisSwivelFrame frame = LegFrame();
            Assert.IsTrue(frame.Valid, "the T-posed reference rig must produce a valid leg frame");
            var rng = new System.Random(11);

            foreach (float ext in new[] { 0.30f, 0.70f, 0.95f, 0.999f, 1.4f, 2.5f })
            {
                for (int t = 0; t < 40; t++)
                {
                    bool isLeft = (t & 1) == 0;
                    Vector3 hip = isLeft ? leftUpperLeg : rightUpperLeg, dir = RandomInBall(rng, 1f).normalized;
                    Vector3 foot = hip + dir * (ext * legLen);

                    Assert.IsTrue(BasisSwivelHintCore.LegHint(frame, hip, foot, legLen, isLeft, out Vector3 hint, out float conf), $"the leg hint must be produced at extension {ext}");
                    Assert.IsTrue(float.IsFinite(conf), "confidence must be finite");

                    Vector3 axis = (foot - hip).normalized;
                    Assert.AreEqual(0f, Vector3.Dot(axis, (hint - hip).normalized), 1e-3f, $"the hint must lie on the knee's circle at extension {ext}");
                }
            }
        }
        [Test]
        public void ThePositionFeatures_AreIdenticalAcrossTheMirror_SoOneModelServesBothLimbs()
        {
            BasisSwivelFrame frame = ArmFrame();
            var rng = new System.Random(1234);

            for (int t = 0; t < 150; t++)
            {
                Vector3 offset = RandomInBall(rng, 0.9f * k_ArmLen);

                BasisSwivelHintCore.Features(frame, rightUpperArm, rightUpperArm + offset, k_ArmLen, false, out float3 rLocal);
                BasisSwivelHintCore.Features(frame, leftUpperArm, leftUpperArm + MirrorX(offset), k_ArmLen, true, out float3 lLocal);

                AssertClose(rLocal, lLocal, $"a mirrored reach must produce an IDENTICAL tipLocal -- that is what makes one model serve both arms (iter {t})");
            }
        }
        [Test]
        public void TheElbows_Mirror_LeftToRight()
        {
            BasisSwivelFrame frame = ArmFrame();
            var rng = new System.Random(777);

            for (int t = 0; t < 150; t++)
            {
                Vector3 offset = RandomInBall(rng, 0.9f * k_ArmLen);

                Assert.IsTrue(BasisSwivelHintCore.ArmHint(frame, rightUpperArm, rightUpperArm + offset, k_ArmLen, false, out Vector3 hintR, out _));
                Assert.IsTrue(BasisSwivelHintCore.ArmHint(frame, leftUpperArm, leftUpperArm + MirrorX(offset), k_ArmLen, true, out Vector3 hintL, out _));

                Vector3 expect = MirrorX(hintR - rightUpperArm), got = hintL - leftUpperArm;
                Assert.AreEqual(0f, Vector3.Distance(expect, got), 1e-3f, $"the left elbow must be the mirror of the right (iter {t}): expected {expect}, got {got}");
            }
        }
        [Test]
        public void ADegenerateRig_ProducesNoFrameAndNoHint()
        {
            BasisSwivelFrame collapsed = BasisSwivelHintCore.BuildFrame(Vector3.zero, Vector3.zero, k_Chest, k_Neck);
            Assert.IsFalse(collapsed.Valid, "coincident shoulders cannot define a body frame");

            BasisSwivelFrame noUp = BasisSwivelHintCore.BuildFrame(leftUpperArm, rightUpperArm, k_Chest, k_Chest);
            Assert.IsFalse(noUp.Valid, "a zero-length spine cannot define a body frame");

            Assert.IsFalse(BasisSwivelHintCore.ArmHint(collapsed, rightUpperArm, Vector3.zero, k_ArmLen, false, out _, out _), "no live frame => no hint");
            Assert.IsFalse(BasisSwivelHintCore.ArmHint(ArmFrame(), rightUpperArm, Vector3.zero, 0f, false, out _, out _), "a zero-length limb => no hint");
        }
        [Test]
        public void ANaNTarget_IsRefused_RatherThanSolvedOn()
        {
            BasisSwivelFrame frame = ArmFrame();
            var nan = new Vector3(float.NaN, 0f, 0f);

            Assert.IsFalse(BasisSwivelHintCore.ArmHint(frame, rightUpperArm, nan, k_ArmLen, false, out _, out _),"a NaN hand target must produce no hint");

            BasisSwivelFrame leg = LegFrame();
            Assert.IsFalse(BasisSwivelHintCore.LegHint(leg, rightUpperLeg, nan, legLen, false, out _, out _),"a NaN foot target must produce no hint");

            Assert.IsFalse(BasisSwivelHintCore.BuildFrame(nan, rightUpperArm, k_Chest, k_Neck).Valid,"a NaN bone position must not yield a 'valid' frame");
        }
        // -------------------------------------------------------------------------------------------------
        static Vector3 MirrorX(Vector3 v) => new Vector3(-v.x, v.y, v.z);
        static Vector3 RandomInBall(System.Random rng, float radius)
        {
            for (int i = 0; i < 64; i++)
            {
                var v = new Vector3((float)(rng.NextDouble() * 2.0 - 1.0), (float)(rng.NextDouble() * 2.0 - 1.0), (float)(rng.NextDouble() * 2.0 - 1.0));
                if (v.sqrMagnitude > 1e-4f && v.sqrMagnitude <= 1f)
                {
                    return v * radius;
                }
            }
            return new Vector3(radius, 0f, 0f);
        }
    }
}
