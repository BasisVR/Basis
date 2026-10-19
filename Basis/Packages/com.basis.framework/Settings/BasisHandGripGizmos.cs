using Unity.Scripting.LifecycleManagement;
using System.Collections.Generic;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.Sync;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;

/// <summary>
/// Runtime visualisation of the canonical hand frame held objects weld to (<see cref="BasisHandGrip"/>),
/// driven each frame from <see cref="SMModuleDebugOptions"/> under the master gizmo gate.
///
/// Per hand, local and remote:
///  • a sphere on the palm — the point a grab actually seats an object onto,
///  • the frame's three axes, forward down the hand and up out the back of it, in the usual
///    red/green/blue so the orientation a grip is authored against is readable at a glance,
///  • a line from the wrist bone to the palm, which is exactly the offset the frame applies: welding
///    to the hand bone alone puts objects at the tail of that line, behind the hand,
///  • an optional label with the measured hand length (the unit networked grip offsets travel in) and
///    a WRIST marker when the avatar had no finger bones and the frame had to fall back to the bind
///    pose — the case where a remote copy cannot reproduce the holder's grip.
///
/// The local player draws warm and remotes draw cool, so a held object sitting right in your own hand
/// but wrong in someone else's is visible from one view. Every frame comes from the same
/// <see cref="BasisHandGrip"/> call the weld and the network path use, so the drawn frame can never sit
/// somewhere the real one does not.
///
/// While this is on it also draws the WELD of every held networked prop, from both ends of the hold
/// (<see cref="BasisPickupWeldDiagnostics"/>): a leg from the palm to the prop, labelled with the attach
/// id, the hand length the offset is scaled by, and — on the holder's own client — the error of rebuilding
/// the prop's pose from the values it just put on the wire. That is the one class of bug this whole path
/// cannot otherwise show you, because a holder never decodes what it sent: if the label on your screen and
/// the label on the observer's screen disagree about the id, the hand length, or whether the frame is
/// canonical, that disagreement IS the misplacement.
/// </summary>
[AutoStaticsCleanup]
public static partial class BasisHandGripGizmos
{
    // Mirrored from settings by SMModuleDebugOptions.
    public static bool Show;
    public static bool ShowLabels;

    // All sized off the hand itself, which already carries avatar scale.
    private const float PalmSizeRatio = 0.16f;
    private const float AxisWidthRatio = 0.035f;
    private const float WristWidthRatio = 0.05f;
    private const float LabelHeightRatio = 0.7f;
    private const float LabelBaseScale = 0.02f;

    private static readonly Color LocalColor = new Color(1f, 0.55f, 0.12f, 1f);
    private static readonly Color RemoteColor = new Color(0.35f, 0.75f, 1f, 1f);
    private static readonly Color AxisRight = new Color(0.95f, 0.3f, 0.3f, 1f);
    private static readonly Color AxisUp = new Color(0.35f, 0.9f, 0.4f, 1f);
    private static readonly Color AxisForward = new Color(0.35f, 0.6f, 1f, 1f);
    private static readonly Color DegradedColor = new Color(1f, 0.85f, 0.2f, 1f);

    private struct HandVisual
    {
        public int Palm;
        public int AxisX;
        public int AxisY;
        public int AxisZ;
        public int WristLine;
        public int Label;
    }

    private struct HandSample
    {
        public BasisHandFrame Frame;
        public bool Left;
        public bool Local;
    }

    private struct WeldVisual
    {
        public int Leg;
        public int Label;
    }

    /// <summary>A round trip this far off on the holder's own client means the prefab's channels are lossy.</summary>
    private const float SelfCheckTolerance = 0.002f;

    private static readonly List<HandVisual> _visuals = new List<HandVisual>();
    private static readonly List<HandSample> _samples = new List<HandSample>();
    private static readonly List<WeldVisual> _weldVisuals = new List<WeldVisual>();
    private static readonly List<KeyValuePair<ushort, BasisRemotePlayer>> _remotes = new List<KeyValuePair<ushort, BasisRemotePlayer>>();
    private static readonly System.Text.StringBuilder _text = new System.Text.StringBuilder(48);

    /// <summary>Per-frame entry point. <paramref name="scale"/> is the local avatar scale.</summary>
    public static void Tick(float scale)
    {
        if (!Show)
        {
            Shutdown();
            return;
        }
        if (scale <= 0f) scale = 1f;

        // Held props publish into this while it is on; drawn and cleared below, so the collection never
        // outlives a frame and costs nothing at all when the gizmo is off.
        BasisPickupWeldDiagnostics.Enabled = true;

        _samples.Clear();
        CollectLocal();
        CollectRemotes();

        Vector3 cameraPosition = BasisLocalCameraDriver.Position;

        while (_visuals.Count > _samples.Count)
        {
            Destroy(_visuals[_visuals.Count - 1]);
            _visuals.RemoveAt(_visuals.Count - 1);
        }

        for (int Index = 0; Index < _samples.Count; Index++)
        {
            if (Index >= _visuals.Count)
            {
                _visuals.Add(default);
            }
            _visuals[Index] = Draw(_visuals[Index], _samples[Index], cameraPosition, scale);
        }

        DrawWelds(cameraPosition, scale);
    }

