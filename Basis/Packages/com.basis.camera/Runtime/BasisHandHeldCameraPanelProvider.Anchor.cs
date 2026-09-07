using System.Collections.Generic;
using UnityEngine;
using CameraAnchorKind = BasisHandHeldCameraInteractable.CameraAnchorKind;
using CameraPinSpace = BasisHandHeldCameraInteractable.CameraPinSpace;

namespace Basis.BasisUI.HandHeldCamera
{
    /// <summary>
    /// Panel surface for the camera's anchor: which frame it sits in, and what carries it.
    ///
    /// <para>The anchor sits above the modifier slots because it is the frame they solve in. A
    /// position modifier decides where the camera goes relative to its subject; the anchor decides
    /// what the whole shot is bolted to, which is the difference between a tripod on the dock and a
    /// tripod on the boat.</para>
    /// </summary>
    public partial class BasisHandHeldCameraPanelProvider
    {
        /// <summary>
        /// One entry per <see cref="CameraPinSpace"/>, in enum order — the dropdown reads its
        /// selection back as an index into this.
        /// </summary>
        private static readonly string[] AnchorSpaceKeys =
        {
            "camera.anchor.hand", "camera.anchor.playspace", "camera.anchor.world", "camera.anchor.attached",
        };

        private PanelSectionToggle _anchorSection;
        private PanelElementDescriptor _anchorGroup;
        private PanelDropdown _anchorDropdown;
        private PanelDropdown _anchorTargetDropdown;
        private PanelToggle _anchorFollowsBodyToggle;
        private PanelElementDescriptor _anchorStatus;

        /// <summary>
        /// Net ids behind the target rows, offset by the leading entries the list always carries.
        /// Held rather than parsed back out of the entry, the way the follow roster does it.
        /// </summary>
        private readonly List<ushort> _anchorTargetIds = new List<ushort>();

        /// <summary>
        /// Whether the target dropdown has ever been given entries. The first build is never
        /// optional: an empty roster and an empty list agree that nothing moved, and the dropdown
        /// would keep the placeholder rows its prefab shipped with — which is what a solo instance
        /// looks like, and clicking one of those rows is the crash this flag exists to stop.
        /// </summary>
        private bool _anchorTargetsBuilt;

        private string _lastAnchorStatusText;
        private string _lastAnchorObjectLabel;
        private CameraPinSpace _lastAnchorSpace = (CameraPinSpace)(-1);

        private const string AnchorTargetNoneKey = "camera.anchorTarget.none";
        private const string AnchorTargetLocalKey = "camera.anchorTarget.local";
        private const string AnchorTargetObjectEntry = "object";

        private void BuildAnchorGroup(RectTransform parent)
        {
            _anchorSection = PanelSectionToggle.CreateNewEntry(parent);
            _anchorGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                _anchorSection, parent, BasisLocalization.Get("camera.anchor"), false);
            RectTransform content = _anchorGroup.ContentParent;

            _anchorDropdown = PanelDropdown.CreateNewEntry(content);
            _anchorDropdown.Descriptor.SetTitle(BasisLocalization.Get("camera.anchor"));
            _anchorDropdown.Descriptor.SetTooltip(BasisLocalization.Get("camera.anchor.description"));
            _anchorDropdown.AssignLocalizedEntries(
                new List<string>(AnchorSpaceKeys), new List<string>(AnchorSpaceKeys));
            _anchorDropdown.OnValueChanged = _ =>
            {
                int index = _anchorDropdown != null ? _anchorDropdown.Index : -1;
                if (_activeCamera == null || index < 0 || index >= AnchorSpaceKeys.Length) return;

                _activeCamera.SetAnchorSpace((CameraPinSpace)index);
                RefreshAnchorVisibility();
            };

