using System;
using System.Collections.Generic;
using Basis.BasisUI.Styling;
using Basis.Scripts.Avatar;
using Basis.Scripts.Debugging;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Device_Management.Devices.Pairing;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;

namespace Basis.BasisUI
{
    public static class SettingsProviderDeviceOverrides
    {
        private const string AutoEntry = "auto";
        private const string StandardPartStyle = "Button Standard";
        private const string IgnoredPartStyle = "Button Danger";
        private const float PartsRowHeight = 80f;
        private static readonly BasisDeviceIgnore[] Parts = { BasisDeviceIgnore.Pose, BasisDeviceIgnore.Buttons, BasisDeviceIgnore.Sticks, BasisDeviceIgnore.Fingers, BasisDeviceIgnore.Pointer };
        private static readonly string[] PartKeys = { "trackerLinking.deviceOverrides.parts.pose", "trackerLinking.deviceOverrides.parts.buttons", "trackerLinking.deviceOverrides.parts.sticks", "trackerLinking.deviceOverrides.parts.fingers", "trackerLinking.deviceOverrides.parts.pointer" };
        private static readonly string[] PartTooltipKeys = { "trackerLinking.deviceOverrides.parts.pose.tooltip", "trackerLinking.deviceOverrides.parts.buttons.tooltip", "trackerLinking.deviceOverrides.parts.sticks.tooltip", "trackerLinking.deviceOverrides.parts.fingers.tooltip", "trackerLinking.deviceOverrides.parts.pointer.tooltip" };

        private sealed class Row
        {
            public string Key;
            public BasisInput Input;
            public PanelElementDescriptor Group;
            public PanelButton Identify;
            public PanelDropdown Hand;
            public PanelToggle Ignore;
            public PanelTabGroup PartsRow;
            public PanelButton[] PartButtons;
        }

        public static void Build(RectTransform container)
        {
            PanelSectionToggle section = PanelSectionToggle.CreateNewEntry(container);
            section.SetTitle(Get(string.Empty));
            section.Descriptor.SetTooltip(Get(".tooltip"));
            int start = container.childCount;

            PanelElementDescriptor rowsGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            rowsGroup.SetBackgroundVisible(false);
            if (rowsGroup.Header != null)
            {
                rowsGroup.Header.gameObject.SetActive(false);
            }
            RectTransform rowsParent = rowsGroup.ContentParent;

            List<Row> rows = new List<Row>();
            List<BasisInput> devices = new List<BasisInput>();
            PanelElementDescriptor empty = null;
            PanelElementDescriptor box = null;
            bool syncing = false;
            bool subscribed = false;
            BasisObservableList<BasisInput> deviceList = BasisDeviceManagement.Instance != null ? BasisDeviceManagement.Instance.AllInputDevices : null;

            bool IsReleased()
            {
                return rowsGroup == null || rowsGroup.IsReleased;
            }

            void Unsubscribe()
            {
                if (!subscribed)
                {
                    return;
                }
                subscribed = false;
                if (deviceList != null)
                {
                    deviceList.OnListChanged -= Refresh;
                }
                BasisDeviceOverrides.OnChanged -= Refresh;
                BasisTrackerIdentifyGizmos.OnIdentifyChanged -= Refresh;
                BasisLocalAvatarDriver.CalibrationComplete -= Refresh;
                BasisAvatarIKStageCalibration.OnFullBodyCalibrated -= Refresh;
            }

            void RebuildLayout()
            {
                for (int index = 0; index < rows.Count; index++)
                {
                    Row row = rows[index];
                    if (row.Group == null || row.Group.IsReleased)
                    {
                        continue;
                    }
                    RectTransform deepest = row.PartsRow != null && !row.PartsRow.IsReleased ? row.PartsRow.TabButtonParent as RectTransform : null;
                    PanelElementDescriptor.RebuildLayoutChain(deepest != null ? deepest : row.Group.ContentParent, rowsParent);
                }
                if (empty != null && !empty.IsReleased)
                {
                    PanelElementDescriptor.RebuildLayoutChain(empty.ContentParent, rowsParent);
                }
                PanelElementDescriptor.RebuildLayoutChain(rowsParent, container);
            }

            void Refresh()
            {
                if (IsReleased())
                {
                    Unsubscribe();
                    return;
                }
                if (box != null && !box.gameObject.activeSelf)
                {
                    return;
                }
                CollectDevices(devices);
                bool rebuild = rows.Count != devices.Count;
                for (int index = 0; !rebuild && index < rows.Count; index++)
                {
                    rebuild = !ReferenceEquals(rows[index].Input, devices[index]) || rows[index].Key != devices[index].OverrideKey;
                }
                syncing = true;
                if (rebuild)
                {
                    for (int index = 0; index < rows.Count; index++)
                    {
                        PanelElementDescriptor group = rows[index].Group;
                        if (group != null && !group.IsReleased)
                        {
                            group.ReleaseInstance();
                        }
                    }
                    rows.Clear();
                    if (empty != null && !empty.IsReleased)
                    {
                        empty.ReleaseInstance();
                    }
                    empty = null;
                    if (devices.Count == 0)
                    {
                        empty = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, rowsParent);
                        empty.SetTitle(Get(".none"));
                    }
                    for (int index = 0; index < devices.Count; index++)
                    {
                        rows.Add(BuildRow(rowsParent, devices[index], () => syncing));
                    }
                }
                else
                {
                    for (int index = 0; index < rows.Count; index++)
                    {
                        ApplyRow(rows[index]);
                    }
                }
                syncing = false;
                RebuildLayout();
            }

