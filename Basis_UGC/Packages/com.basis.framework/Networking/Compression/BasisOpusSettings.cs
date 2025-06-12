using LiteNetLib;
using OpusSharp.Core;
using UnityEngine;

public static class LocalOpusSettings
{
    public const DeliveryMethod AudioSendMethod = DeliveryMethod.Sequenced;
    public static int RecordingFullLength = 1;
    public static OpusPredefinedValues OpusApplication = OpusPredefinedValues.OPUS_APPLICATION_AUDIO;
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
      return Mathf.CeilToInt(SharedOpusSettings.DesiredDurationInSeconds * MicrophoneSampleRate);
    }
    public static float[] CalculateProcessBuffer()
    {
        return new float[SampleRate()];
    }
}
public static class SharedOpusSettings
{
    public static float DesiredDurationInSeconds = 0.02f;
}
public static class RemoteOpusSettings
{
    public static OpusPredefinedValues OpusApplication = OpusPredefinedValues.OPUS_APPLICATION_AUDIO;

    public const int NetworkSampleRate = 48000;
    public static int PlayBackSampleRate = AudioSettings.outputSampleRate;
    /// <summary>
    /// we only ever need one channel
    /// </summary>
    public static int Channels { get; private set; } = 1;
    public static int SampleLength => NetworkSampleRate * Channels;
    //960 a single frame in opus. in unity it is 1024 for audio playback
    public static int FrameSize => Mathf.CeilToInt(SharedOpusSettings.DesiredDurationInSeconds * NetworkSampleRate);
    public static int TotalFrameBufferSize => FrameSize * AdditionalStoredBufferData;

    public static int AdditionalStoredBufferData = 16;
    public static int JitterBufferSize = 5;
}
