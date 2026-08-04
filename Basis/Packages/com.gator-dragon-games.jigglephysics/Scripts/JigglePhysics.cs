using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;

namespace GatorDragonGames.JigglePhysics {

public static class JigglePhysics {
    private static Dictionary<Transform, JiggleTreeSegment> jiggleRootLookup;
    private static bool _globalDirty = true;
    private static readonly List<Transform> tempTransforms = new ();
    private static readonly List<Vector3> tempRestLocalPositions = new ();
    private static readonly List<Quaternion> tempRestLocalRotations = new ();
    private static readonly List<JiggleSimulatedPoint> tempPoints = new();
    // Child indices for tempPoints at a stride of MAX_CHILDREN, grown by AddChildToPoint. Separate
    // from the point struct so the solver does not stream 128 bytes of it per point per pass.
    private static readonly List<int> tempChildren = new();
    private static readonly List<JigglePointParameters> tempParameters = new ();
    private static readonly List<JiggleCollider> tempColliders = new ();
    private static readonly List<Transform> tempColliderTransforms = new ();
    private static List<JiggleTreeSegment> rootJiggleTreeSegments;
    private static readonly List<JiggleTreeSegment> reparentScratch = new();
    // One reusable valid-child buffer per recursion depth, so Visit enumerates each bone's
    // children once instead of re-scanning the Transform hierarchy. Depth-indexed because a
    // node and its descendants are live on the stack simultaneously; same-depth siblings run
    // sequentially and never alias.
    private static readonly List<List<Transform>> visitChildListPool = new();
    private static bool initializedRendering = false;
    private static int skips = 0;

    public const float MERGE_DISTANCE = 0.001f;

    private static JiggleJobs jobs;
    private static bool hasRunThisFrame;
    
    private static double accumulator;
    private static double lastFixedCurrentTime = 0f;

    private const int MaxCullingCameras = 16;
    private static readonly JiggleCullingCamera[] cullingCameraBuffer = new JiggleCullingCamera[MaxCullingCameras];
    private static int cullingCameraCount;
    private static bool collisionFrustumCull = false;
    private static bool collisionDistanceCull = false;
    private static float collisionCullDistance = 20f;
    private static readonly Plane[] cullingFrustumScratch = new Plane[6];

    /// <summary>
    /// True while a tree rebuild is pending. The rebuild measures rest lengths off live bone
    /// positions, so a caller that poses bones with its own jobs has to let them land before
    /// preparing — on every other frame the prepare touches no transform at all.
    /// </summary>
    public static bool WillRebuildTrees => _globalDirty;

    private static double pendingRealTime;
    private static bool preparedThisFrame;

    /// <summary>
    /// The main-thread half of <see cref="ScheduleSimulate"/> — substep accounting, parameter
    /// pushes, and any pending tree or collider commit — stopping short of actually scheduling.
    /// Returns true when there is work to dispatch.
    ///
    /// Split out so a caller can slot other work between the preparation and the schedule. Nothing
    /// here reads a bone pose unless <see cref="WillRebuildTrees"/> is set, so on a steady frame
    /// another system's jobs can still be in flight over those same bones while this runs.
    /// </summary>
    public static bool PrepareSimulate(double realTime, float fixedDeltaTime) {
        preparedThisFrame = false;
        if (hasRunThisFrame) {
            return false;
        }

        if (!(realTime - lastFixedCurrentTime > fixedDeltaTime)) return false;

        while (realTime - lastFixedCurrentTime > fixedDeltaTime) {
            lastFixedCurrentTime += fixedDeltaTime;
            skips = Mathf.Min(skips + 1, JiggleSettings.MaxSubsteps);
        }

        // Only the animated registry is walked — the old two full-root loops paid an interface
        // call per root per frame (the dominant prepare cost at scale) to find the usually-empty
        // animated set. UpdateParametersIfNeeded still self-guards on the live provider flag.
        RebuildAnimatedRootsIfNeeded();
        int animatedCount = animatedRootSegments.Count;

        bool mutates = _globalDirty || animatedCount > 0;
        if (mutates) {
            jobs?.CompleteSimulate();
        }

        for (int i = 0; i < animatedCount; i++) {
            animatedRootSegments[i].UpdateParametersIfNeeded();
        }

        jobs = GetJiggleJobs(lastFixedCurrentTime, fixedDeltaTime);
        jobs.SetCollisionCulling(collisionFrustumCull, collisionDistanceCull, collisionCullDistance, cullingCameraBuffer, cullingCameraCount);

        pendingRealTime = realTime;
        preparedThisFrame = true;
        return true;
    }

