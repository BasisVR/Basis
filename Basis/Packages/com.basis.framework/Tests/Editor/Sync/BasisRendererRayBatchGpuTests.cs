using Basis.Scripts.BasisSdk.Interactions;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.Rendering;
namespace Basis.Tests.Sync
{
    [NoAutoStaticsCleanup]
    public class BasisRendererRayBatchGpuTests
    {
        public const string UserShader = "Packages/com.basis.framework/Tests/Editor/Sync/BasisRendererRaysUserTest.compute";
        static readonly StringBuilder report = new StringBuilder();
        readonly List<UnityEngine.Object> made = new List<UnityEngine.Object>();
        bool previousSynchronous;
        [OneTimeSetUp]
        public void Setup()
        {
            previousSynchronous = BurstCompiler.Options.EnableBurstCompileSynchronously;
            BurstCompiler.Options.EnableBurstCompileSynchronously = true;
        }
        [OneTimeTearDown]
        public void Teardown()
        {
            BurstCompiler.Options.EnableBurstCompileSynchronously = previousSynchronous;
            string path = Environment.GetEnvironmentVariable(BasisRendererRayBatchTests.ReportEnv);
            if (!string.IsNullOrEmpty(path) && report.Length > 0)
            {
                File.WriteAllText(path + ".gpu", $"# device={SystemInfo.graphicsDeviceName} api={SystemInfo.graphicsDeviceType}\n" + report);
            }
        }
        [SetUp]
        public void RequireCompute()
        {
            if (!BasisRendererRayBatchGpu.Supported)
            {
                Assert.Ignore("compute shaders unsupported on " + SystemInfo.graphicsDeviceType);
            }
        }
        [TearDown]
        public void Cleanup()
        {
            for (int i = made.Count - 1; i >= 0; i--)
            {
                if (made[i] != null)
                {
                    UnityEngine.Object.DestroyImmediate(made[i]);
                }
            }
            made.Clear();
        }
        List<SkinnedMeshRenderer> Crowd(int count, List<Animator> animators = null)
        {
            GameObject asset = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(BasisRendererRayBatchTests.CrowdAvatar);
            if (asset == null)
            {
                Assert.Ignore("crowd avatar missing");
            }
            List<SkinnedMeshRenderer> skinned = new List<SkinnedMeshRenderer>();
            for (int i = 0; i < count; i++)
            {
                GameObject instance = UnityEngine.Object.Instantiate(asset, new Vector3(1.5f * (i % 5), 0f, 1.5f * (i / 5)), Quaternion.identity);
                made.Add(instance);
                animators?.Add(instance.GetComponentInChildren<Animator>());
                skinned.AddRange(instance.GetComponentsInChildren<SkinnedMeshRenderer>());
            }
            return skinned;
        }
        SkinnedMeshRenderer SyntheticSkin()
        {
            GameObject root = new GameObject("synthetic skin"), b0 = new GameObject("b0"), b1 = new GameObject("b1"), cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            made.Add(root);
            made.Add(cube);
            b0.transform.SetParent(root.transform, false);
            b1.transform.SetParent(b0.transform, false);
            b1.transform.localPosition = new Vector3(0f, 0.5f, 0f);
            root.transform.SetPositionAndRotation(new Vector3(1f, 2f, 3f), Quaternion.Euler(10f, 30f, 0f));
            root.transform.localScale = Vector3.one * 0.5f;
            Mesh mesh = UnityEngine.Object.Instantiate(cube.GetComponent<MeshFilter>().sharedMesh);
            made.Add(mesh);
            Vector3[] vertices = mesh.vertices;
            BoneWeight[] weights = new BoneWeight[vertices.Length];
            Vector3[] bulge = new Vector3[vertices.Length], pinch = new Vector3[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                weights[i] = vertices[i].y > 0f ? new BoneWeight { boneIndex0 = 1, weight0 = 1f } : new BoneWeight { boneIndex0 = 0, weight0 = 0.6f, boneIndex1 = 1, weight1 = 0.4f };
                bulge[i] = vertices[i].normalized * 0.3f;
                if (vertices[i].x > 0f)
                {
                    pinch[i] = new Vector3(-0.2f, 0f, 0.1f);
                }
            }
            mesh.boneWeights = weights;
            mesh.AddBlendShapeFrame("bulge", 100f, bulge, null, null);
            mesh.AddBlendShapeFrame("pinch", 100f, pinch, null, null);
            SkinnedMeshRenderer skinned = root.AddComponent<SkinnedMeshRenderer>();
            skinned.bones = new[] { b0.transform, b1.transform };
            skinned.rootBone = b0.transform;
            mesh.bindposes = new[] { b0.transform.worldToLocalMatrix * root.transform.localToWorldMatrix, b1.transform.worldToLocalMatrix * root.transform.localToWorldMatrix };
            skinned.sharedMesh = mesh;
            skinned.SetBlendShapeWeight(0, 60f);
            skinned.SetBlendShapeWeight(1, 35f);
            b1.transform.localRotation = Quaternion.Euler(0f, 0f, 40f);
            return skinned;
        }
        static float VertexError(BasisRendererRayBatchGpu gpu, BasisRendererRayBatch cpu)
        {
            using NativeArray<float3> ours = new NativeArray<float3>(gpu.VertexCount, Allocator.Persistent);
            gpu.ReadWorldVertices(ours);
            float worst = 0f;
            for (int i = 0; i < cpu.RendererCount; i++)
            {
                NativeArray<float3> expected = cpu.WorldVertices(i);
                int offset = 0;
                for (int k = 0; k < i; k++)
                {
                    offset += cpu.WorldVertices(k).Length;
                }
                for (int v = 0; v < expected.Length; v++)
                {
                    worst = math.max(worst, math.distance(expected[v], ours[offset + v]));
                }
            }
            return worst;
        }
        [Test]
        public void GpuSkinningMatchesCpu()
        {
            List<SkinnedMeshRenderer> renderers = Crowd(2);
            renderers.Add(SyntheticSkin());
            using BasisRendererRayBatchGpu gpu = new BasisRendererRayBatchGpu();
            using BasisRendererRayBatch cpu = new BasisRendererRayBatch();
            foreach (SkinnedMeshRenderer s in renderers)
            {
                Assert.IsTrue(gpu.Add(s));
                Assert.IsTrue(cpu.Add(s));
            }
            gpu.Update();
            cpu.Build(default, false).Complete();
            float first = VertexError(gpu, cpu);
            SkinnedMeshRenderer synthetic = renderers[renderers.Count - 1];
            synthetic.SetBlendShapeWeight(0, 10f);
            synthetic.bones[1].localRotation = Quaternion.Euler(20f, 10f, -30f);
            renderers[0].transform.root.GetComponentInChildren<Animator>().GetBoneTransform(HumanBodyBones.LeftUpperArm).localRotation *= Quaternion.Euler(30f, 0f, 20f);
            gpu.Update();
            cpu.Build(default, false).Complete();
            float second = VertexError(gpu, cpu);
            string line = string.Format(CultureInfo.InvariantCulture, "gpuVertices,first={0:E2},afterPoseAndWeights={1:E2},verts={2}", first, second, gpu.VertexCount);
            report.AppendLine(line);
            UnityEngine.Debug.Log(line);
            Assert.LessOrEqual(first, 1e-4f, line);
            Assert.LessOrEqual(second, 1e-4f, line);
        }
        static NativeArray<BasisRendererGpuRay> ToGpu(NativeArray<BasisRendererRay> rays)
        {
            NativeArray<BasisRendererGpuRay> gpu = new NativeArray<BasisRendererGpuRay>(rays.Length, Allocator.Persistent);
            for (int i = 0; i < rays.Length; i++)
            {
                gpu[i] = new BasisRendererGpuRay { origin = rays[i].origin, direction = rays[i].direction, maxDistance = rays[i].maxDistance };
            }
            return gpu;
        }
        static int Compare(NativeArray<BasisRendererBatchHit> cpu, NativeArray<BasisRendererGpuHit> gpu, float tolerance, out int hitCount)
        {
            int differ = 0;
            hitCount = 0;
            for (int i = 0; i < cpu.Length; i++)
            {
                if (cpu[i].hit)
                {
                    hitCount++;
                }
                bool g = gpu[i].hit != 0;
                if (g != cpu[i].hit || (g && math.abs(gpu[i].distance - cpu[i].distance) > tolerance))
                {
                    differ++;
                }
            }
            return differ;
        }
        [Test]
        public void GpuHitsMatchCpu([Values(1, 10)] int avatars)
        {
            List<Animator> animators = new List<Animator>();
            List<SkinnedMeshRenderer> renderers = Crowd(avatars, animators);
            using BasisRendererRayBatchGpu gpu = new BasisRendererRayBatchGpu();
            using BasisRendererRayBatch cpu = new BasisRendererRayBatch();
            foreach (SkinnedMeshRenderer s in renderers)
            {
                Assert.IsTrue(gpu.Add(s));
                Assert.IsTrue(cpu.Add(s));
            }
            gpu.Update();
            cpu.Build(default, false).Complete();
            cpu.Build().Complete();
            Bounds bounds = renderers[0].bounds;
            foreach (SkinnedMeshRenderer s in renderers)
            {
                bounds.Encapsulate(s.bounds);
            }
            using NativeArray<BasisRendererRay> rays = BasisRendererRayBatchTests.RaysFor(bounds, 16384, 41);
            using NativeArray<BasisRendererGpuRay> gpuRays = ToGpu(rays);
            using NativeArray<BasisRendererBatchHit> cpuHits = new NativeArray<BasisRendererBatchHit>(rays.Length, Allocator.Persistent);
            using NativeArray<BasisRendererGpuHit> gpuHits = new NativeArray<BasisRendererGpuHit>(rays.Length, Allocator.Persistent);
            cpu.Raycast(rays, cpuHits).Complete();
            gpu.RaycastImmediate(gpuRays, gpuHits);
            float tolerance = 1e-4f * bounds.size.magnitude;
            int differ = Compare(cpuHits, gpuHits, tolerance, out int hitCount);
            foreach (Animator a in animators)
            {
                a.GetBoneTransform(HumanBodyBones.RightUpperArm).localRotation *= Quaternion.Euler(-35f, 15f, 25f);
            }
            gpu.Update();
            cpu.Build(default, false).Complete();
            cpu.Raycast(rays, cpuHits).Complete();
            gpu.RaycastImmediate(gpuRays, gpuHits);
            int differPosed = Compare(cpuHits, gpuHits, tolerance, out int hitPosed);
            string line = string.Format(CultureInfo.InvariantCulture, "gpuParity,avatars={0},rays={1},hits={2},differ={3},hitsAfterPosing={4},differAfterPosing={5}", avatars, rays.Length, hitCount, differ, hitPosed, differPosed);
            report.AppendLine(line);
            UnityEngine.Debug.Log(line);
            Assert.Greater(hitCount, rays.Length / 20, line);
            Assert.LessOrEqual(differ, rays.Length / 500, line);
            Assert.LessOrEqual(differPosed, rays.Length / 500, line);
        }
        [Test]
        public void UserComputeShaderCanTrace()
        {
            ComputeShader user = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(UserShader);
            Assert.IsNotNull(user, UserShader);
            GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            made.Add(sphere);
            using BasisRendererRayBatchGpu gpu = new BasisRendererRayBatchGpu();
            Assert.IsTrue(gpu.Add(sphere.GetComponent<Renderer>()));
            gpu.Update();
            const int count = 256;
            Vector4[] origins = new Vector4[count], directions = new Vector4[count];
            for (int i = 0; i < count; i++)
            {
                Vector3 dir = UnityEngine.Random.onUnitSphere;
                origins[i] = new Vector4(dir.x * 3f, dir.y * 3f, dir.z * 3f, 10f);
                directions[i] = -dir;
            }
            origins[0] = new Vector4(0f, 5f, -5f, 10f);
            directions[0] = Vector3.forward;
            using GraphicsBuffer o = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 16), d = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 16), result = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 4);
            o.SetData(origins);
            d.SetData(directions);
            int kernel = user.FindKernel("UserTrace");
            gpu.Bind(user, kernel);
            user.SetBuffer(kernel, "_UserOrigins", o);
            user.SetBuffer(kernel, "_UserDirections", d);
            user.SetBuffer(kernel, "_UserDistances", result);
            user.SetInt("_UserCount", count);
            user.Dispatch(kernel, (count + 63) / 64, 1, 1);
            float[] distances = new float[count];
            result.GetData(distances);
            Assert.AreEqual(-1f, distances[0], "a ray past the sphere misses");
            for (int i = 1; i < count; i++)
            {
                Assert.AreEqual(2.5f, distances[i], 0.02f, $"ray {i}");
            }
        }
        [Test]
        public void Throughput([Values("dense_sphere_262k", "crowd_20")] string scene)
        {
            List<Renderer> renderers = new List<Renderer>();
            List<Animator> animators = new List<Animator>();
            if (scene == "crowd_20")
            {
                renderers.AddRange(Crowd(20, animators));
            }
            else
            {
                GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                made.Add(go);
                Mesh dense = BasisRendererRayBatchTests.DenseSphereMesh(512, 256);
                made.Add(dense);
                go.GetComponent<MeshFilter>().sharedMesh = dense;
                renderers.Add(go.GetComponent<Renderer>());
            }
            Bounds bounds = renderers[0].bounds;
            foreach (Renderer r in renderers)
            {
                bounds.Encapsulate(r.bounds);
            }
            using BasisRendererRayBatchGpu gpu = new BasisRendererRayBatchGpu();
            using BasisRendererRayBatch cpu = new BasisRendererRayBatch();
            foreach (Renderer r in renderers)
            {
                Assert.IsTrue(gpu.Add(r));
                Assert.IsTrue(cpu.Add(r));
            }
            gpu.Update();
            cpu.Build(default, false).Complete();
            Stopwatch watch = new Stopwatch();
            double Median(Action action, int reps = 9)
            {
                action();
                List<double> samples = new List<double>();
                for (int r = 0; r < reps; r++)
                {
                    watch.Restart();
                    action();
                    watch.Stop();
                    samples.Add(watch.Elapsed.TotalMilliseconds);
                }
                samples.Sort();
                return samples[samples.Count / 2];
            }
            int frame = 0;
            double gpuUpdate = Median(() =>
            {
                foreach (Animator a in animators)
                {
                    a.GetBoneTransform(HumanBodyBones.LeftUpperArm).localRotation *= Quaternion.Euler(0.5f * ((frame++ % 3) - 1), 0.3f, 0f);
                }
                if (animators.Count == 0)
                {
                    renderers[0].transform.position += new Vector3(1e-5f, 0f, 0f);
                }
                gpu.Update();
                using NativeArray<float3> one = new NativeArray<float3>(1, Allocator.Persistent);
                gpu.ReadWorldVertices(one);
            });
            double cpuUpdate = Median(() =>
            {
                foreach (Animator a in animators)
                {
                    a.GetBoneTransform(HumanBodyBones.LeftUpperArm).localRotation *= Quaternion.Euler(0.5f * ((frame++ % 3) - 1), 0.3f, 0f);
                }
                if (animators.Count == 0)
                {
                    renderers[0].transform.position += new Vector3(1e-5f, 0f, 0f);
                }
                cpu.Build().Complete();
            });
            gpu.Update();
            foreach (int rayCount in new[] { 16384, 262144, 1048576 })
            {
                using NativeArray<BasisRendererRay> rays = BasisRendererRayBatchTests.RaysFor(bounds, rayCount, 53);
                using NativeArray<BasisRendererGpuRay> gpuRays = ToGpu(rays);
                using NativeArray<BasisRendererBatchHit> cpuHits = new NativeArray<BasisRendererBatchHit>(rayCount, Allocator.Persistent);
                using NativeArray<BasisRendererGpuHit> gpuHits = new NativeArray<BasisRendererGpuHit>(rayCount, Allocator.Persistent);
                using GraphicsBuffer rayBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, rayCount, BasisRendererRayBatchGpu.RayStride), hitBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, rayCount, BasisRendererRayBatchGpu.HitStride);
                rayBuffer.SetData(gpuRays);
                double cpuMs = Median(() => cpu.Raycast(rays, cpuHits).Complete());
                double gpuOnlyPerDispatch;
                double gpuRoundTrip = Median(() => gpu.RaycastImmediate(gpuRays, gpuHits));
                double gpuOnly = Median(() =>
                {
                    using CommandBuffer cmd = new CommandBuffer();
                    for (int k = 0; k < 20; k++)
                    {
                        gpu.Raycast(cmd, rayBuffer, hitBuffer, rayCount);
                    }
                    Graphics.ExecuteCommandBuffer(cmd);
                    using NativeArray<BasisRendererGpuHit> last = new NativeArray<BasisRendererGpuHit>(1, Allocator.Persistent);
                    NativeArray<BasisRendererGpuHit> target = last;
                    AsyncGPUReadback.RequestIntoNativeArray(ref target, hitBuffer, BasisRendererRayBatchGpu.HitStride, (rayCount - 1) * BasisRendererRayBatchGpu.HitStride).WaitForCompletion();
                });
                gpuOnlyPerDispatch = gpuOnly / 20.0;
                int differ = Compare(cpuHits, gpuHits, 1e-4f * bounds.size.magnitude, out int hitCount);
                string line = string.Format(CultureInfo.InvariantCulture, "gpuBench,{0},tris={1},rays={2},hits={3},cpuParallelMs={4:F2},gpuWithUploadAndReadbackMs={5:F2},gpuOnlyMs={6:F2},cpuUpdateMs={7:F2},gpuUpdateMs={8:F2},differ={9},gpuMB={10:F1},geometryMB={11:F1},uniqueMeshes={12},nearestFirst={13},dxc={14}", scene, gpu.TriangleCount, rayCount, hitCount, cpuMs, gpuRoundTrip, gpuOnlyPerDispatch, cpuUpdate, gpuUpdate, differ, gpu.GpuBytes / 1048576.0, gpu.GeometryBytes / 1048576.0, gpu.UniqueMeshes, gpu.NearestFirstTraversal, gpu.CompiledWithDxc);
                report.AppendLine(line);
                UnityEngine.Debug.Log(line);
                Assert.LessOrEqual(differ, rayCount / 500, line);
            }
        }
    }
}
