using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;
using UnityEngine.Profiling;

namespace GatorDragonGames.JigglePhysics {

public class JiggleJobs {
    public static bool UseMergedTransformReadReset = true;

    private JiggleMemoryBus _memoryBus;

    private JobHandle handlePersonalColliderRead;
    private bool hasHandlePersonalColliderRead;
    
    private JobHandle handleSceneColliderRead;
    private bool hasHandleSceneColliderRead;

    private JobHandle handleBulkRead;
    private bool hasHandleBulkRead;
    
    private JobHandle handleBulkReset;
    private bool hasHandleBulkReset;

    private JobHandle handleSimulate;
    private bool hasHandleSimulate;

    private JobHandle handleTransformWrite;
    private bool hasHandleTransformWrite;

    private JobHandle handleRootRead;
    private bool hasHandleRootRead;

    private JobHandle handleInterpolate;
    private bool hasHandleInterpolate;
    
    private JobHandle handleBroadPhaseClear;
    private bool hasHandleBroadPhaseClear;
    
    private JobHandle handleBroadPhase;
    private bool hasHandleBroadPhase;

    private JobHandle handleColliderCull;
    private bool hasHandleColliderCull;
    
    private JobHandle handleInputInterpolate;
    private bool hasHandleInputInterpolate;

    private JiggleJobBulkColliderTransformRead jobBulkPersonalColliderTransformRead;
    private JiggleJobBulkColliderTransformRead jobBulkSceneColliderTransformRead;
    private JiggleJobBulkTransformRead jobBulkTransformRead;
    private JiggleJobBulkTransformReset jobBulkTransformReset;
    private JiggleJobBulkTransformReadReset jobBulkTransformReadReset;
    private JiggleJobSimulate jobSimulate;
    private JiggleJobBulkReadRoots jobBulkReadRoots;
    private JiggleJobInterpolation jobInterpolation;
    private JiggleJobBroadPhaseClear jobBroadPhaseClear;
    private JiggleJobBroadPhase jobBroadPhase;
    private JiggleJobColliderCull jobColliderCull;
    private JiggleJobInputInterpolation jobInputInterpolation;

    private JiggleJobTransformWrite jobTransformWrite;

    private List<IntPtr> freePointers;

    private NativeArray<JiggleCullingCamera> cullingCameras;
    private int cullingCameraCount;
    private byte frustumCull;
    private byte distanceCull;
    private float maxCollisionDistance;

    private JiggleCullingCamera[] pendingCullingCameras;
    private int pendingCullingCameraCount;
    private bool pendingFrustumCull;
    private bool pendingDistanceCull;
    private float pendingMaxDistance;

    private const int MaxCullingCameras = 16;

    private bool capturedStartupSettings;
    private int colliderCullMinBatch = 64;

    public delegate void JiggleFinishSimulateAction(JiggleJobs job, double simulatedTime);
    public event JiggleFinishSimulateAction OnFinishSimulate;

    public JiggleJobs(double fixedTime, float fixedDeltaTime) {
        _memoryBus = new JiggleMemoryBus();
        jobSimulate = new JiggleJobSimulate(_memoryBus, fixedDeltaTime);
        jobBulkTransformRead = new JiggleJobBulkTransformRead(_memoryBus);
        jobBulkTransformReset = new JiggleJobBulkTransformReset(_memoryBus);
        jobBulkTransformReadReset = new JiggleJobBulkTransformReadReset(_memoryBus);
        jobBulkReadRoots = new JiggleJobBulkReadRoots(_memoryBus);
        jobInterpolation = new JiggleJobInterpolation(_memoryBus, fixedTime, fixedDeltaTime);
        jobBulkPersonalColliderTransformRead = new JiggleJobBulkColliderTransformRead(_memoryBus.personalColliders);
        jobBulkSceneColliderTransformRead = new JiggleJobBulkColliderTransformRead(_memoryBus.sceneColliders);
        jobTransformWrite = new JiggleJobTransformWrite(_memoryBus);
        jobBroadPhase = new JiggleJobBroadPhase(_memoryBus);
        jobBroadPhaseClear = new JiggleJobBroadPhaseClear(_memoryBus);
        jobColliderCull = new JiggleJobColliderCull(_memoryBus);
        jobInputInterpolation = new JiggleJobInputInterpolation(_memoryBus, fixedTime, fixedDeltaTime);
        freePointers = new List<IntPtr>();
        cullingCameras = new NativeArray<JiggleCullingCamera>(MaxCullingCameras, Allocator.Persistent);
    }

