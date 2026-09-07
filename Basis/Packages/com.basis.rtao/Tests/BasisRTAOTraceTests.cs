using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.UnifiedRayTracing;

namespace Basis.Rendering.RTAO.Tests
{
    public sealed class BasisRTAOTraceTests
    {
        private const int Width = 8;
        private const int Height = 8;
        private const int Slices = 2;

        private BasisRTAOGpuHarness harness;
        private BasisRTAOContext context;
        private IRayTracingAccelStruct accelStruct;
        private readonly List<Mesh> meshes = new List<Mesh>();

        [SetUp]
        public void SetUp()
        {
            BasisRTAOGpuHarness.SkipUnlessComputeIsAvailable();
            harness = new BasisRTAOGpuHarness();

            BasisRTAOResources resources = ScriptableObject.CreateInstance<BasisRTAOResources>();
            harness.Track(resources);
            resources.PopulateFromPackage();

            context = BasisRTAOContext.Create(resources, BasisRTAOContext.HardwareSupported ? BasisRTAOBackend.Hardware : BasisRTAOBackend.ComputeBvh, out string error);
            if (context == null)
                Assert.Ignore($"No ray tracing backend is available here: {error}");
        }

        [TearDown]
        public void TearDown()
        {
            accelStruct?.Dispose();
            accelStruct = null;
            context?.Dispose();
            context = null;

            for (int i = 0; i < meshes.Count; i++)
            {
                if (meshes[i] != null)
                    Object.DestroyImmediate(meshes[i]);
            }
            meshes.Clear();

            harness?.Dispose();
            harness = null;
        }

        [Test]
        public void ContextPicksHardwareWhenTheGpuHasIt()
        {
            RayTracingBackend expected = BasisRTAOContext.HardwareSupported ? RayTracingBackend.Hardware : RayTracingBackend.Compute;
            Assert.AreEqual(expected, context.Backend);
            Assert.IsNotNull(context.TraceShader);
        }

        [Test]
        public void ContextRefusesComputeWhenTheFallbackIsOff()
        {
            if (BasisRTAOContext.HardwareSupported)
                Assert.Ignore("This GPU has hardware ray tracing, so the refusal path cannot be exercised here.");

            BasisRTAOResources resources = ScriptableObject.CreateInstance<BasisRTAOResources>();
            try
            {
                resources.PopulateFromPackage();
                BasisRTAOContext refused = BasisRTAOContext.Create(resources, BasisRTAOBackend.Hardware, out string error);
                Assert.IsNull(refused);
                Assert.IsNotEmpty(error);
            }
            finally
            {
                Object.DestroyImmediate(resources);
            }
        }

        [Test]
        public void ContextRefusesEmptyResources()
        {
            BasisRTAOResources resources = ScriptableObject.CreateInstance<BasisRTAOResources>();
            try
            {
                BasisRTAOContext refused = BasisRTAOContext.Create(resources, BasisRTAOBackend.ComputeBvh, out string error);
                Assert.IsNull(refused);
                StringAssert.Contains("missing", error);
            }
            finally
            {
                Object.DestroyImmediate(resources);
            }
        }

        [Test]
        public void CeilingOccludesTheGroundBelowIt()
        {
            BuildScene(withCeiling: true);
            Vector4[] result = Trace(SamplePoints(Vector3.zero, new Vector3(40f, 0f, 0f)), radius: 4f, rays: 8);

            float under = MeanVisibility(result, 0);
            float away = MeanVisibility(result, 1);

            Assert.Less(under, 0.35f, $"A ceiling 5 cm above the surface must occlude most of the hemisphere, got {under:F3}.");
            Assert.Greater(away, 0.9f, $"A point 40 m from the only occluder must stay open, got {away:F3}.");
            Assert.Greater(away - under, 0.5f, "The occluded and open regions must separate clearly.");
        }

        [Test]
        public void OpenSkyIsFullyVisible()
        {
            BuildScene(withCeiling: false);
            Vector4[] result = Trace(SamplePoints(Vector3.zero, new Vector3(40f, 0f, 0f)), radius: 4f, rays: 8);

            Assert.Greater(MeanVisibility(result, 0), 0.95f, "With nothing above the surface, every ray must escape.");
            Assert.Greater(MeanVisibility(result, 1), 0.95f);
        }

