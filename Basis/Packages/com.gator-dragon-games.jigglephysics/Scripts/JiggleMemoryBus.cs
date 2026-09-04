using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;
using UnityEngine.Profiling;
using Object = UnityEngine.Object;

namespace GatorDragonGames.JigglePhysics {

public struct PoseData {
    public JiggleTransform pose;
    public float3 rootPosition;
    public float3 rootOffset;
    public float rootSnapStrength;

    public static PoseData Lerp(PoseData a, PoseData b, float t) {
        return new PoseData() {
            pose = JiggleTransform.Lerp(a.pose, b.pose, t),
            rootPosition = math.lerp(a.rootPosition, b.rootPosition, t),
            rootOffset = math.lerp(a.rootOffset, b.rootOffset, t),
            rootSnapStrength = math.lerp(a.rootSnapStrength, b.rootSnapStrength, t)
        };
    }

    public override string ToString() {
        return $"(Pose: {pose}, RootPosition: {rootPosition}, RootOffset: {rootOffset})";
    }
}

public struct JiggleRigidTeleport {
    public quaternion rotation;
    public float3 translation;

    public static JiggleRigidTeleport FromTranslation(float3 deltaPosition) {
        return new JiggleRigidTeleport {
            rotation = quaternion.identity,
            translation = deltaPosition
        };
    }

    public static JiggleRigidTeleport FromRigid(quaternion deltaRotation, float3 pivot, float3 deltaPosition) {
        return new JiggleRigidTeleport {
            rotation = deltaRotation,
            translation = pivot - math.mul(deltaRotation, pivot) + deltaPosition
        };
    }

    public JiggleRigidTeleport Then(JiggleRigidTeleport next) {
        return new JiggleRigidTeleport {
            rotation = math.normalize(math.mul(next.rotation, rotation)),
            translation = math.mul(next.rotation, translation) + next.translation
        };
    }

    public float3 Apply(float3 point) {
        return math.mul(rotation, point) + translation;
    }

    public quaternion Apply(quaternion rot) {
        return math.mul(rotation, rot);
    }

    public float3 ApplyDirection(float3 direction) {
        return math.mul(rotation, direction);
    }
}

public class JiggleMemoryBus {
    // : IContainer<JiggleTreeStruct> {
    public int treeCapacity { get; private set; }
    public int transformCapacity { get; private set; }
    // Scene colliders stay managed-authoritative: CommitColliders is their writer and copies this into
    // the native sceneColliders. Trees, the transform working set, and personal colliders are now
    // native-authoritative (mutated in place during CommitTrees), so their managed mirrors are gone.
    private JiggleCollider[] sceneColliderArray;

    private JiggleCollider[] personalColliderArrayOutput;
    private JiggleCollider[] sceneColliderArrayOutput;
    private JiggleTransform[] interpolationOutputPosesArrayOutput;
    private JiggleTreeJobData[] jiggleTreeStructsArrayOutput;

    public NativeArray<JiggleTreeJobData> jiggleTreeStructs;
    
    public NativeArray<JiggleTransform> inputPosesPrevious;
    public NativeArray<JiggleTransform> inputPosesCurrent;
    
    public NativeArray<JiggleTransform> simulateInputPoses;
    public NativeArray<JiggleTransform> restPoseTransforms;
    public NativeArray<JiggleTransform> previousLocalRestPoseTransforms;
    public NativeArray<float3> rootOutputPositions;
    public NativeArray<JiggleTransform> interpolationOutputPoses;
    public NativeArray<PoseData> simulationOutputPoseData;
    public NativeArray<PoseData> interpolationCurrentPoseData;
    public NativeArray<PoseData> interpolationPreviousPoseData;
    public NativeHashMap<int2, JiggleGridCell> broadPhaseMap;
    public NativeReference<JiggleGridCell> globalCell;

    public NativeParallelMultiHashMap<int, JiggleGrabConstraint> grabConstraints;
    public int grabConstraintCount;
    private JiggleGrabConstraint[] pendingGrabConstraints;
    private int pendingGrabConstraintCount;
    private bool pendingGrabConstraintsDirty;

    public NativeArray<JiggleCollider> personalColliders;

    public NativeArray<JiggleCollider> sceneColliders;

    public NativeArray<JiggleColliderBroadPhaseEntry> broadPhaseEntries;

    private List<Transform> transformAccessList;
    private List<Transform> transformRootAccessList;
    private List<Transform> personalColliderTransformAccessList;
    private List<Transform> sceneColliderTransformAccessList;
    // Index mirror of sceneColliderTransformAccessList (transform -> slot), kept in sync at
    // every add/remove site so collider removal is an O(1) lookup instead of List.IndexOf's
    // linear scan — that scan was O(scene colliders) per remove and O(removes * colliders)
    // across a frame under distance-LOD collider churn (~8ms in profiles).
    private Dictionary<Transform, int> sceneColliderTransformToIndex;

    public JiggleDoubleBufferTransformAccessArray doubleBufferTransformAccessArray;
    public JiggleDoubleBufferTransformAccessArray doubleBufferTransformRootAccessArray;
    public JiggleDoubleBufferTransformAccessArray doubleBufferPersonalColliderTransformAccessArray;
    public JiggleDoubleBufferTransformAccessArray doubleBufferSceneColliderTransformAccessArray;

    private struct AddRemoveCommand {
        public enum CommandType {
            Add,
            Remove
        }
        public CommandType commandType;
        public JiggleTree tree;
    }
    private List<AddRemoveCommand> pendingCommands;
    private List<JiggleTree> pendingRemoveTrees;
    private List<JiggleTree> pendingAddTrees;
    private Dictionary<int, JiggleRigidTeleport> pendingTeleports;
    // rootID -> index into jiggleTreeStructs[0..treeCount]. Makes tree lookups
    // (PreRemoveTree/RemoveTree/ApplyTeleport) O(1) instead of a linear scan, and lets
    // RemoveTree swap-with-last instead of Array.Copy-compacting. Safe because tree array
    // order is not load-bearing: every consumer iterates all trees or keys off rootID, and
    // per-tree data rides transformIndexOffset stored in the struct, not the array slot.
    private Dictionary<int, int> rootIDToTreeIndex;

    private List<JiggleTree> pendingProcessingAdds;
    private List<JiggleTree> pendingProcessingRemoves;
    // rootIDToTreeIndex still resolves a removed tree until FinishTreeCommit runs, so a rootID
    // queued for removal twice in one commit would free the same fragmenter range twice.
    private HashSet<int> preRemovedRootIDs;

    // Buffers whose owning JiggleTreeJobData was re-pointed by JiggleTree.Set while its old copy
    // may still sit in jiggleTreeStructs. That copy is only swapped out when the tree commit
    // flips, so these pointers must stay alive until the end of the flip — freeing them on the
    // next Simulate (the FreeOnComplete path) is too early and the sim job reads freed memory.
    private List<IntPtr> deferredFlipFrees;

    // Pre-commit contents of every access-array slot this commit touches, captured on first touch.
    // Writing a TransformAccessArray slot makes Unity rebuild that array's batch layout on the next
    // Schedule, and that rebuild is O(entire array): 0.60ms over 32k bones against 0.096ms for an
    // untouched array, per schedule, measured 2026-08-02. A regenerating tree normally lands back on
    // the same slots holding the same bones, so the front arrays were being dirtied every frame to
    // write values they already held. These let the commit mutate the lists freely and then write
    // back only the slots whose value actually changed, leaving an unchanged array clean.
    private readonly Dictionary<int, Transform> preCommitTransformSlots = new();
    private readonly Dictionary<int, Transform> preCommitRootSlots = new();
    private readonly Dictionary<int, Transform> preCommitPersonalColliderSlots = new();
    private readonly Dictionary<int, Transform> preCommitSceneColliderSlots = new();

    private JiggleMemoryFragmenter memoryFragmenter;

    private JiggleMemoryFragmenter personalColliderMemoryFragmenter;
    private JiggleMemoryFragmenter sceneColliderMemoryFragmenter;

    private List<JiggleColliderSerializable> pendingSceneColliderAdd;
    private List<JiggleColliderSerializable> pendingSceneColliderRemove;
    // Membership mirror of pendingSceneColliderAdd's transforms, kept in sync at
    // every add/remove/clear site so dedup is O(1). Avoids rebuilding a HashSet
    // from the whole pending list on each ScheduleAdd(Batch) — that was O(n) per
    // call and O(n^2) across a join storm (many avatars queue colliders before
    // CommitColliders' multi-frame state machine drains the list), plus a fresh
    // per-call allocation that churned GC.
    private HashSet<Transform> pendingSceneColliderAddSet;

    private bool hasWrittenData = false;

    private int preTransformCount;

    public int transformCount;
    public int treeCount;

    public int personalColliderCount;
    public int personalColliderCapacity;

    public int sceneColliderCount;
    public int sceneColliderCapacity;

