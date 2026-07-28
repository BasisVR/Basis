using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;

namespace GatorDragonGames.JigglePhysics.Tests {

/// <summary>
/// Covers JiggleJobs, the orchestration layer that owns the memory bus and schedules the whole job
/// graph. Unlike the per-stage simulate tests these drive the real pipeline end to end — commit,
/// read, broad phase, simulate, interpolate, write back — so they are the tests that answer "does
/// jiggle actually run", not just "does this one stage do its arithmetic".
/// </summary>
[TestFixture]
internal unsafe class JiggleJobsTests {
    private const float Tolerance = 1e-4f;
    private const float FixedDeltaTime = 0.02f;

    private JiggleBoneScene scene;
    private JiggleJobs jobs;
    private readonly List<JiggleTree> trees = new List<JiggleTree>();
    private double simulatedTime;

    private sealed class CommittedRig {
        public JiggleTree tree;
        public Transform[] bones;
        public Transform Tip => bones[bones.Length - 1];
    }

    [SetUp]
    public void SetUp() {
        JiggleRuntimeStatics.Boot();
        scene = new JiggleBoneScene();
        jobs = new JiggleJobs(0.0, FixedDeltaTime);
        simulatedTime = 0.0;
    }

    [TearDown]
    public void TearDown() {
        jobs?.Dispose();
        jobs = null;
        for (int i = 0; i < trees.Count; i++) {
            JiggleSceneFactory.FreeStruct(trees[i]);
        }
        trees.Clear();
        JiggleRuntimeStatics.Shutdown();
        scene?.Dispose();
        scene = null;
    }

    /// <summary>
    /// A horizontal chain, so gravity has something to bend. A chain hanging along gravity is
    /// already at equilibrium and would never visibly move.
    /// </summary>
    private CommittedRig AddChain(int boneCount = 4, float stiffness = 0.8f, float gravity = 1f,
        string prefix = "bone") {
        var root = scene.Chain(boneCount, 0.25f, prefix, new Vector3(0.25f, 0f, 0f));
        var rig = JiggleSceneFactory.Rig(root);
        rig.jiggleTreeInputParameters.stiffness.value = stiffness;
        rig.jiggleTreeInputParameters.gravity.value = gravity;
        var tree = JigglePhysics.CreateJiggleTree(rig, null);
        trees.Add(tree);
        jobs.ScheduleAdd(tree);
        return new CommittedRig { tree = tree, bones = JiggleBoneScene.Descend(root, boneCount) };
    }

    private JiggleTree AddRigWithPersonalCollider(float radius) {
        var root = scene.Chain(3);
        var rig = JiggleSceneFactory.Rig(root);
        rig.jiggleColliders = new[] { JiggleSceneFactory.SphereCollider(scene.Spawn("chest"), radius) };
        var tree = JigglePhysics.CreateJiggleTree(rig, null);
        trees.Add(tree);
        jobs.ScheduleAdd(tree);
        return tree;
    }

    /// <summary>
    /// Simulate() bails out early while the bus still reports no transforms, and the commit itself
    /// is sliced, so a freshly scheduled rig needs a few turns of the crank before the full graph
    /// runs. Three steps is enough for a small rig.
    /// </summary>
    private void Step(int count = 1) {
        for (int i = 0; i < count; i++) {
            simulatedTime += FixedDeltaTime;
            jobs.Simulate(simulatedTime, simulatedTime, 1);
            jobs.SchedulePoses(simulatedTime);
            jobs.CompletePoses();
        }
        jobs.CompleteSimulate();
    }

    private static IntPtr Scratch() {
        return (IntPtr)UnsafeUtility.Malloc(64, 16, Allocator.Persistent);
    }

    // ------------------------------------------------------------- lifecycle

