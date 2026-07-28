using System.Collections.Generic;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking.Sync;
using UnityEngine;

/// <summary>
/// Runtime visualisation of the generic value-sync interpolation pipeline
/// (<see cref="BasisSyncedObject"/> / <see cref="BasisSyncedTransform"/> and the other synced
/// components). Driven each frame from <see cref="SMModuleDebugOptions"/> under the master gizmo
/// gate, so it shows live in VR/desktop rather than only the editor scene view.
///
/// Per remote, interpolated object:
///  • a line from the last received keyframe (from) to the target keyframe (to),
///  • a sphere riding that line at the live interpolated position,
///  • a red overshoot segment past 'to' whenever the buffer ran dry and it is extrapolating,
///  • a small marker at 'to', and an optional billboarded label (#id, interp %, buffer depth).
///  • with the Sync Bandwidth toggle, each object's received bytes-on-wire/sec and packet rate.
///
/// The sphere/label colour tracks jitter-buffer health: green while the buffer sits at its
/// target depth, amber as it starves, red while extrapolating — so a cluster of red spheres
/// reads at a glance as "the data flow can't keep up".
///
/// Locally-owned objects have no receive pipeline, so they show a blue transmit marker
/// (anchor sphere + "#id own" label with tx rates) instead — without it the client that
/// grabbed the pickup or drives the vehicle sees nothing and reads that as "sync broken".
/// </summary>
public static class BasisSyncGizmos
{
    // Mirrored from settings by SMModuleDebugOptions.
    public static bool Show;
    public static bool ShowLabels;
    public static bool ShowBandwidth;

    private const float PathBaseWidth = 0.006f;
    private const float SphereBaseSize = 0.05f;
    private const float TargetBaseSize = 0.028f;
    private const float LabelBaseScale = 0.02f;
    private const float LabelBaseHeight = 0.12f;

    private static readonly Color PathColor = new Color(0.30f, 0.65f, 1f, 1f);
    private static readonly Color TargetColor = new Color(0.70f, 0.80f, 0.95f, 1f);
    private static readonly Color ExtrapColor = new Color(1f, 0.28f, 0.22f, 1f);
    private static readonly Color HealthGood = new Color(0.30f, 0.90f, 0.35f, 1f);
    private static readonly Color HealthWarn = new Color(0.95f, 0.80f, 0.20f, 1f);
    private static readonly Color OwnedColor = new Color(0.35f, 0.70f, 1f, 1f);

    private struct SyncGizmo
    {
        public int PathLine;
        public int ExtrapLine;
        public int PosSphere;
        public int TargetSphere;
        public int Label;
        public int LabelKey;
        public string LabelText;
        public bool Owned;
    }

    private static readonly Dictionary<BasisSyncedObject, SyncGizmo> _objects = new Dictionary<BasisSyncedObject, SyncGizmo>();
    private static readonly HashSet<BasisSyncedObject> _seen = new HashSet<BasisSyncedObject>();
    private static readonly List<BasisSyncedObject> _stale = new List<BasisSyncedObject>();
    private static readonly System.Text.StringBuilder _text = new System.Text.StringBuilder(64);
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

        float width = PathBaseWidth * scale;
        float sphereSize = SphereBaseSize * scale;
        float targetSize = TargetBaseSize * scale;

        IReadOnlyList<BasisSyncedObject> remote = BasisSyncDriver.RemoteObjects;
        int count = remote.Count;
        for (int i = 0; i < count; i++)
        {
            BasisSyncedObject obj = remote[i];
            if (obj == null || !obj.TryGetSyncGizmoSample(out BasisSyncGizmoSample s)) continue;
            TickObject(obj, s, false, scale, width, sphereSize, targetSize);
        }

        foreach (BasisSyncedObject obj in BasisSyncDriver.OwnedObjects)
        {
            if (obj == null || !obj.TryGetOwnedSyncGizmoSample(out BasisSyncGizmoSample s)) continue;
            TickObject(obj, s, true, scale, width, sphereSize, targetSize);
        }

