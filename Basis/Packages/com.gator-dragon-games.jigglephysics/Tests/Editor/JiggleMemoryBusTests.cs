using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace GatorDragonGames.JigglePhysics.Tests {

/// <summary>
/// Covers JiggleMemoryBus, the multi-frame state machine that folds rigs and colliders into the
/// flat buffers the jobs read. Everything here is a lifecycle event an avatar actually causes:
/// joining, leaving, regenerating, teleporting, and registering world colliders. Adds and removes
/// are deliberately batched the way a join storm batches them, because the interesting failures are
/// in the ordering rather than in any single operation.
/// </summary>
[TestFixture]
internal unsafe class JiggleMemoryBusTests {
    private const float Tolerance = 1e-4f;
    private const int DefaultTransformAccessBatchSize = 512;

    private JiggleBoneScene scene;
    private JiggleMemoryBus bus;
    private readonly List<JiggleTree> trees = new List<JiggleTree>();

    [SetUp]
    public void SetUp() {
        JiggleRuntimeStatics.Boot();
        scene = new JiggleBoneScene();
        bus = new JiggleMemoryBus();
    }

    [TearDown]
    public void TearDown() {
        // The batch size is a process wide tunable, so a fixture that lowers it has to put it back
        // or every later commit test silently runs sliced.
        JiggleMemoryBus.SetTransformAccessBatchSize(DefaultTransformAccessBatchSize);
        for (int i = 0; i < trees.Count; i++) {
            JiggleSceneFactory.FreeStruct(trees[i]);
        }
        trees.Clear();
        bus?.Dispose();
        bus = null;
        JiggleRuntimeStatics.Shutdown();
        scene?.Dispose();
        scene = null;
    }

    private JiggleTree NewTree(int boneCount = 3, string prefix = "bone") {
        var root = scene.Chain(boneCount, 0.25f, prefix);
        var tree = JigglePhysics.CreateJiggleTree(JiggleSceneFactory.Rig(root), null);
        trees.Add(tree);
        return tree;
    }

    /// <summary>
    /// Drives CommitTrees until it has certainly drained. The commit deliberately does a bounded
    /// slice of work per call, so callers that care about how many frames it takes count the calls
    /// themselves instead of using this.
    /// </summary>
    private void PumpTrees(int calls = 64) {
        for (int i = 0; i < calls; i++) {
            bus.CommitTrees();
        }
    }

    private void PumpColliders(int calls = 8) {
        for (int i = 0; i < calls; i++) {
            bus.CommitColliders();
        }
    }

    private JiggleTreeJobData Committed(int rootID) {
        for (int i = 0; i < bus.treeCount; i++) {
            if (bus.jiggleTreeStructs[i].rootID == rootID) {
                return bus.jiggleTreeStructs[i];
            }
        }
        Assert.Fail($"tree {rootID} is not committed");
        return default;
    }

    private bool IsCommitted(int rootID) {
        for (int i = 0; i < bus.treeCount; i++) {
            if (bus.jiggleTreeStructs[i].rootID == rootID) {
                return true;
            }
        }
        return false;
    }

    // ---------------------------------------------------------------- adding

    [Test]
    public void ScheduledAdd_IsNotVisibleUntilTheCommitCompletes() {
        var tree = NewTree();
        bus.ScheduleAdd(tree);

        Assert.AreEqual(0, bus.treeCount, "queued but not committed");
        bus.CommitTrees();
        Assert.AreEqual(0, bus.treeCount, "first commit only reserves the slice");
        bus.CommitTrees();
        Assert.AreEqual(1, bus.treeCount);
    }

    [Test]
    public void AddTree_PublishesItsTreeAndTransformCounts() {
        var tree = NewTree();

        bus.ScheduleAdd(tree);
        PumpTrees();

        Assert.AreEqual(1, bus.treeCount);
        Assert.AreEqual(5, bus.transformCount);
        Assert.AreEqual(5u, Committed(tree.rootID).pointCount);
    }

    [Test]
    public void AddTree_SeedsThePoseBuffersFromTheBones() {
        var root = scene.Chain(3);
        root.position = new Vector3(3f, 4f, 5f);
        var tree = JigglePhysics.CreateJiggleTree(JiggleSceneFactory.Rig(root), null);
        trees.Add(tree);

        bus.ScheduleAdd(tree);
        PumpTrees();

        var offset = (int)Committed(tree.rootID).transformIndexOffset;
        Assert.IsTrue(bus.simulateInputPoses[offset].isVirtual, "back projected root");
        Assert.IsFalse(bus.simulateInputPoses[offset + 1].isVirtual);
        JiggleAssert.AreEqual(new float3(3f, 4f, 5f), bus.simulateInputPoses[offset + 1].position, Tolerance);
        JiggleAssert.AreEqual(new float3(3f, 3.75f, 5f), bus.simulateInputPoses[offset + 2].position, Tolerance);
        Assert.IsTrue(bus.simulateInputPoses[offset + 4].isVirtual, "projected tip");
    }

    [Test]
    public void AddTree_RecordsTheRootPositionAgainstEverySlot() {
        var root = scene.Chain(3);
        root.position = new Vector3(3f, 4f, 5f);
        var tree = JigglePhysics.CreateJiggleTree(JiggleSceneFactory.Rig(root), null);
        trees.Add(tree);

        bus.ScheduleAdd(tree);
        PumpTrees();

        var offset = (int)Committed(tree.rootID).transformIndexOffset;
        for (int i = 0; i < 5; i++) {
            JiggleAssert.AreEqual(new float3(3f, 4f, 5f), bus.rootOutputPositions[offset + i], Tolerance);
        }
    }

    [Test]
    public void AddTwoTrees_GetDisjointTransformRanges() {
        var first = NewTree(3, "a");
        var second = NewTree(4, "b");

        bus.ScheduleAdd(first);
        bus.ScheduleAdd(second);
        PumpTrees();

        var a = Committed(first.rootID);
        var b = Committed(second.rootID);
        var aEnd = a.transformIndexOffset + a.pointCount;
        var bEnd = b.transformIndexOffset + b.pointCount;

        Assert.AreEqual(2, bus.treeCount);
        Assert.IsTrue(aEnd <= b.transformIndexOffset || bEnd <= a.transformIndexOffset,
            "committed trees overlap in the transform buffer");
    }

    // -------------------------------------------------------------- removing

    /// <summary>
    /// A rig enabled and disabled inside a single frame is common during scene loads. Both halves
    /// have to evaporate: committing the add would strand a slice nothing ever removes.
    /// </summary>
    [Test]
    public void AddAndRemoveInTheSameFrame_CancelEachOtherOut() {
        var tree = NewTree();

        bus.ScheduleAdd(tree);
        bus.ScheduleRemove(tree);
        PumpTrees();

        Assert.AreEqual(0, bus.treeCount);
        Assert.AreEqual(0, bus.transformCount);
    }

    /// <summary>
    /// Regeneration schedules the remove before the add of the very same rig. Unlike the pair above
    /// this must survive, and the tree must not be disposed on the way through.
    /// </summary>
    [Test]
    public void RemoveThenAddInTheSameFrame_IsTheRegenerationPathAndKeepsTheTree() {
        var tree = NewTree();
        bus.ScheduleAdd(tree);
        PumpTrees();

        bus.ScheduleRemove(tree);
        bus.ScheduleAdd(tree);
        PumpTrees();

        Assert.AreEqual(1, bus.treeCount);
        Assert.IsTrue(IsCommitted(tree.rootID));
        Assert.IsTrue(tree.GetStruct().points != null, "the tree was disposed mid regeneration");
    }

    [Test]
    public void RemoveTree_DropsItFromTheTreeList() {
        var tree = NewTree();
        bus.ScheduleAdd(tree);
        PumpTrees();

        bus.ScheduleRemove(tree);
        PumpTrees();

        Assert.AreEqual(0, bus.treeCount);
        Assert.IsFalse(IsCommitted(tree.rootID));
    }

    [Test]
    public void RemoveTree_MarksItsPoseSlotsVirtual() {
        var tree = NewTree();
        bus.ScheduleAdd(tree);
        PumpTrees();
        var offset = (int)Committed(tree.rootID).transformIndexOffset;

        bus.ScheduleRemove(tree);
        PumpTrees();

        for (int i = 0; i < 5; i++) {
            Assert.IsTrue(bus.simulationOutputPoseData[offset + i].pose.isVirtual, $"slot {i} still writes back");
            Assert.IsTrue(bus.interpolationCurrentPoseData[offset + i].pose.isVirtual);
            Assert.IsTrue(bus.interpolationPreviousPoseData[offset + i].pose.isVirtual);
        }
    }

    /// <summary>
    /// Removal does not shrink transformCount — the freed slice is returned to the fragmenter and
    /// its pose slots are flagged virtual so the jobs skip them, but the high water mark stays.
    /// </summary>
    [Test]
    public void RemoveTree_LeavesTheTransformHighWaterMarkInPlace() {
        var tree = NewTree();
        bus.ScheduleAdd(tree);
        PumpTrees();

        bus.ScheduleRemove(tree);
        PumpTrees();

        Assert.AreEqual(0, bus.treeCount);
        Assert.AreEqual(5, bus.transformCount);
    }

    [Test]
    public void RemovedSlots_AreHandedToTheNextTree() {
        var first = NewTree(3, "a");
        bus.ScheduleAdd(first);
        PumpTrees();
        var reclaimed = Committed(first.rootID).transformIndexOffset;

        bus.ScheduleRemove(first);
        PumpTrees();
        var second = NewTree(3, "b");
        bus.ScheduleAdd(second);
        PumpTrees();

        Assert.AreEqual(reclaimed, Committed(second.rootID).transformIndexOffset);
    }

    /// <summary>
    /// Removal swaps the last tree into the freed slot rather than compacting, so the survivors move
    /// around inside jiggleTreeStructs. Their transform slices must not move with them.
    /// </summary>
    [Test]
    public void RemovingAMiddleTree_LeavesTheOthersAddressable() {
        var a = NewTree(3, "a");
        var b = NewTree(3, "b");
        var c = NewTree(3, "c");
        bus.ScheduleAdd(a);
        bus.ScheduleAdd(b);
        bus.ScheduleAdd(c);
        PumpTrees();
        var offsetA = Committed(a.rootID).transformIndexOffset;
        var offsetC = Committed(c.rootID).transformIndexOffset;

        bus.ScheduleRemove(b);
        PumpTrees();

        Assert.AreEqual(2, bus.treeCount);
        Assert.IsTrue(IsCommitted(a.rootID));
        Assert.IsTrue(IsCommitted(c.rootID));
        Assert.IsFalse(IsCommitted(b.rootID));
        Assert.AreEqual(offsetA, Committed(a.rootID).transformIndexOffset);
        Assert.AreEqual(offsetC, Committed(c.rootID).transformIndexOffset);
    }

    // ------------------------------------------------------------- rejection

    [Test]
    public void TreeWhoseBoneCountDriftedFromItsPointCount_IsRejectedAndMarkedDirty() {
        var tree = NewTree();
        tree.GetStruct();
        tree.bones = new Transform[tree.bones.Length - 1];
        LogAssert.Expect(LogType.Error,
            "JigglePhysics: Cannot add tree, point count does not match bone count. Attempting to regenerate tree...");

        bus.ScheduleAdd(tree);
        PumpTrees();

        Assert.AreEqual(0, bus.treeCount);
        Assert.IsTrue(tree.dirty);
    }

    [Test]
    public void TreeWithANullBone_IsRejectedAndReleasesItsSlice() {
        var broken = NewTree(3, "broken");
        broken.bones[2] = null;
        LogAssert.Expect(LogType.Error, "JigglePhysics: Cannot add tree with null bone at index 2 to memory bus.");

        bus.ScheduleAdd(broken);
        PumpTrees();
        var healthy = NewTree(3, "healthy");
        bus.ScheduleAdd(healthy);
        PumpTrees();

        Assert.AreEqual(1, bus.treeCount);
        Assert.AreEqual(0u, Committed(healthy.rootID).transformIndexOffset, "the rejected slice was not released");
    }

    /// <summary>
    /// Two rigs targeting one root bone produce two trees with the same rootID, which the lookup
    /// cannot tell apart — the second add leaks a slot and later removals hit the wrong tree.
    /// </summary>
    [Test]
    public void TwoTreesSharingARootBone_AreReportedAsABug() {
        var root = scene.Chain(3);
        var rig = JiggleSceneFactory.Rig(root);
        var first = JigglePhysics.CreateJiggleTree(rig, null);
        var second = JigglePhysics.CreateJiggleTree(rig, null);
        trees.Add(first);
        trees.Add(second);
        LogAssert.Expect(LogType.Error,
            $"JigglePhysics: Duplicate jiggle tree rootID {first.rootID}. Tree tracking will leak a slot and removal will target the wrong tree.");

        bus.ScheduleAdd(first);
        bus.ScheduleAdd(second);
        PumpTrees();

        Assert.AreEqual(2, bus.treeCount);
    }

    // ----------------------------------------------------------- join storms

    [Test]
    public void JoinStorm_EveryRigCommitsWithADisjointTransformRange() {
        const int rigCount = 60;
        var added = new List<JiggleTree>();
        for (int i = 0; i < rigCount; i++) {
            var tree = NewTree(3, $"storm{i}_");
            added.Add(tree);
            bus.ScheduleAdd(tree);
        }

        PumpTrees();

        Assert.AreEqual(rigCount, bus.treeCount);
        var occupied = new HashSet<uint>();
        for (int i = 0; i < added.Count; i++) {
            var data = Committed(added[i].rootID);
            for (uint slot = data.transformIndexOffset; slot < data.transformIndexOffset + data.pointCount; slot++) {
                Assert.IsTrue(occupied.Add(slot), $"transform slot {slot} was handed out twice");
            }
        }
        Assert.AreEqual(rigCount * 5, occupied.Count);
    }

    /// <summary>
    /// Growing past the initial tree capacity costs one extra commit — the resize bails out early
    /// and leaves the batch queued — but nothing may be dropped on the way through.
    /// </summary>
    [Test]
    public void JoinStorm_BeyondTheInitialTreeCapacity_GrowsAndStillCommitsEveryRig() {
        const int rigCount = 513;
        var capacityBefore = bus.treeCapacity;
        for (int i = 0; i < rigCount; i++) {
            var root = scene.Spawn($"solo{i}");
            var tree = JigglePhysics.CreateJiggleTree(JiggleSceneFactory.Rig(root), null);
            trees.Add(tree);
            bus.ScheduleAdd(tree);
        }

        PumpTrees();

        Assert.AreEqual(512, capacityBefore);
        Assert.Greater(bus.treeCapacity, capacityBefore);
        Assert.AreEqual(rigCount, bus.treeCount);
        Assert.AreEqual(rigCount * 3, bus.transformCount);
    }

    [Test]
    public void LeaveStorm_RemovingEveryRigEmptiesTheTreeList() {
        const int rigCount = 40;
        var added = new List<JiggleTree>();
        for (int i = 0; i < rigCount; i++) {
            var tree = NewTree(3, $"leave{i}_");
            added.Add(tree);
            bus.ScheduleAdd(tree);
        }
        PumpTrees();

        for (int i = 0; i < added.Count; i++) {
            bus.ScheduleRemove(added[i]);
        }
        PumpTrees();

        Assert.AreEqual(0, bus.treeCount);
        for (int i = 0; i < bus.transformCount; i++) {
            Assert.IsTrue(bus.simulationOutputPoseData[i].pose.isVirtual, $"slot {i} still writes back to a dead rig");
        }
    }

    [Test]
    public void InterleavedJoinsAndLeaves_KeepTheTreeListConsistent() {
        var resident = new List<JiggleTree>();
        for (int round = 0; round < 6; round++) {
            var joining = NewTree(3, $"round{round}_");
            bus.ScheduleAdd(joining);
            resident.Add(joining);
            if (resident.Count > 3) {
                bus.ScheduleRemove(resident[0]);
                resident.RemoveAt(0);
            }
            PumpTrees();
        }

        Assert.AreEqual(resident.Count, bus.treeCount);
        for (int i = 0; i < resident.Count; i++) {
            Assert.IsTrue(IsCommitted(resident[i].rootID), $"resident rig {i} went missing");
        }
    }

    [Test]
    public void CommitTrees_WithASmallBatchSize_SpansMultipleFrames() {
        JiggleMemoryBus.SetTransformAccessBatchSize(1);
        var tree = NewTree();
        bus.ScheduleAdd(tree);

        bus.CommitTrees();
        bus.CommitTrees();
        bus.CommitTrees();
        var partway = bus.treeCount;
        PumpTrees();

        Assert.AreEqual(0, partway, "a one-per-frame slice cannot finish five transforms in three calls");
        Assert.AreEqual(1, bus.treeCount);
    }

    // ------------------------------------------------------------- teleports

    /// <summary>
    /// A rig teleported before it has ever committed has no slice to shift, so the delta is folded
    /// straight into the tree — otherwise a rig that spawns and teleports in the same frame snaps
    /// back to the origin on its first simulated step.
    /// </summary>
    [Test]
    public void Teleport_BeforeTheTreeIsCommitted_MovesTheTreeDirectly() {
        var tree = NewTree();
        var before = tree.points[1].position;

        bus.ScheduleTeleport(tree, new float3(10f, 0f, 0f));

        JiggleAssert.AreEqual(before + new float3(10f, 0f, 0f), tree.points[1].position, Tolerance);
    }

    [Test]
    public void Teleport_AfterCommit_IsDeferredUntilTheTeleportsAreApplied() {
        var tree = NewTree();
        bus.ScheduleAdd(tree);
        PumpTrees();
        var offset = (int)Committed(tree.rootID).transformIndexOffset;
        var before = bus.simulateInputPoses[offset + 1].position;

        bus.ScheduleTeleport(tree, new float3(10f, 0f, 0f));
        var deferred = bus.simulateInputPoses[offset + 1].position;
        bus.ApplyPendingTeleports();

        JiggleAssert.AreEqual(before, deferred, Tolerance);
        JiggleAssert.AreEqual(before + new float3(10f, 0f, 0f), bus.simulateInputPoses[offset + 1].position, Tolerance);
    }

    [Test]
    public void Teleport_ShiftsEveryWorldSpaceBufferButNotTheRestPose() {
        var tree = NewTree();
        bus.ScheduleAdd(tree);
        PumpTrees();
        var offset = (int)Committed(tree.rootID).transformIndexOffset;
        var slot = offset + 2;
        var delta = new float3(0f, 0f, 7f);
        var inputCurrent = bus.inputPosesCurrent[slot].position;
        var inputPrevious = bus.inputPosesPrevious[slot].position;
        var interpolated = bus.interpolationOutputPoses[slot].position;
        var rootOutput = bus.rootOutputPositions[slot];
        var simulated = bus.simulationOutputPoseData[slot].pose.position;
        var simulatedRoot = bus.simulationOutputPoseData[slot].rootPosition;
        var rest = bus.restPoseTransforms[slot].position;

        bus.ScheduleTeleport(tree, delta);
        bus.ApplyPendingTeleports();

        JiggleAssert.AreEqual(inputCurrent + delta, bus.inputPosesCurrent[slot].position, Tolerance);
        JiggleAssert.AreEqual(inputPrevious + delta, bus.inputPosesPrevious[slot].position, Tolerance);
        JiggleAssert.AreEqual(interpolated + delta, bus.interpolationOutputPoses[slot].position, Tolerance);
        JiggleAssert.AreEqual(rootOutput + delta, bus.rootOutputPositions[slot], Tolerance);
        JiggleAssert.AreEqual(simulated + delta, bus.simulationOutputPoseData[slot].pose.position, Tolerance);
        JiggleAssert.AreEqual(simulatedRoot + delta, bus.simulationOutputPoseData[slot].rootPosition, Tolerance);
        JiggleAssert.AreEqual(rest, bus.restPoseTransforms[slot].position, Tolerance);
    }

    [Test]
    public void Teleport_AlsoShiftsTheSimulatedPoints() {
        var tree = NewTree();
        bus.ScheduleAdd(tree);
        PumpTrees();
        var before = Committed(tree.rootID).points[1].position;

        bus.ScheduleTeleport(tree, new float3(0f, 5f, 0f));
        bus.ApplyPendingTeleports();

        JiggleAssert.AreEqual(before + new float3(0f, 5f, 0f), Committed(tree.rootID).points[1].position, Tolerance);
    }

    [Test]
    public void Teleport_ScheduledTwiceInAFrame_Accumulates() {
        var tree = NewTree();
        bus.ScheduleAdd(tree);
        PumpTrees();
        var offset = (int)Committed(tree.rootID).transformIndexOffset;
        var before = bus.simulateInputPoses[offset + 1].position;

        bus.ScheduleTeleport(tree, new float3(1f, 0f, 0f));
        bus.ScheduleTeleport(tree, new float3(0f, 2f, 0f));
        bus.ApplyPendingTeleports();

        JiggleAssert.AreEqual(before + new float3(1f, 2f, 0f), bus.simulateInputPoses[offset + 1].position, Tolerance);
    }

    [Test]
    public void ApplyPendingTeleports_DrainsTheQueue() {
        var tree = NewTree();
        bus.ScheduleAdd(tree);
        PumpTrees();
        var offset = (int)Committed(tree.rootID).transformIndexOffset;

        bus.ScheduleTeleport(tree, new float3(1f, 0f, 0f));
        bus.ApplyPendingTeleports();
        var once = bus.simulateInputPoses[offset + 1].position;
        bus.ApplyPendingTeleports();

        JiggleAssert.AreEqual(once, bus.simulateInputPoses[offset + 1].position, Tolerance);
    }

    [Test]
    public void Teleport_OfANullTree_IsIgnored() {
        Assert.DoesNotThrow(() => bus.ScheduleTeleport(null, new float3(1f, 1f, 1f)));
    }

    // -------------------------------------------------------- scene colliders

    [Test]
    public void SceneCollider_IsCommittedIntoASlot() {
        var transform = scene.Spawn("worldCollider");

        bus.ScheduleAdd(JiggleSceneFactory.SphereCollider(transform, 0.5f));
        PumpColliders();

        Assert.AreEqual(1, bus.sceneColliderCount);
        Assert.IsTrue(bus.sceneColliders[0].enabled);
        Assert.AreEqual(0.5f, bus.sceneColliders[0].radius, Tolerance);
    }

    [Test]
    public void SceneCollider_SchedulingTheSameTransformTwice_TakesOneSlot() {
        var transform = scene.Spawn("worldCollider");

        bus.ScheduleAdd(JiggleSceneFactory.SphereCollider(transform));
        bus.ScheduleAdd(JiggleSceneFactory.SphereCollider(transform));
        PumpColliders();

        Assert.AreEqual(1, bus.sceneColliderCount);
    }

    [Test]
    public void SceneCollider_RemovedBeforeCommit_NeverTakesASlot() {
        var transform = scene.Spawn("worldCollider");
        var collider = JiggleSceneFactory.SphereCollider(transform);

        bus.ScheduleAdd(collider);
        bus.ScheduleRemove(collider);
        PumpColliders();

        Assert.AreEqual(0, bus.sceneColliderCount);
    }

    /// <summary>
    /// Re-registering a collider that is already committed has to refresh its slot rather than take
    /// a second one: a fresh allocation orphans the first slot, which stays enabled forever with no
    /// transform mapping left to remove it by.
    /// </summary>
    [Test]
    public void SceneCollider_ReAddedAfterCommit_RefreshesItsSlotInPlace() {
        var transform = scene.Spawn("worldCollider");
        bus.ScheduleAdd(JiggleSceneFactory.SphereCollider(transform, 0.5f));
        PumpColliders();

        bus.ScheduleAdd(JiggleSceneFactory.SphereCollider(transform, 1.25f));
        PumpColliders();

        Assert.AreEqual(1, bus.sceneColliderCount);
        Assert.AreEqual(1.25f, bus.sceneColliders[0].radius, Tolerance);
    }

    [Test]
    public void SceneCollider_Removed_IsDisabledRatherThanCompactedAway() {
        var transform = scene.Spawn("worldCollider");
        var collider = JiggleSceneFactory.SphereCollider(transform);
        bus.ScheduleAdd(collider);
        PumpColliders();

        bus.ScheduleRemove(collider);
        PumpColliders();

        Assert.AreEqual(1, bus.sceneColliderCount, "the high water mark stays");
        Assert.IsFalse(bus.sceneColliders[0].enabled);
    }

    [Test]
    public void SceneCollider_RemovedSlotIsReused() {
        var first = scene.Spawn("first");
        var second = scene.Spawn("second");
        var collider = JiggleSceneFactory.SphereCollider(first);
        bus.ScheduleAdd(collider);
        PumpColliders();
        bus.ScheduleRemove(collider);
        PumpColliders();

        bus.ScheduleAdd(JiggleSceneFactory.SphereCollider(second, 2f));
        PumpColliders();

        Assert.AreEqual(1, bus.sceneColliderCount);
        Assert.AreEqual(2f, bus.sceneColliders[0].radius, Tolerance);
        Assert.IsTrue(bus.sceneColliders[0].enabled);
    }

    [Test]
    public void ScheduleAddBatch_DedupesWithinTheBatchAndAgainstPendingAdds() {
        var shared = scene.Spawn("shared");
        var other = scene.Spawn("other");
        bus.ScheduleAdd(JiggleSceneFactory.SphereCollider(shared));

        bus.ScheduleAddBatch(new List<JiggleColliderSerializable> {
            JiggleSceneFactory.SphereCollider(shared),
            JiggleSceneFactory.SphereCollider(other),
            JiggleSceneFactory.SphereCollider(other),
        });
        PumpColliders();

        Assert.AreEqual(2, bus.sceneColliderCount);
    }

    [Test]
    public void ScheduleRemoveBatch_CancelsPendingAddsAndQueuesTheRest() {
        var committed = scene.Spawn("committed");
        var pending = scene.Spawn("pending");
        var committedCollider = JiggleSceneFactory.SphereCollider(committed);
        bus.ScheduleAdd(committedCollider);
        PumpColliders();
        var pendingCollider = JiggleSceneFactory.SphereCollider(pending);
        bus.ScheduleAdd(pendingCollider);

        bus.ScheduleRemoveBatch(new List<JiggleColliderSerializable> { committedCollider, pendingCollider });
        PumpColliders();

        Assert.IsFalse(bus.sceneColliders[0].enabled, "the committed collider was not removed");
        Assert.AreEqual(1, bus.sceneColliderCount, "the pending collider should never have taken a slot");
    }

    /// <summary>
    /// The managed mirror is the writer for scene colliders, so it has to retire slots whose
    /// transform died without a matching remove. Otherwise the commit copies the stale entry back
    /// over the read job's self-heal and the collider keeps colliding from wherever it last was.
    /// </summary>
    [Test]
    public void SceneCollider_WhoseTransformWasDestroyed_IsRetiredOnTheNextCommit() {
        var doomed = scene.Spawn("doomed");
        bus.ScheduleAdd(JiggleSceneFactory.SphereCollider(doomed));
        PumpColliders();
        Object.DestroyImmediate(doomed.gameObject);

        bus.ScheduleAdd(JiggleSceneFactory.SphereCollider(scene.Spawn("replacement")));
        PumpColliders();

        Assert.IsFalse(bus.sceneColliders[0].enabled);
        Assert.IsTrue(bus.sceneColliders[1].enabled);
    }

    // ----------------------------------------------------- personal colliders

    [Test]
    public void PersonalColliders_AreCopiedIntoTheTreesOwnRange() {
        var root = scene.Chain(3);
        var rig = JiggleSceneFactory.Rig(root);
        rig.jiggleColliders = new[] { JiggleSceneFactory.SphereCollider(scene.Spawn("chest"), 0.4f) };
        var tree = JigglePhysics.CreateJiggleTree(rig, null);
        trees.Add(tree);

        bus.ScheduleAdd(tree);
        PumpTrees();

        var data = Committed(tree.rootID);
        Assert.AreEqual(1u, data.colliderCount);
        Assert.IsTrue(bus.personalColliders[(int)data.colliderIndexOffset].enabled);
        Assert.AreEqual(0.4f, bus.personalColliders[(int)data.colliderIndexOffset].radius, Tolerance);
    }

    [Test]
    public void RemovingATree_DisablesItsPersonalColliders() {
        var root = scene.Chain(3);
        var rig = JiggleSceneFactory.Rig(root);
        rig.jiggleColliders = new[] { JiggleSceneFactory.SphereCollider(scene.Spawn("chest"), 0.4f) };
        var tree = JigglePhysics.CreateJiggleTree(rig, null);
        trees.Add(tree);
        bus.ScheduleAdd(tree);
        PumpTrees();
        var colliderSlot = (int)Committed(tree.rootID).colliderIndexOffset;

        bus.ScheduleRemove(tree);
        PumpTrees();

        Assert.IsFalse(bus.personalColliders[colliderSlot].enabled);
    }

    // -------------------------------------------------------------- plumbing

    [Test]
    public void RotateBuffers_CyclesTheThreePoseBuffers() {
        bus.simulationOutputPoseData[0] = new PoseData { rootSnapStrength = 1f };
        bus.interpolationCurrentPoseData[0] = new PoseData { rootSnapStrength = 2f };
        bus.interpolationPreviousPoseData[0] = new PoseData { rootSnapStrength = 3f };

        bus.RotateBuffers();

        Assert.AreEqual(2f, bus.interpolationPreviousPoseData[0].rootSnapStrength, Tolerance);
        Assert.AreEqual(1f, bus.interpolationCurrentPoseData[0].rootSnapStrength, Tolerance);
        Assert.AreEqual(3f, bus.simulationOutputPoseData[0].rootSnapStrength, Tolerance);
    }

    [Test]
    public void RotateBuffers_SwapsTheInputPoseHistory() {
        bus.inputPosesCurrent[0] = new JiggleTransform { position = new float3(1f, 0f, 0f) };
        bus.inputPosesPrevious[0] = new JiggleTransform { position = new float3(2f, 0f, 0f) };

        bus.RotateBuffers();

        JiggleAssert.AreEqual(new float3(2f, 0f, 0f), bus.inputPosesCurrent[0].position, Tolerance);
        JiggleAssert.AreEqual(new float3(1f, 0f, 0f), bus.inputPosesPrevious[0].position, Tolerance);
    }

    /// <summary>
    /// Tree regeneration is capped per flush, so a large dirty set arrives as several small batches.
    /// A commit rebuilds the whole transform list, so committing each batch would turn one
    /// structural change into as many full rebuilds as there were batches. The commit waits for the
    /// backlog instead, and everything lands in one.
    /// </summary>
    [Test]
    public void CommitTrees_WhileTheRegenerationBacklogRemains_HoldsTheCommit() {
        var tree = NewTree();
        bus.ScheduleAdd(tree);
        bus.SetTreeBacklog(true);

        PumpTrees(4);

        Assert.AreEqual(0, bus.treeCount, "the commit should wait while more dirty trees are still coming");

        bus.SetTreeBacklog(false);
        PumpTrees(8);

        Assert.AreEqual(1, bus.treeCount, "and land once the backlog has drained");
    }

    /// <summary>
    /// The safety valve. Deferring is keyed off a backlog flag the caller owns, so anything that
    /// re-dirties every flush would otherwise hold the commit forever and jiggle would silently
    /// never come online.
    /// </summary>
    [Test]
    public void CommitTrees_WithABacklogThatNeverDrains_CommitsAnyway() {
        var tree = NewTree();
        bus.ScheduleAdd(tree);
        bus.SetTreeBacklog(true);

        PumpTrees(32);

        Assert.AreEqual(1, bus.treeCount, "the deferral cap must bound how long a stuck backlog can stall the commit");
    }

    [Test]
    public void CommitTrees_WithNothingQueued_IsANoOp() {
        Assert.DoesNotThrow(() => PumpTrees(4));

        Assert.AreEqual(0, bus.treeCount);
        Assert.AreEqual(0, bus.transformCount);
    }

    [Test]
    public void CommitColliders_WithNothingQueued_IsANoOp() {
        Assert.DoesNotThrow(() => PumpColliders(4));

        Assert.AreEqual(0, bus.sceneColliderCount);
    }

    [Test]
    public void GetTrees_ReportsTheCommittedTreeCount() {
        var tree = NewTree();
        bus.ScheduleAdd(tree);
        PumpTrees();

        var published = bus.GetTrees(out var treeCount);

        Assert.AreEqual(1, treeCount);
        Assert.AreEqual(tree.rootID, published[0].rootID);
    }
}

}
