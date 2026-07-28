using System;
using System.Collections.Generic;
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

    // Buffers whose owning JiggleTreeJobData was re-pointed by JiggleTree.Set while its old copy
    // may still sit in jiggleTreeStructs. That copy is only swapped out when the tree commit
    // flips, so these pointers must stay alive until the end of the flip — freeing them on the
    // next Simulate (the FreeOnComplete path) is too early and the sim job reads freed memory.
    private List<IntPtr> deferredFlipFrees;

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

    private static List<Transform> dummyTransforms;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void Init() {
        if (dummyTransforms != null) {
            foreach (Transform t in dummyTransforms) {
                Object.Destroy(t.gameObject);
            }

            dummyTransforms.Clear();
        } else {
            dummyTransforms = new List<Transform>();
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

    public static Transform GetDummyTransform(int index) {
        while (dummyTransforms.Count <= index) {
            Transform dummyTransform = new GameObject($"JigglePhysicsDummyTransform{dummyTransforms.Count}").transform;
            // Throws outside play mode, which is enough to fail any edit mode test that reaches the
            // bus. HideAndDontSave already survives scene loads, so this only matters while playing.
            if (Application.isPlaying) {
                Object.DontDestroyOnLoad(dummyTransform.gameObject);
            }
            dummyTransform.gameObject.hideFlags = HideFlags.HideAndDontSave;
            dummyTransforms.Add(dummyTransform);
        }
        return dummyTransforms[index];
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
    }

    private void ReadIn<T>(NativeArray<T> native, T[] array, int count) where T : struct {
        NativeArray<T>.Copy(native, array, count);
    }

    #if UNITY_EDITOR && JIGGLE_VALIDATE
    // Per-frame NaN/allocation diagnostics — opt-in via the JIGGLE_VALIDATE scripting define. Off by
    // default: this walks every point every frame (O(points)), and Sanitize() already repairs NaNs.
    private bool GetIsValid(out string failReason) {
        for (int i = 0; i < treeCount; i++) {
            var tree = jiggleTreeStructs[i];
            if (!tree.GetIsValid(out failReason)) {
                return false;
            }
            for (int o=0;o<tree.pointCount;o++) {
                if (!memoryFragmenter.GetIsAllocated(o + (int)tree.transformIndexOffset)) {
                    failReason = $"Transform index {o + tree.transformIndexOffset} in tree {i} is not allocated, invalid access!";
                    return false;
                }
            }
        }

        for (int i = 0; i < sceneColliderCount; i++) {
            var collider = sceneColliderArray[i];
            if (collider.enabled) {
                if (!sceneColliderMemoryFragmenter.GetIsAllocated(i)) {
                    failReason = $"Scene collider index {i} is not allocated, invalid access!";
                    return false;
                }
            }
        }

        for (int i = 0; i < transformCount; i++) {
            var transformInfo = simulationOutputPoseData[i];
            if (!transformInfo.pose.isVirtual) {
                if (!memoryFragmenter.GetIsAllocated(i)) {
                    failReason = $"Transform index {i} is not allocated, invalid access!";
                    return false;
                }
            }
        }

        failReason = "All good!";
        return true;
    }
    #endif

    private void PreRemoveTree(JiggleTree tree) {
        if (!rootIDToTreeIndex.TryGetValue(tree.rootID, out var i)) return;
        var removedTree = jiggleTreeStructs[i];
        memoryFragmenter.Free((int)removedTree.transformIndexOffset, (int)removedTree.pointCount);
        for (int j = (int)removedTree.transformIndexOffset; j < removedTree.transformIndexOffset + removedTree.pointCount; j++) {
            transformAccessList[j] = GetDummyTransform(j);
            transformRootAccessList[j] = GetDummyTransform(j);
        }
        if (removedTree.colliderCount > 0) {
            personalColliderMemoryFragmenter.Free((int)removedTree.colliderIndexOffset, (int)removedTree.colliderCount);
        }
        for (int j = (int)removedTree.colliderIndexOffset; j < removedTree.colliderIndexOffset + removedTree.colliderCount; j++) {
            var freedCollider = personalColliders[j];
            freedCollider.enabled = false;
            personalColliders[j] = freedCollider;
            personalColliderTransformAccessList[j] = GetDummyTransform(j);
        }
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

    private bool TryAddTransformsToSlice(int index, JiggleTree jiggleTree) {
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
            }

            for (int i = 0; i < jiggleTreeJobData.colliderCount; i++) {
                var collider = jiggleTree.personalColliders[i];
                collider.enabled = true;
                personalColliders[colliderStartIndex + i] = collider;
                personalColliderTransformAccessList[colliderStartIndex + i] = jiggleTree.personalColliderTransforms[i];
            }

            personalColliderCount = personalColliderMemoryFragmenter.GetHighestAllocatedIndex()+1;
        }

        #endregion


        var rootBone = jiggleTree.bones[0];

        var desiredCount = index + (int)jiggleTreeJobData.pointCount;
        while (transformAccessList.Count < desiredCount) {
            transformAccessList.Add(rootBone);
            transformRootAccessList.Add(rootBone);
        }

        for (int o = 0; o < jiggleTreeJobData.pointCount; o++) {
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
                    sceneColliderTransformAccessList[id] = dummy;
                    if (inPlace) {
                        doubleBufferSceneColliderTransformAccessArray.SetFront(id, dummy);
                    }
                    sceneColliderTransformToIndex.Remove(collider.transform);
                }
            }
            pendingSceneColliderRemove.Clear();

            for (int i = 0; i < pendingAddSceneColliderCount; i++) {
                var collider = pendingSceneColliderAdd[i];
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
                sceneColliderTransformAccessList[index] = collider.transform;
                if (inPlace) {
                    doubleBufferSceneColliderTransformAccessArray.SetFront(index, collider.transform);
                }
                sceneColliderTransformToIndex[collider.transform] = index;
                sceneColliderArray[index] = collider.collider;
                sceneColliderCount = math.max(index+1, sceneColliderCount);
            }

            pendingSceneColliderAdd.Clear();
            pendingSceneColliderAddSet.Clear();

            if (inPlace) {
                RetireDeadSceneColliderSlots();
                NativeArray<JiggleCollider>.Copy(sceneColliderArray, sceneColliders, sceneColliderCount);
                return;
            }

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
                return;
            }

            if (treeBacklogRemains && deferredTreeCommits < MaxDeferredTreeCommits) {
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

            for (int i = 0; i < pendingRemoveCount; i++) {
                PreRemoveTree(pendingRemoveTrees[i]);
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

                if (!TryAddTransformsToSlice(startIndex, jiggleTree)) {
                    memoryFragmenter.Free(startIndex, pointCount);
                    jiggleTree.SetDirty();
                    pendingAddTrees.RemoveAt(i);
                    pendingAddCount--;
                    i--;
                }
            }

            pendingProcessingAdds.AddRange(pendingAddTrees);
            pendingAddTrees.Clear();
            
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

            #if UNITY_EDITOR && JIGGLE_VALIDATE
            if (!GetIsValid(out var failReason)) {
                Debug.LogError(failReason);
            }
            #endif
            doubleBufferTransformAccessArray.Flip();
            doubleBufferTransformRootAccessArray.Flip();
            doubleBufferPersonalColliderTransformAccessArray.Flip();
            // The flip above is the point where every re-pointed tree struct has left
            // jiggleTreeStructs (RemoveTree ran this call), so the old buffers are unreachable.
            DrainDeferredFlipFrees();
            commitTreeState = CommitState.Idle;
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

    public void ApplyPendingTeleports() {
        if (pendingTeleports.Count == 0) return;
        foreach (var kvp in pendingTeleports) {
            ApplyTeleport(kvp.Key, kvp.Value);
        }
        pendingTeleports.Clear();
    }

    private void ApplyTeleport(int rootID, JiggleRigidTeleport teleport) {
        if (transformCount == 0) return;
        if (!rootIDToTreeIndex.TryGetValue(rootID, out var treeIndex)) return;

        var tree = jiggleTreeStructs[treeIndex];
        int start = (int)tree.transformIndexOffset;
        int end = start + (int)tree.pointCount;
        if (end > transformCount) return;

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

        tree.TransformRigid(teleport.rotation, teleport.translation);
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

        doubleBufferTransformAccessArray?.Dispose();
        doubleBufferTransformRootAccessArray?.Dispose();
        doubleBufferPersonalColliderTransformAccessArray?.Dispose();
        doubleBufferSceneColliderTransformAccessArray?.Dispose();
    }

}

}
