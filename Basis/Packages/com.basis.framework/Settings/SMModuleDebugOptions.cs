using System.Collections.Generic;
using UnityEngine;
using Basis.BasisUI;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.TransformBinders.BoneControl;

public class SMModuleDebugOptions : BasisSettingsBase
{
    public static bool UseGizmos = false;
    public static bool UseTrackerGizmos = false;

    // --- Canonical setting keys (from defaults) ---
    private static string K_DEBUG_VISUALS => BasisSettingsDefaults.DebugVisuals.BindingKey;     // "debugvisuals"
    private static string K_TRACKER_GIZMOS => BasisSettingsDefaults.TrackerGizmos.BindingKey;   // "trackergizmos"

    // Tracker → sphere gizmo ID. Only role-assigned trackers get a gizmo so the
    // visualization mirrors what's actually driving a body part.
    private readonly Dictionary<BasisInput, int> _trackerGizmos = new Dictionary<BasisInput, int>();

    // Tracker → line gizmo ID, one segment from tracker pose to driven bone.
    private readonly Dictionary<BasisInput, int> _trackerLines = new Dictionary<BasisInput, int>();

    // Distinct from the rainbow bone gizmos so trackers stand out at a glance.
    private static readonly Color TrackerGizmoColor = new Color(0f, 1f, 1f, 1f);
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
        base.OnDestroy();
    }

    public override void ValidSettingsChange(string matchedSettingName, string optionValue)
    {
        if (matchedSettingName == K_DEBUG_VISUALS)
        {
            HandleDebugVisuals(optionValue);
            return;
        }

        if (matchedSettingName == K_TRACKER_GIZMOS)
        {
            HandleTrackerGizmos(optionValue);
        }
    }

    private void HandleDebugVisuals(string optionValue)
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
        }
    }

    private void Update()
    {
        if (!UseGizmos || !UseTrackerGizmos)
        {
            return;
        }

        BasisDeviceManagement manager = BasisDeviceManagement.Instance;
        if (manager == null)
        {
            return;
        }

        BasisObservableList<BasisInput> devices = manager.AllInputDevices;
        int count = devices.Count;

        float scale = BasisHeightDriver.ScaledToMatchValue;
        if (scale <= 0f)
        {
            scale = 1f;
        }
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

    private void ClearTrackerGizmos()
    {
        ClearMap(_trackerGizmos);
        ClearMap(_trackerLines);
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
