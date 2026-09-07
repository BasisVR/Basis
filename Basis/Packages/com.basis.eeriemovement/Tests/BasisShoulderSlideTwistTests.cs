using NUnit.Framework;
using UnityEngine;
using Basis.IK;
namespace Basis.Tests.IK
{
    public class BasisShoulderSlideTwistTests
    {
        static float EulerY(Quaternion q)
        {
            float y = q.eulerAngles.y;
            return y > 180f ? y - 360f : y;
        }
        [Test]
        public void PureYaw_MatchesEulerAngles_SoOrdinaryTwistIsUnchanged()
        {
            for (float yaw = -80f; yaw <= 80f; yaw += 5f)
            {
                Quaternion chestLocal = Quaternion.Euler(0f, yaw, 0f);
                float swing = BasisTwistSolveCore.SignedTwistAngleDeg(chestLocal, Vector3.up);
                Assert.That(Mathf.DeltaAngle(swing, yaw), Is.EqualTo(0f).Within(1e-3f), $"swing-twist disagreed with the pure-yaw angle at {yaw} deg (got {swing}).");
                Assert.That(Mathf.DeltaAngle(swing, EulerY(chestLocal)), Is.EqualTo(0f).Within(1e-3f), $"swing-twist is not a no-op vs eulerAngles.y at {yaw} deg.");
            }
        }
        [Test]
        public void StaysContinuous_AsTheChestPitchesThroughVertical()
        {
            // The bug's exact shape: a chest with a little roll, pitched from 60 to 120 deg relative to the
            // hips in fine steps. eulerAngles.y jumps ~180 deg crossing 90; the swing-twist must not.
            const float yaw = 20f, roll = 15f;
            float prev = 0f; bool have = false; float maxStep = 0f;
            for (float pitch = 60f; pitch <= 120f; pitch += 0.5f)
            {
                Quaternion chestLocal = Quaternion.Euler(0f, yaw, 0f) * Quaternion.Euler(pitch, 0f, roll);
                float swing = BasisTwistSolveCore.SignedTwistAngleDeg(chestLocal, Vector3.up);
                Assert.That(float.IsNaN(swing) || float.IsInfinity(swing), Is.False, $"non-finite twist at pitch {pitch}.");
                if (have)
                {
                    maxStep = Mathf.Max(maxStep, Mathf.Abs(Mathf.DeltaAngle(prev, swing)));
                }
                prev = swing; have = true;
            }
            // Each step is 0.5 deg of pitch; the twist moves a fraction of a degree. 5 deg is far above the
            // real motion and far below the ~165 deg euler jump this replaced.
            Assert.That(maxStep, Is.LessThan(5f), $"chest-twist measure jumped {maxStep:0.0} deg through the pitch pole -- the gimbal phantom is back.");
        }
    }
}