    public bool TryGetRenderDependencies(out JobHandle handle) {
        if (hasHandleSimulate && hasHandleInterpolate) {
            handle = JobHandle.CombineDependencies(handleSimulate, handleInterpolate);
            return true;
        }
        handle = default;
        return false;
    }
    
    public void SetFixedDeltaTime(float fixedDeltaTime) {
        jobSimulate.SetFixedDeltaTime(fixedDeltaTime);
        jobInterpolation.SetFixedDeltaTime(fixedDeltaTime);
    }

    private void CaptureStartupSettings() {
        if (capturedStartupSettings) {
            return;
        }
        capturedStartupSettings = true;
        var inverseCellSize = JiggleSettings.InverseBroadPhaseCellSize;
        jobColliderCull.inverseCellSize = inverseCellSize;
        jobColliderCull.maxColliderCellSpan = JiggleSettings.MaxColliderCellSpan;
        jobSimulate.inverseCellSize = inverseCellSize;
        jobSimulate.maxTreeCellSpan = JiggleSettings.MaxTreeCellSpan;
        jobBroadPhaseClear.maxStalenessFrames = JiggleSettings.CellStalenessFrames;
        colliderCullMinBatch = JiggleSettings.ColliderCullMinBatch;
        JiggleSettings.MarkBooted();
    }

    public void SetCollisionCulling(bool frustum, bool distance, float maxDistance, JiggleCullingCamera[] cameras, int cameraCount) {
        pendingFrustumCull = frustum;
        pendingDistanceCull = distance;
        pendingMaxDistance = maxDistance;
        pendingCullingCameras = cameras;
        pendingCullingCameraCount = cameraCount;
    }

    public void Dispose() {
        if (hasHandleBulkRead) handleBulkRead.Complete();
        if (hasHandleBulkReset) handleBulkReset.Complete();
        if (hasHandleRootRead) handleRootRead.Complete();
        if (hasHandleSimulate) handleSimulate.Complete();
        if (hasHandleTransformWrite) handleTransformWrite.Complete();
        if (hasHandleInterpolate) handleInterpolate.Complete();
        if (hasHandlePersonalColliderRead) handlePersonalColliderRead.Complete();
        if (hasHandleSceneColliderRead) handleSceneColliderRead.Complete();
        if (hasHandleColliderCull) handleColliderCull.Complete();
        if (hasHandleBroadPhase) handleBroadPhase.Complete();
        if (hasHandleBroadPhaseClear) handleBroadPhaseClear.Complete();
        if (hasHandleInputInterpolate) handleInputInterpolate.Complete();
        Free();
        if (cullingCameras.IsCreated) cullingCameras.Dispose();
        _memoryBus.Dispose();
    }

    private bool accessArraysDesynced;

    // Scheduling a transform job is not free and is not constant: Unity rebuilds a
    // TransformAccessArray's batch layout on the first Schedule after the array is touched, at
    // O(whole array) — 0.60ms over 32k bones against 0.096ms clean. These four samples say which of
    // the pose chain's schedules is paying it.
    public JobHandle SchedulePoses(double timeAsDouble) {
        if (_memoryBus.transformCount == 0 || accessArraysDesynced) {
            return default;
        }
        jobBulkTransformReset.UpdateArrays(_memoryBus);
        // TODO: This technically only needs to happen for root bones, as their positions are used for posing. Instead just doing a full reset because I'm lazy.
        Profiler.BeginSample("JiggleJobs.SchedulePose.Reset");
        if (hasHandleBulkReset && hasHandleTransformWrite) {
            handleBulkReset = jobBulkTransformReset.Schedule(_memoryBus.GetTransformAccessArray(), JobHandle.CombineDependencies(handleTransformWrite, handleBulkReset));
        } else {
            handleBulkReset = jobBulkTransformReset.Schedule(_memoryBus.GetTransformAccessArray());
        }
        Profiler.EndSample();
        hasHandleBulkReset = true;

        return SchedulePoses(handleBulkReset, timeAsDouble);
    }