    /// <summary>
    /// The scheduling half. Only does anything after a <see cref="PrepareSimulate"/> that returned
    /// true, so calling it unconditionally is safe.
    /// </summary>
    public static void DispatchSimulate(JobHandle externalDependency = default) {
        if (!preparedThisFrame) {
            return;
        }
        preparedThisFrame = false;

        jobs.Simulate(lastFixedCurrentTime, pendingRealTime, skips, externalDependency);
        skips = 0;
        hasRunThisFrame = true;
    }

    public static void ScheduleSimulate(double realTime, float fixedDeltaTime) {
        if (hasRunThisFrame) {
            return;
        }

        if (!(realTime - lastFixedCurrentTime > fixedDeltaTime)) return;

        while (realTime - lastFixedCurrentTime > fixedDeltaTime) {
            lastFixedCurrentTime += fixedDeltaTime;
            skips = Mathf.Min(skips + 1, JiggleSettings.MaxSubsteps);
        }

        RebuildAnimatedRootsIfNeeded();
        int animatedCount = animatedRootSegments.Count;

        // Tree regeneration (JiggleTree.Set) and parameter pushes MemCpy into buffers the
        // in-flight simulate job is still reading/writing — Simulate() completes it too late,
        // after the mutation. Sync first whenever this frame will mutate.
        bool mutatesSimData = _globalDirty || animatedCount > 0;
        if (mutatesSimData) {
            jobs?.CompleteSimulate();
        }

        for (int i = 0; i < animatedCount; i++) {
            animatedRootSegments[i].UpdateParametersIfNeeded();
        }

        jobs = GetJiggleJobs(lastFixedCurrentTime, fixedDeltaTime);
        jobs.SetCollisionCulling(collisionFrustumCull, collisionDistanceCull, collisionCullDistance, cullingCameraBuffer, cullingCameraCount);
        jobs.Simulate(lastFixedCurrentTime, realTime, skips);
        skips = 0;
        hasRunThisFrame = true;
    }

    public static void SchedulePose(double currentTime) {
        hasRunThisFrame = false;
        jobs?.SchedulePoses(currentTime);
    }


    public static void CompletePose() {
        jobs?.CompletePoses();
    }

    public static void CompleteSimulate() {
        jobs?.CompleteSimulate();
    }

    public static void OnDrawGizmos() {
        if (!Application.isPlaying) {
            return;
        }

        jobs?.OnDrawGizmos();
    }
    private static List<JigglePointParameters> parametersCache;
    
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void Initialize() {
        JiggleSettings.ResetBootLatch();
        lastFixedCurrentTime = 0f;
        accumulator = 0;
        parametersCache = new();
        rootJiggleTreeSegments = new List<JiggleTreeSegment>();
        jiggleRootLookup = new Dictionary<Transform, JiggleTreeSegment>();
        animatedRootSegments.Clear();
        animatedRootsDirty = true;
        initializedRendering = false;
        _globalDirty = true;
        jobs?.Dispose();
        jobs = new JiggleJobs(Time.fixedTimeAsDouble, Time.fixedDeltaTime);
    }

    public static void Dispose() {
        jobs?.Dispose();
        JiggleRenderer.Dispose();
        rootJiggleTreeSegments = new List<JiggleTreeSegment>();
        jiggleRootLookup = new Dictionary<Transform, JiggleTreeSegment>();
        animatedRootSegments.Clear();
        animatedRootsDirty = true;
        _globalDirty = true;
        jobs = null;
        JiggleSettings.ResetBootLatch();
        // JiggleRenderer.Dispose released the instancers and chunk buffers, so leaving this latched
        // would skip the OnEnable that rebuilds them and silently stop drawing gizmos for the rest
        // of the session (Initialize only runs again on a domain reload).
        initializedRendering = false;
    }

    public static void ScheduleRender() {
        if (jobs == null) {
            return;
        }
        if (!initializedRendering) {
            JiggleRenderer.OnEnable(jobs);
            initializedRendering = true;
        }
        JiggleRenderer.PrepareRender(jobs);
    }

    public static void CompleteRender(Material proceduralMaterial, Mesh sphere) {
        CompleteRender(proceduralMaterial, sphere, null);
    }