        [Test]
        public void GroundPlaneDoesNotSelfOcclude()
        {
            BuildScene(withCeiling: false);
            Vector4[] result = Trace(SamplePoints(Vector3.zero, Vector3.zero), radius: 4f, rays: 16);

            float visibility = MeanVisibility(result, 0);
            Assert.Greater(visibility, 0.95f,
                $"Rays leaving a flat surface must not hit the surface they started on. Got {visibility:F3}, which means the ray origin bias is too small.");
        }

        [Test]
        public void EachSliceTracesItsOwnGeometry()
        {
            BuildScene(withCeiling: true);
            Vector4[] result = Trace(SamplePoints(Vector3.zero, new Vector3(40f, 0f, 0f)), radius: 4f, rays: 8);

            float sliceZero = MeanVisibility(result, 0);
            float sliceOne = MeanVisibility(result, 1);

            Assert.Greater(Mathf.Abs(sliceZero - sliceOne), 0.2f,
                "Slice 1 must be traced from its own positions. Matching results would mean dispatchThreadID.z never reached the array index, which is exactly the VR path.");
        }

        [Test]
        public void MatchingSlicesProduceMatchingOcclusion()
        {
            BuildScene(withCeiling: true);
            Vector4[] result = Trace(SamplePoints(Vector3.zero, Vector3.zero), radius: 4f, rays: 8);

            for (int i = 0; i < Width * Height; i++)
            {
                float left = result[i].x;
                float right = result[i + Width * Height].x;
                Assert.AreEqual(left, right, 1e-3f,
                    $"texel {i} disagreed between eyes. Identical inputs must give identical occlusion, otherwise the two eyes shimmer against each other.");
            }
        }

        [Test]
        public void InvalidPixelsReportFullVisibility()
        {
            BuildScene(withCeiling: true);

            Vector4[] positions = new Vector4[Width * Height * Slices];
            Vector4[] normals = new Vector4[Width * Height * Slices];
            for (int i = 0; i < positions.Length; i++)
            {
                positions[i] = new Vector4(0f, 0f, 0f, 0f);
                normals[i] = EncodeNormal(Vector3.up);
            }

            Vector4[] result = Trace(positions, normals, radius: 4f, rays: 8);
            for (int i = 0; i < result.Length; i++)
                Assert.AreEqual(1f, result[i].x, 1e-3f, $"texel {i} was marked as sky but still traced rays.");
        }

        [Test]
        public void ShorterRadiusSeesLessOcclusion()
        {
            BuildScene(withCeiling: true, ceilingHeight: 1.5f);
            Vector4[] positions = SamplePoints(Vector3.zero, Vector3.zero);

            float wide = MeanVisibility(Trace(positions, radius: 4f, rays: 16), 0);
            float narrow = MeanVisibility(Trace(positions, radius: 0.5f, rays: 16), 0);

            Assert.Greater(narrow, wide,
                "A ceiling beyond the ray length must not register, so the shorter radius has to read brighter.");
            Assert.Greater(narrow, 0.95f, "Nothing sits inside the short radius, so it must be fully open.");
        }

        [Test]
        public void MeanHitDistanceTracksOccluderProximity()
        {
            BuildScene(withCeiling: true, ceilingHeight: 0.05f);
            Vector4[] close = Trace(SamplePoints(Vector3.zero, Vector3.zero), radius: 4f, rays: 16);
            float closeDistance = MeanDistance(close, 0);

            TearDownScene();
            BuildScene(withCeiling: true, ceilingHeight: 2f);
            Vector4[] far = Trace(SamplePoints(Vector3.zero, Vector3.zero), radius: 4f, rays: 16);
            float farDistance = MeanDistance(far, 0);

            Assert.Less(closeDistance, farDistance,
                "The second channel feeds the adaptive blur radius, so it must grow as the occluder moves away.");
        }

