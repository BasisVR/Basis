using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using Basis.Shims;
using Cilbox;
using NUnit.Framework;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.Rendering;
namespace Basis.Tests.Sync
{
    [NoAutoStaticsCleanup]
    public sealed class BasisRendererRayShimTests
    {
        static readonly Type Rays = typeof(BasisRendererRayShim).Assembly.GetType("Cilbox.CilboxRendererRays", true);
        static int BatchCount => (int)Rays.GetProperty("Count", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        static void Tick() => Rays.GetMethod("Tick", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
        readonly List<UnityEngine.Object> made = new List<UnityEngine.Object>();
        [SetUp]
        public void SetUp() => Assume.That(BatchCount, Is.EqualTo(0), "a batch leaked from another test");
        [TearDown]
        public void TearDown()
        {
            for (int i = made.Count - 1; i >= 0; i--)
            {
                if (made[i] != null)
                {
                    UnityEngine.Object.DestroyImmediate(made[i]);
                }
            }
            made.Clear();
            Tick();
            Assert.AreEqual(0, BatchCount, "batches must be freed once their content is gone");
        }
        CilboxProxy Script(CilboxBasisCommon shared = null)
        {
            CilboxBasisCommon box = shared;
            if (box == null)
            {
                GameObject host = new GameObject("ray shim test");
                made.Add(host);
                box = host.AddComponent<CilboxSceneBasis>();
                box.classes = new Dictionary<string, int>();
                box.interpreterAccountingDepth = 1;
            }
            CilboxProxy proxy = new GameObject("Script").AddComponent<CilboxProxy>();
            proxy.transform.SetParent(box.transform);
            proxy.box = box;
            return proxy;
        }
        Renderer Primitive(PrimitiveType type, Vector3 position, Vector3 scale)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            made.Add(go);
            go.transform.position = position;
            go.transform.localScale = scale;
            return go.GetComponent<Renderer>();
        }
        [Test]
        public void Refused_WithoutInterpreterCaller()
        {
            Assert.AreEqual(0, BasisRendererRayShim.Create(null));
            Assert.AreEqual(0, BasisRendererRayShim.Create(new object()));
            CilboxProxy idle = Script();
            idle.box.interpreterAccountingDepth = 0;
            Assert.AreEqual(0, BasisRendererRayShim.Create(idle));
            Assert.AreEqual(0, BatchCount);
        }
        [Test]
        public void ABatch_BelongsToItsContent()
        {
            CilboxProxy owner = Script(), sibling = Script(owner.box as CilboxBasisCommon), other = Script();
            int batch = BasisRendererRayShim.Create(owner);
            Assert.Greater(batch, 0);
            Renderer sphere = Primitive(PrimitiveType.Sphere, Vector3.zero, Vector3.one);
            Assert.IsFalse(BasisRendererRayShim.Add(other, batch, sphere), "other content must not use the batch");
            Assert.IsTrue(BasisRendererRayShim.Add(sibling, batch, sphere), "scripts of the same content share it");
            float[] distances = new float[1];
            Assert.AreEqual(0, BasisRendererRayShim.Raycast(other, batch, new[] { new Vector3(0f, 0f, -5f) }, new[] { Vector3.forward }, 1, 10f, distances, null, null, null));
            BasisRendererRayShim.Release(other, batch);
            Assert.AreEqual(1, BatchCount, "other content must not release it");
            Assert.AreEqual(1, BasisRendererRayShim.Raycast(owner, batch, new[] { new Vector3(0f, 0f, -5f) }, new[] { Vector3.forward }, 1, 10f, distances, null, null, null));
            BasisRendererRayShim.Release(owner, batch);
            Assert.AreEqual(0, BatchCount);
        }
        [Test]
        public void Caps_BatchesPerContent()
        {
            CilboxProxy owner = Script();
            for (int i = 0; i < BasisRendererRayShim.MaxBatchesPerContent; i++)
            {
                Assert.Greater(BasisRendererRayShim.Create(owner), 0);
            }
            Assert.AreEqual(0, BasisRendererRayShim.Create(owner));
            Assert.Greater(BasisRendererRayShim.Create(Script()), 0, "the cap is per content");
        }
        [Test]
        public void Raycast_FillsManagedArrays()
        {
            CilboxProxy owner = Script();
            int batch = BasisRendererRayShim.Create(owner);
            Renderer sphere = Primitive(PrimitiveType.Sphere, Vector3.zero, Vector3.one), cube = Primitive(PrimitiveType.Cube, new Vector3(3f, 0f, 0f), new Vector3(1f, 2f, 1f));
            Assert.IsTrue(BasisRendererRayShim.Add(owner, batch, sphere));
            Assert.IsTrue(BasisRendererRayShim.Add(owner, batch, cube));
            Assert.AreEqual(2, BasisRendererRayShim.RendererCount(owner, batch));
            Assert.AreEqual(780, BasisRendererRayShim.TriangleCount(owner, batch));
            Vector3[] origins = { new Vector3(0f, 0f, -5f), new Vector3(3f, 0.9f, -5f), new Vector3(0f, 5f, -5f), Vector3.zero };
            Vector3[] directions = { Vector3.forward, Vector3.forward * 3f, Vector3.forward, Vector3.zero };
            float[] distances = new float[4];
            Vector3[] points = new Vector3[4], normals = new Vector3[4];
            int[] renderers = new int[4];
            Assert.AreEqual(2, BasisRendererRayShim.Raycast(owner, batch, origins, directions, 4, 10f, distances, points, normals, renderers));
            Assert.AreEqual(4.5f, distances[0], 0.02f);
            Assert.AreSame(sphere, BasisRendererRayShim.GetRenderer(owner, batch, renderers[0]));
            Assert.Greater(Vector3.Dot(normals[0], Vector3.back), 0.95f);
            Assert.AreEqual(4.5f, distances[1], 1e-4f);
            Assert.AreSame(cube, BasisRendererRayShim.GetRenderer(owner, batch, renderers[1]));
            Assert.AreEqual(new Vector3(3f, 0.9f, -0.5f).ToString("F3"), points[1].ToString("F3"));
            Assert.AreEqual(-1f, distances[2]);
            Assert.AreEqual(-1, renderers[2]);
            Assert.AreEqual(-1f, distances[3], "a zero direction is a miss");
            Assert.AreEqual(1, BasisRendererRayShim.Raycast(owner, batch, origins, directions, 99, 10f, new float[1], null, null, null), "count clamps to the shortest array");
            sphere.transform.position = new Vector3(0f, 0f, 2f);
            Assert.IsTrue(BasisRendererRayShim.Rebuild(owner, batch));
            BasisRendererRayShim.Raycast(owner, batch, origins, directions, 1, 10f, distances, null, null, null);
            Assert.AreEqual(6.5f, distances[0], 0.02f, "a rebuild picks up moved renderers");
            UnityEngine.Object.DestroyImmediate(cube.gameObject);
            Assert.IsTrue(BasisRendererRayShim.Rebuild(owner, batch));
            Assert.AreEqual(1, BasisRendererRayShim.RendererCount(owner, batch), "destroyed renderers drop out on rebuild");
        }
        [Test]
        public void ABatch_IsFreed_WhenItsScriptGoesAway()
        {
            CilboxProxy owner = Script();
            Assert.Greater(BasisRendererRayShim.Create(owner), 0);
            Tick();
            Assert.AreEqual(1, BatchCount);
            UnityEngine.Object.DestroyImmediate(owner.gameObject);
            Tick();
            Assert.AreEqual(0, BatchCount);
        }
        CilboxProxy AvatarScript()
        {
            GameObject host = new GameObject("ray shim avatar test");
            made.Add(host);
            CilboxBasisCommon box = host.AddComponent<CilboxAvatarBasis>();
            box.classes = new Dictionary<string, int>();
            box.interpreterAccountingDepth = 1;
            return Script(box);
        }
        [Test]
        public void Gpu_RaycastsIntoGraphicsBuffers()
        {
            if (!BasisRendererRayGpuShim.IsSupported)
            {
                Assert.Ignore("no compute");
            }
            CilboxProxy owner = Script(), other = Script();
            int batch = BasisRendererRayGpuShim.Create(owner);
            Assert.Greater(batch, 0);
            Renderer sphere = Primitive(PrimitiveType.Sphere, Vector3.zero, Vector3.one), cube = Primitive(PrimitiveType.Cube, new Vector3(3f, 0f, 0f), new Vector3(1f, 2f, 1f));
            Assert.IsTrue(BasisRendererRayGpuShim.Add(owner, batch, sphere));
            Assert.IsTrue(BasisRendererRayGpuShim.Add(owner, batch, cube));
            Assert.IsFalse(BasisRendererRayGpuShim.Update(other, batch), "other content must not drive the batch");
            Assert.IsTrue(BasisRendererRayGpuShim.Update(owner, batch));
            Basis.Scripts.BasisSdk.Interactions.BasisRendererGpuRay[] rays =
            {
                new Basis.Scripts.BasisSdk.Interactions.BasisRendererGpuRay { origin = new Unity.Mathematics.float3(0f, 0f, -5f), direction = new Unity.Mathematics.float3(0f, 0f, 1f), maxDistance = 10f },
                new Basis.Scripts.BasisSdk.Interactions.BasisRendererGpuRay { origin = new Unity.Mathematics.float3(3f, 0.9f, -5f), direction = new Unity.Mathematics.float3(0f, 0f, 1f), maxDistance = 10f },
                new Basis.Scripts.BasisSdk.Interactions.BasisRendererGpuRay { origin = new Unity.Mathematics.float3(0f, 5f, -5f), direction = new Unity.Mathematics.float3(0f, 0f, 1f), maxDistance = 10f },
            };
            using GraphicsBuffer rayBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, rays.Length, BasisRendererRayGpuShim.RayStride), hitBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, rays.Length, BasisRendererRayGpuShim.HitStride), wrong = new GraphicsBuffer(GraphicsBuffer.Target.Structured, rays.Length, 16);
            rayBuffer.SetData(rays);
            Assert.IsFalse(BasisRendererRayGpuShim.Raycast(owner, batch, rayBuffer, wrong, rays.Length), "a hit buffer with the wrong stride is refused");
            Assert.IsFalse(BasisRendererRayGpuShim.Raycast(other, batch, rayBuffer, hitBuffer, rays.Length));
            Assert.IsTrue(BasisRendererRayGpuShim.Raycast(owner, batch, rayBuffer, hitBuffer, rays.Length));
            Basis.Scripts.BasisSdk.Interactions.BasisRendererGpuHit[] hits = new Basis.Scripts.BasisSdk.Interactions.BasisRendererGpuHit[rays.Length];
            hitBuffer.GetData(hits);
            Assert.AreEqual(1, hits[0].hit);
            Assert.AreEqual(4.5f, hits[0].distance, 0.02f);
            Assert.AreSame(sphere, BasisRendererRayGpuShim.GetRenderer(owner, batch, hits[0].renderer));
            Assert.AreEqual(1, hits[1].hit);
            Assert.AreSame(cube, BasisRendererRayGpuShim.GetRenderer(owner, batch, hits[1].renderer));
            Assert.AreEqual(0, hits[2].hit);
            Assert.AreEqual(-1f, hits[2].distance);
        }
        [Test]
        public void Gpu_GeometryIsChargedToTheContentBudget()
        {
            if (!BasisRendererRayGpuShim.IsSupported)
            {
                Assert.Ignore("no compute");
            }
            Mesh dense = DenseSphere(1024, 512);
            made.Add(dense);
            Renderer big = Primitive(PrimitiveType.Sphere, Vector3.zero, Vector3.one);
            big.GetComponent<MeshFilter>().sharedMesh = dense;
            CilboxProxy avatar = AvatarScript();
            int small = BasisRendererRayGpuShim.Create(avatar);
            Assert.IsTrue(BasisRendererRayGpuShim.Add(avatar, small, big));
            Assert.IsFalse(BasisRendererRayGpuShim.Update(avatar, small), "a 1M triangle tree does not fit an avatar's 16 MB compute budget");
            CilboxProxy world = Script();
            int large = BasisRendererRayGpuShim.Create(world);
            Assert.IsTrue(BasisRendererRayGpuShim.Add(world, large, big));
            Assert.IsTrue(BasisRendererRayGpuShim.Update(world, large), "it fits a world's 256 MB");
            Assert.Greater(BasisComputeShim.RemainingBytes(avatar), 0);
            Assert.Less(BasisComputeShim.RemainingBytes(world), BasisComputeShim.WorldBytes);
            UnityEngine.Object.DestroyImmediate(world.gameObject);
        }
        static Mesh DenseSphere(int segments, int rings)
        {
            Mesh mesh = new Mesh { indexFormat = IndexFormat.UInt32 };
            Vector3[] vertices = new Vector3[(segments + 1) * (rings + 1)];
            for (int r = 0; r <= rings; r++)
            {
                float phi = Mathf.PI * r / rings;
                for (int s = 0; s <= segments; s++)
                {
                    float theta = 2f * Mathf.PI * s / segments;
                    vertices[r * (segments + 1) + s] = new Vector3(Mathf.Sin(phi) * Mathf.Cos(theta), Mathf.Cos(phi), Mathf.Sin(phi) * Mathf.Sin(theta)) * 0.5f;
                }
            }
            int[] indices = new int[segments * rings * 6];
            int k = 0;
            for (int r = 0; r < rings; r++)
            {
                for (int s = 0; s < segments; s++)
                {
                    int a = r * (segments + 1) + s, b = a + segments + 1;
                    indices[k++] = a;
                    indices[k++] = b;
                    indices[k++] = a + 1;
                    indices[k++] = a + 1;
                    indices[k++] = b;
                    indices[k++] = b + 1;
                }
            }
            mesh.vertices = vertices;
            mesh.triangles = indices;
            mesh.RecalculateBounds();
            return mesh;
        }
        [Test]
        public void Throughput_BatchedVersusOneRayPerCall()
        {
            CilboxProxy owner = Script();
            int batch = BasisRendererRayShim.Create(owner);
            Renderer sphere = Primitive(PrimitiveType.Sphere, Vector3.zero, Vector3.one);
            Mesh dense = DenseSphere(256, 128);
            made.Add(dense);
            sphere.GetComponent<MeshFilter>().sharedMesh = dense;
            Assert.IsTrue(BasisRendererRayShim.Add(owner, batch, sphere));
            const int count = 16384;
            System.Random random = new System.Random(5);
            Vector3[] origins = new Vector3[count], directions = new Vector3[count], points = new Vector3[count], normals = new Vector3[count];
            float[] distances = new float[count];
            int[] renderers = new int[count];
            for (int i = 0; i < count; i++)
            {
                Vector3 target = new Vector3((float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f) * 0.8f;
                directions[i] = UnityEngine.Random.onUnitSphere;
                origins[i] = target - directions[i] * 2f;
            }
            BasisRendererRayShim.Raycast(owner, batch, origins, directions, count, 5f, distances, points, normals, renderers);
            Stopwatch watch = Stopwatch.StartNew();
            int hits = 0;
            const int reps = 5;
            for (int r = 0; r < reps; r++)
            {
                hits = BasisRendererRayShim.Raycast(owner, batch, origins, directions, count, 5f, distances, points, normals, renderers);
            }
            watch.Stop();
            double batched = watch.Elapsed.TotalMilliseconds / reps;
            int single = 0;
            watch.Restart();
            for (int i = 0; i < count; i++)
            {
                if (BasisPoseOverrideShim.RaycastRenderer(owner, sphere, origins[i], directions[i], 5f, out Vector3 point, out _))
                {
                    single++;
                }
            }
            watch.Stop();
            double perCall = watch.Elapsed.TotalMilliseconds;
            int disagree = 0;
            for (int i = 0; i < count; i++)
            {
                if ((distances[i] >= 0f) != BasisPoseOverrideShim.RaycastRenderer(owner, sphere, origins[i], directions[i], 5f, out _, out _))
                {
                    disagree++;
                }
            }
            string line = string.Format(CultureInfo.InvariantCulture, "shim,dense_sphere_65k,rays={0},hits={1},batchedMs={2:F2},oneRayPerCallMs={3:F2},speedup={4:F1},disagree={5}", count, hits, batched, perCall, perCall / batched, disagree);
            UnityEngine.Debug.Log(line);
            string path = Environment.GetEnvironmentVariable("BASIS_RAYBATCH_REPORT");
            if (!string.IsNullOrEmpty(path))
            {
                File.AppendAllText(path + ".shim", line + "\n");
            }
            Assert.LessOrEqual(disagree, count / 1000);
            Assert.Less(batched, perCall);
        }
    }
}
