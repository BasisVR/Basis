using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.UIElements;
#endif

namespace GatorDragonGames.JigglePhysics {

[Serializable]
public struct JiggleTransformCachedData {
    public Transform bone;
    public float normalizedDistanceFromRoot;
    public float lossyScale;
    public Vector3 restLocalPosition;
    public Vector4 restLocalRotation;
}

[Serializable]
public struct JiggleRigData {
    [SerializeField] public bool hasSerializedData;
    [SerializeField] public string serializedVersion;
    [SerializeField] public Transform rootBone;
    [SerializeField] public bool excludeRoot;
    // Inverted sense on purpose: absent in previously-serialized data -> false -> grabbable.
    [SerializeField] public bool lockFromGrabbing;
    // Zero means "use the shipped default", which is what data serialized before this field
    // existed deserializes to — so old bundles get a sensible pull limit rather than a rigid
    // chain (0) with no migration step. Use lockFromGrabbing to forbid grabbing outright.
    [SerializeField] public float maxGrabStretch;
    [SerializeField] public JiggleTreeInputParameters jiggleTreeInputParameters;
    [SerializeField] public Transform[] excludedTransforms;
    [SerializeField, HideInInspector] public JiggleTransformCachedData[] transformCachedData;
    [SerializeField] public JiggleColliderSerializable[] jiggleColliders;
    
    [NonSerialized]
    private Dictionary<Transform, JiggleTransformCachedData> transformToCachedDataMap;
    [NonSerialized]
    private HashSet<Transform> excludedSet;

    private bool TryUpdateSerialization() {
        switch (serializedVersion) {
            case "v0.0.0": // Collision radius local space -> world space
                if (rootBone == null) {
                    return false;
                }
                var cachedScale = GetCache(rootBone);
                var scale = rootBone.lossyScale;
                var scaleSample = (scale.x + scale.y + scale.z)/3f;
                var scaleCorrection = cachedScale.lossyScale*(1f/(scaleSample*scaleSample));
                jiggleTreeInputParameters.collisionRadius.value *= scaleCorrection;
                serializedVersion = "v0.0.1";
                return true;
            case "v0.0.1": // rest pose is now serialized on author, generate if missing.
                var length = transformCachedData.Length;
                for (int i = 0; i < length; i++) {
                    var cachedData = transformCachedData[i];
                    var t = cachedData.bone;
                    if (!t) continue;
                    t.GetLocalPositionAndRotation(out var localPosition, out var localRotation);
                    cachedData.restLocalPosition = localPosition;
                    cachedData.restLocalRotation = new Vector4(localRotation.x, localRotation.y, localRotation.z, localRotation.w);
                    transformCachedData[i] = cachedData;
                }
                serializedVersion = "v0.0.2";
                return true;
            default:
                return false;
        }
    }

    public void ResampleRestPose() {
        var length = transformCachedData.Length;
        for (int i = 0; i < length; i++) {
            var cachedData = transformCachedData[i];
            var t = cachedData.bone;
            if (!t) continue;
            t.GetLocalPositionAndRotation(out var localPosition, out var localRotation);
            cachedData.restLocalPosition = localPosition;
            cachedData.restLocalRotation = new Vector4(localRotation.x, localRotation.y, localRotation.z, localRotation.w);
            transformCachedData[i] = cachedData;
        }
        RegenerateCacheLookup();
    }

    public void SnapToRestPose() {
        var length = transformCachedData.Length;
        for (int i = 0; i < length; i++) {
            var cachedData = transformCachedData[i];
            var t = cachedData.bone;
            if (!t || t == rootBone) continue;
            t.SetLocalPositionAndRotation(cachedData.restLocalPosition, new Quaternion(cachedData.restLocalRotation.x, cachedData.restLocalRotation.y, cachedData.restLocalRotation.z, cachedData.restLocalRotation.w));
        }
    }

