using System.Collections.Generic;
using Basis.Scripts.Avatar;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;

namespace Basis.BasisUI
{
    public static class SettingsProviderDeviceOffsets
    {
        private const string NoneEntry = "none";
        private const string PositionTooltip = "trackerLinking.deviceOffsets.position.tooltip";
        private const string RotationTooltip = "trackerLinking.deviceOffsets.rotation.tooltip";
        private const float AxisRowHeight = 80f;
        private static readonly string[] AxisNames = { "X", "Y", "Z" };
        private static readonly BasisDeviceOffsetAxes[] PositionAxes = { BasisDeviceOffsetAxes.PositionX, BasisDeviceOffsetAxes.PositionY, BasisDeviceOffsetAxes.PositionZ };
        private static readonly BasisDeviceOffsetAxes[] RotationAxes = { BasisDeviceOffsetAxes.RotationX, BasisDeviceOffsetAxes.RotationY, BasisDeviceOffsetAxes.RotationZ };

        public static void Build(RectTransform container)
        {
            PanelSectionToggle section = PanelSectionToggle.CreateNewEntry(container);
            section.SetTitle(BasisLocalization.Get("trackerLinking.deviceOffsets"));
            section.Descriptor.SetTooltip(BasisLocalization.Get("trackerLinking.deviceOffsets.tooltip"));
            int start = container.childCount;

            PanelDropdown deviceDropdown = PanelDropdown.CreateNewEntry(container);
            deviceDropdown.Descriptor.SetTitle(BasisLocalization.Get("trackerLinking.deviceOffsets.device"));
            deviceDropdown.Descriptor.SetTooltip(BasisLocalization.Get("trackerLinking.deviceOffsets.device.tooltip"));

            float limit = BasisDeviceOffsetMath.PositionLimit;
            PanelSlider[] position =
            {
                CreateSlider(container, "trackerLinking.deviceOffsets.positionX", PositionTooltip, -limit, limit, 3, ValueDisplayMode.Meters),
                CreateSlider(container, "trackerLinking.deviceOffsets.positionY", PositionTooltip, -limit, limit, 3, ValueDisplayMode.Meters),
                CreateSlider(container, "trackerLinking.deviceOffsets.positionZ", PositionTooltip, -limit, limit, 3, ValueDisplayMode.Meters),
            };
            PanelButton[] positionGrab = CreateAxisRow(container, "trackerLinking.deviceOffsets.grabPosition", "trackerLinking.deviceOffsets.grabPosition.description");

            float pitch = BasisDeviceOffsetMath.PitchLimit;
            PanelSlider[] rotation =
            {
                CreateSlider(container, "trackerLinking.deviceOffsets.rotationX", RotationTooltip, -pitch, pitch, 1, ValueDisplayMode.Degrees),
                CreateSlider(container, "trackerLinking.deviceOffsets.rotationY", RotationTooltip, -180f, 180f, 1, ValueDisplayMode.Degrees),
                CreateSlider(container, "trackerLinking.deviceOffsets.rotationZ", RotationTooltip, -180f, 180f, 1, ValueDisplayMode.Degrees),
            };
            PanelButton[] rotationGrab = CreateAxisRow(container, "trackerLinking.deviceOffsets.grabRotation", "trackerLinking.deviceOffsets.grabRotation.description");

            PanelButton resetButton = PanelButton.CreateNew(container);
            resetButton.Descriptor.SetTitle(BasisLocalization.Get("trackerLinking.deviceOffsets.reset"));
            resetButton.Descriptor.SetTooltip(BasisLocalization.Get("trackerLinking.deviceOffsets.reset.tooltip"));

            object source = new object();
            List<string> keys = new List<string>();
            List<string> labels = new List<string>();
            string assignedSignature = null;
            bool syncing = false;
            bool refreshing = false;
            bool driven = false;
            bool subscribed = false;
            PanelElementDescriptor box = null;
            var devices = BasisDeviceManagement.Instance != null ? BasisDeviceManagement.Instance.AllInputDevices : null;

            bool IsReleased()
            {
                return resetButton == null || resetButton.IsReleased;
            }

            string CurrentKey()
            {
                string key = BasisDeviceOffsetEditor.SelectedKey;
                return key != null && keys.Contains(key) ? key : null;
            }

            bool HasOffset(string key)
            {
                return BasisDeviceOffsets.TryGet(key, out Vector3 offsetPosition, out Quaternion offsetRotation) && !BasisDeviceOffsetMath.IsIdentity(offsetPosition, offsetRotation);
            }

            void ShowValues(string key)
            {
                BasisDeviceOffsets.TryGet(key, out Vector3 offsetPosition, out Quaternion offsetRotation);
                bool drive = key != null && BasisDeviceOffsetEditor.IsGrabbing && BasisDeviceOffsetEditor.TargetKey == key;
                Vector3 euler = BasisDeviceOffsetMath.ToEuler(offsetRotation);
                syncing = true;
                for (int axis = 0; axis < 3; axis++)
                {
                    if (drive != driven)
                    {
                        position[axis].SetExternalDrive(drive);
                        rotation[axis].SetExternalDrive(drive);
                    }
                    position[axis].SetValueWithoutNotify(offsetPosition[axis]);
                    rotation[axis].SetValueWithoutNotify(euler[axis]);
                }
                driven = drive;
                syncing = false;
            }

            void ShowControls(string key)
            {
                bool hasKey = key != null;
                deviceDropdown.SetInteractable(keys.Count > 0);
                bool canGrab = keys.Count > 0 && BasisDeviceOffsetEditor.HasGrabbingHand();
                string unavailable = canGrab ? null : BasisLocalization.Get("trackerLinking.deviceOffsets.grab.unavailable");
                for (int axis = 0; axis < 3; axis++)
                {
                    position[axis].SetInteractable(hasKey);
                    rotation[axis].SetInteractable(hasKey);
                    ShowAxis(positionGrab[axis], PositionAxes[axis], canGrab, unavailable);
                    ShowAxis(rotationGrab[axis], RotationAxes[axis], canGrab, unavailable);
                }
                resetButton.SetInteractable(HasOffset(key));
            }

            void Refresh()
            {
                if (IsReleased())
                {
                    Unsubscribe();
                    return;
                }
                if (refreshing)
                {
                    return;
                }
                refreshing = true;
                CollectDevices(keys, labels);
                string key = CurrentKey();
                if (key == null && keys.Count > 0)
                {
                    key = keys[0];
                    BasisDeviceOffsetEditor.Select(key);
                }
                syncing = true;
                string signature = string.Join("\n", keys);
                if (signature != assignedSignature)
                {
                    assignedSignature = signature;
                    if (keys.Count == 0)
                    {
                        deviceDropdown.AssignEntries(new List<string> { NoneEntry }, new List<string> { BasisLocalization.Get("trackerLinking.deviceOffsets.none") });
                    }
                    else
                    {
                        deviceDropdown.AssignEntries(new List<string>(keys), new List<string>(labels));
                    }
                }
                deviceDropdown.SetValueWithoutNotify(key ?? NoneEntry);
                syncing = false;
                ShowValues(key);
                ShowControls(key);
                refreshing = false;
            }

            void HandleOffsetChanged(string changedKey, object changeSource)
            {
                if (IsReleased())
                {
                    Unsubscribe();
                    return;
                }
                string key = CurrentKey();
                if (ReferenceEquals(changeSource, source) || (changedKey != null && changedKey != key))
                {
                    return;
                }
                ShowValues(key);
                resetButton.SetInteractable(HasOffset(key));
            }

            void HandleChanged()
            {
                Refresh();
            }

            void Unsubscribe()
            {
                if (!subscribed)
                {
                    return;
                }
                subscribed = false;
                if (devices != null)
                {
                    devices.OnListChanged -= HandleChanged;
                }
                BasisTrackerPairing.OnPairingsChanged -= HandleChanged;
                BasisLocalAvatarDriver.CalibrationComplete -= HandleChanged;
                BasisAvatarIKStageCalibration.OnFullBodyCalibrated -= HandleChanged;
                BasisDeviceOffsets.OnOffsetChanged -= HandleOffsetChanged;
                BasisDeviceOffsetEditor.OnStateChanged -= HandleChanged;
            }

            void ApplyFromSliders(bool persist)
            {
                string key = CurrentKey();
                if (syncing || key == null)
                {
                    return;
                }
                Vector3 offsetPosition = new Vector3(position[0].SliderComponent.value, position[1].SliderComponent.value, position[2].SliderComponent.value);
                Quaternion offsetRotation = BasisDeviceOffsetMath.FromEuler(new Vector3(rotation[0].SliderComponent.value, rotation[1].SliderComponent.value, rotation[2].SliderComponent.value));
                BasisDeviceOffsets.Set(key, offsetPosition, offsetRotation, persist, source);
                resetButton.SetInteractable(HasOffset(key));
            }

            void WireSlider(PanelSlider slider)
            {
                slider.SliderComponent.onValueChanged.AddListener(_ => ApplyFromSliders(false));
                slider.OnValueChanged += _ => ApplyFromSliders(true);
            }

            for (int axis = 0; axis < 3; axis++)
            {
                WireSlider(position[axis]);
                WireSlider(rotation[axis]);
                WireAxis(positionGrab[axis], PositionAxes[axis]);
                WireAxis(rotationGrab[axis], RotationAxes[axis]);
            }

            deviceDropdown.OnValueChanged += value =>
            {
                if (!syncing)
                {
                    BasisDeviceOffsetEditor.Select(value == NoneEntry ? null : value);
                }
            };
            resetButton.OnClicked += () =>
            {
                string key = CurrentKey();
                if (key != null)
                {
                    BasisDeviceOffsets.Clear(key, null);
                }
            };

            Refresh();

            if (devices != null)
            {
                devices.OnListChanged += HandleChanged;
            }
            BasisTrackerPairing.OnPairingsChanged += HandleChanged;
            BasisLocalAvatarDriver.CalibrationComplete += HandleChanged;
            BasisAvatarIKStageCalibration.OnFullBodyCalibrated += HandleChanged;
            BasisDeviceOffsets.OnOffsetChanged += HandleOffsetChanged;
            BasisDeviceOffsetEditor.OnStateChanged += HandleChanged;
            subscribed = true;
            resetButton.OnInstanceReleased += Unsubscribe;

            box = PanelSectionToggleHelpers.FinalizeBoxedSectionFromIndex(section, container, start, false, visible =>
            {
                if (visible)
                {
                    Refresh();
                }
                if (box != null)
                {
                    PanelElementDescriptor.RebuildLayoutChain(box.ContentParent, container);
                }
            });
        }

