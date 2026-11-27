using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace GatorDragonGames.JigglePhysics {

public unsafe class JiggleTree {
    public Transform[] bones;
    public Vector3[] restPositions;
    public Quaternion[] restRotations;
    public Transform[] personalColliderTransforms;
    public JiggleCollider[] personalColliders;

    /// <summary>
    /// The points and parameters live only in the job buffers; these are views over them, not
    /// copies. Points are live simulation state, so outside the commit window a read races the
    /// sim job for everything but the build-time fields (parentIndex, childrenCount, hasTransform).
    /// </summary>
    public Span<JiggleSimulatedPoint> points => hasJiggleTreeStruct ? new Span<JiggleSimulatedPoint>(jiggleTreeJobData.points, (int)jiggleTreeJobData.pointCount) : Span<JiggleSimulatedPoint>.Empty;
    public Span<JigglePointParameters> parameters => hasJiggleTreeStruct ? new Span<JigglePointParameters>(jiggleTreeJobData.parameters, (int)jiggleTreeJobData.pointCount) : Span<JigglePointParameters>.Empty;
    public int pointCount => hasJiggleTreeStruct ? (int)jiggleTreeJobData.pointCount : 0;
    public int GetChild(int pointIndex, int childSlot) => jiggleTreeJobData.GetChild(pointIndex, childSlot);

    public event System.Action<JiggleTree> dirtied;
    
    public bool dirty { get; private set; }
    public int rootID { get; private set; }

    /// <summary>
    /// Latched once this tree's parameters are pushed outside a commit (the animated-parameter path),
    /// after which its bones always read their lossy scale. Without it the per-slot wantsScale flag
    /// the read-reset job uses would be decided at commit time and could be outrun by a push that
    /// raises collisionRadius above zero. Latching rather than tracking means the flag can only ever
    /// become more conservative, which is the safe direction: the cost of a wrong `true` is one
    /// matrix fetch, the cost of a wrong `false` is a wrong collision radius.
    /// </summary>
    public bool alwaysReadScale { get; private set; }

    /// <summary>True when any point of this tree can collide, so its bones need a real lossy scale.</summary>
    public bool GetWantsScale() {
        if (alwaysReadScale) {
            return true;
        }
        var count = pointCount;
        for (int i = 0; i < count; i++) {
            if (jiggleTreeJobData.parameters[i].collisionRadius != 0f) {
                return true;
            }
        }
        return false;
    }

    public void SetDirty() {
        if (dirty) {
            return;
        }
        dirty = true;
        dirtied?.Invoke(this);
    }

    private bool hasJiggleTreeStruct = false;
    private JiggleTreeJobData jiggleTreeJobData;

    /// <summary>
    /// A disposed tree hands back an empty struct (pointCount 0, null buffers) until the next
    /// <see cref="Set"/> rebuilds it; the memory bus rejects that shape as a point and bone mismatch.
    /// </summary>
    public JiggleTreeJobData GetStruct() {
        return jiggleTreeJobData;
    }

    public void ResetTransformsToRest() {
        var count = Mathf.Min(bones.Length, Mathf.Min(restPositions.Length, restRotations.Length));
        for(var i=0;i<count;i++) {
            var bone = bones[i];
            if (!bone) continue;
            bone.localPosition = restPositions[i];
            bone.localRotation = restRotations[i];
        }
    }

    public void Dispose() {
        ResetTransformsToRest();
        if (!hasJiggleTreeStruct) return;
        jiggleTreeJobData.Dispose();
        hasJiggleTreeStruct = false;
    }

    /// <summary>
    /// Immediately resamples the rest pose of the bones in the tree. This can be useful if you have modified the bones' transforms on initialization and want to control when the rest pose is sampled.
    /// Not used normally because 0 scale bones get merged, and aren't part of the tree. So we typically want to regenerate the entire tree in that case.
    /// </summary>
    public void ResampleRestPose() {
        var bonesLength = bones.Length;
        for(int i=0;i<bonesLength;i++) {
            bones[i].GetLocalPositionAndRotation(out var pos, out var rot);
            restPositions[i] = pos;
            restRotations[i] = rot;
        }
    }

    public void Translate(float3 deltaPosition) {
        if (hasJiggleTreeStruct) {
            jiggleTreeJobData.Translate(deltaPosition);
        }
    }

    public void TransformRigid(quaternion deltaRotation, float3 deltaTranslation) {
        if (hasJiggleTreeStruct) {
            jiggleTreeJobData.TransformRigid(deltaRotation, deltaTranslation);
        }
    }

    public void SetColliderIndexOffset(int offset) {
        jiggleTreeJobData.colliderIndexOffset = (uint)offset;
    }
    
    public void SetTransformIndexOffset(int offset) {
        if (offset < 0) {
            throw new UnityException("JiggleTree.SetTransformIndexOffset: offset must be non-negative");
        }
        jiggleTreeJobData.transformIndexOffset = (uint)offset;
    }

    public JiggleTree(List<Transform> bones, List<JiggleSimulatedPoint> points, List<JigglePointParameters> parameters, List<Transform> personalColliderTransforms, List<JiggleCollider> personalColliders, List<Vector3> restPositions, List<Quaternion> restRotations, List<int> childrenIndices) {
        dirty = false;
        this.bones = bones.ToArray();
        this.restPositions = restPositions.ToArray();
        this.restRotations = restRotations.ToArray();
        this.personalColliders = personalColliders.ToArray();
        this.personalColliderTransforms = personalColliderTransforms.ToArray();
#if UNITY_6000_4_OR_NEWER
        rootID = bones[0].GetEntityId().GetHashCode();
#else
        rootID = bones[0].GetInstanceID();
#endif
        jiggleTreeJobData = new JiggleTreeJobData(rootID, -1, 0, personalColliders.Count, points, parameters, childrenIndices);
        hasJiggleTreeStruct = true;
    }

    public void Set(List<Transform> bones, List<JiggleSimulatedPoint> points, List<JigglePointParameters> parameters, List<Transform> personalColliderTransforms, List<JiggleCollider> personalColliders, List<Vector3> restPositions, List<Quaternion> restRotations, List<int> childrenIndices) {
        var bonesCount = bones.Count;
        if (bonesCount == this.bones.Length) {
            bones.CopyTo(this.bones);
            restPositions.CopyTo(this.restPositions);
            restRotations.CopyTo(this.restRotations);
        } else {
            this.bones = bones.ToArray();
            this.restPositions = restPositions.ToArray();
            this.restRotations = restRotations.ToArray();
        }

        var personalColliderTransformsCount = personalColliderTransforms.Count;
        var personalCollidersCount = personalColliders.Count;
        if (personalCollidersCount == this.personalColliders.Length && personalColliderTransformsCount == this.personalColliderTransforms.Length) {
            personalColliders.CopyTo(this.personalColliders);
            personalColliderTransforms.CopyTo(this.personalColliderTransforms);
        } else {
            this.personalColliders = personalColliders.ToArray();
            this.personalColliderTransforms = personalColliderTransforms.ToArray();
        }

#if UNITY_6000_4_OR_NEWER
        rootID = bones[0].GetEntityId().GetHashCode();
#else
        rootID = bones[0].GetInstanceID();
#endif

        // Writes straight into the job buffers, reusing them when the point count holds. A disposed
        // tree reallocates here, with its transform and collider offsets unset until the next commit.
        if (hasJiggleTreeStruct) {
            jiggleTreeJobData.Set(rootID, points, parameters, childrenIndices, personalCollidersCount);
        } else {
            jiggleTreeJobData = new JiggleTreeJobData(rootID, -1, 0, personalCollidersCount, points, parameters, childrenIndices);
            hasJiggleTreeStruct = true;
        }

        dirty = false;
    }

    /// <summary>
    /// Pushes parameters without going through a commit, so from here on this tree's bones read their
    /// scale unconditionally — see <see cref="alwaysReadScale"/>.
    /// </summary>
    public void SetParameters(List<JigglePointParameters> parameters) {
        if (!alwaysReadScale) {
            alwaysReadScale = true;
            JigglePhysics.MarkAlwaysReadScale(this);
        }
        if (!hasJiggleTreeStruct) {
            return;
        }
        var pointsCount = pointCount;
        var parametersCount = parameters.Count;
        if (pointsCount != parametersCount) {
            Debug.LogError($"JiggleTree.SetParameters: points count {pointsCount} does not match parameters count {parametersCount}");
            return;
        }
        jiggleTreeJobData.SetParameters(parameters);
    }

    private static void DebugDrawSphere(Vector3 origin, float radius, Color color, float duration, int segments = 8) {
        float angleStep = 360f / segments;
        Vector3 prevPoint = Vector3.zero;
        Vector3 currPoint = Vector3.zero;

        // Draw circle in XY plane
        for (int i = 0; i <= segments; i++) {
            float angle = Mathf.Deg2Rad * i * angleStep;
            currPoint = origin + new Vector3(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius, 0);
            if (i > 0) Debug.DrawLine(prevPoint, currPoint, color, duration);
            prevPoint = currPoint;
        }

        // Draw circle in XZ plane
        for (int i = 0; i <= segments; i++) {
            float angle = Mathf.Deg2Rad * i * angleStep;
            currPoint = origin + new Vector3(Mathf.Cos(angle) * radius, 0, Mathf.Sin(angle) * radius);
            if (i > 0) Debug.DrawLine(prevPoint, currPoint, color, duration);
            prevPoint = currPoint;
        }

        // Draw circle in YZ plane
        for (int i = 0; i <= segments; i++) {
            float angle = Mathf.Deg2Rad * i * angleStep;
            currPoint = origin + new Vector3(0, Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius);
            if (i > 0) Debug.DrawLine(prevPoint, currPoint, color, duration);
            prevPoint = currPoint;
        }
    }

}

}