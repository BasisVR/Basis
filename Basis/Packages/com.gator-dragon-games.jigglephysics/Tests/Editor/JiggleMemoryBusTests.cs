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
    /// Drives CommitTrees until it has certainly drained. The common case lands in one call, but the
    /// rebuild fallback does a bounded slice of work per call, so callers that care about how many
    /// frames it takes count the calls themselves instead of using this.
    /// </summary>
    private void PumpTrees(int calls = 64) {
        for (int i = 0; i < calls; i++) {
            bus.CommitTrees();
        }
    }

    /// <summary>
    /// Forces the next tree commit down the sliced-rebuild path. Flipping the front access array out
    /// from under the list produces the same shape of desync a bone destroyed out of band causes —
    /// Unity drops it and the array stops mirroring the list — which is the only thing that
    /// disqualifies the in-place commit.
    /// </summary>
    private void DesyncTreeAccessArrays() {
        bus.doubleBufferTransformAccessArray.Flip();
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
    public void ScheduledAdd_IsNotVisibleUntilTheCommitRuns() {
        var tree = NewTree();
        bus.ScheduleAdd(tree);

        Assert.AreEqual(0, bus.treeCount, "queued but not committed");
        bus.CommitTrees();
        Assert.AreEqual(1, bus.treeCount, "an in-place commit lands the whole add in one call");
    }

    /// <summary>
    /// The steady-state path. Access array slots are index-stable, so a commit only has to write the
    /// slots the change touched — it does not rebuild the whole transform list across several
    /// frames, which is what made a commit cost O(every jiggle transform) per structural change.
    /// </summary>
    [Test]
    public void CommitTrees_WithTheAccessArraysMirrored_LandsInOneCallWhateverTheBatchSize() {
        JiggleMemoryBus.SetTransformAccessBatchSize(1);
        var first = NewTree();
        bus.ScheduleAdd(first);
        bus.CommitTrees();
        Assert.AreEqual(1, bus.treeCount, "the first ever commit starts mirrored (both empty)");

        var second = NewTree();
        bus.ScheduleAdd(second);
        bus.CommitTrees();

        Assert.AreEqual(2, bus.treeCount, "a one-per-frame slice budget cannot slow down an in-place commit");
        Assert.AreEqual(10, bus.transformCount);
    }

    /// <summary>
    /// An in-place commit writes the access arrays itself instead of regenerating them from the
    /// lists, so what matters is that it writes what a rebuild would have: every bone registered at
    /// the slot it was allocated, and the root array holding that tree's root bone across the slice.
    /// A drift here misaligns the parallel native buffers, which is the class of bug that reads
    /// another rig's pose.
    /// </summary>
    [Test]
    public void CommitTrees_InPlace_RegistersEveryBoneAtItsAllocatedSlot() {
        var tree = NewTree();
        bus.ScheduleAdd(tree);
        bus.CommitTrees();

        var offset = (int)Committed(tree.rootID).transformIndexOffset;
        var access = bus.GetTransformAccessArray();
        var rootAccess = bus.GetTransformRootAccessArray();

        Assert.AreEqual(bus.transformCount, access.length, "the access array must mirror the list it was written from");
        Assert.AreEqual(bus.transformCount, rootAccess.length);
        for (int i = 0; i < tree.bones.Length; i++) {
            Assert.AreSame(tree.bones[i], access[offset + i], $"slot {i}");
            Assert.AreSame(tree.bones[0], rootAccess[offset + i], $"root slot {i}");
        }
    }

    /// <summary>
    /// The other half: a removed slice has to stop pointing at the departed rig's bones in the same
    /// commit, or the read jobs keep sampling an avatar that logically left.
    /// </summary>
    [Test]
    public void CommitTrees_InPlace_ParksDummiesInARemovedTreesSlots() {
        var tree = NewTree();
        bus.ScheduleAdd(tree);
        bus.CommitTrees();
        var offset = (int)Committed(tree.rootID).transformIndexOffset;

        bus.ScheduleRemove(tree);
        bus.CommitTrees();

        var access = bus.GetTransformAccessArray();
        var rootAccess = bus.GetTransformRootAccessArray();
        for (int i = 0; i < 5; i++) {
            Assert.AreSame(JiggleMemoryBus.GetDummyTransform(offset + i), access[offset + i], $"slot {i}");
            Assert.AreSame(JiggleMemoryBus.GetDummyTransform(offset + i), rootAccess[offset + i], $"root slot {i}");
        }
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

    /// <summary>
    /// A rig can be queued for removal twice inside one commit window: the segment dirties itself
    /// (which schedules a remove) and then its bones die before the commit runs, so the dead-segment
    /// prune schedules another. rootIDToTreeIndex still resolves the tree for both, so the slice
    /// would be freed twice and then handed to two different trees at once.
    /// </summary>
    [Test]
    public void RemoveScheduledTwiceBeforeTheCommit_FreesTheSliceOnce() {
        var tree = NewTree(3, "a");
        bus.ScheduleAdd(tree);
        PumpTrees();

        bus.ScheduleRemove(tree);
        bus.ScheduleRemove(tree);
        PumpTrees();

        Assert.AreEqual(0, bus.treeCount);

        var first = NewTree(3, "b");
        var second = NewTree(3, "c");
        bus.ScheduleAdd(first);
        bus.ScheduleAdd(second);
        PumpTrees();

        var firstStart = Committed(first.rootID).transformIndexOffset;
        var firstEnd = firstStart + Committed(first.rootID).pointCount;
        var secondStart = Committed(second.rootID).transformIndexOffset;
        var secondEnd = secondStart + Committed(second.rootID).pointCount;
        Assert.IsTrue(firstEnd <= secondStart || secondEnd <= firstStart,
            $"slices overlap: [{firstStart},{firstEnd}) and [{secondStart},{secondEnd})");
    }

    /// <summary>
    /// A bone destroyed while still enrolled makes Unity drop it from the access array and shift
    /// every later slot, while the pose buffers keep the old slot indexing — so jiggle would pose
    /// one avatar's bones from another's tree. The consumer refuses to schedule while that is true,
    /// which makes reaching the rebuild mandatory: it used to be reachable only through a pending
    /// command, so a bone dying with no structural change behind it would have left jiggle off for
    /// the rest of the session.
    /// </summary>
    [Test]
    public void AccessArraysDesyncedWithNothingQueued_StillRealign() {
        var tree = NewTree();
        bus.ScheduleAdd(tree);
        PumpTrees();
        Assert.IsFalse(bus.GetAccessArraysDesynced(), "a settled commit leaves the arrays mirrored");

        DesyncTreeAccessArrays();
        Assert.IsTrue(bus.GetAccessArraysDesynced(), "front array no longer matches the list");

        PumpTrees();

        Assert.IsFalse(bus.GetAccessArraysDesynced(), "the rebuild must not depend on a queued command");
        Assert.IsTrue(IsCommitted(tree.rootID), "realigning must not drop the live tree");
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

    /// <summary>
    /// The fallback: once the front access array stops mirroring its list the in-place write has
    /// nowhere safe to land, so the commit rebuilds from the list a bounded slice at a time. That
    /// rebuild is still what keeps a mass structural change from spiking one frame.
    /// </summary>
    [Test]
    public void CommitTrees_WhenTheAccessArraysDesync_RebuildsASliceAtATime() {
        var first = NewTree();
        bus.ScheduleAdd(first);
        bus.CommitTrees();

        JiggleMemoryBus.SetTransformAccessBatchSize(1);
        DesyncTreeAccessArrays();
        var second = NewTree();
        bus.ScheduleAdd(second);

        bus.CommitTrees();
        bus.CommitTrees();
        bus.CommitTrees();
        var partway = bus.treeCount;
        PumpTrees();

        Assert.AreEqual(1, partway, "a one-per-frame slice cannot rebuild ten transforms in three calls");
        Assert.AreEqual(2, bus.treeCount, "and the rebuild still converges on the same state");
        Assert.AreEqual(10, bus.transformCount);
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

    // ------------------------------------------------- lossy scale on demand

    /// <summary>
    /// The read-reset job only fetches a bone's lossy scale when its slot asks for it, because Cache
    /// consumes scale solely as `collisionRadius * averageScale` — zero radius, zero use. A default
    /// rig does not collide, so it must not be paying for a localToWorldMatrix per bone per frame.
    /// </summary>
    [Test]
    public void CommittedSlots_DoNotAskForScale_WhenTheRigCannotCollide() {
        var tree = NewTree();
        for (int i = 0; i < tree.parameters.Length; i++) {
            Assert.AreEqual(0f, tree.parameters[i].collisionRadius, "fixture assumption: the default rig has no collision radius");
        }

        bus.ScheduleAdd(tree);
        PumpTrees();

        var data = Committed(tree.rootID);
        for (int i = 0; i < data.pointCount; i++) {
            var slot = (int)data.transformIndexOffset + i;
            Assert.IsFalse(bus.inputPosesCurrent[slot].wantsScale, $"slot {i}");
            Assert.IsFalse(bus.simulateInputPoses[slot].wantsScale, $"slot {i}");
        }
    }

    [Test]
    public void CommittedSlots_AskForScale_WhenAnyPointHasACollisionRadius() {
        var tree = NewTree();
        tree.parameters[1].collisionRadius = 0.05f;

        bus.ScheduleAdd(tree);
        PumpTrees();

        var data = Committed(tree.rootID);
        for (int i = 0; i < data.pointCount; i++) {
            var slot = (int)data.transformIndexOffset + i;
            Assert.IsTrue(bus.inputPosesCurrent[slot].wantsScale, $"slot {i}");
        }
    }

    /// <summary>
    /// The hole this flag could fall into: parameters pushed outside a commit can raise
    /// collisionRadius above zero after the slots were already stamped. The tree latches on that
    /// push and every slot it owns goes back to reading scale.
    /// </summary>
    [Test]
    public void MarkAlwaysReadScale_TurnsTheScaleReadBackOn() {
        var tree = NewTree();
        bus.ScheduleAdd(tree);
        PumpTrees();
        var data = Committed(tree.rootID);
        Assert.IsFalse(bus.inputPosesCurrent[(int)data.transformIndexOffset].wantsScale, "precondition");

        bus.MarkAlwaysReadScale(tree);

        for (int i = 0; i < data.pointCount; i++) {
            var slot = (int)data.transformIndexOffset + i;
            Assert.IsTrue(bus.inputPosesCurrent[slot].wantsScale, $"slot {i}");
            Assert.IsTrue(bus.inputPosesPrevious[slot].wantsScale, $"slot {i}");
            Assert.IsTrue(bus.simulateInputPoses[slot].wantsScale, $"slot {i}");
        }
    }

    /// <summary>A tree that has ever pushed parameters keeps asking for scale on later commits too.</summary>
    [Test]
    public void ATreeThatPushedParameters_KeepsAskingForScale_AfterARecommit() {
        var tree = NewTree();
        tree.SetParameters(new List<JigglePointParameters>(tree.parameters));
        Assert.IsTrue(tree.alwaysReadScale, "SetParameters should latch the tree");

        bus.ScheduleAdd(tree);
        PumpTrees();

        var data = Committed(tree.rootID);
        Assert.IsTrue(bus.inputPosesCurrent[(int)data.transformIndexOffset].wantsScale);
    }

    // ------------------------------------------- keeping the access arrays clean

    /// <summary>
    /// A regenerating rig re-commits the same bones onto the same slots, and that has to leave the
    /// front access arrays untouched. Not a cosmetic point: any write at all makes Unity rebuild the
    /// array's batch layout on the next Schedule, at O(whole array) — measured 0.60ms over 32k bones
    /// against 0.096ms for a clean one, paid by every transform job scheduled that frame. It is why
    /// culling avatars did not reduce the scheduling cost.
    /// </summary>
    [Test]
    public void RecommittingATreeUnchanged_DoesNotTouchTheFrontAccessArrays() {
        var tree = NewTree(4);
        bus.ScheduleAdd(tree);
        PumpTrees();
        bus.doubleBufferTransformAccessArray.ResetFrontWriteCount();
        bus.doubleBufferTransformRootAccessArray.ResetFrontWriteCount();

        // What tree regeneration queues: a remove and an add of the same tree, same bones.
        bus.ScheduleRemove(tree);
        bus.ScheduleAdd(tree);
        PumpTrees();

        Assert.AreEqual(0, bus.doubleBufferTransformAccessArray.FrontWriteCount,
            "an unchanged re-commit dirtied the transform access array");
        Assert.AreEqual(0, bus.doubleBufferTransformRootAccessArray.FrontWriteCount,
            "an unchanged re-commit dirtied the root access array");
        Assert.IsTrue(IsCommitted(tree.rootID), "the tree should still be resident");
    }

    /// <summary>The flip side: a commit that really does change a slot still writes it.</summary>
    [Test]
    public void CommittingADifferentTree_DoesTouchTheFrontAccessArrays() {
        var first = NewTree(4, "first");
        bus.ScheduleAdd(first);
        PumpTrees();
        bus.doubleBufferTransformAccessArray.ResetFrontWriteCount();

        var second = NewTree(4, "second");
        bus.ScheduleRemove(first);
        bus.ScheduleAdd(second);
        PumpTrees();

        Assert.Greater(bus.doubleBufferTransformAccessArray.FrontWriteCount, 0,
            "swapping one rig for another has to reach the access array");
        var access = bus.GetTransformAccessArray();
        var data = Committed(second.rootID);
        for (int i = 0; i < data.pointCount; i++) {
            var slot = (int)data.transformIndexOffset + i;
            Assert.AreSame(second.bones[i], access[slot], $"slot {i}");
        }
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
    /// Which slot the replacement lands in is not part of that contract — the reclaim pass hands
    /// the dead index straight back to the allocator, so assert on what survives, not on where.
    /// </summary>
    [Test]
    public void SceneCollider_WhoseTransformWasDestroyed_IsRetiredOnTheNextCommit() {
        var doomed = scene.Spawn("doomed");
        bus.ScheduleAdd(JiggleSceneFactory.SphereCollider(doomed));
        PumpColliders();
        Object.DestroyImmediate(doomed.gameObject);

        bus.ScheduleAdd(JiggleSceneFactory.SphereCollider(scene.Spawn("replacement")));
        PumpColliders();

        int enabled = 0;
        for (int i = 0; i < bus.sceneColliderCount; i++) {
            if (bus.sceneColliders[i].enabled) {
                enabled++;
            }
        }
        Assert.AreEqual(1, enabled, "the dead collider must not keep colliding alongside the replacement");
    }

    /// <summary>
    /// The distance LOD drops and re-registers the same colliders as avatars cross a threshold. When
    /// that nets out to the slot it already held, the scene collider access array must stay clean —
    /// same reason as the tree arrays: any write costs an O(whole array) rebuild on the next Schedule.
    /// </summary>
    [Test]
    public void ReRegisteringASceneCollider_InOneCommit_DoesNotTouchTheFrontAccessArray() {
        var transform = scene.Spawn("worldCollider");
        var collider = JiggleSceneFactory.SphereCollider(transform);
        bus.ScheduleAdd(collider);
        PumpColliders();
        bus.doubleBufferSceneColliderTransformAccessArray.ResetFrontWriteCount();

        bus.ScheduleRemove(collider);
        bus.ScheduleAdd(collider);
        PumpColliders();

        Assert.AreEqual(0, bus.doubleBufferSceneColliderTransformAccessArray.FrontWriteCount,
            "a collider that landed back on its own slot dirtied the access array");
        Assert.AreEqual(1, bus.sceneColliderCount);
        Assert.IsTrue(bus.sceneColliders[0].enabled, "it should still be registered");
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
    /// A rebuilding commit redoes the whole transform list, so committing each batch would turn one
    /// structural change into as many full rebuilds as there were batches. A rebuilding commit waits
    /// for the backlog instead, and everything lands in one.
    /// </summary>
    [Test]
    public void CommitTrees_RebuildingWhileTheRegenerationBacklogRemains_HoldsTheCommit() {
        var first = NewTree();
        bus.ScheduleAdd(first);
        bus.CommitTrees();
        DesyncTreeAccessArrays();

        var second = NewTree();
        bus.ScheduleAdd(second);
        bus.SetTreeBacklog(true);

        PumpTrees(4);

        Assert.AreEqual(1, bus.treeCount, "the commit should wait while more dirty trees are still coming");

        bus.SetTreeBacklog(false);
        PumpTrees();

        Assert.AreEqual(2, bus.treeCount, "and land once the backlog has drained");
    }

    /// <summary>
    /// An in-place commit costs what the change costs, not what the whole transform list costs, so
    /// there is nothing to coalesce — holding it behind the backlog would only delay rigs coming
    /// online.
    /// </summary>
    [Test]
    public void CommitTrees_InPlaceWithABacklog_DoesNotHoldTheCommit() {
        var tree = NewTree();
        bus.ScheduleAdd(tree);
        bus.SetTreeBacklog(true);

        bus.CommitTrees();

        Assert.AreEqual(1, bus.treeCount);
    }

    /// <summary>
    /// The safety valve. Deferring is keyed off a backlog flag the caller owns, so anything that
    /// re-dirties every flush would otherwise hold a rebuilding commit forever and jiggle would
    /// silently never come online.
    /// </summary>
    [Test]
    public void CommitTrees_WithABacklogThatNeverDrains_CommitsAnyway() {
        var first = NewTree();
        bus.ScheduleAdd(first);
        bus.CommitTrees();
        DesyncTreeAccessArrays();

        var second = NewTree();
        bus.ScheduleAdd(second);
        bus.SetTreeBacklog(true);

        PumpTrees(32);

        Assert.AreEqual(2, bus.treeCount, "the deferral cap must bound how long a stuck backlog can stall the commit");
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

    // ---------------------------------------------------------------- grab constraints

    [Test]
    public void GrabConstraints_DrainIntoTheMapForACommittedTree() {
        var tree = NewTree();
        bus.ScheduleAdd(tree);
        PumpTrees();

        bus.SetGrabConstraints(new[] {
            new JiggleGrabConstraint { rootID = tree.rootID, pointIndex = 2, targetPosition = new float3(1f, 2f, 3f), strength = 1f },
        }, 1);
        bus.ApplyPendingGrabConstraints();

        Assert.AreEqual(1, bus.grabConstraintCount);
        Assert.IsTrue(bus.grabConstraints.TryGetFirstValue(tree.rootID, out var stored, out _));
        Assert.AreEqual(2, stored.pointIndex);
        JiggleAssert.AreEqual(new float3(1f, 2f, 3f), stored.targetPosition, Tolerance);
    }

    [Test]
    public void GrabConstraints_ForUnknownRootIDs_ArePrefilteredAtTheDrain() {
        var tree = NewTree();
        bus.ScheduleAdd(tree);
        PumpTrees();

        bus.SetGrabConstraints(new[] {
            new JiggleGrabConstraint { rootID = tree.rootID, pointIndex = 2, strength = 1f },
            new JiggleGrabConstraint { rootID = 987654, pointIndex = 2, strength = 1f },
        }, 2);
        bus.ApplyPendingGrabConstraints();

        Assert.AreEqual(1, bus.grabConstraintCount);
        Assert.IsFalse(bus.grabConstraints.TryGetFirstValue(987654, out _, out _));
    }

    [Test]
    public void GrabConstraints_ReplaceWholesale_AndClearWithCountZero() {
        var tree = NewTree();
        bus.ScheduleAdd(tree);
        PumpTrees();

        bus.SetGrabConstraints(new[] {
            new JiggleGrabConstraint { rootID = tree.rootID, pointIndex = 2, strength = 1f },
        }, 1);
        bus.ApplyPendingGrabConstraints();
        Assert.AreEqual(1, bus.grabConstraintCount);

        bus.SetGrabConstraints(System.Array.Empty<JiggleGrabConstraint>(), 0);
        bus.ApplyPendingGrabConstraints();

        Assert.AreEqual(0, bus.grabConstraintCount);
        Assert.IsFalse(bus.grabConstraints.TryGetFirstValue(tree.rootID, out _, out _));
    }

    [Test]
    public void GrabConstraints_DrainWithoutNewStaging_IsANoOp() {
        var tree = NewTree();
        bus.ScheduleAdd(tree);
        PumpTrees();

        bus.SetGrabConstraints(new[] {
            new JiggleGrabConstraint { rootID = tree.rootID, pointIndex = 2, strength = 1f },
        }, 1);
        bus.ApplyPendingGrabConstraints();
        bus.ApplyPendingGrabConstraints();

        Assert.AreEqual(1, bus.grabConstraintCount);
        Assert.IsTrue(bus.grabConstraints.TryGetFirstValue(tree.rootID, out _, out _));
    }

    /// <summary>
    /// Removing a rig parks a pooled dummy in every slot it vacated, and the fragmenter puts a rig
    /// wherever there is room, so in a populated world the first removal asks for a high slot index.
    /// The pool used to fill densely up to whatever it was asked for, which meant that one removal
    /// created every GameObject below it as well — thousands of them, inside the commit, for the
    /// handful of slots that actually needed a placeholder.
    /// </summary>
    [Test]
    public void GetDummyTransform_AtAHighSlot_CreatesOnlyThatSlot() {
        // Its own range, well clear of the one JiggleCommitPerformanceSimulation seeds: the pool is
        // process wide and latched, so a slot another fixture already asked for proves nothing here.
        const int HighSlot = 1 << 24;
        const int Neighbour = HighSlot - 1;
        var before = JiggleRuntimeStatics.DummyTransformPoolCount;
        Assert.IsFalse(JiggleRuntimeStatics.DummyTransformPoolContains(HighSlot),
            $"the probe slot has to start unused, pool holds {before}");

        var dummy = JiggleMemoryBus.GetDummyTransform(HighSlot);

        Assert.IsNotNull(dummy);
        Assert.IsTrue(JiggleRuntimeStatics.DummyTransformPoolContains(HighSlot),
            $"the slot asked for was not pooled, pool went {before} to {JiggleRuntimeStatics.DummyTransformPoolCount}");
        Assert.IsFalse(JiggleRuntimeStatics.DummyTransformPoolContains(Neighbour),
            "the dummy pool filled densely up to the requested slot instead of creating just that one " +
            $"(pool went {before} to {JiggleRuntimeStatics.DummyTransformPoolCount})");
        Assert.AreSame(dummy, JiggleMemoryBus.GetDummyTransform(HighSlot),
            "a slot has to keep the same dummy, or an in-place write and a rebuild disagree about it");
    }

    /// <summary>
    /// JIGGLE_VALIDATE used to gate per-frame diagnostics that re-walked every tree, every point,
    /// every collider and every transform inside the commit, and that could not Burst-compile inside
    /// JiggleJobSimulate.Execute. That code is deleted, so the define buys nothing now — but it has
    /// come back in ProjectSettings.asset twice already (the asset is shared and the editor rewrites
    /// it), each time surfacing as a multi-millisecond JiggleJobs.Simulate.Commit nobody could
    /// attribute. A failure here means the define is back, and probably the gated code with it.
    /// </summary>
    [Test]
    public void JiggleValidate_IsNotDefined() {
        #if JIGGLE_VALIDATE
        Assert.Fail("JIGGLE_VALIDATE is in Scripting Define Symbols (Project Settings > Player). " +
            "It gates per-frame jiggle validation that costs whole milliseconds inside the commit " +
            "and stops JiggleJobSimulate Burst-compiling in the editor. Remove it, and check whether " +
            "the validation code came back with it.");
        #else
        Assert.Pass("JIGGLE_VALIDATE is not defined.");
        #endif
    }
}

}