    [Test]
    public void NewPipeline_StartsEmptyWithPreallocatedCapacity() {
        Assert.AreEqual(0, jobs.GetTransformCount());
        Assert.AreEqual(0, jobs.GetSceneColliderCount());
        Assert.AreEqual(0, jobs.GetPersonalColliderCount());
        Assert.Greater(jobs.GetTransformCapcity(), 0);
        Assert.Greater(jobs.GetSceneColliderCapacity(), 0);
        Assert.Greater(jobs.GetPersonalColliderCapacity(), 0);
    }

    [Test]
    public void Simulate_WithNothingScheduled_DoesNotThrow() {
        Assert.DoesNotThrow(() => Step(3));

        Assert.AreEqual(0, jobs.GetTransformCount());
    }

    [Test]
    public void SchedulePoses_WithNothingCommitted_IsANoOp() {
        Assert.DoesNotThrow(() => jobs.SchedulePoses(0.0));
        Assert.DoesNotThrow(() => jobs.CompletePoses());
    }

    [Test]
    public void CompleteCalls_BeforeAnythingIsScheduled_AreSafe() {
        Assert.DoesNotThrow(() => jobs.CompleteSimulate());
        Assert.DoesNotThrow(() => jobs.CompletePoses());
    }

    [Test]
    public void ScheduleAdd_CommitsTheRigOntoThePipeline() {
        AddChain();

        Step(3);

        Assert.AreEqual(6, jobs.GetTransformCount());
        jobs.GetTrees(out var treeCount);
        Assert.AreEqual(1, treeCount);
    }

    [Test]
    public void ScheduleRemove_TakesTheRigBackOffThePipeline() {
        var rig = AddChain();
        Step(3);

        jobs.ScheduleRemove(rig.tree);
        Step(3);

        jobs.GetTrees(out var treeCount);
        Assert.AreEqual(0, treeCount);
    }

    [Test]
    public void GetTrees_ExposesTheCommittedTree() {
        var rig = AddChain();

        Step(3);

        var published = jobs.GetTrees(out var treeCount);
        Assert.AreEqual(1, treeCount);
        Assert.AreEqual(rig.tree.rootID, published[0].rootID);
        Assert.AreEqual((uint)rig.tree.points.Length, published[0].pointCount);
    }

    /// <summary>
    /// Outgrowing the transform buffer reallocates every one of the ten parallel pose arrays and
    /// copies the live prefix across. A rig committed before the resize has to come out the other
    /// side pointing at the same place.
    /// </summary>
    [Test]
    public void OutgrowingTheTransformBuffer_PreservesTheRigsAlreadyCommitted() {
        var resident = AddChain(3, prefix: "resident");
        Step(4);
        var capacityBefore = jobs.GetTransformCapcity();
        var residentPose = jobs.GetInterpolatedOutputPoses(out _)[1].position;
        for (int i = 0; i < 70; i++) {
            AddChain(60, prefix: $"bulk{i}_");
        }

        Step(30);

        Assert.Greater(jobs.GetTransformCount(), capacityBefore, "the buffer was never actually outgrown");
        Assert.GreaterOrEqual(jobs.GetTransformCapcity(), jobs.GetTransformCount());
        JiggleAssert.AreEqual(residentPose, jobs.GetInterpolatedOutputPoses(out _)[1].position, 1e-2f);
        Assert.IsNotNull(resident.tree);
    }

    // ---------------------------------------------------------- the pipeline

    /// <summary>
    /// The end to end check: a horizontal chain with gravity on has to actually bend, and the bend
    /// has to reach the real bone transforms rather than staying inside the native buffers.
    /// </summary>
    [Test]
    public void Simulate_UnderGravity_BendsTheChainAndWritesItBackToTheBones() {
        var rig = AddChain(5, stiffness: 0.2f, gravity: 4f);
        Step(3);
        var startY = rig.Tip.position.y;

        Step(60);

        Assert.Less(rig.Tip.position.y, startY - 0.01f, "gravity never reached the bone transforms");
        JiggleAssert.IsFinite((float3)rig.Tip.position, "tip");
    }