    /// <summary>
    /// One leg per held prop, from the palm frame that end used to where the prop actually is, plus the
    /// numbers the two ends have to agree on. Anything the reconstruction could not do — an unresolved
    /// hand, a frame that fell back to the wrist, a lossy encode — draws in the alert colour.
    /// </summary>
    private static void DrawWelds(Vector3 cameraPosition, float scale)
    {
        IReadOnlyList<BasisPickupWeldReport> reports = BasisPickupWeldDiagnostics.Reports;

        while (_weldVisuals.Count > reports.Count)
        {
            DestroyWeld(_weldVisuals[_weldVisuals.Count - 1]);
            _weldVisuals.RemoveAt(_weldVisuals.Count - 1);
        }

        for (int Index = 0; Index < reports.Count; Index++)
        {
            if (Index >= _weldVisuals.Count)
            {
                _weldVisuals.Add(default);
            }
            _weldVisuals[Index] = DrawWeld(_weldVisuals[Index], reports[Index], cameraPosition, scale);
        }

        BasisPickupWeldDiagnostics.Clear();
    }

    private static WeldVisual DrawWeld(WeldVisual visual, BasisPickupWeldReport report, Vector3 cameraPosition, float scale)
    {
        float hand = report.HandLength > 0f ? report.HandLength : BasisHandGrip.FallbackHandLength;
        bool healthy = report.FrameResolved && report.Canonical && report.SelfCheckError <= SelfCheckTolerance;
        Color tint = !healthy ? DegradedColor : report.Owner ? LocalColor : RemoteColor;

        EnsureLine(ref visual.Leg, report.PalmPosition, report.PropPosition, hand * WristWidthRatio, tint);

        if (ShowLabels)
        {
            _text.Clear();
            _text.Append(report.Name);
            _text.Append(report.Owner ? "  held  " : "  seen  ");
            _text.Append(BasisPickupSyncNetworking.DescribeHandId(report.HandId));
            if (!report.FrameResolved)
            {
                _text.Append("  NO HAND");
            }
            else
            {
                if (!report.Canonical) _text.Append("  WRIST");
                _text.Append("  ");
                _text.Append((report.HandLength * 100f).ToString("0.0"));
                _text.Append("cm  x");
                _text.Append(report.OffsetHandLengths.ToString("0.00"));
                if (report.Owner && report.SelfCheckError > SelfCheckTolerance)
                {
                    _text.Append("  ENCODE ");
                    _text.Append((report.SelfCheckError * 100f).ToString("0.0"));
                    _text.Append("cm/");
                    _text.Append(report.SelfCheckAngle.ToString("0.0"));
                    _text.Append("deg");
                }
            }
            string label = _text.ToString();

            Vector3 labelPosition = Vector3.Lerp(report.PalmPosition, report.PropPosition, 0.5f)
                                    + Vector3.up * (hand * LabelHeightRatio * 0.5f);
            if (visual.Label <= 0)
            {
                BasisGizmoManager.CreateTextGizmo("PickupWeldLabel", out visual.Label, labelPosition, label, tint);
            }
            else
            {
                Quaternion rotation = BasisGizmoManager.BillboardRotation(labelPosition, cameraPosition);
                BasisGizmoManager.UpdateTextGizmo(visual.Label, labelPosition, rotation, LabelBaseScale * scale, label, tint);
            }
        }
        else if (visual.Label > 0)
        {
            BasisGizmoManager.DestroyGizmo(visual.Label);
            visual.Label = 0;
        }

        return visual;
    }

    private static void CollectLocal()
    {
        if (BasisLocalPlayer.Instance == null)
        {
            return;
        }
        Add(BasisLocalBoneDriver.LeftHandControl, left: true);
        Add(BasisLocalBoneDriver.RightHandControl, left: false);

        static void Add(BasisLocalBoneControl bone, bool left)
        {
            if (BasisHandGrip.TryGetLocalFrame(bone, left, out BasisHandFrame frame))
            {
                _samples.Add(new HandSample { Frame = frame, Left = left, Local = true });
            }
        }
    }

    /// <summary>
    /// Remote hands come off each remote's own avatar driver mapping — the same source the receive side
    /// of a networked hold reconstructs against. Ordered by player id so a hand keeps its gizmo slot
    /// between frames instead of swapping labels with another player's.
    /// </summary>
    private static void CollectRemotes()
    {
        _remotes.Clear();
        foreach (KeyValuePair<ushort, BasisRemotePlayer> pair in BasisNetworkPlayers.RemotePlayers)
        {
            if (pair.Value != null && !pair.Value.IsDestroyed)
            {
                _remotes.Add(pair);
            }
        }
        _remotes.Sort(static (a, b) => a.Key.CompareTo(b.Key));

        for (int Index = 0; Index < _remotes.Count; Index++)
        {
            BasisRemotePlayer remote = _remotes[Index].Value;
            if (BasisHandGrip.TryGetPlayerFrame(remote, true, out BasisHandFrame leftFrame))
            {
                _samples.Add(new HandSample { Frame = leftFrame, Left = true, Local = false });
            }
            if (BasisHandGrip.TryGetPlayerFrame(remote, false, out BasisHandFrame rightFrame))
            {
                _samples.Add(new HandSample { Frame = rightFrame, Left = false, Local = false });
            }
        }
    }

