using Basis.Scripts.Rendering;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Rendering
{
    /// <summary>
    /// Pins the angular gaze-foveation math against the field-verified legacy projection.
    ///
    /// The VRS compute shader used to compare tile UVs against a UV-space circle around a
    /// projected gaze point; it now reconstructs each tile's view ray from the projection
    /// matrix and rates by eccentricity from the gaze direction. The one thing the old path
    /// had going for it was that its projection convention (GL.GetGPUProjectionMatrix with
    /// renderIntoTexture=false, no Y flip) was confirmed correct in-headset. These tests
    /// guarantee the new inverse mapping is the exact algebraic inverse of that same forward
    /// projection — including asymmetric VR frusta and a Y-negated (GPU-flipped) matrix —
    /// so the convention proof carries over.
    ///
    /// Matrices are built by hand rather than via Matrix4x4.Perspective/Frustum/TRS so the
    /// suite exercises only managed math and stays runnable outside the engine.
    /// </summary>
    public class BasisVrsMathTests
    {
        const float Eps = 1e-4f;

        // GL-convention perspective frustum: the exact matrix family Matrix4x4.Frustum and
        // Unity's VR eye projections produce (clip.w = -z_view).
        static Matrix4x4 Frustum(float l, float r, float b, float t, float n, float f)
        {
            Matrix4x4 m = Matrix4x4.zero;
            m.m00 = 2f * n / (r - l);
            m.m02 = (r + l) / (r - l);
            m.m11 = 2f * n / (t - b);
            m.m12 = (t + b) / (t - b);
            m.m22 = -(f + n) / (f - n);
            m.m23 = -2f * f * n / (f - n);
            m.m32 = -1f;
            return m;
        }

        static Matrix4x4 Symmetric(float fovYDeg, float aspect, float n, float f)
        {
            float half = n * Mathf.Tan(0.5f * fovYDeg * Mathf.Deg2Rad);
            return Frustum(-half * aspect, half * aspect, -half, half, n, f);
        }

        static Matrix4x4 FlipY(Matrix4x4 proj)
        {
            proj.m10 = -proj.m10; proj.m11 = -proj.m11; proj.m12 = -proj.m12; proj.m13 = -proj.m13;
            return proj;
        }

        // Unity-style world→view matrix for a camera at p yawed by yawDeg: view space is
        // +x right, +y up, camera looks down -z. Managed trig only.
        static Matrix4x4 View(Vector3 p, float yawDeg)
        {
            float a = yawDeg * Mathf.Deg2Rad;
            Vector3 right = new Vector3(Mathf.Cos(a), 0f, -Mathf.Sin(a));
            Vector3 up = new Vector3(0f, 1f, 0f);
            Vector3 fwd = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
            Matrix4x4 m = Matrix4x4.zero;
            m.m00 = right.x; m.m01 = right.y; m.m02 = right.z; m.m03 = -Vector3.Dot(right, p);
            m.m10 = up.x; m.m11 = up.y; m.m12 = up.z; m.m13 = -Vector3.Dot(up, p);
            m.m20 = -fwd.x; m.m21 = -fwd.y; m.m22 = -fwd.z; m.m23 = Vector3.Dot(fwd, p);
            m.m33 = 1f;
            return m;
        }

        // The exact forward projection the old shipped path used (proj * view * point, 0.5x/w + 0.5).
        static Vector2 LegacyProjectToUV(Vector3 worldPoint, Matrix4x4 view, Matrix4x4 proj)
        {
            Vector4 clip = proj * (view * new Vector4(worldPoint.x, worldPoint.y, worldPoint.z, 1f));
            Assert.Greater(clip.w, 1e-5f, "test point must be in front of the eye");
            return new Vector2(0.5f * (clip.x / clip.w) + 0.5f, 0.5f * (clip.y / clip.w) + 0.5f);
        }

        static void AssertRoundTrip(Matrix4x4 proj)
        {
            Vector4 unproj = BasisVrsMath.UnprojectParams(proj);
            for (float nx = -0.9f; nx <= 0.91f; nx += 0.3f)
            {
                for (float ny = -0.9f; ny <= 0.91f; ny += 0.3f)
                {
                    Vector2 ndc = new Vector2(nx, ny);
                    Vector3 dir = BasisVrsMath.ViewDirForNdc(unproj, ndc);
                    Assert.Less(dir.z, 0f, "view rays look down -Z");
                    Vector2 uv = BasisVrsMath.ViewDirToUV(proj, dir, new Vector2(-1f, -1f));
                    Assert.AreEqual(0.5f * nx + 0.5f, uv.x, Eps, $"u at ndc {ndc}");
                    Assert.AreEqual(0.5f * ny + 0.5f, uv.y, Eps, $"v at ndc {ndc}");
                }
            }
        }

        [Test]
        public void SymmetricProjectionRoundTrips()
        {
            AssertRoundTrip(Symmetric(90f, 1.2f, 0.1f, 1000f));
        }

        [Test]
        public void AsymmetricVrProjectionRoundTrips()
        {
            // Off-center frustum like a VR eye: unequal left/right and top/bottom tangents.
            AssertRoundTrip(Frustum(-0.14f, 0.1f, -0.11f, 0.12f, 0.1f, 1000f));
        }

        [Test]
        public void YFlippedProjectionRoundTrips()
        {
            AssertRoundTrip(FlipY(Frustum(-0.14f, 0.1f, -0.11f, 0.12f, 0.1f, 1000f)));
        }

        [Test]
        public void MatchesLegacyForwardProjectionThroughAView()
        {
            Matrix4x4 proj = Frustum(-0.14f, 0.1f, -0.11f, 0.12f, 0.1f, 1000f);
            Matrix4x4 view = View(new Vector3(1.3f, 1.6f, -0.4f), 35f);
            Vector3 worldPoint = new Vector3(2.5f, 2.1f, 3.7f);

            Vector2 legacy = LegacyProjectToUV(worldPoint, view, proj);
            Vector3 viewDir = view.MultiplyPoint3x4(worldPoint).normalized;
            Vector2 uv = BasisVrsMath.ViewDirToUV(proj, viewDir, new Vector2(-1f, -1f));

            Assert.AreEqual(legacy.x, uv.x, Eps);
            Assert.AreEqual(legacy.y, uv.y, Eps);
        }

        [Test]
        public void ForwardMapsToOpticalCenter()
        {
            Matrix4x4 symmetric = Symmetric(75f, 1f, 0.1f, 100f);
            Vector2 uv = BasisVrsMath.ViewDirToUV(symmetric, new Vector3(0f, 0f, -1f), new Vector2(-1f, -1f));
            Assert.AreEqual(0.5f, uv.x, Eps);
            Assert.AreEqual(0.5f, uv.y, Eps);

            // Behind-the-eye directions must fall back, never divide by ~0.
            Vector2 fallback = BasisVrsMath.ViewDirToUV(symmetric, new Vector3(0f, 0f, 1f), new Vector2(0.25f, 0.75f));
            Assert.AreEqual(0.25f, fallback.x, Eps);
            Assert.AreEqual(0.75f, fallback.y, Eps);
        }

        [Test]
        public void CosForUvRadiusMatchesAtanForm()
        {
            float m11 = Symmetric(90f, 1f, 0.1f, 100f).m11;
            Assert.AreEqual(1f, BasisVrsMath.CosForUvRadius(0f, m11), Eps);
            float previous = 2f;
            for (float r = 0.05f; r <= 0.5f; r += 0.05f)
            {
                float expected = Mathf.Cos(Mathf.Atan(2f * r / m11));
                float actual = BasisVrsMath.CosForUvRadius(r, m11);
                Assert.AreEqual(expected, actual, Eps, $"radius {r}");
                Assert.Less(actual, previous, "cos threshold must shrink as the radius grows");
                previous = actual;
            }
        }

        [Test]
        public void EyeGazeViewDirBlendsAndDegrades()
        {
            Matrix4x4 view = View(new Vector3(0.2f, 1.5f, 0f), 20f);
            Vector3 focal = new Vector3(1f, 1.8f, 4f);
            Assert.Less(view.MultiplyPoint3x4(focal).z, 0f, "focal must start in front of the eye");

            Vector3 atZero = BasisVrsMath.EyeGazeViewDir(view, focal, 0f);
            Assert.AreEqual(0f, Vector3.Distance(atZero, new Vector3(0f, 0f, -1f)), Eps);

            Vector3 atOne = BasisVrsMath.EyeGazeViewDir(view, focal, 1f);
            Assert.AreEqual(0f, Vector3.Distance(atOne, view.MultiplyPoint3x4(focal).normalized), Eps);

            // Focal point behind the eye plane degrades to the optical axis at any weight.
            Vector3 behindWorld = new Vector3(0.2f, 1.5f, 0f) - new Vector3(Mathf.Sin(20f * Mathf.Deg2Rad), 0f, Mathf.Cos(20f * Mathf.Deg2Rad)) * 2f;
            Vector3 degraded = BasisVrsMath.EyeGazeViewDir(view, behindWorld, 1f);
            Assert.AreEqual(0f, Vector3.Distance(degraded, new Vector3(0f, 0f, -1f)), Eps);
        }

        [Test]
        public void ResolveRatesUsesNativeValuesWhenSupported()
        {
            BasisVrsMath.ResolveRates(true, true, true, true, 5u, 9u, 6u, 10u, out Vector4 rates, out Vector4 aniso);
            Assert.AreEqual(0f, rates.x);
            Assert.AreEqual(5f, rates.y);
            Assert.AreEqual(10f, rates.z);
            Assert.AreEqual(9f, aniso.x);
            Assert.AreEqual(6f, aniso.y);
        }

        [Test]
        public void ResolveRatesFallsBackWithoutAdditionalRates()
        {
            // Tier-2-minimum GPU: 2x2 available, no 4x2/2x4/4x4 — every coarse band clamps to 2x2.
            BasisVrsMath.ResolveRates(true, false, false, false, 5u, 0u, 0u, 0u, out Vector4 rates, out Vector4 aniso);
            Assert.AreEqual(5f, rates.y);
            Assert.AreEqual(5f, rates.z);
            Assert.AreEqual(5f, aniso.x);
            Assert.AreEqual(5f, aniso.y);

            // Nothing beyond 1x1: everything degrades to full rate (and the pass gates itself off).
            BasisVrsMath.ResolveRates(false, false, false, false, 0u, 0u, 0u, 0u, out rates, out aniso);
            Assert.AreEqual(0f, rates.y);
            Assert.AreEqual(0f, rates.z);
            Assert.AreEqual(0f, aniso.x);
            Assert.AreEqual(0f, aniso.y);
        }
    }
}
