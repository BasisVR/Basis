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
        RebindSession session = new RebindSession(asset);

        // One section for every key, each kind of key under its own heading.
        CreateCollapsibleSection(container, BasisLocalization.Get("settings.controls.keyboardBindings.title"), BasisLocalization.Get("settings.controls.keyboardBindings.description"), section =>
        {
            // Movement — split WASD vs Arrow Keys
            BuildSplitCompositeGroups(section, playerMap.FindAction("Move"),
                BasisLocalization.Get("settings.controls.keyboard.movementWasd"), BasisLocalization.Get("settings.controls.keyboard.movementWasd.description"),
                BasisLocalization.Get("settings.controls.keyboard.movementArrows"), BasisLocalization.Get("settings.controls.keyboard.movementArrows.description"),
                session);

            // Vertical Movement
            BuildActionGroup(section, playerMap.FindAction("MoveLocalUpDown"), "fly",
                BasisLocalization.Get("settings.controls.keyboard.verticalMovement"), BasisLocalization.Get("settings.controls.keyboard.verticalMovement.description"), session);

            // Actions
            BuildSimpleGroup(section, playerMap, session, BasisLocalization.Get("settings.controls.keyboard.actions"), BasisLocalization.Get("settings.controls.keyboard.actions.description"),
                ("Jump", BasisLocalization.Get("settings.controls.keyboard.action.jump")),
                ("Running", BasisLocalization.Get("settings.controls.keyboard.action.sprint")),
                ("Crouch", BasisLocalization.Get("settings.controls.keyboard.action.crouch")),
                ("Prone", BasisLocalization.Get("settings.controls.keyboard.action.prone")),
                ("ToggleMicMute", BasisLocalization.Get("settings.controls.keyboard.action.microphoneInput")));

            // Camera
            BuildActionGroup(section, playerMap.FindAction("Look Delta"), "look",
                BasisLocalization.Get("settings.controls.keyboard.camera"), BasisLocalization.Get("settings.controls.keyboard.camera.description"), session);

            // Menu
            BuildSimpleGroup(section, playerMap, session, BasisLocalization.Get("settings.controls.keyboard.menu"), BasisLocalization.Get("settings.controls.keyboard.menu.description"),
                ("Tab", BasisLocalization.Get("settings.controls.keyboard.action.freeCursor")),
                ("OpenChat", BasisLocalization.Get("settings.controls.keyboard.action.openChat")));

            // Mode Switching
            BuildSimpleGroup(section, playerMap, session, BasisLocalization.Get("settings.controls.keyboard.modeSwitching"), BasisLocalization.Get("settings.controls.keyboard.modeSwitching.description"),
                ("HotSwitchToDesktop", BasisLocalization.Get("settings.controls.keyboard.action.desktopMode")),
                ("HotSwtichToXR", BasisLocalization.Get("settings.controls.keyboard.action.xrMode")),
                ("HotSwtichToVR", BasisLocalization.Get("settings.controls.keyboard.action.vrMode")));

            CreateResetAllButton(section, session);
            session.RefreshAll();
            BindingOverridesChanged += session.RefreshAll;
        });
    }

    private static void BuildSplitCompositeGroups(
        PanelElementDescriptor section, InputAction action,
        string primaryTitle, string primaryDesc,
        string altTitle, string altDesc,
        RebindSession session)
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
                CreateBindingRow(group, action, index, label, session);
        }

        if (altEntries.Count > 0)
        {
            PanelElementDescriptor group = CreateSubGroup(section, altTitle, altDesc);
            foreach (var (index, label) in altEntries)
                CreateBindingRow(group, action, index, label, session);
        }
    }

    private static void BuildActionGroup(
        PanelElementDescriptor section, InputAction action, string actionKey,
        string groupTitle, string groupDesc,
        RebindSession session)
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
                CreateBindingRow(group, action, index, label, session);
        }
    }

    private static void BuildSimpleGroup(
        PanelElementDescriptor section, InputActionMap playerMap, RebindSession session,
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
                CreateBindingRow(group, action, index, label, session);
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

    private sealed class BindingRow
    {
        public PanelButton Button;
        public InputAction Action;
        public int Index;
        public string Label;
        public string DefaultKey;

        public bool IsGone => Button == null || Button.IsReleased || Button.Descriptor == null;

        public bool IsOverridden
        {
            get
            {
                InputBinding binding = Action.bindings[Index];
                return !string.IsNullOrEmpty(binding.overridePath)
                    && !string.Equals(binding.overridePath, binding.path, StringComparison.OrdinalIgnoreCase);
            }
        }

        public string CurrentKey => ReadableKey(Action.bindings[Index].effectivePath);
    }

    private sealed class RebindSession
    {
        public readonly InputActionAsset Asset;
        public readonly List<BindingRow> Rows = new List<BindingRow>();
        public PanelButton ResetAllButton;
        public BindingRow Active;
        public Action Cancel;

        public RebindSession(InputActionAsset asset)
        {
            Asset = asset;
        }

        public string LabelFor(InputAction action, int bindingIndex)
        {
            for (int i = 0; i < Rows.Count; i++)
            {
                if (Rows[i].Action == action && Rows[i].Index == bindingIndex)
                {
                    return Rows[i].Label;
                }
            }
            InputBinding binding = action.bindings[bindingIndex];
            return binding.isPartOfComposite ? $"{action.name} {binding.name}" : action.name;
        }

        public void RefreshAll()
        {
            if (ResetAllButton == null || ResetAllButton.IsReleased)
            {
                BindingOverridesChanged -= RefreshAll;
                return;
            }

            for (int i = 0; i < Rows.Count; i++)
            {
                if (Rows[i] != Active)
                {
                    ShowRow(Rows[i], null);
                }
            }

            bool any = HasAnyOverride(Asset);
            ResetAllButton.SetInteractable(any, any ? null : BasisLocalization.Get("settings.controls.keyboard.resetAll.none"));
        }

        public void ShowRow(BindingRow row, string note)
        {
            if (row.IsGone)
            {
                return;
            }

            string title = FormatTitle(row.Label, row.DefaultKey, row.CurrentKey);
            row.Button.Descriptor.SetTitle(note == null ? title : $"{title} {note}");

            // Only a moved key offers itself back, so the reset gesture and the section reset
            // never put back something that is already at its default.
            row.Button.OnResetRequested = row.IsOverridden ? () => RequestRowReset(this, row) : null;
        }

        public string ConflictNote(string path, InputAction owner, int ownerIndex)
        {
            return BasisLocalization.Get("settings.controls.keyboard.keyInUseBy", ReadableKey(path), LabelFor(owner, ownerIndex));
        }
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
        int bindingIndex, string label, RebindSession session)
    {
        BindingRow row = new BindingRow
        {
            Action = action,
            Index = bindingIndex,
            Label = label,
            DefaultKey = ReadableKey(action.bindings[bindingIndex].path),
        };

        PanelButton button = PanelButton.CreateNew(parent.ContentParent);
        button.Descriptor.SetTooltip(BasisLocalization.Get("settings.controls.keyboard.row.tooltip"));
        row.Button = button;
        session.Rows.Add(row);
        session.ShowRow(row, null);

        button.OnClicked += () => StartRebind(row, session);
        button.OnApplyResetToDefault = () =>
        {
            if (!row.IsOverridden)
            {
                return;
            }
            // A section or page reset puts every row back, so a key another row still holds for
            // a moment is not a conflict here.
            row.Action.RemoveBindingOverride(row.Index);
            SaveBindingOverrides(session.Asset);
        };
        button.DescribeSettingChange = (out string changeLabel, out string current, out string standard) =>
        {
            changeLabel = row.Label;
            current = row.CurrentKey;
            standard = row.DefaultKey;
            return row.IsOverridden;
        };
    }

    private static void CreateResetAllButton(PanelElementDescriptor section, RebindSession session)
    {
        PanelButton button = PanelButton.CreateNew(section.ContentParent);
        button.Descriptor.SetTitle(BasisLocalization.Get("settings.controls.keyboard.resetAll"));
        button.Descriptor.SetTooltip(BasisLocalization.Get("settings.controls.keyboard.resetAll.tooltip"));
        session.ResetAllButton = button;
        button.OnInstanceReleased += () => BindingOverridesChanged -= session.RefreshAll;
        button.OnClicked += () =>
        {
            session.Cancel?.Invoke();
            if (!HasAnyOverride(session.Asset))
            {
                return;
            }

            List<BasisMenuDialoguePanel.DetailRow> changes = new List<BasisMenuDialoguePanel.DetailRow>();
            string entryFormat = BasisLocalization.Get("menu.panel.reset.changed.entry");
            for (int i = 0; i < session.Rows.Count; i++)
            {
                BindingRow row = session.Rows[i];
                if (row.IsOverridden)
                {
                    changes.Add(new BasisMenuDialoguePanel.DetailRow(row.Label, string.Format(entryFormat, row.CurrentKey, row.DefaultKey)));
                }
            }

            string sectionTitle = BasisLocalization.Get("settings.controls.keyboardBindings.title");
            Confirm(BasisLocalization.Get("ui.resetPage.title", sectionTitle),
                BasisLocalization.Get("ui.resetPage.confirm", sectionTitle),
                changes,
                () => ResetAllBindings(session.Asset));
        };
    }

    private static void RequestRowReset(RebindSession session, BindingRow row)
    {
        session.Cancel?.Invoke();
        if (row.IsGone || !row.IsOverridden)
        {
            return;
        }

        Confirm(BasisLocalization.Get("ui.panelOptions.title"),
            BasisLocalization.Get("settings.controls.keyboard.resetKey.body", row.Label, row.DefaultKey),
            null,
            () =>
            {
                if (row.IsGone || !row.IsOverridden)
                {
                    return;
                }
                string defaultPath = row.Action.bindings[row.Index].path;
                if (TryFindConflict(session.Asset, row.Action, row.Index, defaultPath, out InputAction owner, out int ownerIndex))
                {
                    session.ShowRow(row, session.ConflictNote(defaultPath, owner, ownerIndex));
                    return;
                }
                row.Action.RemoveBindingOverride(row.Index);
                SaveBindingOverrides(session.Asset);
            });
    }

    private static void Confirm(string title, string body, List<BasisMenuDialoguePanel.DetailRow> details, Action apply)
    {
        BasisMenuBase<BasisMainMenu> menu = BasisMenuBase<BasisMainMenu>.Instance;
        if (menu == null)
        {
            apply();
            return;
        }

        if (menu.Dialogue != null)
        {
            return;
        }

        menu.OpenDialogue(title, body, BasisLocalization.Get("ui.reset"), BasisLocalization.Get("ui.cancel"), confirmed =>
        {
            if (confirmed)
            {
                apply();
            }
        });

        if (details != null && details.Count > 0 && menu.Dialogue != null)
        {
            menu.Dialogue.ShowDetails(details);
        }
    }

    private static void StartRebind(BindingRow row, RebindSession session)
    {
        // A second click on the row that is listening backs out instead of starting over.
        bool wasListening = session.Active == row;
        session.Cancel?.Invoke();
        if (wasListening || row.IsGone)
        {
            return;
        }

        PanelButton button = row.Button;
        InputAction action = row.Action;
        int bindingIndex = row.Index;
        InputActionAsset asset = session.Asset;

        button.Descriptor.SetTitle($"{row.Label} — {BasisLocalization.Get("settings.controls.keyboard.pressKey")}");

        if (!button.TryGetComponent(out RebindGuard guard))
        {
            guard = button.gameObject.AddComponent<RebindGuard>();
        }

        bool wasEnabled = action.enabled;
        bool finished = false;
        string rejectedNote = null;
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
            if (session.Active == row)
            {
                session.Active = null;
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

        action.Disable();

        operation = action.PerformInteractiveRebinding(bindingIndex)
            .WithControlsHavingToMatchPath("<Keyboard>")
            .WithControlsExcluding("<Keyboard>/anyKey")
            .WithCancelingThrough("<Keyboard>/escape")
            .OnApplyBinding((_, path) =>
            {
                if (TryFindConflict(asset, action, bindingIndex, path, out InputAction owner, out int ownerIndex))
                {
                    rejectedNote = session.ConflictNote(path, owner, ownerIndex);
                    return;
                }
                action.ApplyBindingOverride(bindingIndex, path);
            })
            .OnComplete(_ =>
            {
                Finish();
                if (rejectedNote != null)
                {
                    session.ShowRow(row, rejectedNote);
                    return;
                }
                SaveBindingOverrides(asset);
            })
            .OnCancel(_ =>
            {
                Finish();
                session.ShowRow(row, null);
            });

        session.Active = row;
        session.Cancel = cancel;
        guard.Lost = cancel;
        button.OnInstanceReleased += cancel;
        operation.Start();
    }

    internal static bool IsBoundElsewhere(InputActionAsset asset, InputAction action, int bindingIndex, string path)
    {
        return TryFindConflict(asset, action, bindingIndex, path, out _, out _);
    }

    /// <summary>
    /// Finds the binding that already holds <paramref name="path"/>, so the row can say who has the
    /// key instead of only that it is taken. Keys that shipped shared stay shared when one of them
    /// is put back to its default.
    /// </summary>
    internal static bool TryFindConflict(InputActionAsset asset, InputAction action, int bindingIndex, string path,
        out InputAction owner, out int ownerIndex)
    {
        owner = null;
        ownerIndex = -1;
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

                    owner = other;
                    ownerIndex = i;
                    return true;
                }
            }
        }
        return false;
    }

    #endregion

    #region Save / Load / Reset

    /// <summary>Raised after the saved key overrides change, so every open bindings page redraws its rows.</summary>
    public static event Action BindingOverridesChanged;

    internal static bool HasAnyOverride(InputActionAsset asset)
    {
        if (asset == null)
        {
            return false;
        }
        foreach (InputActionMap map in asset.actionMaps)
        {
            foreach (InputBinding binding in map.bindings)
            {
                if (!string.IsNullOrEmpty(binding.overridePath)
                    && !string.Equals(binding.overridePath, binding.path, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        return false;
    }

    public static void SaveBindingOverrides(InputActionAsset asset)
    {
        string json = asset.SaveBindingOverridesAsJson();
        PlayerPrefs.SetString(BindingOverridesKey, json);
        PlayerPrefs.Save();
        BindingOverridesChanged?.Invoke();
    }

    public static void LoadBindingOverrides(InputActionAsset asset)
    {
        string json = PlayerPrefs.GetString(BindingOverridesKey, string.Empty);
        if (!string.IsNullOrEmpty(json))
        {
            asset.LoadBindingOverridesFromJson(json);
            BindingOverridesChanged?.Invoke();
        }
    }

    public static void ResetAllBindings(InputActionAsset asset)
    {
        asset.RemoveAllBindingOverrides();
        PlayerPrefs.DeleteKey(BindingOverridesKey);
        PlayerPrefs.Save();
        BindingOverridesChanged?.Invoke();
    }

    #endregion
}