    [Test]
    public void Simulate_WithoutGravity_LeavesTheChainOnItsAnimatedPose() {
        var rig = AddChain(4, stiffness: 0.8f, gravity: 0f);
        Step(3);
        var start = rig.Tip.position;

        Step(40);

        Assert.AreEqual(0f, Vector3.Distance(start, rig.Tip.position), 1e-3f);
    }

    [Test]
    public void Simulate_ProducesFiniteOutputPosesForEverySlot() {
        AddChain(6, stiffness: 0.3f, gravity: 4f);

        Step(30);

        var poses = jobs.GetInterpolatedOutputPoses(out var poseCount);
        Assert.AreEqual(jobs.GetTransformCount(), poseCount);
        for (int i = 0; i < poseCount; i++) {
            JiggleAssert.IsFinite(poses[i].position, $"pose {i}");
            JiggleAssert.IsFinite(poses[i].rotation, $"pose {i}");
        }
    }

    [Test]
    public void Simulate_KeepsTheSimulatedPointsFiniteUnderAggressiveSettings() {
        AddChain(8, stiffness: 1f, gravity: 40f);

        Step(60);

        var published = jobs.GetTrees(out var treeCount);
        Assert.AreEqual(1, treeCount);
        for (int i = 0; i < published[0].pointCount; i++) {
            JiggleAssert.IsFinite(published[0].points[i].position, $"point {i}");
        }
    }

    [Test]
    public void Simulate_WithManyRigs_KeepsEveryOneOfThemFinite() {
        for (int i = 0; i < 12; i++) {
            AddChain(5, stiffness: 0.4f, gravity: 6f, prefix: $"crowd{i}_");
        }

        Step(30);

        var published = jobs.GetTrees(out var treeCount);
        Assert.AreEqual(12, treeCount);
        for (int t = 0; t < treeCount; t++) {
            for (int i = 0; i < published[t].pointCount; i++) {
                JiggleAssert.IsFinite(published[t].points[i].position, $"tree {t} point {i}");
            }
        }
    }

    [Test]
    public void GetResults_ReturnsThePosesAndTreesTogether() {
        var rig = AddChain();

        Step(4);

        jobs.GetResults(out var poses, out var published, out var poseCount, out var treeCount);
        Assert.AreEqual(1, treeCount);
        Assert.AreEqual(jobs.GetTransformCount(), poseCount);
        Assert.AreEqual(rig.tree.rootID, published[0].rootID);
        Assert.IsNotNull(poses);
    }

    [Test]
    public void TryGetRenderDependencies_IsFalseUntilBothHalvesHaveRun() {
        Assert.IsFalse(jobs.TryGetRenderDependencies(out _));
        AddChain();

        Step(4);

        Assert.IsTrue(jobs.TryGetRenderDependencies(out _));
    }

    [Test]
    public void OnFinishSimulate_FiresOnceAStepHasActuallyBeenSimulated() {
        AddChain();
        var fired = 0;
        jobs.OnFinishSimulate += (_, _) => fired++;

        Step(6);

        Assert.Greater(fired, 0);
    }

    [Test]
    public void SetFixedDeltaTime_IsAcceptedWhileRunning() {
        AddChain();
        Step(3);

        Assert.DoesNotThrow(() => jobs.SetFixedDeltaTime(1f / 90f));
        Assert.DoesNotThrow(() => Step(5));
    }

    // ------------------------------------------------------------- colliders

    [Test]
    public void ScheduleAdd_Collider_CommitsIntoTheSceneColliderPool() {
        var transform = scene.Spawn("worldCollider");

        jobs.ScheduleAdd(JiggleSceneFactory.SphereCollider(transform, 0.5f));
        Step(3);

        var colliders = jobs.GetSceneColliders(out var count);
        Assert.AreEqual(1, count);
        Assert.IsTrue(colliders[0].enabled);
        Assert.AreEqual(0.5f, colliders[0].radius, Tolerance);
    }