    private int currentTransformAccessIndex = 0;
    private int currentRootTransformAccessIndex = 0;
    private int currentPersonalColliderTransformAccessIndex = 0;
    private int currentSceneColliderTransformAccessIndex = 0;

    // Per-tick cap on TransformAccessArray (un)registrations during a Commit. This is the
    // per-frame work bound for the sliced rebuild: lower it to shrink per-frame Commit cost
    // (more frames to finish a structural change), raise it to converge faster per frame.
    //
    // There is no optimum to find here. Total rebuild cost is flat at roughly 0.09ms per 1000
    // transforms whatever the batch: spike height scales with the batch, frames to converge scale
    // inversely, and the product does not move. Measured over 8192 transforms, batch 64 took 129
    // calls of 0.010ms and batch 8192 took 2 calls of 0.733ms, for the same 0.73ms of work. So this
    // is a frame budget rather than a tuning dial, and it can be derived instead of guessed:
    //
    //     batch ~= budgetMilliseconds * 11000
    //
    // 512 encodes about 0.06ms per frame, roughly 0.4% of a 60Hz frame. Raise it if structural
    // changes need to land sooner and the budget allows, lower it if Commit shows up in frame spikes.
    private static int transformAccessBatchSize = 512;
    public static void SetTransformAccessBatchSize(int value) => transformAccessBatchSize = Mathf.Max(1, value);

    // Tree regeneration is capped per flush, but a commit rebuilds the whole transform list — so
    // committing each partial batch turns M dirty trees into M/budget full rebuilds. Holding the
    // commit while the backlog drains coalesces them into one; the cap keeps a source that
    // re-dirties forever from stalling the commit indefinitely.
    private bool treeBacklogRemains;
    private int deferredTreeCommits;
    private const int MaxDeferredTreeCommits = 8;
    public void SetTreeBacklog(bool backlogRemains) => treeBacklogRemains = backlogRemains;

    /// <summary>
    /// Sparse on purpose: a dummy is only ever asked for by the slots a commit actually parked, and
    /// those slot indices are wherever the fragmenter happened to place the rig. A dense pool made
    /// the first removal at a high slot create every GameObject below it too — thousands of them,
    /// inside the commit, for a handful of slots that needed one.
    /// </summary>
    private static Dictionary<int, Transform> dummyTransforms;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void Init() {
        if (dummyTransforms != null) {
            foreach (Transform t in dummyTransforms.Values) {
                Object.Destroy(t.gameObject);
            }

            dummyTransforms.Clear();
        } else {
            dummyTransforms = new Dictionary<int, Transform>();
        }
    }

    public void GetColliders(out JiggleCollider[] personalColliders, out JiggleCollider[] sceneColliders,
        out int personalColliderCount, out int sceneColliderCount) {
        ReadIn(this.personalColliders, personalColliderArrayOutput, this.personalColliderCount);
        ReadIn(this.sceneColliders, sceneColliderArrayOutput, this.sceneColliderCount);
        personalColliders = personalColliderArrayOutput;
        sceneColliders = sceneColliderArrayOutput;
        personalColliderCount = this.personalColliderCount;
        sceneColliderCount = this.sceneColliderCount;
    }

    public NativeArray<JiggleCollider> GetPersonalColliders(out int personalColliderCount) {
        personalColliderCount = this.personalColliderCount;
        return personalColliders;
    }

    public NativeArray<JiggleCollider> GetSceneColliders(out int sceneColliderCount) {
        sceneColliderCount = this.sceneColliderCount;
        return sceneColliders;
    }

    public NativeArray<JiggleTransform> GetInterpolatedOutputPoses(out int poseCount) {
        poseCount = transformCount;
        return interpolationOutputPoses;
    }

    public NativeArray<JiggleTreeJobData> GetTrees(out int treeCount) {
        treeCount = this.treeCount;
        return jiggleTreeStructs;
    }

public void GetResults(out JiggleTransform[] poses, out JiggleTreeJobData[] treeJobData, out int poseCount, out int treeCount) {
        ReadIn(interpolationOutputPoses, interpolationOutputPosesArrayOutput, transformCount);
        ReadIn(jiggleTreeStructs, jiggleTreeStructsArrayOutput, this.treeCount);

        poseCount = transformCount;
        treeCount = this.treeCount;
        poses = interpolationOutputPosesArrayOutput;
        treeJobData = jiggleTreeStructsArrayOutput;
    }

    /// <summary>Mirrors the substitution <see cref="JiggleDoubleBufferTransformAccessArray.GenerateNewAccessArrays"/>
    /// makes, so an in-place slot write lands exactly what a rebuild would have: the access lists may
    /// legally hold a null or destroyed reference, the access array may not.</summary>
    private static Transform ResolveAccessTransform(Transform transform, int index) => transform ? transform : GetDummyTransform(index);

    public static Transform GetDummyTransform(int index) {
        if (dummyTransforms.TryGetValue(index, out var existing)) {
            return existing;
        }
        Transform dummyTransform = new GameObject($"JigglePhysicsDummyTransform{index}").transform;
        // Throws outside play mode, which is enough to fail any edit mode test that reaches the
        // bus. HideAndDontSave already survives scene loads, so this only matters while playing.
        if (Application.isPlaying) {
            Object.DontDestroyOnLoad(dummyTransform.gameObject);
        }
        dummyTransform.gameObject.hideFlags = HideFlags.HideAndDontSave;
        dummyTransforms.Add(index, dummyTransform);
        return dummyTransform;
    }

    /// <summary>
    /// True when a front access array no longer mirrors its list, which happens when a registered
    /// transform is destroyed while still enrolled: Unity drops it and shifts every later slot
    /// down, while the pose buffers keep the old slot indexing. Jobs scheduled over that write one
    /// avatar's pose onto another avatar's bones. The commit realigns it, but the commit can be
    /// several frames out under a swap storm, so the pipeline has to sit out the frames in between.
    /// </summary>
    public bool GetAccessArraysDesynced() {
        return GetTreeAccessArraysDesynced() || GetSceneColliderAccessArrayDesynced();
    }

    private bool GetTreeAccessArraysDesynced() {
        return doubleBufferTransformAccessArray.FrontLength != transformAccessList.Count
            || doubleBufferTransformRootAccessArray.FrontLength != transformRootAccessList.Count
            || doubleBufferPersonalColliderTransformAccessArray.FrontLength != personalColliderTransformAccessList.Count;
    }

    private bool GetSceneColliderAccessArrayDesynced() {
        return doubleBufferSceneColliderTransformAccessArray.FrontLength != sceneColliderTransformAccessList.Count;
    }

    public TransformAccessArray GetTransformAccessArray() => doubleBufferTransformAccessArray.GetTransformAccessArray();
    public TransformAccessArray GetTransformRootAccessArray() => doubleBufferTransformRootAccessArray.GetTransformAccessArray();
    public TransformAccessArray GetPersonalColliderTransformAccessArray() => doubleBufferPersonalColliderTransformAccessArray.GetTransformAccessArray();
    public TransformAccessArray GetSceneColliderTransformAccessArray() => doubleBufferSceneColliderTransformAccessArray.GetTransformAccessArray();

    public void RotateBuffers() {
        var tempPoses = interpolationPreviousPoseData;
        interpolationPreviousPoseData = interpolationCurrentPoseData;
        interpolationCurrentPoseData = simulationOutputPoseData;
        simulationOutputPoseData = tempPoses;
        (inputPosesPrevious, inputPosesCurrent) = (inputPosesCurrent, inputPosesPrevious);
    }

    private void ResizeSceneColliderCapacity(int newColliderCapacity) {
        sceneColliderMemoryFragmenter.Resize(newColliderCapacity);
        var newColliders = new JiggleCollider[newColliderCapacity];
        sceneColliderArrayOutput = new JiggleCollider[newColliderCapacity];
        if (sceneColliderArray != null) {
            System.Array.Copy(sceneColliderArray, newColliders, System.Math.Min(sceneColliderCount, sceneColliderArray.Length));
        }
        sceneColliderArray = newColliders;
        if (sceneColliders.IsCreated) {
            sceneColliders.Dispose();
        }
        sceneColliders = new NativeArray<JiggleCollider>(sceneColliderArray, Allocator.Persistent);
        if (broadPhaseEntries.IsCreated) {
            broadPhaseEntries.Dispose();
        }
        broadPhaseEntries = new NativeArray<JiggleColliderBroadPhaseEntry>(newColliderCapacity, Allocator.Persistent);
        sceneColliderCapacity = newColliderCapacity;
    }

