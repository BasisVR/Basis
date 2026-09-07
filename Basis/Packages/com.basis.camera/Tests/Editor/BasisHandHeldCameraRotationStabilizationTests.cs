using Basis.Cinematics;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Camera
{
    /// <summary>
    /// The rotational half of the stabilizer, where each axis is damped on its own time. What it
    /// buys is the shot a slerp cannot express: one that swings with the operator through a turn
    /// while the tilt and the horizon stay where they were.
    /// </summary>
    public class BasisHandHeldCameraRotationStabilizationTests
    {
        private const float Frame = 1f / 90f;
        private const float Even = 0.3f;

        private static Quaternion Step(Quaternion current, Quaternion target,
            float pitch = Even, float yaw = Even, float roll = Even, float deltaTime = Frame)
            => BasisHandHeldCameraInteractable.SolveStabilizedRotation(
                current, target, pitch, yaw, roll, deltaTime);

        private static float Covered(Quaternion result, float targetAngle, int axis)
            => BasisCameraDamping.NormalizeEuler(result.eulerAngles)[axis] / targetAngle;

        [Test]
        public void ThreeEqualTimesAreTheSlerpTheyReplace()
        {
            Quaternion target = Quaternion.Euler(12f, 40f, 8f);
            Quaternion perAxis = Step(Quaternion.identity, target);
            Quaternion slerp = BasisCameraDamping.ApproachRotation(Quaternion.identity, target, Even, Frame);

            Assert.That(Quaternion.Angle(perAxis, slerp), Is.LessThan(1e-3f),
                "the default set of three has to be the motion the single turn number used to give");
        }

        [Test]
        public void ATurnIsFollowedWhileTheHorizonIsHeld()
        {
            const float yawAngle = 30f;
            const float rollAngle = 20f;

            Quaternion target = Quaternion.Euler(0f, yawAngle, rollAngle);
            Quaternion result = Step(Quaternion.identity, target, yaw: 0.1f, roll: 1.5f);

            Assert.That(Covered(result, yawAngle, 1), Is.GreaterThan(Covered(result, rollAngle, 2)),
                "a loose yaw and a tight roll has to swing with the turn and leave the roll behind");
        }

        [Test]
        public void ATiltIsHeldSeparatelyFromTheTurnItArrivesWith()
        {
            const float pitchAngle = 25f;
            const float yawAngle = 25f;

            Quaternion target = Quaternion.Euler(pitchAngle, yawAngle, 0f);
            Quaternion result = Step(Quaternion.identity, target, pitch: 1.5f, yaw: 0.1f);

            Assert.That(Covered(result, pitchAngle, 0), Is.LessThan(Covered(result, yawAngle, 1)));
        }

        [Test]
        public void EveryAxisStillArrives()
        {
            // A stabilizer that lags forever is a camera aimed at the wrong thing. Each axis has to
            // land on the pose the prop is actually holding once the shake stops.
            Quaternion target = Quaternion.Euler(18f, 55f, 12f);
            Quaternion current = Quaternion.identity;

            for (int frame = 0; frame < 900; frame++)
            {
                current = Step(current, target, pitch: 1.5f, yaw: 0.1f, roll: 0.8f);
            }

            Assert.That(Quaternion.Angle(current, target), Is.LessThan(0.1f));
        }

        [Test]
        public void AStoppedClockTurnsNothing()
        {
            Quaternion target = Quaternion.Euler(0f, 90f, 0f);

            Assert.That(Quaternion.Angle(Step(Quaternion.identity, target, deltaTime: 0f), Quaternion.identity),
                Is.LessThan(1e-3f));
            Assert.That(
                Quaternion.Angle(Step(Quaternion.identity, target, pitch: 0.2f, yaw: 0.9f, roll: 1.4f, deltaTime: 0f),
                    Quaternion.identity),
                Is.LessThan(1e-3f), "and not on the per-axis path either");
        }

        [Test]
        public void ZoomStretchesEveryAxisTogether()
        {
            // The zoom scale multiplies all three damp times, so a longer lens has to take a smaller
            // bite out of each axis rather than changing which axis leads.
            const float yawAngle = 40f;
            const float rollAngle = 40f;

            Quaternion target = Quaternion.Euler(0f, yawAngle, rollAngle);
            float zoom = BasisHandHeldCameraInteractable.SolveZoomStabilizationScale(
                BasisHandHeldCameraUI.MinFov, 1f, 0.35f, 4f);

            Quaternion wide = Step(Quaternion.identity, target, yaw: 0.2f, roll: 1f);
            Quaternion tele = Step(Quaternion.identity, target, yaw: 0.2f * zoom, roll: 1f * zoom);

            Assert.That(zoom, Is.GreaterThan(1f), "Precondition: the long end has to stabilize harder.");
            Assert.That(Covered(tele, yawAngle, 1), Is.LessThan(Covered(wide, yawAngle, 1)));
            Assert.That(Covered(tele, rollAngle, 2), Is.LessThan(Covered(wide, rollAngle, 2)));
        }
    }
}
