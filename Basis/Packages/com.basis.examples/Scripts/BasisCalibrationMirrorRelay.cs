using System.Collections.Generic;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using UnityEngine;

/// <summary>
/// Makes the calibration visuals visible inside the calibration cutout mirror.
///
/// The cutout mirror culls its reflection cameras to the LocalPlayerAvatar layer, so anything on
/// Default never appears in it. Two calibration visual systems live on Default:
/// 1. Tracker visuals — the "calibration balls" (FallbackSphere / real device models) BasisInput
///    spawns per tracked device, shown by VisibleTrackers during calibration. These are what
///    players line their pucks up with.
/// 2. BasisGizmoManager visuals — the lock-in proximity spheres and tracker link lines are
///    batched draws submitted on BasisGizmoManager.RenderLayer, and text labels are objects
///    under the runtime "Parent Of Debug Data" container.
///
/// Widening the reflection mask to Default would drag the entire opaque world into the see-through
/// cutout; instead this relayers ONLY those objects onto LocalPlayerAvatar (and points the gizmo
/// RenderLayer at it). The main camera renders that layer anyway (it's how you see your own body),
/// so nothing disappears from the world view. Lives on the mirror instance and restores every
/// touched object's original layer on teardown.
/// </summary>
[DisallowMultipleComponent]
public sealed class BasisCalibrationMirrorRelay : MonoBehaviour
{
    // Visuals can spawn at any time while the panel is up (VisibleTrackers, lock-in begin), so the
    // walk re-runs on a short cadence — a sub-100 ms adoption delay is imperceptible.
    private const int RelayerFrameInterval = 5;

    private int _avatarLayer = -1;
    private int _framesUntilRelayer;
    private readonly Dictionary<Transform, int> _relayered = new Dictionary<Transform, int>();
    private readonly List<Transform> _scratch = new List<Transform>(32);

    private void Awake()
    {
        _avatarLayer = LayerMask.NameToLayer("LocalPlayerAvatar");
    }

    private void LateUpdate()
    {
        if (_avatarLayer < 0) return;
        if (--_framesUntilRelayer > 0) return;
        _framesUntilRelayer = RelayerFrameInterval;

        BasisDeviceManagement manager = BasisDeviceManagement.Instance;
        if (manager != null)
        {
            var devices = manager.AllInputDevices;
            int count = devices.Count;
            for (int i = 0; i < count; i++)
            {
                BasisInput input = devices[i];
                if (input == null || input.BasisVisualTracker == null) continue;
                RelayerSubtree(input.BasisVisualTracker.transform, includeRoot: true);
            }
        }

        // Null until the first gizmo is created; re-read every pass so a re-created container
        // (destroyed on calibrate-complete, remade next session) is picked up automatically.
        GameObject gizmoParent = BasisGizmoManager.Parent;
        if (gizmoParent != null)
        {
            // Leave the empty container itself alone; relayer its children.
            RelayerSubtree(gizmoParent.transform, includeRoot: false);
        }

        BasisGizmoManager.RenderLayer = _avatarLayer;
    }

    /// <summary>Move a subtree onto LocalPlayerAvatar, remembering each object's original layer
    /// the first time it's touched.</summary>
    private void RelayerSubtree(Transform root, bool includeRoot)
    {
        _scratch.Clear();
        root.GetComponentsInChildren(true, _scratch); // includes the root itself
        int count = _scratch.Count;
        for (int i = 0; i < count; i++)
        {
            Transform child = _scratch[i];
            if (child == null || (!includeRoot && child == root)) continue;
            if (child.gameObject.layer == _avatarLayer) continue;

            if (!_relayered.ContainsKey(child)) _relayered[child] = child.gameObject.layer;
            child.gameObject.layer = _avatarLayer;
        }
    }

    private void OnDisable() => Restore();
    private void OnDestroy() => Restore();

    /// <summary>Put every moved object back on its original layer. Destroyed objects
    /// (Unity fake-null keys) are simply skipped — nothing to restore.</summary>
    private void Restore()
    {
        foreach (KeyValuePair<Transform, int> pair in _relayered)
        {
            Transform moved = pair.Key;
            if (moved != null) moved.gameObject.layer = pair.Value;
        }
        _relayered.Clear();
        if (BasisGizmoManager.RenderLayer == _avatarLayer) BasisGizmoManager.RenderLayer = 0;
    }
}
