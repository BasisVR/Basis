using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.IK
{
    /// <summary>
    /// Regression tests for the calibration lock-in aid math (<see cref="BasisCalibrationLockInCore"/>):
    /// the proximity weight/diameter that shrinks a sphere as a tracker closes on its prime spot, and
    /// the foot yaw/tilt alignment that colors the foot-forward guide. Pins the properties the offline
    /// BasisCalibrationLockInSweep gates: clamped monotonic proximity, a worse-axis-wins alignment score,
    /// and the foot guide only greening when the foot is both forward AND flat.
    /// </summary>
    public class BasisCalibrationLockInTests
    {
        const float Capture = 0.05f, Falloff = 0.30f;
        const float MaxYaw = BasisCalibrationLockInCore.DefaultMaxYawDeg;
        const float MaxTilt = BasisCalibrationLockInCore.DefaultMaxTiltDeg;

        // ---------- Proximity weight ----------

        [Test]
        public void ProximityWeight_IsOneAtCapture_AndZeroAtFalloff()
        {
            Assert.That(BasisCalibrationLockInCore.ProximityWeight(0f, Capture, Falloff), Is.EqualTo(1f).Within(1e-4f),
                "a tracker on the spot must be fully locked (weight 1).");
            Assert.That(BasisCalibrationLockInCore.ProximityWeight(Capture, Capture, Falloff), Is.EqualTo(1f).Within(1e-4f),
                "at the capture radius the weight must already be 1.");
            Assert.That(BasisCalibrationLockInCore.ProximityWeight(Falloff, Capture, Falloff), Is.EqualTo(0f).Within(1e-4f),
                "at the falloff radius the weight must be 0.");
            Assert.That(BasisCalibrationLockInCore.ProximityWeight(Falloff + 1f, Capture, Falloff), Is.EqualTo(0f).Within(1e-4f),
                "past falloff the weight must clamp at 0, never go negative.");
        }

        [Test]
        public void ProximityWeight_DecreasesMonotonically_WithDistance()
        {
            float prev = 2f;
            for (float d = 0f; d <= Falloff + 0.05f; d += 0.01f)
            {
                float w = BasisCalibrationLockInCore.ProximityWeight(d, Capture, Falloff);
                Assert.That(w, Is.InRange(0f, 1f), $"weight at {d:0.000} m left the 0..1 range ({w:0.000}).");
                Assert.That(w, Is.LessThanOrEqualTo(prev + 1e-5f),
                    $"weight must never rise as distance grows (d={d:0.000}, w={w:0.000}, prev={prev:0.000}).");
                prev = w;
            }
        }

        [Test]
        public void ProximityWeight_DegenerateRadii_StepAtCapture()
        {
            // falloff <= capture: no ramp, just a hard locked/not-locked step.
            Assert.That(BasisCalibrationLockInCore.ProximityWeight(Capture, Capture, Capture), Is.EqualTo(1f).Within(1e-4f));
            Assert.That(BasisCalibrationLockInCore.ProximityWeight(Capture + 0.01f, Capture, Capture), Is.EqualTo(0f).Within(1e-4f));
        }

        // ---------- Proximity diameter ----------

        [Test]
        public void ProximityRadius_ShrinksFromMaxToMin_AsItLocks()
        {
            const float min = 0.02f, max = 0.09f;
            Assert.That(BasisCalibrationLockInCore.ProximityRadius(0f, min, max), Is.EqualTo(max).Within(1e-4f),
                "far away (weight 0) the ball must be at its max diameter.");
            Assert.That(BasisCalibrationLockInCore.ProximityRadius(1f, min, max), Is.EqualTo(min).Within(1e-4f),
                "locked (weight 1) the ball must shrink to its min diameter.");
            Assert.That(BasisCalibrationLockInCore.ProximityRadius(0.5f, min, max), Is.EqualTo((min + max) * 0.5f).Within(1e-4f),
                "halfway must be the midpoint diameter.");
        }

        [Test]
        public void IsLocked_FlipsAtCapture()
        {
            Assert.IsTrue(BasisCalibrationLockInCore.IsLocked(Capture, Capture));
            Assert.IsTrue(BasisCalibrationLockInCore.IsLocked(Capture - 0.001f, Capture));
            Assert.IsFalse(BasisCalibrationLockInCore.IsLocked(Capture + 0.001f, Capture));
        }

        // ---------- Foot yaw ----------

        [Test]
        public void FootYaw_IsZero_WhenFootPointsBodyForward()
        {
            float yaw = BasisCalibrationLockInCore.FootYawDegrees(Vector3.forward, Vector3.forward, Vector3.up);
            Assert.That(yaw, Is.EqualTo(0f).Within(0.01f), "a foot pointing exactly body-forward has no yaw deviation.");
        }

        [Test]
        public void FootYaw_RecoversToeOutAngle_Signed()
        {
            Vector3 footFwd = Quaternion.AngleAxis(30f, Vector3.up) * Vector3.forward;
            float yaw = BasisCalibrationLockInCore.FootYawDegrees(Vector3.forward, footFwd, Vector3.up);
            Assert.That(yaw, Is.EqualTo(30f).Within(0.1f), "a 30 deg toe-out must read as 30 deg of yaw.");
        }

        [Test]
        public void FootYaw_IgnoresTilt()
        {
            // Pitch the foot forward 40 deg (about body-right). Flattened, it still faces forward.
            Vector3 footFwd = Quaternion.AngleAxis(40f, Vector3.right) * Vector3.forward;
            float yaw = BasisCalibrationLockInCore.FootYawDegrees(Vector3.forward, footFwd, Vector3.up);
            Assert.That(Mathf.Abs(yaw), Is.LessThan(0.5f), "a purely tilted (not turned) foot must read ~0 yaw.");
        }

        // ---------- Foot tilt ----------

        [Test]
        public void FootTilt_IsZero_WhenFlat_AndRecoversTheTilt()
        {
            Assert.That(BasisCalibrationLockInCore.FootTiltDegrees(Vector3.up, Vector3.up), Is.EqualTo(0f).Within(0.01f),
                "a flat foot (up == world up) has no tilt.");
            Vector3 tiltedUp = Quaternion.AngleAxis(20f, Vector3.forward) * Vector3.up;
            Assert.That(BasisCalibrationLockInCore.FootTiltDegrees(tiltedUp, Vector3.up), Is.EqualTo(20f).Within(0.1f),
                "a 20 deg roll must read as 20 deg of tilt.");
        }

        // ---------- Alignment gate + score ----------

        [Test]
        public void IsFootAligned_RespectsBothLimits()
        {
            Assert.IsTrue(BasisCalibrationLockInCore.IsFootAligned(MaxYaw - 1f, MaxTilt - 1f, MaxYaw, MaxTilt));
            Assert.IsFalse(BasisCalibrationLockInCore.IsFootAligned(MaxYaw + 1f, 0f, MaxYaw, MaxTilt), "over the yaw limit must fail.");
            Assert.IsFalse(BasisCalibrationLockInCore.IsFootAligned(0f, MaxTilt + 1f, MaxYaw, MaxTilt), "over the tilt limit must fail.");
        }

        [Test]
        public void AlignmentScore_IsOne_WhenForwardAndFlat()
        {
            Assert.That(BasisCalibrationLockInCore.FootAlignmentScore(0f, 0f, MaxYaw, MaxTilt), Is.EqualTo(1f).Within(1e-4f),
                "perfectly forward and flat must score a full 1.");
        }

        [Test]
        public void AlignmentScore_WorseAxisWins()
        {
            // Yaw perfect, tilt at the limit -> the bad tilt axis must drag the score to 0.
            Assert.That(BasisCalibrationLockInCore.FootAlignmentScore(0f, MaxTilt, MaxYaw, MaxTilt), Is.EqualTo(0f).Within(1e-4f),
                "a perfectly-aimed but maximally-tilted foot must not green (worse axis wins).");
            Assert.That(BasisCalibrationLockInCore.FootAlignmentScore(MaxYaw, 0f, MaxYaw, MaxTilt), Is.EqualTo(0f).Within(1e-4f),
                "a flat but maximally-turned foot must not green either.");
        }

        [Test]
        public void AlignmentScore_DecreasesMonotonically_WithYaw()
        {
            float prev = 2f;
            for (float yaw = 0f; yaw <= MaxYaw; yaw += 1f)
            {
                float s = BasisCalibrationLockInCore.FootAlignmentScore(yaw, 0f, MaxYaw, MaxTilt);
                Assert.That(s, Is.InRange(0f, 1f), $"score at yaw {yaw} left 0..1 ({s:0.000}).");
                Assert.That(s, Is.LessThanOrEqualTo(prev + 1e-5f), $"score must not rise as yaw grows (yaw={yaw}, s={s:0.000}).");
                prev = s;
            }
        }
    }
}
