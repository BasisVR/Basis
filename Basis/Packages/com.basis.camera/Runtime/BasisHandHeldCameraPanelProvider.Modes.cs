using System.Collections.Generic;
using UnityEngine;
using CameraSettings = BasisHandHeldCameraUI.CameraSettings;

namespace Basis.BasisUI.HandHeldCamera
{
    /// <summary>
    /// The mode picker and the settings readout.
    ///
    /// <para>The page also reads the whole settings file back as text, which is the only place in
    /// the panel where every value is visible at once.</para>
    /// </summary>
    public partial class BasisHandHeldCameraPanelProvider
    {
        /// <summary>
        /// Panel ticks between refreshes of the settings readout.
        ///
        /// <para>It needs the whole settings file harvested off the live camera, which is the same
        /// work a save does — cheap, but not free, and not worth doing sixty or ninety times a
        /// second to notice a slider moved. Four times a second is faster than anyone can read a
        /// value change.</para>
        /// </summary>
        private const int ModeCheckInterval = 15;

        private PanelDropdown _modeDropdown;

        // The dropdown's rows, in its own order: the value each one stores.
        private readonly List<string> _modeValues = new List<string>();

        /// <summary>What the page is currently showing, so an unchanged tick repaints nothing.</summary>
        private string _lastShownKey;

        private int _modeCheckCountdown;

        // The settings readout.
        private PanelSectionToggle _readoutSection;
        private PanelElementDescriptor _readoutGroup;
        private PanelElementDescriptor _readoutCard;

        /// <summary>
        /// Lines in the readout as it currently stands. The text is rewritten four times a second
        /// but its shape almost never moves — the same rows, with different numbers in them — so
        /// this is what separates "a value changed" from "the card is now a different height",
        /// which is the only one of the two that is worth reflowing the page for.
        /// </summary>
        private int _readoutLineCount = -1;

        /// <summary>
        /// This tab's own scroll content, held so a reflow can be aimed at it directly.
        ///
        /// <para>The readout is rewritten on the tick whichever tab is on screen, so it cannot use
        /// the panel's <c>ActivePageContent()</c> — from another tab that is a rect the readout is
        /// not inside, and walking out of a chain looking for it would rebuild every layout group
        /// between here and the canvas.</para>
        /// </summary>
        private RectTransform _modePageContent;

        /// <summary>
        /// The Mode tab: the picker, then the settings readout.
        ///
        /// <para>A page of its own rather than a row in the navigation column. That column is 350
        /// wide while the labelled dropdown prefab reserves 500 for its control alone, so the row
        /// overhung the column and its own label collapsed to nothing behind the control; moving the
        /// label to its own card fixed the width but left the picker past the bottom of a column
        /// that does not scroll.</para>
        ///
        /// <para>The picker sits loose above the readout because it is what the page is for, and
        /// everything below it is about the mode it names. The readout is collapsible like every
        /// other section in this panel: it is fifty rows long, and being able to fold it away is the
        /// difference between a reference you open when you want it and a wall the rest of the page
        /// sits underneath.</para>
        /// </summary>
        private void BuildModeTab(RectTransform parent)
        {
            _modePageContent = parent;

            _modeDropdown = PanelDropdown.CreateNewEntry(parent);
            _modeDropdown.Descriptor.SetTitle(BasisLocalization.Get("camera.modePreset"));
            BuildModeList();
            _modeDropdown.OnValueChanged = _ => OnModeSelected();

            _readoutSection = PanelSectionToggle.CreateNewEntry(parent);
            _readoutGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                _readoutSection, parent, BasisLocalization.Get("camera.userMode.readout"), false);
            BuildSettingsReadout(_readoutGroup.ContentParent);
            PanelSectionToggleHelpers.FinalizeCollapsibleGroup(
                _readoutSection, _readoutGroup, true, OnSectionExpanded);
        }

        /// <summary>
        /// Fills the dropdown with the built-in modes.
        ///
        /// <para>Values are the localization keys, never the text on the row:
        /// <see cref="PanelDropdown"/> resolves a selection by string-matching the entry, so two
        /// modes that translate alike would both resolve to the first match.</para>
        /// </summary>
        private void BuildModeList()
        {
            if (_modeDropdown == null) return;

            _modeValues.Clear();
            List<string> labels = new List<string>();

            for (int Index = 0; Index < BasisCameraModes.Ordered.Length; Index++)
            {
                BasisCameraModeDescriptor descriptor = BasisCameraModes.Get(BasisCameraModes.Ordered[Index]);
                _modeValues.Add(descriptor.TitleKey);
                labels.Add(BasisLocalization.Get(descriptor.TitleKey));
            }

            _modeDropdown.AssignEntries(new List<string>(_modeValues), labels);
            _lastShownKey = null;
        }

        private void OnModeSelected()
        {
            if (_activeCamera == null || _modeDropdown == null) return;

            int index = _modeDropdown.Index;
            if (index < 0 || index >= BasisCameraModes.Ordered.Length) return;

            BasisCameraMode mode = BasisCameraModes.Ordered[index];

            // Custom is a state the camera arrives at, not one it can be sent to: there is nothing
            // to apply. Picking it means "leave my settings alone", so put the dropdown back to
            // whatever the camera actually is and let the tick settle the label.
            if (mode == BasisCameraMode.Custom)
            {
                RefreshModeVisuals(force: true);
                return;
            }

            _activeCamera.ApplyCameraMode(mode);

            // A preset writes values the panel is already showing, so every control it touched is
            // now stale. Re-seed from the camera rather than from the preset — the camera is what
            // clamped, rejected or rounded them.
            ApplyActiveCameraToControls();
            RefreshModeVisuals(force: true);
        }