    public void RegenerateCacheLookup() {
        var count = transformCachedData.Length;
        // Clear-and-reuse: this runs on every rig init, twice per avatar load on remotes, so a
        // fresh dictionary per call was steady allocation churn at lobby scale.
        if (transformToCachedDataMap == null) {
            transformToCachedDataMap = new Dictionary<Transform, JiggleTransformCachedData>(count);
        } else {
            transformToCachedDataMap.Clear();
        }
        for (int i = 0; i < count; i++) {
            var cachedData = transformCachedData[i];
            transformToCachedDataMap[cachedData.bone] = cachedData;
        }
        if (excludedTransforms != null && excludedTransforms.Length > 0) {
            var excludedCount = excludedTransforms.Length;
            if (excludedSet == null) {
                excludedSet = new HashSet<Transform>(excludedCount);
            } else {
                excludedSet.Clear();
            }
            for (int i = 0; i < excludedCount; i++) {
                excludedSet.Add(excludedTransforms[i]);
            }
        } else {
            excludedSet = null;
        }
    }

    public bool GetIsExcluded(Transform t) {
        if (excludedSet != null) {
            return excludedSet.Contains(t);
        }
        var count = excludedTransforms.Length;
        for (int i = 0; i < count; i++) {
            if (excludedTransforms[i] == t) {
                return true;
            }
        }
        return false;
    }
    
    /// <summary>
    /// Matches the cap OnValidate applies in the editor. OnValidate never runs for content loaded at
    /// runtime, so without this a bundle can declare any number of colliders and each one costs a
    /// pass per point per substep inside the simulate job.
    /// </summary>
    public const int MaxRuntimeJiggleColliders = 32;

    public void GetJiggleColliders(List<JiggleCollider> colliders) {
        colliders.Clear();
        var count = jiggleColliders.Length;
        if (count > MaxRuntimeJiggleColliders) {
            count = MaxRuntimeJiggleColliders;
        }
        for(int i=0;i<count;i++) {
            colliders.Add(jiggleColliders[i].collider);
        }
    }

    void ValidateCurve(ref AnimationCurve animationCurve) {
        if (animationCurve == null || animationCurve.length == 0) {
            animationCurve = AnimationCurve.Linear(0f, 1f, 1f, 1f);
        }
    }

    public void OnValidate() {
        jiggleTreeInputParameters.OnValidate();
        excludedTransforms ??= Array.Empty<Transform>();
        ValidateCurve(ref jiggleTreeInputParameters.stiffness.curve);
        ValidateCurve(ref jiggleTreeInputParameters.angleLimit.curve);
        ValidateCurve(ref jiggleTreeInputParameters.stretch.curve);
        ValidateCurve(ref jiggleTreeInputParameters.drag.curve);
        ValidateCurve(ref jiggleTreeInputParameters.airDrag.curve);
        ValidateCurve(ref jiggleTreeInputParameters.gravity.curve);
        ValidateCurve(ref jiggleTreeInputParameters.collisionRadius.curve);
        // Bones are mid-simulation in play mode; resampling would bake the jiggled pose into the serialized rest.
        if (Application.isPlaying) {
            RegenerateCacheLookup();
        } else {
            BuildNormalizedDistanceFromRootList();
        }
        for (int i = 0; i < 100; i++) {
            if (!TryUpdateSerialization()) {
                break;
            }
        }
        if (jiggleColliders is { Length: > 32 }) {
            Debug.LogWarning("JigglePhysics: Maximum of 32 personal Jiggle Colliders are supported per tree. Extra colliders will be dropped.");
            Array.Resize(ref jiggleColliders, 32);
        }
    }
    public void BuildNormalizedDistanceFromRootList() {
        if (!rootBone) {
            return;
        }
        var data = new List<JiggleTransformCachedData>();
        float maxLength = 0f;
        VisitAndSetCacheData(data, rootBone, rootBone.position, 0f, ref maxLength);
        var totalLength = Mathf.Max(maxLength, 0.001f);
        var dataCount = data.Count;
        for (int i = 0; i < dataCount; i++) {
            var entry = data[i];
            entry.normalizedDistanceFromRoot /= totalLength;
            data[i] = entry;
        }
        transformCachedData = data.ToArray();
        RegenerateCacheLookup();
    }

