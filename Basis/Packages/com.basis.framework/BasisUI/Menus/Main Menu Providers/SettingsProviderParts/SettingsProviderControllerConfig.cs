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
        BuildBindingsUI(tab);

        descriptor.ForceRebuild();
        return tab;
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