            _anchorFollowsBodyToggle = PanelToggle.CreateNewEntry(content);
            _anchorFollowsBodyToggle.Descriptor.SetTitle(BasisLocalization.Get("camera.anchorFollowsBody"));
            _anchorFollowsBodyToggle.Descriptor.SetTooltip(
                BasisLocalization.Get("camera.anchorFollowsBody.description"));
            _anchorFollowsBodyToggle.OnValueChanged = v =>
            {
                if (_activeCamera != null) _activeCamera.anchorFollowsBody = v;
            };

            _anchorTargetDropdown = PanelDropdown.CreateNewEntry(content);
            _anchorTargetDropdown.Descriptor.SetTitle(BasisLocalization.Get("camera.anchorTarget"));
            _anchorTargetDropdown.Descriptor.SetTooltip(BasisLocalization.Get("camera.anchorTarget.description"));
            _anchorTargetDropdown.OnValueChanged = _ => OnAnchorTargetPicked();

            RectTransform pickRow = PanelElementDescriptor.BuildActionRow(content, "CameraAnchorPickRow");

            PanelButton surfaceButton = PanelButton.CreateNew(pickRow);
            surfaceButton.Descriptor.SetTitle(BasisLocalization.Get("camera.anchorToSurface"));
            surfaceButton.Descriptor.SetTooltip(BasisLocalization.Get("camera.anchorToSurface.description"));
            surfaceButton.OnClicked += () =>
            {
                _activeCamera?.TryAnchorToSurfaceBelow();
                RefreshAnchorVisibility();
            };

            PanelButton viewButton = PanelButton.CreateNew(pickRow);
            viewButton.Descriptor.SetTitle(BasisLocalization.Get("camera.anchorToView"));
            viewButton.Descriptor.SetTooltip(BasisLocalization.Get("camera.anchorToView.description"));
            viewButton.OnClicked += () =>
            {
                _activeCamera?.TryAnchorToViewTarget();
                RefreshAnchorVisibility();
            };

            // Under the controls it reports on, because what an anchor is actually riding is not
            // visible from any of them: a picked object is named nowhere else, and a target that
            // has gone away leaves every control reading exactly as it did while it worked.
            _anchorStatus = BuildRecordingStatusCard(content, "camera.anchor.status", "camera.anchor.status.none");

            RefreshAnchorTargets();
            RefreshAnchorVisibility();
        }

        /// <summary>
        /// Applies a target-dropdown selection. Row 0 is "nothing", which is a world anchor by
        /// another name; the object row is only ever present while an object is already anchored,
        /// so selecting it changes nothing rather than re-running a pick the user did not ask for.
        /// </summary>
        private void OnAnchorTargetPicked()
        {
            if (_activeCamera == null || _anchorTargetDropdown == null) return;

            List<string> entries = _anchorTargetDropdown.Entries;
            int index = _anchorTargetDropdown.Index;
            if (entries == null || index < 0 || index >= entries.Count) return;

            string entry = entries[index];
            if (entry == AnchorTargetObjectEntry) return;

            if (entry == AnchorTargetNoneKey)
            {
                _activeCamera.ClearAnchorTarget();
            }
            else if (entry == AnchorTargetLocalKey)
            {
                _activeCamera.SetAnchorToPlayer(0, false);
            }
            else
            {
                int row = index - LeadingAnchorRows(entries);
                if (row >= 0 && row < _anchorTargetIds.Count)
                {
                    _activeCamera.SetAnchorToPlayer(_anchorTargetIds[row], true);
                }
            }

            RefreshAnchorVisibility();
        }

        /// <summary>How many rows sit above the remotes: the object row when present, nothing, and Me.</summary>
        private static int LeadingAnchorRows(List<string> entries)
            => entries != null && entries.Count > 0 && entries[0] == AnchorTargetObjectEntry ? 3 : 2;

