using System;
using System.Collections.Generic;
using Basis.BasisUI;
using Basis.Scripts.Avatar;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Content-tag block list for the user-side filter. Sits beneath the performance
/// limits in the Settings tab and lets the user refuse content carrying any of a
/// curated preset list (18+, Horror, Gore, …) plus arbitrary custom tags. Mirror
/// of the SDK-side inspector authoring helper — same preset list, same string
/// matching, so a creator's "Gore" tag and a viewer's "Gore" block line up.
/// </summary>
public static class SettingsProviderContentTags
{
    /// <summary>
    /// Holds every PanelToggle representing a non-preset custom blocked tag, keyed
    /// by the tag string (case-insensitively, but stored using the user's original
    /// casing for display). Used so removing a custom tag can find and destroy its
    /// row without iterating siblings.
    /// </summary>
    private static readonly Dictionary<string, PanelToggle> _customRows =
        new Dictionary<string, PanelToggle>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Preset toggles, keyed by preset name. Holding references lets a custom-tag
    /// add of a preset name flip the existing toggle to On instead of creating a
    /// duplicate custom row.
    /// </summary>
    private static readonly Dictionary<string, PanelToggle> _presetRows =
        new Dictionary<string, PanelToggle>(StringComparer.OrdinalIgnoreCase);

    private static RectTransform _customListContainer;
    private static RectTransform _layoutRoot;

    public static void BuildContentTagsContent(RectTransform container)
    {
        _layoutRoot = container;
        _customRows.Clear();
        _presetRows.Clear();

        PanelElementDescriptor group =
            PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
        group.SetTitle(BasisLocalization.Get("settings.perf.contentTags.title"));
        group.SetDescription(BasisLocalization.Get("settings.perf.contentTags.description"));

        // Preset toggles: each one's checked state is "is this preset on the user's
        // blocklist". Order matches BasisContentTagPresets so the SDK inspector and
        // this panel display the same labels in the same order.
        for (int i = 0; i < BasisContentTagPresets.All.Length; i++)
        {
            string preset = BasisContentTagPresets.All[i];
            PanelToggle toggle = PanelToggle.CreateNewEntry(group.ContentParent);
            toggle.Descriptor.SetTitle(string.Format(BasisLocalization.Get("settings.perf.contentTags.blockFormat"), preset));
            toggle.SetValueWithoutNotify(IsBlocked(preset));
            toggle.OnValueChanged += on => OnPresetToggle(preset, on);
            _presetRows[preset] = toggle;
        }

        // Custom tags: dynamic list that grows when the user types a new tag and
        // shrinks when they toggle one off. Built into a sub-container so the add
        // row stays visually anchored beneath the dynamic list.
        PanelElementDescriptor customGroup =
            PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
        customGroup.SetTitle(BasisLocalization.Get("settings.perf.contentTags.custom.title"));
        customGroup.SetDescription(BasisLocalization.Get("settings.perf.contentTags.custom.description"));

        _customListContainer = customGroup.ContentParent;

        // Seed the custom list from the saved blocklist (anything that isn't a preset).
        string[] blocked = BasisContentTagFilter.GetBlockedTags();
        for (int i = 0; i < blocked.Length; i++)
        {
            string tag = blocked[i];
            if (IsPreset(tag)) continue;
            AddCustomRow(tag);
        }

        // Add row: text field + "Add" button. Submitting the field commits without
        // requiring a click. The button is here for users who don't realize Enter
        // works (and for non-keyboard input on standalone VR).
        PanelTextField input = PanelTextField.CreateNewEntry(customGroup.ContentParent);
        input.Descriptor.SetTitle(BasisLocalization.Get("settings.perf.contentTags.custom.addLabel"));
        input.OnValueChanged += value => CommitCustomEntry(input, value);

        PanelButton addButton = PanelButton.CreateNew(customGroup.ContentParent);
        addButton.Descriptor.SetTitle(BasisLocalization.Get("settings.perf.contentTags.custom.addButton"));
        addButton.OnClicked = () => CommitCustomEntry(input, input.Value);

        ForceLayout();
    }