        private static PanelSlider CreateSlider(RectTransform container, string titleKey, string tooltipKey, float min, float max, int decimals, ValueDisplayMode mode)
        {
            PanelSlider slider = PanelSlider.CreateNew(PanelSlider.SliderStyles.Entry, container);
            slider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(BasisLocalization.Get(titleKey), min, max, false, decimals, mode));
            slider.Descriptor.SetTooltip(BasisLocalization.Get(tooltipKey));
            slider.SetResetDefault(0f);
            slider.ExcludeFromShare = true;
            slider.SetValueWithoutNotify(0f);
            return slider;
        }

        private static PanelButton[] CreateAxisRow(RectTransform container, string titleKey, string descriptionKey)
        {
            PanelElementDescriptor group = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            group.SetBackgroundVisible(false);
            group.SetTitle(BasisLocalization.Get(titleKey));
            group.SetDescription(BasisLocalization.Get(descriptionKey));
            PanelTabGroup row = PanelTabGroup.CreateNew(group.ContentParent, LayoutDirection.HorizontalNoBackground);
            row.Descriptor.SetHeight(AxisRowHeight);
            string tooltip = BasisLocalization.Get("trackerLinking.deviceOffsets.grabAxis.tooltip");
            PanelButton[] buttons = new PanelButton[AxisNames.Length];
            for (int axis = 0; axis < AxisNames.Length; axis++)
            {
                PanelButton button = PanelButton.CreateNew(PanelButton.ButtonStyles.StandardButton, row.TabButtonParent);
                button.Descriptor.SetTitle("<b><color=#" + ColorUtility.ToHtmlStringRGB(BasisDeviceOffsetEditor.AxisColor(axis)) + ">" + AxisNames[axis] + "</color></b>");
                button.Descriptor.SetTooltip(tooltip);
                buttons[axis] = button;
            }
            return buttons;
        }

