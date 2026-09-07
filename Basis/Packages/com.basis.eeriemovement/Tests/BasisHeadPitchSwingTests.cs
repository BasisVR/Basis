using NUnit.Framework;
using UnityEngine;
using Basis.IK;
namespace Basis.Tests.IK
{
    public class BasisHeadPitchSwingTests
    {
        // A ~1.7 m avatar: eye 17 cm above the neck joint and 8 cm in front of it.
        static readonly Vector3 EyeFromNeck = new Vector3(0f, 0.17f, 0.08f);
        static Vector3 Swing(float pitchDeg, float yawDeg = 0f, float strength = 1f, float backward = 0.35f, Vector3? eyeFromNeck = null)
        {
            BasisHeadPitchSwingCore.Solve(pitchDeg, yawDeg, eyeFromNeck ?? EyeFromNeck, strength, backward, out Vector3 offset, out _);
            return offset;
        }
        [Test]
        public void LookingStraightAhead_MovesTheHeadNotAtAll()
        {
            Assert.That(Swing(0f).magnitude, Is.LessThan(1e-5f),"a level gaze must leave the eye exactly where it was, or the avatar's rest pose is wrong");
        }
        [Test]
        public void LookingDown_CarriesTheHeadForward()
        {
            float fwd = Swing(45f).z;
            Assert.That(fwd, Is.GreaterThan(0.05f), $"looking 45 deg down carried the eye only {fwd * 100f:0.0} cm forward — this is the whole point: " +"your eyes travel out over your feet when you look at them");
        }
        [Test]
        public void LookingUp_CarriesTheHeadBack()
        {
            Assert.That(Swing(-45f).z, Is.LessThan(0f),"tipping the gaze up must carry the eye BACK; the lever arm does not care which way it swings");
        }
        [Test]
        public void TheBackwardCarry_IsSmallerThanTheForwardOne()
        {
            float down = Swing(50f).z, up = -Swing(-50f).z;

            Assert.That(up, Is.LessThan(down), $"looking up pulled the camera back {up * 100f:0.0} cm against {down * 100f:0.0} cm forward for the " +"same angle down — a look-up must not yank the camera backwards as hard as a look-down pushes it out");
        }
        [Test]
        public void TheCarry_FollowsTheGazeHeading_NotWorldForward()
        {
            foreach (float yaw in new[] { 0f, 90f, 180f, 270f, 37f })
            {
                Vector3 offset = Swing(45f, yaw), heading = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;

                Assert.That(Vector3.Angle(offset.normalized, heading), Is.LessThan(0.5f), $"at yaw {yaw:0} deg the head was carried toward {offset.normalized} instead of along the heading {heading}");
                Assert.That(offset.magnitude, Is.EqualTo(Swing(45f, 0f).magnitude).Within(1e-4f), $"the carry changed SIZE with yaw ({yaw:0} deg) — turning on the spot must not change how far a look-down reaches");
            }
        }
        [Test]
        public void TheCarry_NeverTouchesEyeHeight()
        {
            for (float pitch = -89f; pitch <= 80f; pitch += 7f)
            {
                Assert.That(Swing(pitch).y, Is.EqualTo(0f), $"the head carry moved the eye vertically at pitch {pitch:0} — eye height feeds the avatar rescale loop " +"and must not vary with where you happen to be looking");
            }
        }
        [Test]
        public void TheCarry_IsContinuous_AcrossTheWholePitchRange()
        {
            // Desktop clamps to [-89, 80]. Sweep it finely: any step-change here is a camera pop in the player's face.
            Vector3 prev = Swing(-89f);
            float worst = 0f;
            for (float pitch = -89f; pitch <= 80f; pitch += 0.25f)
            {
                Vector3 cur = Swing(pitch);
                worst = Mathf.Max(worst, Vector3.Distance(prev, cur));
                prev = cur;
            }

            Assert.That(worst, Is.LessThan(0.002f), $"the head carry jumped {worst * 1000f:0.0} mm in a quarter of a degree — that is a camera pop");
        }
        [Test]
        public void ScalingTheAvatar_ScalesTheCarryWithIt()
        {
            // EyeFromNeck comes straight off the avatar's own T-pose, so a twice-as-big avatar must be carried
            // twice as far. Anything else leaks body size into the camera.
            Assert.That(Swing(45f, eyeFromNeck: EyeFromNeck * 2f).magnitude, Is.EqualTo(Swing(45f).magnitude * 2f).Within(1e-4f),"a twice-as-tall avatar must carry its head twice as far; the carry has to be scale-invariant");
        }
        [Test]
        public void Disabling_It_ByStrength_ProducesNothing()
        {
            Assert.That(Swing(45f, 0f, strength: 0f).magnitude, Is.EqualTo(0f),"strength 0 must be a true no-op, so turning the feature off restores the old behaviour exactly");
        }
        [Test]
        public void GarbageIn_ProducesNoMovement_RatherThanANaNHead()
        {
            foreach (Vector3 offset in new[]
            {
                Swing(float.NaN),
                Swing(45f, float.NaN),
                Swing(45f, 0f, strength: float.NaN),
                Swing(45f, 0f, 1f, backward: float.NaN),
                Swing(45f, eyeFromNeck: new Vector3(0f, float.NaN, 0f)),
                Swing(45f, eyeFromNeck: Vector3.zero),
            })
            {
                Assert.That(float.IsFinite(offset.x) && float.IsFinite(offset.y) && float.IsFinite(offset.z), Is.True, $"a bad input produced a non-finite head offset {offset} — a NaN head in Unity never recovers");
            }

            // The look-UP branch is the one that multiplies by BackwardScale, so NaN there has its own path in.
            Vector3 up = Swing(-45f, 0f, 1f, backward: float.NaN);
            Assert.That(float.IsFinite(up.z), Is.True, $"a NaN backward scale produced a non-finite head offset {up}");
        }
    }
}
