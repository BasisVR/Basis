using Basis.Scripts.Common;
using UnityEngine;

[System.Serializable]
public struct BasisPoseData
{
    [SerializeField]
    public Quaternion[] LeftThumb;
    [SerializeField]
    public Quaternion[] LeftIndex;
    [SerializeField]
    public Quaternion[] LeftMiddle;
    [SerializeField]
    public Quaternion[] LeftRing;
    [SerializeField]
    public Quaternion[] LeftLittle;
    [SerializeField]
    public Quaternion[] RightThumb;
    [SerializeField]
    public Quaternion[] RightIndex;
    [SerializeField]
    public Quaternion[] RightMiddle;
    [SerializeField]
    public Quaternion[] RightRing;
    [SerializeField]
    public Quaternion[] RightLittle;
}