    private JobHandle SchedulePoses(JobHandle dep, double timeAsDouble) {
        if (_memoryBus.transformCount == 0) {
            return dep;
        }

        jobBulkReadRoots.UpdateArrays(_memoryBus);
        jobInterpolation.UpdateArrays(_memoryBus);
        jobTransformWrite.UpdateArrays(_memoryBus);

        Profiler.BeginSample("JiggleJobs.SchedulePose.RootRead");
        handleRootRead = jobBulkReadRoots.ScheduleReadOnly(_memoryBus.GetTransformRootAccessArray(), 128, dep);
        Profiler.EndSample();
        hasHandleRootRead = true;

        jobInterpolation.currentTime = timeAsDouble;
        Profiler.BeginSample("JiggleJobs.SchedulePose.Interpolate");
        handleInterpolate = jobInterpolation.ScheduleParallel(_memoryBus.transformCount, 128, handleRootRead);
        Profiler.EndSample();
        hasHandleInterpolate = true;

        Profiler.BeginSample("JiggleJobs.SchedulePose.Write");
        handleTransformWrite = jobTransformWrite.Schedule(_memoryBus.GetTransformAccessArray(), handleInterpolate);
        Profiler.EndSample();

        hasHandleTransformWrite = true;
        return handleTransformWrite;
    }

    public void CompletePoses() {
        if (hasHandleTransformWrite) {
            handleTransformWrite.Complete();
        }
        // The first-pose branch in SchedulePoses schedules the reset with no dependency
        // edge back into the write chain, so join the earlier pose stages explicitly
        // rather than relying on transitivity. No-ops when already covered.
        if (hasHandleRootRead) {
            handleRootRead.Complete();
        }
        if (hasHandleBulkReset) {
            handleBulkReset.Complete();
        }
    }

    public void FreeOnComplete(IntPtr pointer) {
        freePointers.Add(pointer);
    }

    public void FreeOnCommitFlip(IntPtr pointer) {
        _memoryBus.FreeOnCommitFlip(pointer);
    }

    /// <summary>
    /// Blocks until the in-flight simulate job (if any) has finished. Must be called before any
    /// main-thread write to tree point/parameter buffers (JiggleTree.Set, SetParameters) — those
    /// buffers are read and written by the job, and Simulate() only completes it after CommitTrees
    /// has already consumed the mutated data.
    /// </summary>
    public void CompleteSimulate() {
        if (hasHandleSimulate) {
            handleSimulate.Complete();
        }
    }

    private void Free() {
        var freePointerCount = freePointers.Count;
        for (int i = 0; i < freePointerCount; i++) {
            unsafe {
                UnsafeUtility.Free((void*)freePointers[i], Allocator.Persistent);
            }
        }
        freePointers.Clear();
    }

