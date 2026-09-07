using NUnit.Framework;
using UnityEngine;

namespace Basis.Rendering.RTAO.Tests
{
    public sealed class BasisRTAOStereoPlumbingTests
    {
        [Test]
        public void ViewCountIsTwoOnlyForSinglePassStereo()
        {
            Assert.AreEqual(1, BasisRTAOPass.ViewCountOf(false, false));
            Assert.AreEqual(1, BasisRTAOPass.ViewCountOf(false, true), "XR off means one view no matter what the single pass flag says.");
            Assert.AreEqual(1, BasisRTAOPass.ViewCountOf(true, false), "Multi pass XR renders one view per camera pass.");
            Assert.AreEqual(2, BasisRTAOPass.ViewCountOf(true, true));
        }

        [Test]
        public void TheRayOriginBiasCannotSwallowTheSearchRadius()
        {
            // originBias = min(normalBias + distanceBias * d, radius * 0.25)
            const float normalBias = 0.005f, distanceBias = 0.0005f, radius = 0.1f;
            foreach (float distance in new[] { 1f, 10f, 40f, 100f, 1000f })
            {
                float bias = Mathf.Min(normalBias + distanceBias * distance, radius * 0.25f);
                Assert.Less(bias, radius * 0.5f,
                    $"At {distance} m the ray would start {bias:F3} m above the surface with only {radius} m of search, so occlusion would fade out and then stop.");
            }
        }

        [Test]
        public void TraceResolutionHalvesOnBothAxes()
        {
            Vector2Int size = BasisRTAOPass.TraceResolution(2064, 2208, 2);
            Assert.AreEqual(1032, size.x);
            Assert.AreEqual(1104, size.y);
        }

        [Test]
        public void TraceResolutionPassesThroughAtFullRate()
        {
            Vector2Int size = BasisRTAOPass.TraceResolution(1920, 1080, 1);
            Assert.AreEqual(1920, size.x);
            Assert.AreEqual(1080, size.y);
        }

        [Test]
        public void TraceResolutionClampsTheDivider()
        {
            Assert.AreEqual(new Vector2Int(1920, 1080), BasisRTAOPass.TraceResolution(1920, 1080, 0));
            Assert.AreEqual(new Vector2Int(480, 270), BasisRTAOPass.TraceResolution(1920, 1080, 4));
            Assert.AreEqual(new Vector2Int(480, 270), BasisRTAOPass.TraceResolution(1920, 1080, 99));
        }

        [Test]
        public void TraceResolutionNeverCollapsesToZero()
        {
            Vector2Int size = BasisRTAOPass.TraceResolution(1, 1, 4);
            Assert.AreEqual(1, size.x);
            Assert.AreEqual(1, size.y);
        }

        [Test]
        public void TraceResolutionOnOddSizesStaysInsideTheSourceRect()
        {
            const int fullWidth = 1921;
            const int fullHeight = 1081;
            Vector2Int size = BasisRTAOPass.TraceResolution(fullWidth, fullHeight, 2);

            Assert.LessOrEqual((size.x - 1) * 2 + 1, fullWidth - 1,
                "The prepass reads full res texel halfCoord*scale + 1, which must stay inside the depth texture.");
            Assert.LessOrEqual((size.y - 1) * 2 + 1, fullHeight - 1);
        }

        [Test]
        public void ProjectionMatricesAreBuiltPerEye()
        {
            GameObject go = new GameObject("BasisRTAOMatrixTestCamera");
            try
            {
                Camera camera = go.AddComponent<Camera>();
                camera.nearClipPlane = 0.1f;
                camera.farClipPlane = 100f;
                camera.fieldOfView = 60f;
                camera.aspect = 1f;

                Matrix4x4 left = camera.projectionMatrix * camera.worldToCameraMatrix;
                Matrix4x4 shifted = camera.projectionMatrix * Matrix4x4.Translate(new Vector3(-0.032f, 0f, 0f)) * camera.worldToCameraMatrix;

                Vector3 point = new Vector3(0f, 0f, 5f);
                Vector2 leftUV = ProjectToScreenUV(left, point, out float leftW);
                Vector2 rightUV = ProjectToScreenUV(shifted, point, out float rightW);

                Assert.Greater(leftW, 0f);
                Assert.Greater(rightW, 0f);
                Assert.Greater(Mathf.Abs(leftUV.x - rightUV.x), 1e-4f, "A stereo eye offset must move the projected point, otherwise the per eye matrices are not reaching the shader.");
                Assert.AreEqual(leftUV.y, rightUV.y, 1e-5f, "A horizontal eye offset must not move the point vertically.");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void ClipWCarriesLinearViewDepth()
        {
            GameObject go = new GameObject("BasisRTAODepthTestCamera");
            try
            {
                Camera camera = go.AddComponent<Camera>();
                camera.nearClipPlane = 0.1f;
                camera.farClipPlane = 100f;
                camera.transform.position = Vector3.zero;
                camera.transform.rotation = Quaternion.identity;

                Matrix4x4 viewProjection = camera.projectionMatrix * camera.worldToCameraMatrix;
                ProjectToScreenUV(viewProjection, new Vector3(0f, 0f, 7.5f), out float clipW);

                Assert.AreEqual(7.5f, clipW, 1e-3f,
                    "The temporal pass compares clip.w against the stored history depth, so clip.w must be the linear view depth.");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        public static Vector2 ProjectToScreenUV(Matrix4x4 viewProjection, Vector3 positionWS, out float clipW)
        {
            Vector4 clip = viewProjection * new Vector4(positionWS.x, positionWS.y, positionWS.z, 1f);
            clipW = clip.w;
            return new Vector2(clip.x / clip.w * 0.5f + 0.5f, clip.y / clip.w * 0.5f + 0.5f);
        }
    }
}
