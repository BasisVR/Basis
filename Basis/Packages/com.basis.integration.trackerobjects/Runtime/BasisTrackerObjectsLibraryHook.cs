using System.Collections.Generic;
using System.Threading.Tasks;
using Basis.BasisUI;
using Basis.Scripts.Avatar;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Device_Management.Devices.Pairing;
using Basis.Scripts.TransformBinders.BoneControl;
using Basis.TrackerObjects;
using UnityEngine;

namespace Basis.Integration.TrackerObjects
{
    internal static class BasisTrackerObjectsLibraryHook
    {
        private static readonly Vector2 PickerSize = new Vector2(900, 720);
        private static readonly Vector2 RowSize = new Vector2(80, 80);
        private static readonly Vector2 PickerRowSize = new Vector2(700, 60);

        // Latest row button per netID, so a binding dissolving externally (steal,
        // static lock, tracker loss) can update the open menu's icon. Rows are
        // transient; entries go stale when the tab rebuilds and are pruned on use.
        private static readonly Dictionary<string, PanelButton> _rowButtons = new Dictionary<string, PanelButton>();

        // The device list this hook is watching for spare-tracker availability.
        // Subscribed lazily from OnRowCreated because BasisDeviceManagement.Instance
        // doesn't exist yet at SubsystemRegistration, and re-checked there in case
        // the singleton was reassigned.
        private static BasisObservableList<BasisInput> _watchedDevices;
        private static readonly List<string> _staleRowKeys = new List<string>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Subscribe()
        {
            // Statics survive Play sessions when domain reload is disabled; drop the
            // dead session's device watch and row cache before re-registering.
            if (_watchedDevices != null)
            {
                _watchedDevices.OnListChanged -= RefreshAllButtonVisibility;
                _watchedDevices = null;
            }
            _rowButtons.Clear();
            _staleRowKeys.Clear();

            LibraryProvider.OnInstanceRowCreated -= OnRowCreated;
            LibraryProvider.OnInstanceRowCreated += OnRowCreated;
            BasisTrackerObjectManager.OnBindingCreated -= OnBindingCreated;
            BasisTrackerObjectManager.OnBindingCreated += OnBindingCreated;
            BasisTrackerObjectManager.OnBindingRemoved -= OnBindingRemoved;
            BasisTrackerObjectManager.OnBindingRemoved += OnBindingRemoved;
        }

        private static void OnBindingCreated(BasisTrackerBinding binding)
        {
            RefreshRowButton(binding.LoadedNetID, bound: true);
            // Binding a tracker can consume the last spare; unbound rows lose their button.
            RefreshAllButtonVisibility();
        }

        private static void OnBindingRemoved(BasisTrackerBinding binding)
        {
            RefreshRowButton(binding.LoadedNetID, bound: false);
            // Unbinding frees a tracker back into the spare pool; hidden buttons return.
            RefreshAllButtonVisibility();
        }

        private static void RefreshRowButton(string netID, bool bound)
        {
            if (string.IsNullOrEmpty(netID)) return;
            if (!_rowButtons.TryGetValue(netID, out PanelButton button)) return;
            if (button == null)
            {
                // The menu closed or the tab rebuilt since this row was recorded.
                _rowButtons.Remove(netID);
                return;
            }
            button.SetIcon(bound ? AddressableAssets.Sprites.Unlink : AddressableAssets.Sprites.Link);
            button.Descriptor.SetTooltip(BasisLocalization.Get(bound
                ? "library.instantiated.unbindTracker.tooltip"
                : "library.instantiated.assignTracker.tooltip"));
        }

        private static void WatchDeviceList()
        {
            BasisObservableList<BasisInput> devices = BasisDeviceManagement.Instance?.AllInputDevices;
            if (devices == null || ReferenceEquals(devices, _watchedDevices)) return;
            if (_watchedDevices != null)
            {
                _watchedDevices.OnListChanged -= RefreshAllButtonVisibility;
            }
            devices.OnListChanged += RefreshAllButtonVisibility;
            _watchedDevices = devices;
        }

