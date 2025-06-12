using System;
using UnityEngine;

namespace HVR.Basis.Comms
{
    [Serializable]
    public struct BlendshapeActuationDefinition
    {
        public string address;
        // InStart may be greater than InEnd, i.e. when we want to actuate from 0 to -1.
        public float inStart;
        public float inEnd;
        public float outStart;
        public float outEnd;
        public bool useCurve;
        public AnimationCurve curve;
        public string[] blendshapes;
        // If a blendshape actuator definition is searching for multiple blendshapes (due to different naming conventions),
        // and several exist, we don't want to actuate all of them. In this case, use onlyFirstMatch = true
        public bool onlyFirstMatch;
    }
}