    private static HandVisual Draw(HandVisual visual, HandSample sample, Vector3 cameraPosition, float scale)
    {
        BasisHandFrame frame = sample.Frame;
        float hand = frame.HandLength > 0f ? frame.HandLength : BasisHandGrip.FallbackHandLength;
        Color tint = sample.Local ? LocalColor : RemoteColor;

        EnsureSphere(ref visual.Palm, frame.Position, hand * PalmSizeRatio, frame.Canonical ? tint : DegradedColor);

        float axisWidth = hand * AxisWidthRatio;
        EnsureLine(ref visual.AxisX, frame.Position, frame.Position + frame.Rotation * Vector3.right * hand, axisWidth, AxisRight);
        EnsureLine(ref visual.AxisY, frame.Position, frame.Position + frame.Rotation * Vector3.up * hand, axisWidth, AxisUp);
        EnsureLine(ref visual.AxisZ, frame.Position, frame.Position + frame.Rotation * Vector3.forward * hand, axisWidth, AxisForward);

        // The wrist-to-palm leg. A weld straight onto the hand bone lands objects at its far end.
        EnsureLine(ref visual.WristLine, frame.WristPosition, frame.Position, hand * WristWidthRatio, tint);

        if (ShowLabels)
        {
            _text.Clear();
            _text.Append(sample.Left ? 'L' : 'R');
            _text.Append(sample.Local ? " you  " : " remote  ");
            _text.Append((frame.HandLength * 100f).ToString("0.0"));
            _text.Append("cm");
            if (!frame.Canonical)
            {
                _text.Append("  WRIST");
            }
            string label = _text.ToString();

            Vector3 labelPosition = frame.Position + Vector3.up * (hand * LabelHeightRatio);
            Color labelColor = frame.Canonical ? tint : DegradedColor;
            if (visual.Label <= 0)
            {
                BasisGizmoManager.CreateTextGizmo("HandGripLabel", out visual.Label, labelPosition, label, labelColor);
            }
            else
            {
                Quaternion rotation = BasisGizmoManager.BillboardRotation(labelPosition, cameraPosition);
                BasisGizmoManager.UpdateTextGizmo(visual.Label, labelPosition, rotation, LabelBaseScale * scale, label, labelColor);
            }
        }
        else if (visual.Label > 0)
        {
            BasisGizmoManager.DestroyGizmo(visual.Label);
            visual.Label = 0;
        }

        return visual;
    }

    private static void EnsureSphere(ref int id, Vector3 position, float size, Color color)
    {
        if (id <= 0)
        {
            if (BasisGizmoManager.CreateSphereGizmo("HandGrip", out int created, position, size, color))
            {
                id = created;
            }
            return;
        }
        BasisGizmoManager.UpdateSphereGizmo(id, position, Vector3.one * size);
        BasisGizmoManager.UpdateGizmoColor(id, color);
    }

    private static void EnsureLine(ref int id, Vector3 start, Vector3 end, float width, Color color)
    {
        if (id <= 0)
        {
            if (BasisGizmoManager.CreateLineGizmo("HandGripLine", out int created, start, end, width, color))
            {
                id = created;
            }
            return;
        }
        BasisGizmoManager.UpdateLineGizmo(id, start, end);
        BasisGizmoManager.UpdateGizmoColor(id, color);
    }

    public static void Shutdown()
    {
        int count = _visuals.Count;
        for (int Index = 0; Index < count; Index++)
        {
            Destroy(_visuals[Index]);
        }
        _visuals.Clear();
        _samples.Clear();
        _remotes.Clear();

        for (int Index = 0; Index < _weldVisuals.Count; Index++)
        {
            DestroyWeld(_weldVisuals[Index]);
        }
        _weldVisuals.Clear();
        BasisPickupWeldDiagnostics.Enabled = false;
        BasisPickupWeldDiagnostics.Clear();
    }

    private static void DestroyWeld(WeldVisual visual)
    {
        if (visual.Leg > 0) BasisGizmoManager.DestroyGizmo(visual.Leg);
        if (visual.Label > 0) BasisGizmoManager.DestroyGizmo(visual.Label);
    }

    private static void Destroy(HandVisual visual)
    {
        if (visual.Palm > 0) BasisGizmoManager.DestroyGizmo(visual.Palm);
        if (visual.AxisX > 0) BasisGizmoManager.DestroyGizmo(visual.AxisX);
        if (visual.AxisY > 0) BasisGizmoManager.DestroyGizmo(visual.AxisY);
        if (visual.AxisZ > 0) BasisGizmoManager.DestroyGizmo(visual.AxisZ);
        if (visual.WristLine > 0) BasisGizmoManager.DestroyGizmo(visual.WristLine);
        if (visual.Label > 0) BasisGizmoManager.DestroyGizmo(visual.Label);
    }
}
