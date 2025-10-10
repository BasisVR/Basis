using UnityEngine.Events;
using System.Collections.Generic;

namespace uLipSync
{

    public static class Common
    {
        public const string AssetName = "uLipSync";
        public const float DefaultMinVolume = -2.5f;
        public const float DefaultMaxVolume = -1.5f;
        public const float DifferenceVolume = Common.DefaultMaxVolume - Common.DefaultMinVolume;
        public const float MfccMinValue = -50f;
        public const float MfccMaxValue = 30f;
    }

    public enum CompareMethod
    {
        L1Norm,
        L2Norm,
        CosineSimilarity,
    }
}