    private void ResizePersonalColliderCapacity(int newColliderCapacity) {
        personalColliderMemoryFragmenter.Resize(newColliderCapacity);
        personalColliderArrayOutput = new JiggleCollider[newColliderCapacity];
        var newColliders = new NativeArray<JiggleCollider>(newColliderCapacity, Allocator.Persistent);
        if (personalColliders.IsCreated) {
            NativeArray<JiggleCollider>.Copy(personalColliders, newColliders, System.Math.Min(personalColliderCount, personalColliders.Length));
            personalColliders.Dispose();
        }
        personalColliders = newColliders;
        personalColliderCapacity = newColliderCapacity;
    }

    private void ResizeTransformCapacity(int newTransformCapacity) {
        memoryFragmenter.Resize(newTransformCapacity);

        var newInputPosesPrevious = new NativeArray<JiggleTransform>(newTransformCapacity, Allocator.Persistent);
        var newInputPosesCurrent = new NativeArray<JiggleTransform>(newTransformCapacity, Allocator.Persistent);
        var newSimulateInputPoses = new NativeArray<JiggleTransform>(newTransformCapacity, Allocator.Persistent);
        var newRestPoseTransforms = new NativeArray<JiggleTransform>(newTransformCapacity, Allocator.Persistent);
        var newPreviousLocalRestPoseTransforms = new NativeArray<JiggleTransform>(newTransformCapacity, Allocator.Persistent);
        var newRootOutputPositions = new NativeArray<float3>(newTransformCapacity, Allocator.Persistent);
        var newInterpolationOutputPoses = new NativeArray<JiggleTransform>(newTransformCapacity, Allocator.Persistent);
        var newSimulationOutputPoseData = new NativeArray<PoseData>(newTransformCapacity, Allocator.Persistent);
        var newInterpolationCurrentPoseData = new NativeArray<PoseData>(newTransformCapacity, Allocator.Persistent);
        var newInterpolationPreviousPoseData = new NativeArray<PoseData>(newTransformCapacity, Allocator.Persistent);

        if (simulateInputPoses.IsCreated) {
            int copyCount = System.Math.Min(transformCount, newTransformCapacity);
            NativeArray<JiggleTransform>.Copy(inputPosesPrevious, newInputPosesPrevious, copyCount);
            NativeArray<JiggleTransform>.Copy(inputPosesCurrent, newInputPosesCurrent, copyCount);
            NativeArray<JiggleTransform>.Copy(simulateInputPoses, newSimulateInputPoses, copyCount);
            NativeArray<JiggleTransform>.Copy(restPoseTransforms, newRestPoseTransforms, copyCount);
            NativeArray<JiggleTransform>.Copy(previousLocalRestPoseTransforms, newPreviousLocalRestPoseTransforms, copyCount);
            NativeArray<float3>.Copy(rootOutputPositions, newRootOutputPositions, copyCount);
            NativeArray<JiggleTransform>.Copy(interpolationOutputPoses, newInterpolationOutputPoses, copyCount);
            NativeArray<PoseData>.Copy(simulationOutputPoseData, newSimulationOutputPoseData, copyCount);
            NativeArray<PoseData>.Copy(interpolationCurrentPoseData, newInterpolationCurrentPoseData, copyCount);
            NativeArray<PoseData>.Copy(interpolationPreviousPoseData, newInterpolationPreviousPoseData, copyCount);

            inputPosesPrevious.Dispose();
            inputPosesCurrent.Dispose();
            simulateInputPoses.Dispose();
            restPoseTransforms.Dispose();
            previousLocalRestPoseTransforms.Dispose();
            rootOutputPositions.Dispose();
            interpolationOutputPoses.Dispose();
            simulationOutputPoseData.Dispose();
            interpolationCurrentPoseData.Dispose();
            interpolationPreviousPoseData.Dispose();
        }

        inputPosesPrevious = newInputPosesPrevious;
        inputPosesCurrent = newInputPosesCurrent;
        simulateInputPoses = newSimulateInputPoses;
        restPoseTransforms = newRestPoseTransforms;
        previousLocalRestPoseTransforms = newPreviousLocalRestPoseTransforms;
        rootOutputPositions = newRootOutputPositions;
        interpolationOutputPoses = newInterpolationOutputPoses;
        simulationOutputPoseData = newSimulationOutputPoseData;
        interpolationCurrentPoseData = newInterpolationCurrentPoseData;
        interpolationPreviousPoseData = newInterpolationPreviousPoseData;

        interpolationOutputPosesArrayOutput = new JiggleTransform[newTransformCapacity];

        transformCapacity = newTransformCapacity;
    }

    private void ResizeTreeCapacity(int newTreeCapacity) {
        jiggleTreeStructsArrayOutput = new JiggleTreeJobData[newTreeCapacity];

        var newJiggleTreeStructs = new NativeArray<JiggleTreeJobData>(newTreeCapacity, Allocator.Persistent);
        if (jiggleTreeStructs.IsCreated) {
            NativeArray<JiggleTreeJobData>.Copy(jiggleTreeStructs, newJiggleTreeStructs, System.Math.Min(treeCount, jiggleTreeStructs.Length));
            jiggleTreeStructs.Dispose();
        }

        jiggleTreeStructs = newJiggleTreeStructs;
        treeCapacity = newTreeCapacity;
    }

    public JiggleMemoryBus() {
        pendingCommands = new();
        pendingProcessingRemoves = new();
        pendingProcessingAdds = new();
        preRemovedRootIDs = new();
        pendingSceneColliderAdd = new();
        pendingSceneColliderRemove = new();
        pendingSceneColliderAddSet = new();
        pendingAddTrees = new();
        pendingRemoveTrees = new();
        pendingTeleports = new();
        rootIDToTreeIndex = new();
        deferredFlipFrees = new();

        memoryFragmenter = new JiggleMemoryFragmenter(4096);
        personalColliderMemoryFragmenter = new JiggleMemoryFragmenter(2048);
        sceneColliderMemoryFragmenter = new JiggleMemoryFragmenter(2048);

        ResizeSceneColliderCapacity(2048);
        ResizePersonalColliderCapacity(2048);
        ResizeTransformCapacity(4096);
        ResizeTreeCapacity(512);

        transformAccessList = new List<Transform>();
        transformRootAccessList = new List<Transform>();
        personalColliderTransformAccessList = new List<Transform>();
        sceneColliderTransformAccessList = new List<Transform>();
        sceneColliderTransformToIndex = new Dictionary<Transform, int>();
        doubleBufferTransformAccessArray = new JiggleDoubleBufferTransformAccessArray(128, "Transforms");
        doubleBufferTransformRootAccessArray = new JiggleDoubleBufferTransformAccessArray(128, "Roots");
        doubleBufferPersonalColliderTransformAccessArray = new JiggleDoubleBufferTransformAccessArray(128, "PersonalColliders");
        doubleBufferSceneColliderTransformAccessArray = new JiggleDoubleBufferTransformAccessArray(128, "SceneColliders");

        transformCount = 0;
        treeCount = 0;
        sceneColliderCount = 0;
        personalColliderCount = 0;
        broadPhaseMap = new NativeHashMap<int2, JiggleGridCell>(128, Allocator.Persistent);
        globalCell = new NativeReference<JiggleGridCell>(Allocator.Persistent);
        globalCell.Value = new JiggleGridCell(JiggleJobBroadPhase.MAX_COLLIDERS);
        grabConstraints = new NativeParallelMultiHashMap<int, JiggleGrabConstraint>(JiggleGrabConstraint.MaxTotalGrabs, Allocator.Persistent);
        grabConstraintCount = 0;
        pendingGrabConstraints = new JiggleGrabConstraint[JiggleGrabConstraint.MaxTotalGrabs];
        pendingGrabConstraintCount = 0;
        pendingGrabConstraintsDirty = false;
    }

    private void ReadIn<T>(NativeArray<T> native, T[] array, int count) where T : struct {
        NativeArray<T>.Copy(native, array, count);
    }

    // There is deliberately no per-frame allocation/NaN validation here. It used to live behind the
    // JIGGLE_VALIDATE define and re-walked every tree, every point (each one a linear scan of the
    // fragmenter's free list), every scene collider and every transform inside the commit — the
    // whole of a 6.59ms and later a 19.53ms JiggleJobs.Simulate.Commit, twice traced back to the
    // define being left on in ProjectSettings.asset. A define stored in a shared, editor-writable
    // asset is not an off switch, so the diagnostics are gone rather than gated: the pure
    // JiggleTreeJobData/JiggleSimulatedPoint/JigglePointParameters GetIsValid helpers survive as
    // test assertions, and JiggleSimulatedPoint.Sanitize() still repairs NaNs every frame.

    /// <summary>Records a slot's pre-commit value once, so the write-back can tell what really changed.</summary>
    private static void RecordSlot(Dictionary<int, Transform> record, List<Transform> list, int index) {
        record.TryAdd(index, list[index]);
    }

