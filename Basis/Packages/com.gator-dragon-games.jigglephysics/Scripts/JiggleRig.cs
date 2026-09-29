using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace GatorDragonGames.JigglePhysics {

public class JiggleRig : MonoBehaviour, IJiggleParameterProvider {
    [SerializeField] private JiggleRigData jiggleRigData;
    [SerializeField, Tooltip("Whether to check if parameters have been changed each frame.")] private bool animatedParameters = false;
    
    [NonSerialized] private JiggleTreeSegment segment;
    private bool addedToJiggleTreeSegments = false;

    public JiggleRigData GetJiggleRigData() => jiggleRigData;

    public JiggleTree GetJiggleTree() => segment?.jiggleTree;

    public bool GetLockedFromGrabbing() => jiggleRigData.lockFromGrabbing;

    /// <summary>
    /// Resolved grab reach limit, as a multiple of a point's distance from the chain root.
    /// Authored zero (including data serialized before the field existed) falls back to the default.
    /// </summary>
    public float GetMaxGrabStretch() =>
        jiggleRigData.maxGrabStretch > 0f ? jiggleRigData.maxGrabStretch : JiggleGrabConstraint.DefaultMaxStretchFactor;

    /// <summary>
    /// Finds the closest simulated point to a world position, for external grab-style constraints.
    /// Skips virtual points, which have no bone to grab; the root particle is included because a
    /// single bone rig has nothing else, and the solver applies grabs after its root pin.
    /// </summary>
    public bool TryGetClosestGrabPoint(Vector3 worldPosition, float maxDistance, out int pointIndex, out Vector3 pointPosition) {
        pointIndex = -1;
        pointPosition = default;
        if (jiggleRigData.lockFromGrabbing) {
            return false;
        }
        var tree = segment?.jiggleTree;
        if (tree == null || tree.dirty || tree.points == null || tree.bones == null) {
            return false;
        }
        var points = tree.points;
        var bones = tree.bones;
        var count = Mathf.Min(points.Length, bones.Length);
        var bestDistanceSq = maxDistance * maxDistance;
        for (int i = 1; i < count; i++) {
            if (!points[i].hasTransform) {
                continue;
            }
            var bone = bones[i];
            if (!bone) {
                continue;
            }
            var distanceSq = (bone.position - worldPosition).sqrMagnitude;
            if (distanceSq < bestDistanceSq) {
                bestDistanceSq = distanceSq;
                pointIndex = i;
                pointPosition = bone.position;
            }
        }
        return pointIndex >= 0;
    }

    public JiggleTreeInputParameters GetInputParameters() => jiggleRigData.jiggleTreeInputParameters;
    
    
    /// <summary>
    /// Sets the jiggle tree input parameters, but only locally, to send it to jobs either make sure animatedParameters is true, or call UpdateParameters after you're done making changes.
    /// </summary>
    /// <param name="newParameters">The parameters that the jiggles should use to determine their motion.</param>
    public void SetInputParameters(JiggleTreeInputParameters newParameters) {
        jiggleRigData.jiggleTreeInputParameters = newParameters;
    }

    #if !JIGGLEPHYSICS_DISABLE_ON_ENABLE
    private void OnEnable() {
        OnInitialize();
    }
    #endif
    
    #if !JIGGLEPHYSICS_DISABLE_ON_DISABLE
    private void OnDisable() {
        OnRemove();
    }
    #endif

    public void OnInitialize() {
        if (jiggleRigData.rootBone == null) {
            Debug.LogError($"Jiggle Rig on '{name}' enabled without a root bone assigned, disabling it.", this);
            enabled = false;
            return;
        }

        jiggleRigData.RegenerateCacheLookup();

        segment ??= new JiggleTreeSegment(this);
        segment.SetAnimatedParameters(animatedParameters);
        segment.SetDirty();
        if (!addedToJiggleTreeSegments) {
            JigglePhysics.AddJiggleTreeSegment(segment);
            addedToJiggleTreeSegments = true;
        }
    }

    public void OnRemove() {
        if (segment != null && addedToJiggleTreeSegments) {
            JigglePhysics.RemoveJiggleTreeSegment(segment);
            addedToJiggleTreeSegments = false;
        }
    }

    /// <summary>
    /// Immediately resamples the rest pose of the bones in the tree. This can be useful if you have modified the bones' transforms on initialization and want to control when the rest pose is sampled.
    /// </summary>
    public void ResampleRestPose() {
        jiggleRigData.ResampleRestPose();
        // Has to go through the segment: JiggleTree.SetDirty raises its dirty flag before invoking
        // the callback, so by the time the segment's own SetDirty runs its `dirty: false` guard the
        // guard is already false and the tree is never scheduled for removal. It then gets added a
        // second time under the same rootID, leaking its transform slice.
        segment?.SetDirty();
    }

    public void RebuildTransformCache() {
        jiggleRigData.BuildNormalizedDistanceFromRootList();
        segment?.SetDirty();
    }

    public void SnapToRestPose() {
        jiggleRigData.SnapToRestPose();
    }

    public void Teleport(Vector3 deltaPosition) {
        segment?.Teleport(deltaPosition);
    }

    public void Teleport(Quaternion deltaRotation, Vector3 pivot, Vector3 deltaPosition) {
        segment?.Teleport(deltaRotation, pivot, deltaPosition);
    }
    
    /// <summary>
    /// Sends updated parameters to the jiggle tree on the jobs side.
    /// </summary>
    public void UpdateParameters() {
        if (segment == null) {
            return;
        }
        segment.UpdateParameters();
    }

    public bool HasAnimatedParameters {
        get => animatedParameters;
        set {
            animatedParameters = value;
            segment?.SetAnimatedParameters(value);
        }
    }

    private void OnValidate() {
        if (!jiggleRigData.hasSerializedData) {
            jiggleRigData = JiggleRigData.Default();
        }
        jiggleRigData.OnValidate();
        if (Application.isPlaying) {
            // Inspector edits write the serialized field directly, bypassing the property setter —
            // resync the segment mirror or a play-mode toggle never reaches the animated registry.
            segment?.SetAnimatedParameters(animatedParameters);
            UpdateParameters();
        }
    }

    private void OnDrawGizmosSelected() {
        if (!isActiveAndEnabled) {
            return;
        }
        jiggleRigData.OnDrawGizmosSelected();
    }

}

}
