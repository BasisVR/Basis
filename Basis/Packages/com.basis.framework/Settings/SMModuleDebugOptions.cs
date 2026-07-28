using System.Collections.Generic;
using UnityEngine;
using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.Debugging;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Device_Management.Devices.Pairing;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;

public class SMModuleDebugOptions : BasisSettingsBase
{
    public static SMModuleDebugOptions Instance;

    public static bool UseGizmos = false;
    public static bool UseTrackerGizmos = false;
    public static bool UseLinkedTrackerLines = false;
    public static bool UseEyeGazeGizmo = false;
    public static bool UseIKColliders = false;
    public static bool UseHintOffsets = false;
    public static bool UseFootPlacement = false;
    public static bool UseInteractionHover = false;
    public static bool UseFingerTouchGizmo = false;
    public static bool UseSeatTargets = false;

    // Single shared switch for billboarded text labels on every gizmo system
    // (tracker roles, linked-pair ids, IK collider names, and the audio gizmos,
    // whose BasisAudioGizmos.ShowLabels mirror is set from the same handler).
    public static bool UseGizmoLabels = false;

    // Audio-debug gizmos (see BasisAudioGizmos). Off by default; the static bools
    // that gate the per-frame work live on BasisAudioGizmos itself.

    // Per-gizmo toggles. All default off; the derived UseGizmos gate
    // (RecomputeUseGizmos) stays off until the user enables at least one.
    public static bool UseSkeletonLines = false;
    public static bool UseCalibrationSpheres = false;
    public static bool UseJiggleVisuals = false;

    // --- Canonical setting keys (from defaults) ---
    private static string K_SHOW_GIZMOS => BasisSettingsDefaults.ShowGizmos.BindingKey;                       // "showgizmos"
    private static string K_GIZMO_SKELETON_LINES => BasisSettingsDefaults.GizmoSkeletonLines.BindingKey;       // "gizmoskeletonlines"
    private static string K_GIZMO_CALIB_SPHERES => BasisSettingsDefaults.GizmoCalibrationSpheres.BindingKey;   // "gizmocalibrationspheres"
    private static string K_GIZMO_JIGGLE_VISUALS => BasisSettingsDefaults.GizmoJiggleVisuals.BindingKey;       // "gizmojigglevisuals"
    private static string K_TRACKER_GIZMOS => BasisSettingsDefaults.TrackerGizmos.BindingKey;                  // "trackergizmos"
    private static string K_LINKED_TRACKER_LINES => BasisSettingsDefaults.LinkedTrackerLines.BindingKey;      // "linkedtrackerlines"
    private static string K_GIZMO_EYE_GAZE => BasisSettingsDefaults.GizmoEyeGaze.BindingKey;                  // "gizmoeyegaze"
    private static string K_GIZMO_IK_COLLIDERS => BasisSettingsDefaults.GizmoIKColliders.BindingKey;          // "gizmoikcolliders"
    private static string K_GIZMO_AUDIO_RANGES => BasisSettingsDefaults.GizmoAudioRanges.BindingKey;          // "gizmoaudioranges"
    private static string K_GIZMO_AUDIO_CONE => BasisSettingsDefaults.GizmoAudioListenerCone.BindingKey;      // "gizmoaudiolistenercone"
    private static string K_GIZMO_AUDIO_LEVELS => BasisSettingsDefaults.GizmoAudioLevels.BindingKey;          // "gizmoaudiolevels"
    private static string K_GIZMO_LABELS => BasisSettingsDefaults.GizmoLabels.BindingKey;                      // "gizmolabels"
    private static string K_GIZMO_NETWORK_SYNC => BasisSettingsDefaults.GizmoNetworkSync.BindingKey;            // "gizmonetworksync"
    private static string K_GIZMO_NETWORK_SYNC_BW => BasisSettingsDefaults.GizmoNetworkSyncBandwidth.BindingKey; // "gizmonetworksyncbandwidth"
    private static string K_GIZMO_NETWORK_PLAYERS => BasisSettingsDefaults.GizmoNetworkPlayers.BindingKey;        // "gizmonetworkplayers"
    private static string K_GIZMO_NETWORK_PLAYERS_BW => BasisSettingsDefaults.GizmoNetworkPlayersBandwidth.BindingKey; // "gizmonetworkplayersbandwidth"
    private static string K_GIZMO_NETWORK_ADDITIONAL => BasisSettingsDefaults.GizmoNetworkAdditionalInfo.BindingKey;    // "gizmonetworkadditionalinfo"
    private static string K_GIZMO_POINTER_RAY => BasisSettingsDefaults.GizmoPointerRay.BindingKey;
    private static string K_GIZMO_HINT_OFFSETS => BasisSettingsDefaults.GizmoHintOffsets.BindingKey;
    private static string K_GIZMO_FOOT_PLACEMENT => BasisSettingsDefaults.GizmoFootPlacement.BindingKey;
    private static string K_GIZMO_INTERACTION_HOVER => BasisSettingsDefaults.GizmoInteractionHover.BindingKey;
    private static string K_GIZMO_FINGER_TOUCH => BasisSettingsDefaults.GizmoFingerTouch.BindingKey;
    private static string K_GIZMO_SEAT_TARGETS => BasisSettingsDefaults.GizmoSeatTargets.BindingKey;