    public static void CompleteRender(Material proceduralMaterial, Mesh sphere, Mesh capsule) {
        if (jobs == null) {
            return;
        }
        if (!initializedRendering) {
            JiggleRenderer.OnEnable(jobs);
            initializedRendering = true;
        }

        JiggleRenderer.FinishRender(proceduralMaterial, sphere, capsule);
    }
    
    public static void SetGlobalDirty() {
        _globalDirty = true;
        animatedRootsDirty = true;
    }

    /// <summary>
    /// Marks every registered tree for a rebuild. Rebuilding re-reads the serialized rest pose and
    /// reseeds the verlet state from the current bone positions, discarding any stale simulation
    /// state — e.g. after an editor undo has moved bones out from under the sim.
    /// </summary>
    public static void SetAllTreesDirty() {
        if (rootJiggleTreeSegments == null) {
            return;
        }
        int count = rootJiggleTreeSegments.Count;
        for (int i = 0; i < count; i++) {
            rootJiggleTreeSegments[i].SetDirty();
        }
    }

    // Roots whose provider animates parameters, rebuilt lazily from rootJiggleTreeSegments so the
    // per-frame parameter push iterates only them instead of interface-calling every root. Every
    // structural change funnels through SetGlobalDirty (or the prune below); runtime flag flips
    // arrive via the segment mirror's MarkAnimatedRootsDirty.
    private static readonly List<JiggleTreeSegment> animatedRootSegments = new List<JiggleTreeSegment>();
    private static bool animatedRootsDirty = true;

    public static void MarkAnimatedRootsDirty() => animatedRootsDirty = true;

    private static void RebuildAnimatedRootsIfNeeded() {
        if (!animatedRootsDirty) return;
        animatedRootsDirty = false;
        animatedRootSegments.Clear();
        int count = rootJiggleTreeSegments.Count;
        for (int i = 0; i < count; i++) {
            var seg = rootJiggleTreeSegments[i];
            if (seg.animatedParameters) animatedRootSegments.Add(seg);
        }
    }

    public static void SetCollisionCulling(bool frustumCull, bool distanceCull, float maxDistance) {
        collisionFrustumCull = frustumCull;
        collisionDistanceCull = distanceCull;
        collisionCullDistance = maxDistance;
    }

    public static void SetCullingCameras(List<Camera> cameras) {
        int count = 0;
        if (cameras != null) {
            var cameraCount = cameras.Count;
            var expansion = JiggleSettings.CullFrustumExpansion;
            for (int i = 0; i < cameraCount && count < MaxCullingCameras; i++) {
                var cam = cameras[i];
                if (cam == null) {
                    continue;
                }
                if (expansion > 1f) {
                    var projection = cam.projectionMatrix;
                    var inverseExpansion = 1f / expansion;
                    projection.m00 *= inverseExpansion;
                    projection.m01 *= inverseExpansion;
                    projection.m02 *= inverseExpansion;
                    projection.m03 *= inverseExpansion;
                    projection.m10 *= inverseExpansion;
                    projection.m11 *= inverseExpansion;
                    projection.m12 *= inverseExpansion;
                    projection.m13 *= inverseExpansion;
                    GeometryUtility.CalculateFrustumPlanes(projection * cam.worldToCameraMatrix, cullingFrustumScratch);
                } else {
                    GeometryUtility.CalculateFrustumPlanes(cam, cullingFrustumScratch);
                }
                cullingCameraBuffer[count] = new JiggleCullingCamera() {
                    plane0 = PlaneToFloat4(cullingFrustumScratch[0]),
                    plane1 = PlaneToFloat4(cullingFrustumScratch[1]),
                    plane2 = PlaneToFloat4(cullingFrustumScratch[2]),
                    plane3 = PlaneToFloat4(cullingFrustumScratch[3]),
                    plane4 = PlaneToFloat4(cullingFrustumScratch[4]),
                    plane5 = PlaneToFloat4(cullingFrustumScratch[5]),
                    position = cam.transform.position,
                };
                count++;
            }
        }
        cullingCameraCount = count;
    }

    private static float4 PlaneToFloat4(Plane plane) {
        return new float4(plane.normal.x, plane.normal.y, plane.normal.z, plane.distance);
    }

    public static void AddJiggleCollider(JiggleColliderSerializable collider) {
        jobs.ScheduleAdd(collider);
    }