        /// <summary>
        /// Rebuilds the target list against the live roster. Same rules as the follow roster: never
        /// while the list is open, and the first build is unconditional.
        /// </summary>
        private void RefreshAnchorTargets()
        {
            if (_anchorTargetDropdown == null || _activeCamera == null) return;

            if (_anchorTargetDropdown.DropdownComponent != null &&
                _anchorTargetDropdown.DropdownComponent.IsExpanded) return;

            var remotes = Basis.Scripts.Networking.BasisNetworkPlayers.RemotePlayers;
            bool hasObject = _activeCamera.AnchorKind == CameraAnchorKind.Object;

            if (_anchorTargetsBuilt && !AnchorRosterChanged(remotes, hasObject)) return;
            _anchorTargetsBuilt = true;

            _anchorTargetIds.Clear();
            var entries = new List<string>();
            var labels = new List<string>();

            if (hasObject)
            {
                entries.Add(AnchorTargetObjectEntry);
                labels.Add(_activeCamera.AnchorLabel);
            }

            _lastAnchorObjectLabel = hasObject ? _activeCamera.AnchorLabel : null;

            entries.Add(AnchorTargetNoneKey);
            labels.Add(BasisLocalization.Get(AnchorTargetNoneKey));
            entries.Add(AnchorTargetLocalKey);
            labels.Add(BasisLocalization.Get(AnchorTargetLocalKey));

            foreach (var pair in remotes)
            {
                if (pair.Value == null) continue;
                _anchorTargetIds.Add(pair.Key);
            }

            // A ConcurrentDictionary enumerates in bucket order, which reshuffles as players come
            // and go. Net id is stable and is join order.
            _anchorTargetIds.Sort();

            for (int Index = 0; Index < _anchorTargetIds.Count; Index++)
            {
                ushort id = _anchorTargetIds[Index];
                entries.Add(id.ToString());
                labels.Add(remotes.TryGetValue(id, out var remote) && !string.IsNullOrEmpty(remote.SafeDisplayName)
                    ? remote.SafeDisplayName
                    : $"Player {id}");
            }

            _anchorTargetDropdown.AssignEntries(entries, labels);
            _anchorTargetDropdown.SetValueWithoutNotify(entries[SelectedAnchorRow(entries)]);
            ForceLayoutRebuild(_anchorGroup);
        }

        private int SelectedAnchorRow(List<string> entries)
        {
            switch (_activeCamera.AnchorKind)
            {
                case CameraAnchorKind.Object:
                    return 0;

                case CameraAnchorKind.Player:
                    if (!_activeCamera.AnchorPlayerIsRemote)
                    {
                        return entries.IndexOf(AnchorTargetLocalKey);
                    }

                    int row = _anchorTargetIds.IndexOf(_activeCamera.AnchorPlayerId);
                    return row >= 0 ? LeadingAnchorRows(entries) + row : entries.IndexOf(AnchorTargetNoneKey);

                default:
                    return entries.IndexOf(AnchorTargetNoneKey);
            }
        }

        private bool AnchorRosterChanged(
            System.Collections.Concurrent.ConcurrentDictionary<ushort, Basis.Scripts.BasisSdk.Players.BasisRemotePlayer> remotes,
            bool hasObject)
        {
            List<string> entries = _anchorTargetDropdown.Entries;
            if (entries == null) return true;

            // The object row carries a name that is not a key, so it has to be rebuilt when the
            // anchored object changes as well as when it appears or goes.
            bool listedObject = entries.Count > 0 && entries[0] == AnchorTargetObjectEntry;
            if (listedObject != hasObject) return true;
            if (hasObject && _lastAnchorObjectLabel != _activeCamera.AnchorLabel) return true;

            int live = 0;
            foreach (var pair in remotes)
            {
                if (pair.Value == null) continue;
                if (live >= _anchorTargetIds.Count || !remotes.ContainsKey(_anchorTargetIds[live])) return true;
                live++;
            }

            return live != _anchorTargetIds.Count;
        }

