using Basis.BasisUI;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices.Desktop;
using Basis.Scripts.TransformBinders.BoneControl;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static BasisActionDriver;

public static class SettingsProviderControllerConfig
{
    public static PanelTabPage OpenControllerConfig(PanelTabGroup tabGroup)
    {
        PanelTabPage tab = PanelTabPage.CreateVertical(tabGroup.Descriptor.ContentParent);
        PanelElementDescriptor descriptor = tab.Descriptor;
        RectTransform container = descriptor.ContentParent;

        // Gameplay & Input (starts expanded)
        PanelSlider sliderSnapTurnAngleRef = null;
        PanelSlider sliderSmoothTurnSpeedRef = null;
        SettingsProviderKeyboardBindings.CreateCollapsibleSection(
            container, BasisLocalization.Get("settings.controls.gameplay.title"), BasisLocalization.Get("settings.controls.gameplay.description"), group =>
        {
            PanelDropdown dropdownDominantHand = PanelDropdown.CreateNewEntry(group);
            dropdownDominantHand.Descriptor.SetTitle(BasisLocalization.Get("settings.controls.dominantHand"));
            dropdownDominantHand.Descriptor.SetTooltip(BasisLocalization.Get("settings.controls.dominantHand.tooltip"));
            dropdownDominantHand.AssignEntries(new List<string> { BasisDominantHand.Right, BasisDominantHand.Left });
            dropdownDominantHand.AssignBinding(BasisSettingsDefaults.DominantHand);

            PanelDropdown dropdownDesktopInputInVR = PanelDropdown.CreateNewEntry(group);
            dropdownDesktopInputInVR.Descriptor.SetTitle(BasisLocalization.Get("settings.controls.desktopInputInVR"));
            dropdownDesktopInputInVR.Descriptor.SetTooltip(BasisLocalization.Get("settings.controls.desktopInputInVR.tooltip"));
            dropdownDesktopInputInVR.AssignLocalizedEntries(
                new List<string>
                {
                    BasisSettingsDefaults.DesktopInputInVR_Adaptive,
                    BasisSettingsDefaults.DesktopInputInVR_AlwaysOn,
                    BasisSettingsDefaults.DesktopInputInVR_Off,
                },
                new List<string>
                {
                    "settings.controls.desktopInputInVR.adaptive",
                    "settings.controls.desktopInputInVR.alwaysOn",
                    "settings.controls.desktopInputInVR.off",
                });
            dropdownDesktopInputInVR.AssignBinding(BasisSettingsDefaults.DesktopInputInVR);

            PanelToggle toggleInvertMouse = PanelToggle.CreateNewEntry(group);
            toggleInvertMouse.Descriptor.SetTitle(BasisLocalization.Get("settings.controls.invertMouse"));
            toggleInvertMouse.Descriptor.SetTooltip(BasisLocalization.Get("settings.controls.invertMouse.tooltip"));
            toggleInvertMouse.AssignBinding(BasisSettingsDefaults.InvertMouse);

            PanelSlider mousesensitivty = PanelSlider.CreateEntryAndBind(
                group,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.controls.mouseSensitivity"), 0, 2f, false, 2, ValueDisplayMode.Percentage),
                BasisSettingsDefaults.mousesensitivty);
            mousesensitivty.Descriptor.SetTooltip(BasisLocalization.Get("settings.controls.mouseSensitivity.tooltip"));

            PanelSlider scrollSpeedSlider = PanelSlider.CreateEntryAndBind(
                group,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.controls.scrollSpeed"), 20, 300, true, 0, ValueDisplayMode.Raw),
                BasisSettingsDefaults.ScrollSpeed);
            scrollSpeedSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.controls.scrollSpeed.tooltip"));

            PanelToggle snapturntoggle = PanelToggle.CreateNewEntry(group);
            snapturntoggle.Descriptor.SetTitle(BasisLocalization.Get("settings.controls.snapTurn"));
            snapturntoggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.controls.snapTurn.tooltip"));
            snapturntoggle.AssignBinding(BasisSettingsDefaults.usesnapturn);

            sliderSnapTurnAngleRef = PanelSlider.CreateEntryAndBind(
                group,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.controls.snapTurnAngle"), 0, 120, true, 0, ValueDisplayMode.Degrees),
                BasisSettingsDefaults.SnapTurnAngle);
            sliderSnapTurnAngleRef.Descriptor.SetTooltip(BasisLocalization.Get("settings.controls.snapTurnAngle.tooltip"));

            sliderSmoothTurnSpeedRef = PanelSlider.CreateEntryAndBind(
                group,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.controls.smoothTurnSpeed"), 50, 400, true, 0, ValueDisplayMode.Raw),
                BasisSettingsDefaults.SmoothTurnSpeed);
            sliderSmoothTurnSpeedRef.Descriptor.SetTooltip(BasisLocalization.Get("settings.controls.smoothTurnSpeed.tooltip"));

            snapturntoggle.OnValueChanged += isOn =>
            {
                sliderSnapTurnAngleRef.Descriptor.SetActive(isOn);
                sliderSmoothTurnSpeedRef.Descriptor.SetActive(!isOn);
                group.ForceRebuild();
            };
        }, startExpanded: false);

        // Apply initial visibility AFTER CreateCollapsibleSection's SetContentActive pass,
        // which would otherwise re-activate both sliders when the section starts expanded.
        bool snapOn = BasisSettingsDefaults.usesnapturn.RawValue;
        sliderSnapTurnAngleRef.Descriptor.SetActive(snapOn);
        sliderSmoothTurnSpeedRef.Descriptor.SetActive(!snapOn);

        // VR finger touch — direct fingertip presses on menus (BasisDirectTouch).
        // Tuning controls only show while the feature is enabled.
        List<PanelElementDescriptor> fingerTouchTuning = new List<PanelElementDescriptor>();
        void ApplyFingerTouchTuningVisibility()
        {
            bool tuningVisible = !BasisSettingsDefaults.DisableVRFingerTouch.RawValue;
            for (int i = 0; i < fingerTouchTuning.Count; i++)
            {
                fingerTouchTuning[i].SetActive(tuningVisible);
            }
        }
        SettingsProviderKeyboardBindings.CreateCollapsibleSection(
            container, BasisLocalization.Get("settings.general.fingerTouch.title"),
            BasisLocalization.Get("settings.general.disableVRFingerTouch.description"), group =>
        {
            PanelToggle toggleDisableVRFingerTouch = PanelToggle.CreateNewEntry(group);
            toggleDisableVRFingerTouch.AssignBinding(BasisSettingsDefaults.DisableVRFingerTouch);
            toggleDisableVRFingerTouch.Descriptor.SetTitle(BasisLocalization.Get("settings.general.disableVRFingerTouch"));
            toggleDisableVRFingerTouch.Descriptor.SetTooltip(BasisLocalization.Get("settings.general.disableVRFingerTouch.tooltip"));

            PanelDropdown dropdownTouchFinger = PanelDropdown.CreateNewEntry(group);
            dropdownTouchFinger.Descriptor.SetTitle(BasisLocalization.Get("settings.general.fingerTouch.finger"));
            dropdownTouchFinger.Descriptor.SetTooltip(BasisLocalization.Get("settings.general.fingerTouch.finger.tooltip"));
            dropdownTouchFinger.AssignLocalizedEntries(
                new List<string>
                {
                    BasisSettingsDefaults.FingerTouchFinger_Index,
                    BasisSettingsDefaults.FingerTouchFinger_Thumb,
                    BasisSettingsDefaults.FingerTouchFinger_Middle,
                    BasisSettingsDefaults.FingerTouchFinger_Ring,
                    BasisSettingsDefaults.FingerTouchFinger_Little,
                },
                new List<string>
                {
                    "settings.general.fingerTouch.finger.index",
                    "settings.general.fingerTouch.finger.thumb",
                    "settings.general.fingerTouch.finger.middle",
                    "settings.general.fingerTouch.finger.ring",
                    "settings.general.fingerTouch.finger.little",
                });
            dropdownTouchFinger.AssignBinding(BasisSettingsDefaults.FingerTouchFinger);
            fingerTouchTuning.Add(dropdownTouchFinger.Descriptor);

            PanelDropdown dropdownTouchHands = PanelDropdown.CreateNewEntry(group);
            dropdownTouchHands.Descriptor.SetTitle(BasisLocalization.Get("settings.general.fingerTouch.hands"));
            dropdownTouchHands.Descriptor.SetTooltip(BasisLocalization.Get("settings.general.fingerTouch.hands.tooltip"));
            dropdownTouchHands.AssignLocalizedEntries(
                new List<string>
                {
                    BasisSettingsDefaults.FingerTouchHands_Both,
                    BasisSettingsDefaults.FingerTouchHands_Left,
                    BasisSettingsDefaults.FingerTouchHands_Right,
                },
                new List<string>
                {
                    "settings.general.fingerTouch.hands.both",
                    "settings.general.fingerTouch.hands.left",
                    "settings.general.fingerTouch.hands.right",
                });
            dropdownTouchHands.AssignBinding(BasisSettingsDefaults.FingerTouchHands);
            fingerTouchTuning.Add(dropdownTouchHands.Descriptor);

            PanelSlider sliderTipOffset = PanelSlider.CreateEntryAndBind(
                group,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.general.fingerTouch.tipOffset"), 0f, 0.05f, false, 3, ValueDisplayMode.Meters),
                BasisSettingsDefaults.FingerTouchTipOffset);
            sliderTipOffset.Descriptor.SetTooltip(BasisLocalization.Get("settings.general.fingerTouch.tipOffset.tooltip"));
            fingerTouchTuning.Add(sliderTipOffset.Descriptor);

            PanelSlider sliderFingerLength = PanelSlider.CreateEntryAndBind(
                group,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.general.fingerTouch.fingerLength"), 0.02f, 0.3f, false, 3, ValueDisplayMode.Meters),
                BasisSettingsDefaults.FingerTouchFingerLength);
            sliderFingerLength.Descriptor.SetTooltip(BasisLocalization.Get("settings.general.fingerTouch.fingerLength.tooltip"));
            fingerTouchTuning.Add(sliderFingerLength.Descriptor);

            PanelSlider sliderTouchRadius = PanelSlider.CreateEntryAndBind(
                group,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.general.fingerTouch.radius"), 0.001f, 0.05f, false, 3, ValueDisplayMode.Meters),
                BasisSettingsDefaults.FingerTouchRadius);
            sliderTouchRadius.Descriptor.SetTooltip(BasisLocalization.Get("settings.general.fingerTouch.radius.tooltip"));
            fingerTouchTuning.Add(sliderTouchRadius.Descriptor);

            PanelSlider sliderHoverDistance = PanelSlider.CreateEntryAndBind(
                group,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.general.fingerTouch.hoverDistance"), 0.01f, 0.15f, false, 3, ValueDisplayMode.Meters),
                BasisSettingsDefaults.FingerTouchHoverDistance);
            sliderHoverDistance.Descriptor.SetTooltip(BasisLocalization.Get("settings.general.fingerTouch.hoverDistance.tooltip"));
            fingerTouchTuning.Add(sliderHoverDistance.Descriptor);

            PanelSlider sliderPressDepth = PanelSlider.CreateEntryAndBind(
                group,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.general.fingerTouch.pressDepth"), 0.002f, 0.05f, false, 3, ValueDisplayMode.Meters),
                BasisSettingsDefaults.FingerTouchPressDepth);
            sliderPressDepth.Descriptor.SetTooltip(BasisLocalization.Get("settings.general.fingerTouch.pressDepth.tooltip"));
            fingerTouchTuning.Add(sliderPressDepth.Descriptor);

            PanelSlider sliderReleaseDistance = PanelSlider.CreateEntryAndBind(
                group,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.general.fingerTouch.releaseDistance"), 0.005f, 0.08f, false, 3, ValueDisplayMode.Meters),
                BasisSettingsDefaults.FingerTouchReleaseDistance);
            sliderReleaseDistance.Descriptor.SetTooltip(BasisLocalization.Get("settings.general.fingerTouch.releaseDistance.tooltip"));
            fingerTouchTuning.Add(sliderReleaseDistance.Descriptor);

            PanelSlider sliderScrollSensitivity = PanelSlider.CreateEntryAndBind(
                group,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.general.fingerTouch.scrollSensitivity"), 100f, 2000f, true, 0, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FingerTouchScrollSensitivity);
            sliderScrollSensitivity.Descriptor.SetTooltip(BasisLocalization.Get("settings.general.fingerTouch.scrollSensitivity.tooltip"));
            fingerTouchTuning.Add(sliderScrollSensitivity.Descriptor);

            PanelToggle toggleFingerTouchHaptics = PanelToggle.CreateNewEntry(group);
            toggleFingerTouchHaptics.AssignBinding(BasisSettingsDefaults.FingerTouchHaptics);
            toggleFingerTouchHaptics.Descriptor.SetTitle(BasisLocalization.Get("settings.general.fingerTouch.haptics"));
            toggleFingerTouchHaptics.Descriptor.SetTooltip(BasisLocalization.Get("settings.general.fingerTouch.haptics.tooltip"));
            fingerTouchTuning.Add(toggleFingerTouchHaptics.Descriptor);

            toggleDisableVRFingerTouch.OnValueChanged += _ =>
            {
                ApplyFingerTouchTuningVisibility();
                group.ForceRebuild();
            };
        });

        ApplyFingerTouchTuningVisibility();

        // VR UI Click
        SettingsProviderKeyboardBindings.CreateCollapsibleSection(
            container, BasisLocalization.Get("settings.controls.uiClick.title"),
            BasisLocalization.Get("settings.controls.uiClick.description"), group =>
        {
            PanelSlider clickPressSlider = PanelSlider.CreateEntryAndBind(
                group,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.controls.uiClickPress"), 0.05f, 1f, false, 2, ValueDisplayMode.percentageFromZero),
                BasisSettingsDefaults.UIClickPressThreshold);
            clickPressSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.controls.uiClickPress.tooltip"));

            PanelSlider clickReleaseSlider = PanelSlider.CreateEntryAndBind(
                group,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.controls.uiClickRelease"), 0.05f, 1f, false, 2, ValueDisplayMode.percentageFromZero),
                BasisSettingsDefaults.UIClickReleaseThreshold);
            clickReleaseSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.controls.uiClickRelease.tooltip"));

            void ClampReleaseToPress()
            {
                float press = BasisSettingsDefaults.UIClickPressThreshold.RawValue;
                if (BasisSettingsDefaults.UIClickReleaseThreshold.RawValue <= press)
                {
                    return;
                }
                BasisSettingsDefaults.UIClickReleaseThreshold.SetValue(press);
                clickReleaseSlider.SetValueWithoutNotify(press);
            }

            clickPressSlider.OnValueChanged += _ => ClampReleaseToPress();
            clickReleaseSlider.OnValueChanged += _ => ClampReleaseToPress();
        });

        // Deadzone - General
        SettingsProviderKeyboardBindings.CreateCollapsibleSection(
            container, BasisLocalization.Get("settings.controls.generalDeadzone.title"), BasisLocalization.Get("settings.controls.generalDeadzone.description"), group =>
        {
            PanelSlider controllerDeadZoneSlider = PanelSlider.CreateEntryAndBind(
                group,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.controls.radialDeadZone"), 0f, 1f, false, 3, ValueDisplayMode.Percentage),
                BasisSettingsDefaults.ControllerDeadZone);
            controllerDeadZoneSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.controls.radialDeadZone.tooltip"));
            controllerDeadZoneSlider.OnValueChanged += _ => UpdatePreview();
        });

        // Horizontal Comfort
        SettingsProviderKeyboardBindings.CreateCollapsibleSection(
            container, BasisLocalization.Get("settings.controls.yawComfort.title"),
            BasisLocalization.Get("settings.controls.yawComfort.description"), group =>
        {
            PanelSlider minHorizontalDeadZoneSlider = PanelSlider.CreateEntryAndBind(
                group,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.controls.xDeadZoneMin"), 0f, 1f, false, 3, ValueDisplayMode.Percentage),
                BasisSettingsDefaults.Basexdeadzone);
            minHorizontalDeadZoneSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.controls.xDeadZoneMin.tooltip"));

            PanelSlider horizontalGateStrengthSlider = PanelSlider.CreateEntryAndBind(
                group,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.controls.xGateFullY"), 0f, 1f, false, 3, ValueDisplayMode.Percentage),
                BasisSettingsDefaults.Extraxdeadzoneatfully);
            horizontalGateStrengthSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.controls.xGateFullY.tooltip"));

            PanelSlider wingCurveSlider = PanelSlider.CreateEntryAndBind(
                group,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.controls.gateCurve"), 0f, 3f, false, 3, ValueDisplayMode.Percentage),
                BasisSettingsDefaults.Wingexponent);
            wingCurveSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.controls.gateCurve.tooltip"));

            minHorizontalDeadZoneSlider.OnValueChanged += _ => UpdatePreview();
            horizontalGateStrengthSlider.OnValueChanged += _ => UpdatePreview();
            wingCurveSlider.OnValueChanged += _ => UpdatePreview();
        });

        // Vertical
        SettingsProviderKeyboardBindings.CreateCollapsibleSection(
            container, BasisLocalization.Get("settings.controls.pitchComfort.title"), BasisLocalization.Get("settings.controls.pitchComfort.description"), group =>
        {
            PanelSlider verticalDeadZoneSlider = PanelSlider.CreateEntryAndBind(
                group,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.controls.lookYDeadZone"), 0f, 1f, false, 3, ValueDisplayMode.Percentage),
                BasisSettingsDefaults.Ydeadzone);
            verticalDeadZoneSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.controls.lookYDeadZone.tooltip"));
            verticalDeadZoneSlider.OnValueChanged += _ => UpdatePreview();
        });

        // Keyboard Bindings & Remapping
        SettingsProviderKeyboardBindings.BuildKeyboardBindingsUI(tab);

        // Action Bindings
        SettingsProviderKeyboardBindings.CreateCollapsibleSection(
            container, BasisLocalization.Get("settings.controls.actionBindings.title", BasisDeviceManagement.StaticCurrentMode),
            BasisLocalization.Get("settings.controls.actionBindings.description"), group =>
        {
            BuildBindingsUI(group.ContentParent);
        });

        // Grid Snap (placement snapping for held objects)
        SettingsProviderKeyboardBindings.CreateCollapsibleSection(
            container, BasisLocalization.Get("settings.developer.gridSnap.title"),
            BasisLocalization.Get("settings.developer.gridSnap.description"), group =>
        {
            PanelToggle toggleForceGridSnap = PanelToggle.CreateNewEntry(group);
            toggleForceGridSnap.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.gridSnap.force"));
            toggleForceGridSnap.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.gridSnap.force.tooltip"));
            toggleForceGridSnap.AssignBinding(BasisSettingsDefaults.ForceGridSnap);

            PanelSlider sliderGridSnapSize = PanelSlider.CreateEntryAndBind(
                group,
                new PanelSlider.SliderSettings(
                    BasisLocalization.Get("settings.developer.gridSnap.size"),
                    BasisLocalization.Get("settings.developer.gridSnap.size.description"),
                    0.05f, 5f, false, 2, ValueDisplayMode.Meters),
                BasisSettingsDefaults.GridSnapSize);
            sliderGridSnapSize.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.gridSnap.size.tooltip"));

            PanelToggle toggleForceRotationSnap = PanelToggle.CreateNewEntry(group);
            toggleForceRotationSnap.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.gridSnap.forceRotation"));
            toggleForceRotationSnap.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.gridSnap.forceRotation.tooltip"));
            toggleForceRotationSnap.AssignBinding(BasisSettingsDefaults.ForceRotationSnap);

            PanelSlider sliderRotationSnapDegrees = PanelSlider.CreateEntryAndBind(
                group,
                new PanelSlider.SliderSettings(
                    BasisLocalization.Get("settings.developer.gridSnap.rotation"),
                    BasisLocalization.Get("settings.developer.gridSnap.rotation.description"),
                    1f, 90f, false, 1, ValueDisplayMode.Degrees),
                BasisSettingsDefaults.RotationSnapDegrees);
            sliderRotationSnapDegrees.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.gridSnap.rotation.tooltip"));
        });

        SettingsProvider.AddResetPageButton(container, "settings.tab.controls", () =>
        {
            ResetControlsDefaults();
            BasisActionDriver.ResetBindingsToDefaultsAsyncIgnored();
            if (BasisLocalInputActions.Asset != null)
            {
                SettingsProviderKeyboardBindings.ResetAllBindings(BasisLocalInputActions.Asset);
            }
        });

        descriptor.ForceRebuild();
        return tab;
    }

    private static void UpdatePreview()
    {
        // wire up to butterflygatepreview one day
    }

    private static void ResetControlsDefaults()
    {
        BasisSettingsDefaults.ForceGridSnap.ResetToDefault();
        BasisSettingsDefaults.GridSnapSize.ResetToDefault();
        BasisSettingsDefaults.ForceRotationSnap.ResetToDefault();
        BasisSettingsDefaults.RotationSnapDegrees.ResetToDefault();
        BasisSettingsDefaults.DominantHand.ResetToDefault();
        BasisSettingsDefaults.DesktopInputInVR.ResetToDefault();
        BasisSettingsDefaults.InvertMouse.ResetToDefault();
        BasisSettingsDefaults.mousesensitivty.ResetToDefault();
        BasisSettingsDefaults.usesnapturn.ResetToDefault();
        BasisSettingsDefaults.SnapTurnAngle.ResetToDefault();
        BasisSettingsDefaults.SmoothTurnSpeed.ResetToDefault();
        BasisSettingsDefaults.ScrollSpeed.ResetToDefault();
        BasisSettingsDefaults.ControllerDeadZone.ResetToDefault();
        BasisSettingsDefaults.Basexdeadzone.ResetToDefault();
        BasisSettingsDefaults.Extraxdeadzoneatfully.ResetToDefault();
        BasisSettingsDefaults.Wingexponent.ResetToDefault();
        BasisSettingsDefaults.Ydeadzone.ResetToDefault();
        BasisSettingsDefaults.DisableVRFingerTouch.ResetToDefault();
        BasisSettingsDefaults.FingerTouchFinger.ResetToDefault();
        BasisSettingsDefaults.FingerTouchHands.ResetToDefault();
        BasisSettingsDefaults.FingerTouchTipOffset.ResetToDefault();
        BasisSettingsDefaults.FingerTouchFingerLength.ResetToDefault();
        BasisSettingsDefaults.FingerTouchRadius.ResetToDefault();
        BasisSettingsDefaults.FingerTouchHoverDistance.ResetToDefault();
        BasisSettingsDefaults.FingerTouchPressDepth.ResetToDefault();
        BasisSettingsDefaults.FingerTouchReleaseDistance.ResetToDefault();
        BasisSettingsDefaults.FingerTouchScrollSensitivity.ResetToDefault();
        BasisSettingsDefaults.FingerTouchHaptics.ResetToDefault();
        BasisSettingsDefaults.UIClickPressThreshold.ResetToDefault();
        BasisSettingsDefaults.UIClickReleaseThreshold.ResetToDefault();
    }

    private static void BuildBindingsUI(RectTransform container)
    {
        var roles = (BasisBoneTrackedRole[])Enum.GetValues(typeof(BasisBoneTrackedRole));
        var roleNames = roles.Select(r => PrettyEnumName(r.ToString())).ToArray();

        var actions = ((ActionId[])Enum.GetValues(typeof(ActionId)))
            .Where(a => a != ActionId.Count)
            .ToArray();

        var actionNames = actions.Select(a => PrettyEnumName(a.ToString())).ToList();

        var selectorGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
        selectorGroup.SetTitle($"Select Action For {BasisDeviceManagement.StaticCurrentMode}");
        selectorGroup.SetDescription("Choose an action to edit its bound roles.");

        PanelDropdown actionDropdown = PanelDropdown.CreateNewEntry(selectorGroup.ContentParent);
        actionDropdown.Descriptor.SetTitle(BasisLocalization.Get("settings.controls.action"));
        actionDropdown.Descriptor.SetTooltip(BasisLocalization.Get("settings.controls.action.tooltip"));
        actionDropdown.AssignEntries(actionNames);

        var rolesGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
        rolesGroup.SetTitle(BasisLocalization.Get("settings.controls.roles"));

        var roleToggles = new PanelToggle[roles.Length];

        bool updatingUI = false;
        ActionId currentAction = actions.Length > 0 ? actions[0] : ActionId.Count;

        for (int i = 0; i < roles.Length; i++)
        {
            var role = roles[i];

            PanelToggle t = PanelToggle.CreateNewEntry(rolesGroup.ContentParent);
            t.Descriptor.SetTitle(roleNames[i]);

            t.OnValueChanged += async isOn =>
            {
                if (updatingUI)
                {
                    return;
                }

                if (isOn)
                {
                    BasisActionDriver.Bind(currentAction, role);
                }
                else
                {
                    BasisActionDriver.Unbind(currentAction, role);
                }

                await BasisActionDriver.SaveFromDriver();
            };

            roleToggles[i] = t;
        }

        actionDropdown.DropdownComponent.onValueChanged.AddListener(index =>
        {
            currentAction = actions[Mathf.Clamp(index, 0, actions.Length - 1)];
            RefreshRoleTogglesFromDriver(roles, roleToggles, ref updatingUI, currentAction);
        });

        RefreshRoleTogglesFromDriver(roles, roleToggles, ref updatingUI, currentAction);
    }

    private static void RefreshRoleTogglesFromDriver(
        BasisBoneTrackedRole[] roles,
        PanelToggle[] roleToggles,
        ref bool updatingUI,
        ActionId currentAction)
    {
        updatingUI = true;
        var bound = BasisActionDriver.GetBindings(currentAction);

        for (int i = 0; i < roles.Length; i++)
            roleToggles[i].SetValueWithoutNotify(bound.Contains(roles[i]));

        updatingUI = false;
    }

    private static string PrettyEnumName(string raw)
    {
        if (string.IsNullOrEmpty(raw)) { return raw; }
        var chars = new List<char>(raw.Length + 8);
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (i > 0 && char.IsUpper(c) && (char.IsLower(raw[i - 1]) || (i + 1 < raw.Length && char.IsLower(raw[i + 1]))))
            {
                chars.Add(' ');
            }
            chars.Add(c);
        }
        return new string(chars.ToArray());
    }
}
