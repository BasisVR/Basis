using System.Collections.Generic;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.Receivers;
using UnityEngine;

/// <summary>
/// Per-remote-player network visualisation — the player-pose analogue of <see cref="BasisSyncGizmos"/>.
/// Driven each frame from <see cref="SMModuleDebugOptions"/> under the master gizmo gate.
///
/// Each remote player whose pose buffer is interpolating gets:
///  • a line from the last received hips pose (from) to the target hips pose (to),
///  • a sphere riding that line at the live interpolated position,
///  • a small marker at 'to', and an optional billboarded label (interp %, buffer depth, rate).
///
/// Colour tracks playback health off the receiver's adaptive rate: green at rate 1.0, red as it
/// slows to avoid an underrun (starving), amber as it speeds up to clear a backlog. The Player
/// Bandwidth toggle adds received bytes-on-wire/sec and packet rate per player. Player poses are
/// clamped to the target on the wire — there is no extrapolation overshoot like the value-sync gizmos.
/// </summary>
public static class BasisPlayerNetworkGizmos
{
    // Mirrored from settings by SMModuleDebugOptions.
    public static bool Show;
    public static bool ShowLabels;
    public static bool ShowBandwidth;
    public static bool ShowAdditionalInfo;

    private const float PathBaseWidth = 0.01f;
    private const float SphereBaseSize = 0.09f;
    private const float TargetBaseSize = 0.05f;
    private const float LabelBaseScale = 0.02f;
    private const float LabelBaseHeight = 1.0f;   // above the hips anchor, roughly head height

    // Playback-rate band, mirrors BasisNetworkReceiver Min/MaxPlaybackRate.
    private const float MinPlaybackRate = 0.85f;
    private const float MaxPlaybackRate = 1.35f;

    private static readonly Color PathColor = new Color(0.55f, 0.50f, 1f, 1f);
    private static readonly Color TargetColor = new Color(0.80f, 0.78f, 0.98f, 1f);
    private static readonly Color HealthGood = new Color(0.30f, 0.90f, 0.35f, 1f);   // rate ~1.0
    private static readonly Color HealthSlow = new Color(1f, 0.28f, 0.22f, 1f);      // rate < 1 (starving)
    private static readonly Color HealthFast = new Color(0.95f, 0.80f, 0.20f, 1f);   // rate > 1 (backlog)

    private struct PlayerGizmo
    {
        public int PathLine;
        public int PosSphere;
        public int TargetSphere;
        public int Label;
        public int LabelKey;
        public string LabelText;
    }

    private static readonly Dictionary<ushort, PlayerGizmo> _players = new Dictionary<ushort, PlayerGizmo>();
    private static readonly HashSet<ushort> _seen = new HashSet<ushort>();
    private static readonly List<ushort> _stale = new List<ushort>();
    private static readonly System.Text.StringBuilder _text = new System.Text.StringBuilder(96);
    private static Vector3 _camPos;
    private static bool _hooked;

    /// <summary>Per-frame entry point. <paramref name="scale"/> is the local avatar scale.</summary>
    public static void Tick(float scale)
    {
        EnsureMasterHook();

        if (!Show && !ShowBandwidth)
        {
            Shutdown();
            return;
        }
        if (scale <= 0f) scale = 1f;

        _camPos = BasisLocalCameraDriver.Position;
        _seen.Clear();

        BasisNetworkReceiver[] snapshot = BasisNetworkPlayers.ReceiversSnapshot;
        int count = BasisNetworkPlayers.ReceiverCount;
        float width = PathBaseWidth * scale;
        float sphereSize = SphereBaseSize * scale;
        float targetSize = TargetBaseSize * scale;

        for (int i = 0; i < count && i < snapshot.Length; i++)
        {
            BasisNetworkReceiver receiver = snapshot[i];
            if (receiver == null || !receiver.HasBufferHolds) continue;
            var cur = receiver.Current;
            var nxt = receiver.Next;
            if (cur == null || nxt == null) continue;

            ushort id = receiver.playerId;
            _seen.Add(id);
            _players.TryGetValue(id, out PlayerGizmo g);

            Vector3 from = cur.Position;
            Vector3 to = nxt.Position;
            float t = Mathf.Clamp01((float)receiver.InterpolationTimeDebug);
            Vector3 live = Vector3.Lerp(from, to, t);
            Color health = HealthColor(receiver.LastPlaybackRate);

            if (Show)
            {
                if (g.PathLine <= 0)
                {
                    BasisGizmoManager.CreateLineGizmo($"Player_Path_{id}", out g.PathLine, from, to, width, PathColor);
                    BasisGizmoManager.CreateSphereGizmo($"Player_Pos_{id}", out g.PosSphere, live, sphereSize, health);
                    BasisGizmoManager.CreateSphereGizmo($"Player_To_{id}", out g.TargetSphere, to, targetSize, TargetColor);
                }
                else
                {
                    BasisGizmoManager.UpdateLineGizmo(g.PathLine, from, to);
                    BasisGizmoManager.UpdateSphereGizmo(g.PosSphere, live, Vector3.one * sphereSize);
                    BasisGizmoManager.UpdateGizmoColor(g.PosSphere, health);
                    BasisGizmoManager.UpdateSphereGizmo(g.TargetSphere, to, Vector3.one * targetSize);
                }
            }
            else
            {
                // Bandwidth-only: a single sphere at the live position, no path.
                DestroyId(ref g.PathLine);
                DestroyId(ref g.TargetSphere);
                if (g.PosSphere <= 0)
                {
                    BasisGizmoManager.CreateSphereGizmo($"Player_State_{id}", out g.PosSphere, live, sphereSize, health);
                }
                else
                {
                    BasisGizmoManager.UpdateSphereGizmo(g.PosSphere, live, Vector3.one * sphereSize);
                    BasisGizmoManager.UpdateGizmoColor(g.PosSphere, health);
                }
            }

            bool showState = Show && ShowLabels;
            if (showState || ShowBandwidth)
            {
                if (g.Label <= 0 || BasisGizmoManager.IsTextVisible(g.Label))
                {
                    int key = LabelKey(receiver, showState, ShowBandwidth);
                    if (g.Label <= 0 || key != g.LabelKey || g.LabelText == null)
                    {
                        g.LabelKey = key;
                        g.LabelText = BuildLabel(receiver, showState, ShowBandwidth);
                    }
                }
                Vector3 labelPos = to + Vector3.up * (LabelBaseHeight * scale);
                Quaternion rot = BasisGizmoManager.BillboardRotation(labelPos, _camPos);
                if (g.Label <= 0)
                {
                    BasisGizmoManager.CreateTextGizmo($"Player_Label_{id}", out g.Label, labelPos, g.LabelText, health);
                }
                BasisGizmoManager.UpdateTextGizmo(g.Label, labelPos, rot, LabelBaseScale * scale, g.LabelText, health);
            }
            else
            {
                DestroyId(ref g.Label);
            }

            _players[id] = g;
        }

        Prune();
    }

