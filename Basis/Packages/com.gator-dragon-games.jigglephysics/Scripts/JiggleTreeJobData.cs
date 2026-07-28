using System;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Assertions;

namespace GatorDragonGames.JigglePhysics {
public unsafe struct JiggleTreeJobData {
    public static bool operator ==(JiggleTreeJobData left, JiggleTreeJobData right) => left.Equals(right);
    public static bool operator !=(JiggleTreeJobData left, JiggleTreeJobData right) => !left.Equals(right);

    public int rootID;
    public uint pointCount;
    public uint transformIndexOffset;
    public uint colliderIndexOffset;
    public uint colliderCount;

    public JiggleSimulatedPoint* points;
    public JigglePointParameters* parameters;
    public const int MAX_POINTS = 10000;

    public JiggleTreeJobData(int rootID, int transformIndexOffset, int colliderIndexOffset, int colliderCount, JiggleSimulatedPoint[] inputPoints, JigglePointParameters[] inputParameters) {
        this.rootID = rootID;
        pointCount = (uint)inputPoints.Length;
        this.colliderIndexOffset = (uint)colliderIndexOffset;
        this.transformIndexOffset = (uint)transformIndexOffset;
        this.colliderCount = (uint)colliderCount;
        points = (JiggleSimulatedPoint*)UnsafeUtility.Malloc(
            sizeof(JiggleSimulatedPoint) * pointCount,
            UnsafeUtility.AlignOf<JiggleSimulatedPoint>(),
            Allocator.Persistent
        );
        parameters = (JigglePointParameters*)UnsafeUtility.Malloc(
            sizeof(JigglePointParameters) * pointCount,
            UnsafeUtility.AlignOf<JigglePointParameters>(),
            Allocator.Persistent
        );
        fixed (JiggleSimulatedPoint* src = inputPoints) {
            UnsafeUtility.MemCpy(points, src, sizeof(JiggleSimulatedPoint) * pointCount);
        }
        fixed (JigglePointParameters* src = inputParameters) {
            UnsafeUtility.MemCpy(parameters, src, sizeof(JigglePointParameters) * pointCount);
        }
    }

    public void Set(int newRootID, JiggleSimulatedPoint[] inputPoints, JigglePointParameters[] inputParameters, int colliderCount) {
        this.colliderCount = (uint)colliderCount;
        rootID = newRootID;
        if (inputPoints.Length != pointCount) {
            // A committed copy of this struct (holding the old pointers) can stay in the memory
            // bus until the next tree-commit flip, and the sim job keeps dereferencing it every
            // tick until then — so the old buffers must outlive that flip. FreeOnComplete frees
            // on the very next Simulate, which is too early.
            if (points != null) {
                JigglePhysics.FreeOnCommitFlip((IntPtr)points);
                points = null;
            }
            if (parameters != null) {
                JigglePhysics.FreeOnCommitFlip((IntPtr)parameters);
                parameters = null;
            }
            pointCount = (uint)inputPoints.Length;
            points = (JiggleSimulatedPoint*)UnsafeUtility.Malloc(
                sizeof(JiggleSimulatedPoint) * pointCount,
                UnsafeUtility.AlignOf<JiggleSimulatedPoint>(),
                Allocator.Persistent
            );
            parameters = (JigglePointParameters*)UnsafeUtility.Malloc(
                sizeof(JigglePointParameters) * pointCount,
                UnsafeUtility.AlignOf<JigglePointParameters>(),
                Allocator.Persistent
            );
        }
        fixed (JiggleSimulatedPoint* src = inputPoints) {
            UnsafeUtility.MemCpy(points, src, sizeof(JiggleSimulatedPoint) * pointCount);
        }
        fixed (JigglePointParameters* src = inputParameters) {
            UnsafeUtility.MemCpy(parameters, src, sizeof(JigglePointParameters) * pointCount);
        }
    }

    public void SetParameters(JigglePointParameters[] inputParameters) {
        Assert.AreEqual(pointCount, inputParameters.Length);
        fixed (JigglePointParameters* src = inputParameters) {
            UnsafeUtility.MemCpy(parameters, src, sizeof(JigglePointParameters) * pointCount);
        }
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
                var child = points[point.childrenIndices[0]];
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
        for (int i = 0; i < pointCount; i++) {
            var point = points[i];
            if (!point.GetIsValid((int)pointCount, out failReason)) {
                return false;
            }
        }

        failReason = "All good!";
        return true;
    }
}
}