        [Test]
        public void MoreRaysConvergeTowardsTheSameEstimate()
        {
            BuildScene(withCeiling: true, ceilingHeight: 0.5f);
            Vector4[] positions = SamplePoints(Vector3.zero, Vector3.zero);

            float few = MeanVisibility(Trace(positions, radius: 4f, rays: 4), 0);
            float many = MeanVisibility(Trace(positions, radius: 4f, rays: 16), 0);

            Assert.AreEqual(few, many, 0.15f,
                "The estimator must be unbiased: raising the ray count refines the estimate instead of moving it.");
        }

        private void TearDownScene()
        {
            accelStruct?.Dispose();
            accelStruct = null;
            for (int i = 0; i < meshes.Count; i++)
            {
                if (meshes[i] != null)
                    Object.DestroyImmediate(meshes[i]);
            }
            meshes.Clear();
        }

        private void BuildScene(bool withCeiling, float ceilingHeight = 0.05f)
        {
            accelStruct = context.CreateAccelerationStructure();

            Mesh ground = Quad(60f);
            meshes.Add(ground);
            accelStruct.AddInstance(new MeshInstanceDesc(ground)
            {
                localToWorldMatrix = Matrix4x4.identity,
                mask = 0xff,
                enableTriangleCulling = false,
                opaqueGeometry = true
            });

            if (withCeiling)
            {
                Mesh ceiling = Quad(6f);
                meshes.Add(ceiling);
                accelStruct.AddInstance(new MeshInstanceDesc(ceiling)
                {
                    localToWorldMatrix = Matrix4x4.Translate(new Vector3(0f, ceilingHeight, 0f)),
                    mask = 0xff,
                    enableTriangleCulling = false,
                    opaqueGeometry = true
                });
            }

            CommandBuffer cmd = new CommandBuffer { name = "BasisRTAOTestBuild" };
            try
            {
                accelStruct.Build(cmd, context.GetBuildScratch(accelStruct));
                Graphics.ExecuteCommandBuffer(cmd);
            }
            finally
            {
                cmd.Release();
            }
        }

        private static Mesh Quad(float size)
        {
            float half = size * 0.5f;
            Mesh mesh = new Mesh { name = "BasisRTAOTestQuad", hideFlags = HideFlags.HideAndDontSave };
            mesh.SetVertices(new List<Vector3>
            {
                new Vector3(-half, 0f, -half),
                new Vector3(half, 0f, -half),
                new Vector3(half, 0f, half),
                new Vector3(-half, 0f, half)
            });
            mesh.SetTriangles(new[] { 0, 2, 1, 0, 3, 2 }, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            mesh.UploadMeshData(false);
            return mesh;
        }

        private static Vector4 EncodeNormal(Vector3 normal)
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
            return new Vector4(encoded.x, encoded.y, 0f, 0f);
        }

        private static Vector4[] SamplePoints(Vector3 sliceZeroCentre, Vector3 sliceOneCentre)
        {
            Vector4[] positions = new Vector4[Width * Height * Slices];
            for (int slice = 0; slice < Slices; slice++)
            {
                Vector3 centre = slice == 0 ? sliceZeroCentre : sliceOneCentre;
                for (int y = 0; y < Height; y++)
                {
                    for (int x = 0; x < Width; x++)
                    {
                        float offsetX = (x - (Width - 1) * 0.5f) * 0.05f;
                        float offsetZ = (y - (Height - 1) * 0.5f) * 0.05f;
                        Vector3 point = centre + new Vector3(offsetX, 0f, offsetZ);
                        positions[slice * Width * Height + y * Width + x] = new Vector4(point.x, point.y, point.z, 1f);
                    }
                }
            }
            return positions;
        }

        private Vector4[] Trace(Vector4[] positions, float radius, int rays)
        {
            Vector4[] normals = new Vector4[positions.Length];
            for (int i = 0; i < normals.Length; i++)
                normals[i] = EncodeNormal(Vector3.up);
            return Trace(positions, normals, radius, rays);
        }