    private void VisitAndSetCacheData(List<JiggleTransformCachedData> data, Transform t, Vector3 lastPosition, float currentLength, ref float maxLength, int depth = 0) {
        if (t == null || GetIsExcluded(t)) {
            return;
        }
        if (depth >= JigglePhysics.MAX_VISIT_DEPTH) {
            return;
        }
        var scale = t.lossyScale;
        currentLength += Vector3.Distance(lastPosition, t.position);
        if (currentLength > maxLength) maxLength = currentLength;
        t.GetLocalPositionAndRotation(out var localPosition, out var localRotation);
        var position = t.position;
        data.Add(new JiggleTransformCachedData() {
            bone = t,
            restLocalPosition = localPosition,
            restLocalRotation = new Vector4(localRotation.x, localRotation.y, localRotation.z, localRotation.w),
            normalizedDistanceFromRoot = currentLength,
            lossyScale = (scale.x + scale.y + scale.z)/3f,
        });
        var childCount = t.childCount;
        for (int i = 0; i < childCount; i++) {
            var child = t.GetChild(i);
            if (child == null || GetIsExcluded(child)) continue;
            VisitAndSetCacheData(data, child, position, currentLength, ref maxLength, depth + 1);
        }
    }

    public void GetValidChildrenInto(Transform t, List<Transform> into) {
        into.Clear();
        if (t == null) return;
        var childCount = t.childCount;
        for (int i = 0; i < childCount; i++) {
            var child = t.GetChild(i);
            if (child == null || GetIsExcluded(child)) continue;
            into.Add(child);
        }
    }

    public int GetValidChildrenCount(Transform t) {
        if (t == null) return 0;
        int count = 0;
        var childCount = t.childCount;
        for(int i=0;i<childCount;i++) {
            var child = t.GetChild(i);
            if (child == null || GetIsExcluded(child)) continue;
            count++;
        }
        return count;
    }

    public Transform GetValidChild(Transform t, int index) {
        if (t == null) return null;
        int count = 0;
        var childCount = t.childCount;
        for(int i=0;i<childCount;i++) {
            var child = t.GetChild(i);
            if (child == null || GetIsExcluded(child)) continue;
            if (count == index) {
                return child;
            }
            count++;
        }
        return null;
    }
    
    public void GetJiggleColliderTransforms(List<Transform> colliderTransforms) {
        colliderTransforms.Clear();
        var count = jiggleColliders.Length;
        for(int i=0;i<count;i++) {
            colliderTransforms.Add(jiggleColliders[i].transform);
        }
    }
    
    public bool GetHasRootTransformError() => !rootBone;
    public bool GetCacheIsValid() {
        if (transformCachedData is not { Length: > 0 } || transformToCachedDataMap == null || transformToCachedDataMap.Count != transformCachedData.Length) {
            return false;
        }
        var count = transformCachedData.Length;
        for (int i = 0; i < count; i++) {
            if (!transformCachedData[i].bone) return false;
        }
        return true;
    }
    public JiggleTransformCachedData GetCache(Transform t) {
#if UNITY_EDITOR
        if (transformToCachedDataMap == null) {
            throw new InvalidOperationException("JiggleRigData: Cache lookup not initialized. Call RegenerateCacheLookup() first.");
        }
        if (!transformToCachedDataMap.TryGetValue(t, out var cachedData)) {
            throw new KeyNotFoundException($"JiggleRigData: Transform '{t.name}' not found in cache. Ensure it is a child of the root bone and not excluded.");
        }
        return cachedData;
#else
        return transformToCachedDataMap[t];
#endif
    }

    /// <summary>
    /// Sends updated parameters to the jiggle tree on the jobs side. Uses the provided list to prevent allocations.
    /// </summary>
    /// <param name="tree">Tree to update</param>
    /// <param name="parameters">empty list purely used to prevent allocations</param>
    public void UpdateParameters(JiggleTree tree, List<JigglePointParameters> parameters) {
        parameters.Clear();
        var bones = tree.bones;
        if (bones == null) {
            return;
        }
        var boneCount = bones.Length;
        for (int i = 0; i < boneCount; i++) {
            var bone = bones[i];
            var cache = GetCache(bone);
            var parameter = GetJiggleBoneParameter(cache.normalizedDistanceFromRoot);
            // Mirror the pinned override CreateJiggleTree's Visit assigns, otherwise every
            // animated-parameter push unpins an excluded root and the whole chain sways.
            // Slot 0 is exempt: it is the back projected virtual root, which only borrows the root
            // bone's transform to keep the bone and point arrays aligned. Visit never sees it, so
            // CreateJiggleTree leaves it on the authored parameters — overriding it here instead
            // zeroed the gravity the first real bone integrates against.
            if (i != 0 && ((excludeRoot && bone == rootBone) || GetIsExcluded(bone))) {
                parameter = new JigglePointParameters() {
                    angleElasticity = 1f,
                    lengthElasticity = 1f,
                    rootElasticity = 1f,
                    elasticitySoften = 0f
                };
            }
            parameters.Add(parameter);
        }
        tree.SetParameters(parameters);
    }
    
