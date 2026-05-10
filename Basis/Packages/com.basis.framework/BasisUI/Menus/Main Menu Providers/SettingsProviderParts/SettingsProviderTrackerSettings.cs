using Basis.Scripts.Avatar;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Device_Management.Devices.Pairing;
using Basis.Scripts.TransformBinders.BoneControl;
using System;
using System.Collections.Generic;
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
            public string Id;
            public PanelElementDescriptor Group;
            public PanelDropdown LinkDropdown;
            public PanelDropdown RoleDropdown;
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

            // Static intro — explains what this tab is for and survives device-list rebuilds.
            PanelElementDescriptor headerGroup = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, tabRoot);
            headerGroup.SetTitle(BasisLocalization.Get("trackerLinking.header.title"));
            headerGroup.SetDescription(BasisLocalization.Get("trackerLinking.header.description"));

            // Connector trackers toggle — hides the per-tracker list (linking +
            // role override dropdowns) so a configured player doesn't have to
            // scroll past every device on every visit. Same opt-in pattern as
            // the advanced toggle below; user touches it once to set things up.
            PanelToggle connectorToggle = PanelToggle.CreateNewEntry(tabRoot);
            connectorToggle.Descriptor.SetTitle(BasisLocalization.Get("trackerLinking.connectorTrackers"));
            connectorToggle.Descriptor.SetDescription(BasisLocalization.Get("trackerLinking.connectorTrackers.description"));
            connectorToggle.AssignBinding(BasisSettingsDefaults.TrackerLinkingConnectorVisible);

            // Advanced toggle — hides the tuning sliders behind an opt-in so
            // the page stays approachable for users who only want to link
            // trackers. Same pattern as SettingsProviderIK's advancedToggle.
            PanelToggle advancedToggle = PanelToggle.CreateNewEntry(tabRoot);
            advancedToggle.Descriptor.SetTitle(BasisLocalization.Get("trackerLinking.advanced"));
            advancedToggle.Descriptor.SetDescription(BasisLocalization.Get("trackerLinking.advanced.description"));
            advancedToggle.AssignBinding(BasisSettingsDefaults.TrackerLinkingAdvancedVisible);

            // Static tuning group — bound to BasisSettingsDefaults bindings, so values
            // persist across menu reopens and across sessions. Built once.
            PanelElementDescriptor tuningGroup = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, tabRoot);
            tuningGroup.SetTitle(BasisLocalization.Get("trackerLinking.tuning.title"));
            tuningGroup.SetDescription(BasisLocalization.Get("trackerLinking.tuning.description"));
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

            tabPage.OnInstanceReleased += () =>
            {
                BasisObservableList<BasisInput> currentDevices = BasisDeviceManagement.Instance?.AllInputDevices;
                if (currentDevices != null)
                {
                    currentDevices.OnListChanged -= handleChange;
                }
                BasisTrackerPairing.OnPairingsChanged -= handleChange;
                BasisTrackerRoleOverride.OnOverridesChanged -= handleChange;
                state.TrackersContainer = null;
                state.TrackersGroup = null;
                state.TabDescriptor = null;
                state.HasBuilt = false;
                state.Entries.Clear();
            };

            // Page-level reset stays on tabRoot (not inside tuningGroup) so it
            // remains reachable when the advanced toggle hides the sliders.
            SettingsProvider.AddResetPageButton(tabRoot, TabKey, ResetTrackerSettingsDefaults);

            // Initial visibility + OnValueChanged gating. Two-step rebuild
            // (inner group, then tab descriptor) matches the existing pattern
            // in HandleChange so nested LayoutGroups settle correctly.
            tuningGroup.gameObject.SetActive(BasisSettingsDefaults.TrackerLinkingAdvancedVisible.RawValue);
            advancedToggle.OnValueChanged += visible =>
            {
                tuningGroup.gameObject.SetActive(visible);
                tuningGroup.ForceRebuild();
                tabDesc.ForceRebuild();
            };

            trackersGroup.gameObject.SetActive(BasisSettingsDefaults.TrackerLinkingConnectorVisible.RawValue);
            connectorToggle.OnValueChanged += visible =>
            {
                trackersGroup.gameObject.SetActive(visible);
                trackersGroup.ForceRebuild();
                tabDesc.ForceRebuild();
            };

            handleChange();
            tabDesc.ForceRebuild();
            return tabPage;
        }

        private static void HandleChange(TabState state)
        {
            if (state.TrackersContainer == null) return;

            List<BasisInput> trackers = CollectEligibleTrackers();
            List<string> newIds = new List<string>(trackers.Count);
            for (int i = 0; i < trackers.Count; i++)
            {
                newIds.Add(trackers[i].UniqueDeviceIdentifier);
            }

            // Full rebuild on the first call (otherwise the empty list path
            // would skip the "no trackers" message), any time the eligible-
            // tracker set has changed (a tracker connected, disconnected, or
            // shifted position in the device list — every case where the
            // dropdown rows need to be added/removed/reordered), and as a
            // self-healing fallback if the cached entry count has somehow
            // gone out of sync with the tracker count.
            //
            // Otherwise — pairing/override graph changed but eligible trackers
            // are the same set in the same order — we update the existing
            // dropdowns in place. This is the path that protects the user's
            // mid-click dropdown from being torn down underneath them.
            bool needsRebuild = !state.HasBuilt
                || !IdsMatch(state.Entries, newIds)
                || state.Entries.Count != trackers.Count;

            if (needsRebuild)
            {
                FullRebuild(state, trackers, newIds);
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

            RefreshExistingEntries(state, trackers, newIds);
        }

        private static bool IdsMatch(List<TabEntry> entries, List<string> b)
        {
            if (entries.Count != b.Count) return false;
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].Id != b[i]) return false;
            }
            return true;
        }

        private static void RefreshExistingEntries(TabState state, List<BasisInput> trackers, List<string> ids)
        {
            state.SuppressDropdownEvents[0] = true;
            for (int i = 0; i < trackers.Count; i++)
            {
                BasisInput input = trackers[i];
                string id = input.UniqueDeviceIdentifier;

                if (i >= state.Entries.Count) continue;
                TabEntry entry = state.Entries[i];

                if (entry.Group != null && !entry.Group.IsReleased)
                {
                    entry.Group.SetDescription(BuildEntryDescription(input));
                }
                if (entry.LinkDropdown != null && !entry.LinkDropdown.IsReleased)
                {
                    entry.LinkDropdown.SetValueWithoutNotify(ResolveCurrentLink(id, ids));
                }
                if (entry.RoleDropdown != null && !entry.RoleDropdown.IsReleased)
                {
                    entry.RoleDropdown.SetValueWithoutNotify(ResolveCurrentRoleOverride(id));
                }
            }
            state.SuppressDropdownEvents[0] = false;
        }

        private static void FullRebuild(TabState state, List<BasisInput> trackers, List<string> newIds)
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

            if (trackers.Count == 0)
            {
                PanelElementDescriptor empty = PanelElementDescriptor.CreateNew(
                    PanelElementDescriptor.ElementStyles.Group, state.TrackersContainer);
                empty.SetTitle(BasisLocalization.Get("trackerLinking.noTrackers.title"));
                empty.SetDescription(BasisLocalization.Get("trackerLinking.noTrackers.description"));
                return;
            }

            // Suppress dropdown change events while we set initial values —
            // without this, AssignEntries + SetValueWithoutNotify can still fire
            // callbacks depending on prefab configuration, and we'd recursively
            // rebuild as we cascade through dropdowns.
            state.SuppressDropdownEvents[0] = true;
            for (int i = 0; i < trackers.Count; i++)
            {
                BuildEntryFor(state, trackers[i], newIds);
            }
            state.SuppressDropdownEvents[0] = false;
        }

        private static void BuildEntryFor(TabState state, BasisInput input, List<string> allIds)
        {
            string id = input.UniqueDeviceIdentifier;

            PanelElementDescriptor group = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, state.TrackersContainer);
            group.SetTitle(id);
            group.SetDescription(BuildEntryDescription(input));

            PanelDropdown linkDropdown = PanelDropdown.CreateNewEntry(group.ContentParent);
            linkDropdown.Descriptor.SetTitle(BasisLocalization.Get("trackerLinking.linkLabel"));
            linkDropdown.Descriptor.SetDescription(BasisLocalization.Get("trackerLinking.linkDescription"));

            List<string> linkEntries = new List<string>(allIds.Count) { UnlinkedLabel };
            for (int i = 0; i < allIds.Count; i++)
            {
                if (allIds[i] == id) continue;
                linkEntries.Add(allIds[i]);
            }
            linkDropdown.AssignEntries(linkEntries);
            linkDropdown.SetValueWithoutNotify(ResolveCurrentLink(id, allIds));

            string capturedId = id;
            bool[] suppressFlag = state.SuppressDropdownEvents;
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

            PanelDropdown roleDropdown = PanelDropdown.CreateNewEntry(group.ContentParent);
            roleDropdown.Descriptor.SetTitle(BasisLocalization.Get("trackerLinking.roleOverrideLabel"));
            roleDropdown.Descriptor.SetDescription(BasisLocalization.Get("trackerLinking.roleOverrideDescription"));

            List<string> roleEntries = BuildRoleEntries();
            roleDropdown.AssignEntries(roleEntries);
            roleDropdown.SetValueWithoutNotify(ResolveCurrentRoleOverride(id));

            roleDropdown.OnValueChanged += newValue =>
            {
                if (suppressFlag[0]) return;
                if (string.IsNullOrEmpty(newValue) || newValue == AutoRoleLabel())
                {
                    BasisTrackerRoleOverride.ClearOverride(capturedId);
                    return;
                }
                if (TryParseOverrideRole(newValue, out BasisBoneTrackedRole parsed))
                {
                    BasisTrackerRoleOverride.SetOverride(capturedId, parsed);
                }
            };

            state.Entries.Add(new TabEntry
            {
                Id = id,
                Group = group,
                LinkDropdown = linkDropdown,
                RoleDropdown = roleDropdown,
            });
        }

        private static string BuildEntryDescription(BasisInput input)
        {
            if (input.TryGetRole(out BasisBoneTrackedRole role))
            {
                return BasisLocalization.Get("trackerLinking.entry.role", role.ToString());
            }
            return BasisLocalization.Get("trackerLinking.entry.unassigned");
        }

        private static string ResolveCurrentLink(string id, List<string> eligibleIds)
        {
            if (BasisTrackerPairing.TryGetPartner(id, out string partner) && eligibleIds.Contains(partner))
            {
                return partner;
            }
            return UnlinkedLabel;
        }

        private static string ResolveCurrentRoleOverride(string id)
        {
            if (BasisTrackerRoleOverride.TryGetOverride(id, out BasisBoneTrackedRole role))
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
            // Surprise penalty: how aggressively a glitching half loses authority.
            PanelSlider penalty = PanelSlider.CreateAndBind(parent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("trackerLinking.tuning.surprisePenalty"),
                    0.5f, 10f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.PairingSurprisePenalty);
            if (penalty != null)
            {
                penalty.Descriptor.SetDescription(BasisLocalization.Get("trackerLinking.tuning.surprisePenalty.description"));
            }

            // Surprise clamp: above this, the velocity baseline freezes.
            PanelSlider clamp = PanelSlider.CreateAndBind(parent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("trackerLinking.tuning.surpriseClamp"),
                    1.5f, 10f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.PairingSurpriseClamp);
            if (clamp != null)
            {
                clamp.Descriptor.SetDescription(BasisLocalization.Get("trackerLinking.tuning.surpriseClamp.description"));
            }

            // EMA floor (in meters/frame) — guard against divide-by-zero on a
            // frozen tracker. Fine-grained because typical motion is mm/frame.
            PanelSlider floor = PanelSlider.CreateAndBind(parent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("trackerLinking.tuning.emaFloor"),
                    0.0005f, 0.05f, false, 4, ValueDisplayMode.Meters),
                BasisSettingsDefaults.PairingEmaFloor);
            if (floor != null)
            {
                floor.Descriptor.SetDescription(BasisLocalization.Get("trackerLinking.tuning.emaFloor.description"));
            }

            // Soft-snap correction cap.
            PanelSlider maxCorrection = PanelSlider.CreateAndBind(parent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("trackerLinking.tuning.maxCorrection"),
                    0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.PairingMaxCorrectionStrength);
            if (maxCorrection != null)
            {
                maxCorrection.Descriptor.SetDescription(BasisLocalization.Get("trackerLinking.tuning.maxCorrection.description"));
            }

            // Soft-snap half-life (in meters of error).
            PanelSlider halfLife = PanelSlider.CreateAndBind(parent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("trackerLinking.tuning.softSnapHalfLife"),
                    0.005f, 0.5f, false, 3, ValueDisplayMode.Meters),
                BasisSettingsDefaults.PairingSoftSnapHalfLife);
            if (halfLife != null)
            {
                halfLife.Descriptor.SetDescription(BasisLocalization.Get("trackerLinking.tuning.softSnapHalfLife.description"));
            }

            // Lockstep tolerance (in meters of error).
            PanelSlider lockstep = PanelSlider.CreateAndBind(parent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("trackerLinking.tuning.lockstepTolerance"),
                    0.005f, 0.5f, false, 3, ValueDisplayMode.Meters),
                BasisSettingsDefaults.PairingLockstepTolerance);
            if (lockstep != null)
            {
                lockstep.Descriptor.SetDescription(BasisLocalization.Get("trackerLinking.tuning.lockstepTolerance.description"));
            }

            // Velocity-baseline EMA alpha.
            PanelSlider emaAlpha = PanelSlider.CreateAndBind(parent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("trackerLinking.tuning.emaAlpha"),
                    0.005f, 0.5f, false, 3, ValueDisplayMode.Raw),
                BasisSettingsDefaults.PairingEmaAlpha);
            if (emaAlpha != null)
            {
                emaAlpha.Descriptor.SetDescription(BasisLocalization.Get("trackerLinking.tuning.emaAlpha.description"));
            }

            // Rest-distance EMA alpha.
            PanelSlider distEmaAlpha = PanelSlider.CreateAndBind(parent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("trackerLinking.tuning.distanceEmaAlpha"),
                    0.005f, 0.5f, false, 3, ValueDisplayMode.Raw),
                BasisSettingsDefaults.PairingDistanceEmaAlpha);
            if (distEmaAlpha != null)
            {
                distEmaAlpha.Descriptor.SetDescription(BasisLocalization.Get("trackerLinking.tuning.distanceEmaAlpha.description"));
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
                weightSmoothing.Descriptor.SetDescription(BasisLocalization.Get("trackerLinking.tuning.weightSmoothing.description"));
            }
        }

        private static void ResetTrackerSettingsDefaults()
        {
            BasisSettingsDefaults.TrackerLinkingAdvancedVisible.ResetToDefault();
            BasisSettingsDefaults.TrackerLinkingConnectorVisible.ResetToDefault();
            BasisSettingsDefaults.PairingSurprisePenalty.ResetToDefault();
            BasisSettingsDefaults.PairingSurpriseClamp.ResetToDefault();
            BasisSettingsDefaults.PairingEmaFloor.ResetToDefault();
            BasisSettingsDefaults.PairingMaxCorrectionStrength.ResetToDefault();
            BasisSettingsDefaults.PairingSoftSnapHalfLife.ResetToDefault();
            BasisSettingsDefaults.PairingLockstepTolerance.ResetToDefault();
            BasisSettingsDefaults.PairingEmaAlpha.ResetToDefault();
            BasisSettingsDefaults.PairingDistanceEmaAlpha.ResetToDefault();
            BasisSettingsDefaults.PairingWeightSmoothing.ResetToDefault();
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
