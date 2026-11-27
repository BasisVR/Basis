using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Assertions;

namespace GatorDragonGames.JigglePhysics {
public unsafe struct JiggleTreeJobData : IEquatable<JiggleTreeJobData> {
    public static bool operator ==(JiggleTreeJobData left, JiggleTreeJobData right) => left.Equals(right);
    public static bool operator !=(JiggleTreeJobData left, JiggleTreeJobData right) => !left.Equals(right);
    public bool Equals(JiggleTreeJobData other) => rootID == other.rootID && pointCount == other.pointCount && transformIndexOffset == other.transformIndexOffset && colliderIndexOffset == other.colliderIndexOffset && colliderCount == other.colliderCount && childCapacity == other.childCapacity && points == other.points && parameters == other.parameters && childrenIndices == other.childrenIndices;
    public override bool Equals(object obj) => obj is JiggleTreeJobData other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(rootID, pointCount, transformIndexOffset, colliderIndexOffset, colliderCount, (IntPtr)points, (IntPtr)parameters, (IntPtr)childrenIndices);

    public int rootID;
    public uint pointCount;
    public uint transformIndexOffset;
    public uint colliderIndexOffset;
    public uint colliderCount;
    public uint childCapacity;

    public JiggleSimulatedPoint* points;
    public JigglePointParameters* parameters;

    /// <summary>
    /// Every point's children packed end to end: point i's children are
    /// childrenIndices[childrenOffsets[i] .. + childrenCount]. One allocation holds both, the packed
    /// list first, so freeing childrenIndices frees the offsets too. Every point is the child of at
    /// most one other, so the packed list never needs more than pointCount entries, against the
    /// pointCount * MAX_CHILDREN the fixed stride layout spent. Use <see cref="GetChild"/>.
    /// </summary>
    public int* childrenIndices;
    public int* childrenOffsets;

    public const int MAX_POINTS = 10000;

    public int GetChild(int pointIndex, int childSlot) {
        return childrenIndices[childrenOffsets[pointIndex] + childSlot];
    }

    /// <summary>
    /// Builds from caller-owned buffers. <paramref name="inputChildren"/> is the build layout, a fixed
    /// stride of MAX_CHILDREN per point, and is packed on the way in.
    /// </summary>
    public JiggleTreeJobData(int rootID, int transformIndexOffset, int colliderIndexOffset, int colliderCount, IReadOnlyList<JiggleSimulatedPoint> inputPoints, IReadOnlyList<JigglePointParameters> inputParameters, IReadOnlyList<int> inputChildren) {
        this.rootID = rootID;
        this.colliderIndexOffset = (uint)colliderIndexOffset;
        this.transformIndexOffset = (uint)transformIndexOffset;
        this.colliderCount = (uint)colliderCount;
        pointCount = 0;
        childCapacity = 0;
        points = null;
        parameters = null;
        childrenIndices = null;
        childrenOffsets = null;
        Allocate(inputPoints.Count, CountChildren(inputPoints));
        Write(inputPoints, inputParameters, inputChildren);
    }

    private static int CountChildren(IReadOnlyList<JiggleSimulatedPoint> inputPoints) {
        var total = 0;
        var count = inputPoints.Count;
        for (int i = 0; i < count; i++) {
            total += math.clamp(inputPoints[i].childrenCount, 0, JiggleSimulatedPoint.MAX_CHILDREN);
        }
        return total;
    }

    private void Allocate(int count, int requiredChildren) {
        pointCount = (uint)count;
        childCapacity = (uint)math.max(count, requiredChildren);
        points = (JiggleSimulatedPoint*)UnsafeUtility.Malloc(sizeof(JiggleSimulatedPoint) * math.max(count, 1), UnsafeUtility.AlignOf<JiggleSimulatedPoint>(), Allocator.Persistent);
        parameters = (JigglePointParameters*)UnsafeUtility.Malloc(sizeof(JigglePointParameters) * math.max(count, 1), UnsafeUtility.AlignOf<JigglePointParameters>(), Allocator.Persistent);
        AllocateChildren();
    }

    private void AllocateChildren() {
        childrenIndices = (int*)UnsafeUtility.Malloc(sizeof(int) * math.max((int)(childCapacity + pointCount), 1), UnsafeUtility.AlignOf<int>(), Allocator.Persistent);
        childrenOffsets = childrenIndices + childCapacity;
    }

    private void Write(IReadOnlyList<JiggleSimulatedPoint> inputPoints, IReadOnlyList<JigglePointParameters> inputParameters, IReadOnlyList<int> inputChildren) {
        var count = (int)pointCount;
        for (int i = 0; i < count; i++) {
            points[i] = inputPoints[i];
        }
        WriteParameters(inputParameters);
        PackChildren(inputChildren);
    }

    private void WriteParameters(IReadOnlyList<JigglePointParameters> inputParameters) {
        var count = math.min((int)pointCount, inputParameters.Count);
        for (int i = 0; i < count; i++) {
            parameters[i] = inputParameters[i];
        }
    }

    /// <summary>
    /// A short or missing input is tolerated and reads as child 0: childrenCount bounds every read and
    /// GetIsValid rejects an index that points nowhere.
    /// </summary>
    private void PackChildren(IReadOnlyList<int> inputChildren) {
        var inputCount = inputChildren?.Count ?? 0;
        var written = 0;
        for (int i = 0; i < pointCount; i++) {
            childrenOffsets[i] = written;
            var childCount = math.clamp(points[i].childrenCount, 0, JiggleSimulatedPoint.MAX_CHILDREN);
            var source = i * JiggleSimulatedPoint.MAX_CHILDREN;
            for (int c = 0; c < childCount && written < childCapacity; c++) {
                var slot = source + c;
                childrenIndices[written++] = slot < inputCount ? inputChildren[slot] : 0;
            }
        }
    }

