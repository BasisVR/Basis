using System;

/// <summary>
/// Pure change-key math for the network-gizmo debug labels (<see cref="BasisPlayerNetworkGizmos"/>
/// and <see cref="BasisSyncGizmos"/>). The keys are deliberately coarse — interp-t in 10% steps,
/// playback rate in 5% steps, bandwidth in 128 B/s buckets — because the interp fraction cycles
/// 0→1 every network keyframe: a fine-grained key dirties every label every frame and TMP
/// re-tessellation becomes the dominant gizmo cost at high player counts. No UnityEngine
/// dependencies so the bucketing invariants are unit-testable outside the editor.
/// </summary>
public static class BasisNetworkGizmoLabelCore
{
    private static int Round(float value)
    {
        return (int)MathF.Round(value);
    }

    private static float Clamp01(float value)
    {
        if (value < 0f) return 0f;
        if (value > 1f) return 1f;
        return value;
    }

    /// <summary>
    /// Change-key for a per-player pose label (interp %, buffer depth, rate, bandwidth,
    /// and — with the additional-info toggle — the player's voice traffic).
    /// </summary>
    public static int PlayerLabelKey(int playerId, float interpT, float playbackRate, int stagedCount, float bytesPerSecond, float packetsPerSecond, bool showState, bool showBandwidth, float voiceBytesPerSecond = 0f, float voicePacketsPerSecond = 0f, bool showVoice = false)
    {
        int k = playerId * 397;
        if (showState)
        {
            int t = Round(Clamp01(interpT) * 10f);
            int rate = Round(playbackRate * 20f);
            k = (k * 31) ^ t ^ (stagedCount << 8) ^ (rate << 14);
        }
        if (showBandwidth)
        {
            k = (k * 31) ^ Round(bytesPerSecond * (1f / 128f)) ^ (Round(packetsPerSecond) << 16);
        }
        if (showVoice)
        {
            k = (k * 31) ^ Round(voiceBytesPerSecond * (1f / 128f)) ^ (Round(voicePacketsPerSecond) << 16);
        }
        return (k * 4) ^ (showState ? 2 : 0) ^ (showBandwidth ? 1 : 0) ^ (showVoice ? 4 : 0);
    }

    /// <summary>
    /// Change-key for the floating channel-totals readout (avatar / voice / scene sums).
    /// Counts are exact; rates ride the same 128 B/s and 1 pkt/s buckets as the labels so
    /// the summary re-tessellates at a human pace, not per sample.
    /// </summary>
    public static int OverviewKey(int avatarCount, float avatarBytesPerSecond, float avatarPacketsPerSecond, float voiceBytesPerSecond, float voicePacketsPerSecond, int sceneCount, float sceneBytesPerSecond, float scenePacketsPerSecond)
    {
        int k = 486187739;
        k = (k * 31) ^ avatarCount;
        k = (k * 31) ^ Round(avatarBytesPerSecond * (1f / 128f)) ^ (Round(avatarPacketsPerSecond) << 16);
        k = (k * 31) ^ Round(voiceBytesPerSecond * (1f / 128f)) ^ (Round(voicePacketsPerSecond) << 16);
        k = (k * 31) ^ sceneCount;
        k = (k * 31) ^ Round(sceneBytesPerSecond * (1f / 128f)) ^ (Round(scenePacketsPerSecond) << 16);
        return k;
    }

    /// <summary>Change-key for a synced-object label (interp %, buffer depth, extrapolation, bandwidth).</summary>
    public static int SyncLabelKey(int networkId, float interpT, int bufferDepth, bool extrapolating, float bytesPerSecond, float packetsPerSecond, bool showState, bool showBandwidth)
    {
        int k = networkId * 397;
        if (showState)
        {
            float clamped = interpT < 0f ? 0f : (interpT > 9.99f ? 9.99f : interpT);
            int t = Round(clamped * 10f);
            k = (k * 31) ^ t ^ (bufferDepth << 8) ^ (extrapolating ? 1 : 0);
        }
        if (showBandwidth)
        {
            k = (k * 31) ^ Round(bytesPerSecond * (1f / 128f)) ^ (Round(packetsPerSecond) << 16);
        }
        return (k * 4) ^ (showState ? 2 : 0) ^ (showBandwidth ? 1 : 0);
    }
}
