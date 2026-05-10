using System.Collections.Generic;
using UnityEngine;
using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Device_Management.Devices.Pairing;
using Basis.Scripts.TransformBinders.BoneControl;

public class SMModuleDebugOptions : BasisSettingsBase
{
    public static bool UseGizmos = false;
    public static bool UseTrackerGizmos = false;
    public static bool UseLinkedTrackerLines = false;
    public static bool UseEyeGazeGizmo = false;

    // Sub-toggles under ShowGizmos. Default true so the master switch alone restores
    // the pre-split behavior (lines + spheres + jiggle render) for users who never
    // touch the granular controls.
    public static bool UseSkeletonLines = true;
    public static bool UseCalibrationSpheres = true;
    public static bool UseJiggleVisuals = true;

    // --- Canonical setting keys (from defaults) ---
    private static string K_SHOW_GIZMOS => BasisSettingsDefaults.ShowGizmos.BindingKey;                       // "showgizmos"
    private static string K_GIZMO_SKELETON_LINES => BasisSettingsDefaults.GizmoSkeletonLines.BindingKey;       // "gizmoskeletonlines"
    private static string K_GIZMO_CALIB_SPHERES => BasisSettingsDefaults.GizmoCalibrationSpheres.BindingKey;   // "gizmocalibrationspheres"
    private static string K_GIZMO_JIGGLE_VISUALS => BasisSettingsDefaults.GizmoJiggleVisuals.BindingKey;       // "gizmojigglevisuals"
    private static string K_TRACKER_GIZMOS => BasisSettingsDefaults.TrackerGizmos.BindingKey;                  // "trackergizmos"
    private static string K_LINKED_TRACKER_LINES => BasisSettingsDefaults.LinkedTrackerLines.BindingKey;      // "linkedtrackerlines"
    private static string K_GIZMO_EYE_GAZE => BasisSettingsDefaults.GizmoEyeGaze.BindingKey;                  // "gizmoeyegaze"

    // Tracker → sphere gizmo ID. Only role-assigned trackers get a gizmo so the
    // visualization mirrors what's actually driving a body part.
    private readonly Dictionary<BasisInput, int> _trackerGizmos = new Dictionary<BasisInput, int>();

    // Tracker → line gizmo ID, one segment from tracker pose to driven bone.
    private readonly Dictionary<BasisInput, int> _trackerLines = new Dictionary<BasisInput, int>();

    // Virtual midpoint → line gizmo ID, one yellow segment from PartnerA to
    // PartnerB so the user can see at a glance which physical trackers are
    // currently merged into a virtual.
    private readonly Dictionary<BasisVirtualMidpointInput, int> _linkLines = new Dictionary<BasisVirtualMidpointInput, int>();

    // Distinct from the rainbow bone gizmos so trackers stand out at a glance.
    private static readonly Color TrackerGizmoColor = new Color(0f, 1f, 1f, 1f);
    // Yellow keeps the link line visually separate from the cyan tracker→bone
    // line, so when both toggles are on it's still obvious which is which.
    private static readonly Color LinkedTrackerLineColor = new Color(1f, 1f, 0f, 1f);
    private const float TrackerGizmoBaseSize = 0.04f;
    private const float TrackerLineBaseWidth = 0.005f;

    public override void Awake()
    {
        base.Awake();
        // When the master gizmo system tears down, our cached IDs become stale —
        // the manager's destroy pass clears every entry in BasisGizmoManager.Gizmos.
        BasisGizmoManager.OnUseGizmosChanged += OnUseGizmosChanged;
    }

    public new void OnDestroy()
    {
        BasisGizmoManager.OnUseGizmosChanged -= OnUseGizmosChanged;
        ClearTrackerGizmos();
        ClearLinkLines();
        base.OnDestroy();
    }