    [Test]
    public void ScheduleRemove_Collider_DisablesItsSlot() {
        var transform = scene.Spawn("worldCollider");
        var collider = JiggleSceneFactory.SphereCollider(transform);
        jobs.ScheduleAdd(collider);
        Step(3);

        jobs.ScheduleRemove(collider);
        Step(3);

        var colliders = jobs.GetSceneColliders(out _);
        Assert.IsFalse(colliders[0].enabled);
    }

    [Test]
    public void ScheduleAddBatch_Colliders_DedupeByTransform() {
        var first = scene.Spawn("a");
        var second = scene.Spawn("b");

        jobs.ScheduleAddBatch(new List<JiggleColliderSerializable> {
            JiggleSceneFactory.SphereCollider(first),
            JiggleSceneFactory.SphereCollider(second),
            JiggleSceneFactory.SphereCollider(first),
        });
        Step(3);

        Assert.AreEqual(2, jobs.GetSceneColliderCount());
    }

    [Test]
    public void ScheduleRemoveBatch_Colliders_RetiresEveryCommittedSlot() {
        var first = JiggleSceneFactory.SphereCollider(scene.Spawn("a"));
        var second = JiggleSceneFactory.SphereCollider(scene.Spawn("b"));
        jobs.ScheduleAddBatch(new List<JiggleColliderSerializable> { first, second });
        Step(3);

        jobs.ScheduleRemoveBatch(new List<JiggleColliderSerializable> { first, second });
        Step(3);

        var colliders = jobs.GetSceneColliders(out var count);
        Assert.AreEqual(2, count);
        Assert.IsFalse(colliders[0].enabled);
        Assert.IsFalse(colliders[1].enabled);
    }

    /// <summary>
    /// The collider read job runs every frame against the committed TransformAccessArray, so a
    /// committed collider's world matrix has to track its transform rather than staying wherever it
    /// was authored — a stale matrix is how a collider ends up colliding from where the avatar spawned.
    /// </summary>
    [Test]
    public void CommittedColliders_FollowTheirTransform() {
        // A rig has to be present: Simulate short-circuits entirely while the bus reports no
        // transforms, so with colliders alone the collider read job is never scheduled.
        AddChain();
        var transform = scene.Spawn("worldCollider");
        jobs.ScheduleAdd(JiggleSceneFactory.SphereCollider(transform, 0.5f));
        Step(4);

        transform.position = new Vector3(4f, 5f, 6f);
        Step(2);

        var colliders = jobs.GetSceneColliders(out _);
        JiggleAssert.AreEqual(new float3(4f, 5f, 6f), colliders[0].localToWorldMatrix.c3.xyz, 1e-3f);
    }

    [Test]
    public void GetPersonalColliders_ExposesTheRigsOwnColliders() {
        AddRigWithPersonalCollider(0.4f);

        Step(3);

        var personal = jobs.GetPersonalColliders(out var count);
        Assert.AreEqual(1, count);
        Assert.AreEqual(0.4f, personal[0].radius, Tolerance);
        Assert.AreEqual(1, jobs.GetPersonalColliderCount());
    }

    [Test]
    public void GetColliders_CopiesBothPoolsIntoManagedArrays() {
        AddRigWithPersonalCollider(0.4f);
        jobs.ScheduleAdd(JiggleSceneFactory.SphereCollider(scene.Spawn("world"), 0.9f));
        Step(3);

        jobs.GetColliders(out var personal, out var world, out var personalCount, out var worldCount);

        Assert.AreEqual(1, personalCount);
        Assert.AreEqual(1, worldCount);
        Assert.AreEqual(0.4f, personal[0].radius, Tolerance);
        Assert.AreEqual(0.9f, world[0].radius, Tolerance);
    }

    [Test]
    public void SetCollisionCulling_IsAcceptedAndDoesNotDisturbTheGraph() {
        AddChain();
        jobs.ScheduleAdd(JiggleSceneFactory.SphereCollider(scene.Spawn("world"), 0.5f));
        Step(3);

        jobs.SetCollisionCulling(true, true, 5f, new JiggleCullingCamera[1], 1);

        Assert.DoesNotThrow(() => Step(5));
        Assert.AreEqual(1, jobs.GetSceneColliderCount());
    }

