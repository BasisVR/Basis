using Basis.BasisUI;
using Basis.Scripts.Device_Management;
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

        // Gameplay & Input
        PanelElementDescriptor generalGroup =
            PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
        generalGroup.SetTitle("Gameplay & Input");
        generalGroup.SetDescription("General controls and comfort settings.");

        PanelDropdown dropdownDominantHand = PanelDropdown.CreateNewEntry(generalGroup);
        dropdownDominantHand.Descriptor.SetTitle("Dominant Hand");
        dropdownDominantHand.AssignEntries(new List<string> { BasisDominantHand.Right, BasisDominantHand.Left });
        dropdownDominantHand.AssignBinding(BasisSettingsDefaults.DominantHand);

        PanelToggle toggleInvertMouse = PanelToggle.CreateNewEntry(generalGroup);
        toggleInvertMouse.Descriptor.SetTitle("Invert Mouse");
        toggleInvertMouse.AssignBinding(BasisSettingsDefaults.InvertMouse);

        PanelSlider mousesensitivty = PanelSlider.CreateEntryAndBind(
            generalGroup,
            PanelSlider.SliderSettings.Advanced("Mouse Sensitivity", 0, 2f, false, 2, ValueDisplayMode.Percentage),
            BasisSettingsDefaults.mousesensitivty);

        PanelToggle smoothlocomotion = PanelToggle.CreateNewEntry(generalGroup);
        smoothlocomotion.Descriptor.SetTitle("Use Snap Turn Locomotion");
        smoothlocomotion.AssignBinding(BasisSettingsDefaults.usesnapturn);

        PanelSlider sliderSnapTurnAngle = PanelSlider.CreateEntryAndBind(
            generalGroup,
            PanelSlider.SliderSettings.Advanced("Snap Turn Angle", 0, 120, true, 0, ValueDisplayMode.Degrees),
            BasisSettingsDefaults.SnapTurnAngle);

        PanelSlider sliderSmoothTurnSpeed = PanelSlider.CreateEntryAndBind(
            generalGroup,
            PanelSlider.SliderSettings.Advanced("Smooth Turn Speed", 50, 400, true, 0, ValueDisplayMode.Raw),
            BasisSettingsDefaults.SmoothTurnSpeed);

        bool snapOn = BasisSettingsDefaults.usesnapturn.RawValue;
        sliderSnapTurnAngle.gameObject.SetActive(snapOn);
        sliderSmoothTurnSpeed.gameObject.SetActive(!snapOn);
        smoothlocomotion.OnValueChanged += isOn =>
        {
            sliderSnapTurnAngle.gameObject.SetActive(isOn);
            sliderSmoothTurnSpeed.gameObject.SetActive(!isOn);
        };

        // Deadzone - General
        PanelElementDescriptor generalGroupDeadZone =
            PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
        generalGroupDeadZone.SetTitle("General");
        generalGroupDeadZone.SetDescription("Basic filtering applied to the whole stick. (excluding look)");

        PanelSlider controllerDeadZoneSlider = PanelSlider.CreateEntryAndBind(
            generalGroupDeadZone,
            PanelSlider.SliderSettings.Advanced("Radial Dead Zone", 0f, 1f, false, 3, ValueDisplayMode.Percentage),
            BasisSettingsDefaults.ControllerDeadZone);

        // Horizontal Comfort
        PanelElementDescriptor horizontalGroup =
            PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
        horizontalGroup.SetTitle("Horizontal (Yaw) Comfort");
        horizontalGroup.SetDescription("Prevents forward/back stick pressure from causing accidental left/right drift (\"butterfly wings\").");

        PanelSlider minHorizontalDeadZoneSlider = PanelSlider.CreateEntryAndBind(
            horizontalGroup,
            PanelSlider.SliderSettings.Advanced("X Dead Zone (Min)", 0f, 1f, false, 3, ValueDisplayMode.Percentage),
            BasisSettingsDefaults.Basexdeadzone);

        PanelSlider horizontalGateStrengthSlider = PanelSlider.CreateEntryAndBind(
            horizontalGroup,
            PanelSlider.SliderSettings.Advanced("X Gate (At Full Y)", 0f, 1f, false, 3, ValueDisplayMode.Percentage),
            BasisSettingsDefaults.Extraxdeadzoneatfully);

        PanelSlider wingCurveSlider = PanelSlider.CreateEntryAndBind(
            horizontalGroup,
            PanelSlider.SliderSettings.Advanced("Gate Curve", 0f, 3f, false, 3, ValueDisplayMode.Percentage),
            BasisSettingsDefaults.Wingexponent);

        // Vertical
        PanelElementDescriptor verticalGroup =
            PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
        verticalGroup.SetTitle("Vertical (Pitch / Other)");
        verticalGroup.SetDescription("look joystick Y Dead Zone");

        PanelSlider verticalDeadZoneSlider = PanelSlider.CreateEntryAndBind(
            verticalGroup,
            PanelSlider.SliderSettings.Advanced("Look Y Dead Zone", 0f, 1f, false, 3, ValueDisplayMode.Percentage),
            BasisSettingsDefaults.Ydeadzone);

        controllerDeadZoneSlider.OnValueChanged += _ => UpdatePreview();
        minHorizontalDeadZoneSlider.OnValueChanged += _ => UpdatePreview();
        horizontalGateStrengthSlider.OnValueChanged += _ => UpdatePreview();
        verticalDeadZoneSlider.OnValueChanged += _ => UpdatePreview();
        wingCurveSlider.OnValueChanged += _ => UpdatePreview();

        // Action Bindings
        BuildBindingsUI(tab);

        SettingsProvider.AddResetPageButton(container, "Controls", () =>
        {
            ResetControlsDefaults();
            BasisActionDriver.ResetBindingsToDefaultsAsyncIgnored();
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
        BasisSettingsDefaults.DominantHand.ResetToDefault();
        BasisSettingsDefaults.InvertMouse.ResetToDefault();
        BasisSettingsDefaults.mousesensitivty.ResetToDefault();
        BasisSettingsDefaults.usesnapturn.ResetToDefault();
        BasisSettingsDefaults.SnapTurnAngle.ResetToDefault();
        BasisSettingsDefaults.SmoothTurnSpeed.ResetToDefault();
        BasisSettingsDefaults.ControllerDeadZone.ResetToDefault();
        BasisSettingsDefaults.Basexdeadzone.ResetToDefault();
        BasisSettingsDefaults.Extraxdeadzoneatfully.ResetToDefault();
        BasisSettingsDefaults.Wingexponent.ResetToDefault();
        BasisSettingsDefaults.Ydeadzone.ResetToDefault();
    }
    private static void BuildBindingsUI(PanelTabPage tab)
    {
        RectTransform container = tab.Descriptor.ContentParent;

        var roles = (BasisBoneTrackedRole[])Enum.GetValues(typeof(BasisBoneTrackedRole));
        var roleNames = roles.Select(r => PrettyEnumName(r.ToString())).ToArray();

        var actions = ((ActionId[])Enum.GetValues(typeof(ActionId)))
            .Where(a => a != ActionId.Count)
            .ToArray();

        var actionNames = actions.Select(a => PrettyEnumName(a.ToString())).ToList();

        var selectorGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
        selectorGroup.SetTitle($"Select Action For {BasisDeviceManagement.StaticCurrentMode}" );
        selectorGroup.SetDescription("Choose an action to edit its bound roles.");

        PanelDropdown actionDropdown = PanelDropdown.CreateNewEntry(selectorGroup.ContentParent);
        actionDropdown.Descriptor.SetTitle("Action");
        actionDropdown.AssignEntries(actionNames);

        var rolesGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
        rolesGroup.SetTitle("Roles");

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
