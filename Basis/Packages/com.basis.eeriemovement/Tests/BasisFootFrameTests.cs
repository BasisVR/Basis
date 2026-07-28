using Basis.Scripts.Drivers;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;

namespace Basis.Tests.IK
{
    /// <summary>
    /// Frame gates for the foot-placement sim (<see cref="BasisFootSimulateJob"/>).
    ///
    /// Every bug this file exists to catch was the same shape: a rotation read in the wrong frame, or read off an
    /// axis that had gone degenerate. They are grouped here because they share a root cause, not a subsystem.
    ///
    /// 1. BODY FORWARD AT THE POLES. Every bone's facing was obtained by flattening its forward onto the ground
    ///    plane: flat = fwd - up*dot(fwd,up). That is only meaningful while the forward HAS horizontal length.
    ///    Look straight down (or up) and the head's forward is parallel to playerUp, the projection collapses to
    ///    ~zero, and normalize() turns float noise into a wildly swinging yaw. The old guards let this through --
    ///    hips and chest required only lengthsq > 0.001, i.e. a mere 1.8 degrees off vertical -- while the head's
    ///    stricter lengthsq > 0.1 guard simply DROPPED the head past ~72 degrees of pitch, so the body forward
    ///    lurched as the head crossed the threshold. Neither is a facing; both are artifacts.
    ///
    ///    The yaw is not lost at the pole, it has only changed axes: when a bone's FORWARD goes vertical, its UP
    ///    axis becomes HORIZONTAL and carries the yaw exactly. BoneYaw() reads whichever axis is currently
    ///    horizontal and cross-fades by |flat(fwd)|. These gates sweep pitch through the poles INCLUDING exactly
    ///    +/-90 degrees, because the specific way today's cervical bug hid was a threshold branch that the test
    ///    never crossed. Enumerate the branches; cross every threshold.
    ///
    /// 2. FOOT ROTATION AT REST. FootRotation() builds a frame from the BODY's axes. A humanoid foot bone's local
    ///    axes are NOT the body's -- its +Z commonly runs down the shin or out along the toes, entirely
    ///    rig-dependent -- so returning that frame as the bone's rotation is what came out "toes-up" and got foot
    ///    rotation switched off in the first place. Re-seating through footAlign (the bone measured IN that frame
    ///    at the T-pose) makes a standing foot reproduce its rest rotation EXACTLY. That is an identity, not a
    ///    tuning: assert it as one, and toes-up can never come back.
    ///
    /// 3. NaN MUST NOT ESCAPE. A NaN'd transform in Unity PERSISTS -- the rig dies and never recovers -- so a
    ///    single degenerate frame is unrecoverable rather than a blip. Worse, the obvious guard shape is wrong:
    ///    `if (bad &lt; threshold) reject` FAILS OPEN on NaN, because NaN &lt; x is false for every x. Validity checks
    ///    must be written as `!(good &gt; threshold)` so NaN falls into the reject branch.
    /// </summary>
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

        /// <summary>Head-only weighting, so these gates measure the HEAD's contribution and nothing else.</summary>
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

        /// <summary>Yaw about playerUp, then pitch about the resulting local right axis -- an ordinary head.</summary>
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

        /// <summary>The formula BoneYaw replaced: flatten the forward, and hard-cut when it gets short.</summary>
        static bool LegacyHeadYaw(quaternion rot, float3 up, out float3 yawDir)
        {
            float3 fwd = math.mul(rot, new float3(0f, 0f, 1f));
            float3 flat = fwd - up * math.dot(fwd, up);
            if (math.lengthsq(flat) > 0.1f)
            {
                yawDir = math.normalize(flat);
                return true;
            }
            yawDir = float3.zero;
            return false;
        }

        // ---- T8: the poles --------------------------------------------------------------------------------

