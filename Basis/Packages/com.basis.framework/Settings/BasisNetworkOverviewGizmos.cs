using System.Collections.Generic;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.Receivers;
using Basis.Scripts.Networking.Sync;
using UnityEngine;

/// <summary>
/// Floating overall network readout — the "additional information" gizmo. One billboarded
/// label ahead of the viewer totalling the received traffic per channel: the avatar channel
/// (every remote player's pose stream), the voice channel (spatial + shout audio), and the
/// scene channel (every spawned/synced object). Driven each frame from
/// <see cref="SMModuleDebugOptions"/>; the label text only re-tessellates when a
/// quantized total moves (see BasisNetworkGizmoLabelCore.OverviewKey).
/// </summary>
public static class BasisNetworkOverviewGizmos
{
    // Mirrored from settings by SMModuleDebugOptions.
    public static bool Show;

    private const float AnchorForward = 1.6f;
    private const float AnchorUp = 0.4f;
    private const float LabelBaseScale = 0.02f;

    private static readonly Color LabelColor = new Color(0.55f, 0.85f, 1f, 1f);

    private static readonly System.Text.StringBuilder _text = new System.Text.StringBuilder(160);
    private static int _labelId = -1;
    private static int _labelKey;
    private static string _labelText;
    private static bool _hooked;

    /// <summary>Per-frame entry point. <paramref name="scale"/> is the local avatar scale.</summary>
    public static void Tick(float scale)
    {
        EnsureMasterHook();

        if (!Show)
        {
            Shutdown();
            return;
        }
        if (scale <= 0f) scale = 1f;

        // Avatar + voice channels: sum over the remote player receivers.
        int avatarCount = 0;
        float avatarBps = 0f, avatarPps = 0f;
        float voiceBps = 0f, voicePps = 0f;
        BasisNetworkReceiver[] snapshot = BasisNetworkPlayers.ReceiversSnapshot;
        int count = BasisNetworkPlayers.ReceiverCount;
        for (int i = 0; i < count && i < snapshot.Length; i++)
        {
            BasisNetworkReceiver receiver = snapshot[i];
            if (receiver == null) continue;
            avatarCount++;
            avatarBps += receiver.BytesPerSecond;
            avatarPps += receiver.PacketsPerSecond;
            voiceBps += receiver.VoiceBytesPerSecond;
            voicePps += receiver.VoicePacketsPerSecond;
        }

        // Scene channel: sum over the remote synced objects (spawned items included —
        // they carry synced components).
        int sceneCount = 0;
        float sceneBps = 0f, scenePps = 0f;
        IReadOnlyList<BasisSyncedObject> remote = BasisSyncDriver.RemoteObjects;
        int remoteCount = remote.Count;
        for (int i = 0; i < remoteCount; i++)
        {
            BasisSyncedObject obj = remote[i];
            if (obj == null || !obj.TryGetSyncGizmoSample(out BasisSyncGizmoSample s)) continue;
            sceneCount++;
            sceneBps += s.BytesPerSecond;
            scenePps += s.PacketsPerSecond;
        }

        int key = BasisNetworkGizmoLabelCore.OverviewKey(avatarCount, avatarBps, avatarPps, voiceBps, voicePps, sceneCount, sceneBps, scenePps);
        if (_labelId <= 0 || key != _labelKey || _labelText == null)
        {
            _labelKey = key;
            _labelText = BuildText(avatarCount, avatarBps, avatarPps, voiceBps, voicePps, sceneCount, sceneBps, scenePps);
        }

        Vector3 camPos = BasisLocalCameraDriver.Position;
        Vector3 forward = BasisLocalCameraDriver.Forward();
        if (forward.sqrMagnitude < 1e-6f) forward = Vector3.forward;
        forward.Normalize();
        Vector3 anchor = camPos + forward * (AnchorForward * scale) + Vector3.up * (AnchorUp * scale);
        Quaternion rotation = BasisGizmoManager.BillboardRotation(anchor, camPos);

        if (_labelId <= 0)
        {
            BasisGizmoManager.CreateTextGizmo("NetworkOverview", out _labelId, anchor, _labelText, LabelColor);
        }
        BasisGizmoManager.UpdateTextGizmo(_labelId, anchor, rotation, LabelBaseScale * scale, _labelText, LabelColor);
    }

    public static void Shutdown()
    {
        if (_labelId > 0)
        {
            BasisGizmoManager.DestroyGizmo(_labelId);
        }
        _labelId = -1;
        _labelText = null;
    }

    private static string BuildText(int avatarCount, float avatarBps, float avatarPps, float voiceBps, float voicePps, int sceneCount, float sceneBps, float scenePps)
    {
        _text.Clear();
        _text.Append("Network RX\n");
        _text.Append("avatars ").Append(avatarCount).Append("  ").Append(FormatRate(avatarBps)).Append("  ").Append(Mathf.RoundToInt(avatarPps)).Append(" pkt/s\n");
        _text.Append("voice  ").Append(FormatRate(voiceBps)).Append("  ").Append(Mathf.RoundToInt(voicePps)).Append(" pkt/s\n");
        _text.Append("scene ").Append(sceneCount).Append("  ").Append(FormatRate(sceneBps)).Append("  ").Append(Mathf.RoundToInt(scenePps)).Append(" pkt/s");
        return _text.ToString();
    }

    private static string FormatRate(float bytesPerSecond)
    {
        if (bytesPerSecond >= 1024f) return (bytesPerSecond / 1024f).ToString("0.0") + " KB/s";
        return Mathf.RoundToInt(bytesPerSecond) + " B/s";
    }

    private static void EnsureMasterHook()
    {
        if (_hooked) return;
        BasisGizmoManager.OnUseGizmosChanged += OnMasterToggleChanged;
        _hooked = true;
    }

    // The master gizmo teardown wipes the manager's slots — forget the stale id so the
    // next Tick re-creates cleanly.
    private static void OnMasterToggleChanged(bool state)
    {
        if (!state)
        {
            _labelId = -1;
        }
    }
}