        /// <summary>
        /// A row's button shows when a spare bindable tracker exists, or when its prop
        /// is already bound (the Unlink action must stay reachable regardless).
        /// </summary>
        private static bool ShouldShowButton(string netID, bool anySpare)
        {
            return anySpare || BasisTrackerObjectManager.TryGetBindingByLoadedNetID(netID, out _);
        }

        private static void RefreshAllButtonVisibility()
        {
            bool anySpare = HasSpareTracker();
            _staleRowKeys.Clear();
            foreach (KeyValuePair<string, PanelButton> pair in _rowButtons)
            {
                if (pair.Value == null)
                {
                    _staleRowKeys.Add(pair.Key);
                    continue;
                }
                pair.Value.Descriptor.SetActive(ShouldShowButton(pair.Key, anySpare));
            }
            int staleCount = _staleRowKeys.Count;
            for (int index = 0; index < staleCount; index++)
            {
                _rowButtons.Remove(_staleRowKeys[index]);
            }
        }

        private static void OnRowCreated(RectTransform parent, BasisRuntimeSpawnRegistry.SpawnInstance instance)
        {
            if (instance == null) return;
            string netID = instance.LoadedNetID;
            if (string.IsNullOrEmpty(netID)) return;

            // Only GameObject-mode (prop) instances can host a tracker binding — scenes
            // and avatars have no pickup/rigid surface to drive, and embedded items
            // aren't user-owned spawns. Skip adding the button at all rather than
            // disabling it — a dead extra button just pushes the row over.
            if (instance.SpawnMode != BasisRuntimeSpawnRegistry.SpawnMode.GameObject) return;
            if (instance.SpawnMethod == BasisRuntimeSpawnRegistry.SpawnMethod.Embedded) return;
            // Static-locked props are frozen server-side; the row rebuilds on the
            // Modified broadcast, so the button reappears when the lock clears.
            if (instance.Static) return;

            WatchDeviceList();

            bool hasBinding = BasisTrackerObjectManager.TryGetBindingByLoadedNetID(netID, out _);
            PanelButton button = PanelButton.CreateNew(PanelButton.ButtonStyles.StandardButton, parent);
            button.Descriptor.SetTitle(string.Empty);
            button.SetIcon(hasBinding ? AddressableAssets.Sprites.Unlink : AddressableAssets.Sprites.Link);
            button.SetSize(RowSize);
            // Inset the icon so its strokes stay clear of the bevel — matches the row's
            // other action buttons.
            button.Descriptor.IconImage.rectTransform.sizeDelta = new Vector2(-30, -30);
            button.Descriptor.SetTooltip(BasisLocalization.Get(hasBinding
                ? "library.instantiated.unbindTracker.tooltip"
                : "library.instantiated.assignTracker.tooltip"));
            _rowButtons[netID] = button;
            // Built hidden when no spare tracker exists (and this prop isn't bound);
            // the device-list and binding subscriptions bring it back live.
            if (!hasBinding && !HasSpareTracker())
            {
                button.Descriptor.SetActive(false);
            }

            // Icon and tooltip updates flow from OnBindingCreated/OnBindingRemoved
            // (via RefreshRowButton), so the click handler only drives the manager.
            button.OnClicked += async () =>
            {
                if (BasisTrackerObjectManager.TryGetBindingByLoadedNetID(netID, out BasisTrackerBinding existing))
                {
                    BasisTrackerObjectManager.TryRemoveBinding(existing.Id);
                    return;
                }

                if (!BasisRuntimeSpawnRegistry.SpawnedGameobjects.TryGetValue(netID, out GameObject go) || go == null)
                {
                    BasisDebug.LogWarning($"AssignTracker: spawn instance {netID} has no resolved GameObject", BasisDebug.LogTag.TrackerObjects);
                    return;
                }

                BasisInput chosen = await OpenPickerAsync(go.transform);
                if (chosen == null) return;

                // The spawn can be removed or swapped while the picker is open, so
                // re-resolve rather than binding through the stale capture. Static and
                // duplicate-binding races are re-checked inside the manager.
                if (!BasisRuntimeSpawnRegistry.SpawnedGameobjects.TryGetValue(netID, out go) || go == null) return;

                await BasisTrackerObjectManager.TryCreateBindingAsync(chosen, go.transform, netID);
            };
        }

