using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.GlobalIllumination
{
    /// <summary>
    /// The proxy path replaced the general inverse-transpose with the analytical orthogonal-column
    /// form. These pin that for every matrix the capsule builder can produce — an orthonormal basis
    /// with per-axis scales — the three rows the kernel reads come out the same, and that the
    /// degenerate shapes fall back rather than exploding.
    /// </summary>
    public class BasisGlobalIlluminationNormalMatrixTests
    {
        private static void AssertRowsMatch(in Matrix4x4 matrix, float tolerance)
        {
            var general = new BasisGlobalIlluminationRayInstance();
            general.SetNormalMatrix(matrix);
            var orthogonal = new BasisGlobalIlluminationRayInstance();
            orthogonal.SetNormalMatrixOrthogonal(matrix);

            AssertVectorsMatch(general.normal0, orthogonal.normal0, tolerance, "row 0");
            AssertVectorsMatch(general.normal1, orthogonal.normal1, tolerance, "row 1");
            AssertVectorsMatch(general.normal2, orthogonal.normal2, tolerance, "row 2");
        }

        private static void AssertVectorsMatch(Vector4 a, Vector4 b, float tolerance, string label)
        {
            Assert.That(b.x, Is.EqualTo(a.x).Within(tolerance), label + " x");
            Assert.That(b.y, Is.EqualTo(a.y).Within(tolerance), label + " y");
            Assert.That(b.z, Is.EqualTo(a.z).Within(tolerance), label + " z");
        }

        [Test]
        public void MatchesTheGeneralInverseTranspose_ForCapsuleShapedMatrices()
        {
            AssertRowsMatch(BasisAvatarProxyJobs.Build(new Vector3(1, 2, 3), new Vector3(1, 3.5f, 3), 0.12f, 0f), 1e-4f);
            AssertRowsMatch(BasisAvatarProxyJobs.Build(new Vector3(-4, 0.5f, 2), new Vector3(1, 1.5f, -3), 0.3f, 0.1f), 1e-4f);
            AssertRowsMatch(BasisAvatarProxyJobs.Build(Vector3.zero, new Vector3(0.01f, 0.02f, 0.015f), 0.05f, 0f), 1e-2f);
        }

        [Test]
        public void MatchesTheGeneralInverseTranspose_ForPlainTrsMatrices()
        {
            AssertRowsMatch(Matrix4x4.TRS(new Vector3(5, -2, 8), Quaternion.Euler(30, 60, 15), new Vector3(0.5f, 2f, 1.25f)), 1e-4f);
            AssertRowsMatch(Matrix4x4.TRS(Vector3.one * 100f, Quaternion.Euler(-80, 200, 45), Vector3.one * 0.02f), 1e-2f);
        }

        [Test]
        public void ACollapsedLimbFallsBackToTheGeneralPath()
        {
            // Radius zero makes every column zero; the analytical form must defer, not divide by it.
            Matrix4x4 collapsed = Matrix4x4.TRS(new Vector3(1, 2, 3), Quaternion.identity, Vector3.zero);

            var general = new BasisGlobalIlluminationRayInstance();
            general.SetNormalMatrix(collapsed);
            var orthogonal = new BasisGlobalIlluminationRayInstance();
            orthogonal.SetNormalMatrixOrthogonal(collapsed);

            AssertVectorsMatch(general.normal0, orthogonal.normal0, 1e-6f, "row 0");
            AssertVectorsMatch(general.normal1, orthogonal.normal1, 1e-6f, "row 1");
            AssertVectorsMatch(general.normal2, orthogonal.normal2, 1e-6f, "row 2");
        }
    }
}