    public void Simulate(double simulateTime, double realTime, int substeps, JobHandle externalDependency = default) {
        // CommitTrees/CommitColliders (and the buffer rotation below) mutate the transform
        // access arrays and buffers the pose chain is scheduled over. The host's pose fence
        // can be skipped on an exception frame, and hosts may call ScheduleSimulate between
        // SchedulePoses and CompletePose — join the pose chain first. No-op on healthy frames.
        CompletePoses();
        if (_memoryBus.transformCount == 0) {
            _memoryBus.CommitTrees();
            _memoryBus.CommitColliders();
            jobSimulate.UpdateArrays(_memoryBus);
            jobBulkTransformRead.UpdateArrays(_memoryBus);
            jobBulkTransformReadReset.UpdateArrays(_memoryBus);
            jobBulkReadRoots.UpdateArrays(_memoryBus);
            jobInterpolation.UpdateArrays(_memoryBus);
            jobBulkPersonalColliderTransformRead.UpdateArrays(_memoryBus.personalColliders);
            jobBulkSceneColliderTransformRead.UpdateArrays(_memoryBus.sceneColliders);
            jobTransformWrite.UpdateArrays(_memoryBus);
            jobBroadPhase.UpdateArrays(_memoryBus);
            jobColliderCull.UpdateArrays(_memoryBus);
            jobBroadPhaseClear.UpdateArrays(_memoryBus);
            jobInputInterpolation.UpdateArrays(_memoryBus);
            return;
        }

        // TODO: Use an external monobehavior to update gravity?
        var gravity = Physics.gravity;
        if (hasHandleSimulate) {
            Profiler.BeginSample("JiggleJobs.Simulate.CompletePrevious");
            handleSimulate.Complete();
            Profiler.EndSample();
            Free();
            OnFinishSimulate?.Invoke(this, simulateTime);
        }

        Profiler.BeginSample("JiggleJobs.Simulate.Teleports");
        _memoryBus.ApplyPendingTeleports();
        Profiler.EndSample();

        _memoryBus.ApplyPendingGrabConstraints();

        _memoryBus.RotateBuffers();
        jobInterpolation.previousTimeStamp = jobInterpolation.timeStamp;
        jobInterpolation.timeStamp = jobSimulate.timeStamp;
        jobInputInterpolation.previousTimeStamp = jobInputInterpolation.timeStamp;
        jobInputInterpolation.timeStamp = realTime;
        jobInputInterpolation.currentTime = simulateTime;
        
        Profiler.BeginSample("JiggleJobs.Simulate.Commit");
        _memoryBus.CommitTrees();
        _memoryBus.CommitColliders();
        Profiler.EndSample();

        // A bone destroyed while still enrolled shifts every later slot of the access arrays, so
        // until the commit rebuilds them the slot indexing the pose buffers use is wrong and every
        // transform job would cross avatar boundaries. Sitting the frame out costs a frame of
        // jiggle; scheduling over it poses one player's bones from another player's tree.
        accessArraysDesynced = _memoryBus.GetAccessArraysDesynced();
        if (accessArraysDesynced) {
            return;
        }

        jobSimulate.UpdateArrays(_memoryBus);
        jobSimulate.substeps = substeps;
        if (UseMergedTransformReadReset) {
            jobBulkTransformReadReset.UpdateArrays(_memoryBus);
        } else {
            jobBulkTransformReset.UpdateArrays(_memoryBus);
            jobBulkTransformRead.UpdateArrays(_memoryBus);
        }
        jobBulkPersonalColliderTransformRead.UpdateArrays(_memoryBus.personalColliders);
        jobBulkSceneColliderTransformRead.UpdateArrays(_memoryBus.sceneColliders);
        jobBroadPhase.UpdateArrays(_memoryBus);
        jobColliderCull.UpdateArrays(_memoryBus);
        jobBroadPhaseClear.UpdateArrays(_memoryBus);
        jobInputInterpolation.UpdateArrays(_memoryBus);

        frustumCull = (byte)(pendingFrustumCull ? 1 : 0);
        distanceCull = (byte)(pendingDistanceCull ? 1 : 0);
        maxCollisionDistance = pendingMaxDistance;
        cullingCameraCount = pendingCullingCameras != null ? math.min(pendingCullingCameraCount, MaxCullingCameras) : 0;
        for (int ci = 0; ci < cullingCameraCount; ci++) {
            cullingCameras[ci] = pendingCullingCameras[ci];
        }
        var cullingActive = JiggleSettings.CullingEnabled;
        jobColliderCull.cullingCameras = cullingCameras;
        jobColliderCull.cullingCameraCount = cullingActive ? cullingCameraCount : 0;
        jobColliderCull.frustumCull = frustumCull;
        jobColliderCull.distanceCull = distanceCull;
        jobColliderCull.maxCollisionDistance = maxCollisionDistance;
        jobColliderCull.nearKeepRadius = JiggleSettings.CullNearKeepRadius;
        jobColliderCull.frustumMargin = JiggleSettings.CullFrustumMargin;
        CaptureStartupSettings();

        Profiler.BeginSample("JiggleJobs.Simulate.Schedule");
        var colliderReadDependency = hasHandleSimulate ? JobHandle.CombineDependencies(handleSimulate, externalDependency) : externalDependency;
        var personalColliderTransforms = _memoryBus.GetPersonalColliderTransformAccessArray();
        var sceneColliderTransforms = _memoryBus.GetSceneColliderTransformAccessArray();
        handlePersonalColliderRead = personalColliderTransforms.length > 0 ? jobBulkPersonalColliderTransformRead.ScheduleReadOnly(personalColliderTransforms, 128, colliderReadDependency) : colliderReadDependency;
        handleSceneColliderRead = sceneColliderTransforms.length > 0 ? jobBulkSceneColliderTransformRead.ScheduleReadOnly(sceneColliderTransforms, 128, colliderReadDependency) : colliderReadDependency;

        hasHandlePersonalColliderRead = true;
        hasHandleSceneColliderRead = true;
        
        var colliderHandles = JobHandle.CombineDependencies(handlePersonalColliderRead, handleSceneColliderRead);
        
        handleBroadPhaseClear = jobBroadPhaseClear.Schedule();
        hasHandleBroadPhaseClear = true;

        var sceneColliderCount = _memoryBus.sceneColliderCount;
        // Read live rather than captured at startup: the worker pool can be resized at runtime, and
        // the same build runs on a 4 core headset and a 32 thread desktop.
        handleColliderCull = sceneColliderCount > 0
            ? jobColliderCull.Schedule(sceneColliderCount, JiggleJobColliderCull.GetBatchSize(sceneColliderCount, JobsUtility.JobWorkerCount, colliderCullMinBatch), handleSceneColliderRead)
            : handleSceneColliderRead;
        hasHandleColliderCull = true;

        handleBroadPhase = jobBroadPhase.Schedule(JobHandle.CombineDependencies(handleColliderCull, handleBroadPhaseClear));
        hasHandleBroadPhase = true;

        var bulkResetDep = hasHandleTransformWrite
            ? JobHandle.CombineDependencies(colliderHandles, handleTransformWrite)
            : colliderHandles;

        if (UseMergedTransformReadReset) {
            handleBulkRead = jobBulkTransformReadReset.Schedule(_memoryBus.GetTransformAccessArray(), bulkResetDep);
            handleBulkReset = handleBulkRead;
        } else {
            handleBulkReset = jobBulkTransformReset.Schedule(_memoryBus.GetTransformAccessArray(), bulkResetDep);
            handleBulkRead = jobBulkTransformRead.ScheduleReadOnly(_memoryBus.GetTransformAccessArray(), 128, handleBulkReset);
        }
        hasHandleBulkReset = true;
        hasHandleBulkRead = true;

        handleInputInterpolate = jobInputInterpolation.ScheduleParallel(_memoryBus.transformCount, 128, handleBulkRead);
        hasHandleInputInterpolate = true;

        jobSimulate.gravity = gravity;
        jobSimulate.timeStamp = simulateTime;
        handleSimulate = jobSimulate.ScheduleParallel(_memoryBus.treeCount, 1, JobHandle.CombineDependencies(handleBroadPhase, handleInputInterpolate, handlePersonalColliderRead));
        hasHandleSimulate = true;

        JobHandle.ScheduleBatchedJobs();
        Profiler.EndSample();
    }