    public override void ValidSettingsChange(string matchedSettingName, string optionValue)
    {
        if (matchedSettingName == K_SHOW_GIZMOS)
        {
            HandleShowGizmos(optionValue);
            return;
        }

        if (matchedSettingName == K_GIZMO_SKELETON_LINES)
        {
            if (bool.TryParse(optionValue, out UseSkeletonLines))
            {
                BasisLocalPlayer player = BasisLocalPlayer.Instance;
                if (player != null && player.LocalBoneDriver != null)
                {
                    player.LocalBoneDriver.ApplySkeletonLineVisibility();
                }
            }
            return;
        }

        if (matchedSettingName == K_GIZMO_CALIB_SPHERES)
        {
            if (bool.TryParse(optionValue, out UseCalibrationSpheres))
            {
                // Flag alone only gates per-frame UpdateSphereGizmo calls — the
                // gizmo GameObjects need an explicit hide/show to actually
                // appear/disappear in the scene.
                BasisLocalPlayer player = BasisLocalPlayer.Instance;
                if (player != null && player.LocalBoneDriver != null)
                {
                    player.LocalBoneDriver.ApplyCalibrationSphereVisibility();
                }
            }
            return;
        }

        if (matchedSettingName == K_GIZMO_JIGGLE_VISUALS)
        {
            bool.TryParse(optionValue, out UseJiggleVisuals);
            return;
        }

        if (matchedSettingName == K_TRACKER_GIZMOS)
        {
            HandleTrackerGizmos(optionValue);
            return;
        }

        if (matchedSettingName == K_LINKED_TRACKER_LINES)
        {
            HandleLinkedTrackerLines(optionValue);
            return;
        }

        if (matchedSettingName == K_GIZMO_EYE_GAZE)
        {
            if (bool.TryParse(optionValue, out UseEyeGazeGizmo) && !UseEyeGazeGizmo)
            {
                BasisEyeGazeGizmo.Shutdown();
            }
        }
    }

    private void HandleShowGizmos(string optionValue)
    {
        if (!bool.TryParse(optionValue, out bool selected))
        {
            return;
        }

#if UNITY_SERVER
        selected = false;
#endif

        if (UseGizmos == selected)
        {
            return;
        }

        UseGizmos = selected;
        BasisDebug.Log($"Gizmo State is {UseGizmos} {selected}");

        if (UseGizmos)
        {
            BasisGizmoManager.TryCreateParent();
        }

        BasisGizmoManager.OnUseGizmosChanged?.Invoke(UseGizmos);

        if (!UseGizmos)
        {
            BasisGizmoManager.DestroyParent();

            foreach (BasisGizmos gizmo in BasisGizmoManager.Gizmos.Values)
            {
                if (gizmo != null)
                {
                    GameObject.Destroy(gizmo.gameObject);
                }
            }

            foreach (BasisLineGizmos lineGizmo in BasisGizmoManager.GizmosLine.Values)
            {
                if (lineGizmo != null)
                {
                    GameObject.Destroy(lineGizmo.gameObject);
                }
            }

            BasisGizmoManager.Gizmos.Clear();
            BasisGizmoManager.GizmosLine.Clear();
        }
    }

    private void HandleTrackerGizmos(string optionValue)
    {
        if (!bool.TryParse(optionValue, out bool selected))
        {
            return;
        }

#if UNITY_SERVER
        selected = false;
#endif

        if (UseTrackerGizmos == selected)
        {
            return;
        }

        UseTrackerGizmos = selected;
        if (!UseTrackerGizmos)
        {
            ClearTrackerGizmos();
        }
        // Creation is handled lazily in Update — that way new trackers picked up
        // mid-session also get a gizmo without extra plumbing.
    }

    private void HandleLinkedTrackerLines(string optionValue)
    {
        if (!bool.TryParse(optionValue, out bool selected))
        {
            return;
        }

#if UNITY_SERVER
        selected = false;
#endif

        if (UseLinkedTrackerLines == selected)
        {
            return;
        }

        UseLinkedTrackerLines = selected;
        if (!UseLinkedTrackerLines)
        {
            ClearLinkLines();
        }
        // Lines are created lazily in Update so new pairings appearing mid-session
        // are picked up automatically.
    }

    public override void ChangedSettings()
    {
    }

