using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace GatorDragonGames.JigglePhysics.Tests {

[TestFixture]
internal unsafe class JiggleJobPrepareRenderTests {
    private NativeArray<JiggleCollider> personal;
    private NativeArray<JiggleCollider> scene;
    private NativeArray<JiggleTransform> poses;
    private NativeArray<JiggleTreeJobData> trees;
    private NativeArray<JiggleRenderInstancer.GPUChunk> sphereChunks;
    private NativeArray<JiggleRenderInstancer.GPUChunk> capsuleChunks;
    private NativeReference<Bounds> sphereBounds;
    private NativeReference<Bounds> capsuleBounds;
    private NativeReference<int> sphereCount;
    private NativeReference<int> capsuleCount;

    [SetUp]
    public void SetUp() {
        personal = new NativeArray<JiggleCollider>(8, Allocator.Persistent);
        scene = new NativeArray<JiggleCollider>(8, Allocator.Persistent);
        poses = new NativeArray<JiggleTransform>(8, Allocator.Persistent);
        trees = new NativeArray<JiggleTreeJobData>(1, Allocator.Persistent);
        sphereChunks = new NativeArray<JiggleRenderInstancer.GPUChunk>(64, Allocator.Persistent);
        capsuleChunks = new NativeArray<JiggleRenderInstancer.GPUChunk>(64, Allocator.Persistent);
        sphereBounds = new NativeReference<Bounds>(Allocator.Persistent);
        capsuleBounds = new NativeReference<Bounds>(Allocator.Persistent);
        sphereCount = new NativeReference<int>(Allocator.Persistent);
        capsuleCount = new NativeReference<int>(Allocator.Persistent);
    }

    [TearDown]
    public void TearDown() {
        if (personal.IsCreated) personal.Dispose();
        if (scene.IsCreated) scene.Dispose();
        if (poses.IsCreated) poses.Dispose();
        if (trees.IsCreated) trees.Dispose();
        if (sphereChunks.IsCreated) sphereChunks.Dispose();
        if (capsuleChunks.IsCreated) capsuleChunks.Dispose();
        if (sphereBounds.IsCreated) sphereBounds.Dispose();
        if (capsuleBounds.IsCreated) capsuleBounds.Dispose();
        if (sphereCount.IsCreated) sphereCount.Dispose();
        if (capsuleCount.IsCreated) capsuleCount.Dispose();
    }

    private JiggleJobPrepareRender BuildJob(int personalCount = 0, int sceneCount = 0, int treeCount = 0) {
        return new JiggleJobPrepareRender {
            personalColliders = personal,
            sceneColliders = scene,
            outputPoses = poses,
            trees = trees,
            personalColliderCount = personalCount,
            sceneColliderCount = sceneCount,
            treeCount = treeCount,
            transformCount = poses.Length,
            sphereChunks = sphereChunks,
            sphereBounds = sphereBounds,
            sphereCount = sphereCount,
            capsuleChunks = capsuleChunks,
            capsuleBounds = capsuleBounds,
            capsuleCount = capsuleCount,
        };
    }

    [Test]
    public void PersonalSphere_ProducesOneSphereChunk() {
        personal[0] = JiggleTestFactory.Sphere(new float3(1f, 2f, 3f), 0.5f);

        BuildJob(personalCount: 1).Execute();

        Assert.AreEqual(1, sphereCount.Value);
        JiggleAssert.AreEqual(new float3(1f, 2f, 3f), sphereChunks[0].matrix.c3.xyz, 1e-3f);
    }

    [Test]
    public void SphereChunk_IsScaledToTheColliderDiameter() {
        personal[0] = JiggleTestFactory.Sphere(float3.zero, 0.5f);

        BuildJob(personalCount: 1).Execute();

        Assert.AreEqual(1f, math.length(sphereChunks[0].matrix.c0.xyz), 1e-3f);
    }

    [Test]
    public void DisabledColliders_ProduceNoChunks() {
        personal[0] = JiggleTestFactory.Sphere(float3.zero, 0.5f, enabled: false);

        BuildJob(personalCount: 1).Execute();

        Assert.AreEqual(0, sphereCount.Value);
    }

    /// <summary>
    /// Retired and freshly added slots keep a stale or zeroed matrix until the collider read job has
    /// visited them; worldRadius doubles as "this slot has been sampled at least once".
    /// </summary>
    [Test]
    public void UnsampledColliderSlots_ProduceNoChunks() {
        personal[0] = new JiggleCollider {
            enabled = true, type = JiggleCollider.JiggleColliderType.Sphere, radius = 0.5f, worldRadius = 0f,
        };

        BuildJob(personalCount: 1).Execute();

        Assert.AreEqual(0, sphereCount.Value);
    }

    [Test]
    public void PersonalAndSceneColliders_UseDistinctColours() {
        personal[0] = JiggleTestFactory.Sphere(float3.zero, 0.5f);
        scene[0] = JiggleTestFactory.Sphere(new float3(5f, 0f, 0f), 0.5f);

        BuildJob(personalCount: 1, sceneCount: 1).Execute();

        Assert.AreEqual(2, sphereCount.Value);
        Assert.AreNotEqual(sphereChunks[0].color.x, sphereChunks[1].color.x);
    }

    [Test]
    public void Capsules_ProduceCapsuleChunks() {
        personal[0] = JiggleTestFactory.Capsule(new float3(0f, 1f, 0f), 0.25f, 2f, JiggleCollider.CapsuleAxis.Y);

        BuildJob(personalCount: 1).Execute();

        Assert.AreEqual(1, capsuleCount.Value);
        Assert.AreEqual(0, sphereCount.Value);
        JiggleAssert.AreEqual(new float3(0f, 1f, 0f), capsuleChunks[0].matrix.c3.xyz, 1e-3f);
    }

    [Test]
    public void CapsuleMatrix_IsLengthenedAlongItsAxis() {
        personal[0] = JiggleTestFactory.Capsule(float3.zero, 0.25f, 4f, JiggleCollider.CapsuleAxis.Y);

        BuildJob(personalCount: 1).Execute();

        var lengthScale = math.length(capsuleChunks[0].matrix.c1.xyz);
        Assert.AreEqual(4f * 0.5f + 0.25f, lengthScale, 1e-3f);
    }

    /// <summary>
    /// The mesh is authored along Y, so the long axis stays in column 1 and the permutation instead
    /// steers that column onto the collider's configured world axis.
    /// </summary>
    [Test]
    public void CapsuleMatrix_PointsItsLongAxisAlongTheConfiguredAxis() {
        personal[0] = JiggleTestFactory.Capsule(float3.zero, 0.25f, 4f, JiggleCollider.CapsuleAxis.X);
        BuildJob(personalCount: 1).Execute();
        var alongX = capsuleChunks[0].matrix.c1.xyz;

        personal[0] = JiggleTestFactory.Capsule(float3.zero, 0.25f, 4f, JiggleCollider.CapsuleAxis.Y);
        BuildJob(personalCount: 1).Execute();
        var alongY = capsuleChunks[0].matrix.c1.xyz;

        personal[0] = JiggleTestFactory.Capsule(float3.zero, 0.25f, 4f, JiggleCollider.CapsuleAxis.Z);
        BuildJob(personalCount: 1).Execute();
        var alongZ = capsuleChunks[0].matrix.c1.xyz;

        Assert.AreEqual(2.25f, math.length(alongX), 1e-3f);
        JiggleAssert.AreEqual(new float3(1f, 0f, 0f), math.normalize(alongX), 1e-3f);
        JiggleAssert.AreEqual(new float3(0f, 1f, 0f), math.normalize(alongY), 1e-3f);
        JiggleAssert.AreEqual(new float3(0f, 0f, 1f), math.normalize(alongZ), 1e-3f);
    }

    [Test]
    public void Planes_ProduceNoChunks() {
        personal[0] = JiggleTestFactory.Plane(float3.zero, new float3(0f, 1f, 0f));

        BuildJob(personalCount: 1).Execute();

        Assert.AreEqual(0, sphereCount.Value);
        Assert.AreEqual(0, capsuleCount.Value);
    }

    [Test]
    public void SphereBounds_GrowToCoverTheColliders() {
        personal[0] = JiggleTestFactory.Sphere(new float3(4f, 0f, 0f), 1f);

        BuildJob(personalCount: 1).Execute();

        Assert.GreaterOrEqual(sphereBounds.Value.size.x, 10f);
    }

    [Test]
    public void EmptyInput_ProducesZeroCounts() {
        BuildJob().Execute();

        Assert.AreEqual(0, sphereCount.Value);
        Assert.AreEqual(0, capsuleCount.Value);
    }

    [Test]
    public void TreePoints_AreDrawnAsSpheres() {
        var tree = JiggleTestTree.Chain(2, float3.zero, new float3(1f, 0f, 0f), 1f,
            JiggleTestFactory.Params(collisionRadius: 0.2f));
        for (int i = 0; i < tree.PointCount; i++) {
            var point = tree.points[i];
            point.worldRadius = point.hasTransform ? 0.2f : 0f;
            tree.points[i] = point;
            poses[i] = new JiggleTransform {
                isVirtual = !tree.points[i].hasTransform,
                position = tree.points[i].pose,
                rotation = quaternion.identity,
                scale = new float3(1f),
            };
        }
        var jobData = new JiggleTreeJobData(0, 0, 0, 0, tree.points, tree.parameters);
        trees[0] = jobData;

        BuildJob(treeCount: 1).Execute();

        Assert.AreEqual(2, sphereCount.Value, "only the two transform-backed points should be drawn");

        Unity.Collections.LowLevel.Unsafe.UnsafeUtility.Free(jobData.points, Allocator.Persistent);
        Unity.Collections.LowLevel.Unsafe.UnsafeUtility.Free(jobData.parameters, Allocator.Persistent);
    }
}

}