    /// <summary>
    /// Batch-add multiple colliders. Builds a HashSet of existing transforms once,
    /// then adds all new colliders with O(1) dedup instead of O(n) per collider.
    /// </summary>
    public static void AddJiggleColliders(List<JiggleColliderSerializable> colliders) {
        jobs.ScheduleAddBatch(colliders);
    }

    public static void RemoveJiggleCollider(JiggleColliderSerializable collider) {
        jobs?.ScheduleRemove(collider);
    }

    /// <summary>
    /// Batch-remove multiple colliders. Uses a HashSet for O(n+m) dedup
    /// instead of O(n*m) linear scans per collider.
    /// </summary>
    public static void RemoveJiggleColliders(List<JiggleColliderSerializable> colliders) {
        jobs?.ScheduleRemoveBatch(colliders);
    }

    public static unsafe void FreeOnComplete(IntPtr pointer) {
        // Trees outlive the pipeline on shutdown and domain reload, and there is nothing left to
        // defer the free behind at that point. Without this the dispose threw and leaked the buffer,
        // which is why FreeOnCommitFlip below already guards the same way.
        if (jobs != null) {
            jobs.FreeOnComplete(pointer);
        } else {
            UnsafeUtility.Free((void*)pointer, Allocator.Persistent);
        }
    }

    public static unsafe void FreeOnCommitFlip(IntPtr pointer) {
        if (jobs != null) {
            jobs.FreeOnCommitFlip(pointer);
        } else {
            UnsafeUtility.Free((void*)pointer, Allocator.Persistent);
        }
    }
    
    public static void AddJiggleTreeSegment(JiggleTreeSegment jiggleTreeSegment) {
        if (!jiggleRootLookup.TryAdd(jiggleTreeSegment.transform, jiggleTreeSegment)) {
            Debug.LogWarning("Multiple Jiggle trees detected targeting the same root transform, Jiggle Physics doesn't support this.", jiggleTreeSegment.transform);
            return;
        }
        RemoveAddChildren(jiggleTreeSegment.transform);
        TryAddRootJiggleTreeSegment(jiggleTreeSegment);
        SetGlobalDirty();
    }
    
    private static bool GetParentJiggleTreeSegment(Transform t, out JiggleTreeSegment parentJiggleTreeSegment) {
        var current = t;
        while (current.parent != null) {
            current = current.parent;
            if (jiggleRootLookup.TryGetValue(current, out var jiggleTreeSegment)) {
                parentJiggleTreeSegment = jiggleTreeSegment;
                return true;
            }
        }
        parentJiggleTreeSegment = null;
        return false;
    }
    
    private static bool GetJiggleTreeSegmentByBone(Transform t, out JiggleTreeSegment jiggleRoot) {
        if (jiggleRootLookup.TryGetValue(t, out JiggleTreeSegment root)) {
            jiggleRoot = root;
            return true;
        }
        jiggleRoot = null;
        return false;
    }

    private static bool TryAddRootJiggleTreeSegment(JiggleTreeSegment jiggleTreeSegment) {
        var foundParent = GetParentJiggleTreeSegment(jiggleTreeSegment.transform, out var parentJiggleTreeSegment);
        if (foundParent) {
            jiggleTreeSegment.SetParent(parentJiggleTreeSegment);
            parentJiggleTreeSegment.SetDirty();
            return false;
        } else {
            rootJiggleTreeSegments.Add(jiggleTreeSegment);
            return true;
        }
    }

    private static void RemoveAddChildren(Transform t) {
        foreach (Transform child in t) {
            if (jiggleRootLookup.TryGetValue(child, out var jiggleRootSegment)) {
                rootJiggleTreeSegments.Remove(jiggleRootSegment);
                TryAddRootJiggleTreeSegment(jiggleRootSegment);
            }
            RemoveAddChildren(child);
        }
    }
    
    private static JiggleJobs GetJiggleJobs(double fixedTime, float fixedDeltaTime) {
        if (!_globalDirty) {
            jobs?.SetTreeBacklog(false);
            return jobs;
        }
        jobs ??= new JiggleJobs(fixedTime, fixedDeltaTime);
        jobs.SetFixedDeltaTime(fixedDeltaTime);
        bool backlogRemains = GetJiggleTrees();
        _globalDirty = backlogRemains;
        jobs.SetTreeBacklog(backlogRemains);
        return jobs;
    }

