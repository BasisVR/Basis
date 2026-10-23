using Basis.Scripts.Avatar;
using NUnit.Framework;
using UnityEngine;
namespace Basis.Tests.Fitting
{
    public class BasisCalibrationGuideTests
    {
        const float Frame = 1f / 60f;
        [Test]
        public void ALevelHeadLookingAlongTheStageScoresFull()
        {
            BasisCalibrationLockInCore.HeadErrors(Quaternion.identity, Vector3.forward, out float pitch, out float roll, out float yaw);
            Assert.That(pitch, Is.EqualTo(0f).Within(1e-4f));
            Assert.That(roll, Is.EqualTo(0f).Within(1e-4f));
            Assert.That(yaw, Is.EqualTo(0f).Within(1e-4f));
            Assert.That(BasisCalibrationLockInCore.HeadLevelScore(pitch, roll, yaw), Is.EqualTo(1f));
        }
        [Test]
        public void LookingDownTiltingOrTurningAwayCoolsTheHeadTarget()
        {
            BasisCalibrationLockInCore.HeadErrors(Quaternion.Euler(20f, 0f, 0f), Vector3.forward, out float pitch, out float roll, out float yaw);
            Assert.That(pitch, Is.EqualTo(-20f).Within(1e-3f));
            Assert.That(BasisCalibrationLockInCore.HeadLevelScore(pitch, roll, yaw), Is.EqualTo(0f));
            BasisCalibrationLockInCore.HeadErrors(Quaternion.Euler(0f, 0f, 8f), Vector3.forward, out pitch, out roll, out yaw);
            Assert.That(Mathf.Abs(roll), Is.EqualTo(8f).Within(1e-3f));
            Assert.That(BasisCalibrationLockInCore.HeadLevelScore(pitch, roll, yaw), Is.EqualTo(0.75f).Within(1e-3f));
            BasisCalibrationLockInCore.HeadErrors(Quaternion.Euler(0f, 10f, 0f), Vector3.forward, out pitch, out roll, out yaw);
            Assert.That(yaw, Is.EqualTo(10f).Within(1e-3f));
            Assert.That(BasisCalibrationLockInCore.HeadLevelScore(pitch, roll, yaw), Is.EqualTo(1f - 4f / 24f).Within(1e-3f));
        }
        [Test]
        public void TheGazeDotLandsWhereTheHeadPoints()
        {
            Vector3 eye = new Vector3(0f, 1.6f, 0f), gaze = Quaternion.Euler(-5f, 0f, 0f) * Vector3.forward;
            Assert.That(BasisCalibrationLockInCore.TryGazeOnPlane(eye, gaze, new Vector3(0f, 1.6f, 0.6f), Vector3.back, out Vector3 hit), Is.True);
            Assert.That(hit.y, Is.EqualTo(1.6f + 0.6f * Mathf.Tan(5f * Mathf.Deg2Rad)).Within(1e-4f));
            Assert.That(hit.z, Is.EqualTo(0.6f).Within(1e-4f));
            Assert.That(BasisCalibrationLockInCore.TryGazeOnPlane(eye, Vector3.back, new Vector3(0f, 1.6f, 0.6f), Vector3.back, out _), Is.False);
        }
        [Test]
        public void ARingFacesItsViewerAtTheRequestedRadius()
        {
            Vector3 center = new Vector3(0.3f, 1.2f, 0.5f), viewer = new Vector3(0f, 1.6f, -0.4f), normal = (viewer - center).normalized;
            Vector3[] points = new Vector3[32];
            BasisCalibrationLockInCore.RingPoints(center, viewer - center, 0.05f, points);
            foreach (Vector3 point in points)
            {
                Assert.That(Vector3.Distance(point, center), Is.EqualTo(0.05f).Within(1e-5f));
                Assert.That(Vector3.Dot(point - center, normal), Is.EqualTo(0f).Within(1e-5f));
            }
            BasisCalibrationLockInCore.RingPoints(center, Vector3.up, 0.05f, points);
            Assert.That(points[0].y, Is.EqualTo(center.y).Within(1e-5f));
        }
        [Test]
        public void SpreadArmsGiveTheBodyForwardWhereverTheHeadIsLooking()
        {
            Vector3 left = new Vector3(-0.8f, 1.4f, 0.1f), right = new Vector3(0.8f, 1.4f, 0.1f), glance = Quaternion.Euler(0f, 60f, 0f) * Vector3.forward;
            Assert.That(BasisCalibrationLockInCore.TryBodyForwardFromHands(left, right, glance, 0.96f, out Vector3 forward), Is.True);
            Assert.That(Vector3.Angle(forward, Vector3.forward), Is.LessThan(0.01f));
            Assert.That(BasisCalibrationLockInCore.TryBodyForwardFromHands(right, left, glance, 0.96f, out forward), Is.True);
            Assert.That(Vector3.Angle(forward, Vector3.forward), Is.LessThan(0.01f));
            Assert.That(BasisCalibrationLockInCore.TryBodyForwardFromHands(new Vector3(-0.25f, 1f, 0f), new Vector3(0.25f, 1f, 0f), glance, 0.96f, out _), Is.False);
        }
        [Test]
        public void AWristOnTheArmLineScoresFullWhateverTheReach()
        {
            Vector3 shoulder = new Vector3(0.2f, 1.4f, 0f), wrist = new Vector3(0.75f, 1.4f, 0f);
            float error = BasisCalibrationLockInCore.ArmLineErrorDegrees(shoulder, wrist, new Vector3(0.9f, 1.4f, 0f), out Vector3 onLine);
            Assert.That(error, Is.EqualTo(0f).Within(1e-3f));
            Assert.That(Vector3.Distance(onLine, new Vector3(0.9f, 1.4f, 0f)), Is.LessThan(1e-4f));
            error = BasisCalibrationLockInCore.ArmLineErrorDegrees(shoulder, wrist, new Vector3(0.7f, 1.3f, 0f), out onLine);
            Assert.That(error, Is.EqualTo(Mathf.Atan2(0.1f, 0.5f) * Mathf.Rad2Deg).Within(1e-3f));
            Assert.That(onLine.y, Is.EqualTo(1.4f).Within(1e-4f));
            Assert.That(BasisCalibrationLockInCore.AngleScore(error, BasisCalibrationLockInCore.WristToleranceDeg, BasisCalibrationLockInCore.WristFalloffDeg), Is.InRange(0.6f, 0.8f));
        }
        [Test]
        public void TheStageHoldsThroughGlancesAndRecentresAfterARealTurn()
        {
            BasisCalibrationStageFrame stage = default;
            Assert.That(stage.Track(Vector3.zero, Vector3.forward, false, Frame), Is.True);
            Vector3 turned = Quaternion.Euler(0f, 60f, 0f) * Vector3.forward;
            for (int frame = 0; frame < 60; frame++)
            {
                Assert.That(stage.Track(Vector3.zero, turned, false, Frame), Is.False);
            }
            Assert.That(stage.Track(Vector3.zero, Vector3.forward, false, Frame), Is.False);
            bool recentred = false;
            for (int frame = 0; frame < 120 && !recentred; frame++)
            {
                recentred = stage.Track(Vector3.zero, turned, false, Frame);
            }
            Assert.That(recentred, Is.True);
            Assert.That(Vector3.Angle(stage.Forward, turned), Is.LessThan(0.01f));
            Assert.That(stage.Track(new Vector3(0.3f, 0f, 0f), turned, false, Frame), Is.False);
        }
        [Test]
        public void RaisingTheArmsSnapsTheStageToTheBody()
        {
            BasisCalibrationStageFrame stage = default;
            stage.Track(Vector3.zero, Quaternion.Euler(0f, 20f, 0f) * Vector3.forward, false, Frame);
            Assert.That(stage.Track(Vector3.zero, Vector3.forward, true, Frame), Is.True);
            Assert.That(Vector3.Angle(stage.Forward, Vector3.forward), Is.LessThan(0.01f));
            Vector3 slight = Quaternion.Euler(0f, 3f, 0f) * Vector3.forward;
            Assert.That(stage.Track(Vector3.zero, slight, false, Frame), Is.False);
            Assert.That(stage.Track(Vector3.zero, slight, true, Frame), Is.False);
            Assert.That(Vector3.Angle(stage.Forward, Vector3.forward), Is.LessThan(0.01f));
        }
        [Test]
        public void TheMirrorsViewpointIsTheEyeBehindTheGlass()
        {
            Vector3 reflected = BasisCalibrationLockInCore.ReflectAcrossPlane(new Vector3(0f, 1.6f, 0f), new Vector3(0f, 1.6f, 0.9f), Vector3.back);
            Assert.That(Vector3.Distance(reflected, new Vector3(0f, 1.6f, 1.8f)), Is.LessThan(1e-5f));
        }
    }
}
