using Unity.Scripting.LifecycleManagement;
using System.Collections.Generic;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using UnityEngine;

/// <summary>
/// Runtime visualisation of jiggle grabbing, driven each frame from
/// <see cref="SMModuleDebugOptions"/> under the master gizmo gate so it shows live in VR and
/// desktop rather than only in the editor scene view.
///
/// Per live grab:
///  • a sphere on the grabbed bone and one on the pull target, joined by a line,
///  • a wire ring showing that point's reach allowance, so an authored Max Grab Stretch that is
///    too tight to feel is visible rather than guessed at,
///  • an optional label naming the bone and who is pulling it.
///
/// Local grabs draw warm, other players' grabs draw cool, so at a glance you can tell your own
/// pull from someone else's on the same avatar.
///
/// It also draws each of the local player's hands' pick spheres — the volume a grab press
/// searches — which is the fastest way to see why a reach missed, and scales with the avatar the
/// same way the real search does.
/// </summary>
[AutoStaticsCleanup]
public static partial class BasisJiggleGrabGizmos
{
    // Mirrored from settings by SMModuleDebugOptions.
    public static bool Show;
    public static bool ShowLabels;

    private const float BoneBaseSize = 0.03f;
    private const float TargetBaseSize = 0.045f;
    private const float LineBaseWidth = 0.005f;
    private const float LabelBaseScale = 0.02f;
    private const float LabelBaseHeight = 0.09f;
    private const int LimitRingSegments = 24;
    // Relative to the pick radius, so the outline stays thin at any avatar scale.
    private const float ReachRingWidth = 0.02f;

    private static readonly Color LocalGrabColor = new Color(1f, 0.55f, 0.12f, 1f);
    private static readonly Color RemoteGrabColor = new Color(0.35f, 0.75f, 1f, 1f);
    private static readonly Color LimitColor = new Color(0.95f, 0.35f, 0.35f, 0.9f);
    private static readonly Color ReachColor = new Color(0.45f, 0.95f, 0.55f, 1f);

    private struct GrabVisual
    {
        public int BoneSphere;
        public int TargetSphere;
        public int Line;
        public int LimitRing;
        public int Label;
        public string LabelText;
    }

    private static readonly List<GrabVisual> _visuals = new List<GrabVisual>();
    // Three line gizmos per hand — the rings of the wire sphere, kept flat in one list.
    private static readonly List<int> _reachSpheres = new List<int>();
    [NoAutoStaticsCleanup] private static readonly Vector3[] _ringPoints = new Vector3[LimitRingSegments + 1];
    private static readonly System.Text.StringBuilder _text = new System.Text.StringBuilder(64);

    /// <summary>Per-frame entry point. <paramref name="scale"/> is the local avatar scale.</summary>
    public static void Tick(float scale)
    {
        BasisJiggleGrabDriver.CollectGizmoSamples = Show;
        if (!Show)
        {
            Shutdown();
            return;
        }
        if (scale <= 0f) scale = 1f;

        Vector3 cameraPosition = BasisLocalCameraDriver.Position;
        IReadOnlyList<BasisJiggleGrabDriver.GrabGizmoSample> samples = BasisJiggleGrabDriver.GizmoSamples;
        int count = samples.Count;

        while (_visuals.Count > count)
        {
            Destroy(_visuals[_visuals.Count - 1]);
            _visuals.RemoveAt(_visuals.Count - 1);
        }

        for (int Index = 0; Index < count; Index++)
        {
            BasisJiggleGrabDriver.GrabGizmoSample sample = samples[Index];
            Color color = sample.IsLocalGrab ? LocalGrabColor : RemoteGrabColor;

            if (Index >= _visuals.Count)
            {
                _visuals.Add(default);
            }
            GrabVisual visual = _visuals[Index];

            EnsureSphere(ref visual.BoneSphere, sample.BonePosition, BoneBaseSize * scale, color);
            EnsureSphere(ref visual.TargetSphere, sample.TargetPosition, TargetBaseSize * scale, color);
            EnsureLine(ref visual.Line, sample.BonePosition, sample.TargetPosition, LineBaseWidth * scale, color);

            if (sample.MaxStretch > 0f)
            {
                BuildRing(sample.BonePosition, sample.MaxStretch, cameraPosition);
                if (visual.LimitRing <= 0)
                {
                    BasisGizmoManager.CreateLineGizmo("JiggleGrabLimit", out visual.LimitRing, _ringPoints,
                        LineBaseWidth * scale * 0.6f, LimitColor);
                }
                else
                {
                    BasisGizmoManager.UpdateLineGizmo(visual.LimitRing, _ringPoints);
                }
            }
            else if (visual.LimitRing > 0)
            {
                BasisGizmoManager.DestroyGizmo(visual.LimitRing);
                visual.LimitRing = 0;
            }

            if (ShowLabels)
            {
                _text.Clear();
                _text.Append(sample.BoneName);
                _text.Append(sample.IsLocalGrab ? "  (you)" : "  (#");
                if (!sample.IsLocalGrab)
                {
                    _text.Append(sample.GrabberId);
                    _text.Append(')');
                }
                string label = _text.ToString();
                Vector3 labelPosition = sample.TargetPosition + Vector3.up * (LabelBaseHeight * scale);
                Quaternion rotation = BasisGizmoManager.BillboardRotation(labelPosition, cameraPosition);
                if (visual.Label <= 0)
                {
                    BasisGizmoManager.CreateTextGizmo("JiggleGrabLabel", out visual.Label, labelPosition, label, color);
                    visual.LabelText = label;
                }
                else
                {
                    BasisGizmoManager.UpdateTextGizmo(visual.Label, labelPosition, rotation, LabelBaseScale * scale, label, color);
                    visual.LabelText = label;
                }
            }
            else if (visual.Label > 0)
            {
                BasisGizmoManager.DestroyGizmo(visual.Label);
                visual.Label = 0;
            }

            _visuals[Index] = visual;
        }

        UpdateReachSpheres(scale);
    }

