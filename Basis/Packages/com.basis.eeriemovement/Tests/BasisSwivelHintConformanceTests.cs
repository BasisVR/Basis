using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using Basis.IK;

namespace Basis.Tests.IK
{
    /// <summary>
    /// Pins <see cref="BasisSwivelHintCore"/> -- the LIVE RIG's feature construction for the swivel models --
    /// against the construction the models were actually FITTED in (BasisMocapAccuracy, which IS the fit
    /// pipeline).
    ///
    /// ================================================================================================
    /// WHY THIS FILE EXISTS, AND WHY IT WAS NOT ENOUGH.
    ///
    /// The mocap harness CANNOT verify a live-rig change. It drives the solve CORES directly; it never runs the
    /// animation job. So it can prove the model is good and still tell you nothing about whether the rig is
    /// feeding it correctly -- and a swivel model fed wrong does not degrade gracefully. It produces CONFIDENT
    /// GARBAGE, which is the only kind of wrong that survives a green suite.
    ///
    /// ⚠ THIS FILE WAS GREEN WHILE THE ELBOWS WERE UP BY THE EARS IN A HEADSET. Twice over:
    ///
    ///   1. Every test here drove the model with a hand ON the arm (|tipLocal| <= 1), because that is all the
    ///      corpus contains. THE LIVE RIG IS HANDED THE RAW CONTROLLER TARGET, which sails past the avatar's
    ///      reach constantly -- and the model was a 3rd-order polynomial with NO DOMAIN CLAMP, i.e. a random
    ///      number generator out there. TheModel_RefusesToExtrapolate is the test that was missing.
    ///
    ///   2. The model read the hand's ROTATION, divided by a T-pose captured at job build -- but
    ///      BasisLocalAvatarDriver exits T-pose BEFORE it builds the rig, so that "rest pose" was not reliably a
    ///      rest pose. Those features are gone: a bone's rotation is a rig convention and it does not transfer.
    ///
    /// The lesson is written into the TESTS and not merely into the comments: every test here now drives the
    /// model with inputs THE LIVE RIG CAN ACTUALLY PRODUCE, especially the ones the corpus never contains.
    /// ================================================================================================
    /// </summary>
    public class BasisSwivelHintConformanceTests
    {
        const float k_Tol = 1e-4f;

        // A mocap-like rig: T-posed, upright, facing +Z, at world identity.
        static readonly Vector3 k_LeftUpperArm = new Vector3(-0.17f, 1.40f, 0f);
        static readonly Vector3 k_RightUpperArm = new Vector3(0.17f, 1.40f, 0f);
        static readonly Vector3 k_Chest = new Vector3(0f, 1.25f, 0f);
        static readonly Vector3 k_Neck = new Vector3(0f, 1.50f, 0f);

        static readonly Vector3 k_LeftUpperLeg = new Vector3(-0.09f, 0.92f, 0f);
        static readonly Vector3 k_RightUpperLeg = new Vector3(0.09f, 0.92f, 0f);
        static readonly Vector3 k_Hips = new Vector3(0f, 0.95f, 0f);

        const float k_ArmLen = 0.60f;
        const float k_LegLen = 0.85f;

        static BasisSwivelFrame ArmFrame() =>
            BasisSwivelHintCore.BuildFrame(k_LeftUpperArm, k_RightUpperArm, k_Chest, k_Neck);

        static BasisSwivelFrame LegFrame() =>
            BasisSwivelHintCore.BuildFrame(k_LeftUpperLeg, k_RightUpperLeg, k_Hips, k_Chest);

        /// <summary>The harness's own construction, transcribed from BasisMocapAccuracy.SolveArm. Deliberately
        /// a COPY and not a call: the point is to pin the runtime against the fit's formula AS WRITTEN.</summary>
        static float3 HarnessArmLocal(Vector3 shoulder, Vector3 hand, float armLen, bool isLeft)
        {
            Vector3 bUp = (k_Neck - k_Chest).normalized;
            Vector3 bRight = k_RightUpperArm - k_LeftUpperArm;
            bRight = (bRight - bUp * Vector3.Dot(bRight, bUp)).normalized;
            Vector3 bFwd = Vector3.Cross(bRight, bUp);
            Vector3 bOut = isLeft ? -bRight : bRight;

            Vector3 s2h = hand - shoulder;
            return new float3(Vector3.Dot(s2h, bOut) / armLen,
                              Vector3.Dot(s2h, bUp) / armLen,
                              Vector3.Dot(s2h, bFwd) / armLen);
        }