    public void Set(int newRootID, IReadOnlyList<JiggleSimulatedPoint> inputPoints, IReadOnlyList<JigglePointParameters> inputParameters, IReadOnlyList<int> inputChildren, int colliderCount) {
        this.colliderCount = (uint)colliderCount;
        rootID = newRootID;
        var requiredChildren = CountChildren(inputPoints);
        if (inputPoints.Count != pointCount || points == null) {
            // A committed copy of this struct (holding the old pointers) can stay in the memory
            // bus until the next tree-commit flip, and the sim job keeps dereferencing it every
            // tick until then, so the old buffers must outlive that flip. FreeOnComplete frees
            // on the very next Simulate, which is too early.
            FreeOnCommitFlip();
            Allocate(inputPoints.Count, requiredChildren);
        } else if (requiredChildren > childCapacity) {
            if (childrenIndices != null) {
                JigglePhysics.FreeOnCommitFlip((IntPtr)childrenIndices);
            }
            childCapacity = (uint)requiredChildren;
            AllocateChildren();
        }
        Write(inputPoints, inputParameters, inputChildren);
    }

    private void FreeOnCommitFlip() {
        if (points != null) {
            JigglePhysics.FreeOnCommitFlip((IntPtr)points);
            points = null;
        }
        if (parameters != null) {
            JigglePhysics.FreeOnCommitFlip((IntPtr)parameters);
            parameters = null;
        }
        if (childrenIndices != null) {
            JigglePhysics.FreeOnCommitFlip((IntPtr)childrenIndices);
            childrenIndices = null;
            childrenOffsets = null;
        }
    }

    public void SetParameters(IReadOnlyList<JigglePointParameters> inputParameters) {
        Assert.AreEqual(pointCount, inputParameters.Count);
        WriteParameters(inputParameters);
    }

    public void Dispose() {
        if (points != null) {
            JigglePhysics.FreeOnComplete((IntPtr)points);
            points = null;
        }
        if (parameters != null) {
            JigglePhysics.FreeOnComplete((IntPtr)parameters);
            parameters = null;
        }
        if (childrenIndices != null) {
            JigglePhysics.FreeOnComplete((IntPtr)childrenIndices);
            childrenIndices = null;
            childrenOffsets = null;
        }
        pointCount = 0;
        childCapacity = 0;
    }

    public void OnDrawGizmosSelected() {
        for (int i = 0; i < pointCount; i++) {
            var point = points[i];
            if (point.hasTransform) {
                Gizmos.DrawWireSphere(point.position, point.worldRadius);
            } else {
                if (point.parentIndex == -1) {
                    Gizmos.color = Color.magenta;
                    Gizmos.DrawWireSphere(point.pose, 0.05f);
                } else {
                    Gizmos.color = Color.magenta;
                    Gizmos.DrawWireSphere(point.position, 0.05f);
                }
            }
            if (point.childrenCount != 0) {
                var child = points[GetChild(i, 0)];
                Gizmos.color = Color.cyan;
                Gizmos.DrawLine(point.position, child.position);
            }
        }
    }

    public void Sanitize() {
        for (int i = 0; i < pointCount; i++) {
            points[i].Sanitize();
        }
    }

    public void Translate(float3 deltaPosition) {
        if (points == null) return;
        for (int i = 0; i < pointCount; i++) {
            var p = points[i];
            p.lastPosition += deltaPosition;
            p.position += deltaPosition;
            p.workingPosition += deltaPosition;
            p.pose += deltaPosition;
            p.parentPose += deltaPosition;
            points[i] = p;
        }
    }

    public void TransformRigid(quaternion deltaRotation, float3 deltaTranslation) {
        if (points == null) return;
        for (int i = 0; i < pointCount; i++) {
            var p = points[i];
            p.lastPosition = math.mul(deltaRotation, p.lastPosition) + deltaTranslation;
            p.position = math.mul(deltaRotation, p.position) + deltaTranslation;
            p.workingPosition = math.mul(deltaRotation, p.workingPosition) + deltaTranslation;
            p.pose = math.mul(deltaRotation, p.pose) + deltaTranslation;
            p.parentPose = math.mul(deltaRotation, p.parentPose) + deltaTranslation;
            points[i] = p;
        }
    }

    public bool GetIsValid(out string failReason) {
        if (pointCount == 0 || pointCount > MAX_POINTS) {
            failReason = $"Invalid point count {pointCount}";
            return false;
        }
        if (points == null) {
            failReason = "Points pointer is null";
            return false;
        }
        if (parameters == null) {
            failReason = "Parameters pointer is null";
            return false;
        }
        if (childrenIndices == null || childrenOffsets == null) {
            failReason = "childrenIndices pointer is null";
            return false;
        }
        for (int i = 0; i < pointCount; i++) {
            var point = points[i];
            if (!point.GetIsValid((int)pointCount, out failReason)) {
                return false;
            }
            if (childrenOffsets[i] < 0 || childrenOffsets[i] + point.childrenCount > childCapacity) {
                failReason = "childrenIndices is outside range";
                return false;
            }
            for (int c = 0; c < point.childrenCount; c++) {
                var childIndex = GetChild(i, c);
                if (childIndex < 0 || childIndex >= pointCount) {
                    failReason = "childrenIndices is outside range";
                    return false;
                }
            }
            if (!parameters[i].GetIsValid(out failReason)) {
                return false;
            }
        }

        failReason = "All good!";
        return true;
    }
}
}