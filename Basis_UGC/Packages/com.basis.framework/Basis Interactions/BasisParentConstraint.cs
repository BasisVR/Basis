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
        Quaternion weightedRot = Quaternion.identity;

        for (int Index = 0; Index < sources.Length; Index++)
        {
            ref SourceData source = ref sources[Index];
            if (source.weight <= 0f) continue;

            // pos offset is in local space to the sorce
            Vector3 worldOffset = source.rotation * source.positionOffset;
            weightedPos += (source.position + worldOffset) * source.weight;

            Quaternion worldRotation = source.rotation * source.rotationOffset;
            if (totalWeight == 0f)
            {
                weightedRot = worldRotation;
            }
            else
            {
                weightedRot = Quaternion.Slerp(weightedRot, worldRotation, source.weight / (totalWeight + source.weight));
            }

            totalWeight += source.weight;
        }

        // apply weighted average
        if (totalWeight > 0f)
        {
            weightedPos /= totalWeight;
        }

        // global weight interpolates original with the calculated transform
        pos = Vector3.Lerp(_restPosition, weightedPos, GlobalWeight);
        rot = Quaternion.Slerp(_restRotation, weightedRot, GlobalWeight);

        return true;
    }

    public void UpdateSourcePositionAndRotation(int Index, Vector3 position, Quaternion rotation)
    {
        var source = sources[Index];
        source.position = position;
        source.rotation = rotation;
        sources[Index] = source;
    }

    public void SetOffsetPositionAndRotation(int Index, Vector3 positionOffset, Quaternion rotationOffset)
    {
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