    public static void Shutdown()
    {
        if (_players.Count == 0) return;
        foreach (KeyValuePair<ushort, PlayerGizmo> kvp in _players)
        {
            Destroy(kvp.Value);
        }
        _players.Clear();
    }

    private static Color HealthColor(float rate)
    {
        if (rate < 1f) return Color.Lerp(HealthSlow, HealthGood, Mathf.InverseLerp(MinPlaybackRate, 1f, rate));
        return Color.Lerp(HealthGood, HealthFast, Mathf.InverseLerp(1f, MaxPlaybackRate, rate));
    }

    // Cheap change-key so the label string only rebuilds when something visible moves.
    // Bucketing lives in BasisNetworkGizmoLabelCore (with its unit tests) — deliberately
    // coarse because the interp fraction cycles 0→1 every keyframe, so a fine key would
    // dirty every label every frame and TMP re-tessellation dominates the gizmo cost.
    private static int LabelKey(BasisNetworkReceiver r, bool showState, bool showBw)
    {
        return BasisNetworkGizmoLabelCore.PlayerLabelKey(r.playerId, (float)r.InterpolationTimeDebug, r.LastPlaybackRate, r.StagedCount, r.BytesPerSecond, r.PacketsPerSecond, showState, showBw,
            r.VoiceBytesPerSecond, r.VoicePacketsPerSecond, ShowAdditionalInfo);
    }

    private static string BuildLabel(BasisNetworkReceiver r, bool showState, bool showBw)
    {
        _text.Clear();
        _text.Append('#').Append(r.playerId);

        if (showState)
        {
            _text.Append("\nt ").Append(Mathf.RoundToInt(Mathf.Clamp01((float)r.InterpolationTimeDebug) * 100f))
                 .Append("%  buf ").Append(r.StagedCount);
            float rate = r.LastPlaybackRate;
            if (rate < 0.98f || rate > 1.02f) _text.Append("  x").Append(rate.ToString("0.00"));
        }
        if (showBw)
        {
            _text.Append('\n').Append(FormatRate(r.BytesPerSecond)).Append("  ").Append(Mathf.RoundToInt(r.PacketsPerSecond)).Append(" pkt/s");
        }
        if (ShowAdditionalInfo)
        {
            _text.Append("\nvoice ").Append(FormatRate(r.VoiceBytesPerSecond)).Append("  ").Append(Mathf.RoundToInt(r.VoicePacketsPerSecond)).Append(" pkt/s");
        }
        return _text.ToString();
    }

    private static string FormatRate(float bytesPerSecond)
    {
        if (bytesPerSecond >= 1024f) return (bytesPerSecond / 1024f).ToString("0.0") + " KB/s";
        return Mathf.RoundToInt(bytesPerSecond) + " B/s";
    }

    private static void DestroyId(ref int id)
    {
        if (id > 0)
        {
            BasisGizmoManager.DestroyGizmo(id);
            id = -1;
        }
    }

    private static void Destroy(PlayerGizmo g)
    {
        if (g.PathLine > 0) BasisGizmoManager.DestroyGizmo(g.PathLine);
        if (g.PosSphere > 0) BasisGizmoManager.DestroyGizmo(g.PosSphere);
        if (g.TargetSphere > 0) BasisGizmoManager.DestroyGizmo(g.TargetSphere);
        if (g.Label > 0) BasisGizmoManager.DestroyGizmo(g.Label);
    }

    private static void Prune()
    {
        if (_players.Count == _seen.Count) return;
        _stale.Clear();
        foreach (KeyValuePair<ushort, PlayerGizmo> kvp in _players)
        {
            if (!_seen.Contains(kvp.Key)) _stale.Add(kvp.Key);
        }
        for (int i = 0; i < _stale.Count; i++)
        {
            if (_players.TryGetValue(_stale[i], out PlayerGizmo g))
            {
                Destroy(g);
                _players.Remove(_stale[i]);
            }
        }
    }

    private static void EnsureMasterHook()
    {
        if (_hooked) return;
        BasisGizmoManager.OnUseGizmosChanged += OnMasterToggleChanged;
        _hooked = true;
    }

    // The master gizmo toggle going off destroys BasisGizmoManager's dictionaries, so our cached
    // IDs are stale — forget them (don't destroy) so the next Tick re-creates cleanly.
    private static void OnMasterToggleChanged(bool state)
    {
        if (!state) _players.Clear();
    }
}
