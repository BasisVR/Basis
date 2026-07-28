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
        public Vector3 plantedBodyFwd;
        public Vector3 stepStartPos, stepTargetPos;
        /// <summary>Foot rotation frozen at lift-off; the swing blends FROM it. Mirrors stepStartPos.</summary>
        public Quaternion stepStartRot;
        public float stepTimer, stepDur;
        /// <summary>Seconds since this foot landed; gates the double-support window. Mirrors
        /// BasisFootNativeState.plantedTime — it lived ONLY in the native struct, so every
        /// FootStateToNative rebuild silently reset it to "just landed" and locked both feet
        /// out of stepping for a full doubleSupportSec.</summary>
        public float plantedTime;

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
            plantedTime = SettledPlantedTime;
        }
    }
}
