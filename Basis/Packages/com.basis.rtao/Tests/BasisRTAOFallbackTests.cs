using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Basis.Rendering.RTAO.Tests
{
    public sealed class BasisRTAOFallbackTests
    {
        private const int Width = 16;
        private const int Height = 16;

        private BasisRTAOGpuHarness harness;
        private ComputeShader screenSpace;
        private int kernel;

        [SetUp]
        public void SetUp()
        {
            BasisRTAOGpuHarness.SkipUnlessComputeIsAvailable();
            harness = new BasisRTAOGpuHarness();
            screenSpace = AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.basis.rtao/Shaders/BasisRTAOScreenSpace.compute");
            Assert.IsNotNull(screenSpace, "BasisRTAOScreenSpace.compute failed to import.");
            kernel = screenSpace.FindKernel("BasisRTAOScreenSpaceTrace");
            Assert.GreaterOrEqual(kernel, 0);
        }

        [TearDown]
        public void TearDown()
        {
            harness?.Dispose();
            harness = null;
        }

        [Test]
        public void AutoPicksRayTracingWhenTheGpuHasIt()
        {
            Assert.AreEqual(BasisRTAOBackend.Hardware, BasisRTAOTracing.Resolve(BasisRTAOTracingMode.RayTracedOnly, true, true));
        }

        [Test]
        public void AutoFallsBackToScreenSpaceWithoutRayTracing()
        {
            Assert.AreEqual(BasisRTAOBackend.ScreenSpace, BasisRTAOTracing.Resolve(BasisRTAOTracingMode.ScreenSpace, false, true),
                "Direct3D11 has no ray tracing at all, so Auto has to land on the screen space estimator rather than turning the effect off.");
        }

        [Test]
        public void AutoDisablesItselfWithoutComputeShaders()
        {
            Assert.AreEqual(BasisRTAOBackend.None, BasisRTAOTracing.Resolve(BasisRTAOTracingMode.ScreenSpace, false, false));
            Assert.AreEqual(BasisRTAOBackend.None, BasisRTAOTracing.Resolve(BasisRTAOTracingMode.ScreenSpace, false, false),
                "The fallback is a compute kernel, so it cannot rescue a device without compute support.");
        }

        [Test]
        public void RayTracedDegradesToTheEstimatorWithoutHardware()
        {
            Assert.AreEqual(BasisRTAOBackend.Hardware, BasisRTAOTracing.Resolve(BasisRTAOTracingMode.RayTracedOnly, true, true));
            Assert.AreEqual(BasisRTAOBackend.ScreenSpace, BasisRTAOTracing.Resolve(BasisRTAOTracingMode.RayTracedOnly, false, true),
                "A 'Ray Traced' saved on a DXR machine must not mean no occlusion at all on Direct3D11: the" +
                " None gate in AddRenderPasses is silent and the menu hides the mode row on such a device," +
                " so nothing else can rescue the effect there.");
        }

        [Test]
        public void ExplicitModesIgnoreDeviceCapability()
        {
            Assert.AreEqual(BasisRTAOBackend.ScreenSpace, BasisRTAOTracing.Resolve(BasisRTAOTracingMode.ScreenSpace, true, true));
            Assert.AreEqual(BasisRTAOBackend.ComputeBvh, BasisRTAOTracing.Resolve(BasisRTAOTracingMode.ComputeBvh, true, true));
        }

        [Test]
        public void OnlyBvhBackendsCountAsRayTraced()
        {
            Assert.IsTrue(BasisRTAOTracing.IsRayTraced(BasisRTAOBackend.Hardware));
            Assert.IsTrue(BasisRTAOTracing.IsRayTraced(BasisRTAOBackend.ComputeBvh));
            Assert.IsFalse(BasisRTAOTracing.IsRayTraced(BasisRTAOBackend.ScreenSpace),
                "The screen space path must not build an acceleration structure.");
            Assert.IsFalse(BasisRTAOTracing.IsRayTraced(BasisRTAOBackend.None));
        }

        [Test]
        public void ProjectionScaleGrowsWithResolutionAndNarrowsWithFov()
        {
            GameObject go = harness.Track(new GameObject("BasisRTAOProjectionScale"));
            Camera camera = go.AddComponent<Camera>();
            camera.aspect = 1f;

            camera.fieldOfView = 60f;
            float wide = BasisRTAOTracing.ProjectionScale(camera.projectionMatrix, 1080, false);

            camera.fieldOfView = 30f;
            float narrow = BasisRTAOTracing.ProjectionScale(camera.projectionMatrix, 1080, false);

            camera.fieldOfView = 60f;
            float half = BasisRTAOTracing.ProjectionScale(camera.projectionMatrix, 540, false);

            Assert.Greater(narrow, wide, "A narrower field of view puts more pixels on the same world span.");
            Assert.AreEqual(wide * 0.5f, half, 1e-3f, "Halving the target height must halve the pixels per world unit.");
            Assert.AreEqual(0f, BasisRTAOTracing.ProjectionScale(camera.projectionMatrix, 1080, true),
                "An orthographic camera has no depth dependent scale.");
        }

        [Test]
        public void DescribeNamesEveryBackend()
        {
            foreach (BasisRTAOBackend backend in System.Enum.GetValues(typeof(BasisRTAOBackend)))
                Assert.IsNotEmpty(BasisRTAOTracing.Describe(backend));
        }

        private Vector4[] RunScreenSpace(System.Func<int, int, Vector4> positionOf, Vector3 normalWS, float radius, int samples, int slices = 1, float falloff = 0f)
        {
            Texture2DArray position = harness.Track(new Texture2DArray(Width, Height, slices, TextureFormat.RGBAFloat, false, true));
            Texture2DArray normal = harness.Track(new Texture2DArray(Width, Height, slices, TextureFormat.RGBAFloat, false, true));
            position.filterMode = FilterMode.Point;
            normal.filterMode = FilterMode.Point;

            Vector2 encoded = EncodeNormal(normalWS);
            for (int slice = 0; slice < slices; slice++)
            {
                Color[] positionPixels = new Color[Width * Height];
                Color[] normalPixels = new Color[Width * Height];
                for (int y = 0; y < Height; y++)
                {
                    for (int x = 0; x < Width; x++)
                    {
                        Vector4 p = positionOf(x, y);
                        positionPixels[y * Width + x] = new Color(p.x, p.y, p.z, p.w);
                        normalPixels[y * Width + x] = new Color(encoded.x, encoded.y, 0f, 0f);
                    }
                }
                position.SetPixels(positionPixels, slice);
                normal.SetPixels(normalPixels, slice);
            }
            position.Apply(false, false);
            normal.Apply(false, false);

            RenderTexture result = harness.Track(new RenderTexture(new RenderTextureDescriptor(Width, Height, GraphicsFormat.R16G16_SFloat, GraphicsFormat.None, 0)
            {
                dimension = TextureDimension.Tex2DArray,
                volumeDepth = slices,
                enableRandomWrite = true,
                msaaSamples = 1
            }));
            result.filterMode = FilterMode.Point;
            result.hideFlags = HideFlags.HideAndDontSave;
            result.Create();

            Vector4 plane = new Vector4(0f, 0f, 1f, 5f);
            // The fake geometry is authored directly in texel space - column x holds world x*0.05, row y
            // world y*0.05 - so a texel step maps to the plain world axes.
            Vector4 axisX = new Vector4(1f, 0f, 0f, 0f);
            Vector4 axisY = new Vector4(0f, 1f, 0f, 0f);
            screenSpace.SetTexture(kernel, BasisRTAOShaderIds.PositionTex, position);
            screenSpace.SetTexture(kernel, BasisRTAOShaderIds.NormalTex, normal);
            screenSpace.SetTexture(kernel, BasisRTAOShaderIds.ResultTex, result);
            screenSpace.SetVectorArray(BasisRTAOShaderIds.ViewPlane, new[] { plane, plane });
            screenSpace.SetVectorArray(BasisRTAOShaderIds.ScreenAxisX, new[] { axisX, axisX });
            screenSpace.SetVectorArray(BasisRTAOShaderIds.ScreenAxisY, new[] { axisY, axisY });
            screenSpace.SetVector(BasisRTAOShaderIds.Reference, Vector4.zero);
            screenSpace.SetVector(BasisRTAOShaderIds.Trace, new Vector4(samples, radius, falloff, 0f));
            screenSpace.SetVector(BasisRTAOShaderIds.Bias, new Vector4(0.002f, 0.0015f, 0.01f, 0f));
            screenSpace.SetVector(BasisRTAOShaderIds.Size, new Vector4(Width, Height, 1f / Width, 1f / Height));
            screenSpace.SetVector(BasisRTAOShaderIds.ScreenParams, new Vector4(200f, 8f, 2f, 0f));
            screenSpace.SetInt(BasisRTAOShaderIds.RayCount, samples);
            screenSpace.SetInt(BasisRTAOShaderIds.ViewCount, slices);
            screenSpace.SetInt(BasisRTAOShaderIds.FrameIndex, 2);
            screenSpace.SetInt(BasisRTAOShaderIds.StereoCoherent, 1);
            screenSpace.Dispatch(kernel, (Width + 7) / 8, (Height + 7) / 8, slices);

            return harness.ReadTextureArray(result, Width, Height, slices);
        }

        private static Vector2 EncodeNormal(Vector3 normal)
        {
            normal.Normalize();
            float sum = Mathf.Abs(normal.x) + Mathf.Abs(normal.y) + Mathf.Abs(normal.z);
            Vector2 encoded = new Vector2(normal.x / sum, normal.y / sum);
            if (normal.z < 0f)
            {
                encoded = new Vector2(
                    (1f - Mathf.Abs(encoded.y)) * (encoded.x >= 0f ? 1f : -1f),
                    (1f - Mathf.Abs(encoded.x)) * (encoded.y >= 0f ? 1f : -1f));
            }
            return encoded;
        }

        private static float Mean(Vector4[] values, int slice = 0)
        {
            float sum = 0f;
            int offset = slice * Width * Height;
            for (int i = 0; i < Width * Height; i++)
                sum += values[offset + i].x;
            return sum / (Width * Height);
        }

        [Test]
        public void FlatSurfaceIsUnoccluded()
        {
            Vector4[] result = RunScreenSpace((x, y) => new Vector4(x * 0.05f, y * 0.05f, 5f, 1f), Vector3.back, 1f, 16);
            Assert.Greater(Mean(result), 0.95f,
                $"A flat plane facing the camera must read as open, got {Mean(result):F3}. Anything less means the estimator is occluding against its own surface.");
        }

        // The head-on case above is the one pose where dividing the summed slice visibility by the slice
        // COUNT happens to be right: every slice sits square to the surface, so its fully-open value is
        // exactly 1 and a mean and a ratio agree. Tilt the plane and they stop agreeing - a slice's open
        // value becomes projectedLength * (cos(gamma) + gamma * sin(gamma)), which is not 1 and differs
        // per slice - so the mean form under-reads a surface that is not occluded by anything at all. The
        // taps here all lie ON the plane, so no horizon can rise above it and the correct answer is 1
        // whatever the tilt, the slice count, or the per-frame slice rotation.
        [Test]
        public void TiltedUnoccludedSurfaceIsAlsoOpen([Values(1, 4, 16)] int samples)
        {
            // A plane through (0.4, 0.4, 5) receding in z as x grows: dp/dx = (0.05, 0, 0.05), dp/dy =
            // (0, 0.05, 0), so it leans 45 degrees away from a camera at the origin. The normal faces the
            // camera, matching the flat case's Vector3.back convention.
            Vector3 tilted = new Vector3(1f, 0f, -1f).normalized;
            Vector4[] result = RunScreenSpace(
                (x, y) => new Vector4(x * 0.05f, y * 0.05f, 5f + (x - 8) * 0.05f, 1f), tilted, 1f, samples);

            Assert.Greater(Mean(result), 0.95f,
                $"A tilted plane with nothing in front of it must still read as open at {samples} samples, " +
                $"got {Mean(result):F3}. Below this the estimator is normalising against the slice count " +
                "rather than against how much hemisphere those slices could see.");
        }

        [Test]
        public void SkyPixelsReportFullVisibility()
        {
            Vector4[] result = RunScreenSpace((x, y) => Vector4.zero, Vector3.back, 1f, 16);
            for (int i = 0; i < result.Length; i++)
                Assert.AreEqual(1f, result[i].x, 1e-3f, $"texel {i} has no geometry and must be fully visible.");
        }

        [Test]
        public void ARaisedLedgeOccludesTheSurfaceBesideIt()
        {
            Vector4[] flat = RunScreenSpace((x, y) => new Vector4(x * 0.05f, y * 0.05f, 5f, 1f), Vector3.back, 1f, 24);

            Vector4[] stepped = RunScreenSpace((x, y) =>
            {
                float depth = x >= Width / 2 ? 4.4f : 5f;
                return new Vector4(x * 0.05f, y * 0.05f, depth, 1f);
            }, Vector3.back, 1f, 24);

            float flatEdge = 0f, steppedEdge = 0f;
            int count = 0;
            for (int y = 0; y < Height; y++)
            {
                for (int x = Width / 2 - 3; x < Width / 2; x++)
                {
                    flatEdge += flat[y * Width + x].x;
                    steppedEdge += stepped[y * Width + x].x;
                    count++;
                }
            }
            flatEdge /= count;
            steppedEdge /= count;

            Assert.Less(steppedEdge, flatEdge,
                $"Geometry standing 60 cm proud of the surface must darken the pixels beside it. Flat read {flatEdge:F3}, stepped read {steppedEdge:F3}.");
            Assert.Less(steppedEdge, 0.95f, "The occlusion is too faint to be doing anything.");
        }

        [Test]
        public void OcclusionDeepensWithATallerLedgeInsideTheEstimatorRange()
        {
            Vector4[] shallow = RunScreenSpace((x, y) => new Vector4(x * 0.05f, y * 0.05f, x >= Width / 2 ? 4.95f : 5f, 1f), Vector3.back, 1f, 24);
            Vector4[] deep = RunScreenSpace((x, y) => new Vector4(x * 0.05f, y * 0.05f, x >= Width / 2 ? 4.85f : 5f, 1f), Vector3.back, 1f, 24);

            float shallowEdge = 0f, deepEdge = 0f;
            int count = 0;
            for (int y = 0; y < Height; y++)
            {
                for (int x = Width / 2 - 3; x < Width / 2; x++)
                {
                    shallowEdge += shallow[y * Width + x].x;
                    deepEdge += deep[y * Width + x].x;
                    count++;
                }
            }

            Assert.Less(deepEdge / count, shallowEdge / count,
                $"Within the estimator's range a taller ledge covers more of the hemisphere and must read darker. 5 cm read {shallowEdge / count:F3}, 15 cm read {deepEdge / count:F3}.");
        }

        [Test]
        public void ARadiusTooShortToReachTheLedgeSeesNothing()
        {
            Vector4[] result = RunScreenSpace((x, y) => new Vector4(x * 0.05f, y * 0.05f, x >= Width / 2 ? 4.4f : 5f, 1f), Vector3.back, 0.05f, 24);

            float edge = 0f;
            int count = 0;
            for (int y = 0; y < Height; y++)
            {
                for (int x = Width / 2 - 3; x < Width / 2 - 1; x++)
                {
                    edge += result[y * Width + x].x;
                    count++;
                }
            }

            Assert.Greater(edge / count, 0.9f,
                "The world space radius rejects samples beyond it, so a 5 cm radius must not see a 60 cm step three texels away.");
        }

        [Test]
        public void BothEyesAgreeOnIdenticalInput()
        {
            Vector4[] result = RunScreenSpace((x, y) => new Vector4(x * 0.05f, y * 0.05f, x >= Width / 2 ? 4.4f : 5f, 1f), Vector3.back, 1f, 16, slices: 2);

            for (int i = 0; i < Width * Height; i++)
            {
                Assert.AreEqual(result[i].x, result[i + Width * Height].x, 1e-3f,
                    $"texel {i} disagreed between eyes. The fallback shares the world hashed seed for exactly this reason.");
            }
        }

        [Test]
        public void AWallBesideTheFloorReadsNearHalfVisibility()
        {
            // An infinite wall blocks exactly half of the hemisphere over the floor at its base, and the
            // cosine-weighted visibility there is exactly one half - a value the horizon integration must
            // land near and the old disc estimator never expressed. Columns 10+ hold points on a vertical
            // wall at world x = 0.5, rising towards the camera as the columns go right.
            Vector4[] result = RunScreenSpace((x, y) =>
            {
                if (x < 10) { return new Vector4(x * 0.05f, y * 0.05f, 5f, 1f); }
                return new Vector4(0.5f, y * 0.05f, 5f - (x - 9.5f) * 0.7f, 1f);
            }, Vector3.back, 4f, 24);

            float mean = 0f;
            int count = 0;
            for (int y = 4; y < 12; y++)
            {
                for (int x = 6; x < 10; x++)
                {
                    mean += result[y * Width + x].x;
                    count++;
                }
            }
            mean /= count;

            Assert.Greater(mean, 0.35f, $"The floor beside the wall read {mean:F3}; half the hemisphere is still open.");
            Assert.Less(mean, 0.65f, $"The floor beside the wall read {mean:F3}; an infinite wall must cost about half the hemisphere.");
        }

        [Test]
        public void DistanceFalloffSoftensWhatTheEdgeOfTheRadiusSees()
        {
            System.Func<int, int, Vector4> ledge = (x, y) => new Vector4(x * 0.05f, y * 0.05f, x >= Width / 2 ? 4.4f : 5f, 1f);
            Vector4[] hard = RunScreenSpace(ledge, Vector3.back, 1f, 24, falloff: 0f);
            Vector4[] soft = RunScreenSpace(ledge, Vector3.back, 1f, 24, falloff: 6f);

            float hardEdge = 0f, softEdge = 0f;
            int count = 0;
            for (int y = 0; y < Height; y++)
            {
                for (int x = Width / 2 - 3; x < Width / 2; x++)
                {
                    hardEdge += hard[y * Width + x].x;
                    softEdge += soft[y * Width + x].x;
                    count++;
                }
            }

            Assert.Greater(softEdge / count, hardEdge / count + 0.02f,
                $"Falloff discounts occluders near the edge of the radius, so the ledge must darken less through it. Hard read {hardEdge / count:F3}, soft read {softEdge / count:F3}.");
        }

        [Test]
        public void OutputStaysInsideTheVisibilityRange()
        {
            Vector4[] result = RunScreenSpace((x, y) => new Vector4(x * 0.05f, y * 0.05f, (x / 2 + y / 2) % 2 == 0 ? 5f : 4.2f, 1f), Vector3.back, 1.5f, 24);
            for (int i = 0; i < result.Length; i++)
            {
                Assert.GreaterOrEqual(result[i].x, 0f, $"texel {i} visibility went negative.");
                Assert.LessOrEqual(result[i].x, 1f, $"texel {i} visibility exceeded one.");
                Assert.GreaterOrEqual(result[i].y, 0f, $"texel {i} mean distance went negative.");
                Assert.LessOrEqual(result[i].y, 1f, $"texel {i} mean distance exceeded one.");
            }
        }
    }
}