    // ------------------------------------------------------------- teleports

    [Test]
    public void Teleport_ShiftsACommittedRigOnTheNextStep() {
        var rig = AddChain();
        Step(3);
        var before = jobs.GetTrees(out _)[0].points[1].position;

        var delta = new float3(0f, 0f, 9f);
        rig.bones[0].position += (Vector3)delta;
        jobs.Teleport(rig.tree, delta);
        Step(1);

        JiggleAssert.AreEqual(before + delta, jobs.GetTrees(out _)[0].points[1].position, 1e-2f);
    }

    [Test]
    public void Teleport_OfANullTree_IsIgnored() {
        Assert.DoesNotThrow(() => jobs.Teleport(null, new float3(1f, 1f, 1f)));
    }

    [Test]
    public void TeleportRigid_RotatesACommittedRigAboutThePivotOnTheNextStep() {
        var rig = AddChain();
        Step(3);
        var before = jobs.GetTrees(out _)[0].points[1].position;

        var rotation = quaternion.RotateY(math.radians(90f));
        var delta = new float3(0f, 0f, 9f);
        var root = rig.bones[0];
        root.GetPositionAndRotation(out var rootPos, out var rootRot);
        var pivot = (float3)rootPos;
        root.SetPositionAndRotation(pivot + delta, math.mul(rotation, rootRot));
        jobs.Teleport(rig.tree, rotation, pivot, delta);
        Step(1);

        var expected = pivot + math.mul(rotation, before - pivot) + delta;
        JiggleAssert.AreEqual(expected, jobs.GetTrees(out _)[0].points[1].position, 1e-2f);
    }

    [Test]
    public void TeleportRigid_ComposesWithAQueuedTranslationOnTheSameStep() {
        var rig = AddChain();
        Step(3);
        var before = jobs.GetTrees(out _)[0].points[1].position;

        var rotation = quaternion.RotateY(math.radians(90f));
        var delta = new float3(0f, 0f, 9f);
        var root = rig.bones[0];
        root.GetPositionAndRotation(out var rootPos, out var rootRot);
        var pivot = (float3)rootPos;
        root.SetPositionAndRotation(pivot + delta, math.mul(rotation, rootRot));
        jobs.Teleport(rig.tree, delta);
        jobs.Teleport(rig.tree, rotation, pivot + delta, float3.zero);
        Step(1);

        var expected = pivot + delta + math.mul(rotation, before - pivot);
        JiggleAssert.AreEqual(expected, jobs.GetTrees(out _)[0].points[1].position, 1e-2f);
    }

    [Test]
    public void TeleportRigid_OfANullTree_IsIgnored() {
        Assert.DoesNotThrow(() => jobs.Teleport(null, quaternion.RotateY(1f), float3.zero, new float3(1f, 1f, 1f)));
    }

    // -------------------------------------------------------- deferred frees

    [Test]
    public void FreeOnComplete_ReleasesThePointerOnTheNextSimulate() {
        AddChain();
        Step(3);

        jobs.FreeOnComplete(Scratch());

        Assert.DoesNotThrow(() => Step(3));
    }

    [Test]
    public void FreeOnCommitFlip_ReleasesThePointerOnTheNextTreeCommit() {
        AddChain();
        Step(3);

        jobs.FreeOnCommitFlip(Scratch());
        AddChain(3, prefix: "second");

        Assert.DoesNotThrow(() => Step(4));
    }

    [Test]
    public void Dispose_CompletesInFlightWorkWithoutThrowing() {
        AddChain();
        simulatedTime += FixedDeltaTime;
        jobs.Simulate(simulatedTime, simulatedTime, 1);
        jobs.SchedulePoses(simulatedTime);

        Assert.DoesNotThrow(() => jobs.Dispose());

        jobs = null;
    }
}

}