    public void Teleport(JiggleTree tree, float3 deltaPosition) {
        if (tree == null) return;
        _memoryBus.ScheduleTeleport(tree, deltaPosition);
    }

    public void SetGrabConstraints(JiggleGrabConstraint[] constraints, int count) {
        _memoryBus.SetGrabConstraints(constraints, count);
    }

    public void Teleport(JiggleTree tree, quaternion deltaRotation, float3 pivot, float3 deltaPosition) {
        if (tree == null) return;
        _memoryBus.ScheduleTeleport(tree, deltaRotation, pivot, deltaPosition);
    }

    public void SetTreeBacklog(bool backlogRemains) {
        _memoryBus.SetTreeBacklog(backlogRemains);
    }

    public void MarkAlwaysReadScale(JiggleTree tree) {
        _memoryBus.MarkAlwaysReadScale(tree);
    }

    public void ScheduleAdd(JiggleTree tree) {
        _memoryBus.ScheduleAdd(tree);
    }

    public void ScheduleRemove(JiggleTree tree) {
        _memoryBus.ScheduleRemove(tree);
    }
    
    public void ScheduleAdd(JiggleColliderSerializable collider) {
        _memoryBus.ScheduleAdd(collider);
    }

    public void ScheduleAddBatch(List<JiggleColliderSerializable> colliders) {
        _memoryBus.ScheduleAddBatch(colliders);
    }