        /// <summary>
        /// Reflows one of this tab's sections after its content changed height.
        ///
        /// <para>uGUI layout groups do not follow a child that grew, and the group's own root is
        /// measured by its parent before its content has resized — so the rebuild has to run
        /// outward from the rows that actually changed, innermost first, stopping at this page.
        /// </para>
        /// </summary>
        private void RebuildModeLayout(PanelElementDescriptor group)
        {
            if (group == null || _modePageContent == null) return;

            PanelElementDescriptor.RebuildLayoutChain(group.ContentParent, _modePageContent);
        }

        // ---------- The settings, as text ----------

        private void BuildSettingsReadout(RectTransform content)
        {
            _readoutCard = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, content);

            // The section header already names it, and the card has fifty rows to fit — so it
            // gives back both reservations it would otherwise hold beside a title it does not
            // have, and the text gets the width of the page instead of what is left of it.
            _readoutCard.SetTitle(string.Empty);
            if (_readoutCard.IconBackground != null) _readoutCard.IconBackground.SetActive(false);
            ReleaseControlSlot(_readoutCard);
        }

        /// <summary>
        /// Writes the harvested file out into the readout, with the rows that have left the mode
        /// coloured. <see cref="PanelElementDescriptor"/> drops a description identical to the one
        /// already showing, so a camera nobody is touching costs one string compare rather than a
        /// text layout.
        ///
        /// <para>The comparison is against <see cref="BasisHandHeldCamera.ComparedMode"/> rather
        /// than the camera's current mode, because the two only differ once something has been
        /// changed — which is the only time there is anything to colour.</para>
        /// </summary>
        private void RefreshReadout(CameraSettings live)
        {
            if (_readoutCard == null || _activeCamera == null || live == null) return;

            string text = BasisCameraSettingsReadout.Build(
                live,
                (int)_activeCamera.PinSpace,
                _activeCamera.MetaData,
                _activeCamera.CompareToMode(_activeCamera.ComparedMode));

            _readoutCard.SetRichDescription(text);

            // A changed number leaves the card exactly as tall as it was, and reflowing the page
            // four times a second for that would be most of what this tab costs. Only a change in
            // how many rows there are can move the height, and that is what is watched.
            int lines = CountLines(text);
            if (lines == _readoutLineCount) return;

            _readoutLineCount = lines;
            RebuildModeLayout(_readoutGroup);
        }

        private static int CountLines(string text)
        {
            int lines = 1;
            for (int Index = 0; Index < text.Length; Index++)
            {
                if (text[Index] == '\n') lines++;
            }

            return lines;
        }

        /// <summary>The mode in control.</summary>
        private BasisCameraModeDescriptor ResolveActiveDescriptor()
        {
            if (_activeCamera == null) return BasisCameraModes.Get(BasisCameraMode.Custom);

            return BasisCameraModes.Get(_activeCamera.CameraMode);
        }

        /// <summary>
        /// Brings the dropdown and the blurb in line with the camera's mode. Change-gated on which
        /// mode is showing, because the description is a text layout — not worth redoing on a tick
        /// that changed nothing.
        /// </summary>
        private void RefreshModeVisuals(bool force = false)
        {
            if (_activeCamera == null) return;

            BasisCameraModeDescriptor descriptor = ResolveActiveDescriptor();
            string key = descriptor.TitleKey;

            if (!force && _lastShownKey == key) return;

            // Not while it is open: moving the selection out from under an expanded dropdown
            // scrolls it and highlights a different row mid-choice.
            bool expanded = _modeDropdown?.DropdownComponent != null &&
                            _modeDropdown.DropdownComponent.IsExpanded;

            if (_modeDropdown != null)
            {
                if (!expanded && _modeValues.Contains(key))
                {
                    _modeDropdown.SetValueWithoutNotify(key);
                }

                // A tooltip, not a line of the page: the control already names the mode, so the
                // paragraph about it is worth a hover but not the height under every row.
                _modeDropdown.Descriptor.SetTooltip(BasisLocalization.Get(descriptor.DescriptionKey));
            }

            // Only recorded once the control actually shows it. Caching a key the dropdown was too
            // busy to take would leave the picker naming the mode before last, permanently.
            _lastShownKey = expanded ? null : key;
        }

        /// <summary>
        /// Per-tick half: re-derives the mode from the live camera so a setting changed anywhere —
        /// this panel, the prop's own HUD, or another mode's controls — moves the label to Custom.
        /// </summary>
        private void TickModeState()
        {
            if (_activeCamera == null) return;

            bool changed = _activeCamera.RefreshCameraMode();

            if (--_modeCheckCountdown <= 0)
            {
                _modeCheckCountdown = ModeCheckInterval;
                if (_activeCamera.HandHeld != null) RefreshReadout(_activeCamera.HandHeld.CaptureSettings());
            }

            if (changed) RefreshModeVisuals(force: true);
        }

        private void ClearModeReferences()
        {
            ClearBodyReferences();

            _modeDropdown = null;
            _lastShownKey = null;
            _modeValues.Clear();
            _modeCheckCountdown = 0;

            _readoutSection = null;
            _readoutGroup = null;
            _readoutCard = null;
            _modePageContent = null;

            // The card is rebuilt empty on the next open, so a remembered count would skip the one
            // write that actually changes its height — going from nothing to fifty rows.
            _readoutLineCount = -1;
        }
    }
}