    public JigglePointParameters GetJiggleBoneParameter(float normalizedDistanceFromRoot) {
        return jiggleTreeInputParameters.ToJigglePointParameters(normalizedDistanceFromRoot);
    }
    
    public Transform[] GetJiggleBoneTransforms() {
        return rootBone.GetComponentsInChildren<Transform>();
    }
    
    public bool IsValid(Transform root) => (rootBone && rootBone.IsChildOf(root));
    public static JiggleRigData Default() {
        return new JiggleRigData {
            rootBone = null,
            serializedVersion = "v0.0.2",
            hasSerializedData = true,
            excludeRoot = false,
            lockFromGrabbing = false,
            maxGrabStretch = JiggleGrabConstraint.DefaultMaxStretchFactor,
            jiggleTreeInputParameters = JiggleTreeInputParameters.Default(),
            excludedTransforms = Array.Empty<Transform>(),
            transformCachedData = Array.Empty<JiggleTransformCachedData>(),
            jiggleColliders = Array.Empty<JiggleColliderSerializable>() 
        };
    }

    public void OnDrawGizmosSelected() {
        if (jiggleColliders != null) {
            var count = jiggleColliders.Length;
            for(int i=0;i<count;i++) {
                jiggleColliders[i].OnDrawGizmosSelected();
            }
        }
        
        if (!rootBone) return;
        Gizmos.color = new Color(0.9607844f, 0.9607844f, 0.9607844f, 1f);
        var jiggleTree = JigglePhysics.CreateJiggleTree(this, null);
        var points = jiggleTree.points;
        var parameters = jiggleTree.parameters;
        var pointCount = points.Length;
        var cam = Camera.current;
        for (var index = 0; index < pointCount; index++) {
            var simulatedPoint = points[index];
            if (simulatedPoint.parentIndex == -1) continue;
            if (!points[simulatedPoint.parentIndex].hasTransform) continue;
            DrawBone(points[simulatedPoint.parentIndex].position, simulatedPoint.position, jiggleTree.bones[index].lossyScale, parameters[simulatedPoint.parentIndex], cam);
        }
    }
    
    private static void DrawWireDisc(Vector3 center, Vector3 normal, float radius, int segmentCount = 32) {
        normal.Normalize();
        Vector3 up = normal;
        Vector3 forward = Vector3.Slerp(up, -up, 0.5f);
        Vector3 right = Vector3.Cross(up, forward).normalized * radius;

        float angleStep = 360f / segmentCount;
        Vector3 prevPoint = center + right;
        for (int i = 1; i <= segmentCount; i++) {
            float angle = angleStep * i;
            Quaternion rot = Quaternion.AngleAxis(angle, up);
            Vector3 nextPoint = center + rot * right;
            Gizmos.DrawLine(prevPoint, nextPoint);
            prevPoint = nextPoint;
        }
    }
    
    private void DrawBone(Vector3 boneHead, Vector3 boneTail, Vector3 boneScale, JigglePointParameters jigglePointParameters, Camera cam) {
        var camForward = cam.transform.forward;
        var scale = jigglePointParameters.collisionRadius * (boneScale.x + boneScale.y + boneScale.z)/3f;
        DrawWireDisc(boneHead, camForward, scale);
        Gizmos.DrawLine(boneHead, boneTail);
        var boneDirection = (boneTail - boneHead).normalized;
        var angleLimitScale = 0.05f;
        // angleLimit is normalized 0..1 of a 90 degree half-angle (see the simulate job's
        // `angleLimit * PI * 0.5`), not degrees.
        var angleLimitRadians = jigglePointParameters.angleLimit * Mathf.PI * 0.5f;
        DrawWireDisc(boneHead + boneDirection * (angleLimitScale * Mathf.Cos(angleLimitRadians)),
            boneDirection,
            angleLimitScale * Mathf.Sin(angleLimitRadians));
    }
#if UNITY_EDITOR
#endif
}
}
