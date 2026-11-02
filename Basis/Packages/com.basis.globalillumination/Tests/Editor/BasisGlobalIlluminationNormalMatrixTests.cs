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
        public void PublishedNormalRowsMatchTheOrthogonalForm_OnTheJobAndManagedPaths()
        {
            var a = new GameObject("proxy-a").transform;
            var b = new GameObject("proxy-b").transform;
            var c = new GameObject("proxy-c").transform;
            a.position = new Vector3(1f, 2f, 3f);
            b.position = new Vector3(1.4f, 3.1f, 2.2f);
            c.position = new Vector3(-0.5f, 0.2f, 4f);
            var limbs = new System.Collections.Generic.List<BasisAvatarProxy.ResolvedLimb>
            {
                new BasisAvatarProxy.ResolvedLimb(a, b, 0.12f, 0f),
                new BasisAvatarProxy.ResolvedLimb(b, c, 0.3f, 0.1f),
                new BasisAvatarProxy.ResolvedLimb(c, c, 0.05f, 0f),
                new BasisAvatarProxy.ResolvedLimb(a, c, 0f, 0f),
            };
            var schedule = typeof(BasisAvatarProxyJobs).GetMethod("ScheduleBeforeRender", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.IsNotNull(schedule, "ScheduleBeforeRender moved");
            try
            {
                BasisAvatarProxyJobs.Rebuild(limbs);
                for (int pass = 0; pass < 2; pass++)
                {
                    if (pass == 0) schedule.Invoke(null, null);
                    BasisAvatarProxyJobs.Run();
                    for (int i = 0; i < limbs.Count; i++)
                    {
                        Matrix4x4 m = BasisAvatarProxyJobs.MatrixAt(i);
                        var expected = new BasisGlobalIlluminationRayInstance();
                        expected.SetNormalMatrixOrthogonal(m);
                        bool ok = BasisAvatarProxyJobs.TryNormalRowsAt(i, out Vector4 n0, out Vector4 n1, out Vector4 n2);
                        string path = pass == 0 ? "job" : "managed";
                        if (i == 3) { Assert.IsFalse(ok, $"{path}: a zero-radius limb defers to the general inverse"); continue; }
                        Assert.IsTrue(ok, $"{path} limb {i}");
                        AssertVectorsMatch(expected.normal0, n0, 1e-6f, $"{path} limb {i} row 0");
                        AssertVectorsMatch(expected.normal1, n1, 1e-6f, $"{path} limb {i} row 1");
                        AssertVectorsMatch(expected.normal2, n2, 1e-6f, $"{path} limb {i} row 2");
                    }
                }
            }
            finally
            {
                BasisAvatarProxyJobs.Release();
                Object.DestroyImmediate(a.gameObject);
                Object.DestroyImmediate(b.gameObject);
                Object.DestroyImmediate(c.gameObject);
            }
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