    // Out-of-band root-Transform destruction (asset bundle unload, scene teardown, a rig
    // whose OnDisable was skipped) is rare and otherwise handled immediately by
    // RemoveJiggleTreeSegment, so the dead-segment prune — two Unity `== null` checks per root,
    // one of them behind a per-root interface call — is spread as a rolling slice of
    // ~N/PRUNE_CADENCE roots per flush. Same per-root cadence as the old once-per-PRUNE_CADENCE
    // sweep, but without the O(N) spike on the sweep frame.
    private const int PRUNE_CADENCE = 64;
    private static int pruneCursor;

    // <= 0 = unlimited (original behavior). Caps tree (re)builds per dirty flush so a burst of
    // rig enables (many avatars loading the same frame) amortizes over several frames instead
    // of spiking the main thread. Over-budget trees stay dirty and rebuild on later flushes.
    private static int maxTreeRegenerationsPerFlush = 4;
    public static void SetMaxTreeRegenerationsPerFlush(int value) => maxTreeRegenerationsPerFlush = value;

    private static bool GetJiggleTrees() {
        Profiler.BeginSample("JiggleRoot.GetJiggleTrees");
        int count = rootJiggleTreeSegments.Count;

        // This flush's rolling dead-prune window: ~count/PRUNE_CADENCE roots, advancing each flush
        // so every root is validity-checked once per PRUNE_CADENCE flushes without an O(count) spike.
        if (pruneCursor >= count) pruneCursor = 0;
        int sliceSize = math.max(1, (count + PRUNE_CADENCE - 1) / PRUNE_CADENCE);
        int pruneSliceStart = pruneCursor;
        int pruneSliceEnd = math.min(pruneCursor + sliceSize, count);
        pruneCursor = pruneSliceEnd >= count ? 0 : pruneSliceEnd;

        int budget = maxTreeRegenerationsPerFlush;
        bool limited = budget > 0;
        int regenerated = 0;
        bool backlogRemains = false;

        for (int i = count - 1; i >= 0; i--) {
            var seg = rootJiggleTreeSegments[i];
            var currentTree = seg.jiggleTree;
            bool needsRegen = currentTree is not { dirty: false };
            bool inPruneSlice = i >= pruneSliceStart && i < pruneSliceEnd;

            // Cheap path: a clean, already-built tree outside this flush's prune slice needs no
            // work and skips the expensive Unity null + interface-call validity check below.
            if (!needsRegen && !inPruneSlice) {
                continue;
            }
            // Over-budget dirty/new roots defer cheaply — without the validity check — unless the
            // prune slice covers them this flush. This is the hot fix: a backlog of dirty roots no
            // longer pays the per-root interface call + Unity null checks every single flush while
            // waiting its turn under the regeneration budget; only the ≤budget roots actually being
            // rebuilt (plus the small rolling prune slice) hit the expensive check.
            if (needsRegen && !inPruneSlice && limited && regenerated >= budget) {
                backlogRemains = true;
                continue;
            }
            if (seg.transform == null || seg.jiggleRigData.rootBone == null) {
                if (seg.jiggleTree is { dirty: false }) ScheduleRemoveJiggleTree(seg.jiggleTree);
                jiggleRootLookup.Remove(seg.transform);
                rootJiggleTreeSegments.RemoveAt(i);
                animatedRootsDirty = true;
                continue;
            }
            if (!needsRegen) {
                continue;
            }
            if (limited && regenerated >= budget) {
                backlogRemains = true;
                continue;
            }
            seg.RegenerateJiggleTreeIfNeeded();
            jobs.ScheduleAdd(seg.jiggleTree);
            regenerated++;
        }
        Profiler.EndSample();
        return backlogRemains;
    }