            Refresh();

            if (deviceList != null)
            {
                deviceList.OnListChanged += Refresh;
            }
            BasisDeviceOverrides.OnChanged += Refresh;
            BasisTrackerIdentifyGizmos.OnIdentifyChanged += Refresh;
            BasisLocalAvatarDriver.CalibrationComplete += Refresh;
            BasisAvatarIKStageCalibration.OnFullBodyCalibrated += Refresh;
            subscribed = true;
            rowsGroup.OnInstanceReleased += Unsubscribe;

            box = PanelSectionToggleHelpers.FinalizeBoxedSectionFromIndex(section, container, start, false, visible =>
            {
                if (visible)
                {
                    Refresh();
                }
                else if (box != null)
                {
                    PanelElementDescriptor.RebuildLayoutChain(box.rectTransform, container);
                }
            });
        }

        private static Row BuildRow(RectTransform parent, BasisInput input, Func<bool> syncing)
        {
            string key = input.OverrideKey;
            PanelElementDescriptor group = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, parent);
            group.SetTitle(SettingsProviderTrackerSettings.NoParse(SettingsProviderTrackerSettings.BuildTrackerLabel(input)));
            Row row = new Row { Key = key, Input = input, Group = group };

            row.Identify = PanelButton.CreateNew(group.ContentParent);
            row.Identify.Descriptor.SetTooltip(BasisLocalization.Get("trackerLinking.identifyLabel.tooltip"));
            row.Identify.OnClicked += () => BasisTrackerIdentifyGizmos.Toggle(input);

            if (input.IsHandDevice)
            {
                row.Hand = PanelDropdown.CreateNewEntry(group.ContentParent);
                row.Hand.Descriptor.SetTitle(Get(".hand"));
                row.Hand.Descriptor.SetTooltip(Get(".hand.tooltip"));
                row.Hand.ExcludeFromShare = true;
                row.Hand.AssignEntries(
                    new List<string> { AutoEntry, BasisBoneTrackedRole.LeftHand.ToString(), BasisBoneTrackedRole.RightHand.ToString() },
                    new List<string> { Get(".hand.auto"), Get(".hand.left"), Get(".hand.right") },
                    new List<string> { Get(".hand.auto.tooltip"), Get(".hand.left.tooltip"), Get(".hand.right.tooltip") });
                row.Hand.OnValueChanged += value =>
                {
                    if (syncing())
                    {
                        return;
                    }
                    if (string.IsNullOrEmpty(value) || value == AutoEntry)
                    {
                        BasisDeviceOverrides.ClearHand(key);
                    }
                    else if (Enum.TryParse(value, out BasisBoneTrackedRole role))
                    {
                        BasisDeviceOverrides.SetHand(key, role);
                    }
                };
            }

            row.Ignore = PanelToggle.CreateNewEntry(group.ContentParent);
            row.Ignore.Descriptor.SetTitle(Get(".ignore"));
            row.Ignore.Descriptor.SetTooltip(Get(".ignore.tooltip"));
            row.Ignore.ExcludeFromShare = true;
            row.Ignore.OnValueChanged += value =>
            {
                if (syncing())
                {
                    return;
                }
                BasisDeviceOverrides.SetIgnorePart(key, BasisDeviceIgnore.Device, value);
            };

