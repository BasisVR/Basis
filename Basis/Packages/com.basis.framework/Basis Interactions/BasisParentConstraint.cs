using System;
using UnityEngine;

[Serializable]
public class BasisParentConstraint
{

    [Serializable]
    public struct SourceData
    {
        public Vector3 position;
        public Quaternion rotation;

        public Vector3 positionOffset;
        public Quaternion rotationOffset;
        [Range(0, 1)]
        public float weight;
    }

    [SerializeField]
    public bool Enabled = true;
    [SerializeField]
    public float GlobalWeight = 1f;
    [Space(10)]
    [SerializeField]
    private Vector3 _restPosition;
    [SerializeField]
    private Quaternion _restRotation;

    [SerializeField]
    public SourceData[] sources;



    public bool Evaluate(out Vector3 pos, out Quaternion rot)
    {
        if (
            !Enabled ||
            GlobalWeight <= float.Epsilon ||
            sources == null ||
            sources.Length == 0
        )
        {
            pos = Vector3.zero;
            rot = Quaternion.identity;
            return false;
        }

        float totalWeight = 0f;
        Vector3 weightedPos = Vector3.zero;
        Vector4 weightedQuat = Vector4.zero;
        
        for (int index = 0; index < sources.Length; index++)
        {
            ref SourceData source = ref sources[index];
            if (source.weight <= 0f) continue;
            
            Vector3 worldOffset = source.rotation * source.positionOffset;
            weightedPos += (source.position + worldOffset) * source.weight;
            
            // Rotation - accumulate as 4D vectors
            Quaternion worldRotation = source.rotation * source.rotationOffset;
            
            // Ensure quaternions are in same hemisphere (handle q and -q representing same rotation)
            if (totalWeight > 0f && Vector4.Dot(weightedQuat.normalized, new Vector4(worldRotation.x, worldRotation.y, worldRotation.z, worldRotation.w)) < 0f)
            {
                worldRotation = new Quaternion(-worldRotation.x, -worldRotation.y, -worldRotation.z, -worldRotation.w);
            }
            
            weightedQuat += new Vector4(worldRotation.x, worldRotation.y, worldRotation.z, worldRotation.w) * source.weight;
            totalWeight += source.weight;
        }
        
        // Normalize results
        if (totalWeight > 0f)
        {
            weightedPos /= totalWeight;
            weightedQuat /= totalWeight;
            
            // Convert back to quaternion and normalize
            Quaternion blendedRot = new Quaternion(weightedQuat.x, weightedQuat.y, weightedQuat.z, weightedQuat.w).normalized;
            
            // Apply global weight
            pos = Vector3.Lerp(_restPosition, weightedPos, GlobalWeight);
            rot = Quaternion.Slerp(_restRotation, blendedRot, GlobalWeight);
            return true;
        }
        
        pos = _restPosition;
        rot = _restRotation;
        return false;
    }

    public void UpdateSourcePositionAndRotation(int Index, Vector3 position, Quaternion rotation)
    {
        if (Index < 0 || Index >= sources.Length) return;
        var source = sources[Index];
        source.position = position;
        source.rotation = rotation;
        sources[Index] = source;
    }

    public void SetOffsetPositionAndRotation(int Index, Vector3 positionOffset, Quaternion rotationOffset)
    {
        if (Index < 0 || Index >= sources.Length) return;
        var source = sources[Index];
        source.positionOffset = positionOffset;
        source.rotationOffset = rotationOffset;
        sources[Index] = source;
    }

    public void SetRestPositionAndRotation(Vector3 restPosition, Quaternion restRotation)
    {
        _restPosition = restPosition;
        _restRotation = restRotation;
    }

    // TODO: in editor polling (just onvalidate?)
}