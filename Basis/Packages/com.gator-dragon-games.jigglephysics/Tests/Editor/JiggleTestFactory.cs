using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;

namespace GatorDragonGames.JigglePhysics.Tests {

internal static class JiggleTestFactory {
    public const float Tolerance = 1e-4f;

    public static float InverseCellSize => JiggleSettings.InverseBroadPhaseCellSize;

    public static int2 Key(float3 position) {
        return JiggleGridCell.GetKeyForPosition(position, JiggleSettings.InverseBroadPhaseCellSize);
    }

    /// <summary>
    /// Every knob defaults to inert (no gravity, no drag, no elasticity) so a test can enable
    /// exactly the one term it is measuring. rootElasticity defaults to 1 to match the
    /// non-advanced output of ToJigglePointParameters, which pins the first real point to its pose.
    /// </summary>
    public static JigglePointParameters Params(
        float rootElasticity = 1f,
        float angleElasticity = 0f,
        float lengthElasticity = 0f,
        float elasticitySoften = 0f,
        float gravityMultiplier = 0f,
        float blend = 1f,
        float airDrag = 0f,
        float drag = 0f,
        float ignoreRootMotion = 0f,
        float collisionRadius = 0f,
        bool angleLimited = false,
        float angleLimit = 1f,
        float angleLimitSoften = 0f) {
        return new JigglePointParameters {
            rootElasticity = rootElasticity,
            angleElasticity = angleElasticity,
            lengthElasticity = lengthElasticity,
            elasticitySoften = elasticitySoften,
            gravityMultiplier = gravityMultiplier,
            blend = blend,
            airDrag = airDrag,
            drag = drag,
            ignoreRootMotion = ignoreRootMotion,
            collisionRadius = collisionRadius,
            angleLimited = angleLimited,
            angleLimit = angleLimit,
            angleLimitSoften = angleLimitSoften,
        };
    }

    public static JiggleCollider Sphere(float3 center, float radius, bool enabled = true) {
        var collider = new JiggleCollider {
            enabled = enabled,
            type = JiggleCollider.JiggleColliderType.Sphere,
            radius = radius,
            localOffset = float3.zero,
        };
        collider.Read(float4x4.Translate(center));
        return collider;
    }

    public static JiggleCollider Capsule(float3 center, float radius, float height,
        JiggleCollider.CapsuleAxis axis = JiggleCollider.CapsuleAxis.Y, bool enabled = true) {
        var collider = new JiggleCollider {
            enabled = enabled,
            type = JiggleCollider.JiggleColliderType.Capsule,
            radius = radius,
            height = height,
            capsuleAxis = axis,
            localOffset = float3.zero,
        };
        collider.Read(float4x4.Translate(center));
        return collider;
    }

    public static JiggleCollider Plane(float3 pointOnPlane, float3 normal, bool enabled = true) {
        var collider = new JiggleCollider {
            enabled = enabled,
            type = JiggleCollider.JiggleColliderType.Plane,
            radius = 1f,
            localOffset = float3.zero,
        };
        var up = math.normalize(normal);
        var reference = math.abs(up.y) > 0.99f ? new float3(1f, 0f, 0f) : new float3(0f, 1f, 0f);
        var right = math.normalize(math.cross(reference, up));
        var forward = math.cross(up, right);
        collider.Read(new float4x4(
            new float4(right, 0f),
            new float4(up, 0f),
            new float4(forward, 0f),
            new float4(pointOnPlane, 1f)));
        return collider;
    }

    public static JiggleTransform Pose(float3 position) {
        return new JiggleTransform {
            isVirtual = false,
            position = position,
            rotation = quaternion.identity,
            scale = new float3(1f),
        };
    }
}

/// <summary>
/// Builds the point layout the simulate job expects: index 0 is the virtual root, indices
/// 1..realCount are real (transform-backed) points, and the final index is the virtual tip.
/// The virtual root is seeded at the position Cache() will extrapolate for it so the tree
/// starts at rest with zero root delta.
/// </summary>
internal sealed unsafe class JiggleTestTree {
    public JiggleSimulatedPoint[] points;
    public JigglePointParameters[] parameters;
    public JiggleTransform[] inputPoses;
    public int realCount;