        private static void WireAxis(PanelButton button, BasisDeviceOffsetAxes axis)
        {
            button.OnClicked += () => BasisDeviceOffsetEditor.SetAxisEnabled(axis, !BasisDeviceOffsetEditor.IsAxisEnabled(axis));
        }

        private static void ShowAxis(PanelButton button, BasisDeviceOffsetAxes axis, bool canGrab, string unavailable)
        {
            bool enabled = BasisDeviceOffsetEditor.IsAxisEnabled(axis);
            if (button.ButtonStyling != null)
            {
                button.ButtonStyling.ShowIndicator(enabled);
            }
            button.SetInteractable(enabled || canGrab, enabled || canGrab ? null : unavailable);
        }

        private static void CollectDevices(List<string> keys, List<string> labels)
        {
            keys.Clear();
            labels.Clear();
            BasisDeviceManagement management = BasisDeviceManagement.Instance;
            if (management == null)
            {
                return;
            }
            var devices = management.AllInputDevices;
            int count = devices.Count;
            for (int index = 0; index < count; index++)
            {
                BasisInput input = devices[index];
                if (input == null || string.IsNullOrEmpty(input.DeviceOffsetKey) || keys.Contains(input.DeviceOffsetKey) || !input.TryGetRole(out BasisBoneTrackedRole role))
                {
                    continue;
                }
                keys.Add(input.DeviceOffsetKey);
                labels.Add(BasisDeviceOffsetEditor.RoleLabel(role) + ": " + input.CommonDeviceIdentifier);
            }
        }
    }
}