    // Vacating a slot parks null rather than a pooled dummy: every consumer of these lists already
    // maps a falsy slot to GetDummyTransform(index) at the point it publishes one — FlushFrontSlotWrites
    // through ResolveAccessTransform, the sliced rebuild through GenerateNewAccessArrays — and the rest
    // only read Count. Materialising the dummy here instead meant a regenerating rig, which removes and
    // re-adds onto its own slice within the same commit, minted a GameObject per bone that its own
    // re-add overwrote microseconds later. That is what dragged the pool toward one GameObject per slot
    // in the arena, and its per-slot creation is the managed allocation inside Simulate.Commit.
    private void PreRemoveTree(JiggleTree tree, bool inPlace) {
        if (!rootIDToTreeIndex.TryGetValue(tree.rootID, out var i)) return;
        var removedTree = jiggleTreeStructs[i];
        memoryFragmenter.Free((int)removedTree.transformIndexOffset, (int)removedTree.pointCount);
        for (int j = (int)removedTree.transformIndexOffset; j < removedTree.transformIndexOffset + removedTree.pointCount; j++) {
            if (inPlace) {
                RecordSlot(preCommitTransformSlots, transformAccessList, j);
                RecordSlot(preCommitRootSlots, transformRootAccessList, j);
            }
            transformAccessList[j] = null;
            transformRootAccessList[j] = null;
        }
        if (removedTree.colliderCount > 0) {
            personalColliderMemoryFragmenter.Free((int)removedTree.colliderIndexOffset, (int)removedTree.colliderCount);
        }
        for (int j = (int)removedTree.colliderIndexOffset; j < removedTree.colliderIndexOffset + removedTree.colliderCount; j++) {
            var freedCollider = personalColliders[j];
            freedCollider.enabled = false;
            personalColliders[j] = freedCollider;
            if (inPlace) {
                RecordSlot(preCommitPersonalColliderSlots, personalColliderTransformAccessList, j);
            }
            personalColliderTransformAccessList[j] = null;
        }
    }

    /// <summary>
    /// Writes the recorded slots into the front access arrays, skipping every slot whose resolved
    /// value is what it already held. A regenerating tree that lands back on its own slots with its
    /// own bones writes nothing at all, which is what keeps the array clean and its next Schedule
    /// cheap. Appends during the commit still touch the array - a genuinely new slot has to be added.
    /// </summary>
    private void FlushFrontSlotWrites() {
        foreach (var entry in preCommitTransformSlots) {
            var index = entry.Key;
            var resolved = ResolveAccessTransform(transformAccessList[index], index);
            if (!ReferenceEquals(resolved, ResolveAccessTransform(entry.Value, index))) {
                doubleBufferTransformAccessArray.SetFront(index, resolved);
            }
        }
        foreach (var entry in preCommitRootSlots) {
            var index = entry.Key;
            var resolved = ResolveAccessTransform(transformRootAccessList[index], index);
            if (!ReferenceEquals(resolved, ResolveAccessTransform(entry.Value, index))) {
                doubleBufferTransformRootAccessArray.SetFront(index, resolved);
            }
        }
        foreach (var entry in preCommitPersonalColliderSlots) {
            var index = entry.Key;
            var resolved = ResolveAccessTransform(personalColliderTransformAccessList[index], index);
            if (!ReferenceEquals(resolved, ResolveAccessTransform(entry.Value, index))) {
                doubleBufferPersonalColliderTransformAccessArray.SetFront(index, resolved);
            }
        }
        ClearFrontSlotRecords();
    }

    private void ClearFrontSlotRecords() {
        preCommitTransformSlots.Clear();
        preCommitRootSlots.Clear();
        preCommitPersonalColliderSlots.Clear();
    }

    private void RemoveTree(JiggleTree tree) {
        int id = tree.rootID;
        Profiler.BeginSample("JiggleMemoryBus.RemoveTree");
        if (rootIDToTreeIndex.TryGetValue(id, out var i)) {
            var removedTree = jiggleTreeStructs[i];
            int lastIndex = treeCount - 1;
            if (i != lastIndex) {
                var lastTree = jiggleTreeStructs[lastIndex];
                jiggleTreeStructs[i] = lastTree;
                rootIDToTreeIndex[lastTree.rootID] = i;
            }
            rootIDToTreeIndex.Remove(id);
            treeCount--;
            for (int j = (int)removedTree.transformIndexOffset;
                 j < removedTree.transformIndexOffset + removedTree.pointCount;
                 j++) {
                var simData = simulationOutputPoseData[j];
                simData.pose.isVirtual = true;
                simulationOutputPoseData[j] = simData;

                var interpCurr = interpolationCurrentPoseData[j];
                interpCurr.pose.isVirtual = true;
                interpolationCurrentPoseData[j] = interpCurr;

                var interpPrev = interpolationPreviousPoseData[j];
                interpPrev.pose.isVirtual = true;
                interpolationPreviousPoseData[j] = interpPrev;
            }
        }

        Profiler.EndSample();
    }

    private enum CommitState {
        Idle,
        ProcessingTransformAccess,
    }


    private CommitState commitTreeState = CommitState.Idle;
    private CommitState commitSceneColliderState = CommitState.Idle;

    private bool TryAddTransformsToSlice(int index, JiggleTree jiggleTree, bool inPlace) {
        jiggleTree.SetTransformIndexOffset(index);
        var jiggleTreeJobData = jiggleTree.GetStruct();
        // validate
        for (int o = 0; o < jiggleTreeJobData.pointCount; o++) {
            if (jiggleTree.bones[o]) continue;
            Debug.LogError($"JigglePhysics: Cannot add tree with null bone at index {o} to memory bus.");
            return false;
        }
        
        #region AddColliders

        if (jiggleTreeJobData.colliderCount > 0) {
            var success = personalColliderMemoryFragmenter.TryAllocate((int)jiggleTreeJobData.colliderCount, out var colliderStartIndex);
            if (!success) {
                // Fail the add instead of throwing: a throw here escapes CommitTrees mid-batch and
                // leaves pendingAddTrees stranded, permanently wedging the commit state machine.
                Debug.LogError("JigglePhysics: Ran out of memory for personal colliders! This is a bug, please report it!");
                return false;
            }

            jiggleTree.SetColliderIndexOffset(colliderStartIndex);
            jiggleTreeJobData.colliderIndexOffset = (uint)colliderStartIndex;
            while (personalColliderTransformAccessList.Count < colliderStartIndex + (int)jiggleTreeJobData.colliderCount) {
                personalColliderTransformAccessList.Add(jiggleTree.bones[0]);
                if (inPlace) {
                    doubleBufferPersonalColliderTransformAccessArray.AddToFront(jiggleTree.bones[0]);
                }
            }

            for (int i = 0; i < jiggleTreeJobData.colliderCount; i++) {
                var collider = jiggleTree.personalColliders[i];
                collider.enabled = true;
                personalColliders[colliderStartIndex + i] = collider;
                var colliderTransform = jiggleTree.personalColliderTransforms[i];
                if (inPlace) {
                    RecordSlot(preCommitPersonalColliderSlots, personalColliderTransformAccessList, colliderStartIndex + i);
                }
                personalColliderTransformAccessList[colliderStartIndex + i] = colliderTransform;
            }

            personalColliderCount = personalColliderMemoryFragmenter.GetHighestAllocatedIndex()+1;
        }

        #endregion


        var rootBone = jiggleTree.bones[0];

        var desiredCount = index + (int)jiggleTreeJobData.pointCount;
        while (transformAccessList.Count < desiredCount) {
            transformAccessList.Add(rootBone);
            transformRootAccessList.Add(rootBone);
            if (inPlace) {
                doubleBufferTransformAccessArray.AddToFront(rootBone);
                doubleBufferTransformRootAccessArray.AddToFront(rootBone);
            }
        }

        for (int o = 0; o < jiggleTreeJobData.pointCount; o++) {
            if (inPlace) {
                RecordSlot(preCommitTransformSlots, transformAccessList, index + o);
                RecordSlot(preCommitRootSlots, transformRootAccessList, index + o);
            }
            transformAccessList[index + o] = jiggleTree.bones[o];
            transformRootAccessList[index + o] = rootBone;
        }

        preTransformCount = memoryFragmenter.GetHighestAllocatedIndex()+1;
        return true;
    }

