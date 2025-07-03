using Basis.Scripts.Common;
using UnityEngine;

[System.Serializable]
public struct BasisPoseData
{
    [SerializeField]
    public BasisCalibratedCoords[] LeftThumb;
    [SerializeField]
    public BasisCalibratedCoords[] LeftIndex;
    [SerializeField]
    public BasisCalibratedCoords[] LeftMiddle;
    [SerializeField]
    public BasisCalibratedCoords[] LeftRing;
    [SerializeField]
    public BasisCalibratedCoords[] LeftLittle;
    [SerializeField]
    public BasisCalibratedCoords[] RightThumb;
    [SerializeField]
    public BasisCalibratedCoords[] RightIndex;
    [SerializeField]
    public BasisCalibratedCoords[] RightMiddle;
    [SerializeField]
    public BasisCalibratedCoords[] RightRing;
    [SerializeField]
    public BasisCalibratedCoords[] RightLittle;
}
