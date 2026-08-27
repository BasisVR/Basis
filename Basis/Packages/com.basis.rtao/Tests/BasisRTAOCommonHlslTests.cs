using NUnit.Framework;
using UnityEngine;

namespace Basis.Rendering.RTAO.Tests
{
    public sealed class BasisRTAOCommonHlslTests
    {
        private BasisRTAOGpuHarness harness;

        [SetUp]
        public void SetUp()
        {
            BasisRTAOGpuHarness.SkipUnlessComputeIsAvailable();
            harness = new BasisRTAOGpuHarness();
        }

        [TearDown]
        public void TearDown()
        {
            harness?.Dispose();
            harness = null;
        }

        private static Vector4[] SampleDirections(int count)
        {
            Vector4[] directions = new Vector4[count];
            Random.State state = Random.state;
            Random.InitState(20260827);
            for (int i = 0; i < count; i++)
            {
                Vector3 direction = Random.onUnitSphere;
                directions[i] = new Vector4(direction.x, direction.y, direction.z, 0f);
            }
            Random.state = state;
            return directions;
        }

        [Test]
        public void OctahedralEncodingSurvivesTheRoundTrip()
        {
            Vector4[] input = SampleDirections(256);
            Vector4[] output = harness.RunLinearKernel("TestOctahedral", input, input.Length);

            for (int i = 0; i < output.Length; i++)
            {
                Vector3 decoded = new Vector3(output[i].x, output[i].y, output[i].z);
                Assert.AreEqual(1f, decoded.magnitude, 1e-3f, $"decoded normal {i} is not unit length.");
                Assert.Greater(output[i].w, 0.999f, $"normal {i} lost more than 2.5 degrees through the octahedral round trip.");
            }
        }

        [Test]
        public void OctahedralEncodingHandlesTheAxes()
        {
            Vector4[] input =
            {
                new Vector4(1f, 0f, 0f, 0f), new Vector4(-1f, 0f, 0f, 0f),
                new Vector4(0f, 1f, 0f, 0f), new Vector4(0f, -1f, 0f, 0f),
                new Vector4(0f, 0f, 1f, 0f), new Vector4(0f, 0f, -1f, 0f)
            };
            Vector4[] output = harness.RunLinearKernel("TestOctahedral", input, input.Length);

            for (int i = 0; i < output.Length; i++)
                Assert.Greater(output[i].w, 0.9999f, $"axis {i} did not round trip; the octahedral wrap is wrong in a hemisphere.");
        }

        [Test]
        public void CosineHemisphereStaysAboveTheSurface()
        {
            const int count = 512;
            Vector4[] output = harness.RunLinearKernel("TestCosineHemisphere", null, count, (shader, kernel) =>
            {
                shader.SetVector("_TestNormal", new Vector4(0f, 1f, 0f, 0f));
                shader.SetVector("_TestParams", new Vector4(0.37f, 0.11f, 0f, 0f));
            });

            for (int i = 0; i < output.Length; i++)
            {
                Vector3 direction = new Vector3(output[i].x, output[i].y, output[i].z);
                Assert.AreEqual(1f, direction.magnitude, 1e-3f, $"direction {i} is not unit length.");
                Assert.GreaterOrEqual(output[i].w, -1e-3f, $"direction {i} points into the surface, which would self occlude every ray.");
            }
        }

        [Test]
        public void CosineHemisphereIsCosineWeighted()
        {
            const int count = 4096;
            Vector4[] output = harness.RunLinearKernel("TestCosineHemisphere", null, count, (shader, kernel) =>
            {
                shader.SetVector("_TestNormal", new Vector4(0f, 1f, 0f, 0f));
                shader.SetVector("_TestParams", new Vector4(0.13f, 0.71f, 0f, 0f));
            });

            double sum = 0.0;
            for (int i = 0; i < output.Length; i++)
                sum += output[i].w;

            double mean = sum / output.Length;
            Assert.AreEqual(2.0 / 3.0, mean, 0.03,
                "A cosine weighted hemisphere has mean cos(theta) = 2/3. A uniform hemisphere would read 1/2 and would bias the occlusion estimate.");
        }

        [Test]
        public void CosineHemisphereFollowsATiltedNormal()
        {
            const int count = 2048;
            Vector3 normal = new Vector3(0.6f, 0.5f, -0.62f).normalized;
            Vector4[] output = harness.RunLinearKernel("TestCosineHemisphere", null, count, (shader, kernel) =>
            {
                shader.SetVector("_TestNormal", new Vector4(normal.x, normal.y, normal.z, 0f));
                shader.SetVector("_TestParams", new Vector4(0.29f, 0.53f, 0f, 0f));
            });

            Vector3 mean = Vector3.zero;
            for (int i = 0; i < output.Length; i++)
            {
                Vector3 direction = new Vector3(output[i].x, output[i].y, output[i].z);
                Assert.GreaterOrEqual(Vector3.Dot(direction, normal), -1e-3f);
                mean += direction;
            }

            mean /= output.Length;
            Assert.Greater(Vector3.Dot(mean.normalized, normal), 0.99f,
                "The average sampled direction must line up with the surface normal.");
        }