        Prune();
    }

    private static void TickObject(BasisSyncedObject obj, in BasisSyncGizmoSample s, bool owned, float scale, float width, float sphereSize, float targetSize)
    {
        _seen.Add(obj);
        _objects.TryGetValue(obj, out SyncGizmo g);

        // Ownership flipped since this entry was built — rebuild so the label text and
        // colors don't carry the other mode's content under an unchanged change-key.
        if (g.Owned != owned)
        {
            Destroy(g);
            g = default;
            g.Owned = owned;
        }

        Color health = owned ? OwnedColor : HealthColor(s);
        bool spatial = !owned && Show && s.HasSpatial;

        if (spatial)
        {
            // The job lerps unclamped, so beyond t=1 this rides past 'to' — the visible overshoot.
            Vector3 live = Vector3.LerpUnclamped(s.FromWorld, s.ToWorld, s.InterpT);

            if (g.PathLine <= 0)
            {
                BasisGizmoManager.CreateLineGizmo($"Sync_Path_{s.NetworkID}", out g.PathLine, s.FromWorld, s.ToWorld, width, PathColor);
                BasisGizmoManager.CreateSphereGizmo($"Sync_Pos_{s.NetworkID}", out g.PosSphere, live, sphereSize, health);
                BasisGizmoManager.CreateSphereGizmo($"Sync_To_{s.NetworkID}", out g.TargetSphere, s.ToWorld, targetSize, TargetColor);
            }
            else
            {
                BasisGizmoManager.UpdateLineGizmo(g.PathLine, s.FromWorld, s.ToWorld);
                BasisGizmoManager.UpdateSphereGizmo(g.PosSphere, live, Vector3.one * sphereSize);
                BasisGizmoManager.UpdateGizmoColor(g.PosSphere, health);
                BasisGizmoManager.UpdateSphereGizmo(g.TargetSphere, s.ToWorld, Vector3.one * targetSize);
            }

            if (s.Extrapolating)
            {
                if (g.ExtrapLine <= 0)
                    BasisGizmoManager.CreateLineGizmo($"Sync_Extrap_{s.NetworkID}", out g.ExtrapLine, s.ToWorld, live, width, ExtrapColor);
                else
                    BasisGizmoManager.UpdateLineGizmo(g.ExtrapLine, s.ToWorld, live);
            }
            else
            {
                DestroyId(ref g.ExtrapLine);
            }
        }
        else
        {
            // Owned, Show off (bandwidth-only) or no spatial channel: a single anchor sphere.
            DestroyId(ref g.PathLine);
            DestroyId(ref g.ExtrapLine);
            DestroyId(ref g.TargetSphere);

            if (g.PosSphere <= 0)
            {
                BasisGizmoManager.CreateSphereGizmo($"Sync_State_{s.NetworkID}", out g.PosSphere, s.AnchorWorld, sphereSize, health);
            }
            else
            {
                BasisGizmoManager.UpdateSphereGizmo(g.PosSphere, s.AnchorWorld, Vector3.one * sphereSize);
                BasisGizmoManager.UpdateGizmoColor(g.PosSphere, health);
            }
        }

        bool showState = Show && ShowLabels;
        if (showState || ShowBandwidth)
        {
            if (g.Label <= 0 || BasisGizmoManager.IsTextVisible(g.Label))
            {
                int key = LabelKey(s, owned, showState, ShowBandwidth);
                if (g.Label <= 0 || key != g.LabelKey || g.LabelText == null)
                {
                    g.LabelKey = key;
                    g.LabelText = BuildLabel(s, owned, showState, ShowBandwidth);
                }
            }
            Vector3 labelPos = (spatial ? s.ToWorld : s.AnchorWorld) + Vector3.up * (LabelBaseHeight * scale);
            Quaternion rot = BasisGizmoManager.BillboardRotation(labelPos, _camPos);
            if (g.Label <= 0)
            {
                BasisGizmoManager.CreateTextGizmo($"Sync_Label_{s.NetworkID}", out g.Label, labelPos, g.LabelText, health);
            }
            BasisGizmoManager.UpdateTextGizmo(g.Label, labelPos, rot, LabelBaseScale * scale, g.LabelText, health);
        }
        else
        {
            DestroyId(ref g.Label);
        }

        _objects[obj] = g;
    }

    public static void Shutdown()
    {
        if (_objects.Count == 0) return;
        foreach (KeyValuePair<BasisSyncedObject, SyncGizmo> kvp in _objects)
        {
            Destroy(kvp.Value);
        }
        _objects.Clear();
    }

    private static Color HealthColor(in BasisSyncGizmoSample s)
    {
        if (s.Extrapolating) return ExtrapColor;
        float desired = s.DesiredDepth > 1f ? s.DesiredDepth : 1f;
        return Color.Lerp(HealthWarn, HealthGood, Mathf.Clamp01(s.BufferDepth / desired));
    }

    // Cheap change-key so the label string only rebuilds when something visible moves.
    // Bucketing lives in BasisNetworkGizmoLabelCore (with its unit tests) — deliberately
    // coarse because the interp fraction cycles 0→1 every keyframe, so a fine key would
    // dirty every label every frame and TMP re-tessellation dominates the gizmo cost.
    private static int LabelKey(in BasisSyncGizmoSample s, bool owned, bool showState, bool showBw)
    {
        // Owned labels carry no interpolation state (there is none on the sender), so the key
        // only tracks the tx-rate buckets; the high bit keeps an owned/remote pair distinct.
        if (owned)
        {
            return BasisNetworkGizmoLabelCore.SyncLabelKey(s.NetworkID, 0f, 0, false, s.BytesPerSecond, s.PacketsPerSecond, false, showBw) ^ (1 << 30);
        }
        return BasisNetworkGizmoLabelCore.SyncLabelKey(s.NetworkID, s.InterpT, s.BufferDepth, s.Extrapolating, s.BytesPerSecond, s.PacketsPerSecond, showState, showBw);
    }

    private static string BuildLabel(in BasisSyncGizmoSample s, bool owned, bool showState, bool showBw)
    {
        _text.Clear();
        _text.Append('#').Append(s.NetworkID);
        if (owned) _text.Append(" own");
        if (showState && !owned)
        {
            _text.Append("\nt ").Append(Mathf.RoundToInt(Mathf.Clamp01(s.InterpT) * 100f)).Append("%  buf ").Append(s.BufferDepth);
            if (s.Extrapolating) _text.Append("  EXTRAP");
        }
        if (showBw)
        {
            _text.Append('\n');
            if (owned) _text.Append("tx ");
            _text.Append(FormatRate(s.BytesPerSecond)).Append("  ").Append(Mathf.RoundToInt(s.PacketsPerSecond)).Append(" pkt/s");
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

    private static void Destroy(SyncGizmo g)
    {
        if (g.PathLine > 0) BasisGizmoManager.DestroyGizmo(g.PathLine);
        if (g.ExtrapLine > 0) BasisGizmoManager.DestroyGizmo(g.ExtrapLine);
        if (g.PosSphere > 0) BasisGizmoManager.DestroyGizmo(g.PosSphere);
        if (g.TargetSphere > 0) BasisGizmoManager.DestroyGizmo(g.TargetSphere);
        if (g.Label > 0) BasisGizmoManager.DestroyGizmo(g.Label);
    }

    private static void Prune()
    {
        if (_objects.Count == _seen.Count) return;
        _stale.Clear();
        foreach (KeyValuePair<BasisSyncedObject, SyncGizmo> kvp in _objects)
        {
            if (!_seen.Contains(kvp.Key)) _stale.Add(kvp.Key);
        }
        for (int i = 0; i < _stale.Count; i++)
        {
            if (_objects.TryGetValue(_stale[i], out SyncGizmo g))
            {
                Destroy(g);
                _objects.Remove(_stale[i]);
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
        if (!state) _objects.Clear();
    }
}
