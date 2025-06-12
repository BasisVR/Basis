using UnityEngine;

namespace Basis.Scripts.TransformBinders.BoneControl
{
[System.Serializable]
public class BasisFallBone
{
    [SerializeField]
    public Vector3 Position;
    [SerializeField]
    public Vector3 PositionPercentage;
    [SerializeField]
    public HumanBodyBones HumanBone;
    [SerializeField]
    public BasisBoneTrackedRole Role;
}
}