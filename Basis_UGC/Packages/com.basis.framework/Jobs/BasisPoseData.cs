using UnityEngine;

[System.Serializable]
public struct BasisPoseData
{
    [SerializeField]
    public BasisMuscleLocalPose[] LeftThumb;
    [SerializeField]
    public BasisMuscleLocalPose[] LeftIndex;
    [SerializeField]
    public BasisMuscleLocalPose[] LeftMiddle;
    [SerializeField]
    public BasisMuscleLocalPose[] LeftRing;
    [SerializeField]
    public BasisMuscleLocalPose[] LeftLittle;
    [SerializeField]
    public BasisMuscleLocalPose[] RightThumb;
    [SerializeField]
    public BasisMuscleLocalPose[] RightIndex;
    [SerializeField]
    public BasisMuscleLocalPose[] RightMiddle;
    [SerializeField]
    public BasisMuscleLocalPose[] RightRing;
    [SerializeField]
    public BasisMuscleLocalPose[] RightLittle;
}
