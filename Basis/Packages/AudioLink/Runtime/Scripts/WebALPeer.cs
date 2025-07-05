using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

#if UNITY_WEBGL
namespace AudioLink
{
    public class WebALPeer : MonoBehaviour
    {
        const int SAMPLES_COUNT = 4096;
        [HideInInspector][NonSerialized]
        public float[] WaveformSamplesLeft, WaveformSamplesRight;

        public WebALPeer()
        {
            WaveformSamplesLeft = new float[SAMPLES_COUNT];
            WaveformSamplesRight = new float[SAMPLES_COUNT];
        }

    }
}
#endif
