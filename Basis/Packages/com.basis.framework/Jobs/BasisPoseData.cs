using UnityEngine;
[System.Serializable]
public class BasisPoseData
{
    [SerializeField]
    public Quaternion[] LeftThumb = new Quaternion[3];
    [SerializeField]
    public Quaternion[] LeftIndex = new Quaternion[3];
    [SerializeField]
    public Quaternion[] LeftMiddle = new Quaternion[3];
    [SerializeField]
    public Quaternion[] LeftRing = new Quaternion[3];
    [SerializeField]
    public Quaternion[] LeftLittle = new Quaternion[3];
    [SerializeField]
    public Quaternion[] RightThumb = new Quaternion[3];
    [SerializeField]
    public Quaternion[] RightIndex = new Quaternion[3];
    [SerializeField]
    public Quaternion[] RightMiddle = new Quaternion[3];
    [SerializeField]
    public Quaternion[] RightRing = new Quaternion[3];
    [SerializeField]
    public Quaternion[] RightLittle = new Quaternion[3];
}