    private void AddTreeToSlice(JiggleTree jiggleTree) {
        var jiggleTreeJobData = jiggleTree.GetStruct();
        int index = (int)jiggleTreeJobData.transformIndexOffset;
        
        if (index < 0) {
            throw new System.Exception($"JigglePhysics: Invalid index when adding tree to memory bus! {jiggleTree.rootID}:{index}");
        }
        
        jiggleTreeStructs[treeCount] = jiggleTreeJobData;
        if (rootIDToTreeIndex.ContainsKey(jiggleTreeJobData.rootID)) {
            Debug.LogError($"JigglePhysics: Duplicate jiggle tree rootID {jiggleTreeJobData.rootID}. Tree tracking will leak a slot and removal will target the wrong tree.");
        }
        rootIDToTreeIndex[jiggleTreeJobData.rootID] = treeCount;
        var root = jiggleTree.bones[0];
        if (!root) {
            root = GetDummyTransform(index);
        }
        float3 rootPos = root.position;
        var wantsScale = jiggleTree.GetWantsScale();
        for (int o = 0; o < jiggleTreeJobData.pointCount; o++) {
            unsafe {
                var point = jiggleTreeJobData.points[o];
                var bone = jiggleTree.bones[o];
                bool hasBone = bone;
                var hasTransform = hasBone && point.hasTransform;
                if (!hasBone) {
                    bone = GetDummyTransform(index + o);
                }
                bone.GetPositionAndRotation(out var pos, out var rot);
                var pose = new JiggleTransform() {
                    isVirtual = !hasTransform,
                    wantsScale = wantsScale,
                    position = pos,
                    rotation = rot,
                };
                var localPose = new JiggleTransform() {
                    isVirtual = !hasTransform,
                    position = jiggleTree.restPositions[o],
                    rotation = jiggleTree.restRotations[o],
                };
                simulateInputPoses[index + o] = pose;
                restPoseTransforms[index + o] = localPose;
                previousLocalRestPoseTransforms[index + o] = localPose;
                rootOutputPositions[index + o] = rootPos;
                interpolationOutputPoses[index + o] = pose;
                var poseData = new PoseData() {
                    pose = pose,
                    rootOffset = new float3(0f),
                    rootPosition = rootPos,
                };
                simulationOutputPoseData[index + o] = poseData;
                interpolationCurrentPoseData[index + o] = poseData;
                interpolationPreviousPoseData[index + o] = poseData;
                inputPosesCurrent[index + o] = pose;
                inputPosesPrevious[index + o] = pose;
            }
        }

        treeCount++;
        transformCount = memoryFragmenter.GetHighestAllocatedIndex() + 1;
    }

    public void CommitColliders() {
        if (commitSceneColliderState == CommitState.Idle) {
            doubleBufferSceneColliderTransformAccessArray.ClearIfNeeded(transformAccessBatchSize);
            var pendingAddSceneColliderCount = pendingSceneColliderAdd.Count;
            var pendingRemoveSceneColliderCount = pendingSceneColliderRemove.Count;

            if (pendingAddSceneColliderCount == 0 && pendingRemoveSceneColliderCount == 0) {
                if (GetSceneColliderAccessArrayDesynced()) {
                    ReclaimDeadSceneColliderSlots(false);
                    currentSceneColliderTransformAccessIndex = 0;
                    commitSceneColliderState = CommitState.ProcessingTransformAccess;
                }
                return;
            }

            if (sceneColliderCount + pendingAddSceneColliderCount >= sceneColliderCapacity) {
                ResizeSceneColliderCapacity(Mathf.NextPowerOfTwo(sceneColliderCount+pendingAddSceneColliderCount+1));
            }

            // In-place fast path: slots are index-stable (removes dummy-swap, adds refill holes or
            // append), so as long as the front access array still mirrors the list — no out-of-band
            // transform death shrank it — every pending change can be written straight into the
            // front array and the full sliced rebuild (the whole ProcessingTransformAccess pass)
            // skipped. Legal because commits run after Simulate joined the previous chain, so no
            // job holds the array. The collider-LOD tier trickle lands here every time; only a
            // desync (destroyed transform Unity auto-dropped) takes the rebuild path below.
            bool inPlace = doubleBufferSceneColliderTransformAccessArray.FrontLength == sceneColliderTransformAccessList.Count;

            for (int i = 0; i < pendingRemoveSceneColliderCount; i++) {
                var collider = pendingSceneColliderRemove[i];
                if (sceneColliderTransformToIndex.TryGetValue(collider.transform, out var id)) {
                    sceneColliderMemoryFragmenter.Free(id, 1);
                    var sceneCollider = sceneColliderArray[id];
                    sceneCollider.enabled = false;
                    sceneColliderArray[id] = sceneCollider;
                    var dummy = GetDummyTransform(id);
                    if (inPlace) {
                        RecordSlot(preCommitSceneColliderSlots, sceneColliderTransformAccessList, id);
                    }
                    sceneColliderTransformAccessList[id] = dummy;
                    sceneColliderTransformToIndex.Remove(collider.transform);
                }
            }
            pendingSceneColliderRemove.Clear();

            ReclaimDeadSceneColliderSlots(inPlace);

            for (int i = 0; i < pendingAddSceneColliderCount; i++) {
                var collider = pendingSceneColliderAdd[i];
                // The add→commit latency is unbounded (async producers, multi-frame commit
                // states); the transform can die while queued. Enrolling a dead transform
                // desyncs the front array when Unity auto-drops it later.
                if (collider.transform == null) {
                    continue;
                }
                collider.collider.enabled = true;

                // Re-adding a transform that is already committed must refresh its slot, not take
                // a second one: allocating again overwrites the transform->index entry and orphans
                // the old slot, which stays allocated and enabled with no way to ever remove it.
                if (sceneColliderTransformToIndex.TryGetValue(collider.transform, out var existingIndex)) {
                    sceneColliderArray[existingIndex] = collider.collider;
                    continue;
                }

                var found = sceneColliderMemoryFragmenter.TryAllocate(1, out var index);
                if (!found) {
                    Debug.LogError("JigglePhysics: Ran out of scene collider memory, this is a bug please report it! (It should've been allocated in advance)");
                    continue;
                }

                while (sceneColliderTransformAccessList.Count < index+1) {
                    sceneColliderTransformAccessList.Add(collider.transform);
                    if (inPlace) {
                        doubleBufferSceneColliderTransformAccessArray.AddToFront(collider.transform);
                    }
                }
                if (inPlace) {
                    RecordSlot(preCommitSceneColliderSlots, sceneColliderTransformAccessList, index);
                }
                sceneColliderTransformAccessList[index] = collider.transform;
                sceneColliderTransformToIndex[collider.transform] = index;
                sceneColliderArray[index] = collider.collider;
                sceneColliderCount = math.max(index+1, sceneColliderCount);
            }

            pendingSceneColliderAdd.Clear();
            pendingSceneColliderAddSet.Clear();

            if (inPlace) {
                FlushSceneColliderSlotWrites();
                RetireDeadSceneColliderSlots();
                NativeArray<JiggleCollider>.Copy(sceneColliderArray, sceneColliders, sceneColliderCount);
                return;
            }

            // The sliced rebuild regenerates the array from the list, so the snapshot is moot.
            preCommitSceneColliderSlots.Clear();
            currentSceneColliderTransformAccessIndex = 0;
            commitSceneColliderState = CommitState.ProcessingTransformAccess;
        } else if (commitSceneColliderState == CommitState.ProcessingTransformAccess) {
            doubleBufferSceneColliderTransformAccessArray.GenerateNewAccessArrays(ref currentSceneColliderTransformAccessIndex, out var hasFinishedSceneColliders, sceneColliderTransformAccessList, transformAccessBatchSize);
            if (!hasFinishedSceneColliders) return;
            RetireDeadSceneColliderSlots();
            NativeArray<JiggleCollider>.Copy(sceneColliderArray, sceneColliders, sceneColliderCount);
            doubleBufferSceneColliderTransformAccessArray.Flip();
            commitSceneColliderState = CommitState.Idle;
        }
    }

    /// <summary>
    /// Scene-collider counterpart of <see cref="FlushFrontSlotWrites"/>: writes back only the slots
    /// whose resolved value changed, so a collider re-registered onto the slot it already held leaves
    /// the array clean and its next Schedule cheap.
    /// </summary>
    private void FlushSceneColliderSlotWrites() {
        foreach (var entry in preCommitSceneColliderSlots) {
            var index = entry.Key;
            var resolved = ResolveAccessTransform(sceneColliderTransformAccessList[index], index);
            if (!ReferenceEquals(resolved, ResolveAccessTransform(entry.Value, index))) {
                doubleBufferSceneColliderTransformAccessArray.SetFront(index, resolved);
            }
        }
        preCommitSceneColliderSlots.Clear();
    }

    // Full reclaim for slots whose transform died without a matching remove: frees the
    // fragmenter entry, drops the transform->index mapping and parks a dummy in the slot, so
    // the index is reusable. RetireDeadSceneColliderSlots below only disables such a slot,
    // which left it allocated forever and grew sceneColliderCount for the whole session.
    private void ReclaimDeadSceneColliderSlots(bool inPlace) {
        var listCount = sceneColliderTransformAccessList.Count;
        var scanCount = math.min(sceneColliderCount, listCount);
        for (int i = 0; i < scanCount; i++) {
            var slotTransform = sceneColliderTransformAccessList[i];
            if (slotTransform) continue;
            if (!sceneColliderMemoryFragmenter.GetIsAllocated(i)) continue;

            sceneColliderMemoryFragmenter.Free(i, 1);
            var deadCollider = sceneColliderArray[i];
            deadCollider.enabled = false;
            sceneColliderArray[i] = deadCollider;

            if (!ReferenceEquals(slotTransform, null) &&
                sceneColliderTransformToIndex.TryGetValue(slotTransform, out var mappedIndex) && mappedIndex == i) {
                sceneColliderTransformToIndex.Remove(slotTransform);
            }

            var dummy = GetDummyTransform(i);
            if (inPlace) {
                RecordSlot(preCommitSceneColliderSlots, sceneColliderTransformAccessList, i);
            }
            sceneColliderTransformAccessList[i] = dummy;
        }
    }