    public int TipIndex => realCount + 1;
    public int PointCount => realCount + 2;

    public static JiggleTestTree Chain(int realCount, float3 start, float3 direction, float spacing,
        JigglePointParameters? parameters = null) {
        if (realCount < 1) {
            throw new ArgumentOutOfRangeException(nameof(realCount));
        }
        var dir = math.normalize(direction);
        var tree = new JiggleTestTree {
            realCount = realCount,
            points = new JiggleSimulatedPoint[realCount + 2],
            parameters = new JigglePointParameters[realCount + 2],
            inputPoses = new JiggleTransform[realCount + 2],
        };

        var defaults = parameters ?? JiggleTestFactory.Params();
        for (int i = 0; i < tree.parameters.Length; i++) {
            tree.parameters[i] = defaults;
        }

        var rootPose = start - dir * spacing;
        var root = new JiggleSimulatedPoint {
            parentIndex = -1,
            childrenCount = 1,
            hasTransform = false,
            distanceFromRoot = 0f,
            pose = rootPose,
            parentPose = rootPose - dir * spacing,
            position = rootPose,
            lastPosition = rootPose,
            workingPosition = rootPose,
        };
        root.childrenIndices[0] = 1;
        tree.points[0] = root;
        tree.inputPoses[0] = new JiggleTransform {
            isVirtual = true, position = rootPose, rotation = quaternion.identity, scale = new float3(1f),
        };

        for (int i = 1; i <= realCount; i++) {
            var position = start + dir * spacing * (i - 1);
            var point = new JiggleSimulatedPoint {
                parentIndex = i - 1,
                childrenCount = 1,
                hasTransform = true,
                distanceFromRoot = spacing * (i - 1),
                pose = position,
                parentPose = position - dir * spacing,
                position = position,
                lastPosition = position,
                workingPosition = position,
                desiredLengthToParent = spacing,
            };
            point.childrenIndices[0] = i + 1;
            tree.points[i] = point;
            tree.inputPoses[i] = JiggleTestFactory.Pose(position);
        }

        var tipPosition = start + dir * spacing * realCount;
        var tip = new JiggleSimulatedPoint {
            parentIndex = realCount,
            childrenCount = 0,
            hasTransform = false,
            distanceFromRoot = spacing * realCount,
            pose = tipPosition,
            parentPose = tipPosition - dir * spacing,
            position = tipPosition,
            lastPosition = tipPosition,
            workingPosition = tipPosition,
            desiredLengthToParent = spacing,
        };
        tree.points[tree.TipIndex] = tip;
        tree.inputPoses[tree.TipIndex] = new JiggleTransform {
            isVirtual = true, position = tipPosition, rotation = quaternion.identity, scale = new float3(1f),
        };

        return tree;
    }

    /// <summary>
    /// A root whose grandchild has no transform, which drives Cache()'s isolated-root-bone branch.
    /// </summary>
    public static JiggleTestTree SingleBone(float3 position) {
        var tree = new JiggleTestTree {
            realCount = 1,
            points = new JiggleSimulatedPoint[3],
            parameters = new JigglePointParameters[3],
            inputPoses = new JiggleTransform[3],
        };
        for (int i = 0; i < 3; i++) {
            tree.parameters[i] = JiggleTestFactory.Params();
        }

        var root = new JiggleSimulatedPoint {
            parentIndex = -1, childrenCount = 1, hasTransform = false,
            pose = position, parentPose = position, position = position,
            lastPosition = position, workingPosition = position,
        };
        root.childrenIndices[0] = 1;
        tree.points[0] = root;

        var bone = new JiggleSimulatedPoint {
            parentIndex = 0, childrenCount = 1, hasTransform = true,
            pose = position, parentPose = position, position = position,
            lastPosition = position, workingPosition = position, desiredLengthToParent = 0.25f,
        };
        bone.childrenIndices[0] = 2;
        tree.points[1] = bone;

        var tip = new JiggleSimulatedPoint {
            parentIndex = 1, childrenCount = 0, hasTransform = false,
            pose = position, parentPose = position, position = position,
            lastPosition = position, workingPosition = position, desiredLengthToParent = 0.25f,
        };
        tree.points[2] = tip;

        tree.inputPoses[0] = new JiggleTransform { isVirtual = true, position = position, rotation = quaternion.identity, scale = new float3(1f) };
        tree.inputPoses[1] = JiggleTestFactory.Pose(position);
        tree.inputPoses[2] = new JiggleTransform { isVirtual = true, position = position, rotation = quaternion.identity, scale = new float3(1f) };
        return tree;
    }