    private static void OnPresetToggle(string preset, bool block)
    {
        // Reading the live binding here (rather than mutating a cached array) keeps
        // us correct if multiple toggle callbacks fire in the same frame — each
        // pulls the freshest list, applies its delta, writes back.
        string[] current = BasisContentTagFilter.GetBlockedTags();
        string[] next = block
            ? AppendIfMissing(current, preset)
            : RemoveTag(current, preset);
        if (!ReferenceEquals(current, next))
        {
            BasisContentTagFilter.SetBlockedTags(next);
        }
    }

    private static void CommitCustomEntry(PanelTextField input, string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        string trimmed = raw.Trim();

        // Typing a preset name in the custom field flips the preset toggle instead
        // of duplicating a row. Same end-state, no confusing double entry.
        if (IsPreset(trimmed))
        {
            if (_presetRows.TryGetValue(trimmed, out PanelToggle presetToggle))
            {
                presetToggle.SetValueWithoutNotify(true);
            }
            string[] currentForPreset = BasisContentTagFilter.GetBlockedTags();
            string[] nextForPreset = AppendIfMissing(currentForPreset, trimmed);
            if (!ReferenceEquals(currentForPreset, nextForPreset))
            {
                BasisContentTagFilter.SetBlockedTags(nextForPreset);
            }
            ClearInput(input);
            return;
        }

        // Already-blocked custom: no-op on storage, just clear the field so the
        // user gets visual feedback that the tag is in fact tracked.
        if (_customRows.ContainsKey(trimmed))
        {
            ClearInput(input);
            return;
        }

        string[] current = BasisContentTagFilter.GetBlockedTags();
        string[] next = AppendIfMissing(current, trimmed);
        BasisContentTagFilter.SetBlockedTags(next);

        AddCustomRow(trimmed);
        ClearInput(input);
        ForceLayout();
    }

    private static void AddCustomRow(string tag)
    {
        if (_customListContainer == null) return;

        PanelToggle row = PanelToggle.CreateNewEntry(_customListContainer);
        row.Descriptor.SetTitle(string.Format(BasisLocalization.Get("settings.perf.contentTags.blockFormat"), tag));
        row.SetValueWithoutNotify(true);
        row.OnValueChanged += on =>
        {
            if (on) return; // user re-toggled on; no-op
            string[] current = BasisContentTagFilter.GetBlockedTags();
            string[] next = RemoveTag(current, tag);
            if (!ReferenceEquals(current, next))
            {
                BasisContentTagFilter.SetBlockedTags(next);
            }
            _customRows.Remove(tag);
            if (row != null)
            {
                UnityEngine.Object.Destroy(row.gameObject);
            }
            ForceLayout();
        };
        _customRows[tag] = row;
    }

    private static void ClearInput(PanelTextField input)
    {
        if (input == null) return;
        input.SetValueWithoutNotify(string.Empty);
        if (input._inputField != null)
        {
            input._inputField.SetTextWithoutNotify(string.Empty);
        }
    }

    private static void ForceLayout()
    {
        if (_layoutRoot != null)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(_layoutRoot);
        }
    }

    private static bool IsPreset(string tag)
    {
        var presets = BasisContentTagPresets.All;
        for (int i = 0; i < presets.Length; i++)
        {
            if (string.Equals(presets[i], tag, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool IsBlocked(string tag)
    {
        string[] current = BasisContentTagFilter.GetBlockedTags();
        for (int i = 0; i < current.Length; i++)
        {
            if (string.Equals(current[i], tag, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string[] AppendIfMissing(string[] tags, string tag)
    {
        if (tags == null) return new[] { tag };
        for (int i = 0; i < tags.Length; i++)
        {
            if (string.Equals(tags[i], tag, StringComparison.OrdinalIgnoreCase)) return tags;
        }
        var next = new string[tags.Length + 1];
        Array.Copy(tags, next, tags.Length);
        next[tags.Length] = tag;
        return next;
    }

    private static string[] RemoveTag(string[] tags, string tag)
    {
        if (tags == null || tags.Length == 0) return tags ?? new string[0];
        int kept = 0;
        for (int i = 0; i < tags.Length; i++)
        {
            if (!string.Equals(tags[i], tag, StringComparison.OrdinalIgnoreCase)) kept++;
        }
        if (kept == tags.Length) return tags;
        var next = new string[kept];
        int j = 0;
        for (int i = 0; i < tags.Length; i++)
        {
            if (!string.Equals(tags[i], tag, StringComparison.OrdinalIgnoreCase))
            {
                next[j++] = tags[i];
            }
        }
        return next;
    }
}