    public void ScheduleRemove(JiggleColliderSerializable collider) {
        _memoryBus.ScheduleRemove(collider);
    }

    public void ScheduleRemoveBatch(List<JiggleColliderSerializable> colliders) {
        _memoryBus.ScheduleRemoveBatch(colliders);
    }

    public void GetColliders(out JiggleCollider[] personalColliders, out JiggleCollider[] sceneColliders, out int personalColliderCount, out int sceneColliderCount) {
        _memoryBus.GetColliders(out personalColliders, out sceneColliders, out personalColliderCount, out sceneColliderCount);
    }
    
    public void GetResults(out JiggleTransform[] poses, out JiggleTreeJobData[] trees, out int poseCount, out int treeCount) {
        if (hasHandleSimulate) {
            handleSimulate.Complete();
        }

        if (hasHandleInterpolate) {
            handleInterpolate.Complete();
        }
        _memoryBus.GetResults(out poses, out trees, out poseCount, out treeCount);
    }
    
    public NativeArray<JiggleCollider> GetPersonalColliders(out int personalColliderCount) {
        return _memoryBus.GetPersonalColliders(out personalColliderCount);
    }

    public NativeArray<JiggleCollider> GetSceneColliders(out int sceneColliderCount) {
        return _memoryBus.GetSceneColliders(out sceneColliderCount);
    }

    public NativeArray<JiggleTransform> GetInterpolatedOutputPoses(out int poseCount) {
        return _memoryBus.GetInterpolatedOutputPoses(out poseCount);
    }

    public NativeArray<JiggleTreeJobData> GetTrees(out int treeCount) {
        return _memoryBus.GetTrees(out treeCount);
    }
    
    public int GetTransformCapcity() {
        return _memoryBus.transformCapacity;
    }
    public int GetTransformCount() {
        return _memoryBus.transformCount;
    }

    public int GetPersonalColliderCapacity() {
        return _memoryBus.personalColliderCapacity;
    }
    
    public int GetSceneColliderCapacity() {
        return _memoryBus.sceneColliderCapacity;
    }
    
    public int GetPersonalColliderCount() {
        return _memoryBus.personalColliderCount;
    }
    
    public int GetSceneColliderCount() {
        return _memoryBus.sceneColliderCount;
    }

    public void OnDrawGizmos() {
        if (!hasHandleInterpolate || !hasHandleSimulate || !Application.isEditor) {
            return;
        }

        handleInterpolate.Complete();
        handleSimulate.Complete();
        _memoryBus.GetResults(out var poses, out var trees, out var poseCount, out var treeCount);
        for (int i = 0; i < treeCount; i++) {
            var tree = trees[i];
            for (int o = 0; o < tree.pointCount; o++) {
                unsafe {
                    var pose = poses[o+tree.transformIndexOffset];
                    var point = tree.points[o];
                    if (!pose.isVirtual) {
                        Gizmos.color = Color.cyan;
                        Gizmos.DrawWireSphere(pose.position, point.worldRadius);
                    } else {
                        //Gizmos.color = point.parentIndex == -1 ? Color.crimson : Color.magenta;
                        //Gizmos.DrawWireSphere(point.position, 0.025f);
                    }


                    if (point.childrenCount != 0) {
                        for (int j = 0; j < point.childrenCount; j++) {
                            var childPose = poses[tree.GetChild(o, j) + tree.transformIndexOffset];
                            if (!pose.isVirtual && !childPose.isVirtual) {
                                Gizmos.color = Color.cyan;
                                Gizmos.DrawLine(pose.position, childPose.position);
                            } else {
                                //Gizmos.color = Color.magenta;
                                //Gizmos.DrawLine(point.position, childPoint.position);
                            }
                        }
                    }
                }
            }
        }
    }
}

}