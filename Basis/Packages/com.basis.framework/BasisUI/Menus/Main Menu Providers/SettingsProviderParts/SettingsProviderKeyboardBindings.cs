using Basis.BasisUI;
using Basis.Scripts.Device_Management.Devices.Desktop;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

public static class SettingsProviderKeyboardBindings
{
    public const string BindingOverridesKey = "InputBindingOverrides";

    public static void BuildKeyboardBindingsUI(PanelTabPage tab)
    {
        InputActionAsset asset = BasisLocalInputActions.Asset;
        if (asset == null) return;

        InputActionMap playerMap = asset.FindActionMap("Player");
        if (playerMap == null) return;

        RectTransform container = tab.Descriptor.ContentParent;

        // Movement — split WASD vs Arrow Keys
        BuildSplitCompositeGroups(container, playerMap.FindAction("Move"),
            "Movement (WASD)", "Primary movement keys.",
            "Movement (Arrow Keys)", "Alternative movement keys.",
            asset);

        // Camera
        BuildActionGroup(container, playerMap.FindAction("Look Delta"), "Look",
            "Camera", "Keyboard camera controls.", asset);

        // Vertical Movement
        BuildActionGroup(container, playerMap.FindAction("MoveLocalUpDown"), "Fly",
            "Vertical Movement", "Fly up and down.", asset);

        // Actions
        BuildSimpleGroup(container, playerMap, asset, "Actions", "Common gameplay actions.",
            ("Jump", "Jump"),
            ("Running", "Sprint"),
            ("Crouch", "Crouch"),
            ("ToggleMicMute", "Microphone Input"));

        // Menu
        BuildSimpleGroup(container, playerMap, asset, "Menu", "Menu and UI shortcuts.",
            ("Tab", "Free Cursor"),
            ("OpenChat", "Open Chat"));

        // Mode Switching
        BuildSimpleGroup(container, playerMap, asset, "Mode Switching", "Switch input mode.",
            ("HotSwitchToDesktop", "Desktop Mode"),
            ("HotSwtichToXR", "XR Mode"),
            ("HotSwtichToVR", "VR Mode"));
    }

    private static void BuildSplitCompositeGroups(
        RectTransform container, InputAction action,
        string primaryTitle, string primaryDesc,
        string altTitle, string altDesc,
        InputActionAsset asset)
    {
        if (action == null) return;

        var primaryEntries = new List<(int index, string label)>();
        var altEntries = new List<(int index, string label)>();
        var partCounts = new Dictionary<string, int>();

        for (int i = 0; i < action.bindings.Count; i++)
        {
            InputBinding binding = action.bindings[i];
            if (binding.isComposite) { partCounts.Clear(); continue; }
            if (!IsKeyboardBinding(binding)) continue;

            string label = GetPartLabel(action.name, binding.name);
            string partKey = binding.name.ToLower();

            if (partCounts.TryGetValue(partKey, out int count))
            {
                partCounts[partKey] = count + 1;
                altEntries.Add((i, label));
            }
            else
            {
                partCounts[partKey] = 1;
                primaryEntries.Add((i, label));
            }
        }

        if (primaryEntries.Count > 0)
        {
            CreateCollapsibleSection(container, primaryTitle, primaryDesc, group =>
            {
                foreach (var (index, label) in primaryEntries)
                    CreateBindingRow(group, action, index, label, asset);
            });
        }

        if (altEntries.Count > 0)
        {
            CreateCollapsibleSection(container, altTitle, altDesc, group =>
            {
                foreach (var (index, label) in altEntries)
                    CreateBindingRow(group, action, index, label, asset);
            });
        }
    }

    private static void BuildActionGroup(
        RectTransform container, InputAction action, string actionDesc,
        string groupTitle, string groupDesc,
        InputActionAsset asset)
    {
        if (action == null) return;

        var entries = new List<(int index, string label)>();
        var partCounts = new Dictionary<string, int>();

        for (int i = 0; i < action.bindings.Count; i++)
        {
            InputBinding binding = action.bindings[i];
            if (binding.isComposite) continue;
            if (!IsKeyboardBinding(binding)) continue;

            string baseLabel;
            if (binding.isPartOfComposite)
                baseLabel = $"{actionDesc} {GetPartLabel(action.name, binding.name)}";
            else
                baseLabel = actionDesc;

            string partKey = binding.isPartOfComposite ? binding.name.ToLower() : "_solo";

            string label;
            if (partCounts.TryGetValue(partKey, out int count))
            {
                partCounts[partKey] = count + 1;
                label = $"{baseLabel} (Alt)";
            }
            else
            {
                partCounts[partKey] = 1;
                label = baseLabel;
            }

            entries.Add((i, label));
        }

        if (entries.Count > 0)
        {
            CreateCollapsibleSection(container, groupTitle, groupDesc, group =>
            {
                foreach (var (index, label) in entries)
                    CreateBindingRow(group, action, index, label, asset);
            });
        }
    }

