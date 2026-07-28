using Basis.IK;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;

namespace Basis.Tests.IK
{
    /// <summary>
    /// "Look down at my chest, turn my head left and right across the middle, and the body SNAPS"
    /// regression tests for the torso heading -- <see cref="BasisVirtualSpineCore.ExtractYawBurst"/>.
    ///
    /// That function is the single source of the torso's facing direction: its output becomes
    /// torsoYawTarget, which aims the hips, the hips' forward bias, and the chest/spine yaw targets.
    /// It used to flatten the head's FORWARD axis into the horizontal plane and take its azimuth --
    /// but forward carries NO azimuth once the gaze is vertical, so the azimuth's gain is 1/cos(gazePitch):
    /// 5.8x at 80 degrees of look-down and unbounded at 90, where it flips a full 180 degrees as the gaze
    /// crosses the pole. Chin tucked to the chest, a 10-degree head turn threw the torso 183 degrees.
    /// The 1e-12 guard was orders of magnitude too small to fire; the damage lands long before the
    /// projection is literally zero.
    ///
    /// Taking the yaw as the TWIST of a swing-twist decomposition about world up has no projection to
    /// collapse. Its gain is 1/cos(gazePitch / 2) -- bounded by sqrt(2) over every gaze a head can reach --
    /// so the singularity moves from 90 degrees (which you hit looking at your own chest) out to 180
    /// (upside down and facing backwards, which you cannot).
    ///
    /// Sign convention matches Unity and the live job: a positive rotation about X is look-DOWN,
    /// a positive rotation about Y is turn-RIGHT.
    /// </summary>
    public class BasisSpineYawPoleTests
    {
        // 1 / cos(90 deg / 2) == sqrt(2): the exact worst-case gain of the twist over the reachable
        // workspace. The gate sits just above it, and an order of magnitude below what the old
        // forward-azimuth form produced at the same poses.
        const float MaxYawGain = 1.6f;

        /// <summary>
        /// The head as an HMD reports it while the chin is tucked: the neck flexes forward, and the
        /// left/right turn is an axial rotation about that ALREADY-FLEXED neck axis (which is what the
        /// cervical spine actually does). The flexed turn axis is what tips head-forward through the
        /// vertical pole -- turn a head about a perfectly upright axis and no extractor is in trouble.
        /// </summary>
        static quaternion Head(float neckFlexDeg, float turnDeg, float headPitchDeg)
        {
            return math.mul(math.mul(
                quaternion.AxisAngle(new float3(1f, 0f, 0f), math.radians(neckFlexDeg)),
                quaternion.AxisAngle(new float3(0f, 1f, 0f), math.radians(turnDeg))),
                quaternion.AxisAngle(new float3(1f, 0f, 0f), math.radians(headPitchDeg)));
        }

        static float TorsoYawDeg(quaternion head)
        {
            BasisVirtualSpineCore.ExtractYawBurst(head, out quaternion yawOnly);
            BasisVirtualSpineCore.YawDegrees(yawOnly, out float deg);
            return deg;
        }

        // ----------------------------------------------------------- the headline: the reported snap

        [Test]
        public void TorsoYaw_DoesNotSnap_WhenTheHeadSweepsAcrossTheChest()
        {
            // The user's exact motion, at gaze depths from a casual glance down to a full chin-tuck.
            foreach (float gaze in new[] { 60f, 75f, 80f, 85f, 90f })
            {
                float neck = gaze * 0.55f;
                float headPitch = gaze * 0.45f;

                float worstStep = 0f;
                float worstAt = 0f;
                float prev = TorsoYawDeg(Head(neck, -45f, headPitch));

                for (float turn = -44f; turn <= 45f; turn += 1f)
                {
                    float cur = TorsoYawDeg(Head(neck, turn, headPitch));
                    float step = Mathf.Abs(Mathf.DeltaAngle(prev, cur));
                    if (step > worstStep)
                    {
                        worstStep = step;
                        worstAt = turn;
                    }
                    prev = cur;
                }

                // 1 deg of head turn may not throw the torso more than MaxYawGain degrees, anywhere in
                // the sweep -- least of all crossing the middle of the chest, where the old form flipped.
                Assert.That(worstStep, Is.LessThan(MaxYawGain),
                    $"torso heading SNAPPED at {gaze:0} deg of look-down: 1 deg of head turn moved it " +
                    $"{worstStep:0.0} deg (at turn = {worstAt:0} deg, i.e. {(Mathf.Abs(worstAt) < 2f ? "crossing the middle of the chest" : "mid-sweep")}).");
            }
        }

        [Test]
        public void TorsoYaw_DoesNotSnap_WhenTheHeadSweepsAcrossTheCeiling()
        {
            // Same pole, other end: the forward azimuth is just as undefined looking straight up.
            foreach (float gaze in new[] { -80f, -90f })
            {
                float prev = TorsoYawDeg(Head(gaze * 0.55f, -45f, gaze * 0.45f));
                float worstStep = 0f;

                for (float turn = -44f; turn <= 45f; turn += 1f)
                {
                    float cur = TorsoYawDeg(Head(gaze * 0.55f, turn, gaze * 0.45f));
                    worstStep = Mathf.Max(worstStep, Mathf.Abs(Mathf.DeltaAngle(prev, cur)));
                    prev = cur;
                }

                Assert.That(worstStep, Is.LessThan(MaxYawGain),
                    $"torso heading snapped at {Mathf.Abs(gaze):0} deg of look-UP: 1 deg of head turn moved it {worstStep:0.0} deg.");
            }
        }