        /// <summary>
        /// Shows only the controls the selected anchor reads, and reports what it is riding.
        ///
        /// <para>Follows-body belongs to whichever anchor resolves the local player, which is the
        /// playspace anchor and an attached one pointed at yourself — the same setting either way,
        /// so it follows the anchor rather than being duplicated per anchor.</para>
        /// </summary>
        private void RefreshAnchorVisibility()
        {
            if (_activeCamera == null) return;

            CameraPinSpace space = _activeCamera.PinSpace;
            bool attached = space == CameraPinSpace.Attached;
            bool ridesLocalPlayer = space == CameraPinSpace.PlaySpace ||
                (attached && _activeCamera.AnchorKind == CameraAnchorKind.Player && !_activeCamera.AnchorPlayerIsRemote);

            _anchorFollowsBodyToggle?.gameObject.SetActive(ridesLocalPlayer);
            _anchorTargetDropdown?.gameObject.SetActive(attached);
            _anchorStatus?.gameObject.SetActive(attached);

            RefreshAnchorStatus();
            ForceLayoutRebuild(_anchorGroup);
        }

        private void RefreshAnchorStatus()
        {
            if (_anchorStatus == null || _activeCamera == null) return;

            string text;
            if (_activeCamera.AnchorTargetLost)
            {
                text = BasisLocalization.Get("camera.anchor.status.lost");
            }
            else
            {
                switch (_activeCamera.AnchorKind)
                {
                    case CameraAnchorKind.Object:
                        text = BasisLocalization.Get("camera.anchor.status.riding") + " " + _activeCamera.AnchorLabel;
                        break;

                    case CameraAnchorKind.Player:
                        text = BasisLocalization.Get("camera.anchor.status.riding") + " " +
                               (_activeCamera.AnchorPlayerIsRemote
                                   ? AnchorPlayerName(_activeCamera.AnchorPlayerId)
                                   : BasisLocalization.Get(AnchorTargetLocalKey));
                        break;

                    default:
                        text = BasisLocalization.Get("camera.anchor.status.none");
                        break;
                }
            }

            if (_lastAnchorStatusText == text) return;
            _lastAnchorStatusText = text;
            _anchorStatus.SetDescription(text);
        }

        private static string AnchorPlayerName(ushort netId)
            => Basis.Scripts.Networking.BasisNetworkPlayers.RemotePlayers.TryGetValue(netId, out var remote) &&
               remote != null && !string.IsNullOrEmpty(remote.SafeDisplayName)
                ? remote.SafeDisplayName
                : $"Player {netId}";

        /// <summary>
        /// Re-seeds the anchor dropdown when something else has moved it — a camera mode applied,
        /// flight armed, or an anchored object destroyed under the panel. Gated on the value
        /// actually moving, and refused while the list is open.
        /// </summary>
        private void TickAnchorSection()
        {
            if (_activeCamera == null) return;

            RefreshAnchorTargets();

            CameraPinSpace space = _activeCamera.PinSpace;
            if (_lastAnchorSpace != space)
            {
                _lastAnchorSpace = space;
                if (_anchorDropdown != null &&
                    (_anchorDropdown.DropdownComponent == null || !_anchorDropdown.DropdownComponent.IsExpanded))
                {
                    _anchorDropdown.SetValueWithoutNotify(AnchorSpaceKeys[(int)space]);
                }

                RefreshAnchorVisibility();
            }

            SyncToggle(_anchorFollowsBodyToggle, _activeCamera.anchorFollowsBody, ref _lastAnchorFollowsBody);
            RefreshAnchorStatus();
        }

        private bool? _lastAnchorFollowsBody;

        private void ClearAnchorReferences()
        {
            _anchorSection = null;
            _anchorGroup = null;
            _anchorDropdown = null;
            _anchorTargetDropdown = null;
            _anchorFollowsBodyToggle = null;
            _anchorStatus = null;
            _anchorTargetIds.Clear();
            _anchorTargetsBuilt = false;
            _lastAnchorStatusText = null;
            _lastAnchorObjectLabel = null;
            _lastAnchorFollowsBody = null;
            _lastAnchorSpace = (CameraPinSpace)(-1);
        }

#if UNITY_INCLUDE_TESTS
        public static string[] AnchorSpaceKeysForTest => AnchorSpaceKeys;

#endif
    }
}
