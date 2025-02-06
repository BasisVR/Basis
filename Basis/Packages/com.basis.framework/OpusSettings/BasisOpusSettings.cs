using Concentus.Enums;
using UnityEngine;
[CreateAssetMenu(fileName = "newBasisOpusSettings", menuName = "Opus Data")]
public class BasisOpusSettings : ScriptableObject
{
    public int RecordingFullLength = 1;
    public int BitrateKPS = 64000; // 128 kbps
    /// <summary>
    /// where 0 is the fastest on the cpu
    /// and 10 is the most performance hoggy
    /// recommend 10 as network performance is better.
    /// </summary>
    public int Complexity = 10;
    public int samplingFrequency = 48000;
    public OpusApplication OpusApplication = OpusApplication.OPUS_APPLICATION_AUDIO;
    public OpusSignal OpusSignal = OpusSignal.OPUS_SIGNAL_AUTO;
    public float DesiredDurationInSeconds = 0.02f;
    public int GetSampleFreq()
    {
        return samplingFrequency;
    }
    public int CalculateDesiredTime()
    {
        return Mathf.CeilToInt(DesiredDurationInSeconds * GetSampleFreq());
    }
    public float[] CalculateProcessBuffer()
    {
        return new float[CalculateDesiredTime()];
    }
    public int GetChannelAsInt()
    {
        return 1;
    }
}
