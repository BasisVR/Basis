using System;
using UnityEngine;

public partial class BasisLocalFootDriver
{
    [Serializable]
    private class BasisFootState
    {
        public string name;
        public Transform bone, thigh, shin;
        public int sideSign;
        public float thighLen, shinLen, legLength;

        public BasisFootPhase phase;
        public Vector3 plantedPos;
        public Quaternion plantedRot;
        public Vector3 stepStartPos, stepTargetPos;
        public Quaternion stepTargetRot;
        public float stepTimer, stepDur;

        public Vector3 idealPos, filteredNormal;
        public Vector3 currentPos;
        public Quaternion currentRot;
        public Vector3 kneeHint;

        public BasisFootState(string n, Transform b, int s)
        {
            name = n;
            bone = b;
            sideSign = s;
            filteredNormal = Vector3.up;
            phase = BasisFootPhase.Planted;
        }
    }
}