        static void AssertClose(float3 a, float3 b, string what)
        {
            Assert.AreEqual(a.x, b.x, k_Tol, what + ".x");
            Assert.AreEqual(a.y, b.y, k_Tol, what + ".y");
            Assert.AreEqual(a.z, b.z, k_Tol, what + ".z");
        }

        /// <summary>
        /// THE CONFORMANCE TEST. The runtime's features must equal the fit's, term for term, on BOTH sides.
        /// Checked on both arms because a left/right mirror error is invisible on the right and catastrophic
        /// on the left.
        /// </summary>
        [Test]
        public void ArmFeatures_MatchTheFitPipeline_OnAMocapShapedRig()
        {
            BasisSwivelFrame frame = ArmFrame();
            Assert.IsTrue(frame.Valid, "the T-posed reference rig must produce a valid frame");

            var rng = new System.Random(20260714);
            for (int t = 0; t < 200; t++)
            {
                bool isLeft = (t & 1) == 0;
                Vector3 shoulder = isLeft ? k_LeftUpperArm : k_RightUpperArm;
                Vector3 hand = shoulder + RandomInBall(rng, 0.95f * k_ArmLen);

                BasisSwivelHintCore.Features(frame, shoulder, hand, k_ArmLen, isLeft, out float3 local);
                AssertClose(HarnessArmLocal(shoulder, hand, k_ArmLen, isLeft), local,
                            $"tipLocal (iter {t}, {(isLeft ? "L" : "R")})");
            }
        }

        /// <summary>
        /// ⭐ THE TEST THAT WAS MISSING -- AND ITS ABSENCE IS WHAT PUT THE ELBOWS UP BY THE EARS.
        ///
        /// The corpus only ever contains a hand ON the arm, so every test in this file used to drive the model
        /// with |tipLocal| &lt;= 1. The LIVE RIG hands it the raw CONTROLLER TARGET, and a user whose arms are
        /// longer than their avatar's is outside that box on essentially every frame. A 3rd-order polynomial
        /// with coefficients up to 15 does not gracefully degrade out there -- it is a random number generator.
        ///
        /// So: put the target WAY past the avatar's reach, and the elbow must still be a sane elbow.
        /// </summary>
        [Test]
        public void TheModel_RefusesToExtrapolate_WhenTheControllerIsBeyondTheAvatarsReach()
        {
            BasisSwivelFrame frame = ArmFrame();
            var rng = new System.Random(31337);

            for (int t = 0; t < 400; t++)
            {
                bool isLeft = (t & 1) == 0;
                Vector3 shoulder = isLeft ? k_LeftUpperArm : k_RightUpperArm;

                // 1.0x to 3.0x the avatar's arm length: a tall user on a short avatar, a lunge, a mis-scaled
                // calibration. All of these happen, and they happen constantly.
                float over = Mathf.Lerp(1.0f, 3.0f, (float)rng.NextDouble());
                Vector3 dir = RandomInBall(rng, 1f).normalized;
                Vector3 hand = shoulder + dir * (over * k_ArmLen);

                Assert.IsTrue(BasisSwivelHintCore.ArmHint(frame, shoulder, hand, k_ArmLen, isLeft,
                                                          out Vector3 hint, out float conf),
                              $"an out-of-reach target must still produce a hint (x{over:F2} reach)");
                Assert.IsTrue(float.IsFinite(conf) && conf > 0f, "confidence must stay finite and positive");

                // The hint must stay EXACTLY on the elbow's circle -- half an arm off the shoulder,
                // perpendicular to the limb axis. Not "roughly": the whole design rests on it.
                Assert.AreEqual(0.5f * k_ArmLen, Vector3.Distance(hint, shoulder), 1e-3f,
                    $"the hint must stay half an arm-length off the shoulder even at x{over:F2} reach");

                Vector3 axis = (hand - shoulder).normalized;
                Assert.AreEqual(0f, Vector3.Dot(axis, (hint - shoulder).normalized), 1e-3f,
                    $"the hint must stay on the elbow's circle even at x{over:F2} reach");

                // ...and the clamp must actually BIND. Past the domain the answer must STOP CHANGING with
                // distance, because the model is no longer being asked a question it is able to answer.
                Vector3 farther = shoulder + dir * (4f * k_ArmLen);
                Assert.IsTrue(BasisSwivelHintCore.ArmHint(frame, shoulder, farther, k_ArmLen, isLeft,
                                                          out Vector3 hint2, out _));
                Assert.AreEqual(0f, Vector3.Distance(hint, hint2), 1e-3f,
                    "beyond the fit domain the model must SATURATE, not keep extrapolating -- two targets in " +
                    "the same direction, both out of reach, must give the same elbow");
            }
        }

