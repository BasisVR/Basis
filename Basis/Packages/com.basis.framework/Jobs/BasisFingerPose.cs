using UnityEngine;
[System.Serializable]
public class BasisFingerPose
{
    public Vector3 PalmPos;
    public Vector3 WristPos;
    public Quaternion PalmRot;
    public Quaternion WristRot;

    public Vector2 ThumbPercentage;
    public Vector2 IndexPercentage;
    public Vector2 MiddlePercentage;
    public Vector2 RingPercentage;
    public Vector2 LittlePercentage;
}
