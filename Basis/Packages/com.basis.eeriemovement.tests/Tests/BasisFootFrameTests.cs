using Basis.Scripts.Drivers;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
namespace Basis.Tests.IK
{
    public class BasisFootFrameTests
    {
        const float TolDeg = 0.25f;      // generous vs the 90-180 deg defects these gates catch
        // Quaternion round-trip noise floor. LookRotation -> inverse -> mul in float32 lands ~0.06 deg off for an
        // arbitrary bone basis, so a tolerance under that fails on arithmetic rather than on behaviour (it did:
        // 0.056 deg against a 0.05 deg gate). 0.2 deg clears the noise while staying ~450x under the smallest
        // real defect these gates exist to catch -- a toes-up foot is wrong by 90 degrees, not by a fraction of one.
        const float QuatTolDeg = 0.2f;
        static readonly float3 Up = new float3(0f, 1f, 0f);
        // Pitches to sweep. The poles (+/-90) and their immediate neighbours are the whole point: that is where
        // the forward axis collapses and where every previous guard either amplified noise or dropped the bone.
        static readonly float[] Pitches =
            { -90f, -89.9f, -89f, -85f, -72f, -71f, -45f, -18.5f, -18.4f, 0f, 18.4f, 18.5f, 45f, 71f, 72f, 85f, 89f, 89.9f, 90f };
        static readonly float[] Yaws = { 0f, 37f, 90f, 143f, 180f, -120f };
        // ---- builders -------------------------------------------------------------------------------------
        static BasisFootSimParams HeadOnlyParams()
        {
            return new BasisFootSimParams
            {
                bodyFwdHipsWeight = 0f,
                bodyFwdChestWeight = 0f,
                bodyFwdHeadWeight = 1f,
                maxFootTiltDegrees = 45f,
                maxFootYawDegrees = 45f,
            };
        }
        static quaternion HeadRot(float yawDeg, float pitchDeg)
        {
            quaternion yaw = quaternion.AxisAngle(Up, math.radians(yawDeg));
            quaternion pitch = quaternion.AxisAngle(new float3(1f, 0f, 0f), math.radians(pitchDeg));
            return math.mul(yaw, pitch);
        }
        static float3 YawDir(float yawDeg)
        {
            return math.mul(quaternion.AxisAngle(Up, math.radians(yawDeg)), new float3(0f, 0f, 1f));
        }
        static BasisFootSimInput HeadInput(quaternion headRot, float3 avatarForward)
        {
            return new BasisFootSimInput
            {
                dt = 1f / 90f,
                hipsRot = quaternion.identity,
                chestRot = quaternion.identity,
                headRot = headRot,
                avatarForward = avatarForward,
                avatarRight = new float3(1f, 0f, 0f),
                hasChest = false,
                playerUp = Up,
            };
        }
        static float AngleDeg(float3 a, float3 b)
        {
            float d = math.clamp(math.dot(math.normalize(a), math.normalize(b)), -1f, 1f);
            return math.degrees(math.acos(d));
        }
        static bool LegacyHeadYaw(quaternion rot, float3 up, out float3 yawDir)
        {
            float3 fwd = math.mul(rot, new float3(0f, 0f, 1f)), flat = fwd - up * math.dot(fwd, up);
            if (math.lengthsq(flat) > 0.1f)
            {
                yawDir = math.normalize(flat);
                return true;
            }
            yawDir = float3.zero;
            return false;
        }
        // ---- T8: the poles --------------------------------------------------------------------------------
        [Test]
        public void BodyForward_TracksYaw_AtEveryPitch_IncludingThePoles()
        {
            BasisFootSimParams p = HeadOnlyParams();
            BasisFootSimState sim = default;

            foreach (float yaw in Yaws)
            {
                float3 expected = YawDir(yaw);
                // Deliberately WRONG fallback: if the head's contribution is ever dropped, totalWeight hits zero
                // and ComputeBodyForward returns avatarForward. Pointing it 90 degrees away means that failure
                // cannot masquerade as a pass.
                float3 fallback = YawDir(yaw + 90f);

                foreach (float pitch in Pitches)
                {
                    BasisFootSimInput inp = HeadInput(HeadRot(yaw, pitch), fallback);
                    float3 fwd = BasisFootSimulateJob.ComputeBodyForward(inp, sim, p, Up);

                    Assert.IsFalse(math.any(math.isnan(fwd)), $"NaN body forward at yaw={yaw} pitch={pitch}");
                    Assert.AreEqual(1f, math.length(fwd), 1e-3f, $"body forward not unit at yaw={yaw} pitch={pitch}");

                    float err = AngleDeg(fwd, expected);
                    Assert.Less(err, TolDeg, $"body forward is {err:F2} deg off the head's yaw at yaw={yaw} pitch={pitch} " +"(the forward axis is degenerate here -- the yaw lives on the UP axis)");
                }
            }
        }
        [Test]
        public void BodyForward_IsContinuous_AcrossThePitchSweep()
        {
            BasisFootSimParams p = HeadOnlyParams();
            BasisFootSimState sim = default;
            const float yaw = 37f;
            float3 fallback = YawDir(yaw + 90f), prev = float3.zero;
            bool first = true;
            float worst = 0f, worstAt = 0f;

            for (float pitch = -90f; pitch <= 90f; pitch += 0.5f)
            {
                BasisFootSimInput inp = HeadInput(HeadRot(yaw, pitch), fallback);
                float3 fwd = BasisFootSimulateJob.ComputeBodyForward(inp, sim, p, Up);

                if (!first)
                {
                    float step = AngleDeg(prev, fwd);
                    if (step > worst) { worst = step; worstAt = pitch; }
                }
                prev = fwd;
                first = false;
            }

            Assert.Less(worst, 1f, $"body forward jumped {worst:F2} deg over a 0.5 deg pitch step (at pitch={worstAt}) -- " +"a bone is being hard-cut in or out of the average");
        }
        [Test]
        public void Legacy_FlattenedForward_LosesTheYaw_LookingStraightDown()
        {
            // Straight down: the forward projects to nothing, so the legacy guard rejects the bone outright.
            Assert.IsFalse(LegacyHeadYaw(HeadRot(37f, 90f), Up, out _),"legacy formula was expected to drop the head when looking straight down");
            Assert.IsFalse(LegacyHeadYaw(HeadRot(37f, -90f), Up, out _),"legacy formula was expected to drop the head when looking straight up");

            // And it drops the head far earlier than the pole -- anywhere past ~72 degrees of pitch, where the
            // facing is still perfectly well defined. That is the discontinuity users feel as the forward lurching.
            Assert.IsFalse(LegacyHeadYaw(HeadRot(37f, 75f), Up, out _),"legacy formula was expected to drop the head at 75 deg pitch, well before the pole");

            // BoneYaw recovers the yaw exactly in every one of those poses.
            foreach (float pitch in new[] { 75f, -75f, 90f, -90f })
            {
                Assert.IsTrue(BasisFootSimulateJob.BoneYaw(HeadRot(37f, pitch), Up, out float3 yawDir), $"BoneYaw failed at pitch={pitch}");
                float err = AngleDeg(yawDir, YawDir(37f));
                Assert.Less(err, TolDeg, $"BoneYaw is {err:F2} deg off at pitch={pitch}");
            }
        }
        // ---- T4: rest-pose reproduction (kills toes-up forever) --------------------------------------------
        [Test]
        public void FootRotation_AtRest_ReproducesTheBonesTposeRotation_OnAnyRig()
        {
            var job = new BasisFootSimulateJob { p = HeadOnlyParams() };

            float3 restFwd = new float3(0f, 0f, 1f);
            quaternion restFrame = quaternion.LookRotation(restFwd, Up);

            // Foot bone orientations from rigs that actually exist in the wild: +Z down the shin, +Z out along the
            // toes with a roll, a toed-out foot, and an arbitrary basis. None of them are the body's axes.
            quaternion[] boneRestRots =
            {
                quaternion.identity,
                quaternion.Euler(math.radians(90f), 0f, 0f),
                quaternion.Euler(math.radians(-90f), math.radians(12f), math.radians(180f)),
                quaternion.Euler(math.radians(17f), math.radians(-8f), math.radians(63f)),
                quaternion.AxisAngle(math.normalize(new float3(1f, 2f, 3f)), 2.1f),
            };

            foreach (quaternion boneRest in boneRestRots)
            {
                quaternion footAlign = math.mul(math.inverse(restFrame), boneRest);

                // Standing: body forward is the rest forward, ground normal is up, no swing pitch.
                quaternion produced = job.FootRotation(restFwd, Up, Up, footAlign, 0f);
                float err = math.degrees(math.abs(2f * math.acos(math.clamp(math.abs(math.dot(produced.value, boneRest.value)), -1f, 1f))));
                Assert.Less(err, QuatTolDeg, $"a standing foot did not reproduce its own T-pose rotation (off by {err:F3} deg) -- " +"this is the toes-up bug: the body frame is being handed to the bone unmapped");
            }
        }
        [Test]
        public void FootRotation_YawsExactlyWithTheBody()
        {
            var job = new BasisFootSimulateJob { p = HeadOnlyParams() };

            quaternion restFrame = quaternion.LookRotation(new float3(0f, 0f, 1f), Up);
            quaternion boneRest = quaternion.Euler(math.radians(-90f), math.radians(12f), math.radians(180f));
            quaternion footAlign = math.mul(math.inverse(restFrame), boneRest);

            foreach (float yaw in Yaws)
            {
                quaternion expected = math.mul(quaternion.AxisAngle(Up, math.radians(yaw)), boneRest);
                quaternion produced = job.FootRotation(YawDir(yaw), Up, Up, footAlign, 0f);
                float err = math.degrees(math.abs(2f * math.acos(math.clamp(math.abs(math.dot(produced.value, expected.value)), -1f, 1f))));
                Assert.Less(err, QuatTolDeg, $"foot did not yaw with the body at yaw={yaw} (off by {err:F3} deg)");
            }
        }
        [Test]
        public void SwingAnklePitch_PlantarflexesAtToeOff_DorsiflexesAtHeelStrike()
        {
            float toeOff = BasisFootSimulateJob.SwingAnklePitchDeg(0f);
            float mid = BasisFootSimulateJob.SwingAnklePitchDeg(0.5f);
            float heelStrike = BasisFootSimulateJob.SwingAnklePitchDeg(1f);

            Assert.Greater(toeOff, 5f, "the foot should push off plantarflexed (toes down)");
            Assert.Less(heelStrike, -5f, "the foot should present the heel dorsiflexed (toes up)");
            Assert.Less(math.abs(mid), math.max(toeOff, -heelStrike),"mid-swing should be closer to neutral than either end");
        }
        // ---- T3: NaN must not escape ----------------------------------------------------------------------
        [Test]
        public void BodyForward_NeverEmitsNaN_OnDegenerateInput()
        {
            BasisFootSimParams p = HeadOnlyParams();
            BasisFootSimState sim = default;
            float3 sane = new float3(0f, 0f, 1f);

            var cases = new (string name, BasisFootSimInput inp, float3 up)[]
            {
                ("zero quaternion (default(quaternion) is all zeros, NOT identity)", HeadInput(new quaternion(0f, 0f, 0f, 0f), sane), Up),
                ("NaN rotation", HeadInput(new quaternion(float.NaN, float.NaN, float.NaN, float.NaN), sane), Up),
                ("infinite rotation", HeadInput(new quaternion(float.PositiveInfinity, 0f, 0f, 1f), sane), Up),
                ("zero-length playerUp", HeadInput(HeadRot(0f, 0f), sane), float3.zero),
                ("NaN playerUp", HeadInput(HeadRot(0f, 0f), sane), new float3(float.NaN, float.NaN, float.NaN)),
                ("zero avatarForward fallback", HeadInput(new quaternion(0f, 0f, 0f, 0f), float3.zero), Up),
            };

            foreach ((string name, BasisFootSimInput inp, float3 up) in cases)
            {
                float3 fwd = BasisFootSimulateJob.ComputeBodyForward(inp, sim, p, up);
                Assert.IsFalse(math.any(math.isnan(fwd)), $"NaN escaped ComputeBodyForward: {name}");
                Assert.IsFalse(math.any(math.isinf(fwd)), $"Inf escaped ComputeBodyForward: {name}");
            }
        }
        [Test]
        public void NaN_DefeatsTheRejectIfBadGuardShape_ButNotRejectUnlessGood()
        {
            float nan = float.NaN;

            Assert.IsFalse(nan < 0.5f, "the trap: NaN < x is false, so 'reject if bad' FAILS OPEN on NaN");
            Assert.IsTrue(!(nan > 0.5f), "the fix: !(good > x) is true for NaN, so it lands in the reject branch");
        }
        // ── 4. THE CALIBRATION-OFFSET ROUND TRIP ─────────────────────────────────────────────────────────────
        //
        // `data.LeftFootRotation` is a field with TWO incompatible meanings and nothing in the type system to tell
        // them apart:
        //   - the TRACKER path writes a bone-CONTROL rotation, and SolveTwoBone's `target * targetOffset` is what
        //     converts it into the bone's frame. Correct, and load-bearing.
        //   - the FOOT DRIVER writes an already-finished BONE rotation. That same multiply is then pure surplus,
        //     and the foot lands at footRot*offset -- wrong by a whole calibrated rotation.
        //
        // Because the offset is calibrated PER AVATAR, the symptom is a different wrong angle on every rig, which
        // reads like a tuning problem rather than a frame bug. That is precisely how it survived: it does not look
        // like a bug, it looks like bad numbers. (The same double-offset was found in MediaPipe's hands, where the
        // producer likewise baked the bone's rest rotation into what it handed the tracker.)
        //
        // The rule these gates lock down: WHATEVER WE HAND THE SOLVE, IT MUST SURVIVE THE SOLVE'S OWN MULTIPLY.
        // A per-avatar calibration offset is an arbitrary rotation -- it maps a tracker/landmark frame onto
        // whatever axes this particular rig gave its foot bone. These stand in for "several different rigs".
        private static readonly Quaternion[] offsets =
        {
            Quaternion.identity,
            Quaternion.Euler(0f, 0f, 90f),
            Quaternion.Euler(90f, 0f, 0f),
            Quaternion.Euler(-73f, 14f, 122f),
            Quaternion.Euler(180f, 0f, 0f),
        };
        private static readonly Quaternion[] boneRotations =
        {
            Quaternion.identity,
            Quaternion.Euler(12f, 47f, -8f),
            Quaternion.Euler(-31f, 160f, 5f),
            Quaternion.Euler(3f, -120f, 44f),
        };
        [Test]
        public void FootTargetRotation_SurvivesTheSolvesOwnOffsetMultiply()
        {
            foreach (Quaternion bone in boneRotations)
            {
                foreach (Quaternion offset in offsets)
                {
                    Assert.IsTrue(BasisLocalRigDriver.TryFootTargetRotation(bone, offset, out Quaternion target), $"valid inputs must not be rejected (offset={offset.eulerAngles})");

                    // Exactly what SolveTwoBone does to the target we just handed it.
                    Quaternion solved = target * offset;

                    Assert.Less(Quaternion.Angle(solved, bone), 0.5f, $"the offset did not cancel: bone={bone.eulerAngles} offset={offset.eulerAngles} solved={solved.eulerAngles}");
                }
            }
        }
        [Test]
        public void FootTargetRotation_NaiveVersionIsWrongByExactlyTheOffset()
        {
            Quaternion bone = Quaternion.Euler(12f, 47f, -8f), offset = Quaternion.Euler(0f, 0f, 90f);

            Quaternion naive = bone * offset;             // no pre-cancellation: what the bug produced
            Quaternion errorIntroduced = Quaternion.Inverse(bone) * naive;

            Assert.Greater(Quaternion.Angle(naive, bone), 45f,"the naive path must be badly wrong -- if it isn't, the round-trip gate above is vacuous");
            Assert.Less(Quaternion.Angle(errorIntroduced, offset), 0.5f,"and the error introduced is precisely the calibration offset -- that is the double-offset signature");
        }
        [Test]
        public void FootTargetRotation_DegenerateOffset_IsRejectedNotNaN()
        {
            Quaternion bone = Quaternion.Euler(12f, 47f, -8f);

            var zero = new Quaternion(0f, 0f, 0f, 0f);
            var nan = new Quaternion(float.NaN, float.NaN, float.NaN, float.NaN);

            Assert.IsFalse(BasisLocalRigDriver.TryFootTargetRotation(bone, zero, out _),"a zero-quaternion offset (the serialized default) must be rejected");
            Assert.IsFalse(BasisLocalRigDriver.TryFootTargetRotation(bone, nan, out _),"a NaN offset must be rejected -- this is the guard whose `<` form failed open");
        }
        [Test]
        public void FootTargetRotation_DegenerateFootRotation_IsRejectedNotNaN()
        {
            Quaternion offset = Quaternion.Euler(-73f, 14f, 122f);

            var zero = new Quaternion(0f, 0f, 0f, 0f);
            var nan = new Quaternion(float.NaN, float.NaN, float.NaN, float.NaN);

            Assert.IsFalse(BasisLocalRigDriver.TryFootTargetRotation(zero, offset, out _),"a zero foot rotation must be rejected");
            Assert.IsFalse(BasisLocalRigDriver.TryFootTargetRotation(nan, offset, out _),"a NaN foot rotation must be rejected");
        }
    }
}