    private void OnUseGizmosChanged(bool state)
    {
        // Master toggle going off blows away the parent + gizmo dictionaries —
        // forget our IDs so we re-create cleanly when it comes back on.
        if (!state)
        {
            _trackerGizmos.Clear();
            _trackerLines.Clear();
            _linkLines.Clear();
        }
    }

    private void Update()
    {
        if (!UseGizmos)
        {
            return;
        }

        BasisDeviceManagement manager = BasisDeviceManagement.Instance;
        if (manager == null)
        {
            return;
        }

        BasisObservableList<BasisInput> devices = manager.AllInputDevices;

        float scale = BasisHeightDriver.ScaledToMatchValue;
        if (scale <= 0f)
        {
            scale = 1f;
        }

        if (UseTrackerGizmos)
        {
            UpdateTrackerGizmos(devices, scale);
        }

        if (UseLinkedTrackerLines)
        {
            UpdateLinkLines(devices, scale);
        }
    }

    private void UpdateTrackerGizmos(BasisObservableList<BasisInput> devices, float scale)
    {
        int count = devices.Count;
        Vector3 size = Vector3.one * (TrackerGizmoBaseSize * scale);

        for (int i = 0; i < count; i++)
        {
            BasisInput input = devices[i];
            if (input == null || !input.hasRoleAssigned)
            {
                continue;
            }

            Vector3 trackerPos = input.transform.position;

            if (!_trackerGizmos.TryGetValue(input, out int sphereId))
            {
                if (TryCreateTrackerGizmo(input, size, out sphereId))
                {
                    _trackerGizmos[input] = sphereId;
                }
            }
            else
            {
                BasisGizmoManager.UpdateSphereGizmo(sphereId, trackerPos, size);
            }

            if (!input.HasControl || input.Control == null)
            {
                // No driven bone — drop any line we previously had for this tracker.
                if (_trackerLines.TryGetValue(input, out int orphanLineId))
                {
                    BasisGizmoManager.DestroyGizmo(orphanLineId);
                    _trackerLines.Remove(input);
                }
                continue;
            }

            Vector3 bonePos = input.Control.OutgoingWorldData.position;
            if (!_trackerLines.TryGetValue(input, out int lineId))
            {
                if (TryCreateTrackerLine(input, trackerPos, bonePos, scale, out lineId))
                {
                    _trackerLines[input] = lineId;
                }
            }
            else
            {
                BasisGizmoManager.UpdateLineGizmo(lineId, trackerPos, bonePos);
            }
        }

        // Drop entries whose tracker disappeared or got unassigned this frame.
        if (_trackerGizmos.Count > 0 || _trackerLines.Count > 0)
        {
            PruneStale(_trackerGizmos, devices);
            PruneStale(_trackerLines, devices);
        }
    }

    private void UpdateLinkLines(BasisObservableList<BasisInput> devices, float scale)
    {
        int count = devices.Count;
        for (int i = 0; i < count; i++)
        {
            if (devices[i] is not BasisVirtualMidpointInput virt)
            {
                continue;
            }
            if (virt.PartnerA == null || virt.PartnerB == null)
            {
                // Mid-teardown — drop any stale line for this virtual.
                if (_linkLines.TryGetValue(virt, out int orphanId))
                {
                    BasisGizmoManager.DestroyGizmo(orphanId);
                    _linkLines.Remove(virt);
                }
                continue;
            }

            Vector3 aPos = virt.PartnerA.transform.position;
            Vector3 bPos = virt.PartnerB.transform.position;

            if (!_linkLines.TryGetValue(virt, out int lineId))
            {
                if (TryCreateLinkLine(virt, aPos, bPos, scale, out lineId))
                {
                    _linkLines[virt] = lineId;
                }
            }
            else
            {
                BasisGizmoManager.UpdateLineGizmo(lineId, aPos, bPos);
            }
        }

        if (_linkLines.Count > 0)
        {
            PruneStaleLinkLines(devices);
        }
    }