        /// <summary>
        /// ⭐ THE ELBOW HANGS DOWN. Reaching out to the side, a human's elbow sits BELOW the shoulder->hand
        /// line, never above it. Pinned IN reach and BEYOND it, because "elbows up by the ears" is precisely
        /// the bug that shipped.
        /// </summary>
        [Test]
        public void TheElbow_HangsBelowTheShoulder_InReachAndBeyondIt()
        {
            BasisSwivelFrame frame = ArmFrame();

            foreach (float reach in new[] { 0.3f, 0.6f, 0.9f, 1.0f, 1.5f, 2.5f })
            {
                foreach (bool isLeft in new[] { false, true })
                {
                    Vector3 shoulder = isLeft ? k_LeftUpperArm : k_RightUpperArm;
                    float side = isLeft ? -1f : 1f;
                    Vector3 dir = new Vector3(side * 0.92f, 0f, 0.39f).normalized;   // out to the side, a little forward
                    Vector3 hand = shoulder + dir * (reach * k_ArmLen);

                    Assert.IsTrue(BasisSwivelHintCore.ArmHint(frame, shoulder, hand, k_ArmLen, isLeft,
                                                              out Vector3 hint, out _));

                    Assert.Less(hint.y, shoulder.y,
                        $"the derived elbow must hang BELOW the shoulder on a lateral reach -- " +
                        $"{(isLeft ? "LEFT" : "RIGHT")} arm at x{reach:F1} reach put it at y={hint.y:F3} " +
                        $"against a shoulder at y={shoulder.y:F3}.");
                }
            }
        }

        /// <summary>The knee's equivalent: the hint stays on its circle at every extension, and past it.</summary>
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
                    Vector3 hip = isLeft ? k_LeftUpperLeg : k_RightUpperLeg;
                    Vector3 dir = RandomInBall(rng, 1f).normalized;
                    Vector3 foot = hip + dir * (ext * k_LegLen);

                    Assert.IsTrue(BasisSwivelHintCore.LegHint(frame, hip, foot, k_LegLen, isLeft,
                                                              out Vector3 hint, out float conf),
                                  $"the leg hint must be produced at extension {ext}");
                    Assert.IsTrue(float.IsFinite(conf), "confidence must be finite");

