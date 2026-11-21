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
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.Rendering;
namespace Basis.Tests.Sync
{
    [NoAutoStaticsCleanup]
    public class BasisRendererRayBatchTests
    {
        public const string ReportEnv = "BASIS_RAYBATCH_REPORT";
        static readonly StringBuilder report = new StringBuilder();
        static readonly List<UnityEngine.Object> made = new List<UnityEngine.Object>();
        bool previousSynchronous;
        public static IEnumerable<string> Rigs() => BasisFingerGraspTests.Rigs();
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
            string path = Environment.GetEnvironmentVariable(ReportEnv);
            if (!string.IsNullOrEmpty(path) && report.Length > 0)
            {
                File.WriteAllText(path, $"# burst={BurstCompiler.IsEnabled} workers={JobsUtility.JobWorkerCount}\n" + report);
            }
        }
        [TearDown]
        public void Cleanup()
        {
            for (int i = 0; i < made.Count; i++)
            {
                if (made[i] != null)
                {
                    UnityEngine.Object.DestroyImmediate(made[i]);
                }
            }
            made.Clear();
            BasisRendererRaycast.Invalidate();
        }
        public static NativeArray<BasisRendererRay> RaysFor(Bounds bounds, int count, int seed) => Rays(bounds, count, seed);
        public static Mesh DenseSphereMesh(int segments, int rings) => DenseSphere(segments, rings);
        static NativeArray<BasisRendererRay> Rays(Bounds bounds, int count, int seed, float reachScale = 4f)
        {
            System.Random random = new System.Random(seed);
            NativeArray<BasisRendererRay> rays = new NativeArray<BasisRendererRay>(count, Allocator.Persistent);
            float reach = bounds.extents.magnitude;
            for (int i = 0; i < count; i++)
            {
                Vector3 target = bounds.center + Vector3.Scale(bounds.extents, new Vector3((float)random.NextDouble() * 2f - 1f, (float)random.NextDouble() * 2f - 1f, (float)random.NextDouble() * 2f - 1f));
                Vector3 direction = new Vector3((float)random.NextDouble() * 2f - 1f, (float)random.NextDouble() * 2f - 1f, (float)random.NextDouble() * 2f - 1f).normalized;
                if (direction.sqrMagnitude < 0.5f)
                {
                    direction = Vector3.forward;
                }
                Vector3 origin = target - direction * reach * 2f;
                rays[i] = new BasisRendererRay { origin = origin, direction = direction, maxDistance = reach * reachScale };
            }
            return rays;
        }
        static Bounds Union(IReadOnlyList<Renderer> renderers)
        {
            Bounds b = renderers[0].bounds;
            for (int i = 1; i < renderers.Count; i++)
            {
                b.Encapsulate(renderers[i].bounds);
            }
            return b;
        }
        static List<SkinnedMeshRenderer> Skinned(BasisFingerTestRig rig)
        {
            List<SkinnedMeshRenderer> list = new List<SkinnedMeshRenderer>();
            foreach (SkinnedMeshRenderer s in rig.Animator.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (s.sharedMesh != null)
                {
                    list.Add(s);
                }
            }
            return list;
        }
        static Bounds BakedBounds(IReadOnlyList<SkinnedMeshRenderer> renderers)
        {
            Bounds b = default;
            bool any = false;
            Mesh baked = new Mesh();
            foreach (SkinnedMeshRenderer s in renderers)
            {
                s.BakeMesh(baked, true);
                Matrix4x4 m = s.transform.localToWorldMatrix;
                foreach (Vector3 v in baked.vertices)
                {
                    Vector3 w = m.MultiplyPoint3x4(v);
                    if (!any)
                    {
                        b = new Bounds(w, Vector3.zero);
                        any = true;
                    }
                    else
                    {
                        b.Encapsulate(w);
                    }
                }
            }
            UnityEngine.Object.DestroyImmediate(baked);
            return b;
        }
        [TestCaseSource(nameof(Rigs))]
        public void SkinnedVerticesMatchBakeMesh(string rigName)
        {
            using BasisFingerTestRig rig = BasisFingerTestRig.Build(rigName);
            List<SkinnedMeshRenderer> skinned = Skinned(rig);
            if (skinned.Count == 0)
            {
                Assert.Ignore("no skinned mesh");
            }
            int shaped = 0;
            foreach (SkinnedMeshRenderer s in skinned)
            {
                for (int k = 0; k < Mathf.Min(3, s.sharedMesh.blendShapeCount); k++)
                {
                    s.SetBlendShapeWeight(k, 30f + 35f * k);
                    shaped++;
                }
            }
            using BasisRendererRayBatch batch = new BasisRendererRayBatch();
            foreach (SkinnedMeshRenderer s in skinned)
            {
                Assert.IsTrue(batch.Add(s), s.name);
            }
            batch.Build().Complete();
            Mesh baked = new Mesh();
            made.Add(baked);
            float size = BakedBounds(skinned).size.magnitude, worst = 0f;
            for (int i = 0; i < batch.RendererCount; i++)
            {
                SkinnedMeshRenderer s = (SkinnedMeshRenderer)batch.GetRenderer(i);
                s.BakeMesh(baked, true);
                Matrix4x4 m = s.transform.localToWorldMatrix;
                Vector3[] expected = baked.vertices;
                NativeArray<float3> ours = batch.WorldVertices(i);
                Assert.AreEqual(expected.Length, ours.Length);
                for (int v = 0; v < expected.Length; v++)
                {
                    worst = Mathf.Max(worst, Vector3.Distance(m.MultiplyPoint3x4(expected[v]), ours[v]));
                }
            }
            string line = string.Format(CultureInfo.InvariantCulture, "vertices,{0},renderers={1},verts={2},shapesSet={3},maxError={4:E2},relative={5:E2}", Path.GetFileNameWithoutExtension(rigName), batch.RendererCount, batch.VertexCount, shaped, worst, worst / size);
            report.AppendLine(line);
            UnityEngine.Debug.Log(line);
            Assert.LessOrEqual(worst, 1e-4f * size, line);
        }
        [Test]
        public void SyntheticSkinWithBlendshapesMatchesBakeMesh()
        {
            GameObject root = new GameObject("synthetic skin"), b0 = new GameObject("b0"), b1 = new GameObject("b1"), cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            made.Add(root);
            made.Add(cube);
            b0.transform.SetParent(root.transform, false);
            b1.transform.SetParent(b0.transform, false);
            b1.transform.localPosition = new Vector3(0f, 0.5f, 0f);
            root.transform.SetPositionAndRotation(new Vector3(1f, 2f, 3f), Quaternion.Euler(10f, 30f, 0f));
            root.transform.localScale = new Vector3(0.5f, 0.5f, 0.5f);
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
            b1.transform.localRotation = Quaternion.Euler(0f, 0f, 40f);
            using BasisRendererRayBatch batch = new BasisRendererRayBatch();
            Assert.IsTrue(batch.Add(skinned));
            Mesh baked = new Mesh();
            made.Add(baked);
            float Error()
            {
                skinned.BakeMesh(baked, true);
                Vector3[] expected = baked.vertices;
                NativeArray<float3> ours = batch.WorldVertices(0);
                float worst = 0f;
                for (int i = 0; i < expected.Length; i++)
                {
                    worst = Mathf.Max(worst, Vector3.Distance(skinned.transform.localToWorldMatrix.MultiplyPoint3x4(expected[i]), ours[i]));
                }
                return worst;
            }
            batch.Build(default, false).Complete();
            float full = Error();
            skinned.SetBlendShapeWeight(0, 0f);
            skinned.SetBlendShapeWeight(1, 100f);
            b1.transform.localRotation = Quaternion.Euler(25f, 0f, -30f);
            batch.Build(default, true).Complete();
            Assert.IsTrue(batch.LastBuildWasRefit);
            float refit = Error();
            string line = string.Format(CultureInfo.InvariantCulture, "vertices,synthetic_blend_skin,full={0:E2},refitAfterNewWeights={1:E2}", full, refit);
            report.AppendLine(line);
            UnityEngine.Debug.Log(line);
            Assert.LessOrEqual(full, 1e-5f, line);
            Assert.LessOrEqual(refit, 1e-5f, line);
        }
        public const string CrowdAvatar = "Packages/com.basis.sdk/Animator/Animations/LocomotionAnimationsCharacter.fbx";
        static readonly HumanBodyBones[] crowdBones = { HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.Spine, HumanBodyBones.Head };
        static void Move(Animator animator, int frame)
        {
            for (int b = 0; b < crowdBones.Length; b++)
            {
                Transform t = animator.GetBoneTransform(crowdBones[b]);
                if (t != null)
                {
                    t.localRotation *= Quaternion.Euler(0.7f * ((frame + b) % 3 - 1), 0.4f, 0f);
                }
            }
        }
        [Test]
        public void CrowdScaling([Values(1, 5, 10, 20)] int avatars)
        {
            GameObject asset = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(CrowdAvatar);
            if (asset == null)
            {
                Assert.Ignore("crowd avatar missing");
            }
            List<Animator> crowd = new List<Animator>();
            List<SkinnedMeshRenderer> skinned = new List<SkinnedMeshRenderer>();
            for (int i = 0; i < avatars; i++)
            {
                GameObject instance = UnityEngine.Object.Instantiate(asset, new Vector3(1.5f * (i % 5), 0f, 1.5f * (i / 5)), Quaternion.identity);
                made.Add(instance);
                crowd.Add(instance.GetComponentInChildren<Animator>());
                skinned.AddRange(instance.GetComponentsInChildren<SkinnedMeshRenderer>());
            }
            List<Quaternion> pose = new List<Quaternion>();
            foreach (Animator a in crowd)
            {
                foreach (HumanBodyBones bone in crowdBones)
                {
                    pose.Add(a.GetBoneTransform(bone) != null ? a.GetBoneTransform(bone).localRotation : Quaternion.identity);
                }
            }
            void Restore()
            {
                int k = 0;
                foreach (Animator a in crowd)
                {
                    foreach (HumanBodyBones bone in crowdBones)
                    {
                        Transform t = a.GetBoneTransform(bone);
                        if (t != null)
                        {
                            t.localRotation = pose[k];
                        }
                        k++;
                    }
                }
            }
            if (skinned.Count == 0)
            {
                Assert.Ignore("no skinned mesh");
            }
            Bounds scene = BakedBounds(skinned), one = BakedBounds(crowd[0].GetComponentsInChildren<SkinnedMeshRenderer>());
            const int rayCount = 16384, reps = 7;
            using NativeArray<BasisRendererRay> sceneRays = Rays(scene, rayCount, 31), oneRays = Rays(one, rayCount, 37);
            using NativeArray<BasisRendererBatchHit> hits = new NativeArray<BasisRendererBatchHit>(rayCount, Allocator.Persistent), reference = new NativeArray<BasisRendererBatchHit>(rayCount, Allocator.Persistent);
            Stopwatch watch = new Stopwatch();
            int frame = 0;
            for (int mode = 0; mode < 2; mode++)
            {
                bool perRenderer = mode == 1;
                using BasisRendererRayBatch batch = new BasisRendererRayBatch { TreePerRenderer = perRenderer, RebuildInterval = 0 };
                foreach (SkinnedMeshRenderer s in skinned)
                {
                    Assert.IsTrue(batch.Add(s));
                }
                batch.Build(default, false).Complete();
                double Time(Action change, bool refit)
                {
                    List<double> samples = new List<double>();
                    for (int r = 0; r <= reps; r++)
                    {
                        change?.Invoke();
                        frame++;
                        watch.Restart();
                        batch.Build(default, refit).Complete();
                        watch.Stop();
                        if (r > 0)
                        {
                            samples.Add(watch.Elapsed.TotalMilliseconds);
                        }
                    }
                    return Median(samples);
                }
                double allMoving = Time(() => { foreach (Animator a in crowd) Move(a, frame); }, true);
                int changedAll = batch.ChangedRenderers;
                double oneMoving = Time(() => Move(crowd[0], frame), true);
                int changedOne = batch.ChangedRenderers;
                double idle = Time(null, true);
                int changedIdle = batch.ChangedRenderers;
                double full = Time(null, false);
                Restore();
                batch.Build(default, true).Complete();
                double RayTime(NativeArray<BasisRendererRay> rays)
                {
                    List<double> samples = new List<double>();
                    for (int r = 0; r <= reps; r++)
                    {
                        watch.Restart();
                        batch.Raycast(rays, hits).Complete();
                        watch.Stop();
                        if (r > 0)
                        {
                            samples.Add(watch.Elapsed.TotalMilliseconds);
                        }
                    }
                    return Median(samples);
                }
                double sceneMs = RayTime(sceneRays), oneMs = RayTime(oneRays);
                int differ = 0, hitCount = 0;
                if (!perRenderer)
                {
                    NativeArray<BasisRendererBatchHit>.Copy(hits, reference);
                }
                else
                {
                    for (int i = 0; i < rayCount; i++)
                    {
                        if (reference[i].hit)
                        {
                            hitCount++;
                        }
                        if (hits[i].hit != reference[i].hit || (hits[i].hit && (hits[i].distance != reference[i].distance || hits[i].renderer != reference[i].renderer)))
                        {
                            differ++;
                        }
                    }
                }
                string line = string.Format(CultureInfo.InvariantCulture, "crowd,avatars={0},mode={1},tris={2},trees={3},allMovingMs={4:F2},oneMovingMs={5:F2},idleMs={6:F2},fullRebuildMs={7:F2},sceneRaysMs={8:F2},oneAvatarRaysMs={9:F2},changed={10}/{11}/{12},differFromSingleTree={13},hits={14}", avatars, perRenderer ? "perRenderer" : "singleTree", batch.TriangleCount, batch.TreeCount, allMoving, oneMoving, idle, full, sceneMs, oneMs, changedAll, changedOne, changedIdle, perRenderer ? differ.ToString(CultureInfo.InvariantCulture) : "-", hitCount);
                report.AppendLine(line);
                UnityEngine.Debug.Log(line);
                if (perRenderer)
                {
                    Assert.AreEqual(skinned.Count, changedAll, line);
                    Assert.AreEqual(crowd[0].GetComponentsInChildren<SkinnedMeshRenderer>().Length, changedOne, line);
                    Assert.AreEqual(0, changedIdle, line);
                    Assert.AreEqual(0, differ, "both tree layouts must find the same surface");
                }
            }
        }
        static List<Animator> CrowdOf(int avatars, List<SkinnedMeshRenderer> skinned)
        {
            GameObject asset = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(CrowdAvatar);
            if (asset == null)
            {
                Assert.Ignore("crowd avatar missing");
            }
            List<Animator> crowd = new List<Animator>();
            for (int i = 0; i < avatars; i++)
            {
                GameObject instance = UnityEngine.Object.Instantiate(asset, new Vector3(2f * (i % 5), 0f, 2f * (i / 5)), Quaternion.identity);
                made.Add(instance);
                crowd.Add(instance.GetComponentInChildren<Animator>());
                skinned.AddRange(instance.GetComponentsInChildren<SkinnedMeshRenderer>());
            }
            return crowd;
        }
        static int Differ(NativeArray<BasisRendererBatchHit> a, NativeArray<BasisRendererBatchHit> b, out int hitCount)
        {
            int differ = 0;
            hitCount = 0;
            for (int i = 0; i < a.Length; i++)
            {
                if (b[i].hit)
                {
                    hitCount++;
                }
                if (a[i].hit != b[i].hit || (a[i].hit && (a[i].distance != b[i].distance || a[i].renderer != b[i].renderer)))
                {
                    differ++;
                }
            }
            return differ;
        }
        [Test]
        public void LazyBuildUpdatesOnlyAvatarsTheRaysReach()
        {
            List<SkinnedMeshRenderer> skinned = new List<SkinnedMeshRenderer>();
            List<Animator> crowd = CrowdOf(10, skinned);
            using BasisRendererRayBatch lazy = new BasisRendererRayBatch { RebuildInterval = 0 }, full = new BasisRendererRayBatch { RebuildInterval = 0 };
            foreach (SkinnedMeshRenderer s in skinned)
            {
                Assert.IsTrue(lazy.Add(s));
                Assert.IsTrue(full.Add(s));
            }
            lazy.Build(default, false).Complete();
            full.Build(default, false).Complete();
            foreach (Animator a in crowd)
            {
                a.GetBoneTransform(HumanBodyBones.LeftUpperArm).localRotation *= Quaternion.Euler(40f, 10f, -20f);
            }
            Bounds first = BakedBounds(crowd[0].GetComponentsInChildren<SkinnedMeshRenderer>()), third = BakedBounds(crowd[3].GetComponentsInChildren<SkinnedMeshRenderer>());
            using NativeArray<BasisRendererRay> atFirst = Rays(first, 4096, 61, 2.5f), atThird = Rays(third, 4096, 67, 2.5f);
            using NativeArray<BasisRendererBatchHit> a1 = new NativeArray<BasisRendererBatchHit>(4096, Allocator.Persistent), b1 = new NativeArray<BasisRendererBatchHit>(4096, Allocator.Persistent);
            lazy.Build(default, true, atFirst).Complete();
            int changed = lazy.ChangedRenderers, stale = lazy.StaleRenderers;
            full.Build(default, true).Complete();
            lazy.Raycast(atFirst, a1).Complete();
            full.Raycast(atFirst, b1).Complete();
            int differFirst = Differ(a1, b1, out int hitsFirst);
            lazy.Build(default, true, atThird).Complete();
            int staleAfter = lazy.StaleRenderers;
            lazy.Raycast(atThird, a1).Complete();
            full.Raycast(atThird, b1).Complete();
            int differThird = Differ(a1, b1, out int hitsThird);
            string line = string.Format(CultureInfo.InvariantCulture, "lazy,avatars=10,changedForFirst={0},staleAfterFirst={1},staleAfterThird={2},hitsFirst={3},differFirst={4},hitsThird={5},differThird={6}", changed, stale, staleAfter, hitsFirst, differFirst, hitsThird, differThird);
            report.AppendLine(line);
            UnityEngine.Debug.Log(line);
            Assert.Less(changed, skinned.Count, line);
            Assert.Greater(stale, 0, line);
            Assert.Less(staleAfter, stale, line);
            Assert.Greater(hitsFirst, 100, line);
            Assert.AreEqual(0, differFirst, line);
            Assert.AreEqual(0, differThird, line);
        }
        [Test]
        public void MaskLimitsWhichRenderersAreHit()
        {
            GameObject near = GameObject.CreatePrimitive(PrimitiveType.Sphere), far = GameObject.CreatePrimitive(PrimitiveType.Cube);
            made.Add(near);
            made.Add(far);
            far.transform.position = new Vector3(0f, 0f, 3f);
            foreach (bool perRenderer in new[] { true, false })
            {
                using BasisRendererRayBatch batch = new BasisRendererRayBatch { TreePerRenderer = perRenderer };
                Assert.IsTrue(batch.Add(near.GetComponent<Renderer>()));
                Assert.IsTrue(batch.Add(far.GetComponent<Renderer>()));
                batch.Build().Complete();
                using NativeArray<BasisRendererRay> rays = new NativeArray<BasisRendererRay>(new[] { new BasisRendererRay { origin = new float3(0f, 0f, -5f), direction = new float3(0f, 0f, 1f), maxDistance = 20f } }, Allocator.Persistent);
                using NativeArray<BasisRendererBatchHit> hits = new NativeArray<BasisRendererBatchHit>(1, Allocator.Persistent);
                using NativeArray<ulong> mask = new NativeArray<ulong>(batch.MaskLength, Allocator.Persistent);
                batch.Raycast(rays, hits).Complete();
                Assert.AreEqual(0, hits[0].renderer, "unmasked the sphere is in front");
                batch.FillMask(mask, new[] { far.GetComponent<Renderer>() });
                batch.Raycast(rays, hits, mask).Complete();
                Assert.IsTrue(hits[0].hit);
                Assert.AreEqual(1, hits[0].renderer, $"masked to the cube, perRenderer={perRenderer}");
                Assert.AreEqual(7.5f, hits[0].distance, 1e-4f);
                batch.FillMask(mask, new Renderer[0]);
                batch.Raycast(rays, hits, mask).Complete();
                Assert.IsFalse(hits[0].hit);
            }
        }
        [Test]
        public void WorldSharesRenderersAndMeshes()
        {
            GameObject a = GameObject.CreatePrimitive(PrimitiveType.Sphere), b = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            made.Add(a);
            made.Add(b);
            int meshesBefore = BasisRendererRayBatch.SharedMeshCount;
            Renderer ra = a.GetComponent<Renderer>(), rb = b.GetComponent<Renderer>();
            Assert.IsTrue(BasisRendererRayWorld.Acquire(ra));
            Assert.IsTrue(BasisRendererRayWorld.Acquire(ra));
            Assert.IsTrue(BasisRendererRayWorld.Acquire(rb));
            Assert.AreEqual(2, BasisRendererRayWorld.Batch.RendererCount, "the same renderer acquired twice is stored once");
            Assert.AreEqual(meshesBefore + 1, BasisRendererRayBatch.SharedMeshCount, "two spheres share one mesh copy");
            using (BasisRendererRayBatch other = new BasisRendererRayBatch())
            {
                Assert.IsTrue(other.Add(ra));
                Assert.AreEqual(meshesBefore + 1, BasisRendererRayBatch.SharedMeshCount, "another batch shares it too");
            }
            BasisRendererRayWorld.Release(ra);
            Assert.AreEqual(1, BasisRendererRayWorld.References(ra));
            BasisRendererRayWorld.Release(ra);
            BasisRendererRayWorld.Release(rb);
            Assert.IsNull(BasisRendererRayWorld.Batch, "the world frees itself when nothing is left");
            Assert.AreEqual(meshesBefore, BasisRendererRayBatch.SharedMeshCount);
        }
        [Test]
        public void ReportsWhichRendererWasHit()
        {
            GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere), cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            made.Add(sphere);
            made.Add(cube);
            sphere.transform.position = new Vector3(0f, 0f, 0f);
            cube.transform.position = new Vector3(3f, 0f, 0f);
            cube.transform.localScale = new Vector3(1f, 2f, 1f);
            using BasisRendererRayBatch batch = new BasisRendererRayBatch();
            Assert.IsTrue(batch.Add(sphere.GetComponent<Renderer>()));
            Assert.IsTrue(batch.Add(cube.GetComponent<Renderer>()));
            batch.Build();
            NativeArray<BasisRendererRay> rays = new NativeArray<BasisRendererRay>(4, Allocator.Persistent);
            NativeArray<BasisRendererBatchHit> hits = new NativeArray<BasisRendererBatchHit>(4, Allocator.Persistent);
            try
            {
                rays[0] = new BasisRendererRay { origin = new float3(0f, 0f, -5f), direction = new float3(0f, 0f, 1f), maxDistance = 10f };
                rays[1] = new BasisRendererRay { origin = new float3(3f, 0.9f, -5f), direction = new float3(0f, 0f, 1f), maxDistance = 10f };
                rays[2] = new BasisRendererRay { origin = new float3(-5f, 0f, 0f), direction = new float3(1f, 0f, 0f), maxDistance = 10f };
                rays[3] = new BasisRendererRay { origin = new float3(0f, 5f, -5f), direction = new float3(0f, 0f, 1f), maxDistance = 10f };
                batch.Raycast(rays, hits).Complete();
                Assert.IsTrue(hits[0].hit);
                Assert.AreEqual(0, hits[0].renderer);
                Assert.AreEqual(4.5f, hits[0].distance, 0.02f);
                Assert.Greater(math.dot(hits[0].normal, new float3(0f, 0f, -1f)), 0.95f);
                Assert.IsTrue(hits[1].hit);
                Assert.AreEqual(1, hits[1].renderer);
                Assert.AreEqual(4.5f, hits[1].distance, 1e-4f);
                Assert.IsTrue(hits[2].hit);
                Assert.AreEqual(0, hits[2].renderer, "the sphere is in front of the cube on this ray");
                Assert.AreEqual(4.5f, hits[2].distance, 0.02f);
                Assert.IsFalse(hits[3].hit);
                sphere.transform.position = new Vector3(0f, 0f, 2f);
                batch.Build();
                batch.Raycast(rays, hits).Complete();
                Assert.AreEqual(6.5f, hits[0].distance, 0.02f, "a rebuild picks up moved renderers");
            }
            finally
            {
                rays.Dispose();
                hits.Dispose();
            }
        }
        [TestCaseSource(nameof(Rigs))]
        public void RefitMatchesFullRebuildAfterPosing(string rigName)
        {
            using BasisFingerTestRig rig = BasisFingerTestRig.Build(rigName);
            List<SkinnedMeshRenderer> skinned = Skinned(rig);
            if (skinned.Count == 0)
            {
                Assert.Ignore("no skinned mesh");
            }
            using BasisRendererRayBatch refitted = new BasisRendererRayBatch(), rebuilt = new BasisRendererRayBatch();
            foreach (SkinnedMeshRenderer s in skinned)
            {
                Assert.IsTrue(refitted.Add(s));
                Assert.IsTrue(rebuilt.Add(s));
            }
            refitted.Build();
            foreach (HumanBodyBones bone in new[] { HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm, HumanBodyBones.Spine, HumanBodyBones.LeftUpperLeg })
            {
                Transform t = rig.Animator.GetBoneTransform(bone);
                if (t != null)
                {
                    t.localRotation *= Quaternion.Euler(35f, 20f, -25f);
                }
            }
            refitted.Build(default, true).Complete();
            Assert.IsTrue(refitted.LastBuildWasRefit);
            rebuilt.Build(default, false).Complete();
            Bounds bounds = BakedBounds(skinned);
            using NativeArray<BasisRendererRay> rays = Rays(bounds, 8192, 23);
            using NativeArray<BasisRendererBatchHit> a = new NativeArray<BasisRendererBatchHit>(rays.Length, Allocator.Persistent), b = new NativeArray<BasisRendererBatchHit>(rays.Length, Allocator.Persistent);
            refitted.Raycast(rays, a).Complete();
            rebuilt.Raycast(rays, b).Complete();
            int hitCount = 0, differ = 0;
            for (int i = 0; i < rays.Length; i++)
            {
                if (b[i].hit)
                {
                    hitCount++;
                }
                if (a[i].hit != b[i].hit || (a[i].hit && math.abs(a[i].distance - b[i].distance) > 1e-5f * bounds.size.magnitude))
                {
                    differ++;
                }
            }
            Assert.Greater(hitCount, rays.Length / 20);
            Assert.AreEqual(0, differ, "a refit must find the same surface as a full rebuild");
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
            made.Add(mesh);
            return mesh;
        }
        public static IEnumerable<string> BenchmarkCases()
        {
            foreach (string rig in Rigs())
            {
                yield return rig;
            }
            yield return "dense_sphere_65k";
            yield return "dense_sphere_262k";
        }
        static double Median(List<double> values)
        {
            values.Sort();
            return values[values.Count / 2];
        }
        [TestCaseSource(nameof(BenchmarkCases))]
        public void Benchmark(string name)
        {
            BasisFingerTestRig rig = null;
            List<Renderer> renderers = new List<Renderer>();
            Bounds bounds;
            try
            {
                if (name.StartsWith("dense_sphere"))
                {
                    GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    made.Add(go);
                    go.GetComponent<MeshFilter>().sharedMesh = name.EndsWith("65k") ? DenseSphere(256, 128) : DenseSphere(512, 256);
                    renderers.Add(go.GetComponent<Renderer>());
                    bounds = renderers[0].bounds;
                }
                else
                {
                    rig = BasisFingerTestRig.Build(name);
                    List<SkinnedMeshRenderer> skinned = Skinned(rig);
                    if (skinned.Count == 0)
                    {
                        Assert.Ignore("no skinned mesh");
                    }
                    renderers.AddRange(skinned);
                    bounds = BakedBounds(skinned);
                }
                const int rayCount = 16384, reps = 5;
                using NativeArray<BasisRendererRay> rays = Rays(bounds, rayCount, 11);
                using NativeArray<BasisRendererBatchHit> hits = new NativeArray<BasisRendererBatchHit>(rayCount, Allocator.Persistent);
                using NativeArray<BasisRendererBatchHit> single = new NativeArray<BasisRendererBatchHit>(rayCount, Allocator.Persistent);
                BasisMeshGraspSurface managed = new BasisMeshGraspSurface();
                List<double> managedBuild = new List<double>(), batchBuild = new List<double>(), managedRays = new List<double>(), oneThread = new List<double>(), parallel = new List<double>();
                Stopwatch watch = new Stopwatch();
                for (int r = 0; r <= reps; r++)
                {
                    watch.Restart();
                    managed.Clear();
                    foreach (Renderer renderer in renderers)
                    {
                        Assert.IsTrue(managed.Add(renderer, Vector3.zero, 0f));
                    }
                    managed.Raycast(rays[0].origin, math.normalize(rays[0].direction), rays[0].maxDistance, out _, out _);
                    watch.Stop();
                    if (r > 0)
                    {
                        managedBuild.Add(watch.Elapsed.TotalMilliseconds);
                    }
                }
                using BasisRendererRayBatch batch = new BasisRendererRayBatch();
                foreach (Renderer renderer in renderers)
                {
                    Assert.IsTrue(batch.Add(renderer));
                }
                for (int r = 0; r <= reps; r++)
                {
                    watch.Restart();
                    batch.Build(default, false).Complete();
                    watch.Stop();
                    if (r > 0)
                    {
                        batchBuild.Add(watch.Elapsed.TotalMilliseconds);
                    }
                }
                List<double> batchRefit = new List<double>();
                Transform mover = rig != null ? rig.Animator.GetBoneTransform(HumanBodyBones.LeftUpperArm) : renderers[0].transform;
                mover.GetLocalPositionAndRotation(out Vector3 moverPosition, out Quaternion moverRotation);
                for (int r = 0; r <= reps; r++)
                {
                    if (rig != null)
                    {
                        mover.localRotation *= Quaternion.Euler(0.5f, 0f, 0f);
                    }
                    else
                    {
                        mover.position += new Vector3(1e-4f, 0f, 0f);
                    }
                    watch.Restart();
                    batch.Build(default, true).Complete();
                    watch.Stop();
                    Assert.IsTrue(batch.LastBuildWasRefit);
                    if (r > 0)
                    {
                        batchRefit.Add(watch.Elapsed.TotalMilliseconds);
                    }
                }
                mover.SetLocalPositionAndRotation(moverPosition, moverRotation);
                batch.Build(default, true).Complete();
                bool[] managedHit = new bool[rayCount];
                float[] managedDistance = new float[rayCount];
                for (int r = 0; r < 3; r++)
                {
                    watch.Restart();
                    for (int i = 0; i < rayCount; i++)
                    {
                        managedHit[i] = managed.Raycast(rays[i].origin, math.normalize(rays[i].direction), rays[i].maxDistance, out managedDistance[i], out _);
                    }
                    watch.Stop();
                    managedRays.Add(watch.Elapsed.TotalMilliseconds);
                }
                for (int r = 0; r <= reps; r++)
                {
                    watch.Restart();
                    batch.RaycastOnThisThread(rays, single);
                    watch.Stop();
                    if (r > 0)
                    {
                        oneThread.Add(watch.Elapsed.TotalMilliseconds);
                    }
                }
                for (int r = 0; r <= reps; r++)
                {
                    watch.Restart();
                    batch.Raycast(rays, hits).Complete();
                    watch.Stop();
                    if (r > 0)
                    {
                        parallel.Add(watch.Elapsed.TotalMilliseconds);
                    }
                }
                float tolerance = 1e-3f * bounds.size.magnitude;
                int mismatches = 0, hitCount = 0, threadMismatches = 0;
                for (int i = 0; i < rayCount; i++)
                {
                    if (hits[i].hit)
                    {
                        hitCount++;
                    }
                    if (hits[i].hit != single[i].hit || (hits[i].hit && hits[i].distance != single[i].distance))
                    {
                        threadMismatches++;
                    }
                    if (hits[i].hit != managedHit[i] || (managedHit[i] && Mathf.Abs(hits[i].distance - managedDistance[i]) > tolerance))
                    {
                        mismatches++;
                    }
                }
                double mr = Median(managedRays), ot = Median(oneThread), pa = Median(parallel), mb = Median(managedBuild), bb = Median(batchBuild);
                string line = string.Format(CultureInfo.InvariantCulture, "bench,{0},tris={1},nodes={2},rays={3},hits={4},managedBuildMs={5:F2},batchBuildMs={6:F2},batchRefitMs={14:F2},managedRaysMs={7:F2},burstOneThreadMs={8:F2},burstParallelMs={9:F2},speedupOneThread={10:F1},speedupParallel={11:F1},mismatches={12},workers={13}", Path.GetFileNameWithoutExtension(name), batch.TriangleCount, batch.NodeCount, rayCount, hitCount, mb, bb, mr, ot, pa, mr / ot, mr / pa, mismatches, JobsUtility.JobWorkerCount, Median(batchRefit));
                report.AppendLine(line);
                UnityEngine.Debug.Log(line);
                Assert.AreEqual(0, threadMismatches, "parallel and single-thread runs must agree exactly");
                Assert.LessOrEqual(mismatches, rayCount / 100, line);
                Assert.Greater(hitCount, rayCount / 20, line);
                Assert.Less(pa, mr, line);
            }
            finally
            {
                rig?.Dispose();
            }
        }
    }
}