    // Tracker → sphere gizmo ID. Only role-assigned trackers get a gizmo so the
    // visualization mirrors what's actually driving a body part.
    private readonly Dictionary<BasisInput, int> _trackerGizmos = new Dictionary<BasisInput, int>();

    // Tracker → line gizmo ID, one segment from tracker pose to driven bone.
    private readonly Dictionary<BasisInput, int> _trackerLines = new Dictionary<BasisInput, int>();

    // Tracker → text-label gizmo ID (the tracker's role name). Gated by UseGizmoLabels.
    private readonly Dictionary<BasisInput, int> _trackerLabels = new Dictionary<BasisInput, int>();

    // Virtual midpoint → text-label gizmo ID (the merged pair identifier).
    private readonly Dictionary<BasisVirtualMidpointInput, int> _linkLabels = new Dictionary<BasisVirtualMidpointInput, int>();

    // Listener-camera position cached each frame so labels billboard toward the viewer.
    private static Vector3 _camPos;
    private const float LabelScale = 0.02f;

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
        Instance = this;
        // When the master gizmo system tears down, our cached IDs become stale —
        // the manager's destroy pass clears every entry in BasisGizmoManager.Gizmos.
        BasisGizmoManager.OnUseGizmosChanged += OnUseGizmosChanged;
    }

    public new void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
        }
        BasisGizmoManager.OnUseGizmosChanged -= OnUseGizmosChanged;
        ClearTrackerGizmos();
        ClearLinkLines();
        BasisAudioGizmos.Shutdown();
        BasisSyncGizmos.Shutdown();
        BasisPlayerNetworkGizmos.Shutdown();
        BasisNetworkOverviewGizmos.Shutdown();
        BasisPointerRayGizmos.Shutdown();
        BasisHintOffsetGizmos.Shutdown();
        base.OnDestroy();
    }

    public override void ValidSettingsChange(string matchedSettingName, string optionValue)
    {
        if (matchedSettingName == K_SHOW_GIZMOS)
        {
            // The master gizmo toggle was removed from the UI; rendering is now derived
            // from the individual gizmo toggles. Recompute in case a persisted legacy
            // value still fires this key.
            RecomputeUseGizmos();
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
            RecomputeUseGizmos();
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
            RecomputeUseGizmos();
            return;
        }

        if (matchedSettingName == K_GIZMO_JIGGLE_VISUALS)
        {
            bool.TryParse(optionValue, out UseJiggleVisuals);
            RecomputeUseGizmos();
            return;
        }

        if (matchedSettingName == K_TRACKER_GIZMOS)
        {
            HandleTrackerGizmos(optionValue);
            RecomputeUseGizmos();
            return;
        }

        if (matchedSettingName == K_LINKED_TRACKER_LINES)
        {
            HandleLinkedTrackerLines(optionValue);
            RecomputeUseGizmos();
            return;
        }

        if (matchedSettingName == K_GIZMO_EYE_GAZE)
        {
            if (bool.TryParse(optionValue, out UseEyeGazeGizmo) && !UseEyeGazeGizmo)
            {
                BasisEyeGazeGizmo.Shutdown();
            }
            RecomputeUseGizmos();
            return;
        }

        if (matchedSettingName == K_GIZMO_IK_COLLIDERS)
        {
            if (bool.TryParse(optionValue, out UseIKColliders) && !UseIKColliders)
            {
                BasisIKColliderGizmo.Shutdown();
            }
            RecomputeUseGizmos();
            return;
        }

        if (matchedSettingName == K_GIZMO_POINTER_RAY)
        {
            if (bool.TryParse(optionValue, out BasisPointerRayGizmos.Show) && !BasisPointerRayGizmos.Show)
            {
                BasisPointerRayGizmos.Shutdown();
            }
            RecomputeUseGizmos();
            return;
        }

        if (matchedSettingName == K_GIZMO_HINT_OFFSETS)
        {
            bool.TryParse(optionValue, out UseHintOffsets);
            RecomputeUseGizmos();
            return;
        }

        if (matchedSettingName == K_GIZMO_FOOT_PLACEMENT)
        {
            bool.TryParse(optionValue, out UseFootPlacement);
            RecomputeUseGizmos();
            return;
        }

        if (matchedSettingName == K_GIZMO_INTERACTION_HOVER)
        {
            bool.TryParse(optionValue, out UseInteractionHover);
            RecomputeUseGizmos();
            return;
        }

        if (matchedSettingName == K_GIZMO_FINGER_TOUCH)
        {
            if (bool.TryParse(optionValue, out UseFingerTouchGizmo) && !UseFingerTouchGizmo)
            {
                BasisDirectTouch.GizmoShutdown();
            }
            RecomputeUseGizmos();
            return;
        }

        if (matchedSettingName == K_GIZMO_SEAT_TARGETS)
        {
            bool.TryParse(optionValue, out UseSeatTargets);
            RecomputeUseGizmos();
            return;
        }

        if (matchedSettingName == K_GIZMO_AUDIO_RANGES)
        {
            bool.TryParse(optionValue, out BasisAudioGizmos.ShowRanges);
            RecomputeUseGizmos();
            return;
        }

        if (matchedSettingName == K_GIZMO_AUDIO_CONE)
        {
            bool.TryParse(optionValue, out BasisAudioGizmos.ShowListenerCone);
            RecomputeUseGizmos();
            return;
        }

        if (matchedSettingName == K_GIZMO_AUDIO_LEVELS)
        {
            bool.TryParse(optionValue, out BasisAudioGizmos.ShowLevels);
            RecomputeUseGizmos();
            return;
        }

        if (matchedSettingName == K_GIZMO_NETWORK_SYNC)
        {
            if (bool.TryParse(optionValue, out BasisSyncGizmos.Show) && !BasisSyncGizmos.Show && !BasisSyncGizmos.ShowBandwidth)
            {
                BasisSyncGizmos.Shutdown();
            }
            RecomputeUseGizmos();
            return;
        }

        if (matchedSettingName == K_GIZMO_NETWORK_SYNC_BW)
        {
            if (bool.TryParse(optionValue, out BasisSyncGizmos.ShowBandwidth) && !BasisSyncGizmos.ShowBandwidth && !BasisSyncGizmos.Show)
            {
                BasisSyncGizmos.Shutdown();
            }
            RecomputeUseGizmos();
            return;
        }

        if (matchedSettingName == K_GIZMO_NETWORK_PLAYERS)
        {
            if (bool.TryParse(optionValue, out BasisPlayerNetworkGizmos.Show) && !BasisPlayerNetworkGizmos.Show && !BasisPlayerNetworkGizmos.ShowBandwidth)
            {
                BasisPlayerNetworkGizmos.Shutdown();
            }
            RecomputeUseGizmos();
            return;
        }

        if (matchedSettingName == K_GIZMO_NETWORK_PLAYERS_BW)
        {
            if (bool.TryParse(optionValue, out BasisPlayerNetworkGizmos.ShowBandwidth) && !BasisPlayerNetworkGizmos.ShowBandwidth && !BasisPlayerNetworkGizmos.Show)
            {
                BasisPlayerNetworkGizmos.Shutdown();
            }
            RecomputeUseGizmos();
            return;
        }

        if (matchedSettingName == K_GIZMO_NETWORK_ADDITIONAL)
        {
            if (bool.TryParse(optionValue, out bool additional))
            {
                BasisPlayerNetworkGizmos.ShowAdditionalInfo = additional;
                BasisNetworkOverviewGizmos.Show = additional;
                if (!additional)
                {
                    BasisNetworkOverviewGizmos.Shutdown();
                }
            }
            RecomputeUseGizmos();
            return;
        }

        if (matchedSettingName == K_GIZMO_LABELS)
        {
            if (bool.TryParse(optionValue, out UseGizmoLabels))
            {
                BasisAudioGizmos.ShowLabels = UseGizmoLabels;
                BasisSyncGizmos.ShowLabels = UseGizmoLabels;
                BasisPlayerNetworkGizmos.ShowLabels = UseGizmoLabels;
                if (!UseGizmoLabels)
                {
                    ClearMap(_trackerLabels);
                    ClearLinkLabels();
                }
            }
        }
    }

    // Derived gizmo render gate: on whenever any individual gizmo toggle is enabled.
    // Replaces the old master ShowGizmos switch. Gizmo labels are intentionally
    // excluded — they only adorn other gizmos and can't render anything on their own.
    private void RecomputeUseGizmos()
    {
        bool anyOn =
            UseSkeletonLines ||
            UseCalibrationSpheres ||
            UseJiggleVisuals ||
            UseTrackerGizmos ||
            UseLinkedTrackerLines ||
            UseEyeGazeGizmo ||
            UseIKColliders ||
            BasisPointerRayGizmos.Show ||
            UseHintOffsets ||
            UseFootPlacement ||
            UseInteractionHover ||
            UseFingerTouchGizmo ||
            UseSeatTargets ||
            BasisAudioGizmos.ShowRanges ||
            BasisAudioGizmos.ShowListenerCone ||
            BasisAudioGizmos.ShowLevels ||
            BasisSyncGizmos.Show ||
            BasisSyncGizmos.ShowBandwidth ||
            BasisPlayerNetworkGizmos.Show ||
            BasisPlayerNetworkGizmos.ShowBandwidth ||
            BasisNetworkOverviewGizmos.Show;

        SetUseGizmos(anyOn);
    }

    private void SetUseGizmos(bool selected)
    {
#if UNITY_SERVER
        selected = false;
#endif

        if (UseGizmos == selected)
        {
            return;
        }

        UseGizmos = selected;

        BasisGizmoManager.OnUseGizmosChanged?.Invoke(UseGizmos);

        if (!UseGizmos)
        {
            BasisGizmoManager.DestroyAll();
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
        // Creation is handled lazily in the per-frame tick — that way new trackers picked up
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
        // Lines are created lazily in the per-frame tick so new pairings appearing mid-session
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
            _trackerLabels.Clear();
            _linkLines.Clear();
            _linkLabels.Clear();
        }
    }

    public static void Simulate()
    {
        if (BasisTrackerIdentifyGizmos.HasActive)
        {
            BasisTrackerIdentifyGizmos.Tick();
        }

        if (!UseGizmos)
        {
            return;
        }

        Instance?.SimulateGizmos();
    }

    private void SimulateGizmos()
    {
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

        _camPos = BasisLocalCameraDriver.Position;

        if (UseTrackerGizmos)
        {
            UpdateTrackerGizmos(devices, scale);
        }

        if (UseLinkedTrackerLines)
        {
            UpdateLinkLines(devices, scale);
        }

        if (UseIKColliders)
        {
            BasisLocalPlayer player = BasisLocalPlayer.Instance;
            bool ikReady = player != null && player.LocalRigDriver != null && player.LocalRigDriver.IKDataReady;
            BasisIKColliderGizmo.Tick(ikReady, ikReady ? player.LocalRigDriver.basisTransformMapping : null, ikReady ? player.LocalRigDriver.IKJob : default, UseGizmoLabels, _camPos);
        }

        BasisHintOffsetGizmos.Tick(UseHintOffsets, UseGizmoLabels, _camPos);
        BasisPlayerInteract.UpdateHoverGizmos(UseInteractionHover);
        BasisDirectTouch.UpdateGizmos(UseFingerTouchGizmo);
        BasisPointerRayGizmos.Tick(scale);

        BasisLocalPlayer localPlayer = BasisLocalPlayer.Instance;
        if (localPlayer != null)
        {
            localPlayer.BasisLocalFootDriver?.UpdateGizmos(UseFootPlacement, UseGizmoLabels, _camPos);
            localPlayer.LocalSeatDriver?.UpdateSeatGizmos(UseSeatTargets, UseGizmoLabels, _camPos);
        }

        BasisAudioGizmos.Tick(scale);
        BasisSyncGizmos.Tick(scale);
        BasisPlayerNetworkGizmos.Tick(scale);
        BasisNetworkOverviewGizmos.Tick(scale);
    }

    /// <summary>
    /// Lazily creates / updates a billboarded text label keyed by <paramref name="key"/>.
    /// Shared by tracker and link labels; the gizmo diffs text/colour internally so a
    /// steady label costs only the billboard transform write.
    /// </summary>
    private static void UpdateLabel<T>(Dictionary<T, int> map, T key, string gizmoName, string text, Vector3 position, Color color, float scale)
    {
        Quaternion rot = BasisGizmoManager.BillboardRotation(position, _camPos);
        if (!map.TryGetValue(key, out int id) || id <= 0)
        {
            if (!BasisGizmoManager.CreateTextGizmo(gizmoName, out id, position, text, color))
            {
                return;
            }
            map[key] = id;
        }
        BasisGizmoManager.UpdateTextGizmo(id, position, rot, LabelScale * scale, text, color);
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

            if (UseGizmoLabels)
            {
                string role = input.TryGetRole(out BasisBoneTrackedRole r) ? r.ToString() : "Tracker";
                Vector3 labelPos = trackerPos + Vector3.up * (TrackerGizmoBaseSize * 1.6f * scale);
                UpdateLabel(_trackerLabels, input, $"TrackerLabel_{role}", role, labelPos, TrackerGizmoColor, scale);
            }
            else if (_trackerLabels.TryGetValue(input, out int staleLabel))
            {
                BasisGizmoManager.DestroyGizmo(staleLabel);
                _trackerLabels.Remove(input);
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
        if (_trackerGizmos.Count > 0 || _trackerLines.Count > 0 || _trackerLabels.Count > 0)
        {
            PruneStale(_trackerGizmos, devices);
            PruneStale(_trackerLines, devices);
            PruneStale(_trackerLabels, devices);
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
                if (_linkLabels.TryGetValue(virt, out int orphanLabel))
                {
                    BasisGizmoManager.DestroyGizmo(orphanLabel);
                    _linkLabels.Remove(virt);
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

            if (UseGizmoLabels)
            {
                string role = virt.TryGetRole(out BasisBoneTrackedRole vr) ? vr.ToString() : "Pair";
                UpdateLabel(_linkLabels, virt, $"LinkLabel_{role}", $"Linked {role}", (aPos + bPos) * 0.5f, LinkedTrackerLineColor, scale);
            }
            else if (_linkLabels.TryGetValue(virt, out int staleLabel))
            {
                BasisGizmoManager.DestroyGizmo(staleLabel);
                _linkLabels.Remove(virt);
            }
        }

        if (_linkLines.Count > 0 || _linkLabels.Count > 0)
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
            if (_linkLabels.TryGetValue(virt, out int labelId))
            {
                BasisGizmoManager.DestroyGizmo(labelId);
                _linkLabels.Remove(virt);
            }
        }
    }

    private void ClearTrackerGizmos()
    {
        ClearMap(_trackerGizmos);
        ClearMap(_trackerLines);
        ClearMap(_trackerLabels);
    }

    private void ClearLinkLines()
    {
        if (_linkLines.Count > 0)
        {
            foreach (KeyValuePair<BasisVirtualMidpointInput, int> kvp in _linkLines)
            {
                BasisGizmoManager.DestroyGizmo(kvp.Value);
            }
            _linkLines.Clear();
        }
        ClearLinkLabels();
    }

    private void ClearLinkLabels()
    {
        if (_linkLabels.Count == 0)
        {
            return;
        }
        foreach (KeyValuePair<BasisVirtualMidpointInput, int> kvp in _linkLabels)
        {
            BasisGizmoManager.DestroyGizmo(kvp.Value);
        }
        _linkLabels.Clear();
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