        /// <summary>
        /// The body forward must equal the head's YAW at every pitch, including exactly straight up and straight
        /// down. A pure pitch changes where you are looking; it does not change which way you are FACING.
        /// </summary>
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
                    Assert.Less(err, TolDeg,
                        $"body forward is {err:F2} deg off the head's yaw at yaw={yaw} pitch={pitch} " +
                        "(the forward axis is degenerate here -- the yaw lives on the UP axis)");
                }
            }
        }

        /// <summary>
        /// No pops. Sweeping pitch finely, the body forward must never jump -- a discontinuity means a guard is
        /// hard-cutting a bone in or out of the average, which is exactly what the old thresholds did.
        /// </summary>
        [Test]
        public void BodyForward_IsContinuous_AcrossThePitchSweep()
        {
            BasisFootSimParams p = HeadOnlyParams();
            BasisFootSimState sim = default;

            const float yaw = 37f;
            float3 fallback = YawDir(yaw + 90f);
            float3 prev = float3.zero;
            bool first = true;
            float worst = 0f;
            float worstAt = 0f;

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

            Assert.Less(worst, 1f,
                $"body forward jumped {worst:F2} deg over a 0.5 deg pitch step (at pitch={worstAt}) -- " +
                "a bone is being hard-cut in or out of the average");
        }

        /// <summary>
        /// The paired legacy gate. Runs the OLD flatten-and-hard-cut formula and asserts it FAILS at the poles,
        /// so the numbers above are measured rather than asserted into existence -- and so a revert to the old
        /// formula fails loudly instead of silently.
        /// </summary>
        [Test]
        public void Legacy_FlattenedForward_LosesTheYaw_LookingStraightDown()
        {
            // Straight down: the forward projects to nothing, so the legacy guard rejects the bone outright.
            Assert.IsFalse(LegacyHeadYaw(HeadRot(37f, 90f), Up, out _),
                "legacy formula was expected to drop the head when looking straight down");
            Assert.IsFalse(LegacyHeadYaw(HeadRot(37f, -90f), Up, out _),
                "legacy formula was expected to drop the head when looking straight up");

            // And it drops the head far earlier than the pole -- anywhere past ~72 degrees of pitch, where the
            // facing is still perfectly well defined. That is the discontinuity users feel as the forward lurching.
            Assert.IsFalse(LegacyHeadYaw(HeadRot(37f, 75f), Up, out _),
                "legacy formula was expected to drop the head at 75 deg pitch, well before the pole");

            // BoneYaw recovers the yaw exactly in every one of those poses.
            foreach (float pitch in new[] { 75f, -75f, 90f, -90f })
            {
                Assert.IsTrue(BasisFootSimulateJob.BoneYaw(HeadRot(37f, pitch), Up, out float3 yawDir),
                    $"BoneYaw failed at pitch={pitch}");
                float err = AngleDeg(yawDir, YawDir(37f));
                Assert.Less(err, TolDeg, $"BoneYaw is {err:F2} deg off at pitch={pitch}");
            }
        }

        // ---- T4: rest-pose reproduction (kills toes-up forever) --------------------------------------------

        /// <summary>
        /// At rest, FootRotation must reproduce the foot bone's T-pose world rotation EXACTLY.
        ///
        /// footAlign is the bone measured in the rest frame: inverse(restFrame) * boneRestRot. So at rest, where
        /// the target frame IS the rest frame, frame * footAlign collapses to boneRestRot by construction. This
        /// holds for ANY rig -- the foot bone's axes never enter into it, which is the entire point. A rig whose
        /// foot bone is rotated 90 degrees from the body axes (most of them) must still stand flat.
        /// </summary>
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
                Assert.Less(err, QuatTolDeg,
                    $"a standing foot did not reproduce its own T-pose rotation (off by {err:F3} deg) -- " +
                    "this is the toes-up bug: the body frame is being handed to the bone unmapped");
            }
        }

        /// <summary>
        /// The same identity must survive the body being somewhere else in the world. Yaw the body and the foot
        /// must yaw WITH it -- no more, no less. (A foot that rotates by anything other than the body's own yaw
        /// is a foot picking up an offset from somewhere it should not.)
        /// </summary>
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

        /// <summary>Toe-off is plantarflexed (toes DOWN, positive), heel-strike is dorsiflexed (toes UP, negative).</summary>
        [Test]
        public void SwingAnklePitch_PlantarflexesAtToeOff_DorsiflexesAtHeelStrike()
        {
            float toeOff = BasisFootSimulateJob.SwingAnklePitchDeg(0f);
            float mid = BasisFootSimulateJob.SwingAnklePitchDeg(0.5f);
            float heelStrike = BasisFootSimulateJob.SwingAnklePitchDeg(1f);

            Assert.Greater(toeOff, 5f, "the foot should push off plantarflexed (toes down)");
            Assert.Less(heelStrike, -5f, "the foot should present the heel dorsiflexed (toes up)");
            Assert.Less(math.abs(mid), math.max(toeOff, -heelStrike),
                "mid-swing should be closer to neutral than either end");
        }

        // ---- T3: NaN must not escape ----------------------------------------------------------------------

        /// <summary>
        /// Degenerate inputs must never produce a NaN facing. A NaN'd transform PERSISTS in Unity -- the rig dies
        /// and never recovers -- so this is not a cosmetic concern, it is the difference between a bad frame and
        /// a bricked avatar.
        /// </summary>
        [Test]
        public void BodyForward_NeverEmitsNaN_OnDegenerateInput()
        {
            BasisFootSimParams p = HeadOnlyParams();
            BasisFootSimState sim = default;
            float3 sane = new float3(0f, 0f, 1f);

            var cases = new (string name, BasisFootSimInput inp, float3 up)[]
            {
                ("zero quaternion (default(quaternion) is all zeros, NOT identity)",
                    HeadInput(new quaternion(0f, 0f, 0f, 0f), sane), Up),
                ("NaN rotation",
                    HeadInput(new quaternion(float.NaN, float.NaN, float.NaN, float.NaN), sane), Up),
                ("infinite rotation",
                    HeadInput(new quaternion(float.PositiveInfinity, 0f, 0f, 1f), sane), Up),
                ("zero-length playerUp",
                    HeadInput(HeadRot(0f, 0f), sane), float3.zero),
                ("NaN playerUp",
                    HeadInput(HeadRot(0f, 0f), sane), new float3(float.NaN, float.NaN, float.NaN)),
                ("zero avatarForward fallback",
                    HeadInput(new quaternion(0f, 0f, 0f, 0f), float3.zero), Up),
            };

            foreach ((string name, BasisFootSimInput inp, float3 up) in cases)
            {
                float3 fwd = BasisFootSimulateJob.ComputeBodyForward(inp, sim, p, up);
                Assert.IsFalse(math.any(math.isnan(fwd)), $"NaN escaped ComputeBodyForward: {name}");
                Assert.IsFalse(math.any(math.isinf(fwd)), $"Inf escaped ComputeBodyForward: {name}");
            }
        }

        /// <summary>
        /// The guard-shape gate, stated as an executable fact rather than a comment.
        ///
        /// `NaN &lt; 0.5f` is FALSE. So a validity check written as "reject if bad" waves NaN straight through --
        /// which is exactly how a guard that was written to stop a zero quaternion still let a NaN into the bones.
        /// The correct shape is "reject unless good": !(x &gt; threshold), which IS true for NaN.
        /// </summary>
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
        private static readonly Quaternion[] k_Offsets =
        {
            Quaternion.identity,
            Quaternion.Euler(0f, 0f, 90f),
            Quaternion.Euler(90f, 0f, 0f),
            Quaternion.Euler(-73f, 14f, 122f),
            Quaternion.Euler(180f, 0f, 0f),
        };

        private static readonly Quaternion[] k_BoneRotations =
        {
            Quaternion.identity,
            Quaternion.Euler(12f, 47f, -8f),
            Quaternion.Euler(-31f, 160f, 5f),
            Quaternion.Euler(3f, -120f, 44f),
        };

        private static bool IsSentinel(Quaternion q) => q.x == 0f && q.y == 0f && q.z == 0f && q.w == 0f;

        /// <summary>
        /// THE CONTRACT. SolveTwoBone will multiply whatever we give it by the per-avatar offset. So the value we
        /// hand over, once that multiply has happened, must be the bone rotation we actually meant:
        ///
        ///     (footRot * offset^-1) * offset  ==  footRot
        ///
        /// Tolerance is 0.5 degrees: an inverse plus two quaternion multiplies in float32 leaves ~0.1 deg of
        /// round-trip noise. A real double-offset is tens of degrees (see the legacy gate below), so this is ~2
        /// orders of magnitude clear of the defect it guards -- loose enough not to be flaky, nowhere near loose
        /// enough to let the bug back through.
        /// </summary>
        [Test]
        public void FootTargetRotation_SurvivesTheSolvesOwnOffsetMultiply()
        {
            foreach (Quaternion bone in k_BoneRotations)
            {
                foreach (Quaternion offset in k_Offsets)
                {
                    Quaternion target = BasisLocalRigDriver.SafeFootTargetRotation(bone, offset);
                    Assert.IsFalse(IsSentinel(target), $"valid inputs must not degrade to the sentinel (offset={offset.eulerAngles})");

                    // Exactly what SolveTwoBone does to the target we just handed it.
                    Quaternion solved = target * offset;

                    Assert.Less(Quaternion.Angle(solved, bone), 0.5f,
                        $"the offset did not cancel: bone={bone.eulerAngles} offset={offset.eulerAngles} solved={solved.eulerAngles}");
                }
            }
        }

        /// <summary>
        /// LEGACY GATE -- proves the test above has teeth.
        ///
        /// If someone "simplifies" the pre-cancellation away and hands the bone rotation straight over, the result
        /// is wrong by exactly the calibration offset. This asserts that the naive version really is badly wrong,
        /// so the round-trip gate cannot pass vacuously (e.g. if every sample offset were identity). A revert then
        /// fails loudly, with a reason, instead of silently reintroducing a bug that took a full day to find.
        /// </summary>
        [Test]
        public void FootTargetRotation_NaiveVersionIsWrongByExactlyTheOffset()
        {
            Quaternion bone = Quaternion.Euler(12f, 47f, -8f);
            Quaternion offset = Quaternion.Euler(0f, 0f, 90f);

            Quaternion naive = bone * offset;             // no pre-cancellation: what the bug produced
            Quaternion errorIntroduced = Quaternion.Inverse(bone) * naive;

            Assert.Greater(Quaternion.Angle(naive, bone), 45f,
                "the naive path must be badly wrong -- if it isn't, the round-trip gate above is vacuous");
            Assert.Less(Quaternion.Angle(errorIntroduced, offset), 0.5f,
                "and the error introduced is precisely the calibration offset -- that is the double-offset signature");
        }

        /// <summary>
        /// A degenerate OFFSET must degrade to the sentinel, never to NaN.
        ///
        /// A serialized Quaternion defaults to (0,0,0,0) -- NOT identity -- and Quaternion.Inverse divides by the
        /// squared norm, so inverting it yields NaN. The window is real: the offset is only assigned when the avatar
        /// has that foot mapped, and recalibration/avatar-swap rewrite it live. And a NaN here is not a glitch, it
        /// is terminal: a NaN'd bone transform in Unity PERSISTS, so the rig dies and never recovers.
        /// </summary>
        [Test]
        public void FootTargetRotation_DegenerateOffset_DegradesToSentinelNotNaN()
        {
            Quaternion bone = Quaternion.Euler(12f, 47f, -8f);

            var zero = new Quaternion(0f, 0f, 0f, 0f);
            var nan = new Quaternion(float.NaN, float.NaN, float.NaN, float.NaN);

            Assert.IsTrue(IsSentinel(BasisLocalRigDriver.SafeFootTargetRotation(bone, zero)),
                "a zero-quaternion offset (the serialized default) must fall back to the sentinel");
            Assert.IsTrue(IsSentinel(BasisLocalRigDriver.SafeFootTargetRotation(bone, nan)),
                "a NaN offset must fall back to the sentinel -- this is the guard whose `<` form failed open");
        }

        /// <summary>
        /// A degenerate FOOT ROTATION must also degrade to the sentinel. The offset can be perfectly valid and the
        /// producer still hand us garbage (e.g. a LookRotation built on a collapsed frame), so guarding the input
        /// alone is not enough -- the RESULT has to be checked too.
        /// </summary>
        [Test]
        public void FootTargetRotation_DegenerateFootRotation_DegradesToSentinelNotNaN()
        {
            Quaternion offset = Quaternion.Euler(-73f, 14f, 122f);

            var zero = new Quaternion(0f, 0f, 0f, 0f);
            var nan = new Quaternion(float.NaN, float.NaN, float.NaN, float.NaN);

            Assert.IsTrue(IsSentinel(BasisLocalRigDriver.SafeFootTargetRotation(zero, offset)),
                "a zero foot rotation must fall back to the sentinel");
            Assert.IsTrue(IsSentinel(BasisLocalRigDriver.SafeFootTargetRotation(nan, offset)),
                "a NaN foot rotation must fall back to the sentinel");
        }
    }
}