                    Vector3 axis = (foot - hip).normalized;
                    Assert.AreEqual(0f, Vector3.Dot(axis, (hint - hip).normalized), 1e-3f,
                        $"the hint must lie on the knee's circle at extension {ext}");
                }
            }
        }

        /// <summary>
        /// THE MIRROR, pinned on the quantity where it is true: reflect the pose onto the other arm and the
        /// model must see THE SAME tipLocal -- not a mirrored one, an IDENTICAL one. That is what "+x is
        /// OUTWARD for both limbs, so one model serves both" actually means.
        /// </summary>
        [Test]
        public void ThePositionFeatures_AreIdenticalAcrossTheMirror_SoOneModelServesBothLimbs()
        {
            BasisSwivelFrame frame = ArmFrame();
            var rng = new System.Random(1234);

            for (int t = 0; t < 150; t++)
            {
                Vector3 offset = RandomInBall(rng, 0.9f * k_ArmLen);

                BasisSwivelHintCore.Features(frame, k_RightUpperArm, k_RightUpperArm + offset,
                                             k_ArmLen, false, out float3 rLocal);
                BasisSwivelHintCore.Features(frame, k_LeftUpperArm, k_LeftUpperArm + MirrorX(offset),
                                             k_ArmLen, true, out float3 lLocal);

                AssertClose(rLocal, lLocal,
                    $"a mirrored reach must produce an IDENTICAL tipLocal -- that is what makes one model serve both arms (iter {t})");
            }
        }

        /// <summary>
        /// ⭐ ...AND THE MIRRORED ELBOW MUST ACTUALLY MIRROR. Equal features are necessary but not sufficient:
        /// the un-mirroring of the ANGLE has to be right too, and a sign error there is exactly "one elbow is
        /// fine and the other one is inverted".
        /// </summary>
        [Test]
        public void TheElbows_Mirror_LeftToRight()
        {
            BasisSwivelFrame frame = ArmFrame();
            var rng = new System.Random(777);

            for (int t = 0; t < 150; t++)
            {
                Vector3 offset = RandomInBall(rng, 0.9f * k_ArmLen);

                Assert.IsTrue(BasisSwivelHintCore.ArmHint(frame, k_RightUpperArm, k_RightUpperArm + offset,
                                                          k_ArmLen, false, out Vector3 hintR, out _));
                Assert.IsTrue(BasisSwivelHintCore.ArmHint(frame, k_LeftUpperArm, k_LeftUpperArm + MirrorX(offset),
                                                          k_ArmLen, true, out Vector3 hintL, out _));

                Vector3 expect = MirrorX(hintR - k_RightUpperArm);
                Vector3 got = hintL - k_LeftUpperArm;
                Assert.AreEqual(0f, Vector3.Distance(expect, got), 1e-3f,
                    $"the left elbow must be the mirror of the right (iter {t}): expected {expect}, got {got}");
            }
        }

        /// <summary>A degenerate rig must DECLINE, not answer. The caller then leaves the limb on the two-bone
        /// core's own fallback pole, which is what it did before any of this existed.</summary>
        [Test]
        public void ADegenerateRig_ProducesNoFrameAndNoHint()
        {
            BasisSwivelFrame collapsed = BasisSwivelHintCore.BuildFrame(Vector3.zero, Vector3.zero, k_Chest, k_Neck);
            Assert.IsFalse(collapsed.Valid, "coincident shoulders cannot define a body frame");

            BasisSwivelFrame noUp = BasisSwivelHintCore.BuildFrame(k_LeftUpperArm, k_RightUpperArm, k_Chest, k_Chest);
            Assert.IsFalse(noUp.Valid, "a zero-length spine cannot define a body frame");

            Assert.IsFalse(BasisSwivelHintCore.ArmHint(collapsed, k_RightUpperArm, Vector3.zero, k_ArmLen, false,
                                                       out _, out _), "no live frame => no hint");
            Assert.IsFalse(BasisSwivelHintCore.ArmHint(ArmFrame(), k_RightUpperArm, Vector3.zero, 0f, false,
                                                       out _, out _), "a zero-length limb => no hint");
        }

        /// <summary>
        /// A NaN hand target must be REFUSED, not solved on. Not hypothetical: one NaN MediaPipe landmark used
        /// to walk through every guard in the tracking path (every `x &lt; limit` test is FALSE for NaN), reach
        /// the Burst IK job, and become `(int)NaN` == int.MinValue inside the old lookup's trilinear sampler --
        /// aborting the process with no managed stack to read.
        /// </summary>
        [Test]
        public void ANaNTarget_IsRefused_RatherThanSolvedOn()
        {
            BasisSwivelFrame frame = ArmFrame();
            var nan = new Vector3(float.NaN, 0f, 0f);

            Assert.IsFalse(BasisSwivelHintCore.ArmHint(frame, k_RightUpperArm, nan, k_ArmLen, false, out _, out _),
                           "a NaN hand target must produce no hint");

            BasisSwivelFrame leg = LegFrame();
            Assert.IsFalse(BasisSwivelHintCore.LegHint(leg, k_RightUpperLeg, nan, k_LegLen, false, out _, out _),
                           "a NaN foot target must produce no hint");

            Assert.IsFalse(BasisSwivelHintCore.BuildFrame(nan, k_RightUpperArm, k_Chest, k_Neck).Valid,
                           "a NaN bone position must not yield a 'valid' frame");
        }

        // -------------------------------------------------------------------------------------------------

        static Vector3 MirrorX(Vector3 v) => new Vector3(-v.x, v.y, v.z);

        static Vector3 RandomInBall(System.Random rng, float radius)
        {
            for (int i = 0; i < 64; i++)
            {
                var v = new Vector3(
                    (float)(rng.NextDouble() * 2.0 - 1.0),
                    (float)(rng.NextDouble() * 2.0 - 1.0),
                    (float)(rng.NextDouble() * 2.0 - 1.0));
                if (v.sqrMagnitude > 1e-4f && v.sqrMagnitude <= 1f)
                {
                    return v * radius;
                }
            }
            return new Vector3(radius, 0f, 0f);
        }
    }
}
