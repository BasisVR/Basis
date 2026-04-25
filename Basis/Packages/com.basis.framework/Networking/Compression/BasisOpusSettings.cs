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

    /// <summary>
    /// Expected packet loss percentage used to tune Opus's in-band Forward Error
    /// Correction (OPUS_SET_PACKET_LOSS_PERC). Higher values spend more bitrate
    /// on redundant FEC data embedded in each packet, giving the decoder a better
    /// chance of reconstructing a single-packet loss via decode_fec=true. Valid
    /// range is 0..100; Opus recommends ~10 for moderate networks and up to ~30
    /// for lossy ones. Set to 0 to effectively disable FEC. ~15% bitrate overhead
    /// at the default value.
    ///
    /// Prefer <see cref="SetPacketLossPercent"/> for runtime changes so live encoders
    /// pick up the new value via <see cref="OnPacketLossPercentChanged"/>.
    /// </summary>
    public static int PacketLossPercent = 10;

    /// <summary>
    /// Fired whenever <see cref="SetPacketLossPercent"/> changes the value. Live
    /// encoders subscribe to re-issue OPUS_SET_PACKET_LOSS_PERC without having to
    /// tear down the encoder.
    /// </summary>
    public static event Action<int> OnPacketLossPercentChanged;

    /// <summary>Clamp, dedupe, and fire the change event.</summary>
    public static void SetPacketLossPercent(int percent)
    {
        if (percent < 0) percent = 0;
        else if (percent > 100) percent = 100;
        if (PacketLossPercent == percent) return;
        PacketLossPercent = percent;
        OnPacketLossPercentChanged?.Invoke(percent);
    }
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
    public static int JitterBufferSize = 5;
}
