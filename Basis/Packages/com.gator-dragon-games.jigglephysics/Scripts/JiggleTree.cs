using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace GatorDragonGames.JigglePhysics {

public class JiggleTree {
    public Transform[] bones;
    public JiggleSimulatedPoint[] points;
    /// <summary>Child indices for <see cref="points"/> at a stride of JiggleSimulatedPoint.MAX_CHILDREN.</summary>
    public int[] childrenIndices;
    public Vector3[] restPositions;
    public Quaternion[] restRotations;
    public JigglePointParameters[] parameters;
    public Transform[] personalColliderTransforms;
    public JiggleCollider[] personalColliders;

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
        for (int i = 0; i < parameters.Length; i++) {
            if (parameters[i].collisionRadius != 0f) {
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

    public JiggleTreeJobData GetStruct() {
        if (hasJiggleTreeStruct) {
            return jiggleTreeJobData;
        }

        jiggleTreeJobData = new JiggleTreeJobData(rootID, -1, 0, personalColliders.Length, points, parameters, childrenIndices);
        hasJiggleTreeStruct = true;
        return jiggleTreeJobData;
    }

    public void ResetTransformsToRest() {
        for(var i=0;i<points.Length;i++) {
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
        var pointCount = points.Length;
        for (int i = 0; i < pointCount; i++) {
            points[i].lastPosition += deltaPosition;
            points[i].position += deltaPosition;
            points[i].workingPosition += deltaPosition;
            points[i].pose += deltaPosition;
            points[i].parentPose += deltaPosition;
        }
        if (hasJiggleTreeStruct) {
            jiggleTreeJobData.Translate(deltaPosition);
        }
    }

    public void TransformRigid(quaternion deltaRotation, float3 deltaTranslation) {
        var pointCount = points.Length;
        for (int i = 0; i < pointCount; i++) {
            points[i].lastPosition = math.mul(deltaRotation, points[i].lastPosition) + deltaTranslation;
            points[i].position = math.mul(deltaRotation, points[i].position) + deltaTranslation;
            points[i].workingPosition = math.mul(deltaRotation, points[i].workingPosition) + deltaTranslation;
            points[i].pose = math.mul(deltaRotation, points[i].pose) + deltaTranslation;
            points[i].parentPose = math.mul(deltaRotation, points[i].parentPose) + deltaTranslation;
        }
        if (hasJiggleTreeStruct) {
            jiggleTreeJobData.TransformRigid(deltaRotation, deltaTranslation);
        }
    }

    public void SetColliderIndexOffset(int offset) {
        if (!hasJiggleTreeStruct) {
            GetStruct();
        }
        jiggleTreeJobData.colliderIndexOffset = (uint)offset;
    }
    
    public void SetTransformIndexOffset(int offset) {
        if (offset < 0) {
            throw new UnityException("JiggleTree.SetTransformIndexOffset: offset must be non-negative");
        }
        if (!hasJiggleTreeStruct) {
            GetStruct();
        }
        jiggleTreeJobData.transformIndexOffset = (uint)offset;
    }

    public JiggleTree(List<Transform> bones, List<JiggleSimulatedPoint> points, List<JigglePointParameters> parameters, List<Transform> personalColliderTransforms, List<JiggleCollider> personalColliders, List<Vector3> restPositions, List<Quaternion> restRotations, List<int> childrenIndices) {
        dirty = false;
        this.bones = bones.ToArray();
        this.restPositions = restPositions.ToArray();
        this.restRotations = restRotations.ToArray();
        this.points = points.ToArray();
        this.childrenIndices = childrenIndices.ToArray();
        this.parameters = parameters.ToArray();
        this.personalColliders = personalColliders.ToArray();
        this.personalColliderTransforms = personalColliderTransforms.ToArray();
#if UNITY_6000_4_OR_NEWER
        rootID = bones[0].GetEntityId().GetHashCode();
#else
        rootID = bones[0].GetInstanceID();
#endif
    }

    public void Set(List<Transform> bones, List<JiggleSimulatedPoint> points, List<JigglePointParameters> parameters, List<Transform> personalColliderTransforms, List<JiggleCollider> personalColliders, List<Vector3> restPositions, List<Quaternion> restRotations, List<int> childrenIndices) {
        var bonesCount = bones.Count;
        var pointsCount = points.Count;
        if (bonesCount == this.bones.Length && pointsCount == this.points.Length) {
            bones.CopyTo(this.bones);
            restPositions.CopyTo(this.restPositions);
            restRotations.CopyTo(this.restRotations);
            points.CopyTo(this.points);
            parameters.CopyTo(this.parameters);
        } else {
            this.bones = bones.ToArray();
            this.points = points.ToArray();
            this.parameters = parameters.ToArray();
            this.restPositions = restPositions.ToArray();
            this.restRotations = restRotations.ToArray();
        }
        // Rewritten wholesale every rebuild — a same-length rebuild can still rewire which point
        // points at which — but rewriting is a copy, not a reallocation. At MAX_CHILDREN=32 ints per
        // point this is the largest array on the tree by a wide margin (128 bytes per point against
        // 8 for a bone reference), so reallocating it on every regeneration was the bulk of the
        // garbage under GetJiggleTrees. Nothing outside this class aliases it: JiggleTreeJobData
        // memcpys it into its own unmanaged buffer below and jobs only ever read that.
        if (childrenIndices.Count == this.childrenIndices.Length) {
            childrenIndices.CopyTo(this.childrenIndices);
        } else {
            this.childrenIndices = childrenIndices.ToArray();
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
        
        if (hasJiggleTreeStruct) {
            jiggleTreeJobData.Set(rootID, this.points, this.parameters, this.childrenIndices, personalCollidersCount);
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
        var pointsCount = points.Length;
        var parametersCount = parameters.Count;
        if (pointsCount != parametersCount) {
            Debug.LogError($"JiggleTree.SetParameters: points count {pointsCount} does not match parameters count {parametersCount}");
            return;
        }

        for (int i = 0; i < pointsCount; i++) {
            this.parameters[i] = parameters[i];
        }

        if (hasJiggleTreeStruct) {
            jiggleTreeJobData.SetParameters(this.parameters);
        }
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