        private Vector4[] Trace(Vector4[] positions, Vector4[] normals, float radius, int rays)
        {
            Texture2DArray positionTexture = harness.Track(new Texture2DArray(Width, Height, Slices, TextureFormat.RGBAFloat, false, true));
            Texture2DArray normalTexture = harness.Track(new Texture2DArray(Width, Height, Slices, TextureFormat.RGBAFloat, false, true));
            positionTexture.filterMode = FilterMode.Point;
            normalTexture.filterMode = FilterMode.Point;

            for (int slice = 0; slice < Slices; slice++)
            {
                Color[] positionPixels = new Color[Width * Height];
                Color[] normalPixels = new Color[Width * Height];
                for (int i = 0; i < positionPixels.Length; i++)
                {
                    Vector4 position = positions[slice * Width * Height + i];
                    Vector4 normal = normals[slice * Width * Height + i];
                    positionPixels[i] = new Color(position.x, position.y, position.z, position.w);
                    normalPixels[i] = new Color(normal.x, normal.y, normal.z, normal.w);
                }
                positionTexture.SetPixels(positionPixels, slice);
                normalTexture.SetPixels(normalPixels, slice);
            }
            positionTexture.Apply(false, false);
            normalTexture.Apply(false, false);

            RenderTexture result = harness.Track(new RenderTexture(new RenderTextureDescriptor(Width, Height, GraphicsFormat.R16G16_SFloat, GraphicsFormat.None, 0)
            {
                dimension = TextureDimension.Tex2DArray,
                volumeDepth = Slices,
                enableRandomWrite = true,
                msaaSamples = 1
            })
            {
                name = "BasisRTAOTestResult",
                filterMode = FilterMode.Point,
                hideFlags = HideFlags.HideAndDontSave
            });
            result.Create();

            CommandBuffer cmd = new CommandBuffer { name = "BasisRTAOTestTrace" };
            try
            {
                IRayTracingShader shader = context.TraceShader;
                shader.SetAccelerationStructure(cmd, BasisRTAOShaderIds.AccelStructName, accelStruct);
                shader.SetTextureParam(cmd, BasisRTAOShaderIds.PositionTex, positionTexture);
                shader.SetTextureParam(cmd, BasisRTAOShaderIds.NormalTex, normalTexture);
                shader.SetTextureParam(cmd, BasisRTAOShaderIds.ResultTex, result);
                shader.SetVectorParam(cmd, BasisRTAOShaderIds.Reference, Vector4.zero);
                shader.SetVectorParam(cmd, BasisRTAOShaderIds.Trace, new Vector4(rays, radius, 1f, 0f));
                shader.SetVectorParam(cmd, BasisRTAOShaderIds.Bias, new Vector4(0.002f, 0.0015f, 0.01f, 0f));
                shader.SetVectorParam(cmd, BasisRTAOShaderIds.Size, new Vector4(Width, Height, 1f / Width, 1f / Height));
                shader.SetIntParam(cmd, BasisRTAOShaderIds.RayCount, rays);
                shader.SetIntParam(cmd, BasisRTAOShaderIds.ViewCount, Slices);
                shader.SetIntParam(cmd, BasisRTAOShaderIds.FrameIndex, 3);
                shader.SetIntParam(cmd, BasisRTAOShaderIds.StereoCoherent, 1);
                shader.Dispatch(cmd, context.GetTraceScratch(Width, Height, Slices), Width, Height, Slices);
                Graphics.ExecuteCommandBuffer(cmd);
            }
            finally
            {
                cmd.Release();
            }

            return harness.ReadTextureArray(result, Width, Height, Slices);
        }

        private static float MeanVisibility(Vector4[] values, int slice)
        {
            float sum = 0f;
            int offset = slice * Width * Height;
            for (int i = 0; i < Width * Height; i++)
                sum += values[offset + i].x;
            return sum / (Width * Height);
        }

        private static float MeanDistance(Vector4[] values, int slice)
        {
            float sum = 0f;
            int offset = slice * Width * Height;
            for (int i = 0; i < Width * Height; i++)
                sum += values[offset + i].y;
            return sum / (Width * Height);
        }
    }
}
