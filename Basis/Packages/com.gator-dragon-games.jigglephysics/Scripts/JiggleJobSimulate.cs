using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace GatorDragonGames.JigglePhysics {

[BurstCompile]
public struct JiggleJobSimulate : IJobFor {
    // TODO: doubles are strictly a bad way to track time, probably should be ints or longs.
    public double timeStamp;
    public float3 gravity;
    public int substeps;
    
    public int sceneColliderCount;

    [ReadOnly] [NativeDisableParallelForRestriction]
    public NativeArray<JiggleTransform> inputPoses;

    [NativeDisableParallelForRestriction] public NativeArray<PoseData> outputPoses;
    [ReadOnly,NativeDisableParallelForRestriction] public NativeArray<JiggleCollider> personalColliders;
    [ReadOnly,NativeDisableParallelForRestriction] public NativeArray<JiggleCollider> sceneColliders;
    
    [ReadOnly] public NativeHashMap<int2,JiggleGridCell> broadPhaseMap;
    [ReadOnly] public NativeReference<JiggleGridCell> globalCell;

    [ReadOnly] public NativeParallelMultiHashMap<int, JiggleGrabConstraint> grabConstraints;
    public int grabConstraintCount;

    public NativeArray<JiggleTreeJobData> jiggleTrees;

    private float deltaTimeSquared;

    public float inverseCellSize;
    public int maxTreeCellSpan;

    public JiggleJobSimulate(JiggleMemoryBus bus, float fixedDeltaTime) {
        inputPoses = bus.simulateInputPoses;
        jiggleTrees = bus.jiggleTreeStructs;
        outputPoses = bus.simulationOutputPoseData;
        personalColliders = bus.personalColliders;
        sceneColliders = bus.sceneColliders;
        timeStamp = Time.timeAsDouble;
        broadPhaseMap = bus.broadPhaseMap;
        globalCell = bus.globalCell;
        grabConstraints = bus.grabConstraints;
        grabConstraintCount = bus.grabConstraintCount;
        gravity = Physics.gravity;
        sceneColliderCount = 0;
        deltaTimeSquared = fixedDeltaTime * fixedDeltaTime;
        substeps = 1;
        inverseCellSize = JiggleSettings.InverseBroadPhaseCellSize;
        maxTreeCellSpan = JiggleSettings.MaxTreeCellSpan;
    }

    public void UpdateArrays(JiggleMemoryBus bus) {
        inputPoses = bus.simulateInputPoses;
        jiggleTrees = bus.jiggleTreeStructs;
        outputPoses = bus.simulationOutputPoseData;
        personalColliders = bus.personalColliders;
        sceneColliders = bus.sceneColliders;
        sceneColliderCount = bus.sceneColliderCount;
        broadPhaseMap = bus.broadPhaseMap;
        globalCell = bus.globalCell;
        grabConstraints = bus.grabConstraints;
        grabConstraintCount = bus.grabConstraintCount;
    }
    
    public void SetFixedDeltaTime(float fixedDeltaTime) {
        deltaTimeSquared = fixedDeltaTime * fixedDeltaTime;
    }


    private unsafe void Cache(JiggleTreeJobData tree, out int2 minExtentPosition, out int2 maxExtentPosition) {
        float3 min = new float3(float.MaxValue);
        float3 max = new float3(float.MinValue);
        
        // Everything here addresses points through the buffer rather than copying them out and back:
        // a by-value read-modify-write moved the whole struct twice to update five float3s, and each
        // parent and child read cost another copy. Same arithmetic, same order, same results.
        for (int i = 0; i < tree.pointCount; i++) {
            var point = tree.points + i;
            var parameters = tree.parameters + i;
            if (point->parentIndex == -1) {
                // virtual root particles
                var childSlot = tree.GetChild(i, 0);
                var child = tree.points + childSlot;
                var childChild = tree.points + tree.GetChild(childSlot, 0);
                var childPose = tree.GetInputPose(inputPoses, childSlot);
                var childChildPose = tree.GetInputPose(inputPoses, tree.GetChild(childSlot, 0));
                if (!childChild->hasTransform) {
                    // edge case where it's a singular isolated root bone
                    point->pose = childPose.position - new float3(0f, 0.25f, 0f);
                    point->parentPose = childPose.position - new float3(0f, 0.5f, 0f);
                    point->desiredLengthToParent = 0.25f;
                } else {
                    var diff = childPose.position - childChildPose.position;
                    point->pose = childPose.position + diff;
                    point->parentPose = childPose.position + diff * 2f;
                    point->desiredLengthToParent = math.length(diff);
                }

                point->worldRadius = 0f;
                point->workingPosition = point->pose;
            } else if (point->hasTransform) {
                // "real" particles
                var inputPose = tree.GetInputPose(inputPoses, i);
                var parent = tree.points + point->parentIndex;
                point->pose = inputPose.position;
                point->parentPose = parent->pose;
                point->desiredLengthToParent = math.distance(point->pose, parent->pose);
                var averagePointScale = (math.abs(inputPose.scale.x) + math.abs(inputPose.scale.y) + math.abs(inputPose.scale.z)) / 3f;
                point->worldRadius = parameters->collisionRadius * averagePointScale;
                min = math.min(min, point->position-new float3(point->worldRadius));
                max = math.max(max, point->position+new float3(point->worldRadius));
            } else {
                // virtual end particles
                var parent = tree.points + point->parentIndex;
                point->pose = (parent->pose * 2f - parent->parentPose);
                point->parentPose = parent->pose;
                point->desiredLengthToParent = math.distance(point->pose, point->parentPose);
                point->worldRadius = 0f;
            }
        }

        minExtentPosition = JiggleGridCell.GetKeyForPosition(min, inverseCellSize);
        maxExtentPosition = JiggleGridCell.GetKeyForPosition(max, inverseCellSize);
    }

    private unsafe void VerletIntegrate(JiggleTreeJobData tree) {

        var rootPosition = tree.points[0].workingPosition;
        var rootLastPosition = tree.points[0].position;
        var rootDelta = rootPosition - rootLastPosition;

        // One pass, not two. The root-motion shift used to walk every point before the integration
        // walked them again to read the shifted values. Points are stored parent before child — Cache
        // and Constrain both already depend on that, reading a parent that was updated earlier in the
        // same loop — so by the time a point integrates, its parent carries exactly the values the
        // second pass used to read. Same arithmetic on the same inputs, one walk of the point buffer
        // instead of two, and no by-value copy in or out.
        for (int i = 0; i < tree.pointCount; i++) {
            var point = tree.points + i;
            if (point->parentIndex == -1) {
                continue;
            }
            var ownParameters = tree.parameters + i;
            var rootShift = rootDelta * ownParameters->ignoreRootMotion;
            point->lastPosition += rootShift;
            point->position += rootShift;

            var parent = tree.points + point->parentIndex;
            var delta = point->position - point->lastPosition;
            var parentDelta = parent->position - parent->lastPosition;
            var localSpaceVelocity = delta - parentDelta;
            var velocity = delta - localSpaceVelocity;
            var parameters = parent->parentIndex != -1 ? tree.parameters + point->parentIndex : ownParameters;
            point->workingPosition = point->position
                                     + velocity * (1f - parameters->airDrag)
                                     +localSpaceVelocity * (1f - parameters->drag)
                                     + gravity * parameters->gravityMultiplier * deltaTimeSquared;
        }
    }

    quaternion FromToRotationFromNormalizedVectors(float3 from, float3 to) {
        var c = math.cross(from, to);
        var q = new float4(c.x, c.y, c.z, 1f + math.dot(from, to));
        var lenSq = math.dot(q, q);
        if (lenSq < 1e-12f) {
            return new quaternion(0f, 0f, 1f, 0f);
        }
        return new quaternion(q * math.rsqrt(lenSq));
    }

    float float3Angle(float3 a, float3 b) {
        return math.acos(math.dot(
                    math.normalizesafe(a, new float3(0,0,1)), 
                    math.normalizesafe(b, new float3(0,0,1))
                    ));
    }
    

    private unsafe float3 DoDepenetration(JiggleSimulatedPoint* point, JiggleSimulatedPoint* otherPoint, JigglePointParameters* otherPointParameters, JiggleCollider collider) {
        if (!collider.enabled || !point->hasTransform || !otherPoint->hasTransform || point->worldRadius == 0f || otherPoint->worldRadius == 0f) {
            return new float3(0f, 0f, 0f);
        }
        switch (collider.type) {
            case JiggleCollider.JiggleColliderType.Sphere: {
                var hardness = 1f;
                var colliderPosition = collider.localToWorldMatrix.c3.xyz;
                var pointPosition = point->workingPosition;
                var otherPosition = otherPoint->workingPosition;
                var pointRadius = point->worldRadius;
                var colliderRadius = collider.worldRadius;
                var combinedRadius = pointRadius + colliderRadius;
                var boneClosestPoint = GetClosestPointOnLineSegment(
                        colliderPosition,
                        pointPosition,
                        otherPosition,
                        out var tValue
                        );
                var sphere_diff = boneClosestPoint - colliderPosition;
                var sphere_distance = math.length(sphere_diff);
                var depenetrationMagnitude = combinedRadius - sphere_distance;
                if (depenetrationMagnitude <= 0f) {
                    return float3.zero;
                }
                var depenetrationDir = math.normalizesafe(sphere_diff, new float3(0, 0, 1));
                var depenetrationVector = depenetrationDir * depenetrationMagnitude;
                var pValue = math.clamp(2f - tValue * 2f, 0f, 1f);
                depenetrationVector *= hardness * pValue;
                // TODO: find decent rigidbody solve instead of just pushing them both naively
                sphere_diff = pointPosition - colliderPosition;
                sphere_distance = math.length(sphere_diff);
                depenetrationMagnitude = combinedRadius - sphere_distance;
                if (depenetrationMagnitude > 0f) {
                    depenetrationDir = math.normalizesafe(sphere_diff, new float3(0, 0, 1));
                    var depenetrationVector2 = depenetrationDir * depenetrationMagnitude;
                    depenetrationVector2 *= hardness;
                    var mag1 = math.length(depenetrationVector);
                    var mag2 = math.length(depenetrationVector2);
                    depenetrationVector = (depenetrationVector+depenetrationVector2)*0.5f;
                    depenetrationVector = math.normalizesafe(depenetrationVector, new float3(0,0,1)) * math.max(mag1, mag2);
                }
                if (!(otherPointParameters->angleElasticity == 1f
                      && otherPointParameters->rootElasticity == 1f
                      && otherPointParameters->lengthElasticity == 1f)) {
                    return depenetrationVector;
                }
                break;
            }
            case JiggleCollider.JiggleColliderType.Capsule: {
                var hardness = 1f;
                var colliderPosition = collider.localToWorldMatrix.c3.xyz;
                var colliderUp = collider.GetWorldAxis();
                var halfHeight = collider.worldHeight * 0.5f;
                var capsuleA = colliderPosition - colliderUp * halfHeight;
                var capsuleB = colliderPosition + colliderUp * halfHeight;
                var pointPosition = point->workingPosition;
                var otherPosition = otherPoint->workingPosition;
                var pointRadius = point->worldRadius;
                var colliderRadius = collider.worldRadius;
                var combinedRadius = pointRadius + colliderRadius;
                // Find closest points between capsule axis and bone segment
                ClosestPointsOnTwoSegments(capsuleA, capsuleB, pointPosition, otherPosition, out var closestOnCapsule, out var closestOnBone, out var tValueBone);
                var cap_diff = closestOnBone - closestOnCapsule;
                var cap_distance = math.length(cap_diff);
                var cap_depenetrationMagnitude = combinedRadius - cap_distance;
                if (cap_depenetrationMagnitude <= 0f) {
                    return float3.zero;
                }
                var cap_depenetrationDir = math.normalizesafe(cap_diff, new float3(0, 0, 1));
                var cap_depenetrationVector = cap_depenetrationDir * cap_depenetrationMagnitude;
                var cap_pValue = math.clamp(2f - tValueBone * 2f, 0f, 1f);
                cap_depenetrationVector *= hardness * cap_pValue;
                // Point-to-capsule direct check
                ClosestPointOnSegment(pointPosition, capsuleA, capsuleB, out var closestOnCapsuleToPoint);
                cap_diff = pointPosition - closestOnCapsuleToPoint;
                cap_distance = math.length(cap_diff);
                cap_depenetrationMagnitude = combinedRadius - cap_distance;
                if (cap_depenetrationMagnitude > 0f) {
                    cap_depenetrationDir = math.normalizesafe(cap_diff, new float3(0, 0, 1));
                    var cap_depenetrationVector2 = cap_depenetrationDir * cap_depenetrationMagnitude;
                    cap_depenetrationVector2 *= hardness;
                    var mag1 = math.length(cap_depenetrationVector);
                    var mag2 = math.length(cap_depenetrationVector2);
                    cap_depenetrationVector = (cap_depenetrationVector + cap_depenetrationVector2) * 0.5f;
                    cap_depenetrationVector = math.normalizesafe(cap_depenetrationVector, new float3(0, 0, 1)) * math.max(mag1, mag2);
                }
                if (!(otherPointParameters->angleElasticity == 1f
                      && otherPointParameters->rootElasticity == 1f
                      && otherPointParameters->lengthElasticity == 1f)) {
                    return cap_depenetrationVector;
                }
                break;
            }
            case JiggleCollider.JiggleColliderType.Plane: {
                var colliderPosition = collider.localToWorldMatrix.c3.xyz;
                var planeNormal = math.normalizesafe(collider.localToWorldMatrix.c1.xyz, new float3(0, 1, 0));
                var pointPosition = point->workingPosition;
                var pointRadius = point->worldRadius;
                var signedDistance = math.dot(pointPosition - colliderPosition, planeNormal);
                var penetration = pointRadius - signedDistance;
                if (penetration <= 0f) {
                    return float3.zero;
                }
                var plane_depenetrationVector = planeNormal * penetration;
                if (!(otherPointParameters->angleElasticity == 1f
                      && otherPointParameters->rootElasticity == 1f
                      && otherPointParameters->lengthElasticity == 1f)) {
                    return plane_depenetrationVector;
                }
                break;
            }
        }
        return new float3(0f, 0f, 0f);
    }

    private void ClosestPointOnSegment(float3 point, float3 segA, float3 segB, out float3 closest) {
        var ab = segB - segA;
        var lengthSq = math.dot(ab, ab);
        if (lengthSq == 0f) {
            closest = segA;
            return;
        }
        var t = math.clamp(math.dot(point - segA, ab) / lengthSq, 0f, 1f);
        closest = segA + t * ab;
    }

    private void ClosestPointsOnTwoSegments(float3 a0, float3 a1, float3 b0, float3 b1, out float3 closestA, out float3 closestB, out float tB) {
        var d1 = a1 - a0;
        var d2 = b1 - b0;
        var r = a0 - b0;
        var a = math.dot(d1, d1);
        var e = math.dot(d2, d2);
        var f = math.dot(d2, r);
        float s, t;
        if (a <= 1e-8f && e <= 1e-8f) {
            s = 0f; t = 0f;
        } else if (a <= 1e-8f) {
            s = 0f;
            t = math.clamp(f / e, 0f, 1f);
        } else {
            var c = math.dot(d1, r);
            if (e <= 1e-8f) {
                t = 0f;
                s = math.clamp(-c / a, 0f, 1f);
            } else {
                var b = math.dot(d1, d2);
                var denom = a * e - b * b;
                if (denom != 0f) {
                    s = math.clamp((b * f - c * e) / denom, 0f, 1f);
                } else {
                    s = 0f;
                }
                t = (b * s + f) / e;
                if (t < 0f) {
                    t = 0f;
                    s = math.clamp(-c / a, 0f, 1f);
                } else if (t > 1f) {
                    t = 1f;
                    s = math.clamp((b - c) / a, 0f, 1f);
                }
            }
        }
        closestA = a0 + d1 * s;
        closestB = b0 + d2 * t;
        tB = t;
    }
    
    private float3 GetClosestPointOnLineSegment(float3 inputPoint, float3 segmentPoint1, float3 segmentPoint2, out float tValue) {
        tValue = 0f;
        var segment = segmentPoint2 - segmentPoint1;
        var segmentLengthSq = math.dot(segment, segment);
        if (segmentLengthSq == 0f) {
            return segmentPoint1;
        }
        tValue = math.dot(inputPoint - segmentPoint1, segment) / segmentLengthSq;
        tValue = math.clamp(tValue, 0f, 1f);
        return segmentPoint1 + tValue * segment;
    }

    /// <param name="reach">
    /// Upper bound on the distance from this point to the far end of every segment it is tested
    /// against — its parent and each of its children. Carried by reference because a depenetration
    /// moves the point, which loosens the bound by exactly the distance moved.
    /// </param>
    private unsafe void DepenetrateCollider(JiggleTreeJobData tree, JiggleSimulatedPoint* point, JiggleSimulatedPoint* parent, JigglePointParameters* pointParameters, JigglePointParameters* parentParameters, JiggleCollider collider, ref float reach) {
        // DoDepenetration rejects a disabled collider on entry, so without this the children loop
        // below still ran a full pass per child to accumulate zeroes. Disabled colliders are the
        // common case while the distance LOD has a crowd trimmed down.
        if (!collider.enabled) {
            return;
        }

        // Conservative reject. Every segment tested below starts at this point and ends no further
        // than `reach` away, so no part of any of them is closer to the collider than
        // (gap - reach); if that already exceeds the combined radii, every test returns zero. This
        // skips work whose answer is known rather than approximating it: a zero contribution leaves
        // both the running sum (adding zero is exact) and the running max (max with zero) untouched,
        // so the result is bit-identical. Planes are unbounded and are never rejected.
        if (collider.type != JiggleCollider.JiggleColliderType.Plane) {
            var colliderCentre = collider.localToWorldMatrix.c3.xyz;
            var bound = collider.worldRadius + point->worldRadius + reach
                + (collider.type == JiggleCollider.JiggleColliderType.Capsule ? collider.worldHeight * 0.5f : 0f);
            if (math.distancesq(colliderCentre, point->workingPosition) > bound * bound) {
                return;
            }
        }

        var collisionDepenetration = new float3(0f, 0f, 0f);
        collisionDepenetration = DoDepenetration(point, parent, parentParameters, collider);
        var maxDepenetrationMagnitude = math.length(collisionDepenetration);
        var pointIndex = (int)(point - tree.points);
        for (int childIndex = 0; childIndex < point->childrenCount; childIndex++) {
            var child = tree.points + tree.GetChild(pointIndex, childIndex);
            var newCollisionDepenetration = DoDepenetration(point, child, pointParameters, collider);
            maxDepenetrationMagnitude = math.max(maxDepenetrationMagnitude, math.length(newCollisionDepenetration));
            collisionDepenetration += newCollisionDepenetration;
        }
        if (maxDepenetrationMagnitude > 0f) {
            collisionDepenetration = math.normalizesafe(collisionDepenetration, new float3(0,0,1)) * maxDepenetrationMagnitude;
            point->workingPosition += collisionDepenetration;
            reach += maxDepenetrationMagnitude;
        }
    }

    /// <summary>
    /// Upper bound on the per-tree candidate list. A tree spanning a handful of cells with a handful
    /// of colliders in each sits far under this; anything over it falls back to the per-point walk,
    /// so this bounds job stack use rather than capping collision.
    /// </summary>
    private const int MaxGatheredColliders = 1024;

    /// <summary>
    /// Collects every scene collider the tree can reach — the always-on global cell, then each broad
    /// phase cell its extent covers — in exactly the order Constrain used to walk them per point.
    /// The extent is fixed by Cache before the substep loop and the broad phase map is read only for
    /// the life of the job, so the answer is the same for every point of every substep.
    /// Returns -1 if the tree reaches more colliders than the buffer holds, which tells Constrain to
    /// walk the grid per point instead.
    /// </summary>
    private unsafe int GatherColliders(int2 minExtentPosition, int2 maxExtentPosition, int* buffer) {
        var count = 0;
        var global = globalCell.Value;
        for (int index = 0; index < global.count; index++) {
            if (count >= MaxGatheredColliders) {
                return -1;
            }
            buffer[count++] = global.colliderIndices[index];
        }

        int2 min = minExtentPosition;
        int2 max = maxExtentPosition;
        // Corrupt or runaway point positions produce extents spanning millions of cells; walking
        // them would hang the sim for seconds. A healthy tree spans a handful of cells, so skip the
        // grid walk (global colliders above still applied) when the extent is implausible. long
        // math: garbage extents overflow int subtraction.
        long cellSpanX = (long)max.x - min.x + 1;
        long cellSpanY = (long)max.y - min.y + 1;
        if (cellSpanX > 0 && cellSpanY > 0
            && cellSpanX <= maxTreeCellSpan && cellSpanY <= maxTreeCellSpan
            && cellSpanX * cellSpanY <= maxTreeCellSpan) {
            for (int x = min.x; x <= max.x; x++) {
                for (int y = min.y; y <= max.y; y++) {
                    int2 grid = new int2(x, y);
                    if (broadPhaseMap.TryGetValue(grid, out var gridCell)) {
                        for (int index = 0; index < gridCell.count; index++) {
                            if (count >= MaxGatheredColliders) {
                                return -1;
                            }
                            buffer[count++] = gridCell.colliderIndices[index];
                        }
                    }
                }
            }
        }
        return count;
    }

    private unsafe bool ContainsIndex(int* array, int arrayCount, int index) {
        for (int i = 0; i < arrayCount; i++) {
            if (array[i] == index) {
                return true;
            }
        }
        return false;
    }
    private unsafe void Constrain(JiggleTreeJobData tree, int2 minExtentPosition, int2 maxExtentPosition, JiggleGrabConstraint* treeGrabs, int treeGrabCount, int* gatheredColliders, int gatheredColliderCount) {
        for (int i = 0; i < tree.pointCount; i++) {
            var point = tree.points+i;
            var pointParameters = tree.parameters + i;

            if (point->parentIndex == -1) {
                continue;
            }

            var parent = tree.points+point->parentIndex;
            var parentParameters = tree.parameters + point->parentIndex;

            #region Collisions

            // Every depenetration test starts by rejecting a point with no transform or no radius,
            // so a point that fails that can skip the whole collider walk instead of paying for the
            // broad phase and discarding every result. Virtual points — the root and every chain tip
            // — always fail it, and Cache zeroes worldRadius for them.
            if (point->hasTransform && point->worldRadius != 0f) {
                // How far this point's segments extend, computed once for the whole collider walk so
                // each collider costs one squared distance to reject instead of a full test per
                // segment. DepenetrateCollider grows it by whatever it moves the point.
                var pointPosition = point->workingPosition;
                var reachSq = math.distancesq(pointPosition, parent->workingPosition);
                for (int childIndex = 0; childIndex < point->childrenCount; childIndex++) {
                    var child = tree.points + tree.GetChild(i, childIndex);
                    reachSq = math.max(reachSq, math.distancesq(pointPosition, child->workingPosition));
                }
                var reach = math.sqrt(reachSq);

                if (gatheredColliderCount >= 0) {
                    // Pre-walked candidate list: same colliders, same order, gathered once per tree
                    // in Execute rather than re-walked per point per substep. Duplicates are kept —
                    // a collider straddling two cells is applied twice today.
                    for (int index = 0; index < gatheredColliderCount; index++) {
                        DepenetrateCollider(tree, point, parent, pointParameters, parentParameters, sceneColliders[gatheredColliders[index]], ref reach);
                    }
                } else {
                    // The tree reaches more colliders than the gather buffer holds, so walk the grid
                    // per point exactly as before.
                    var global = globalCell.Value;
                    for (int index = 0; index < global.count; index++) {
                        var sceneCollider = sceneColliders[global.colliderIndices[index]];
                        DepenetrateCollider(tree, point, parent, pointParameters, parentParameters, sceneCollider, ref reach);
                    }

                    int2 min = minExtentPosition;
                    int2 max = maxExtentPosition;
                    long cellSpanX = (long)max.x - min.x + 1;
                    long cellSpanY = (long)max.y - min.y + 1;
                    if (cellSpanX > 0 && cellSpanY > 0
                        && cellSpanX <= maxTreeCellSpan && cellSpanY <= maxTreeCellSpan
                        && cellSpanX * cellSpanY <= maxTreeCellSpan) {
                        for (int x = min.x; x <= max.x; x++) {
                            for (int y = min.y; y <= max.y; y++) {
                                int2 grid = new int2(x, y);
                                if (broadPhaseMap.TryGetValue(grid, out var gridCell)) {
                                    for (int index = 0; index < gridCell.count; index++) {
                                        var sceneCollider = sceneColliders[gridCell.colliderIndices[index]];
                                        DepenetrateCollider(tree, point, parent, pointParameters, parentParameters, sceneCollider, ref reach);
                                    }
                                }
                            }
                        }
                    }
                }

                var endIndex = tree.colliderIndexOffset + tree.colliderCount;
                for (int index = (int)tree.colliderIndexOffset; index < endIndex; index++) {
                    DepenetrateCollider(tree, point, parent, pointParameters, parentParameters, personalColliders[index], ref reach);
                }
            }

            #endregion

            #region Special root particle solve

            if (parent->parentIndex == -1) {
                var child = tree.points + tree.GetChild(i, 0);
                point->workingPosition = point->workingPosition = math.lerp(point->workingPosition, point->pose,
                    pointParameters->rootElasticity * pointParameters->rootElasticity);
                // Grabs apply here too, after the root pin: this branch continues past the regions
                // below, and on a single bone rig (a breast chain, say) this is the ONLY real point,
                // so skipping it would make the whole rig ungrabbable.
                ApplyGrabs(point, i, treeGrabs, treeGrabCount);
                var head = point->pose;
                var tail = child->pose;
                var diffasdf = head - tail;
                parent->workingPosition = point->workingPosition + diffasdf;
                point->lastPosition = point->position;
                point->position = point->workingPosition;
                continue;
            }

            #endregion

            #region Back-propagated motion for collisions

            if (point->childrenCount > 0) {
                // Back-propagated motion specifically for collision enabled chains
                var child = tree.points + tree.GetChild(i, 0);
                if (child->hasTransform) {
                    var child_length_elasticity = pointParameters->lengthElasticity * pointParameters->lengthElasticity;
                    var parentToChildPose = child->pose - parent->pose;
                    var parentToChild = child->workingPosition - parent->workingPosition;
                    var parentToChildPoseNormalized = math.normalizesafe(parentToChildPose, new float3(0,0,1));
                    var parentToChildNormalized = math.normalizesafe(parentToChild, new float3(0,0,1));
                    var parentToChildRotCorrection = FromToRotationFromNormalizedVectors(parentToChildPoseNormalized, parentToChildNormalized);
                    var targetVect = point->pose - parent->pose;
                    var currentPointLength = math.length(point->workingPosition-parent->workingPosition);
                    targetVect = math.normalizesafe(targetVect, new float3(0,0,1)) * currentPointLength;
                    var targetPos = math.rotate(parentToChildRotCorrection, targetVect) + parent->workingPosition;

                    var targetFromChild = targetPos - child->workingPosition;
                    var targetfromChildDist = math.length(targetFromChild);
                    targetPos = child->workingPosition + math.lerp(
                        targetFromChild,
                        math.normalizesafe(targetFromChild, new float3(0,0,1)) * child->desiredLengthToParent,
                        child_length_elasticity
                        );

                    //var errorBackwardConstraint = math.length(point->workingPosition - targetPos);
                    //if (targetfromChildDist != 0) {
                    //    errorBackwardConstraint /= targetfromChildDist;
                    //}
                    //errorBackwardConstraint = math.min(errorBackwardConstraint, 1.0f);
                    //errorBackwardConstraint = math.pow(errorBackwardConstraint, parent->parameters.elasticitySoften);
                    var notFoldedBack = math.clamp(-math.dot(math.normalizesafe(parent->workingPosition - point->workingPosition), math.normalizesafe(child->workingPosition - point->workingPosition))+1f,0f,1f);
                    var childAngleElasticity = pointParameters->angleElasticity * pointParameters->angleElasticity;
                    point->workingPosition = math.lerp(point->workingPosition, targetPos, childAngleElasticity * notFoldedBack);

                    //point->workingPosition = math.lerp(point->workingPosition, backward_constraint, notFoldedBack);
                }
            }

            #endregion
            
            #region Angle Constraint
            
            var parentParentWorkingPosition = parent->parentPose;
            if (parent->parentIndex != -1) {
                parentParentWorkingPosition = (tree.points + parent->parentIndex)->workingPosition;
            }
            var length_elasticity = parentParameters->lengthElasticity * parentParameters->lengthElasticity;
            var parentAimPose = math.normalizesafe(point->parentPose - parent->parentPose, new float3(0,0,1));
            var parentAim = math.normalizesafe(parent->workingPosition - parentParentWorkingPosition, new float3(0,0,1));

            var from_to_rot = FromToRotationFromNormalizedVectors(parentAimPose, parentAim);
            var constraintTarget = math.rotate(from_to_rot, point->pose - point->parentPose);

            var desiredPosition = parent->workingPosition + constraintTarget;

            // pow(x, 0) is 1 for every x — including 0 and NaN — and the error term feeds nothing
            // else, so when elasticitySoften is 0 the whole term is the constant 1 and the two square
            // roots, the divide, the min and the pow that produce it are dead work. That is the
            // default case rather than an edge case: ToJigglePointParameters only emits a non-zero
            // soften under the advanced toggle. Not an approximation — this returns exactly what the
            // arithmetic below it would have returned.
            var softenExponent = parentParameters->elasticitySoften;
            float error;
            if (softenExponent == 0f) {
                error = 1f;
            } else {
                var currentLength = math.length(point->workingPosition - parent->workingPosition);
                error = math.distance(point->workingPosition, desiredPosition);
                if (currentLength != 0) {
                    error /= currentLength;
                }
                error = math.min(error, 1.0f);
                error = math.pow(error, softenExponent);
            }
            point->workingPosition = math.lerp(point->workingPosition, desiredPosition, parentParameters->angleElasticity * error);

            #endregion

            // TODO: Early out if collisions are disabled (or don't for a more accurate solve)

            //continue;

            #region Length Constraint

            var offsetFromParent = point->workingPosition - parent->workingPosition;
            var offsetFromParentNormalized = math.normalizesafe(offsetFromParent, new float3(0, 0, 1));
            point->workingPosition = parent->workingPosition + math.lerp(offsetFromParent, offsetFromParentNormalized * point->desiredLengthToParent, length_elasticity);

            #endregion
            
            #region Angle Limit Constraint

            if (parentParameters->angleLimited) {
                var currentAngleError = float3Angle(
                    math.normalizesafe(point->workingPosition - parent->workingPosition, new float3(0f, 0f, 1f)),
                    math.normalizesafe(desiredPosition - parent->workingPosition, new float3(0f, 0f, 1f))
                );

                var A = parentParameters->angleLimit * math.PI * 0.5f;

                if (currentAngleError > A) {
                    var C = float3Angle(
                        point->workingPosition - desiredPosition,
                        parent->workingPosition - desiredPosition
                    ); // known included angle C

                    var b = math.distance(parent->workingPosition, desiredPosition); // known side opposite angle B

                    var B = math.PI - A - C;

                    var oppositeCheck = math.sin(B);
                    var a = oppositeCheck == 0f ? 0f : b * math.sin(A) / oppositeCheck;

                    var correctionVector = desiredPosition - point->workingPosition;
                    var correctionDir = math.normalizesafe(correctionVector, new float3(0,0,1));
                    var correctionDistance = math.length(correctionVector);

                    var angleCorrectionDistance = math.max(0f, correctionDistance - a);
                    var angleCorrection =
                        (correctionDir * angleCorrectionDistance) * (1f - parentParameters->angleLimitSoften * 0.5f);
                    point->workingPosition += angleCorrection;
                }

            }

            #endregion

            #region Grab Constraint

            // Last region on purpose: the pin wins for this point, and children (higher indices)
            // solve against the pinned position in this same pass.
            ApplyGrabs(point, i, treeGrabs, treeGrabCount);

            #endregion

            // What FinishStep used to do in a pass of its own. Constrain never reads position or
            // lastPosition — only workingPosition, pose, parentPose and the structural fields — so
            // rolling the step forward here rather than in a second walk cannot be observed by any
            // later point, and it lands while the point is still in L1. The root is finished after
            // the loop instead: it is skipped above, and the special root solve writes its
            // workingPosition while solving the first real point.
            point->lastPosition = point->position;
            point->position = point->workingPosition;
        }

        if (tree.pointCount > 0) {
            var root = tree.points;
            root->lastPosition = root->position;
            root->position = root->workingPosition;
        }
    }

    private unsafe void ApplyGrabs(JiggleSimulatedPoint* point, int pointIndex, JiggleGrabConstraint* treeGrabs, int treeGrabCount) {
        for (int g = 0; g < treeGrabCount; g++) {
            if (treeGrabs[g].pointIndex != pointIndex) {
                continue;
            }
            var grabTarget = treeGrabs[g].targetPosition;
            // Reach is bounded against the animated pose and scaled by how far down the chain this
            // point sits, so the same authored factor gives a long chain more travel than a short
            // one and neither can be dragged into a rubber band. The root particle has no distance
            // from root, so its own bone length stands in - otherwise it would be unbounded.
            var stretchScale = math.max(point->distanceFromRoot, point->desiredLengthToParent);
            var maxStretch = treeGrabs[g].maxStretchFactor * stretchScale;
            if (maxStretch > 0f) {
                var stretch = grabTarget - point->pose;
                var stretchLength = math.length(stretch);
                if (stretchLength > maxStretch) {
                    grabTarget = point->pose + stretch * (maxStretch / stretchLength);
                }
            }
            point->workingPosition = math.lerp(point->workingPosition, grabTarget, treeGrabs[g].strength);
        }
    }

    private unsafe void ApplyPose(JiggleTreeJobData tree) {
        if (tree.pointCount < 2) {
            return;
        }
        var rootPoint = tree.points + 1;
        var rootSimulationPosition = rootPoint->position;
        var rootPose = rootPoint->pose;
        var rootParameters = tree.parameters + 1;
        var rootParameterElasticity = 1f-(1f-rootParameters->rootElasticity) * rootParameters->airDrag;

        for (int i = 0; i < tree.pointCount; i++) {
            var point = tree.points+i;
            var parameters = tree.parameters + i;
            if (point->childrenCount <= 0) {
                continue;
            }

            var child = tree.points + tree.GetChild(i, 0);

            var local_pose = point->pose;
            var local_child_pose = child->pose;
            var local_child_working_position = child->workingPosition;
            var local_working_position = point->workingPosition;

            if (point->parentIndex == -1) {
                continue;
            }

            float3 cachedAnimatedVector = new float3(0f);
            float3 simulatedVector = cachedAnimatedVector;

            if (point->childrenCount <= 1) {
                cachedAnimatedVector = math.normalizesafe(local_child_pose - local_pose, new float3(0,0,1));
                simulatedVector = math.normalizesafe(local_child_working_position - local_working_position, new float3(0,0,1));
            } else {
                var cachedAnimatedVectorSum = new float3(0f);
                var simulatedVectorSum = cachedAnimatedVectorSum;
                for (var j = 0; j < point->childrenCount; j++) {
                    var child_also = tree.points + tree.GetChild(i, j);
                    var local_child_pose_also = child_also->pose;
                    var local_child_working_position_also = child_also->workingPosition;
                    cachedAnimatedVectorSum += math.normalizesafe(local_child_pose_also - local_pose, new float3(0,0,1));
                    simulatedVectorSum +=
                        math.normalizesafe(local_child_working_position_also - local_working_position, new float3(0,0,1));
                }

                cachedAnimatedVector = math.normalizesafe(cachedAnimatedVectorSum * (1f / point->childrenCount), new float3(0,0,1));
                simulatedVector = math.normalizesafe(simulatedVectorSum * (1f / point->childrenCount), new float3(0,0,1));
            }

            var animPoseToPhysicsPose = math.slerp(quaternion.identity,
                FromToRotationFromNormalizedVectors(cachedAnimatedVector, simulatedVector), parameters->blend);

            var transform = new JiggleTransform() {
                isVirtual = !point->hasTransform,
                position = point->workingPosition,
                rotation = math.mul(animPoseToPhysicsPose, tree.GetInputPose(inputPoses, i).rotation),
            };
            tree.WriteOutputPose(outputPoses, i, transform, rootSimulationPosition - rootPose, rootSimulationPosition, rootParameterElasticity);
        }
    }

    // No per-tree Validate() here, deliberately. It used `tree.GetIsValid(out string)` + throw, none
    // of which Burst can compile, so merely defining the symbol that gated it dropped this entire
    // Execute — Cache, VerletIntegrate, Constrain, the lot — back to managed IL in the editor and
    // made every jiggle profile taken there meaningless. Sanitize() below already repairs NaNs every
    // frame, so the check only ever bought a message. See JiggleMemoryBus for the full story.

    public unsafe void Execute(int index) {
        var tree = jiggleTrees[index];
        // Cache reads the animated pose, which has a single sample per frame, so it is hoisted out of
        // the substep loop. Each substep is then a whole fixed step: two of them advance the sim by
        // exactly as much as two frames would, which is what makes the result frame rate independent.
        Cache(tree, out var minExtentPosition, out var maxExtentPosition);
        var treeGrabs = stackalloc JiggleGrabConstraint[JiggleGrabConstraint.MaxGrabsPerTree];
        var treeGrabCount = 0;
        if (grabConstraintCount > 0 && grabConstraints.IsCreated && grabConstraints.TryGetFirstValue(tree.rootID, out var grab, out var grabIterator)) {
            do {
                if (grab.pointIndex > 0 && grab.pointIndex < tree.pointCount && treeGrabCount < JiggleGrabConstraint.MaxGrabsPerTree) {
                    treeGrabs[treeGrabCount++] = grab;
                }
            } while (grabConstraints.TryGetNextValue(out grab, ref grabIterator));
        }
        // Hoisted out of both the point loop and the substep loop: the candidate colliders follow
        // from the extent Cache just computed, which no substep changes.
        var gatheredColliders = stackalloc int[MaxGatheredColliders];
        var gatheredColliderCount = GatherColliders(minExtentPosition, maxExtentPosition, gatheredColliders);
        var stepCount = math.max(substeps, 1);
        for (int step = 0; step < stepCount; step++) {
            VerletIntegrate(tree);
            // Constrain rolls the step forward itself; there is no separate FinishStep pass.
            Constrain(tree, minExtentPosition, maxExtentPosition, treeGrabs, treeGrabCount, gatheredColliders, gatheredColliderCount);
        }
        // Repair BEFORE the pose write, not after: a NaN step otherwise leaks one frame of NaN
        // into the bone transforms, and because the next frame's animated pose is read back off
        // those same transforms, the corruption feeds itself and never heals — one bad step
        // permanently explodes the avatar (and its NaN render bounds then fail every culling
        // test, so a culled tree stops being re-posed at all). Sanitized state degrades the
        // same event to a one-frame snap back to the animated pose instead.
        // This is also the ingest guard: a tree committed with garbage build-time measurements
        // (rebuilt off a mid-swap skeleton) gets its state clamped here, on workers, before its
        // first write — Cache above re-derives lengths from live poses, so no main-thread
        // sanitize pass at commit is needed.
        jiggleTrees[index].Sanitize();
        ApplyPose(tree);
    }
}

}