    public void SetParameters(int index, JigglePointParameters value) {
        parameters[index] = value;
    }

    public void SetAllParameters(JigglePointParameters value) {
        for (int i = 0; i < parameters.Length; i++) {
            parameters[i] = value;
        }
    }
}

/// <summary>
/// Owns every native allocation the simulate job touches. Tree point/parameter buffers are freed
/// directly rather than through JiggleTreeJobData.Dispose(), which defers to JigglePhysics statics
/// that only exist once a JigglePhysics component is live.
/// </summary>
internal sealed unsafe class JiggleSimHarness : IDisposable {
    public JiggleJobSimulate job;
    public NativeArray<JiggleTreeJobData> trees;
    public NativeArray<JiggleTransform> inputPoses;
    public NativeArray<PoseData> outputPoses;
    public NativeArray<JiggleCollider> personalColliders;
    public NativeArray<JiggleCollider> sceneColliders;
    public NativeHashMap<int2, JiggleGridCell> broadPhaseMap;
    public NativeReference<JiggleGridCell> globalCell;

    private readonly List<IntPtr> allocations = new List<IntPtr>();
    private readonly List<int2> gridCellKeys = new List<int2>();
    private JiggleTestTree source;

    public JiggleSimHarness(JiggleTestTree tree, float fixedDeltaTime = 0.02f, float3 gravity = default,
        int personalColliderCapacity = 8, int sceneColliderCapacity = 8) {
        source = tree;
        var pointCount = tree.PointCount;

        inputPoses = new NativeArray<JiggleTransform>(pointCount, Allocator.Persistent);
        outputPoses = new NativeArray<PoseData>(pointCount, Allocator.Persistent);
        for (int i = 0; i < pointCount; i++) {
            inputPoses[i] = tree.inputPoses[i];
            outputPoses[i] = new PoseData {
                pose = new JiggleTransform {
                    isVirtual = !tree.points[i].hasTransform,
                    position = tree.points[i].pose,
                    rotation = quaternion.identity,
                    scale = new float3(1f),
                },
            };
        }

        var jobData = new JiggleTreeJobData(0, 0, 0, 0, tree.points, tree.parameters);
        allocations.Add((IntPtr)jobData.points);
        allocations.Add((IntPtr)jobData.parameters);

        trees = new NativeArray<JiggleTreeJobData>(1, Allocator.Persistent);
        trees[0] = jobData;

        personalColliders = new NativeArray<JiggleCollider>(personalColliderCapacity, Allocator.Persistent);
        sceneColliders = new NativeArray<JiggleCollider>(sceneColliderCapacity, Allocator.Persistent);
        broadPhaseMap = new NativeHashMap<int2, JiggleGridCell>(16, Allocator.Persistent);
        globalCell = new NativeReference<JiggleGridCell>(new JiggleGridCell(JiggleJobBroadPhase.MAX_COLLIDERS), Allocator.Persistent);

        job = new JiggleJobSimulate();
        job.SetFixedDeltaTime(fixedDeltaTime);
        job.inverseCellSize = JiggleSettings.InverseBroadPhaseCellSize;
        job.maxTreeCellSpan = JiggleSettings.MaxTreeCellSpan;
        job.substeps = 1;
        job.gravity = gravity;
        job.timeStamp = 0.0;
        job.sceneColliderCount = 0;
        job.inputPoses = inputPoses;
        job.outputPoses = outputPoses;
        job.jiggleTrees = trees;
        job.personalColliders = personalColliders;
        job.sceneColliders = sceneColliders;
        job.broadPhaseMap = broadPhaseMap;
        job.globalCell = globalCell;
    }