    // The managed mirror is the writer for scene colliders, so it has to retire slots whose
    // transform died without a matching remove — otherwise the copy back to the native array
    // resurrects them, undoing the read job's isValid self-heal, and the collider keeps
    // colliding from wherever it was.
    private void RetireDeadSceneColliderSlots() {
        for (int i = 0; i < sceneColliderCount; i++) {
            if (!sceneColliderArray[i].enabled) continue;
            if (i < sceneColliderTransformAccessList.Count && sceneColliderTransformAccessList[i]) continue;
            var deadCollider = sceneColliderArray[i];
            deadCollider.enabled = false;
            sceneColliderArray[i] = deadCollider;
        }
    }

    public void CommitTrees() {
        if (commitTreeState == CommitState.Idle) {
            doubleBufferTransformAccessArray.ClearIfNeeded(transformAccessBatchSize);
            doubleBufferTransformRootAccessArray.ClearIfNeeded(transformAccessBatchSize);
            doubleBufferPersonalColliderTransformAccessArray.ClearIfNeeded(transformAccessBatchSize);

            var commandCount = pendingCommands.Count;
            if (commandCount == 0) {
                deferredTreeCommits = 0;
                // A bone can die with no structural change queued behind it, and the rebuild is
                // otherwise only ever reached through a pending command — so without this kick the
                // arrays would stay shifted until the next rig add or remove happened to arrive.
                if (GetTreeAccessArraysDesynced()) {
                    currentTransformAccessIndex = 0;
                    currentRootTransformAccessIndex = 0;
                    currentPersonalColliderTransformAccessIndex = 0;
                    commitTreeState = CommitState.ProcessingTransformAccess;
                }
                return;
            }

            // In-place fast path, same shape as CommitColliders: tree slots are index-stable
            // (PreRemoveTree dummy-swaps a removed slice, TryAddTransformsToSlice refills holes or
            // appends), so as long as each front access array still mirrors its list — no
            // out-of-band bone death shrank one — every touched slot can be written straight into
            // the front array and the whole sliced ProcessingTransformAccess rebuild skipped.
            // Legal because commits run after the simulate chain was joined and the pose chain
            // completed, so no job holds these arrays. All three buffers are gated together: they
            // commit and flip as a unit, and the rebuild path regenerates each from its list
            // anyway, so a desync in one just costs the others a rebuild they'd have taken.
            bool inPlace = doubleBufferTransformAccessArray.FrontLength == transformAccessList.Count
                           && doubleBufferTransformRootAccessArray.FrontLength == transformRootAccessList.Count
                           && doubleBufferPersonalColliderTransformAccessArray.FrontLength == personalColliderTransformAccessList.Count;

            // The backlog hold only pays for itself when a commit costs a full rebuild of every
            // transform; an in-place commit is proportional to what actually changed, so holding it
            // would just delay rigs coming online for nothing.
            if (!inPlace && treeBacklogRemains && deferredTreeCommits < MaxDeferredTreeCommits) {
                deferredTreeCommits++;
                return;
            }
            deferredTreeCommits = 0;

            int newTransforms = 0;
            int newTrees = 0;
            int newPersonalColliders = 0;
            for (int i = 0; i < commandCount; i++) {
                var command = pendingCommands[i];
                newTransforms += command.tree.points.Length;
                newTrees++;
                newPersonalColliders += command.tree.personalColliders.Length;
            }

            {
                bool needsResize = false;
                if (transformCount + newTransforms >= transformCapacity) {
                    ResizeTransformCapacity(Mathf.NextPowerOfTwo(transformCount+newTransforms+1));
                    needsResize = true;
                }
                if (treeCount + newTrees >= treeCapacity) {
                    ResizeTreeCapacity(Mathf.NextPowerOfTwo(treeCount+newTrees+1));
                    needsResize = true;
                }
                if (personalColliderCount + newPersonalColliders >= personalColliderCapacity) {
                    ResizePersonalColliderCapacity(Mathf.NextPowerOfTwo(personalColliderCount+newPersonalColliders+1));
                    needsResize = true;
                }
                if (needsResize) {
                    return;
                }
            }

            for (int i = 0; i < commandCount; i++) {
                var command = pendingCommands[i];
                if (command.commandType == AddRemoveCommand.CommandType.Add) {
                    var found = false;
                    for (int o = i+1; o < commandCount; o++) {
                        var otherCommand = pendingCommands[o];
                        if (otherCommand.commandType == AddRemoveCommand.CommandType.Remove && otherCommand.tree.rootID == command.tree.rootID) {
                            pendingCommands.RemoveAt(o);
                            commandCount -= 1;
                            found = true;
                            break;
                        }
                    }
                    if (!found) {
                        pendingAddTrees.Add(command.tree);
                    }
                } else if (command.commandType == AddRemoveCommand.CommandType.Remove) {
                    pendingRemoveTrees.Add(command.tree);
                } else {
                    throw new System.ArgumentException("Unexpected command type: " + command.commandType);
                }
            }
            pendingCommands.Clear();
            
            var pendingRemoveCount = pendingRemoveTrees.Count;
            var pendingAddCount = pendingAddTrees.Count;

            if (pendingRemoveCount == 0 && pendingAddCount == 0) {
                return;
            }

            preTransformCount = transformCount;

            preRemovedRootIDs.Clear();
            for (int i = 0; i < pendingRemoveCount; i++) {
                var removedTree = pendingRemoveTrees[i];
                if (!preRemovedRootIDs.Add(removedTree.rootID)) {
                    continue;
                }
                PreRemoveTree(removedTree, inPlace);
            }

            pendingProcessingRemoves.AddRange(pendingRemoveTrees);
            pendingRemoveTrees.Clear();

            for (int i = 0; i < pendingAddCount; i++) {
                var jiggleTree = pendingAddTrees[i];
                var pointCount = (int)jiggleTree.GetStruct().pointCount;
                if (pointCount != jiggleTree.bones.Length) {
                    jiggleTree.SetDirty();
                    JigglePhysics.SetGlobalDirty();
                    pendingAddTrees.RemoveAt(i);
                    pendingAddCount--;
                    i--;
                    Debug.LogError("JigglePhysics: Cannot add tree, point count does not match bone count. Attempting to regenerate tree...");
                    continue;
                }
                if (pointCount > JiggleTreeJobData.MAX_POINTS) {
                    pendingAddTrees.RemoveAt(i);
                    pendingAddCount--;
                    i--;
                    Debug.LogError("JigglePhysics: Cannot add tree with more than " + JiggleTreeJobData.MAX_POINTS + " points to memory bus.");
                    continue;
                }

                var found = memoryFragmenter.TryAllocate(pointCount, out var startIndex);
                if (!found || startIndex == -1) {
                    // Same wedge-avoidance as the collider pool: skip the tree and keep the batch alive.
                    Debug.LogError("JigglePhysics: Ran out of memory for jiggle points, this is a bug please report it!");
                    jiggleTree.SetDirty();
                    pendingAddTrees.RemoveAt(i);
                    pendingAddCount--;
                    i--;
                    continue;
                }

                if (!TryAddTransformsToSlice(startIndex, jiggleTree, inPlace)) {
                    memoryFragmenter.Free(startIndex, pointCount);
                    jiggleTree.SetDirty();
                    pendingAddTrees.RemoveAt(i);
                    pendingAddCount--;
                    i--;
                }
            }

            pendingProcessingAdds.AddRange(pendingAddTrees);
            pendingAddTrees.Clear();

            if (inPlace) {
                // Front arrays already mirror every slot this commit touched — nothing to
                // regenerate, nothing to flip, so the commit finishes in this one call.
                FlushFrontSlotWrites();
                FinishTreeCommit(false);
                return;
            }
            // The sliced rebuild regenerates both arrays from the lists, so the recorded slots are
            // moot; dropping them stops a later in-place commit writing back against a stale snapshot.
            ClearFrontSlotRecords();

            currentTransformAccessIndex = 0;
            currentRootTransformAccessIndex = 0;
            currentPersonalColliderTransformAccessIndex = 0;
            commitTreeState = CommitState.ProcessingTransformAccess;
        } else if (commitTreeState == CommitState.ProcessingTransformAccess) {
            doubleBufferTransformAccessArray.GenerateNewAccessArrays(ref currentTransformAccessIndex, out var hasFinishedTransforms, transformAccessList, transformAccessBatchSize);
            if (!hasFinishedTransforms) return;
            doubleBufferTransformRootAccessArray.GenerateNewAccessArrays(ref currentRootTransformAccessIndex, out var hasFinishedRoots, transformRootAccessList, transformAccessBatchSize);
            if (!hasFinishedRoots) return;
            doubleBufferPersonalColliderTransformAccessArray.GenerateNewAccessArrays(ref currentPersonalColliderTransformAccessIndex, out var hasFinishedColliders, personalColliderTransformAccessList, transformAccessBatchSize);
            if (!hasFinishedColliders) return;
            FinishTreeCommit(true);
            commitTreeState = CommitState.Idle;
        }
    }

