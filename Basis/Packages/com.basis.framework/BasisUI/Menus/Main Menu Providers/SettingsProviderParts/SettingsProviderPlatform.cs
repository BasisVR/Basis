using Basis.BasisUI;
using Basis.Scripts.Device_Management;
using System;
using System.Collections.Generic;
using UnityEngine;

public static class SettingsProviderPlatform
{
    /// <summary>
    /// All user-facing device modes to probe against registered BaseTypes.
    /// </summary>
    private static readonly string[] AllModes = new string[]
    {
        BasisConstants.Desktop,
        BasisConstants.OpenVRLoader,
        BasisConstants.OpenXRLoader,
    };

    public static void BuildDeviceModeUI(RectTransform container, Action<bool> onExpandedChanged = null)
    {
        string currentMode = BasisDeviceManagement.StaticCurrentMode ?? BasisConstants.None;

        // Current mode info
        PanelSectionToggle deviceModeToggle = PanelSectionToggle.CreateNewEntry(container);
        PanelElementDescriptor infoGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
            deviceModeToggle,
            container,
            BasisLocalization.Get("settings.platform.activeMode"),
            showGroupTitle: false);

        var currentModeField = PanelTextField.CreateNew(infoGroup.ContentParent);
        currentModeField.Descriptor.SetTitle(BasisLocalization.Get("settings.platform.deviceMode"));
        currentModeField.SetValue(currentMode);

        BasisDeviceManagement dm = BasisDeviceManagement.Instance;

#if BASIS_HAS_OPENVR || BASIS_HAS_OPENXR
        // Soft-swap status (only relevant when VR SDKs are compiled in)
        if (dm != null && dm.IsSoftSwapped)
        {
            var softSwapField = PanelTextField.CreateNew(infoGroup.ContentParent);
            softSwapField.Descriptor.SetTitle(BasisLocalization.Get("settings.platform.vrRuntime"));
            softSwapField.SetValue($"{dm.AutoSwapPreviousVRMode} (kept alive)");
        }

        // Offered next to the manual switch buttons because turning it off is what leaves those
        // buttons as the only way to change mode. Hidden where no SDK reports presence at all,
        // since the toggle would govern a signal that never arrives.
        if (BasisHMDPresence.HasPresenceProvider)
        {
            PanelToggle togglePresenceSensor = PanelToggle.CreateNewEntry(infoGroup.ContentParent);
            togglePresenceSensor.Descriptor.SetTitle(BasisLocalization.Get("settings.platform.usePresenceSensor"));
            togglePresenceSensor.Descriptor.SetTooltip(BasisLocalization.Get("settings.platform.usePresenceSensor.tooltip"));
            togglePresenceSensor.AssignBinding(BasisSettingsDefaults.UsePresenceSensor);
        }
#endif

        // Discover available modes from registered BaseTypes
        List<string> availableModes = new List<string>();
        if (dm != null)
        {
            foreach (string mode in AllModes)
            {
                if (dm.TryFindBasisBaseTypeManagement(mode, out _, OnlyFinding: true))
                {
                    availableModes.Add(mode);
                }
            }
        }

        if (availableModes.Count > 0)
        {
            foreach (string mode in availableModes)
            {
                string capturedMode = mode;
                string displayName = GetModeDisplayName(capturedMode);
                bool isActive = string.Equals(currentMode, capturedMode, System.StringComparison.Ordinal);
                string suffix = isActive ? " <color=green>[ACTIVE]</color>" : "";

                PanelButton modeButton = PanelButton.CreateNew(infoGroup.ContentParent);
                modeButton.Descriptor.SetTitle(BasisLocalization.Get("settings.platform.switchTo", displayName) + suffix);
                modeButton.Descriptor.SetTooltip(BasisLocalization.Get("settings.platform.switchTo.tooltip"));
                modeButton.Descriptor.SetDescription(GetModeDescription(capturedMode));

                // A VR runtime that is already up cannot be handed over to the other one inside
                // the session, so that entry is greyed out rather than left to fail on click.
                if (!isActive && !BasisDeviceManagement.CanEnterMode(capturedMode, out string blockedReason))
                {
                    modeButton.SetInteractable(false, blockedReason);
                }

                modeButton.OnClicked += () =>
                {
                    if (isActive) return;
                    BasisMainMenu.Instance.OpenDialogue(BasisLocalization.Get("settings.platform.switchTo", displayName),
                        BasisLocalization.Get("settings.platform.switchTo.confirm", displayName),
                        BasisLocalization.Get("settings.platform.switchTo", displayName),
                        BasisLocalization.Get("ui.cancel"),
                        async value =>
                        {
                            if (!value) return;
                            await BasisDeviceManagement.Instance.SwitchSetMode(capturedMode);
                        });
                };
            }
        }

        PanelSectionToggleHelpers.FinalizeCollapsibleGroup(deviceModeToggle, infoGroup, true, onExpandedChanged);
    }

    public static void BuildAutoSwapUI(RectTransform container)
    {
#if BASIS_HAS_OPENVR || BASIS_HAS_OPENXR
        PanelElementDescriptor autoSwapGroup =
            PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
        autoSwapGroup.SetTitle(BasisLocalization.Get("settings.platform.swapMode.title"));

        PanelDropdown dropdownSwapMode = PanelDropdown.CreateNewEntry(autoSwapGroup);
        dropdownSwapMode.Descriptor.SetTitle(BasisLocalization.Get("settings.platform.swapMode.title"));
        dropdownSwapMode.Descriptor.SetTooltip(BasisLocalization.Get("settings.platform.swapMode.title.tooltip"));
        dropdownSwapMode.AssignEntries(new System.Collections.Generic.List<string>
        {
            BasisSettingsDefaults.SwapMode_Shutdown,
            BasisSettingsDefaults.SwapMode_AutoSwap
        });
        dropdownSwapMode.AssignBinding(BasisSettingsDefaults.SwapMode);
#endif
    }

    private static string GetModeDisplayName(string mode) => BasisXRRuntimeNotice.ModeDisplayName(mode);

    private static string GetModeDescription(string mode)
    {
        if (mode == BasisConstants.Desktop) return "Desktop mode (no VR). Shortcut: F9";
        if (mode == BasisConstants.OpenVRLoader) return "SteamVR / OpenVR runtime. Shortcut: F11";
        if (mode == BasisConstants.OpenXRLoader) return "OpenXR runtime. Shortcut: F10";
        return mode;
    }
}