    public static JiggleTree CreateJiggleTree(JiggleRigData jiggleRig, JiggleTree tree) {
        Profiler.BeginSample("JiggleTreeUtility.CreateJiggleTree");
        tempTransforms.Clear();
        tempPoints.Clear();
        tempChildren.Clear();
        tempParameters.Clear();
        tempRestLocalPositions.Clear();
        tempRestLocalRotations.Clear();
        jiggleRig.GetJiggleColliders(tempColliders);
        jiggleRig.GetJiggleColliderTransforms(tempColliderTransforms);
        if (!jiggleRig.GetCacheIsValid()) jiggleRig.BuildNormalizedDistanceFromRootList();
        var backProjection = Vector3.zero;
        var backProjectionChildCount = jiggleRig.GetValidChildrenCount(jiggleRig.rootBone);
        if (backProjectionChildCount != 0) {
            var pos = jiggleRig.rootBone.position;
            var childPos = jiggleRig.GetValidChild(jiggleRig.rootBone, 0).position;
            var diff = pos - childPos;
            // An exporter helper bone sitting exactly on the root leaves nothing to project along, and
            // a virtual root landing on the root bone makes Visit treat the root as zero length and
            // merge it away, dropping it from the simulation entirely.
            if (diff.sqrMagnitude < MERGE_DISTANCE * MERGE_DISTANCE) {
                diff = jiggleRig.rootBone.up * 0.25f;
            }
            backProjection = pos + diff;
        } else {
            backProjection = jiggleRig.rootBone.position + jiggleRig.rootBone.up * 0.25f;
        }
        tempPoints.Add(new JiggleSimulatedPoint() { // Back projected virtual root
            position = backProjection,
            lastPosition = backProjection,
            childrenCount = 0,
            parentIndex = -1,
            hasTransform = false,
            animated = false,
        });
        tempParameters.Add(jiggleRig.GetJiggleBoneParameter(0f));
        tempTransforms.Add(jiggleRig.rootBone);
        jiggleRig.rootBone.GetLocalPositionAndRotation(out var localPosition, out var localRotation);
        tempRestLocalPositions.Add(localPosition);
        tempRestLocalRotations.Add(localRotation);
        Visit(jiggleRig.rootBone, tempTransforms, tempPoints, tempParameters, tempRestLocalPositions, tempRestLocalRotations, 0, jiggleRig, backProjection, 0f, 0, out int childIndex);
        if (childIndex != -1) {
            AddChildToPoint(tempPoints, 0, childIndex);
        }

        // Points with no children never reached AddChildToPoint, so size the list for the full set
        // before handing it over.
        EnsureChildCapacity(tempPoints.Count);

        Profiler.EndSample();
        if (tree != null) {
            tree.Set(tempTransforms, tempPoints, tempParameters, tempColliderTransforms, tempColliders, tempRestLocalPositions, tempRestLocalRotations, tempChildren);
            return tree;
        } else {
            return new JiggleTree(tempTransforms, tempPoints, tempParameters, tempColliderTransforms, tempColliders, tempRestLocalPositions, tempRestLocalRotations, tempChildren);
        }
    }

    public static void VisitForLength(Transform t, JiggleRigData rig, Vector3 lastPosition, float currentLength, out float totalLength) {
        if (t == null || rig.GetIsExcluded(t)) {
            totalLength = Mathf.Max(currentLength, 0.001f);
            return;
        }
        currentLength += Vector3.Distance(lastPosition, t.position);
        totalLength = Mathf.Max(currentLength, 0.001f);
        var validChildrenCount = rig.GetValidChildrenCount(t);
        for (int i = 0; i < validChildrenCount; i++) {
            var child = rig.GetValidChild(t, i);
            VisitForLength(child, rig, t.position, currentLength, out var siblingMaxLength);
            totalLength = Mathf.Max(totalLength, siblingMaxLength);
        }
    }

    /// <summary>
    /// Ceiling on bone-chain recursion depth. JiggleTreeDepthTests builds 32 and 64 successfully and
    /// overflows at 128, and the real limit is lower under IL2CPP than in the editor, so a chain past
    /// this is truncated rather than allowed to abort the process.
    /// </summary>
    internal const int MAX_VISIT_DEPTH = 64;

