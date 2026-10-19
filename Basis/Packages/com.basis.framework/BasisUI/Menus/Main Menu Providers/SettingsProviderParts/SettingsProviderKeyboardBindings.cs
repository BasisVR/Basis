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
        RebindSession session = new RebindSession();

        // One section for every key, each kind of key under its own heading.
        CreateCollapsibleSection(container, BasisLocalization.Get("settings.controls.keyboardBindings.title"), BasisLocalization.Get("settings.controls.keyboardBindings.description"), section =>
        {
            // Movement — split WASD vs Arrow Keys
            BuildSplitCompositeGroups(section, playerMap.FindAction("Move"),
                BasisLocalization.Get("settings.controls.keyboard.movementWasd"), BasisLocalization.Get("settings.controls.keyboard.movementWasd.description"),
                BasisLocalization.Get("settings.controls.keyboard.movementArrows"), BasisLocalization.Get("settings.controls.keyboard.movementArrows.description"),
                asset, session);

            // Vertical Movement
            BuildActionGroup(section, playerMap.FindAction("MoveLocalUpDown"), "fly",
                BasisLocalization.Get("settings.controls.keyboard.verticalMovement"), BasisLocalization.Get("settings.controls.keyboard.verticalMovement.description"), asset, session);

            // Actions
            BuildSimpleGroup(section, playerMap, asset, session, BasisLocalization.Get("settings.controls.keyboard.actions"), BasisLocalization.Get("settings.controls.keyboard.actions.description"),
                ("Jump", BasisLocalization.Get("settings.controls.keyboard.action.jump")),
                ("Running", BasisLocalization.Get("settings.controls.keyboard.action.sprint")),
                ("Crouch", BasisLocalization.Get("settings.controls.keyboard.action.crouch")),
                ("Prone", BasisLocalization.Get("settings.controls.keyboard.action.prone")),
                ("ToggleMicMute", BasisLocalization.Get("settings.controls.keyboard.action.microphoneInput")));

            // Camera
            BuildActionGroup(section, playerMap.FindAction("Look Delta"), "look",
                BasisLocalization.Get("settings.controls.keyboard.camera"), BasisLocalization.Get("settings.controls.keyboard.camera.description"), asset, session);

            // Menu
            BuildSimpleGroup(section, playerMap, asset, session, BasisLocalization.Get("settings.controls.keyboard.menu"), BasisLocalization.Get("settings.controls.keyboard.menu.description"),
                ("Tab", BasisLocalization.Get("settings.controls.keyboard.action.freeCursor")),
                ("OpenChat", BasisLocalization.Get("settings.controls.keyboard.action.openChat")));

            // Mode Switching
            BuildSimpleGroup(section, playerMap, asset, session, BasisLocalization.Get("settings.controls.keyboard.modeSwitching"), BasisLocalization.Get("settings.controls.keyboard.modeSwitching.description"),
                ("HotSwitchToDesktop", BasisLocalization.Get("settings.controls.keyboard.action.desktopMode")),
                ("HotSwtichToXR", BasisLocalization.Get("settings.controls.keyboard.action.xrMode")),
                ("HotSwtichToVR", BasisLocalization.Get("settings.controls.keyboard.action.vrMode")));
        });
    }

    private static void BuildSplitCompositeGroups(
        PanelElementDescriptor section, InputAction action,
        string primaryTitle, string primaryDesc,
        string altTitle, string altDesc,
        InputActionAsset asset, RebindSession session)
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
            PanelElementDescriptor group = CreateSubGroup(section, primaryTitle, primaryDesc);
            foreach (var (index, label) in primaryEntries)
                CreateBindingRow(group, action, index, label, asset, session);
        }

        if (altEntries.Count > 0)
        {
            PanelElementDescriptor group = CreateSubGroup(section, altTitle, altDesc);
            foreach (var (index, label) in altEntries)
                CreateBindingRow(group, action, index, label, asset, session);
        }
    }

    private static void BuildActionGroup(
        PanelElementDescriptor section, InputAction action, string actionKey,
        string groupTitle, string groupDesc,
        InputActionAsset asset, RebindSession session)
    {
        if (action == null) return;

        var entries = new List<(int index, string label)>();
        var partCounts = new Dictionary<string, int>();

        for (int i = 0; i < action.bindings.Count; i++)
        {
            InputBinding binding = action.bindings[i];
            if (binding.isComposite) continue;
            if (!IsKeyboardBinding(binding)) continue;

            string partSuffix = binding.isPartOfComposite ? GetPartSuffix(action.name, binding.name) : null;
            string baseLabel = partSuffix != null
                ? BasisLocalization.Get($"settings.controls.keyboard.action.{actionKey}.{partSuffix}")
                : binding.isPartOfComposite
                    ? $"{BasisLocalization.Get($"settings.controls.keyboard.action.{actionKey}")} {binding.name}"
                    : BasisLocalization.Get($"settings.controls.keyboard.action.{actionKey}");

            string partKey = binding.isPartOfComposite ? binding.name.ToLower() : "_solo";

            string label;
            if (partCounts.TryGetValue(partKey, out int count))
            {
                partCounts[partKey] = count + 1;
                label = BasisLocalization.Get("settings.controls.keyboard.alt", baseLabel);
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
            PanelElementDescriptor group = CreateSubGroup(section, groupTitle, groupDesc);
            foreach (var (index, label) in entries)
                CreateBindingRow(group, action, index, label, asset, session);
        }
    }

    private static void BuildSimpleGroup(
        PanelElementDescriptor section, InputActionMap playerMap, InputActionAsset asset, RebindSession session,
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
            PanelElementDescriptor group = CreateSubGroup(section, groupTitle, groupDesc);
            foreach (var (action, index, label) in entries)
                CreateBindingRow(group, action, index, label, asset, session);
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

    /// <summary>
    /// Titled, background-free heading inside a section, so related rows read as one block
    /// without becoming a section of their own.
    /// </summary>
    public static PanelElementDescriptor CreateSubGroup(PanelElementDescriptor section, string title, string description = null)
    {
        PanelElementDescriptor group = PanelElementDescriptor.CreateNew(
            PanelElementDescriptor.ElementStyles.Group, section.ContentParent);
        group.SetBackgroundVisible(false);
        group.SetTitle(title);
        if (!string.IsNullOrEmpty(description))
        {
            group.SetDescription(description);
        }
        return group;
    }

    private static string GetPartLabel(string actionName, string partName)
    {
        string suffix = GetPartSuffix(actionName, partName);
        return suffix != null ? BasisLocalization.Get("settings.controls.keyboard.move." + suffix) : partName;
    }

    private static string GetPartSuffix(string actionName, string partName)
    {
        string norm = partName.ToLower();
        if (actionName == "Move")
        {
            return norm switch
            {
                "up" => "forward",
                "down" => "backward",
                "left" => "left",
                "right" => "right",
                _ => null
            };
        }
        return norm switch
        {
            "up" => "up",
            "down" => "down",
            "left" => "left",
            "right" => "right",
            "positive" => "up",
            "negative" => "down",
            _ => null
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
        return $"{label} \u2014 [{currentKey}] {BasisLocalization.Get("settings.controls.keyboard.defaultKey", defaultKey)}";
    }

    #endregion

    #region Binding Row & Rebind

    private sealed class RebindSession
    {
        public Action Cancel;
    }

    private sealed class RebindGuard : MonoBehaviour
    {
        public Action Lost;

        private void OnDisable()
        {
            Action lost = Lost;
            Lost = null;
            lost?.Invoke();
        }
    }

    private static void CreateBindingRow(PanelElementDescriptor parent, InputAction action,
        int bindingIndex, string label, InputActionAsset asset, RebindSession session)
    {
        InputBinding binding = action.bindings[bindingIndex];
        string defaultKey = ReadableKey(binding.path);
        string currentKey = ReadableKey(binding.effectivePath);

        PanelButton button = PanelButton.CreateNew(parent.ContentParent);
        button.Descriptor.SetTitle(FormatTitle(label, defaultKey, currentKey));

        button.OnClicked += () => StartRebind(button, action, bindingIndex, label, defaultKey, asset, session);
    }

    private static void StartRebind(PanelButton button, InputAction action,
        int bindingIndex, string label, string defaultKey, InputActionAsset asset, RebindSession session)
    {
        session.Cancel?.Invoke();
        if (button == null || button.IsReleased)
        {
            return;
        }

        button.Descriptor.SetTitle($"{label} \u2014 {BasisLocalization.Get("settings.controls.keyboard.pressKey")}");

        if (!button.TryGetComponent(out RebindGuard guard))
        {
            guard = button.gameObject.AddComponent<RebindGuard>();
        }

        bool wasEnabled = action.enabled;
        bool finished = false;
        string rejected = null;
        InputActionRebindingExtensions.RebindingOperation operation = null;
        Action cancel = () => operation?.Cancel();

        void Finish()
        {
            if (finished)
            {
                return;
            }
            finished = true;
            if (session.Cancel == cancel)
            {
                session.Cancel = null;
            }
            if (guard != null)
            {
                guard.Lost = null;
            }
            if (button != null)
            {
                button.OnInstanceReleased -= cancel;
            }
            if (wasEnabled)
            {
                action.Enable();
            }
            operation.Dispose();
        }

        void ShowKey(string note)
        {
            if (button == null || button.IsReleased || button.Descriptor == null)
            {
                return;
            }
            string title = FormatTitle(label, defaultKey, ReadableKey(action.bindings[bindingIndex].effectivePath));
            button.Descriptor.SetTitle(note == null ? title : $"{title} {note}");
        }

        action.Disable();

        operation = action.PerformInteractiveRebinding(bindingIndex)
            .WithControlsExcluding("<Mouse>")
            .WithControlsExcluding("<Pointer>")
            .WithControlsExcluding("<Touchscreen>")
            .WithCancelingThrough("<Keyboard>/escape")
            .OnApplyBinding((_, path) =>
            {
                if (IsBoundElsewhere(asset, action, bindingIndex, path))
                {
                    rejected = path;
                    return;
                }
                action.ApplyBindingOverride(bindingIndex, path);
            })
            .OnComplete(_ =>
            {
                Finish();
                if (rejected != null)
                {
                    ShowKey(BasisLocalization.Get("settings.controls.keyboard.keyInUse", ReadableKey(rejected)));
                    return;
                }
                ShowKey(null);
                SaveBindingOverrides(asset);
            })
            .OnCancel(_ =>
            {
                Finish();
                ShowKey(null);
            });

        session.Cancel = cancel;
        guard.Lost = cancel;
        button.OnInstanceReleased += cancel;
        operation.Start();
    }

    internal static bool IsBoundElsewhere(InputActionAsset asset, InputAction action, int bindingIndex, string path)
    {
        if (asset == null || string.IsNullOrEmpty(path))
        {
            return false;
        }

        bool restoringDefault = string.Equals(path, action.bindings[bindingIndex].path, StringComparison.OrdinalIgnoreCase);
        foreach (InputActionMap map in asset.actionMaps)
        {
            foreach (InputAction other in map.actions)
            {
                for (int i = 0; i < other.bindings.Count; i++)
                {
                    if (other == action && i == bindingIndex)
                    {
                        continue;
                    }

                    InputBinding binding = other.bindings[i];
                    if (binding.isComposite || !string.Equals(binding.effectivePath, path, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (restoringDefault && string.Equals(binding.path, path, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    return true;
                }
            }
        }
        return false;
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