        [Test]
        public void HammersleySequenceStaysInTheUnitSquare()
        {
            const int count = 256;
            Vector4[] output = harness.RunLinearKernel("TestHammersley", null, count, (shader, kernel) =>
                shader.SetVector("_TestParams", new Vector4(0.25f, 0.75f, 3f, 0f)));

            float minX = float.MaxValue, maxX = float.MinValue;
            for (int i = 0; i < output.Length; i++)
            {
                Assert.GreaterOrEqual(output[i].x, 0f);
                Assert.Less(output[i].x, 1f);
                Assert.GreaterOrEqual(output[i].y, 0f);
                Assert.Less(output[i].y, 1f);
                Assert.GreaterOrEqual(output[i].z, 0f);
                Assert.Less(output[i].z, 1f);
                minX = Mathf.Min(minX, output[i].x);
                maxX = Mathf.Max(maxX, output[i].x);
            }

            Assert.Less(minX, 0.1f, "The sequence must cover the low end of the unit square.");
            Assert.Greater(maxX, 0.9f, "The sequence must cover the high end of the unit square.");
        }

        [Test]
        public void HammersleyIsStratifiedNotClustered()
        {
            const int count = 256;
            Vector4[] output = harness.RunLinearKernel("TestHammersley", null, count, (shader, kernel) =>
                shader.SetVector("_TestParams", new Vector4(0f, 0f, 1f, 0f)));

            int[] bins = new int[8];
            for (int i = 0; i < output.Length; i++)
                bins[Mathf.Clamp((int)(output[i].y * 8f), 0, 7)]++;

            int expected = count / 8;
            for (int i = 0; i < bins.Length; i++)
                Assert.AreEqual(expected, bins[i], expected * 0.35f, $"bin {i} of the radical inverse is not evenly filled.");
        }

        [Test]
        public void WorldCellHashIsStableInsideACell()
        {
            const float cellSize = 0.01f;
            Vector4[] input =
            {
                new Vector4(1.2345f, 0.5f, -3.21f, 0f),
                new Vector4(1.2345f + cellSize * 0.4f, 0.5f + cellSize * 0.2f, -3.21f + cellSize * 0.1f, 0f)
            };

            Vector4[] output = harness.RunLinearKernel("TestNoiseSeed", input, input.Length, (shader, kernel) =>
                shader.SetVector("_TestParams", new Vector4(cellSize, 11f, 0f, 0f)));

            Assert.AreEqual(output[0].x, output[1].x,
                "Two eyes reconstruct slightly different world positions for the same surface point. Inside one cell the sample offset must match, or each eye traces different rays and the frame shimmers between them.");
            Assert.AreEqual(output[0].y, output[1].y);
            Assert.AreEqual(output[0].z, output[1].z, 1e-6f, "The same offset must produce the same ray.");
            Assert.AreEqual(output[0].w, output[1].w, 1e-6f);
        }

        [Test]
        public void WorldCellHashChangesBetweenCells()
        {
            const float cellSize = 0.01f;
            Vector4[] input =
            {
                new Vector4(1.2345f, 0.5f, -3.21f, 0f),
                new Vector4(1.2345f + cellSize * 3f, 0.5f, -3.21f, 0f),
                new Vector4(1.2345f, 0.5f + cellSize * 3f, -3.21f, 0f),
                new Vector4(1.2345f, 0.5f, -3.21f + cellSize * 3f, 0f)
            };

            Vector4[] output = harness.RunLinearKernel("TestNoiseSeed", input, input.Length, (shader, kernel) =>
                shader.SetVector("_TestParams", new Vector4(cellSize, 11f, 0f, 0f)));

            Assert.AreNotEqual(output[0].x, output[1].x, "Neighbouring cells on x must decorrelate.");
            Assert.AreNotEqual(output[0].x, output[2].x, "Neighbouring cells on y must decorrelate.");
            Assert.AreNotEqual(output[0].x, output[3].x, "Neighbouring cells on z must decorrelate.");
        }

        [Test]
        public void WorldCellHashChangesEveryFrame()
        {
            Vector4[] input = { new Vector4(1.2345f, 0.5f, -3.21f, 0f) };

            Vector4[] frameA = harness.RunLinearKernel("TestNoiseSeed", input, 1, (shader, kernel) =>
                shader.SetVector("_TestParams", new Vector4(0.01f, 4f, 0f, 0f)));
            Vector4[] frameB = harness.RunLinearKernel("TestNoiseSeed", input, 1, (shader, kernel) =>
                shader.SetVector("_TestParams", new Vector4(0.01f, 5f, 0f, 0f)));

            Assert.AreNotEqual(frameA[0].x, frameB[0].x,
                "The offset must advance with the frame index, otherwise temporal accumulation averages the same rays forever and never converges.");
        }

