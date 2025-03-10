using OpusSharp.Core;
using UnityEngine;

public static class LocalOpusSettings
{
    public static int RecordingFullLength = 1;
    public static OpusPredefinedValues OpusApplication = OpusPredefinedValues.OPUS_APPLICATION_AUDIO;
    public static float DesiredDurationInSeconds = 0.02f;
    public static int MicrophoneSampleRate = 48000;
    /// <summary>
    /// we only ever need one channel
    /// </summary>
    public static int Channels = 1;

    public static float noiseGateThreshold = 0.01f;
    public static float silenceThreshold = 0.0007f;
    public static int rmsWindowSize = 10;
    public static void SetDeviceAudioConfig(int maxFreq)
    {
    //    MicrophoneSampleRate = maxFreq;
    }
    public static int SampleRate()
    {
      return Mathf.CeilToInt(DesiredDurationInSeconds * MicrophoneSampleRate);
    }
    public static float[] CalculateProcessBuffer()
    {
        return new float[SampleRate()];
    }
}

public static class RemoteOpusSettings
{
    public static OpusPredefinedValues OpusApplication = OpusPredefinedValues.OPUS_APPLICATION_AUDIO;
    public static float DesiredDurationInSeconds = 0.02f;
    public const int NetworkSampleRate = 48000;
    public static int PlayBackSampleRate = AudioSettings.outputSampleRate;
    /// <summary>
    /// we only ever need one channel
    /// </summary>
    public static int Channels { get; private set; } = 1;
    public static int SampleLength => NetworkSampleRate * Channels;
    public static int Pcmlength => CalculatePCMSize();
    public static int RecieverLength => Pcmlength * Capacity;
    public static int RecieverLengthCapacity => RecieverLength * Capacity;
    public static int Capacity = 4;
    /// <summary>
    /// 960 by default
    /// </summary>
    /// <returns></returns>
    private static int CalculatePCMSize()
    {
        return Mathf.CeilToInt(DesiredDurationInSeconds * NetworkSampleRate);
    }
}
