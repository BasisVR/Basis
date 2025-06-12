using UnityEngine;
/// <summary>
/// 0.7 = straight fingers
/// -1 is fully closed
/// </summary>
[System.Serializable]
public struct BasisFingerPose
{
    public Vector2 ThumbPercentage;
    public Vector2 IndexPercentage;
    public Vector2 MiddlePercentage;
    public Vector2 RingPercentage;
    public Vector2 LittlePercentage;
}
