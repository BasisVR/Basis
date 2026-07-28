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

    /// <summary>
    /// Default bitrate (bps) the local Opus encoder is created with. The server can
    /// override this per-user at runtime via the admin path; that override goes through
    /// <see cref="SetBitrateOverride"/> which fires <see cref="OnBitrateChanged"/>.
    /// 0 in <see cref="BitrateOverride"/> means "no override — use DefaultBitrate".
    /// </summary>
    public const int DefaultBitrate = 32000;

    /// <summary>
    /// Current admin-pushed bitrate override (bps). 0 = no override, use DefaultBitrate.
    /// </summary>
    public static int BitrateOverride = 0;

    /// <summary>
    /// User-chosen voice bitrate (bps) for direct (P2P) connections, from the
    /// Networking section of general settings. 0 when the override toggle is off.
    /// </summary>
    public static int UserDirectConnectBitrate =>
        Basis.BasisUI.BasisSettingsDefaults.P2PVoiceBitrateOverride != null
        && Basis.BasisUI.BasisSettingsDefaults.P2PVoiceBitrateOverride.RawValue
        && Basis.BasisUI.BasisSettingsDefaults.P2PVoiceBitrate != null
            ? Mathf.RoundToInt(Basis.BasisUI.BasisSettingsDefaults.P2PVoiceBitrate.RawValue)
            : 0;

    /// <summary>
    /// "Normal networking quality" — bitrate for voice that travels through the server
    /// relay (every listener not on a direct link). The admin override when set, otherwise
    /// <see cref="DefaultBitrate"/>. The direct-connect preference deliberately does not
    /// feed into this, so a local override can never change what the server receives.
    /// </summary>
    public static int ServerBitrate => BitrateOverride > 0 ? BitrateOverride : DefaultBitrate;

    /// <summary>
    /// "Direct connect quality" — bitrate for voice sent over a direct (P2P) link. The
    /// user's local override when set, otherwise it matches <see cref="ServerBitrate"/>.
    /// </summary>
    public static int DirectConnectBitrate
    {
        get
        {
            int userDirect = UserDirectConnectBitrate;
            return userDirect > 0 ? userDirect : ServerBitrate;
        }
    }

    /// <summary>
    /// Re-pushes <see cref="ServerBitrate"/> to the live server encoder. The direct-connect
    /// encoder tracks <see cref="DirectConnectBitrate"/> itself at encode time, so a P2P
    /// session connecting/disconnecting or the direct-connect slider moving need not disturb
    /// the server encoder.
    /// </summary>
    public static void ReevaluateEffectiveBitrate()
    {
        OnBitrateChanged?.Invoke(ServerBitrate);
    }

    /// <summary>Fired whenever <see cref="ServerBitrate"/> changes.</summary>
    public static event Action<int> OnBitrateChanged;

    /// <summary>Apply a server-pushed bitrate override (bps). Pass 0 to clear.</summary>
    public static void SetBitrateOverride(int bps)
    {
        if (bps < 0) bps = 0;
        if (BitrateOverride == bps) return;
        BitrateOverride = bps;
        OnBitrateChanged?.Invoke(ServerBitrate);
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
    /// <summary>
    /// Opus frame duration in seconds. Only 0.02 (20 ms) and 0.04 (40 ms) are
    /// supported; the encoder accepts other Opus durations but the rest of the
    /// audio pipeline only assumes these two. Mutate via
    /// <see cref="SetDesiredDurationInSeconds"/> so subscribers can react.
    /// </summary>
    public static float DesiredDurationInSeconds = 0.02f;

    /// <summary>Fired when <see cref="SetDesiredDurationInSeconds"/> changes the value.</summary>
    public static event Action<float> OnDesiredDurationChanged;

    /// <summary>Set, dedupe, and fire the change event. Accepts only 0.02f and 0.04f.</summary>
    public static void SetDesiredDurationInSeconds(float durationSeconds)
    {
        if (durationSeconds != 0.02f && durationSeconds != 0.04f)
        {
            durationSeconds = 0.02f;
        }
        if (DesiredDurationInSeconds == durationSeconds) return;
        DesiredDurationInSeconds = durationSeconds;
        OnDesiredDurationChanged?.Invoke(durationSeconds);
    }
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
    /// <summary>
    /// Largest frame size we ever support at runtime (40 ms @ 48 kHz = 1920 samples).
    /// Decoder-side scratch buffers are sized to this so a server-pushed frame-duration
    /// change from 20ms→40ms doesn't leave a 960-sample buffer too small for a 1920
    /// decoded frame.
    /// </summary>
    public const int MaxFrameSize = 1920;
    public static int JitterBufferSize = 5;
}