    /// <summary>
    /// One sphere per local hand at the position a grab press actually searches from, sized to the
    /// real pick radius — a reach that visibly fails to touch a chain explains itself.
    /// </summary>
    private static void UpdateReachSpheres(float scale)
    {
        int used = 0;
        if (!BasisDeviceManagement.IsUserInDesktop())
        {
            float radius = BasisPlayerInteract.AvatarScaledRange(BasisJiggleGrabDriver.GrabSearchRadius);
            used += DrawReach(0, radius, used);
            used += DrawReach(1, radius, used);
        }

        while (_reachSpheres.Count > used)
        {
            BasisGizmoManager.DestroyGizmo(_reachSpheres[_reachSpheres.Count - 1]);
            _reachSpheres.RemoveAt(_reachSpheres.Count - 1);
        }
    }

    /// <summary>
    /// Asks the driver for the exact position a grab press searches from, rather than recomputing
    /// it — the first version of this gizmo derived the hand pose itself and drew the sphere at the
    /// wrist while claiming to show the pick volume.
    ///
    /// Drawn as three orthogonal rings rather than a sphere: a solid ball the size of the pick
    /// radius swallows the hand it is meant to describe, and the whole point is to watch the hand
    /// approach a chain. Returns how many gizmo slots it used.
    /// </summary>
    private static int DrawReach(byte hand, float radius, int slot)
    {
        if (!BasisJiggleGrabDriver.TryGetHandSearchPosition(hand, out Vector3 position))
        {
            return 0;
        }

        DrawReachRing(slot + 0, position, radius, Vector3.right, Vector3.up);
        DrawReachRing(slot + 1, position, radius, Vector3.right, Vector3.forward);
        DrawReachRing(slot + 2, position, radius, Vector3.up, Vector3.forward);
        return 3;
    }

    private static void DrawReachRing(int slot, Vector3 centre, float radius, Vector3 axisA, Vector3 axisB)
    {
        for (int Index = 0; Index <= LimitRingSegments; Index++)
        {
            float angle = Index / (float)LimitRingSegments * Mathf.PI * 2f;
            _ringPoints[Index] = centre + (axisA * Mathf.Cos(angle) + axisB * Mathf.Sin(angle)) * radius;
        }

        if (slot >= _reachSpheres.Count)
        {
            if (BasisGizmoManager.CreateLineGizmo("JiggleGrabReach", out int created, _ringPoints,
                    ReachRingWidth * radius, ReachColor))
            {
                _reachSpheres.Add(created);
            }
            return;
        }
        BasisGizmoManager.UpdateLineGizmo(_reachSpheres[slot], _ringPoints);
    }

    private static void BuildRing(Vector3 centre, float radius, Vector3 cameraPosition)
    {
        Vector3 forward = centre - cameraPosition;
        if (forward.sqrMagnitude < 0.0001f)
        {
            forward = Vector3.forward;
        }
        Quaternion facing = Quaternion.LookRotation(forward.normalized, Vector3.up);
        for (int Index = 0; Index <= LimitRingSegments; Index++)
        {
            float angle = Index / (float)LimitRingSegments * Mathf.PI * 2f;
            Vector3 offset = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0f) * radius;
            _ringPoints[Index] = centre + facing * offset;
        }
    }

    private static void EnsureSphere(ref int id, Vector3 position, float size, Color color)
    {
        if (id <= 0)
        {
            if (BasisGizmoManager.CreateSphereGizmo("JiggleGrab", out int created, position, size, color))
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
            if (BasisGizmoManager.CreateLineGizmo("JiggleGrabLine", out int created, start, end, width, color))
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
        BasisJiggleGrabDriver.CollectGizmoSamples = false;
        int count = _visuals.Count;
        for (int Index = 0; Index < count; Index++)
        {
            Destroy(_visuals[Index]);
        }
        _visuals.Clear();

        count = _reachSpheres.Count;
        for (int Index = 0; Index < count; Index++)
        {
            BasisGizmoManager.DestroyGizmo(_reachSpheres[Index]);
        }
        _reachSpheres.Clear();
    }

    private static void Destroy(GrabVisual visual)
    {
        if (visual.BoneSphere > 0) BasisGizmoManager.DestroyGizmo(visual.BoneSphere);
        if (visual.TargetSphere > 0) BasisGizmoManager.DestroyGizmo(visual.TargetSphere);
        if (visual.Line > 0) BasisGizmoManager.DestroyGizmo(visual.Line);
        if (visual.LimitRing > 0) BasisGizmoManager.DestroyGizmo(visual.LimitRing);
        if (visual.Label > 0) BasisGizmoManager.DestroyGizmo(visual.Label);
    }
}