    /// <summary>
    /// The native half of a tree commit: retire removed trees, install added ones, and release the
    /// buffers the previous structs owned. Shared by the in-place fast path (which has already
    /// mirrored its slot changes into the front access arrays) and the sliced rebuild path (which
    /// built fresh back arrays and flips them in here).
    /// </summary>
    private void FinishTreeCommit(bool flipAccessArrays) {
        var processingPendingRemoveCount = pendingProcessingRemoves.Count;
        var processingPendingAddCount = pendingProcessingAdds.Count;

        #region Removing

        Profiler.BeginSample("JiggleMemoryBus.Commit.Remove");
        for (int i = 0; i < processingPendingRemoveCount; i++) {
            var tree = pendingProcessingRemoves[i];
            RemoveTree(tree);
            bool found = false;
            for (int o = 0; o < processingPendingAddCount; o++) {
                if (pendingProcessingAdds[o].rootID == tree.rootID) {
                    found = true;
                    break;
                }
            }
            if (!found) {
                tree.Dispose();
            }
        }

        pendingProcessingRemoves.Clear();
        Profiler.EndSample();

        #endregion

        #region Adding

        Profiler.BeginSample("JiggleMemoryBus.Commit.Add");
        for (int i = 0; i < processingPendingAddCount; i++) {
            var jiggleTree = pendingProcessingAdds[i];
            AddTreeToSlice(jiggleTree);
        }

        pendingProcessingAdds.Clear();
        Profiler.EndSample();

        #endregion

        if (flipAccessArrays) {
            doubleBufferTransformAccessArray.Flip();
            doubleBufferTransformRootAccessArray.Flip();
            doubleBufferPersonalColliderTransformAccessArray.Flip();
        }
        // A re-pointed tree only leaves jiggleTreeStructs when ITS OWN remove is processed, and
        // deferredFlipFrees is global — so draining here whenever any commit finishes frees the old
        // points/parameters of trees whose remove is still queued and which the simulate job is
        // still dereferencing every tick. Wait for the machine to quiesce instead: every re-point
        // arrives with a paired remove, so an empty queue means every one of them has landed.
        if (pendingCommands.Count == 0 && pendingAddTrees.Count == 0 && pendingRemoveTrees.Count == 0
            && pendingProcessingAdds.Count == 0 && pendingProcessingRemoves.Count == 0) {
            DrainDeferredFlipFrees();
        }
    }

    public void ScheduleAdd(JiggleColliderSerializable jiggleCollider) {
        // O(1) dedup via the membership set instead of a linear scan of the pending list.
        if (pendingSceneColliderAddSet.Add(jiggleCollider.transform)) {
            pendingSceneColliderAdd.Add(jiggleCollider);
        }
    }

    /// <summary>
    /// Batch-add colliders with O(m) dedup and zero per-call allocation.
    /// Dedup (against both the existing pending list and within the batch) rides on
    /// the persistent <see cref="pendingSceneColliderAddSet"/>, so this no longer
    /// rebuilds a HashSet over the whole pending list each call — that rebuild was
    /// O(n) per call and O(n^2) across a join storm, and churned GC with a fresh set.
    /// </summary>
    public void ScheduleAddBatch(List<JiggleColliderSerializable> colliders) {
        int addCount = colliders.Count;
        for (int i = 0; i < addCount; i++) {
            var c = colliders[i];
            if (pendingSceneColliderAddSet.Add(c.transform)) {
                pendingSceneColliderAdd.Add(c);
            }
        }
    }

    public void ScheduleRemove(JiggleColliderSerializable jiggleCollider) {
        var count = pendingSceneColliderAdd.Count;
        for (int i = 0; i < count; i++) {
            // ReferenceEquals, not ==: Unity's equality operator reports any two destroyed
            // transforms as equal, so a removal issued after the avatar was destroyed would
            // cancel an unrelated pending add and drop the real removal on the floor.
            if (ReferenceEquals(pendingSceneColliderAdd[i].transform, jiggleCollider.transform)) {
                pendingSceneColliderAdd.RemoveAt(i);
                pendingSceneColliderAddSet.Remove(jiggleCollider.transform);
                return;
            }
        }

        pendingSceneColliderRemove.Add(jiggleCollider);
    }

    /// <summary>
    /// Batch-remove colliders with O(n+m) instead of O(n*m).
    /// Builds a HashSet of transforms to remove, then does a single pass over
    /// pendingSceneColliderAdd to cancel pending adds, and queues the rest for removal.
    /// </summary>
    public void ScheduleRemoveBatch(List<JiggleColliderSerializable> colliders) {
        int removeCount = colliders.Count;
        if (removeCount == 0) return;

        // Build set of transforms we want to remove
        var toRemove = new HashSet<Transform>(removeCount);
        for (int i = 0; i < removeCount; i++) {
            toRemove.Add(colliders[i].transform);
        }

        // Single pass over pendingSceneColliderAdd: cancel any pending adds that match
        for (int i = pendingSceneColliderAdd.Count - 1; i >= 0; i--) {
            var t = pendingSceneColliderAdd[i].transform;
            if (toRemove.Remove(t)) {
                pendingSceneColliderAdd.RemoveAt(i);
                pendingSceneColliderAddSet.Remove(t);
            }
        }

        // Anything still in toRemove wasn't in the pending add list — queue for actual removal
        if (toRemove.Count > 0) {
            for (int i = 0; i < removeCount; i++) {
                if (toRemove.Contains(colliders[i].transform)) {
                    pendingSceneColliderRemove.Add(colliders[i]);
                }
            }
        }
    }

    public void FreeOnCommitFlip(IntPtr pointer) {
        deferredFlipFrees.Add(pointer);
    }

    private void DrainDeferredFlipFrees() {
        var count = deferredFlipFrees.Count;
        for (int i = 0; i < count; i++) {
            unsafe {
                UnsafeUtility.Free((void*)deferredFlipFrees[i], Allocator.Persistent);
            }
        }
        deferredFlipFrees.Clear();
    }

    public void ScheduleTeleport(JiggleTree tree, float3 deltaPosition) {
        ScheduleTeleport(tree, JiggleRigidTeleport.FromTranslation(deltaPosition));
    }

    public void ScheduleTeleport(JiggleTree tree, quaternion deltaRotation, float3 pivot, float3 deltaPosition) {
        ScheduleTeleport(tree, JiggleRigidTeleport.FromRigid(deltaRotation, pivot, deltaPosition));
    }

    private void ScheduleTeleport(JiggleTree tree, JiggleRigidTeleport teleport) {
        if (tree == null) {
            return;
        }
        var rootID = tree.rootID;
        if (!rootIDToTreeIndex.ContainsKey(rootID)) {
            tree.TransformRigid(teleport.rotation, teleport.translation);
            return;
        }
        if (pendingTeleports.TryGetValue(rootID, out var existing)) {
            pendingTeleports[rootID] = existing.Then(teleport);
        } else {
            pendingTeleports[rootID] = teleport;
        }
    }