        private static async Task<BasisInput> OpenPickerAsync(Transform target)
        {
            DialogBox<BasisInput> picker = DialogBox<BasisInput>.Create(
                LibraryProvider.panel,
                PickerSize,
                BasisLocalization.Get("library.trackerPicker.title"),
                description: null,
                icon: AddressableAssets.Sprites.Information);

            PanelButton cancel = PanelButton.CreateNew(PanelButton.ButtonStyles.ExitButton, picker.Descriptor.Header);
            cancel.Descriptor.SetTitle(BasisLocalization.Get("library.trackerPicker.cancel"));
            cancel.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 125);
            cancel.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, 50);
            cancel.OnClicked += () => picker.Cancel(null);

            List<BasisInput> candidates = CollectBindableTrackers();
            // Same reach rule as grabbing the prop, so the picker never offers a
            // tracker the bind gate would refuse. The distances are a snapshot at
            // open; the manager re-checks range when the choice lands.
            BasisPickupInteractable pickup = BasisTrackerObjectManager.ResolvePickup(target);
            int eligible = candidates.Count;
            candidates.RemoveAll(t => !BasisTrackerObjectManager.IsWithinBindRange(t.transform.position, target, pickup));
            if (candidates.Count == 0)
            {
                PanelTextField empty = PanelTextField.CreateNew(PanelTextField.TextFieldStyles.Entry, picker.Descriptor.ContentParent);
                empty._inputField.gameObject.SetActive(false);
                empty.Descriptor.SetTitle(BasisLocalization.Get(eligible > 0
                    ? "library.trackerPicker.outOfRange"
                    : "library.trackerPicker.empty"));
            }
            else
            {
                for (int index = 0; index < candidates.Count; index++)
                {
                    BasisInput tracker = candidates[index];
                    string roleLabel = tracker.TryGetRole(out BasisBoneTrackedRole role)
                        ? role.ToString()
                        : "Tracker";
                    PanelButton row = PanelButton.CreateNew(PanelButton.ButtonStyles.StandardButton, picker.Descriptor.ContentParent);
                    row.Descriptor.SetTitle($"{roleLabel} — {tracker.UniqueDeviceIdentifier}");
                    row.SetSize(PickerRowSize);
                    row.OnClicked += () => picker.CloseWithResult(tracker);
                }
            }

            return await picker.WaitAsync();
        }

        private static bool IsBindableSpareTracker(BasisInput input)
        {
            if (input == null) return false;
            if (string.IsNullOrEmpty(input.UniqueDeviceIdentifier)) return false;
            if (input is BasisVirtualMidpointInput) return false;
            if (input.IsLinked) return false;
            if (BasisTrackerRoleOverride.TryGetOverride(input.UniqueDeviceIdentifier, out _)) return false;
            if (input.DeviceMatchSettings != null && input.DeviceMatchSettings.HasTrackedRole) return false;
            // A tracker already driving a body bone (post-calibration) is excluded so
            // calibration and prop binding can't fight over the same device. To reuse
            // a calibrated tracker, decalibrate first.
            if (input.TryGetRole(out _)) return false;
            // One tracker drives at most one prop; unbind it there first.
            if (BasisTrackerObjectManager.IsTrackerBound(input)) return false;
            return true;
        }

        private static bool HasSpareTracker()
        {
            BasisObservableList<BasisInput> devices = BasisDeviceManagement.Instance?.AllInputDevices;
            if (devices == null) return false;
            int count = devices.Count;
            for (int i = 0; i < count; i++)
            {
                if (IsBindableSpareTracker(devices[i])) return true;
            }
            return false;
        }

        private static List<BasisInput> CollectBindableTrackers()
        {
            List<BasisInput> result = new List<BasisInput>();
            BasisObservableList<BasisInput> devices = BasisDeviceManagement.Instance?.AllInputDevices;
            if (devices == null) return result;

            int count = devices.Count;
            for (int i = 0; i < count; i++)
            {
                BasisInput input = devices[i];
                if (IsBindableSpareTracker(input))
                {
                    result.Add(input);
                }
            }
            return result;
        }
    }
}