        // ----------------------------------------------------------- the structural guard

        [Test]
        public void TorsoYawGain_StaysBounded_AtEveryGazeAHeadCanReach()
        {
            // Model-free, and the strongest statement of the fix: NO small head motion -- turning, tilting,
            // nodding, or raw HMD tracking jitter -- may move the torso heading much more than itself.
            // The old form's gain ran to 1/cos(gazePitch), so a head merely HOLDING STILL while looking
            // down at its own chest had its tracker noise amplified ~700x into the hips.
            const float delta = 0.25f;
            float worstGain = 0f;
            string worstPose = "";

            for (float pitch = -90f; pitch <= 90f; pitch += 5f)
            {
                for (float yaw = -180f; yaw < 180f; yaw += 30f)
                {
                    for (float roll = -30f; roll <= 30f; roll += 15f)
                    {
                        quaternion head = math.mul(math.mul(
                            quaternion.AxisAngle(new float3(0f, 1f, 0f), math.radians(yaw)),
                            quaternion.AxisAngle(new float3(1f, 0f, 0f), math.radians(pitch))),
                            quaternion.AxisAngle(new float3(0f, 0f, 1f), math.radians(roll)));

                        float baseYaw = TorsoYawDeg(head);

                        foreach (float3 axis in new[] { new float3(1f, 0f, 0f), new float3(0f, 1f, 0f), new float3(0f, 0f, 1f) })
                        {
                            quaternion nudged = math.mul(quaternion.AxisAngle(axis, math.radians(delta)), head);
                            float gain = Mathf.Abs(Mathf.DeltaAngle(baseYaw, TorsoYawDeg(nudged))) / delta;
                            if (gain > worstGain)
                            {
                                worstGain = gain;
                                worstPose = $"pitch {pitch:0}, yaw {yaw:0}, roll {roll:0}";
                            }
                        }
                    }
                }
            }

            Assert.That(worstGain, Is.LessThan(MaxYawGain),
                $"torso heading amplifies head motion {worstGain:0.0}x at [{worstPose}] -- a head that is merely " +
                "shaking with tracker noise will drag the hips around. Bound is 1/cos(gazePitch/2) <= sqrt(2).");
        }

        // ----------------------------------------------------------- no regression to ordinary use

        [Test]
        public void TorsoYaw_IsExactlyTheHeadsYaw_WheneverTheHeadIsNotRolled()
        {
            // The no-op guarantee, and why this fix carries no tuning risk: for ANY head rotation without
            // roll -- which is ordinary look-around, and what every spine setting was tuned against -- the
            // twist returns bit-for-bit the same angle the old forward-azimuth did. The two forms only
            // ever disagree where the old one was already broken.
            for (float yaw = -180f; yaw < 180f; yaw += 10f)
            {
                for (float pitch = -90f; pitch <= 90f; pitch += 5f)
                {
                    quaternion head = math.mul(
                        quaternion.AxisAngle(new float3(0f, 1f, 0f), math.radians(yaw)),
                        quaternion.AxisAngle(new float3(1f, 0f, 0f), math.radians(pitch)));

                    Assert.That(Mathf.Abs(Mathf.DeltaAngle(TorsoYawDeg(head), yaw)), Is.LessThan(0.01f),
                        $"extracted torso yaw drifted off the head's actual yaw at pitch {pitch:0}, yaw {yaw:0}.");
                }
            }
        }

        [Test]
        public void TorsoYaw_IsYawOnly_AndIdempotent()
        {
            // The two invariants the offline BasisSpineSweep asserts, pinned here as well because the
            // sweep runner window does not execute NUnit. The chain slerps these together (neck -> hips)
            // and re-extracts them, so a heading that is not a pure yaw, or that shifts when re-extracted,
            // would tilt the whole torso.
            var rng = new System.Random(7);

            for (int i = 0; i < 20000; i++)
            {
                quaternion q = math.normalize(new quaternion(
                    (float)(rng.NextDouble() * 2.0 - 1.0), (float)(rng.NextDouble() * 2.0 - 1.0),
                    (float)(rng.NextDouble() * 2.0 - 1.0), (float)(rng.NextDouble() * 2.0 - 1.0)));

                BasisVirtualSpineCore.ExtractYawBurst(q, out quaternion y1);
                BasisVirtualSpineCore.ExtractYawBurst(y1, out quaternion y2);

                float tilt = Mathf.Abs(math.mul(y1, new float3(0f, 0f, 1f)).y);
                Assert.That(tilt, Is.LessThan(1e-4f), "extracted heading was not a pure yaw (its forward left the horizontal plane).");

                BasisVirtualSpineCore.YawDegrees(y1, out float d1);
                BasisVirtualSpineCore.YawDegrees(y2, out float d2);
                Assert.That(Mathf.Abs(Mathf.DeltaAngle(d1, d2)), Is.LessThan(0.01f), "extracting the heading twice did not return the same heading.");
            }
        }
    }
}
