using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Camera
{
    /// <summary>
    /// Roll on the selfie-stick grip. While the puck is held it is the master and the camera is
    /// seeded from its pose every frame, so whether twisting your wrist tips the shot comes down to
    /// what that pose is allowed to carry — which is all Camera Roll changes.
    /// </summary>
    public class BasisCameraGripRollTests
    {
        private static Quaternion Pose(float yaw, float pitch, float roll)
            => Quaternion.Euler(pitch, yaw, roll);

        /// <summary>How far the horizon is off level, in degrees: the tilt of the camera's own right axis.</summary>
        private static float TiltDegrees(Quaternion rotation)
            => Mathf.Abs(Mathf.Asin(Mathf.Clamp((rotation * Vector3.right).y, -1f, 1f)) * Mathf.Rad2Deg);

        private static Quaternion Grip(Quaternion rotation, bool rollEnabled)
            => BasisHandHeldCameraInteractable.ApplyGripRoll(rotation, rollEnabled);

        [TestCase(35f, -20f, 25f)]
        [TestCase(-70f, 15f, -40f)]
        [TestCase(120f, 60f, 90f)]
        [TestCase(10f, 0f, 180f)]
        [TestCase(-45f, 80f, -120f)]
        public void WithRollOff_TheGripAimsTheCameraWithoutTiltingIt(float yaw, float pitch, float roll)
        {
            Quaternion grip = Pose(yaw, pitch, roll);

            Quaternion shot = Grip(grip, rollEnabled: false);

            Assert.That(Vector3.Angle(shot * Vector3.forward, grip * Vector3.forward), Is.LessThan(0.05f),
                "Dropping the roll must not move where the grip is pointing the camera.");
            Assert.That(TiltDegrees(shot), Is.LessThan(0.05f), "The horizon comes back level.");
            Assert.That((shot * Vector3.up).y, Is.GreaterThan(0f),
                "…and the picture is the right way up, even from a grip turned upside down.");
        }

        [TestCase(35f, -20f, 25f)]
        [TestCase(120f, 60f, 90f)]
        public void WithRollOn_TheGripsRollGoesStraightThrough(float yaw, float pitch, float roll)
        {
            Quaternion grip = Pose(yaw, pitch, roll);

            Assert.That(Quaternion.Angle(Grip(grip, rollEnabled: true), grip), Is.LessThan(1e-3f),
                "On, the grip is the pose — nothing is read off it and nothing is put back.");
        }

        [Test]
        public void ALevelGripIsUntouchedEitherWay()
        {
            Quaternion grip = Pose(50f, -30f, 0f);

            Assert.That(Quaternion.Angle(Grip(grip, false), grip), Is.LessThan(0.05f));
            Assert.That(Quaternion.Angle(Grip(grip, true), grip), Is.LessThan(1e-3f));
        }

        [Test]
        public void TheHorizonIsLevelledExactlyAtEverySteepAimThatHasOne()
        {
            // Read off a yaw/pitch decomposition instead, this drifted with pitch — exact out to
            // about 80° and as much as eight degrees off approaching vertical, because the
            // decomposition's near-vertical fallback is built for an anchor that rolls freely.
            float worstTilt = 0f;
            float worstAimMoved = 0f;

            for (float pitch = -89f; pitch <= 89f; pitch += 7f)
            {
                for (float yaw = -180f; yaw < 180f; yaw += 23f)
                {
                    for (float roll = -180f; roll < 180f; roll += 37f)
                    {
                        Quaternion grip = Pose(yaw, pitch, roll);
                        Quaternion shot = Grip(grip, rollEnabled: false);

                        worstTilt = Mathf.Max(worstTilt, TiltDegrees(shot));
                        worstAimMoved = Mathf.Max(worstAimMoved,
                            Vector3.Angle(shot * Vector3.forward, grip * Vector3.forward));
                    }
                }
            }

            Assert.That(worstTilt, Is.LessThan(0.05f), "the horizon has to come back level at every aim, not just gentle ones");
            Assert.That(worstAimMoved, Is.LessThan(0.05f), "and levelling must never move the aim");
        }

        [TestCase(90f)]
        [TestCase(-90f)]
        public void AnAimStraightUpOrDownIsHandedBackUntouched(float pitch)
        {
            // Forward and world up on one line: there is no horizon to be level with, and asking
            // for one is what makes LookRotation cut to identity. Held as it came instead.
            Quaternion grip = Pose(20f, pitch, 30f);

            Assert.That(Quaternion.Angle(Grip(grip, rollEnabled: false), grip), Is.LessThan(1e-3f));
        }

        [Test]
        public void TheParkingOffsetIsTheSameEitherWay()
        {
            // TryGetFollowPipPose takes the puck's parking distance back off along the lens axis
            // using the grip's own rotation, and the roll is settled on the rotation after that.
            // Levelling leaves the aim alone, so both poses park along the same axis — which is
            // what makes applying it afterwards safe rather than a shift of the camera.
            Quaternion grip = Pose(-15f, 35f, 60f);

            Quaternion levelled = Grip(grip, rollEnabled: false);

            Assert.That(Vector3.Distance(levelled * Vector3.forward, grip * Vector3.forward), Is.LessThan(1e-3f));
        }
    }
}