    private static void BuildSimpleGroup(
        RectTransform container, InputActionMap playerMap, InputActionAsset asset,
        string groupTitle, string groupDesc,
        params (string actionName, string label)[] actions)
    {
        var entries = new List<(InputAction action, int index, string label)>();

        foreach (var (actionName, label) in actions)
        {
            InputAction action = playerMap.FindAction(actionName);
            if (action == null) continue;

            for (int i = 0; i < action.bindings.Count; i++)
            {
                InputBinding binding = action.bindings[i];
                if (binding.isComposite || binding.isPartOfComposite) continue;
                if (!IsKeyboardBinding(binding)) continue;
                entries.Add((action, i, label));
            }
        }

        if (entries.Count > 0)
        {
            CreateCollapsibleSection(container, groupTitle, groupDesc, group =>
            {
                foreach (var (action, index, label) in entries)
                    CreateBindingRow(group, action, index, label, asset);
            });
        }
    }

    #region Helpers

    private static bool IsKeyboardBinding(InputBinding binding)
    {
        return !string.IsNullOrEmpty(binding.path) && binding.path.StartsWith("<Keyboard>/");
    }

    /// <summary>
    /// Creates a collapsible section with a toggle header.
    /// Content is built lazily on first expand so it always initializes on an active group.
    /// </summary>
    public static void CreateCollapsibleSection(
        RectTransform container, string title, string description,
        Action<PanelElementDescriptor> buildContent, bool startExpanded = false)
    {
        PanelSectionToggle sectionToggle = PanelSectionToggle.CreateNewEntry(container);
        PanelElementDescriptor group = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
            sectionToggle, container, title, false);
        group.SetBackgroundVisible(false);

        if (!string.IsNullOrEmpty(description))
        {
            PanelElementDescriptor descGroup = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, group.ContentParent);
            descGroup.SetDescription(description);
        }

        buildContent(group);

        PanelSectionToggleHelpers.FinalizeCollapsibleGroup(sectionToggle, group, startExpanded, _ =>
        {
            group.ForceRebuild();
        });
    }

    private static string GetPartLabel(string actionName, string partName)
    {
        string norm = partName.ToLower();
        if (actionName == "Move")
        {
            return norm switch
            {
                "up" => "Forward",
                "down" => "Backward",
                "left" => "Left",
                "right" => "Right",
                _ => partName
            };
        }
        return norm switch
        {
            "up" => "Up",
            "down" => "Down",
            "left" => "Left",
            "right" => "Right",
            "positive" => "Up",
            "negative" => "Down",
            _ => partName
        };
    }

    private static string ReadableKey(string path)
    {
        return InputControlPath.ToHumanReadableString(path,
            InputControlPath.HumanReadableStringOptions.OmitDevice);
    }

    private static string FormatTitle(string label, string defaultKey, string currentKey)
    {
        if (defaultKey == currentKey)
            return $"{label} \u2014 [{currentKey}]";
        return $"{label} \u2014 [{currentKey}] (default {defaultKey})";
    }

    #endregion

    #region Binding Row & Rebind

    private static void CreateBindingRow(PanelElementDescriptor parent, InputAction action,
        int bindingIndex, string label, InputActionAsset asset)
    {
        InputBinding binding = action.bindings[bindingIndex];
        string defaultKey = ReadableKey(binding.path);
        string currentKey = ReadableKey(binding.effectivePath);

        PanelButton button = PanelButton.CreateNew(parent.ContentParent);
        button.Descriptor.SetTitle(FormatTitle(label, defaultKey, currentKey));

        button.OnClicked += () => StartRebind(button, action, bindingIndex, label, defaultKey, asset);
    }

    private static void StartRebind(PanelButton button, InputAction action,
        int bindingIndex, string label, string defaultKey, InputActionAsset asset)
    {
        button.Descriptor.SetTitle($"{label} \u2014 Press a key...");

        action.Disable();

        action.PerformInteractiveRebinding(bindingIndex)
            .WithControlsExcluding("<Mouse>")
            .WithControlsExcluding("<Pointer>")
            .WithControlsExcluding("<Touchscreen>")
            .WithCancelingThrough("<Keyboard>/escape")
            .OnComplete(operation =>
            {
                action.Enable();
                string newKey = ReadableKey(action.bindings[bindingIndex].effectivePath);
                button.Descriptor.SetTitle(FormatTitle(label, defaultKey, newKey));
                SaveBindingOverrides(asset);
                operation.Dispose();
            })
            .OnCancel(operation =>
            {
                action.Enable();
                string currentKey = ReadableKey(action.bindings[bindingIndex].effectivePath);
                button.Descriptor.SetTitle(FormatTitle(label, defaultKey, currentKey));
                operation.Dispose();
            })
            .Start();
    }

    #endregion

    #region Save / Load / Reset

    public static void SaveBindingOverrides(InputActionAsset asset)
    {
        string json = asset.SaveBindingOverridesAsJson();
        PlayerPrefs.SetString(BindingOverridesKey, json);
        PlayerPrefs.Save();
    }

    public static void LoadBindingOverrides(InputActionAsset asset)
    {
        string json = PlayerPrefs.GetString(BindingOverridesKey, string.Empty);
        if (!string.IsNullOrEmpty(json))
        {
            asset.LoadBindingOverridesFromJson(json);
        }
    }

    public static void ResetAllBindings(InputActionAsset asset)
    {
        asset.RemoveAllBindingOverrides();
        PlayerPrefs.DeleteKey(BindingOverridesKey);
        PlayerPrefs.Save();
    }

    #endregion
}
