using System.Collections.Generic;
using UnityEngine;

namespace Basis.Scripts.TransformBinders.BoneControl
{
[System.Serializable]
[CreateAssetMenu(fileName = "Data", menuName = "ScriptableObjects/FallBackBoneData", order = 1)]
public class BasisFallBackBoneData : ScriptableObject
{
    [SerializeField]
    public List<BasisFallBone> FallBackPercentage = new List<BasisFallBone>();
    [SerializeField]
    public List<BasisBoneTrackedRole> BoneTrackedRoles = new List<BasisBoneTrackedRole>();
    public bool FindBone(out BasisFallBone control, BasisBoneTrackedRole Role)
    {
        int Index = BoneTrackedRoles.IndexOf(Role);
        if (FallBackPercentage.Count > Index && Index != -1)
        {
            control = FallBackPercentage[Index];
            return true;
        }
        control = null;
        return false;
    }
}
}