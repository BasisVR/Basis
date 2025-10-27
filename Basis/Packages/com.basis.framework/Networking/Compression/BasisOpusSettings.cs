using OpusSharp.Core;
using System;
using UnityEngine;

public static class LocalOpusSettings
{
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
    public static void EnsureProcessBuffer(ref float[] Processed, out int ProcessBufferLength)
    {
        ProcessBufferLength = SampleRate(); // Protect against negative sizes

        if (Processed == null)
        {
            Processed = new float[ProcessBufferLength];
            return;
        }

        if (Processed.Length != ProcessBufferLength)
        {
            Array.Resize(ref Processed, ProcessBufferLength);
        }
    }
    public static void CreateOrResizeArray(int Input,ref float[] Processed)
    {
        if (Processed == null)
        {
            Processed = new float[Input];
            return;
        }

        if (Processed.Length != Input)
        {
            Array.Resize(ref Processed, Input);
        }
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