    // Runs at the fenced drain inside JiggleJobs.Simulate, so every array below is
    // safely writable. The per-point math is Burst-compiled via ApplyTeleportsJob.Run
    // — hot while vehicles carry seated players, which queues teleports every frame.
    public void ApplyPendingTeleports() {
        if (pendingTeleports.Count == 0) return;
        if (transformCount == 0) {
            pendingTeleports.Clear();
            return;
        }
        // TempJob, not Temp: IJobParallelFor.Run still goes through the schedule
        // machinery, which refuses Temp containers.
        var teleports = new NativeArray<JiggleRigidTeleport>(pendingTeleports.Count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
        var ranges = new NativeArray<int2>(pendingTeleports.Count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
        try {
            int jobCount = 0;
            foreach (var kvp in pendingTeleports) {
                if (!rootIDToTreeIndex.TryGetValue(kvp.Key, out var treeIndex)) continue;
                var tree = jiggleTreeStructs[treeIndex];
                int start = (int)tree.transformIndexOffset;
                int end = start + (int)tree.pointCount;
                if (end > transformCount) continue;
                teleports[jobCount] = kvp.Value;
                ranges[jobCount] = new int2(start, end);
                jobCount++;
                tree.TransformRigid(kvp.Value.rotation, kvp.Value.translation);
            }
            if (jobCount > 0) {
                new ApplyTeleportsJob {
                    teleports = teleports,
                    ranges = ranges,
                    inputPosesPrevious = inputPosesPrevious,
                    inputPosesCurrent = inputPosesCurrent,
                    simulateInputPoses = simulateInputPoses,
                    interpolationOutputPoses = interpolationOutputPoses,
                    rootOutputPositions = rootOutputPositions,
                    simulationOutputPoseData = simulationOutputPoseData,
                    interpolationCurrentPoseData = interpolationCurrentPoseData,
                    interpolationPreviousPoseData = interpolationPreviousPoseData,
                }.Run(jobCount);
            }
        } finally {
            teleports.Dispose();
            ranges.Dispose();
        }
        pendingTeleports.Clear();
    }

    public void SetGrabConstraints(JiggleGrabConstraint[] constraints, int count) {
        count = math.min(count, JiggleGrabConstraint.MaxTotalGrabs);
        for (int i = 0; i < count; i++) {
            pendingGrabConstraints[i] = constraints[i];
        }
        pendingGrabConstraintCount = count;
        pendingGrabConstraintsDirty = true;
    }

    // Staged like teleports: the native map is only rewritten at the fenced drain inside
    // JiggleJobs.Simulate, after handleSimulate completes — never while the sim job may read it.
    public void ApplyPendingGrabConstraints() {
        if (!pendingGrabConstraintsDirty) return;
        grabConstraints.Clear();
        int applied = 0;
        for (int i = 0; i < pendingGrabConstraintCount; i++) {
            var constraint = pendingGrabConstraints[i];
            if (!rootIDToTreeIndex.ContainsKey(constraint.rootID)) continue;
            grabConstraints.Add(constraint.rootID, constraint);
            applied++;
        }
        grabConstraintCount = applied;
        pendingGrabConstraintsDirty = false;
    }

    [BurstCompile]
    private struct ApplyTeleportsJob : IJobParallelFor {
        [ReadOnly] public NativeArray<JiggleRigidTeleport> teleports;
        [ReadOnly] public NativeArray<int2> ranges;
        // Tree point ranges are disjoint, so per-tree parallel writes never alias.
        [NativeDisableParallelForRestriction] public NativeArray<JiggleTransform> inputPosesPrevious;
        [NativeDisableParallelForRestriction] public NativeArray<JiggleTransform> inputPosesCurrent;
        [NativeDisableParallelForRestriction] public NativeArray<JiggleTransform> simulateInputPoses;
        [NativeDisableParallelForRestriction] public NativeArray<JiggleTransform> interpolationOutputPoses;
        [NativeDisableParallelForRestriction] public NativeArray<float3> rootOutputPositions;
        [NativeDisableParallelForRestriction] public NativeArray<PoseData> simulationOutputPoseData;
        [NativeDisableParallelForRestriction] public NativeArray<PoseData> interpolationCurrentPoseData;
        [NativeDisableParallelForRestriction] public NativeArray<PoseData> interpolationPreviousPoseData;

        public void Execute(int index) {
            var teleport = teleports[index];
            int start = ranges[index].x;
            int end = ranges[index].y;
            for (int i = start; i < end; i++) {
                var inputPrev = inputPosesPrevious[i];
                inputPrev.position = teleport.Apply(inputPrev.position);
                inputPrev.rotation = teleport.Apply(inputPrev.rotation);
                inputPosesPrevious[i] = inputPrev;

                var inputCurr = inputPosesCurrent[i];
                inputCurr.position = teleport.Apply(inputCurr.position);
                inputCurr.rotation = teleport.Apply(inputCurr.rotation);
                inputPosesCurrent[i] = inputCurr;

                var simInput = simulateInputPoses[i];
                simInput.position = teleport.Apply(simInput.position);
                simInput.rotation = teleport.Apply(simInput.rotation);
                simulateInputPoses[i] = simInput;

                var interpOut = interpolationOutputPoses[i];
                interpOut.position = teleport.Apply(interpOut.position);
                interpOut.rotation = teleport.Apply(interpOut.rotation);
                interpolationOutputPoses[i] = interpOut;

                rootOutputPositions[i] = teleport.Apply(rootOutputPositions[i]);

                var simPose = simulationOutputPoseData[i];
                simPose.pose.position = teleport.Apply(simPose.pose.position);
                simPose.pose.rotation = teleport.Apply(simPose.pose.rotation);
                simPose.rootPosition = teleport.Apply(simPose.rootPosition);
                simPose.rootOffset = teleport.ApplyDirection(simPose.rootOffset);
                simulationOutputPoseData[i] = simPose;

                var interpCurr = interpolationCurrentPoseData[i];
                interpCurr.pose.position = teleport.Apply(interpCurr.pose.position);
                interpCurr.pose.rotation = teleport.Apply(interpCurr.pose.rotation);
                interpCurr.rootPosition = teleport.Apply(interpCurr.rootPosition);
                interpCurr.rootOffset = teleport.ApplyDirection(interpCurr.rootOffset);
                interpolationCurrentPoseData[i] = interpCurr;

                var interpPrev = interpolationPreviousPoseData[i];
                interpPrev.pose.position = teleport.Apply(interpPrev.pose.position);
                interpPrev.pose.rotation = teleport.Apply(interpPrev.pose.rotation);
                interpPrev.rootPosition = teleport.Apply(interpPrev.rootPosition);
                interpPrev.rootOffset = teleport.ApplyDirection(interpPrev.rootOffset);
                interpolationPreviousPoseData[i] = interpPrev;
            }
        }
    }

    /// <summary>
    /// Turns the scale read back on for every slot a committed tree owns. Called once per tree, the
    /// first time it pushes parameters outside a commit — after that its own commits carry the flag.
    /// A tree that is not resident yet needs nothing: its slots take the flag when they are installed.
    /// </summary>
    public void MarkAlwaysReadScale(JiggleTree jiggleTree) {
        if (jiggleTree == null || !rootIDToTreeIndex.TryGetValue(jiggleTree.rootID, out var treeIndex)) {
            return;
        }
        var tree = jiggleTreeStructs[treeIndex];
        var start = (int)tree.transformIndexOffset;
        var end = start + (int)tree.pointCount;
        if (end > transformCapacity) {
            return;
        }
        for (int i = start; i < end; i++) {
            var simInput = simulateInputPoses[i];
            simInput.wantsScale = true;
            simulateInputPoses[i] = simInput;

            var current = inputPosesCurrent[i];
            current.wantsScale = true;
            inputPosesCurrent[i] = current;

            var previous = inputPosesPrevious[i];
            previous.wantsScale = true;
            inputPosesPrevious[i] = previous;
        }
    }

    public void ScheduleAdd(JiggleTree jiggleTree) {
        pendingCommands.Add(new AddRemoveCommand() {
            commandType = AddRemoveCommand.CommandType.Add,
            tree = jiggleTree,
        });
    }

    public void ScheduleRemove(JiggleTree jiggleTree) {
        pendingCommands.Add(new AddRemoveCommand() {
            commandType = AddRemoveCommand.CommandType.Remove,
            tree = jiggleTree,
        });
    }

    public void Dispose() {
        DrainDeferredFlipFrees();
        if (jiggleTreeStructs.IsCreated) {
            jiggleTreeStructs.Dispose();
            simulateInputPoses.Dispose();
            restPoseTransforms.Dispose();
            previousLocalRestPoseTransforms.Dispose();
            rootOutputPositions.Dispose();
            interpolationOutputPoses.Dispose();
            simulationOutputPoseData.Dispose();
            interpolationCurrentPoseData.Dispose();
            interpolationPreviousPoseData.Dispose();
            personalColliders.Dispose();
            sceneColliders.Dispose();
            if (broadPhaseEntries.IsCreated) {
                broadPhaseEntries.Dispose();
            }
            inputPosesCurrent.Dispose();
            inputPosesPrevious.Dispose();
        }

        var values = broadPhaseMap.GetValueArray(Allocator.Temp);
        var gridCells = new JiggleGridCell[values.Length];
        values.CopyTo(gridCells);
        values.Dispose();
        for (int i = 0; i < gridCells.Length; i++) {
            gridCells[i].Dispose();
        }
        broadPhaseMap.Dispose();
        globalCell.Dispose();
        if (grabConstraints.IsCreated) {
            grabConstraints.Dispose();
        }

        doubleBufferTransformAccessArray?.Dispose();
        doubleBufferTransformRootAccessArray?.Dispose();
        doubleBufferPersonalColliderTransformAccessArray?.Dispose();
        doubleBufferSceneColliderTransformAccessArray?.Dispose();
    }

}

}
