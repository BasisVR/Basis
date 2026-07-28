using Basis.BasisUI.Styling;
using Basis.Scripts.Avatar;
using Basis.Scripts.Debugging;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Device_Management.Devices.Pairing;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Basis.BasisUI
{
    /// <summary>
    /// Settings tab that lists every connected full-body-eligible tracker and lets
    /// the user (a) link any two of them into a virtual midpoint pair and (b) force
    /// a body role on a tracker so calibration binds it without going through
    /// constellation discovery. The constellation classifier in
    /// BasisAvatarIKStageCalibration consumes both: pairings via BasisTrackerPairing,
    /// forced roles via BasisTrackerRoleOverride.
    ///
    /// Layout: a static intro + tuning section at the top (built once when the tab
    /// is created and bound to settings so values persist), then a dynamic list of
    /// per-tracker entries below — each entry has a "linked with" dropdown and a
    /// "force role" dropdown.
    ///
    /// Pairing/override-graph updates only refresh the *selected value* of each
    /// existing dropdown — they do NOT tear the dropdowns down. Tearing down a
    /// dropdown while the user is mid-click on one of its options destroys the
    /// GameObject the click is being routed to and makes the whole entry list
    /// flicker out. A full rebuild only happens when the set of eligible trackers
    /// actually changes (a tracker connected or disconnected).
    /// </summary>
    public static class SettingsProviderTrackerSettings
    {
        private const string UnlinkedLabel = "(unlinked)";
        // Localization key — kept as the historical "trackerlinking" so existing
        // translations and any saved last-tab pointers still resolve.
        private const string TabKey = "settings.tab.trackerlinking";

        // Roles we let the user force a tracker into. Mirrors the FB-tracker priors
        // in BasisAvatarIKStageCalibration.BuildPriors so anything you can pick here
        // is something the constellation classifier would have considered anyway.
        private static readonly BasisBoneTrackedRole[] OverrideableRoles =
        {
            BasisBoneTrackedRole.Hips,
            BasisBoneTrackedRole.Chest,
            BasisBoneTrackedRole.LeftShoulder,
            BasisBoneTrackedRole.RightShoulder,
            BasisBoneTrackedRole.LeftLowerArm,
            BasisBoneTrackedRole.RightLowerArm,
            BasisBoneTrackedRole.LeftLowerLeg,
            BasisBoneTrackedRole.RightLowerLeg,
            BasisBoneTrackedRole.LeftFoot,
            BasisBoneTrackedRole.RightFoot,
            BasisBoneTrackedRole.LeftToes,
            BasisBoneTrackedRole.RightToes,
        };

        private sealed class TabEntry
        {
            public string Key;
            public PanelElementDescriptor Group;
            public PanelButton IdentifyA;
            public PanelButton IdentifyB;
            public PanelDropdown LinkDropdown;
            public PanelDropdown RoleDropdown;
        }

        /// <summary>
        /// One row of the list. A linked pair is ONE row, not two. The merged virtual
        /// midpoint is what calibration actually binds a role to — <see cref="BasisVirtualMidpointInput.Initialize"/>
        /// clears the FB role off both physical halves and the classifier skips them
        /// (<c>if (input.IsLinked) continue;</c>), so neither half ever holds a role.
        /// Rendering the halves as two independent rows is why both of them sat there
        /// reporting "Unassigned" while the pair was working perfectly well.
        /// </summary>
        private sealed class TrackerRow
        {
            public BasisInput A;
            // Null on a single-tracker row.
            public BasisInput B;
            // The live merged device, once the pairing service has built it.
            public BasisVirtualMidpointInput Fused;
            // Set when this tracker is paired to one that is not currently connected.
            public string OfflinePartnerId;

            public bool IsPair => B != null;

            public string Key => B != null
                ? A.UniqueDeviceIdentifier + "|" + B.UniqueDeviceIdentifier
                : A.UniqueDeviceIdentifier;
        }

        // Per-tab-instance state captured in a closure. One instance per Settings
        // open; cleared in OnInstanceReleased so reopening Settings starts fresh.
        private sealed class TabState
        {
            public RectTransform TrackersContainer;
            // The wrapping group whose ContentParent is TrackersContainer.
            // Children are added/removed inside its content area, so this is
            // the descriptor whose own layout needs to settle when the row
            // count changes.
            public PanelElementDescriptor TrackersGroup;
            // Tab page's root descriptor — rebuilt alongside TrackersGroup
            // because Unity's layout cascade doesn't always propagate up
            // through nested LayoutGroups on its own. Same two-step rebuild
            // pattern as the visibility toggles in SettingsProviderIK
            // (advancedToggle / debugToggle handlers).
            public PanelElementDescriptor TabDescriptor;
            public readonly bool[] SuppressDropdownEvents = { false };
            // True after the first build pass has run. Lets the diff logic force
            // a full rebuild on the initial call even when there are zero
            // trackers (so the "no trackers" panel actually gets created).
            public bool HasBuilt;
            public readonly List<TabEntry> Entries = new List<TabEntry>();
        }

        public static PanelTabPage TrackerSettingsTab(PanelTabGroup tabGroup)
        {
            PanelTabPage tabPage = PanelTabPage.CreateVertical(tabGroup.Descriptor.ContentParent);
            PanelElementDescriptor tabDesc = tabPage.Descriptor;
            tabDesc.SetTitle(BasisLocalization.Get(TabKey));
            tabDesc.SetIcon(AddressableAssets.Sprites.Calibrate);

            RectTransform tabRoot = tabDesc.ContentParent;

            // Announced-role trust switch, consumed every scan by
            // BasisAnnouncedTrackerRoles. Opt-in because most setups leave the
            // SteamVR-assigned roles unset or stale. (Vendor conventions like
            // SlimeVR's serials ship their own toggle in their own package.)
            PanelToggle trustSteamVRToggle = PanelToggle.CreateNewEntry(tabRoot);
            trustSteamVRToggle.Descriptor.SetTitle(BasisLocalization.Get("trackerLinking.trustSteamVRRoles"));
            trustSteamVRToggle.Descriptor.SetTooltip(BasisLocalization.Get("trackerLinking.trustSteamVRRoles.tooltip"));
            trustSteamVRToggle.AssignBinding(BasisSettingsDefaults.TrustSteamVRRoles);

            // Connector trackers toggle — hides the per-tracker list (linking +
            // role override dropdowns) so a configured player doesn't have to
            // scroll past every device on every visit. Same opt-in pattern as
            // the advanced toggle below; user touches it once to set things up.
            PanelSectionToggle connectorToggle = PanelSectionToggle.CreateNewEntry(tabRoot);
            connectorToggle.SetTitle(BasisLocalization.Get("trackerLinking.connectorTrackers"));
            connectorToggle.Descriptor.SetTooltip(BasisLocalization.Get("trackerLinking.connectorTrackers.tooltip"));
            connectorToggle.BindToToggle(BasisSettingsDefaults.TrackerLinkingConnectorVisible);

            // Advanced toggle — hides the tuning sliders behind an opt-in so
            // the page stays approachable for users who only want to link
            // trackers. Same pattern as SettingsProviderIK's advancedToggle.
            PanelSectionToggle advancedToggle = PanelSectionToggle.CreateNewEntry(tabRoot);
            advancedToggle.SetTitle(BasisLocalization.Get("trackerLinking.advanced"));
            advancedToggle.Descriptor.SetTooltip(BasisLocalization.Get("trackerLinking.advanced.tooltip"));
            advancedToggle.BindToToggle(BasisSettingsDefaults.TrackerLinkingAdvancedVisible);

            // Static tuning group — bound to BasisSettingsDefaults bindings, so values
            // persist across menu reopens and across sessions. Built once.
            PanelElementDescriptor tuningGroup = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, tabRoot);
            tuningGroup.SetTitle(BasisLocalization.Get("trackerLinking.tuning.title"));
            BuildTuningSliders(tuningGroup.ContentParent);

            // Dynamic per-tracker section — updates on device/pair/override changes.
            // Lives in its own container so the tuning sliders above aren't touched.
            PanelElementDescriptor trackersGroup = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, tabRoot);
            trackersGroup.SetTitle(BasisLocalization.Get("trackerLinking.trackers.title"));

            TabState state = new TabState
            {
                TrackersContainer = trackersGroup.ContentParent,
                TrackersGroup = trackersGroup,
                TabDescriptor = tabDesc,
            };

            // Single change handler routes list, pairing, and override events.
            // Decides whether a full rebuild is necessary by diffing the
            // eligible-id set, and otherwise just refreshes the existing
            // dropdowns' selected values and per-entry role descriptions in
            // place.
            Action handleChange = () => HandleChange(state);

            BasisObservableList<BasisInput> devices = BasisDeviceManagement.Instance?.AllInputDevices;
            if (devices != null)
            {
                devices.OnListChanged += handleChange;
            }
            BasisTrackerPairing.OnPairingsChanged += handleChange;
            BasisTrackerRoleOverride.OnOverridesChanged += handleChange;
            BasisTrackerIdentifyGizmos.OnIdentifyChanged += handleChange;
            BasisLocalAvatarDriver.CalibrationComplete += handleChange;
            BasisAvatarIKStageCalibration.OnFullBodyCalibrated += handleChange;

            tabPage.OnInstanceReleased += () =>
            {
                BasisObservableList<BasisInput> currentDevices = BasisDeviceManagement.Instance?.AllInputDevices;
                if (currentDevices != null)
                {
                    currentDevices.OnListChanged -= handleChange;
                }
                BasisTrackerPairing.OnPairingsChanged -= handleChange;
                BasisTrackerRoleOverride.OnOverridesChanged -= handleChange;
                BasisTrackerIdentifyGizmos.OnIdentifyChanged -= handleChange;
                BasisLocalAvatarDriver.CalibrationComplete -= handleChange;
                BasisAvatarIKStageCalibration.OnFullBodyCalibrated -= handleChange;
                state.TrackersContainer = null;
                state.TrackersGroup = null;
                state.TabDescriptor = null;
                state.HasBuilt = false;
                state.Entries.Clear();
            };

            // Webcam / external tracking sections injected by feature packages
            // (e.g. MediaPipe). Built above the page reset so the reset stays last.
            SettingsProvider.TrackerSettingsExtraBuilder?.Invoke(tabRoot);

            // Page-level reset stays on tabRoot (not inside tuningGroup) so it
            // remains reachable when the advanced toggle hides the sliders.
            SettingsProvider.AddResetPageButton(tabRoot, TabKey, ResetTrackerSettingsDefaults);

            // Initial visibility + OnValueChanged gating. Two-step rebuild
            // (inner group, then tab descriptor) matches the existing pattern
            // in HandleChange so nested LayoutGroups settle correctly.
            PanelSectionToggleHelpers.FinalizeCollapsibleContents(
                advancedToggle,
                BasisSettingsDefaults.TrackerLinkingAdvancedVisible.RawValue,
                _ =>
            {
                tuningGroup.ForceRebuild();
                tabDesc.ForceRebuild();
            },
                tuningGroup);

            PanelSectionToggleHelpers.FinalizeCollapsibleContents(
                connectorToggle,
                BasisSettingsDefaults.TrackerLinkingConnectorVisible.RawValue,
                _ =>
            {
                trackersGroup.ForceRebuild();
                tabDesc.ForceRebuild();
            },
                trackersGroup);

            handleChange();
            tabDesc.ForceRebuild();
            return tabPage;
        }

        private static void HandleChange(TabState state)
        {
            if (state.TrackersContainer == null) return;

            List<BasisInput> trackers = CollectEligibleTrackers();
            List<TrackerRow> rows = BuildRows(trackers);

            // Full rebuild on the first call (otherwise the empty list path
            // would skip the "no trackers" message), any time the row set has
            // changed, and as a self-healing fallback if the cached entry count
            // has somehow gone out of sync.
            //
            // A pairing change now reshapes the LIST (two rows collapse into one,
            // or one splits back into two), so unlike the old id-only diff it has
            // to force a rebuild rather than just refreshing dropdown values. The
            // key carries both halves, so that falls out of the same comparison.
            //
            // Otherwise — an override or identify toggle, where the rows are the
            // same — we update in place. That is the path that protects a dropdown
            // the user is mid-click on from being torn down underneath them.
            bool needsRebuild = !state.HasBuilt
                || !KeysMatch(state.Entries, rows)
                || state.Entries.Count != rows.Count;

            if (needsRebuild)
            {
                FullRebuild(state, rows);
                state.HasBuilt = true;
                // Two-step layout rebuild, matching the toggle-show/hide pattern
                // in SettingsProviderIK (advancedToggle / debugToggle handlers).
                // Force the inner group's layout first so its ContentSizeFitter
                // re-measures with the new row count, then the outer tab so the
                // scroll view's content height picks up that new size. Skipping
                // either step leaves the rows in the hierarchy but invisible
                // (or stacked) because some parent kept its old measured height.
                if (state.TrackersGroup != null && !state.TrackersGroup.IsReleased)
                {
                    state.TrackersGroup.ForceRebuild();
                }
                if (state.TabDescriptor != null && !state.TabDescriptor.IsReleased)
                {
                    state.TabDescriptor.ForceRebuild();
                }
                return;
            }

            RefreshExistingEntries(state, rows);
        }

        /// <summary>
        /// Collapses the flat device list into rows: a linked pair becomes one row led
        /// by its canonical half (the id that sorts first — the same half
        /// <see cref="BasisTrackerPairing.IsCanonical"/> hands the role to).
        /// </summary>
        private static List<TrackerRow> BuildRows(List<BasisInput> trackers)
        {
            List<TrackerRow> rows = new List<TrackerRow>(trackers.Count);
            HashSet<string> consumed = new HashSet<string>();

            for (int i = 0; i < trackers.Count; i++)
            {
                BasisInput input = trackers[i];
                string id = input.UniqueDeviceIdentifier;
                if (consumed.Contains(id)) continue;

                if (!BasisTrackerPairing.TryGetPartner(id, out string partnerId))
                {
                    consumed.Add(id);
                    rows.Add(new TrackerRow { A = input });
                    continue;
                }

                BasisInput partner = FindById(trackers, partnerId);
                if (partner == null)
                {
                    // Paired to a tracker that isn't connected right now. Pairings are
                    // persisted, so this is a normal state (partner off, battery dead,
                    // not paired to the dongle yet) — but the old UI resolved the link
                    // dropdown to "(unlinked)" here, which reads as "your pairing was
                    // lost" when it is still on disk and will come straight back.
                    consumed.Add(id);
                    rows.Add(new TrackerRow { A = input, OfflinePartnerId = partnerId });
                    continue;
                }

                BasisInput canonical = BasisTrackerPairing.IsCanonical(id) ? input : partner;
                BasisInput other = ReferenceEquals(canonical, input) ? partner : input;
                consumed.Add(canonical.UniqueDeviceIdentifier);
                consumed.Add(other.UniqueDeviceIdentifier);
                rows.Add(new TrackerRow
                {
                    A = canonical,
                    B = other,
                    Fused = FindFused(canonical, other),
                });
            }

            return rows;
        }

        private static BasisInput FindById(List<BasisInput> trackers, string id)
        {
            for (int i = 0; i < trackers.Count; i++)
            {
                if (trackers[i].UniqueDeviceIdentifier == id) return trackers[i];
            }
            return null;
        }

        /// <summary>
        /// The merged device the pairing service spawned for this pair, or null if it
        /// hasn't built one. This is the device that actually carries the pair's role.
        /// </summary>
        private static BasisVirtualMidpointInput FindFused(BasisInput a, BasisInput b)
        {
            BasisObservableList<BasisInput> devices = BasisDeviceManagement.Instance?.AllInputDevices;
            if (devices == null) return null;

            for (int i = 0; i < devices.Count; i++)
            {
                if (!(devices[i] is BasisVirtualMidpointInput midpoint)) continue;
                bool forward = ReferenceEquals(midpoint.PartnerA, a) && ReferenceEquals(midpoint.PartnerB, b);
                bool reversed = ReferenceEquals(midpoint.PartnerA, b) && ReferenceEquals(midpoint.PartnerB, a);
                if (forward || reversed) return midpoint;
            }
            return null;
        }

        private static bool KeysMatch(List<TabEntry> entries, List<TrackerRow> rows)
        {
            if (entries.Count != rows.Count) return false;
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].Key != rows[i].Key) return false;
            }
            return true;
        }

        private static void RefreshExistingEntries(TabState state, List<TrackerRow> rows)
        {
            state.SuppressDropdownEvents[0] = true;
            for (int i = 0; i < rows.Count; i++)
            {
                if (i >= state.Entries.Count) continue;
                TrackerRow row = rows[i];
                TabEntry entry = state.Entries[i];

                if (entry.Group != null && !entry.Group.IsReleased)
                {
                    entry.Group.SetRichDescription(BuildRowDescription(row));
                }
                ApplyIdentifyVisual(entry.IdentifyA, row.A, row.IsPair);
                ApplyIdentifyVisual(entry.IdentifyB, row.B, row.IsPair);
                if (entry.LinkDropdown != null && !entry.LinkDropdown.IsReleased)
                {
                    entry.LinkDropdown.SetValueWithoutNotify(UnlinkedLabel);
                }
                if (entry.RoleDropdown != null && !entry.RoleDropdown.IsReleased)
                {
                    entry.RoleDropdown.SetValueWithoutNotify(ResolveCurrentRoleOverride(row));
                }
            }
            state.SuppressDropdownEvents[0] = false;
        }

        private static void FullRebuild(TabState state, List<TrackerRow> rows)
        {
            // Snapshot children before releasing — ReleaseInstance destroys the
            // GameObject and may shift sibling indices before the next iteration.
            int childCount = state.TrackersContainer.childCount;
            Transform[] toRelease = new Transform[childCount];
            for (int i = 0; i < childCount; i++)
            {
                toRelease[i] = state.TrackersContainer.GetChild(i);
            }
            for (int i = 0; i < toRelease.Length; i++)
            {
                Transform child = toRelease[i];
                if (child == null) continue;
                PanelElementDescriptor descriptor = child.GetComponent<PanelElementDescriptor>();
                if (descriptor != null)
                {
                    descriptor.ReleaseInstance();
                }
                else
                {
                    UnityEngine.Object.Destroy(child.gameObject);
                }
            }

            state.Entries.Clear();

            if (rows.Count == 0)
            {
                PanelElementDescriptor empty = PanelElementDescriptor.CreateNew(
                    PanelElementDescriptor.ElementStyles.Group, state.TrackersContainer);
                empty.SetTitle(BasisLocalization.Get("trackerLinking.noTrackers.title"));
                return;
            }

            // Suppress dropdown change events while we set initial values —
            // without this, AssignEntries + SetValueWithoutNotify can still fire
            // callbacks depending on prefab configuration, and we'd recursively
            // rebuild as we cascade through dropdowns.
            state.SuppressDropdownEvents[0] = true;
            for (int i = 0; i < rows.Count; i++)
            {
                BuildEntryFor(state, rows[i], rows);
            }
            state.SuppressDropdownEvents[0] = false;
        }

        // Height of the pair's side-by-side identify row. Matches the action rows in the
        // modal prompts (BasisDirectConnectionPrompt and friends), which are the only
        // other place in the menu that puts buttons next to each other.
        private const float PairIdentifyRowHeight = 80f;

        private static void BuildEntryFor(TabState state, TrackerRow row, List<TrackerRow> rows)
        {
            bool[] suppressFlag = state.SuppressDropdownEvents;
            string idA = row.A.UniqueDeviceIdentifier;
            bool linked = row.IsPair || !string.IsNullOrEmpty(row.OfflinePartnerId);

            PanelElementDescriptor group = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, state.TrackersContainer);
            group.SetTitle(BuildRowTitle(row));
            group.SetRichDescription(BuildRowDescription(row));

            TabEntry entry = new TabEntry
            {
                Key = row.Key,
                Group = group,
            };

            if (row.IsPair)
            {
                // Side by side, not stacked. Two full-width buttons that differ only in
                // which half of the pair they light up read as a pile, and they belong
                // to each other anyway.
                PanelTabGroup identifyRow = PanelTabGroup.CreateNew(
                    group.ContentParent, LayoutDirection.HorizontalNoBackground);
                identifyRow.Descriptor.SetHeight(PairIdentifyRowHeight);
                entry.IdentifyA = BuildIdentifyButton(identifyRow.TabButtonParent, PanelButton.ButtonStyles.StandardButton, row.A, true);
                entry.IdentifyB = BuildIdentifyButton(identifyRow.TabButtonParent, PanelButton.ButtonStyles.StandardButton, row.B, true);
            }
            else
            {
                entry.IdentifyA = BuildIdentifyButton(group.ContentParent, PanelButton.ButtonStyles.Default, row.A, false);
            }

            // A pairing that already exists has nothing left to pick from a dropdown —
            // the only thing left to say about it is whether to break it.
            if (!linked)
            {
                entry.LinkDropdown = BuildLinkDropdown(group, row, rows, suppressFlag);
            }

            PanelDropdown roleDropdown = PanelDropdown.CreateNewEntry(group.ContentParent);
            roleDropdown.Descriptor.SetTitle(BasisLocalization.Get("trackerLinking.roleOverrideLabel"));
            roleDropdown.Descriptor.SetTooltip(BasisLocalization.Get(row.IsPair
                ? "trackerLinking.roleOverridePair.tooltip"
                : "trackerLinking.roleOverrideLabel.tooltip"));
            roleDropdown.AssignEntries(BuildRoleEntries(), BuildRoleDisplayLabels());
            roleDropdown.SetValueWithoutNotify(ResolveCurrentRoleOverride(row));

            string idB = row.IsPair ? row.B.UniqueDeviceIdentifier : null;
            roleDropdown.OnValueChanged += newValue =>
            {
                if (suppressFlag[0]) return;

                // Calibration's TryResolveOverride reads the midpoint's override off
                // PartnerA first, then PartnerB. Writing only the canonical half and
                // clearing the other keeps exactly one source of truth — otherwise a
                // stale override left on B silently outlives a switch back to Auto.
                if (string.IsNullOrEmpty(newValue) || newValue == AutoRoleLabel())
                {
                    BasisTrackerRoleOverride.ClearOverride(idA);
                    if (idB != null) BasisTrackerRoleOverride.ClearOverride(idB);
                    return;
                }
                if (TryParseOverrideRole(newValue, out BasisBoneTrackedRole parsed))
                {
                    BasisTrackerRoleOverride.SetOverride(idA, parsed);
                    if (idB != null) BasisTrackerRoleOverride.ClearOverride(idB);
                }
            };

            entry.RoleDropdown = roleDropdown;

            // Unlink goes last and alone. It is the one destructive control on the row,
            // and it has no business sitting shoulder to shoulder with a button you press
            // casually to make a light come on.
            if (linked)
            {
                PanelButton unlink = PanelButton.CreateNew(group.ContentParent);
                unlink.Descriptor.SetTitle(BasisLocalization.Get("trackerLinking.unlink"));
                unlink.Descriptor.SetTooltip(BasisLocalization.Get("trackerLinking.unlink.tooltip"));
                unlink.SetIcon(AddressableAssets.Sprites.Unlink);
                if (unlink.ButtonStyling != null)
                {
                    unlink.ButtonStyling.SetStyle("Button Danger");
                }
                unlink.OnClicked += () => BasisTrackerPairing.Unlink(idA);
            }

            state.Entries.Add(entry);
        }

        private static PanelButton BuildIdentifyButton(Component parent, string style, BasisInput input, bool named)
        {
            PanelButton button = PanelButton.CreateNew(style, parent);
            button.Descriptor.SetTooltip(BasisLocalization.Get("trackerLinking.identifyLabel.tooltip"));
            ApplyIdentifyVisual(button, input, named);
            button.OnClicked += () => BasisTrackerIdentifyGizmos.Toggle(input);
            return button;
        }

        private static PanelDropdown BuildLinkDropdown(PanelElementDescriptor group, TrackerRow row, List<TrackerRow> rows, bool[] suppressFlag)
        {
            PanelDropdown linkDropdown = PanelDropdown.CreateNewEntry(group.ContentParent);
            linkDropdown.Descriptor.SetTitle(BasisLocalization.Get("trackerLinking.linkLabel"));
            linkDropdown.Descriptor.SetTooltip(BasisLocalization.Get("trackerLinking.linkLabel.tooltip"));

            // Only free trackers are offered. Linking to an already-paired tracker
            // would work — Link() drops its old pairing first — but it would silently
            // dissolve a pair the user never touched, from a dropdown that gave no
            // hint that was about to happen.
            List<string> entries = new List<string> { UnlinkedLabel };
            List<string> labels = new List<string> { UnlinkedDisplayLabel() };
            for (int i = 0; i < rows.Count; i++)
            {
                TrackerRow other = rows[i];
                if (other.IsPair || !string.IsNullOrEmpty(other.OfflinePartnerId)) continue;
                if (ReferenceEquals(other.A, row.A)) continue;
                entries.Add(other.A.UniqueDeviceIdentifier);
                labels.Add(BuildTrackerLabel(other.A));
            }
            linkDropdown.AssignEntries(entries, labels);
            linkDropdown.SetValueWithoutNotify(UnlinkedLabel);

            string capturedId = row.A.UniqueDeviceIdentifier;
            linkDropdown.OnValueChanged += newValue =>
            {
                if (suppressFlag[0]) return;
                if (string.IsNullOrEmpty(newValue) || newValue == UnlinkedLabel)
                {
                    BasisTrackerPairing.Unlink(capturedId);
                }
                else
                {
                    BasisTrackerPairing.Link(capturedId, newValue);
                }
            };
            return linkDropdown;
        }

        private static void ApplyIdentifyVisual(PanelButton button, BasisInput input, bool named)
        {
            if (button == null || button.IsReleased || input == null) return;
            PanelElementDescriptor descriptor = button.Descriptor;
            if (descriptor == null || !descriptor.HasTitle) return;

            bool showing = BasisTrackerIdentifyGizmos.IsShowing(input);
            Color color = BasisTrackerIdentifyGizmos.ColorFor(input);
            string text = BasisLocalization.Get(showing
                ? "trackerLinking.identifyShowing"
                : "trackerLinking.identifyLabel");

            // On a pair row there are two of these side by side, so the label has to
            // name which half it lights up — the swatch alone is no help to anyone who
            // can't separate the two colours.
            if (named)
            {
                text = $"{text} {NoParse(BuildTrackerLabel(input))}";
            }

            descriptor.SetTitle($"<b>{Tint(color, text)}</b>");
            if (button.ButtonStyling != null)
            {
                button.ButtonStyling.ShowIndicator(showing);
            }
        }

        private static string BuildRowTitle(TrackerRow row)
        {
            return row.IsPair
                ? BasisLocalization.Get("trackerLinking.pair.title")
                : BuildTrackerLabel(row.A);
        }

        private static string BuildRowDescription(TrackerRow row)
        {
            if (!row.IsPair)
            {
                return BuildStatusLine(row);
            }

            string plus = Tint(PaletteColor(p => p.FontColor3, MutedFallback), "+");
            return $"{TintedName(row.A)}  {plus}  {TintedName(row.B)}\n{BuildStatusLine(row)}";
        }

        /// <summary>
        /// The one line that tells the user whether this row is actually doing anything.
        /// A pair's role lives on the merged midpoint, so for a pair this reads the role
        /// off <see cref="TrackerRow.Fused"/> — never off the halves, which by design
        /// hold none.
        /// </summary>
        private static string BuildStatusLine(TrackerRow row)
        {
            Color success = PaletteColor(p => p.SuccessColor, new Color(0.09f, 0.8f, 0.47f));
            Color caution = PaletteColor(p => p.CautionColor, new Color(1f, 0.82f, 0.34f));

            if (row.IsPair)
            {
                if (row.Fused == null)
                {
                    return Tint(caution, BasisLocalization.Get("trackerLinking.status.pairPending"));
                }
                if (TryGetBodyRole(row.Fused, out BasisBoneTrackedRole fusedRole))
                {
                    return Tint(success, BasisLocalization.Get("trackerLinking.status.pairDriving", FormatRole(fusedRole)));
                }
                return Tint(caution, BasisLocalization.Get("trackerLinking.status.pairUncalibrated"));
            }

            if (!string.IsNullOrEmpty(row.OfflinePartnerId))
            {
                return Tint(caution, BasisLocalization.Get("trackerLinking.status.partnerOffline"));
            }

            if (TryGetBodyRole(row.A, out BasisBoneTrackedRole role))
            {
                return Tint(success, BasisLocalization.Get("trackerLinking.status.driving", FormatRole(role)));
            }
            return Tint(PaletteColor(p => p.FontColor3, MutedFallback), BasisLocalization.Get("trackerLinking.status.unassigned"));
        }

        /// <summary>
        /// A role that means something for the body. A fresh midpoint is initialized as
        /// CenterEye as a placeholder, so a bare TryGetRole would report a brand-new,
        /// never-calibrated pair as bound.
        /// </summary>
        private static bool TryGetBodyRole(BasisInput input, out BasisBoneTrackedRole role)
        {
            if (input != null
                && input.TryGetRole(out role)
                && BasisBoneTrackedRoleCommonCheck.CheckItsFBTracker(role))
            {
                return true;
            }
            role = BasisBoneTrackedRole.CenterEye;
            return false;
        }

        private static readonly Color MutedFallback = new Color(0.65f, 0.67f, 0.69f);

        private static Color PaletteColor(Func<UiStylePalette, Color> pick, Color fallback)
        {
            UiStylePalette palette = UiStyleSettings.GetActivePalette();
            return palette != null ? pick(palette) : fallback;
        }

        private static string Tint(Color color, string text)
            => $"<color=#{ColorUtility.ToHtmlStringRGB(color)}>{text}</color>";

        /// <summary>
        /// The tracker's name, in the colour its identify sphere lights up. The name
        /// carries the colour rather than a separate swatch block — a block wants to be
        /// a small square, and every way of drawing one in this menu is worse than not:
        /// a shape glyph (●) has no guaranteed entry in the Latin SDF atlas, a TMP mark
        /// highlight comes out as a bar the full height of the line, and a PanelImage
        /// gets its own full-width row in the vertical layout.
        /// </summary>
        private static string TintedName(BasisInput input)
            => Tint(BasisTrackerIdentifyGizmos.ColorFor(input), NoParse(BuildTrackerLabel(input)));

        // Device names come off the hardware, so they are not ours to trust as markup.
        private static string NoParse(string text) => $"<noparse>{text}</noparse>";

        private static string ResolveCurrentRoleOverride(TrackerRow row)
        {
            if (BasisTrackerRoleOverride.TryGetOverride(row.A.UniqueDeviceIdentifier, out BasisBoneTrackedRole role))
            {
                return role.ToString();
            }
            if (row.IsPair && BasisTrackerRoleOverride.TryGetOverride(row.B.UniqueDeviceIdentifier, out role))
            {
                return role.ToString();
            }
            return AutoRoleLabel();
        }

        private static string AutoRoleLabel() => BasisLocalization.Get("trackerLinking.roleOverride.auto");

        private static List<string> BuildRoleEntries()
        {
            List<string> list = new List<string>(OverrideableRoles.Length + 1) { AutoRoleLabel() };
            for (int i = 0; i < OverrideableRoles.Length; i++)
            {
                list.Add(OverrideableRoles[i].ToString());
            }
            return list;
        }

        private static List<string> BuildRoleDisplayLabels()
        {
            List<string> list = new List<string>(OverrideableRoles.Length + 1) { AutoRoleLabel() };
            for (int i = 0; i < OverrideableRoles.Length; i++)
            {
                list.Add(FormatRole(OverrideableRoles[i]));
            }
            return list;
        }

        private static string UnlinkedDisplayLabel() => BasisLocalization.Get("trackerLinking.linkUnlinked");

        private static string FormatRole(BasisBoneTrackedRole role)
        {
            string raw = role.ToString();
            StringBuilder sb = new StringBuilder(raw.Length + 4);
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (i > 0 && char.IsUpper(c) && char.IsLower(raw[i - 1]))
                {
                    sb.Append(' ');
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static string BuildTrackerLabel(BasisInput input)
        {
            if (input == null) return "Tracker";

            string name = input.CommonDeviceIdentifier;
            if (string.IsNullOrEmpty(name)) name = input.ClassName;
            if (string.IsNullOrEmpty(name) && input.gameObject != null) name = input.gameObject.name;

            string id = input.UniqueDeviceIdentifier;
            if (string.IsNullOrEmpty(name)) name = string.IsNullOrEmpty(id) ? "Tracker" : id;

            if (!string.IsNullOrEmpty(id) && !string.Equals(name, id))
            {
                string shortId = id.Length > 6 ? id.Substring(id.Length - 6) : id;
                name = $"{name} ({shortId})";
            }
            return name;
        }

        private static bool TryParseOverrideRole(string value, out BasisBoneTrackedRole role)
        {
            for (int i = 0; i < OverrideableRoles.Length; i++)
            {
                if (OverrideableRoles[i].ToString() == value)
                {
                    role = OverrideableRoles[i];
                    return true;
                }
            }
            role = BasisBoneTrackedRole.CenterEye;
            return false;
        }

        private static void BuildTuningSliders(RectTransform parent)
        {
            // Calibration tolerance: widens every constellation accept region at once. The
            // one knob most users will reach for — bump it if a tracker refuses to bind
            // during full-body calibration (atypical proportions, high-mounted trackers, a
            // wide stance, or an imperfect T-pose).
            PanelSlider calibrationTolerance = PanelSlider.CreateAndBind(parent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("trackerLinking.tuning.calibrationTolerance"),
                    1f, 3f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.CalibrationTolerance);
            if (calibrationTolerance != null)
            {
                calibrationTolerance.Descriptor.SetTooltip(BasisLocalization.Get("trackerLinking.tuning.calibrationTolerance.tooltip"));
            }

            // Surprise penalty: how aggressively a glitching half loses authority.
            PanelSlider penalty = PanelSlider.CreateAndBind(parent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("trackerLinking.tuning.surprisePenalty"),
                    0.5f, 10f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.PairingSurprisePenalty);
            if (penalty != null)
            {
                penalty.Descriptor.SetTooltip(BasisLocalization.Get("trackerLinking.tuning.surprisePenalty.tooltip"));
            }

            // Surprise clamp: above this, the velocity baseline freezes.
            PanelSlider clamp = PanelSlider.CreateAndBind(parent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("trackerLinking.tuning.surpriseClamp"),
                    1.5f, 10f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.PairingSurpriseClamp);
            if (clamp != null)
            {
                clamp.Descriptor.SetTooltip(BasisLocalization.Get("trackerLinking.tuning.surpriseClamp.tooltip"));
            }

            // EMA floor (in meters/frame) — guard against divide-by-zero on a
            // frozen tracker. Fine-grained because typical motion is mm/frame.
            PanelSlider floor = PanelSlider.CreateAndBind(parent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("trackerLinking.tuning.emaFloor"),
                    0.0005f, 0.05f, false, 4, ValueDisplayMode.Meters),
                BasisSettingsDefaults.PairingEmaFloor);
            if (floor != null)
            {
                floor.Descriptor.SetTooltip(BasisLocalization.Get("trackerLinking.tuning.emaFloor.tooltip"));
            }

            // Soft-snap correction cap.
            PanelSlider maxCorrection = PanelSlider.CreateAndBind(parent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("trackerLinking.tuning.maxCorrection"),
                    0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.PairingMaxCorrectionStrength);
            if (maxCorrection != null)
            {
                maxCorrection.Descriptor.SetTooltip(BasisLocalization.Get("trackerLinking.tuning.maxCorrection.tooltip"));
            }

            // Soft-snap half-life (in meters of error).
            PanelSlider halfLife = PanelSlider.CreateAndBind(parent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("trackerLinking.tuning.softSnapHalfLife"),
                    0.005f, 0.5f, false, 3, ValueDisplayMode.Meters),
                BasisSettingsDefaults.PairingSoftSnapHalfLife);
            if (halfLife != null)
            {
                halfLife.Descriptor.SetTooltip(BasisLocalization.Get("trackerLinking.tuning.softSnapHalfLife.tooltip"));
            }

            // Lockstep tolerance (in meters of error).
            PanelSlider lockstep = PanelSlider.CreateAndBind(parent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("trackerLinking.tuning.lockstepTolerance"),
                    0.005f, 0.5f, false, 3, ValueDisplayMode.Meters),
                BasisSettingsDefaults.PairingLockstepTolerance);
            if (lockstep != null)
            {
                lockstep.Descriptor.SetTooltip(BasisLocalization.Get("trackerLinking.tuning.lockstepTolerance.tooltip"));
            }

            // Velocity-baseline EMA alpha.
            PanelSlider emaAlpha = PanelSlider.CreateAndBind(parent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("trackerLinking.tuning.emaAlpha"),
                    0.005f, 0.5f, false, 3, ValueDisplayMode.Raw),
                BasisSettingsDefaults.PairingEmaAlpha);
            if (emaAlpha != null)
            {
                emaAlpha.Descriptor.SetTooltip(BasisLocalization.Get("trackerLinking.tuning.emaAlpha.tooltip"));
            }

            // Rest-distance EMA alpha.
            PanelSlider distEmaAlpha = PanelSlider.CreateAndBind(parent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("trackerLinking.tuning.distanceEmaAlpha"),
                    0.005f, 0.5f, false, 3, ValueDisplayMode.Raw),
                BasisSettingsDefaults.PairingDistanceEmaAlpha);
            if (distEmaAlpha != null)
            {
                distEmaAlpha.Descriptor.SetTooltip(BasisLocalization.Get("trackerLinking.tuning.distanceEmaAlpha.tooltip"));
            }

            // Weight smoothing — how fast the per-tracker confidence weights
            // respond. This is the main knob for trading reactivity against
            // midpoint stability.
            PanelSlider weightSmoothing = PanelSlider.CreateAndBind(parent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("trackerLinking.tuning.weightSmoothing"),
                    0.05f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.PairingWeightSmoothing);
            if (weightSmoothing != null)
            {
                weightSmoothing.Descriptor.SetTooltip(BasisLocalization.Get("trackerLinking.tuning.weightSmoothing.tooltip"));
            }

            // Rotation low-pass half-life (seconds).
            PanelSlider rotationHalfLife = PanelSlider.CreateAndBind(parent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("trackerLinking.tuning.rotationHalfLife"),
                    0f, 0.3f, false, 3, ValueDisplayMode.Raw),
                BasisSettingsDefaults.PairingRotationHalfLife);
            if (rotationHalfLife != null)
            {
                rotationHalfLife.Descriptor.SetTooltip(BasisLocalization.Get("trackerLinking.tuning.rotationHalfLife.tooltip"));
            }
        }

        private static void ResetTrackerSettingsDefaults()
        {
            BasisSettingsDefaults.TrackerLinkingAdvancedVisible.ResetToDefault();
            BasisSettingsDefaults.TrackerLinkingConnectorVisible.ResetToDefault();
            BasisSettingsDefaults.TrustSteamVRRoles.ResetToDefault();
            BasisSettingsDefaults.PairingSurprisePenalty.ResetToDefault();
            BasisSettingsDefaults.PairingSurpriseClamp.ResetToDefault();
            BasisSettingsDefaults.PairingEmaFloor.ResetToDefault();
            BasisSettingsDefaults.PairingMaxCorrectionStrength.ResetToDefault();
            BasisSettingsDefaults.PairingSoftSnapHalfLife.ResetToDefault();
            BasisSettingsDefaults.PairingLockstepTolerance.ResetToDefault();
            BasisSettingsDefaults.PairingEmaAlpha.ResetToDefault();
            BasisSettingsDefaults.PairingDistanceEmaAlpha.ResetToDefault();
            BasisSettingsDefaults.PairingWeightSmoothing.ResetToDefault();
            BasisSettingsDefaults.PairingRotationHalfLife.ResetToDefault();
            BasisSettingsDefaults.CalibrationTolerance.ResetToDefault();
            BasisTrackerRoleOverride.ClearAll();
            BasisTrackerPairing.ClearAll();
            BasisTrackerIdentifyGizmos.ClearAll();
        }

        private static List<BasisInput> CollectEligibleTrackers()
        {
            List<BasisInput> result = new List<BasisInput>();
            BasisObservableList<BasisInput> devices = BasisDeviceManagement.Instance?.AllInputDevices;
            if (devices == null) return result;

            for (int i = 0; i < devices.Count; i++)
            {
                BasisInput input = devices[i];
                if (input == null) continue;
                if (string.IsNullOrEmpty(input.UniqueDeviceIdentifier)) continue;

                // The merged midpoint produced by an active pair lives in
                // AllInputDevices alongside its physical halves; hide it so users
                // don't try to link the virtual to anything.
                if (input is BasisVirtualMidpointInput) continue;

                // Pairing only makes sense for free FB-trackable devices. Skip the
                // HMD and any device whose role was pinned by the matcher (named
                // hand controllers, etc.) — those never participate in calibration.
                if (input.DeviceMatchSettings != null && input.DeviceMatchSettings.HasTrackedRole) continue;
                if (input.TryGetRole(out BasisBoneTrackedRole role))
                {
                    if (role == BasisBoneTrackedRole.Head || role == BasisBoneTrackedRole.CenterEye)
                    {
                        continue;
                    }
                }
                result.Add(input);
            }
            return result;
        }
    }
}