    public JiggleTreeJobData Tree => trees[0];

    public JiggleSimulatedPoint Point(int index) => trees[0].points[index];

    public JigglePointParameters Parameters(int index) => trees[0].parameters[index];

    public void SetPoint(int index, JiggleSimulatedPoint value) {
        trees[0].points[index] = value;
    }

    public void SetParameters(int index, JigglePointParameters value) {
        trees[0].parameters[index] = value;
    }

    public void SetAllParameters(JigglePointParameters value) {
        var tree = trees[0];
        for (int i = 0; i < tree.pointCount; i++) {
            tree.parameters[i] = value;
        }
    }

    /// <summary>
    /// Moves a point's animated pose and its simulation state together, so the tree stays at rest
    /// at the new location instead of being flagged as a runaway by Sanitize().
    /// </summary>
    public void SetInputPosition(int index, float3 position) {
        var pose = inputPoses[index];
        pose.position = position;
        inputPoses[index] = pose;

        var point = trees[0].points[index];
        point.pose = position;
        point.position = position;
        point.lastPosition = position;
        point.workingPosition = position;
        trees[0].points[index] = point;

        var output = outputPoses[index];
        output.pose.position = position;
        outputPoses[index] = output;
    }

    public void SetTreeColliderRange(int offset, int count) {
        var tree = trees[0];
        tree.colliderIndexOffset = (uint)offset;
        tree.colliderCount = (uint)count;
        trees[0] = tree;
    }

    /// <summary>Places a collider index into the global cell, which Constrain() always walks.</summary>
    public void AddGlobalCollider(int colliderIndex) {
        var cell = globalCell.Value;
        cell.colliderIndices[cell.count] = colliderIndex;
        cell.count++;
        globalCell.Value = cell;
    }

    public void AddGridCollider(int2 key, int colliderIndex) {
        if (!broadPhaseMap.TryGetValue(key, out var cell)) {
            cell = new JiggleGridCell(JiggleJobBroadPhase.MAX_COLLIDERS);
            broadPhaseMap.Add(key, cell);
            gridCellKeys.Add(key);
        }
        cell.colliderIndices[cell.count] = colliderIndex;
        cell.count++;
        broadPhaseMap[key] = cell;
    }

    public void Step(int count = 1) {
        for (int i = 0; i < count; i++) {
            job.Execute(0);
        }
    }

    public JiggleTransform Output(int index) => outputPoses[index].pose;

    public PoseData OutputData(int index) => outputPoses[index];

    public void Dispose() {
        foreach (var key in gridCellKeys) {
            if (broadPhaseMap.TryGetValue(key, out var cell)) {
                cell.Dispose();
            }
        }
        gridCellKeys.Clear();

        if (globalCell.IsCreated) {
            var cell = globalCell.Value;
            cell.Dispose();
            globalCell.Dispose();
        }
        if (broadPhaseMap.IsCreated) broadPhaseMap.Dispose();
        if (sceneColliders.IsCreated) sceneColliders.Dispose();
        if (personalColliders.IsCreated) personalColliders.Dispose();
        if (trees.IsCreated) trees.Dispose();
        if (outputPoses.IsCreated) outputPoses.Dispose();
        if (inputPoses.IsCreated) inputPoses.Dispose();

        foreach (var pointer in allocations) {
            UnsafeUtility.Free((void*)pointer, Allocator.Persistent);
        }
        allocations.Clear();
        source = null;
    }
}

internal static class JiggleAssert {
    public static void AreEqual(float3 expected, float3 actual, float tolerance, string message = "") {
        NUnit.Framework.Assert.That(math.distance(expected, actual), NUnit.Framework.Is.LessThanOrEqualTo(tolerance),
            $"{message} expected {expected} but was {actual}");
    }

    public static void IsFinite(float3 value, string message = "") {
        NUnit.Framework.Assert.That(math.all(math.isfinite(value)), NUnit.Framework.Is.True, $"{message} value was {value}");
    }

    public static void IsFinite(quaternion value, string message = "") {
        NUnit.Framework.Assert.That(math.all(math.isfinite(value.value)), NUnit.Framework.Is.True, $"{message} value was {value}");
    }
}

}
