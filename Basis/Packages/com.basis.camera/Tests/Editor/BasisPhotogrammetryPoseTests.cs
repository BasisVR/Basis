using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Camera
{
    /// <summary>
    /// Pure geometry: the Unity-to-nerfstudio pose conversion, its intrinsics, and the
    /// distance/angle capture trigger — all testable with no camera or scene.
    /// </summary>
    public class BasisPhotogrammetryPoseTests
    {
        [Test]
        public void IdentityRotationAtTheOriginIsTheIdentityMatrix()
        {
            Matrix4x4 matrix = BasisPhotogrammetryPose.BuildNerfTransformMatrix(Vector3.zero, Quaternion.identity);
            AssertApproximatelyEqual(Matrix4x4.identity, matrix);
        }

        [Test]
        public void PositionOnlyMovesTheOriginColumnWithZNegated()
        {
            Vector3 position = new Vector3(2f, 3f, 5f);
            Matrix4x4 matrix = BasisPhotogrammetryPose.BuildNerfTransformMatrix(position, Quaternion.identity);

            Vector4 originColumn = matrix.GetColumn(3);
            Assert.That(originColumn.x, Is.EqualTo(2f).Within(1e-5f));
            Assert.That(originColumn.y, Is.EqualTo(3f).Within(1e-5f));
            Assert.That(originColumn.z, Is.EqualTo(-5f).Within(1e-5f));
            Assert.That(originColumn.w, Is.EqualTo(1f).Within(1e-5f));
        }

        [Test]
        public void RotatedAxesStayOrthonormalAndRightHanded()
        {
            Quaternion rotation = Quaternion.Euler(23f, -61f, 8f);
            Matrix4x4 matrix = BasisPhotogrammetryPose.BuildNerfTransformMatrix(new Vector3(1f, -2f, 4f), rotation);

            Vector3 right = matrix.GetColumn(0);
            Vector3 up = matrix.GetColumn(1);
            Vector3 thirdColumn = matrix.GetColumn(2);

            Assert.That(right.magnitude, Is.EqualTo(1f).Within(1e-4f));
            Assert.That(up.magnitude, Is.EqualTo(1f).Within(1e-4f));
            Assert.That(thirdColumn.magnitude, Is.EqualTo(1f).Within(1e-4f));
            Assert.That(Vector3.Dot(right, up), Is.EqualTo(0f).Within(1e-4f));
            Assert.That(Vector3.Dot(right, thirdColumn), Is.EqualTo(0f).Within(1e-4f));
            Assert.That(Vector3.Dot(up, thirdColumn), Is.EqualTo(0f).Within(1e-4f));

            // A proper rotation, not a mirrored one: right × up must land back on the third
            // column, or every reconstructed pose would be flipped inside-out.
            Assert.That(Vector3.Dot(Vector3.Cross(right, up), thirdColumn), Is.GreaterThan(0.999f));
        }

        [Test]
        public void IntrinsicsMatchAPinholeCameraAtNinetyDegreesVerticalFov()
        {
            // A 90 degree vertical FOV means the half-height equals the focal length exactly.
            BasisPhotogrammetryPose.ComputeIntrinsics(90f, 200, 200,
                out float focalLengthX, out float focalLengthY, out float principalPointX, out float principalPointY,
                out float horizontalFieldOfViewRadians);

            Assert.That(focalLengthY, Is.EqualTo(100f).Within(0.01f));
            Assert.That(focalLengthX, Is.EqualTo(focalLengthY));
            Assert.That(principalPointX, Is.EqualTo(100f));
            Assert.That(principalPointY, Is.EqualTo(100f));
            Assert.That(horizontalFieldOfViewRadians, Is.EqualTo(90f * Mathf.Deg2Rad).Within(0.01f));
        }

        [Test]
        public void ShouldCaptureTripsOnDistanceButNotBelowIt()
        {
            Assert.That(BasisPhotogrammetryPose.ShouldCapture(
                Vector3.zero, Quaternion.identity, new Vector3(0.29f, 0f, 0f), Quaternion.identity, 0.3f, 15f), Is.False);
            Assert.That(BasisPhotogrammetryPose.ShouldCapture(
                Vector3.zero, Quaternion.identity, new Vector3(0.31f, 0f, 0f), Quaternion.identity, 0.3f, 15f), Is.True);
        }

        [Test]
        public void ShouldCaptureTripsOnAngleButNotBelowIt()
        {
            Quaternion under = Quaternion.Euler(0f, 14f, 0f);
            Quaternion over = Quaternion.Euler(0f, 16f, 0f);
            Assert.That(BasisPhotogrammetryPose.ShouldCapture(Vector3.zero, Quaternion.identity, Vector3.zero, under, 0.3f, 15f), Is.False);
            Assert.That(BasisPhotogrammetryPose.ShouldCapture(Vector3.zero, Quaternion.identity, Vector3.zero, over, 0.3f, 15f), Is.True);
        }

        private static void AssertApproximatelyEqual(Matrix4x4 expected, Matrix4x4 actual)
        {
            for (int row = 0; row < 4; row++)
            {
                for (int column = 0; column < 4; column++)
                {
                    Assert.That(actual[row, column], Is.EqualTo(expected[row, column]).Within(1e-5f), $"Mismatch at [{row},{column}].");
                }
            }
        }
    }
}
