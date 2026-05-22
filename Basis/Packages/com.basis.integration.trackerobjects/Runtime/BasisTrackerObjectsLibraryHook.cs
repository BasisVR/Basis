using System.Collections.Generic;
using System.Threading.Tasks;
using Basis.BasisUI;
using Basis.Scripts.Avatar;
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

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Subscribe()
        {
            LibraryProvider.OnInstanceRowCreated -= OnRowCreated;
            LibraryProvider.OnInstanceRowCreated += OnRowCreated;
        }

        private static void OnRowCreated(RectTransform parent, BasisRuntimeSpawnRegistry.SpawnInstance instance)
        {
            if (instance == null) return;
            string netID = instance.LoadedNetID;
            if (string.IsNullOrEmpty(netID)) return;

            // Scene-mode and embedded instances can't host a tracker binding (no
            // pickup/rigid surface to drive, and they're not user-owned spawns), so
            // skip adding the button at all — a disabled fourth button just pushes
            // the Select/Teleport/Remove row over.
            if (instance.SpawnMode == BasisRuntimeSpawnRegistry.SpawnMode.Scene) return;
            if (instance.SpawnMethod == BasisRuntimeSpawnRegistry.SpawnMethod.Embedded) return;

            bool hasBinding = BasisTrackerObjectManager.TryGetBindingByLoadedNetID(netID, out _);
            PanelButton button = PanelButton.CreateNew(PanelButton.ButtonStyles.StandardButton, parent);
            button.Descriptor.SetTitle(string.Empty);
            button.SetIcon(hasBinding ? AddressableAssets.Sprites.Unlink : AddressableAssets.Sprites.Link);
            button.SetSize(RowSize);
            // Match the row's left-side status-icon padding (PE Image Simple Square inset).
            button.Descriptor.IconImage.rectTransform.sizeDelta = new Vector2(-30, -30);

            button.OnClicked += async () =>
            {
                if (BasisTrackerObjectManager.TryGetBindingByLoadedNetID(netID, out BasisTrackerBinding existing))
                {
                    BasisTrackerObjectManager.TryRemoveBinding(existing.Id);
                    button.SetIcon(AddressableAssets.Sprites.Link);
                    return;
                }

                if (!BasisRuntimeSpawnRegistry.SpawnedGameobjects.TryGetValue(netID, out GameObject go) || go == null)
                {
                    BasisDebug.LogWarning($"AssignTracker: spawn instance {netID} has no resolved GameObject", BasisDebug.LogTag.TrackerObjects);
                    return;
                }

                BasisInput chosen = await OpenPickerAsync();
                if (chosen == null) return;

                if (BasisTrackerObjectManager.TryCreateBinding(chosen, go.transform, netID, out _))
                {
                    button.SetIcon(AddressableAssets.Sprites.Unlink);
                }
            };
        }

        private static async Task<BasisInput> OpenPickerAsync()
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
            if (candidates.Count == 0)
            {
                PanelTextField empty = PanelTextField.CreateNew(PanelTextField.TextFieldStyles.Entry, picker.Descriptor.ContentParent);
                empty._inputField.gameObject.SetActive(false);
                empty.Descriptor.SetTitle(BasisLocalization.Get("library.trackerPicker.empty"));
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

        private static List<BasisInput> CollectBindableTrackers()
        {
            List<BasisInput> result = new List<BasisInput>();
            BasisObservableList<BasisInput> devices = BasisDeviceManagement.Instance?.AllInputDevices;
            if (devices == null) return result;

            for (int i = 0; i < devices.Count; i++)
            {
                BasisInput input = devices[i];
                if (input == null) continue;
                if (string.IsNullOrEmpty(input.UniqueDeviceIdentifier)) continue;
                if (input is BasisVirtualMidpointInput) continue;
                if (input.IsLinked) continue;
                if (BasisTrackerRoleOverride.TryGetOverride(input.UniqueDeviceIdentifier, out _)) continue;
                if (input.DeviceMatchSettings != null && input.DeviceMatchSettings.HasTrackedRole) continue;
                // A tracker already driving a body bone (post-calibration) is excluded so
                // calibration and prop binding can't fight over the same device. To reuse
                // a calibrated tracker, decalibrate first.
                if (input.TryGetRole(out _)) continue;

                result.Add(input);
            }
            return result;
        }
    }
}
