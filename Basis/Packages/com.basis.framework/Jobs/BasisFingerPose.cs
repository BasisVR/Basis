using UnityEngine;
[System.Serializable]
public class BasisFingerPose
{
    public Vector3 PalmPos;
    public Vector3 WristPos;
    public Quaternion PalmRot;
    public Quaternion WristRot;

    // Each array has 3 elements: 0 = Proximal, 1 = Intermediate, 2 = Tip
    public Vector3[] thumbPositions = new Vector3[3];   // [Proximal, Intermediate, Tip]
    public Vector3[] indexPositions = new Vector3[3];
    public Vector3[] middlePositions = new Vector3[3];
    public Vector3[] ringPositions = new Vector3[3];
    public Vector3[] littlePositions = new Vector3[3];

    public Quaternion[] thumbRotations = new Quaternion[3];   // [Proximal, Intermediate, Tip]
    public Quaternion[] indexRotations = new Quaternion[3];
    public Quaternion[] middleRotations = new Quaternion[3];
    public Quaternion[] ringRotations = new Quaternion[3];
    public Quaternion[] littleRotations = new Quaternion[3];
}