    private static void PruneStale(Dictionary<BasisInput, int> map, BasisObservableList<BasisInput> devices)
    {
        if (map.Count == 0)
        {
            return;
        }

        List<BasisInput> stale = null;
        foreach (KeyValuePair<BasisInput, int> kvp in map)
        {
            BasisInput tracker = kvp.Key;
            if (tracker == null || !tracker.hasRoleAssigned || !devices.Contains(tracker))
            {
                if (stale == null)
                {
                    stale = new List<BasisInput>();
                }
                stale.Add(tracker);
            }
        }

        if (stale == null)
        {
            return;
        }

        for (int i = 0; i < stale.Count; i++)
        {
            BasisInput tracker = stale[i];
            if (map.TryGetValue(tracker, out int id))
            {
                BasisGizmoManager.DestroyGizmo(id);
                map.Remove(tracker);
            }
        }
    }

    private static bool TryCreateTrackerGizmo(BasisInput input, Vector3 size, out int id)
    {
        string label = input.TryGetRole(out BasisBoneTrackedRole role) ? role.ToString() : "Tracker";
        bool created = BasisGizmoManager.CreateSphereGizmo($"Tracker_{label}", out id, input.transform.position, size.x, TrackerGizmoColor);
        if (created)
        {
            BasisGizmoManager.UpdateSphereGizmo(id, input.transform.position, size);
        }
        return created;
    }

    private static bool TryCreateTrackerLine(BasisInput input, Vector3 trackerPos, Vector3 bonePos, float scale, out int id)
    {
        string label = input.TryGetRole(out BasisBoneTrackedRole role) ? role.ToString() : "Tracker";
        return BasisGizmoManager.CreateLineGizmo($"TrackerLink_{label}", out id, trackerPos, bonePos, TrackerLineBaseWidth * scale, TrackerGizmoColor);
    }

    private static bool TryCreateLinkLine(BasisVirtualMidpointInput virt, Vector3 aPos, Vector3 bPos, float scale, out int id)
    {
        string label = virt.UniqueDeviceIdentifier ?? "pair";
        return BasisGizmoManager.CreateLineGizmo($"PairLink_{label}", out id, aPos, bPos, TrackerLineBaseWidth * scale, LinkedTrackerLineColor);
    }

    private void PruneStaleLinkLines(BasisObservableList<BasisInput> devices)
    {
        List<BasisVirtualMidpointInput> stale = null;
        foreach (KeyValuePair<BasisVirtualMidpointInput, int> kvp in _linkLines)
        {
            BasisVirtualMidpointInput virt = kvp.Key;
            // The pairing service removes the virtual from AllInputDevices and
            // calls Teardown (which clears PartnerA/PartnerB) before destroying
            // the GameObject — either condition means our line is orphaned.
            if (virt == null || virt.PartnerA == null || virt.PartnerB == null || !devices.Contains(virt))
            {
                (stale ??= new List<BasisVirtualMidpointInput>()).Add(virt);
            }
        }

        if (stale == null)
        {
            return;
        }

        for (int i = 0; i < stale.Count; i++)
        {
            BasisVirtualMidpointInput virt = stale[i];
            if (_linkLines.TryGetValue(virt, out int id))
            {
                BasisGizmoManager.DestroyGizmo(id);
                _linkLines.Remove(virt);
            }
        }
    }

    private void ClearTrackerGizmos()
    {
        ClearMap(_trackerGizmos);
        ClearMap(_trackerLines);
    }

    private void ClearLinkLines()
    {
        if (_linkLines.Count == 0)
        {
            return;
        }
        foreach (KeyValuePair<BasisVirtualMidpointInput, int> kvp in _linkLines)
        {
            BasisGizmoManager.DestroyGizmo(kvp.Value);
        }
        _linkLines.Clear();
    }

    private static void ClearMap(Dictionary<BasisInput, int> map)
    {
        if (map.Count == 0)
        {
            return;
        }

        foreach (KeyValuePair<BasisInput, int> kvp in map)
        {
            BasisGizmoManager.DestroyGizmo(kvp.Value);
        }
        map.Clear();
    }
}