    private static void Visit(Transform t, List<Transform> transforms, List<JiggleSimulatedPoint> points, List<JigglePointParameters> parameters, List<Vector3> restLocalPositions, List<Quaternion> restLocalRotations, int parentIndex, JiggleRigData lastJiggleRig, Vector3 lastPosition, float currentLength, int depth, out int newIndex) {
        if (t == null) {
            newIndex = -1;
            return;
        }
        if (depth >= MAX_VISIT_DEPTH) {
            newIndex = -1;
            return;
        }
        if (Application.isPlaying && GetJiggleTreeSegmentByBone(t, out JiggleTreeSegment currentJiggleTreeSegment)) {
            lastJiggleRig = currentJiggleTreeSegment.jiggleRigData;
        }
        if (!lastJiggleRig.GetIsExcluded(t)) {
            var children = BorrowVisitChildList(depth);
            lastJiggleRig.GetValidChildrenInto(t, children);
            var validChildrenCount = children.Count;
            var currentPosition = t.position;
            var cache = lastJiggleRig.GetCache(t);
            if (Vector3.Distance(currentPosition, lastPosition) < MERGE_DISTANCE) {
                if (validChildrenCount > 0) {
                    for (int i = 0; i < validChildrenCount; i++) {
                        var child = children[i];
                        Visit(child, transforms, points, parameters, restLocalPositions, restLocalRotations, parentIndex, lastJiggleRig, lastPosition, currentLength, depth + 1, out int childIndex);
                        if (childIndex != -1) {
                            AddChildToPoint(points, parentIndex, childIndex);
                        }
                    }
                    newIndex = -1;
                } else {
                    transforms.Add(t);
                    restLocalPositions.Add(cache.restLocalPosition);
                    restLocalRotations.Add(new Quaternion(cache.restLocalRotation.x, cache.restLocalRotation.y, cache.restLocalRotation.z, cache.restLocalRotation.w));
                    var projDir = currentPosition - lastPosition;
                    if (projDir.sqrMagnitude < MERGE_DISTANCE * MERGE_DISTANCE) {
                        var parentPoint = points[parentIndex];
                        if (parentPoint.parentIndex >= 0) {
                            projDir = (Vector3)(parentPoint.position - points[parentPoint.parentIndex].position);
                        }
                        if (projDir.sqrMagnitude < MERGE_DISTANCE * MERGE_DISTANCE) {
                            projDir = t.up * 0.1f;
                        }
                    }
                    var tipPos = currentPosition + projDir;
                    points.Add(new JiggleSimulatedPoint() { // virtual projected tip
                        position = tipPos,
                        lastPosition = tipPos,
                        childrenCount = 0,
                        distanceFromRoot = currentLength,
                        parentIndex = parentIndex,
                        hasTransform = false,
                        animated = false,
                    });
                    parameters.Add(lastJiggleRig.GetJiggleBoneParameter(cache.normalizedDistanceFromRoot));
                    // Registering here as well as returning the index had every caller add this tip
                    // to its parent a second time, inflating childrenCount and making the multi child
                    // averaging in ApplyPose weight the same tip twice.
                    newIndex = points.Count - 1;
                }
                return;
            }
            transforms.Add(t);
            restLocalPositions.Add(cache.restLocalPosition);
            restLocalRotations.Add(new Quaternion(cache.restLocalRotation.x, cache.restLocalRotation.y, cache.restLocalRotation.z, cache.restLocalRotation.w));
            var parameter = lastJiggleRig.GetJiggleBoneParameter(cache.normalizedDistanceFromRoot);
            if ((lastJiggleRig.excludeRoot && t == lastJiggleRig.rootBone) || lastJiggleRig.GetIsExcluded(t)) {
                parameter = new JigglePointParameters() {
                    angleElasticity = 1f,
                    lengthElasticity = 1f,
                    rootElasticity = 1f,
                    elasticitySoften = 0f
                };
            }

            if (points[parentIndex].hasTransform) {
                currentLength += Vector3.Distance(lastPosition, currentPosition);
            }


            points.Add(new JiggleSimulatedPoint() { // Regular point
                position = currentPosition,
                lastPosition = currentPosition,
                childrenCount = 0,
                distanceFromRoot = currentLength,
                parentIndex = parentIndex,
                hasTransform = true,
                animated = true,
            });
            parameters.Add(parameter);
            newIndex = points.Count - 1;

            if (validChildrenCount == 0) {
                transforms.Add(t);
                restLocalPositions.Add(cache.restLocalPosition);
                restLocalRotations.Add(new Quaternion(cache.restLocalRotation.x, cache.restLocalRotation.y, cache.restLocalRotation.z, cache.restLocalRotation.w));
                points.Add(new JiggleSimulatedPoint() { // virtual projected tip
                    position = currentPosition + (currentPosition - lastPosition),
                    lastPosition = currentPosition + (currentPosition - lastPosition),
                    childrenCount = 0,
                    distanceFromRoot = currentLength,
                    parentIndex = newIndex,
                    hasTransform = false,
                    animated = false,
                });
                parameters.Add(lastJiggleRig.GetJiggleBoneParameter(cache.normalizedDistanceFromRoot));
                AddChildToPoint(points, newIndex, points.Count - 1);
            } else {
                for (int i = 0; i < validChildrenCount; i++) {
                    var child = children[i];
                    Visit(child, transforms, points, parameters, restLocalPositions, restLocalRotations, newIndex, lastJiggleRig, currentPosition, currentLength, depth + 1, out int childIndex);
                    if (childIndex != -1) {
                        AddChildToPoint(points, newIndex, childIndex);
                    }
                }
            }
        } else {
            newIndex = -1;
        }

    }

