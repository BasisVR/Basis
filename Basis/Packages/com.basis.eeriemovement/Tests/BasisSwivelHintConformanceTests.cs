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
        public void ADegenerateRig_ProducesNoFrameAndNoHint()
        {
            BasisSwivelFrame collapsed = BasisSwivelHintCore.BuildFrame(Vector3.zero, Vector3.zero, k_Chest, k_Neck);
            Assert.IsFalse(collapsed.Valid, "coincident shoulders cannot define a body frame");

            BasisSwivelFrame noUp = BasisSwivelHintCore.BuildFrame(leftUpperArm, rightUpperArm, k_Chest, k_Chest);
            Assert.IsFalse(noUp.Valid, "a zero-length spine cannot define a body frame");

        }
        [Test]
        public void ANaNTarget_IsRefused_RatherThanSolvedOn()
        {
            BasisSwivelFrame frame = ArmFrame();
            var nan = new Vector3(float.NaN, 0f, 0f);


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
