using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.Drivers;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
namespace Basis.Tests.Sync
{
    [NoAutoStaticsCleanup]
    public class BasisFingerGraspTests
    {
        public const string ReportEnv = "BASIS_GRASP_REPORT";
        static readonly StringBuilder report = new StringBuilder();
        static readonly List<GameObject> made = new List<GameObject>();
        static readonly string[] extraRigs = { "Packages/com.basis.sdk/Animator/Animations/LocomotionAnimationsCharacter.fbx", "Packages/com.basis.examples/Seats/seats.fbx", "Packages/com.unity.3rdpersondemo/HumanoidIdleJumpUp.fbx" };
        public static IEnumerable<string> Rigs()
        {
            HashSet<string> seen = new HashSet<string>();
            foreach (string name in BasisFingerTestRig.AnatomicalRigs())
            {
                if (seen.Add(name))
                {
                    yield return name;
                }
            }
            foreach (string name in extraRigs)
            {
                if (!seen.Contains(name) && HasFingers(name))
                {
                    yield return name;
                }
            }
        }
        static bool HasFingers(string path)
        {
            GameObject asset = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (asset == null)
            {
                return false;
            }
            GameObject instance = UnityEngine.Object.Instantiate(asset);
            try
            {
                Animator animator = instance.GetComponentInChildren<Animator>();
                if (animator == null || !animator.isHuman)
                {
                    return false;
                }
                for (int i = 0; i < 30; i++)
                {
                    if (animator.GetBoneTransform((HumanBodyBones)((int)HumanBodyBones.LeftThumbProximal + i)) == null)
                    {
                        return false;
                    }
                }
                return true;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }
        public static readonly string[] Shapes = { "handle_thin", "handle_mid", "handle_thick", "ball_small", "ball_mid", "ball_large", "box", "table", "cylinder_convex", "cylinder_concave" };
        struct HandFrame
        {
            public Vector3 wrist, forward, palm, across;
            public Quaternion rotation, wristRotation;
            public float length;
        }
        public struct CaseResult
        {
            public int fingersTouching, nonThumbTouching, touchingSegments, singleTouchingSegments, wrapped, contactFingers, worstFinger;
            public float meanGap, maxGap, maxPenetration, milliseconds, scale;
        }
        [TearDown]
        public void Cleanup()
        {
            for (int i = 0; i < batches.Count; i++)
            {
                batches[i].Dispose();
            }
            batches.Clear();
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
        [OneTimeTearDown]
        public void WriteReport()
        {
            string path = Environment.GetEnvironmentVariable(ReportEnv);
            if (!string.IsNullOrEmpty(path) && report.Length > 0)
            {
                File.WriteAllText(path, "rig,shape,backend,fingers,nonThumb,segmentsPerJoint,segmentsSingleCurl,wrappedFingers,meanGapMm,maxGapMm,maxPenetrationMm,ms\n" + report);
            }
        }
        static HandFrame Frame(BasisFingerTestRig rig, bool left)
        {
            Transform wrist = left ? rig.LeftHand : rig.RightHand;
            int f0 = left ? 0 : 5;
            Vector3 middle = rig.Joints[(f0 + 2) * 3].position, index = rig.Joints[(f0 + 1) * 3].position, little = rig.Joints[(f0 + 4) * 3].position;
            HandFrame frame = new HandFrame { wrist = wrist.position, wristRotation = wrist.rotation, length = (middle - wrist.position).magnitude };
            frame.forward = (middle - wrist.position).normalized;
            Vector3 curlDirection = Vector3.zero;
            Vector3[] open = new Vector3[4], closed = new Vector3[4];
            for (int f = f0 + 1; f < f0 + 5; f++)
            {
                Assert.IsTrue(BasisFingerGraspSolver.BuildChain(wrist, rig.Joints[f * 3], rig.Joints[f * 3 + 1], rig.Joints[f * 3 + 2], out BasisFingerChain chain));
                BasisFingerGraspSolver.Pose(chain, rig.Grid, f, wrist.position, wrist.rotation, BasisFingerGraspSolver.OpenCurl, BasisFingerGraspSolver.OpenCurl, BasisFingerGraspSolver.OpenCurl, 0f, open);
                BasisFingerGraspSolver.Pose(chain, rig.Grid, f, wrist.position, wrist.rotation, -0.3f, -0.3f, -0.3f, 0f, closed);
                curlDirection += closed[3] - open[3];
            }
            frame.palm = Vector3.ProjectOnPlane(curlDirection, frame.forward).normalized;
            frame.across = Vector3.Cross(frame.palm, frame.forward).normalized;
            if (Vector3.Dot(frame.across, index - little) < 0f)
            {
                frame.across = -frame.across;
            }
            frame.rotation = Quaternion.LookRotation(frame.forward, frame.across);
            return frame;
        }
        static GameObject Primitive(PrimitiveType type, Vector3 position, Quaternion rotation, Vector3 scale)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            go.hideFlags = HideFlags.DontSave;
            go.transform.SetPositionAndRotation(position, rotation);
            go.transform.localScale = scale;
            made.Add(go);
            return go;
        }
        static GameObject Shape(string shape, HandFrame h)
        {
            float l = h.length;
            Vector3 handleCenter(float r) => h.wrist + h.forward * (0.75f * l) + h.palm * (r + 0.15f * l);
            Quaternion along = Quaternion.LookRotation(h.forward, h.across), flat = Quaternion.LookRotation(h.forward, h.palm);
            switch (shape)
            {
                case "handle_thin": return Primitive(PrimitiveType.Capsule, handleCenter(0.18f * l), along, new Vector3(0.36f * l, 2f * l, 0.36f * l));
                case "handle_mid": return Primitive(PrimitiveType.Capsule, handleCenter(0.28f * l), along, new Vector3(0.56f * l, 2f * l, 0.56f * l));
                case "handle_thick": return Primitive(PrimitiveType.Capsule, handleCenter(0.4f * l), along, new Vector3(0.8f * l, 2f * l, 0.8f * l));
                case "ball_small": return Ball(h, 0.35f * l);
                case "ball_mid": return Ball(h, 0.5f * l);
                case "ball_large": return Ball(h, 0.7f * l);
                case "box": return Primitive(PrimitiveType.Cube, h.wrist + h.forward * (0.8f * l) + h.palm * (0.6f * l), along, new Vector3(0.9f * l, 0.9f * l, 0.9f * l));
                case "table": return Primitive(PrimitiveType.Cube, h.wrist + h.palm * (0.35f * l), flat, new Vector3(6f * l, 0.2f * l, 6f * l));
                case "cylinder_convex":
                case "cylinder_concave":
                    GameObject go = Primitive(PrimitiveType.Cylinder, handleCenter(0.28f * l), along, new Vector3(0.56f * l, 2f * l, 0.56f * l));
                    UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
                    MeshCollider mc = go.AddComponent<MeshCollider>();
                    mc.sharedMesh = go.GetComponent<MeshFilter>().sharedMesh;
                    mc.convex = shape == "cylinder_convex";
                    return go;
            }
            throw new ArgumentException(shape);
        }
        static GameObject Ball(HandFrame h, float r) => Primitive(PrimitiveType.Sphere, h.wrist + h.forward * (0.8f * h.length) + h.palm * (r + 0.15f * h.length), Quaternion.identity, Vector3.one * (2f * r));
        static BasisGraspSurface Surface(GameObject go, bool renderers, HandFrame h)
        {
            if (!renderers)
            {
                BasisColliderGraspSurface colliders = new BasisColliderGraspSurface();
                colliders.Set(go);
                return colliders;
            }
            BasisMeshGraspSurface mesh = new BasisMeshGraspSurface();
            Assert.IsTrue(mesh.Add(go.GetComponent<Renderer>(), h.wrist, 3f * h.length));
            return mesh;
        }
        static float SingleCurl(BasisFingerTestRig rig, int finger, BasisFingerChain chain, HandFrame h, BasisGraspSurface surface, Vector3[] points)
        {
            float[] baseDepth = new float[3];
            BasisFingerGraspSolver.Pose(chain, rig.Grid, finger, h.wrist, h.wristRotation, BasisFingerGraspSolver.OpenCurl, BasisFingerGraspSolver.OpenCurl, BasisFingerGraspSolver.OpenCurl, 0f, points);
            for (int k = 0; k < 3; k++)
            {
                baseDepth[k] = Mathf.Max(0f, surface.Depth(points[k], points[k + 1], BasisFingerGraspSolver.SegmentRadius(chain, k)));
            }
            float skin = chain.radius * 0.03f;
            bool Hit(float c)
            {
                BasisFingerGraspSolver.Pose(chain, rig.Grid, finger, h.wrist, h.wristRotation, c, c, c, 0f, points);
                for (int k = 0; k < 3; k++)
                {
                    if (surface.Depth(points[k], points[k + 1], BasisFingerGraspSolver.SegmentRadius(chain, k)) > baseDepth[k] + skin)
                    {
                        return true;
                    }
                }
                return false;
            }
            float free = BasisFingerGraspSolver.OpenCurl, hit = float.NaN;
            for (int s = 1; s <= 32; s++)
            {
                float v = Mathf.Lerp(BasisFingerGraspSolver.OpenCurl, BasisFingerGraspSolver.ClosedCurl, (float)s / 32);
                if (Hit(v))
                {
                    hit = v;
                    break;
                }
                free = v;
            }
            if (float.IsNaN(hit))
            {
                return BasisFingerGraspSolver.ClosedCurl;
            }
            for (int b = 0; b < BasisFingerGraspSolver.BisectSteps; b++)
            {
                float mid = (free + hit) * 0.5f;
                if (Hit(mid))
                {
                    hit = mid;
                }
                else
                {
                    free = mid;
                }
            }
            return free;
        }
        static float Gap(BasisMeshGraspSurface metric, Vector3 a, Vector3 b, float radius)
        {
            float depth = metric.Depth(a, b, radius * 4f);
            if (depth == 0f)
            {
                return float.PositiveInfinity;
            }
            return 3f * radius - depth;
        }
        public static CaseResult Measure(BasisFingerTestRig rig, bool left, string shape, bool renderers, out string line) => Measure(rig, left, shape, renderers ? 1 : 0, out line);
        static readonly List<BasisRendererRayBatch> batches = new List<BasisRendererRayBatch>();
        public static CaseResult Measure(BasisFingerTestRig rig, bool left, string shape, int backend, out string line)
        {
            HandFrame h = Frame(rig, left);
            GameObject go = Shape(shape, h);
            Physics.SyncTransforms();
            BasisGraspSurface surface;
            if (backend == 2)
            {
                BasisRendererRayBatch batch = new BasisRendererRayBatch();
                batches.Add(batch);
                Assert.IsTrue(batch.Add(go.GetComponent<Renderer>()));
                batch.Build().Complete();
                BasisBatchGraspSurface batchSurface = new BasisBatchGraspSurface();
                batchSurface.Set(batch);
                surface = batchSurface;
            }
            else
            {
                surface = Surface(go, backend == 1, h);
            }
            BasisMeshGraspSurface metric = new BasisMeshGraspSurface();
            Assert.IsTrue(metric.Add(go.GetComponent<Renderer>(), Vector3.zero, 0f));
            Transform wrist = left ? rig.LeftHand : rig.RightHand;
            float[] curls = new float[30];
            int[] contacts = new int[10];
            BasisFingerGraspSolver.SolveHand(rig.Grid, left, wrist, rig.Joints, h.wrist, h.wristRotation, surface, null, curls, contacts);
            Stopwatch watch = Stopwatch.StartNew();
            const int reps = 5;
            for (int r = 0; r < reps; r++)
            {
                BasisFingerGraspSolver.SolveHand(rig.Grid, left, wrist, rig.Joints, h.wrist, h.wristRotation, surface, null, curls, contacts);
            }
            watch.Stop();
            CaseResult result = new CaseResult { milliseconds = (float)watch.Elapsed.TotalMilliseconds / reps, scale = h.length / 0.09f, maxPenetration = 0f, maxGap = 0f };
            float tolerance = 0.0015f * result.scale, gapSum = 0f;
            Vector3[] points = new Vector3[4], open = new Vector3[4];
            int f0 = left ? 0 : 5;
            for (int f = f0; f < f0 + 5; f++)
            {
                Assert.IsTrue(BasisFingerGraspSolver.BuildChain(wrist, rig.Joints[f * 3], rig.Joints[f * 3 + 1], rig.Joints[f * 3 + 2], out BasisFingerChain chain));
                BasisFingerGraspSolver.Pose(chain, rig.Grid, f, h.wrist, h.wristRotation, BasisFingerGraspSolver.OpenCurl, BasisFingerGraspSolver.OpenCurl, BasisFingerGraspSolver.OpenCurl, 0f, open);
                BasisFingerGraspSolver.Pose(chain, rig.Grid, f, h.wrist, h.wristRotation, curls[f * 3], curls[f * 3 + 1], curls[f * 3 + 2], 0f, points);
                float best = float.PositiveInfinity;
                int touching = 0;
                for (int k = 0; k < 3; k++)
                {
                    float radius = BasisFingerGraspSolver.SegmentRadius(chain, k), gap = Gap(metric, points[k], points[k + 1], radius), openGap = Gap(metric, open[k], open[k + 1], radius);
                    float penetration = Mathf.Max(0f, -gap) - Mathf.Max(0f, -openGap);
                    if (penetration > result.maxPenetration)
                    {
                        result.maxPenetration = penetration;
                        result.worstFinger = f - f0;
                    }
                    if (gap <= tolerance)
                    {
                        touching++;
                    }
                    best = Mathf.Min(best, gap);
                }
                result.touchingSegments += touching;
                if (contacts[f] > 0)
                {
                    result.contactFingers++;
                    if (best <= tolerance)
                    {
                        result.fingersTouching++;
                        if (f != f0)
                        {
                            result.nonThumbTouching++;
                        }
                    }
                    if (!float.IsInfinity(best))
                    {
                        gapSum += Mathf.Max(0f, best);
                        result.maxGap = Mathf.Max(result.maxGap, Mathf.Max(0f, best));
                    }
                    if (Mathf.Abs(curls[f * 3] - curls[f * 3 + 2]) > 0.05f)
                    {
                        result.wrapped++;
                    }
                }
                surface.Begin();
                float single = SingleCurl(rig, f, chain, h, surface, points);
                surface.End();
                BasisFingerGraspSolver.Pose(chain, rig.Grid, f, h.wrist, h.wristRotation, single, single, single, 0f, points);
                for (int k = 0; k < 3; k++)
                {
                    if (Gap(metric, points[k], points[k + 1], BasisFingerGraspSolver.SegmentRadius(chain, k)) <= tolerance)
                    {
                        result.singleTouchingSegments++;
                    }
                }
            }
            result.meanGap = result.contactFingers > 0 ? gapSum / result.contactFingers : 0f;
            line = string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3},{4},{5},{6},{7},{8:F2},{9:F2},{10:F2},{11:F3}", Path.GetFileNameWithoutExtension(rig.Name), shape, backend == 2 ? "batch" : backend == 1 ? "renderers" : "colliders", result.fingersTouching, result.nonThumbTouching, result.touchingSegments, result.singleTouchingSegments, result.wrapped, result.meanGap / result.scale * 1000f, result.maxGap / result.scale * 1000f, result.maxPenetration / result.scale * 1000f, result.milliseconds);
            return result;
        }
        [Test]
        public void BatchSurfaceMatchesMeshSurface()
        {
            GameObject sphere = Primitive(PrimitiveType.Sphere, new Vector3(0.3f, 1f, -0.2f), Quaternion.Euler(0f, 30f, 10f), Vector3.one * 0.4f);
            GameObject box = Primitive(PrimitiveType.Cube, new Vector3(-0.4f, 1f, 0.3f), Quaternion.Euler(20f, 10f, 5f), new Vector3(0.3f, 0.5f, 0.2f));
            GameObject asset = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(BasisRendererRayBatchTests.CrowdAvatar);
            List<Renderer> renderers = new List<Renderer> { sphere.GetComponent<Renderer>(), box.GetComponent<Renderer>() };
            if (asset != null)
            {
                GameObject avatar = UnityEngine.Object.Instantiate(asset, new Vector3(2f, 0f, 0f), Quaternion.identity);
                made.Add(avatar);
                avatar.GetComponentInChildren<Animator>().GetBoneTransform(HumanBodyBones.LeftUpperArm).localRotation *= Quaternion.Euler(30f, 0f, 40f);
                renderers.AddRange(avatar.GetComponentsInChildren<SkinnedMeshRenderer>());
            }
            BasisMeshGraspSurface mesh = new BasisMeshGraspSurface();
            using BasisRendererRayBatch batch = new BasisRendererRayBatch();
            foreach (Renderer r in renderers)
            {
                Assert.IsTrue(mesh.Add(r, Vector3.zero, 0f));
                Assert.IsTrue(batch.Add(r));
            }
            batch.Build().Complete();
            BasisBatchGraspSurface surface = new BasisBatchGraspSurface();
            surface.Set(batch);
            surface.Begin();
            System.Random random = new System.Random(19);
            int compared = 0, touching = 0;
            float worst = 0f;
            for (int i = 0; i < 4000; i++)
            {
                Renderer target = renderers[random.Next(renderers.Count)];
                Bounds b = target.bounds;
                Vector3 a = b.center + Vector3.Scale(b.extents, new Vector3((float)random.NextDouble() * 2.4f - 1.2f, (float)random.NextDouble() * 2.4f - 1.2f, (float)random.NextDouble() * 2.4f - 1.2f));
                Vector3 e = a + UnityEngine.Random.onUnitSphere * 0.05f;
                float radius = 0.01f + (float)random.NextDouble() * 0.02f, threshold = (float)random.NextDouble() * radius;
                float expected = mesh.Depth(a, e, radius), actual = surface.Depth(a, e, radius);
                worst = Mathf.Max(worst, Mathf.Abs(expected - actual));
                if (expected > 0f)
                {
                    touching++;
                }
                Assert.AreEqual(expected, actual, 1e-4f, $"segment {i}");
                Assert.AreEqual(expected > threshold, surface.Exceeds(a, e, radius, threshold), $"exceeds {i}");
                compared++;
            }
            surface.End();
            string line = string.Format(CultureInfo.InvariantCulture, "batchSurface,renderers={0},segments={1},touching={2},worstDepthError={3:E2}", renderers.Count, compared, touching, worst);
            report.AppendLine(line);
            UnityEngine.Debug.Log(line);
            Assert.Greater(touching, 200, line);
        }
        [Test]
        public void ColliderSurfaceDepth()
        {
            GameObject ball = Primitive(PrimitiveType.Sphere, Vector3.zero, Quaternion.identity, Vector3.one);
            BasisColliderGraspSurface surface = new BasisColliderGraspSurface();
            surface.Set(ball);
            Assert.AreEqual(1, surface.Count);
            surface.Begin();
            Assert.Greater(surface.Depth(new Vector3(-1f, 0f, 0f), new Vector3(1f, 0f, 0f), 0.1f), 0.5f);
            Assert.AreEqual(0f, surface.Depth(new Vector3(-1f, 2f, 0f), new Vector3(1f, 2f, 0f), 0.1f));
            Assert.AreEqual(0.1f, surface.Depth(new Vector3(-1f, 0.6f, 0f), new Vector3(1f, 0.6f, 0f), 0.2f), 0.01f);
            surface.End();
            Assert.IsFalse(BasisColliderGraspSurface.Probe().enabled);
        }
        [Test]
        public void MeshSurfaceDepthMatchesColliderOnBox()
        {
            GameObject box = Primitive(PrimitiveType.Cube, Vector3.zero, Quaternion.Euler(10f, 20f, 30f), Vector3.one);
            Physics.SyncTransforms();
            BasisColliderGraspSurface colliders = new BasisColliderGraspSurface();
            colliders.Set(box);
            BasisMeshGraspSurface mesh = new BasisMeshGraspSurface();
            Assert.IsTrue(mesh.Add(box.GetComponent<Renderer>(), Vector3.zero, 0f));
            Assert.AreEqual(12, mesh.Count);
            colliders.Begin();
            System.Random random = new System.Random(7);
            int compared = 0;
            for (int i = 0; i < 200; i++)
            {
                Vector3 a = new Vector3((float)random.NextDouble() * 2f - 1f, (float)random.NextDouble() * 2f - 1f, (float)random.NextDouble() * 2f - 1f) * 1.2f, b = a + UnityEngine.Random.onUnitSphere * 0.3f;
                float m = Mathf.Max(0f, mesh.Depth(a, b, 0.05f)), c = colliders.Depth(a, b, 0.05f);
                float threshold = (float)random.NextDouble() * 0.06f;
                Assert.AreEqual(mesh.Depth(a, b, 0.05f) > threshold, mesh.Exceeds(a, b, 0.05f, threshold), $"mesh exceeds {a} {b} {threshold}");
                Assert.AreEqual(c > threshold, colliders.Exceeds(a, b, 0.05f, threshold), $"collider exceeds {a} {b} {threshold}");
                if (m > 0.05f || c > 0.05f)
                {
                    continue;
                }
                Assert.AreEqual(c, m, 0.004f, $"segment {a} {b}");
                compared++;
            }
            colliders.End();
            Assert.Greater(compared, 20);
        }
        [Test]
        public void RendererRaycastHitsSphereSurface()
        {
            GameObject ball = Primitive(PrimitiveType.Sphere, new Vector3(1f, 2f, 3f), Quaternion.identity, Vector3.one * 2f);
            Renderer renderer = ball.GetComponent<Renderer>();
            for (int i = 0; i < 50; i++)
            {
                Vector3 dir = UnityEngine.Random.onUnitSphere, origin = ball.transform.position + dir * 3f;
                Assert.IsTrue(BasisRendererRaycast.Raycast(renderer, new Ray(origin, -dir), 10f, out BasisRendererHit hit));
                Assert.AreEqual(1f, (hit.point - ball.transform.position).magnitude, 0.02f);
                Assert.Greater(Vector3.Dot(hit.normal, dir), 0.95f);
                Assert.AreEqual(2f, hit.distance, 0.02f);
            }
            Assert.IsFalse(BasisRendererRaycast.Raycast(renderer, new Ray(new Vector3(1f, 6f, 3f), Vector3.right), 10f, out _));
            Assert.IsFalse(BasisRendererRaycast.Raycast(renderer, new Ray(new Vector3(1f, 2f, 7f), -Vector3.forward), 2f, out _));
        }
        [TestCaseSource(nameof(Rigs))]
        public void RendererRaycastMatchesBakedSkinnedMesh(string rigName)
        {
            using BasisFingerTestRig rig = BasisFingerTestRig.Build(rigName);
            SkinnedMeshRenderer skinned = rig.Animator.GetComponentInChildren<SkinnedMeshRenderer>();
            if (skinned == null)
            {
                Assert.Ignore("no skinned mesh");
            }
            Mesh baked = new Mesh();
            skinned.BakeMesh(baked, true);
            GameObject proxy = new GameObject("baked") { hideFlags = HideFlags.DontSave };
            made.Add(proxy);
            proxy.transform.SetPositionAndRotation(skinned.transform.position, skinned.transform.rotation);
            proxy.transform.localScale = skinned.transform.lossyScale;
            MeshCollider collider = proxy.AddComponent<MeshCollider>();
            collider.sharedMesh = baked;
            Physics.SyncTransforms();
            Bounds bounds = skinned.bounds;
            System.Random random = new System.Random(3);
            int hits = 0;
            bool backfaces = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = true;
            try
            {
                for (int i = 0; i < 200; i++)
                {
                    Vector3 target = bounds.center + Vector3.Scale(bounds.extents, new Vector3((float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f));
                    Vector3 origin = target + UnityEngine.Random.onUnitSphere * bounds.extents.magnitude * 2f;
                    Ray ray = new Ray(origin, (target - origin).normalized);
                    bool physics = collider.Raycast(ray, out RaycastHit expected, 100f);
                    bool ours = BasisRendererRaycast.Raycast(skinned, ray, 100f, out BasisRendererHit hit);
                    Assert.AreEqual(physics, ours, $"ray {i}");
                    if (!physics)
                    {
                        continue;
                    }
                    hits++;
                    Assert.AreEqual(expected.distance, hit.distance, 1e-3f * bounds.extents.magnitude);
                    Assert.LessOrEqual(collider.bounds.SqrDistance(hit.point), 1e-6f * bounds.extents.sqrMagnitude);
                }
            }
            finally
            {
                Physics.queriesHitBackfaces = backfaces;
            }
            Assert.Greater(hits, 20);
            UnityEngine.Object.DestroyImmediate(baked);
        }
        [TestCaseSource(nameof(Rigs))]
        public void WrapsAroundSurfaces(string rigName)
        {
            using BasisFingerTestRig rig = BasisFingerTestRig.Build(rigName);
            List<string> failures = new List<string>();
            int perJoint = 0, single = 0;
            foreach (int backend in new[] { 0, 1, 2 })
            {
                foreach (string shape in Shapes)
                {
                    if (backend > 0 && shape == "cylinder_concave")
                    {
                        continue;
                    }
                    CaseResult r = Measure(rig, false, shape, backend, out string line);
                    report.AppendLine(line);
                    UnityEngine.Debug.Log(line);
                    perJoint += r.touchingSegments;
                    single += r.singleTouchingSegments;
                    float mm = r.scale * 0.001f;
                    if (r.maxPenetration > 2f * mm)
                    {
                        failures.Add($"{line}: penetration on finger {r.worstFinger}");
                    }
                    if (r.meanGap > 2f * mm)
                    {
                        failures.Add($"{line}: gap");
                    }
                    if (r.nonThumbTouching < 3)
                    {
                        failures.Add($"{line}: only {r.nonThumbTouching} non-thumb fingers on the surface");
                    }
                    Cleanup();
                }
            }
            Assert.GreaterOrEqual(perJoint, single, "per-joint wrap should keep at least as many segments on the surface as one curl per finger");
            Assert.IsEmpty(failures, string.Join("\n", failures));
        }
        [TestCaseSource(nameof(Rigs))]
        public void LeftHandMirrorsRight(string rigName)
        {
            using BasisFingerTestRig rig = BasisFingerTestRig.Build(rigName);
            CaseResult right = Measure(rig, false, "handle_mid", false, out string rightLine);
            Cleanup();
            CaseResult left = Measure(rig, true, "handle_mid", false, out string leftLine);
            report.AppendLine(leftLine.Replace("handle_mid", "handle_mid_left"));
            Assert.GreaterOrEqual(left.nonThumbTouching, 3, leftLine);
            Assert.AreEqual(right.nonThumbTouching, left.nonThumbTouching, 1, $"{rightLine}\n{leftLine}");
        }
    }
}