    private static List<Transform> BorrowVisitChildList(int depth) {
        while (visitChildListPool.Count <= depth) {
            visitChildListPool.Add(new List<Transform>());
        }
        var list = visitChildListPool[depth];
        list.Clear();
        return list;
    }

    /// <summary>
    /// Records a child on the point at <paramref name="pointIndex"/>. The indices live in
    /// <see cref="tempChildren"/> rather than in the point, so this is the only writer and it grows
    /// that list to cover every point added so far.
    /// </summary>
    private static void AddChildToPoint(List<JiggleSimulatedPoint> points, int pointIndex, int childIndex) {
        var point = points[pointIndex];
        if (point.childrenCount>=JiggleSimulatedPoint.MAX_CHILDREN) {
            Debug.LogWarning($"JigglePhysics: Bone exceeded maximum of {JiggleSimulatedPoint.MAX_CHILDREN} children, extra children will be ignored.");
            return;
        }
        EnsureChildCapacity(points.Count);
        tempChildren[pointIndex * JiggleSimulatedPoint.MAX_CHILDREN + point.childrenCount] = childIndex;
        point.childrenCount++;
        points[pointIndex] = point;
    }

    /// <summary>Grows the child scratch list to cover <paramref name="pointCount"/> points, zero filled.</summary>
    private static void EnsureChildCapacity(int pointCount) {
        var required = pointCount * JiggleSimulatedPoint.MAX_CHILDREN;
        while (tempChildren.Count < required) {
            tempChildren.Add(0);
        }
    }
    
    /// <summary>
    /// A tree has started pushing parameters outside the commit path, so the slots it already owns
    /// have to start reading their lossy scale. One-shot per tree: the latch never clears.
    /// </summary>
    public static void MarkAlwaysReadScale(JiggleTree jiggleTree) {
        jobs?.MarkAlwaysReadScale(jiggleTree);
    }

    public static void ScheduleRemoveJiggleTree(JiggleTree jiggleTree) {
        jobs?.ScheduleRemove(jiggleTree);
    }
    
    public static void Teleport(JiggleTree tree, float3 deltaPosition) {
        jobs?.Teleport(tree, deltaPosition);
    }

    public static void Teleport(JiggleTree tree, quaternion deltaRotation, float3 pivot, float3 deltaPosition) {
        jobs?.Teleport(tree, deltaRotation, pivot, deltaPosition);
    }

    public static void SetGrabConstraints(JiggleGrabConstraint[] constraints, int count) {
        jobs?.SetGrabConstraints(constraints, count);
    }

    public static void RemoveJiggleTreeSegment(JiggleTreeSegment jiggleTreeSegment) {
        if (rootJiggleTreeSegments.Contains(jiggleTreeSegment)) {
            rootJiggleTreeSegments.Remove(jiggleTreeSegment);
        }

        jiggleRootLookup.Remove(jiggleTreeSegment.transform);

        // Re-parent the removed segment's direct children to its parent. Driven off the
        // segment's own child list (O(children)) instead of scanning every registered segment,
        // which was O(N) per removal and O(N^2) across a mass disconnect. Snapshot into a
        // scratch list first because SetParent mutates the live child list as we go.
        var children = jiggleTreeSegment.GetChildren();
        if (children != null && children.Count > 0) {
            reparentScratch.Clear();
            reparentScratch.AddRange(children);
            for (int i = 0; i < reparentScratch.Count; i++) {
                var child = reparentScratch[i];
                child.SetParent(jiggleTreeSegment.parent);
                if (child.parent == null && !rootJiggleTreeSegments.Contains(child)) {
                    rootJiggleTreeSegments.Add(child);
                }
            }
            reparentScratch.Clear();
        }

        jiggleTreeSegment.SetDirty();

        if (jiggleTreeSegment.parent != null) {
            jiggleTreeSegment.parent.SetDirty();
            jiggleTreeSegment.SetParent(null);
        }

        SetGlobalDirty();
    }

}

}