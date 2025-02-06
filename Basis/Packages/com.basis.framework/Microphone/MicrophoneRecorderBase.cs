using UnityEngine;
using System;
public abstract class MicrophoneRecorderBase : MonoBehaviour
{
    public static Action OnHasAudio;
    public static Action OnHasSilence; // Event triggered when silence is detected
    public AudioClip clip;
    public BasisOpusSettings BasisOpusSettings;
    public bool IsInitialize = false;
    public string MicrophoneDevice = null;
    public float silenceThreshold = 0.0007f; // RMS threshold for detecting silence
    public int samplingFrequency;
    public int ProcessBufferLength;
    public float Volume = 1; // Volume adjustment factor, default to 1 (no adjustment)
    [HideInInspector]
    public float[] microphoneBufferArray;
    [HideInInspector]
    public float[] processBufferArray;
    public float noiseGateThreshold = 0.01f; // Threshold for the noise gate
    public int Channels = 1;
    [HideInInspector]
    public float[] rmsValues;
    public int rmsIndex = 0;
    public int rmsWindowSize = 10; // Size of the moving average window
    public float averageRms;
    public RNNoise.NET.Denoiser Denoiser = new RNNoise.NET.Denoiser();
    public void OnDestroy()
    {
        Denoiser.Dispose();
    }
}