            PanelElementDescriptor partsGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, group.ContentParent);
            partsGroup.SetBackgroundVisible(false);
            partsGroup.SetTitle(Get(".parts"));
            partsGroup.SetDescription(Get(".parts.description"));
            partsGroup.SetTooltip(Get(".parts.tooltip"));
            PanelTabGroup partsRow = PanelTabGroup.CreateNew(partsGroup.ContentParent, LayoutDirection.HorizontalNoBackground);
            partsRow.Descriptor.SetHeight(PartsRowHeight);
            row.PartsRow = partsRow;
            row.PartButtons = new PanelButton[Parts.Length];
            for (int index = 0; index < Parts.Length; index++)
            {
                BasisDeviceIgnore part = Parts[index];
                PanelButton button = PanelButton.CreateNew(PanelButton.ButtonStyles.StandardButton, partsRow.TabButtonParent);
                button.Descriptor.SetTitle(BasisLocalization.Get(PartKeys[index]));
                button.Descriptor.SetTooltip(BasisLocalization.Get(PartTooltipKeys[index]));
                button.OnClicked += () => BasisDeviceOverrides.SetIgnorePart(key, part, (BasisDeviceOverrides.GetIgnore(key) & part) == 0);
                row.PartButtons[index] = button;
            }

            ApplyRow(row);
            return row;
        }

        private static void ApplyRow(Row row)
        {
            BasisInput input = row.Input;
            if (input == null || row.Group == null || row.Group.IsReleased)
            {
                return;
            }
            row.Group.SetRichDescription(BuildStatus(input));
            SettingsProviderTrackerSettings.ApplyIdentifyVisual(row.Identify, input, false);
            if (row.Hand != null && !row.Hand.IsReleased)
            {
                row.Hand.SetValueWithoutNotify(BasisDeviceOverrides.TryGetHand(row.Key, out BasisBoneTrackedRole hand) ? hand.ToString() : AutoEntry);
            }
            BasisDeviceIgnore ignore = BasisDeviceOverrides.GetIgnore(row.Key);
            bool wholeDevice = (ignore & BasisDeviceIgnore.Device) != 0;
            if (row.Ignore != null && !row.Ignore.IsReleased)
            {
                row.Ignore.SetValueWithoutNotify(wholeDevice);
            }
            if (row.PartButtons == null)
            {
                return;
            }
            for (int index = 0; index < row.PartButtons.Length; index++)
            {
                PanelButton button = row.PartButtons[index];
                if (button == null || button.IsReleased)
                {
                    continue;
                }
                if (button.ButtonStyling != null)
                {
                    string style = (ignore & Parts[index]) != 0 ? IgnoredPartStyle : StandardPartStyle;
                    if (button.ButtonStyling.ColorStyle != style)
                    {
                        button.ButtonStyling.SetStyle(style);
                    }
                }
                button.SetInteractable(!wholeDevice);
            }
        }

        private static string BuildStatus(BasisInput input)
        {
            Color success = SettingsProviderTrackerSettings.PaletteColor(p => p.SuccessColor, new Color(0.09f, 0.8f, 0.47f));
            Color caution = SettingsProviderTrackerSettings.PaletteColor(p => p.CautionColor, new Color(1f, 0.82f, 0.34f));
            Color muted = SettingsProviderTrackerSettings.PaletteColor(p => p.FontColor3, SettingsProviderTrackerSettings.MutedFallback);
            if (input.IgnoresDevice)
            {
                return SettingsProviderTrackerSettings.Tint(caution, Get(".status.ignored"));
            }
            if (!input.TryGetRole(out BasisBoneTrackedRole role))
            {
                return SettingsProviderTrackerSettings.Tint(muted, Get(".status.unassigned"));
            }
            string roleName = SettingsProviderTrackerSettings.FormatRole(role);
            string text = input.IgnoresPose
                ? BasisLocalization.Get("trackerLinking.deviceOverrides.status.poseIgnored", roleName)
                : BasisLocalization.Get("trackerLinking.status.driving", roleName);
            if (input.HasRoleOverride)
            {
                text = $"{text} ({Get(".status.override")})";
            }
            return SettingsProviderTrackerSettings.Tint(input.IgnoresPose ? caution : success, text);
        }

        private static string Get(string suffix)
        {
            return BasisLocalization.Get("trackerLinking.deviceOverrides" + suffix);
        }

        private static void CollectDevices(List<BasisInput> devices)
        {
            devices.Clear();
            BasisDeviceManagement management = BasisDeviceManagement.Instance;
            if (management == null)
            {
                return;
            }
            BasisObservableList<BasisInput> all = management.AllInputDevices;
            int count = all.Count;
            for (int index = 0; index < count; index++)
            {
                BasisInput input = all[index];
                if (input == null || string.IsNullOrEmpty(input.OverrideKey))
                {
                    continue;
                }
                if (input is BasisVirtualMidpointInput || input is BasisTouchInputDevice)
                {
                    continue;
                }
                if (input.HasNaturalRole && IsHead(input.NaturalRole))
                {
                    continue;
                }
                if (input.TryGetRole(out BasisBoneTrackedRole role) && IsHead(role))
                {
                    continue;
                }
                devices.Add(input);
            }
        }

        private static bool IsHead(BasisBoneTrackedRole role)
        {
            return role == BasisBoneTrackedRole.CenterEye || role == BasisBoneTrackedRole.Head;
        }
    }
}