        [Test]
        public void TheFrameSequenceIsLowDiscrepancyNotWhiteNoise()
        {
            const int frames = 16;
            Vector4[] input = { new Vector4(1.2345f, 0.5f, -3.21f, 0f) };

            Vector2[] offsets = new Vector2[frames];
            for (int frame = 0; frame < frames; frame++)
            {
                int captured = frame;
                Vector4[] output = harness.RunLinearKernel("TestNoiseSeed", input, 1, (shader, kernel) =>
                    shader.SetVector("_TestParams", new Vector4(0.01f, captured, 0f, 0f)));
                offsets[frame] = new Vector2(output[0].x, output[0].y);
            }

            float closest = float.MaxValue;
            for (int i = 0; i < frames; i++)
            {
                for (int j = i + 1; j < frames; j++)
                    closest = Mathf.Min(closest, ToroidalDistance(offsets[i], offsets[j]));
            }

            // R2 holds 0.17 at sixteen points. Independent draws sit at 0.04 and clear 0.084 only one time
            // in twenty, so this separates the two outright.
            Assert.Greater(closest, 0.12f,
                $"Re-hashing the cell with the frame index draws an independent offset every frame, and what the accumulator then averages converges at 1/sqrt(n). The closest pair of sixteen consecutive offsets was {closest:F3}, which is what white noise looks like.");
        }

        [Test]
        public void TheFrameSequenceStillAdvancesHoursIn()
        {
            Vector4[] input = { new Vector4(1.2345f, 0.5f, -3.21f, 0f) };

            Vector4[] first = harness.RunLinearKernel("TestNoiseSeed", input, 1, (shader, kernel) =>
                shader.SetVector("_TestParams", new Vector4(0.01f, 100000f, 0f, 0f)));
            Vector4[] second = harness.RunLinearKernel("TestNoiseSeed", input, 1, (shader, kernel) =>
                shader.SetVector("_TestParams", new Vector4(0.01f, 100001f, 0f, 0f)));

            Assert.AreNotEqual(first[0].x, second[0].x,
                "A raw frame counter reaches six figures inside an hour of play, and a float multiplied by it has no fraction left to carry. Wrap the index before it scales the lattice or the offset silently stops moving.");
        }

        private static float ToroidalDistance(Vector2 a, Vector2 b)
        {
            float dx = Mathf.Abs(a.x - b.x);
            float dy = Mathf.Abs(a.y - b.y);
            return new Vector2(Mathf.Min(dx, 1f - dx), Mathf.Min(dy, 1f - dy)).magnitude;
        }

        [Test]
        public void ProjectionHelperMatchesTheManagedReference()
        {
            GameObject go = new GameObject("BasisRTAOProjectionTestCamera");
            try
            {
                Camera camera = go.AddComponent<Camera>();
                camera.transform.position = new Vector3(1f, 2f, -3f);
                camera.transform.rotation = Quaternion.Euler(12f, 34f, 0f);
                camera.nearClipPlane = 0.1f;
                camera.farClipPlane = 200f;
                camera.fieldOfView = 65f;
                camera.aspect = 16f / 9f;

                Matrix4x4 viewProjection = camera.projectionMatrix * camera.worldToCameraMatrix;

                Vector4[] input =
                {
                    new Vector4(1f, 2f, 5f, 0f),
                    new Vector4(-4f, 0.5f, 9f, 0f),
                    new Vector4(6f, -2f, 20f, 0f),
                    new Vector4(0f, 0f, 0.5f, 0f)
                };

                Vector4[] output = harness.RunLinearKernel("TestProjection", input, input.Length, (shader, kernel) =>
                    shader.SetMatrix("_TestViewProj", viewProjection));

                for (int i = 0; i < input.Length; i++)
                {
                    Vector3 point = new Vector3(input[i].x, input[i].y, input[i].z);
                    Vector2 expected = BasisRTAOStereoPlumbingTests.ProjectToScreenUV(viewProjection, point, out float expectedW);

                    Assert.AreEqual(expectedW, output[i].z, 1e-3f, $"point {i} clip w mismatch.");
                    Assert.AreEqual(expected.x, output[i].x, 1e-4f, $"point {i} u mismatch.");
                    Assert.AreEqual(expected.y, output[i].y, 1e-4f, $"point {i} v mismatch.");
                }
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void ProjectionRoundTripsThroughTheScreenCentre()
        {
            GameObject go = new GameObject("BasisRTAOCentreTestCamera");
            try
            {
                Camera camera = go.AddComponent<Camera>();
                camera.transform.position = Vector3.zero;
                camera.transform.rotation = Quaternion.identity;
                camera.nearClipPlane = 0.1f;
                camera.farClipPlane = 100f;

                Matrix4x4 viewProjection = camera.projectionMatrix * camera.worldToCameraMatrix;
                Vector4[] output = harness.RunLinearKernel("TestProjection", new[] { new Vector4(0f, 0f, 10f, 0f) }, 1,
                    (shader, kernel) => shader.SetMatrix("_TestViewProj", viewProjection));

                Assert.AreEqual(0.5f, output[0].x, 1e-4f, "A point on the forward axis must land at the horizontal centre.");
                Assert.AreEqual(0.5f, output[0].y, 1e-4f, "A point on the forward axis must land at the vertical centre.");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }
    }
}
