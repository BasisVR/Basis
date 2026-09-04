using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using Basis.Scripts.Rendering;
using Basis.Network.Core;
using BasisNetworkClient;
using BasisPermissions;
using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using Basis.Scripts.Settings;
using GatorDragonGames.JigglePhysics;

namespace Basis.BasisUI
{
    public partial class SettingsProvider : BasisMenuActionProvider<BasisMainMenu>
    {
        private const string ChatTabKey = "settings.tab.chat";
        private static string _pendingTabKey;
        private static string _sessionTabKey = string.Empty;
        private static string _lastSelectedTabKey
        {
            get => BasisMenuStateMemory.Enabled ? BasisMenuStateMemory.ActiveTab : _sessionTabKey;
            set
            {
                _sessionTabKey = value;
                if (BasisMenuStateMemory.Enabled)
                {
                    BasisMenuStateMemory.ActiveTab = value;
                }
            }
        }
        private static PanelTextField _chatTextField;
        private static string _pendingChatComposerText;
        private static bool _pendingChatComposerFocus;
        private static bool _pendingChatComposerPlaySound;
        private static bool _chatComposerPlayNotificationSound = true;

        /// <summary>
        /// Maps a tab localization key to the index of its button inside
        /// <see cref="PanelTabGroup.SelectionButtons"/>. Rebuilt each time the
        /// Settings menu is opened so navigation survives a language switch.
        /// </summary>
        private static readonly Dictionary<string, int> _tabKeyToIndex = new();

        /// <summary>
        /// External packages can register additional settings tabs here via [RuntimeInitializeOnLoadMethod].
        /// Each entry is (tabName, builder) where builder receives the PanelTabGroup and returns a PanelTabPage.
        /// </summary>
        public static readonly List<(string TabName, Func<PanelTabGroup, PanelTabPage> Builder)> ExternalTabs = new();

        /// <summary>
        /// External hook for the Developer tab's "Debug Face Tracking" section.
        /// The comms package owns the face tracking pipeline types (relays, OSC,
        /// blendshape actuation) and registers a builder here that populates the
        /// passed-in container with live diagnostic fields.
        /// </summary>
        public static Action<RectTransform> FaceTrackingDebugBuilder;

        /// <summary>
        /// External hook for the Developer tab's "Debug Eye Tracking" section.
        /// Same shape as <see cref="FaceTrackingDebugBuilder"/> — the comms
        /// package registers a builder that populates the container.
        /// </summary>
        public static Action<RectTransform> EyeTrackingDebugBuilder;

        public static Action<RectTransform> AvatarCustomizationBuilder;

        /// <summary>
        /// External hook to append extra sections to the Tracker Settings tab. Packages
        /// (e.g. com.basis.mediapipe) register a builder here that populates the passed-in
        /// tab content with their own controls.
        /// </summary>
        public static Action<RectTransform> TrackerSettingsExtraBuilder;

        /// <summary>
        /// External hook to append a section to the Audio tab. Packages (e.g.
        /// com.basis.mediaplayer) register a builder that populates the passed-in
        /// tab content with their own controls.
        /// </summary>
        public static Action<RectTransform> AudioTabExtraBuilder;

        public static Action<RectTransform> LicensesBuilder;

        /// <summary>
        /// Feature packages append a Developer-tab section builder here (typically via
        /// [RuntimeInitializeOnLoadMethod]); <see cref="DeveloperTab"/> invokes each one so a
        /// package can own its diagnostics UI without the framework referencing the package.
        /// </summary>
        public static readonly List<Action<RectTransform>> DeveloperSectionBuilders = new();

        /// <summary>
        /// Reset callbacks paired with <see cref="DeveloperSectionBuilders"/>; the Developer
        /// page's reset button runs these so a package can reset its own settings.
        /// </summary>
        public static readonly List<Action> DeveloperResetActions = new();

        [RuntimeInitializeOnLoadMethod]
        public static void AddToMenu()
        {
            BasisMenuBase<BasisMainMenu>.AddProvider(new SettingsProvider());
#if !BASIS_DISABLE_MICROPHONE
            SMDMicrophone.OnMicrophoneSettingsChanged += SyncUiFromSnapshot;
            SMDMicrophone.OnMicrophoneDevicesChanged += RefreshMicrophoneDeviceEntries;
#endif
            ApplyOpenLipSyncMaxSlots();
            BasisSettingsSystem.OnSettingsFinishedChanges += ApplyOpenLipSyncMaxSlots;
            ApplyJiggleCollisionCulling();
            BasisSettingsSystem.OnSettingsFinishedChanges += ApplyJiggleCollisionCulling;
            BasisJiggleColliderLOD.ApplyFromSettings();
            BasisSettingsSystem.OnSettingsFinishedChanges += BasisJiggleColliderLOD.ApplyFromSettings;
            BasisJiggleSimulationLOD.ApplyFromSettings();
            BasisSettingsSystem.OnSettingsFinishedChanges += BasisJiggleSimulationLOD.ApplyFromSettings;
            BasisAvatarSkinLOD.ApplyFromSettings();
            BasisSettingsSystem.OnSettingsFinishedChanges += BasisAvatarSkinLOD.ApplyFromSettings;
            BasisAvatarShadowLOD.ApplyFromSettings();
            BasisSettingsSystem.OnSettingsFinishedChanges += BasisAvatarShadowLOD.ApplyFromSettings;
            Basis.Scripts.Rendering.BasisVisibilitySystem.ApplyFromSettings();
            BasisSettingsSystem.OnSettingsFinishedChanges += Basis.Scripts.Rendering.BasisVisibilitySystem.ApplyFromSettings;
            BasisAvatarFarLOD.ApplyFromSettings();
            BasisSettingsSystem.OnSettingsFinishedChanges += BasisAvatarFarLOD.ApplyFromSettings;
            ApplyDesktopInputInVR();
            BasisSettingsSystem.OnSettingsFinishedChanges += ApplyDesktopInputInVR;
        }

        private static void ApplyDesktopInputInVR()
        {
            string mode = BasisSettingsDefaults.DesktopInputInVR.RawValue;
            if (string.Equals(mode, BasisSettingsDefaults.DesktopInputInVR_AlwaysOn, StringComparison.Ordinal))
            {
                BasisInputSystemPump.Mode = BasisInputPumpMode.AllInputs;
            }
            else if (string.Equals(mode, BasisSettingsDefaults.DesktopInputInVR_Off, StringComparison.Ordinal))
            {
                BasisInputSystemPump.Mode = BasisInputPumpMode.VRDesktopInputOff;
            }
            else
            {
                BasisInputSystemPump.Mode = BasisInputPumpMode.Adaptive;
            }
        }

        private static void ApplyOpenLipSyncMaxSlots()
        {
            BasisOpenLipSyncDriver.UseSlotLimit = BasisSettingsDefaults.UseOpenLipSyncLimit.RawValue;
            BasisOpenLipSyncDriver.MaxSlots = Mathf.Max(0, (int)BasisSettingsDefaults.OpenLipSyncMaxSlots.RawValue);
            BasisOpenLipSyncDriver.EnforceSlotLimit();
        }

        private static void ApplyJiggleCollisionCulling()
        {
            JiggleSettings.CullFrustumExpansion = BasisSettingsDefaults.JiggleCullFrustumExpansion.RawValue;
            JiggleSettings.CullNearKeepRadius = BasisSettingsDefaults.JiggleCullNearKeepRadius.RawValue;
            JigglePhysics.SetCollisionCulling(
                BasisSettingsDefaults.UseJiggleCollisionFrustumCull.RawValue,
                BasisSettingsDefaults.UseJiggleCollisionDistanceCull.RawValue,
                Mathf.Max(0f, BasisSettingsDefaults.JiggleCollisionCullDistance.RawValue));
        }

        private static float appliedJiggleBroadPhaseCellSize = float.NaN;

        public static void ApplyJiggleStartupSettings()
        {
            JiggleSettings.BroadPhaseCellSize = BasisSettingsDefaults.JiggleBroadPhaseCellSize.RawValue;
            appliedJiggleBroadPhaseCellSize = JiggleSettings.BroadPhaseCellSize;
        }

        public static bool JiggleBroadPhaseCellSizeNeedsRestart =>
            !float.IsNaN(appliedJiggleBroadPhaseCellSize)
            && !Mathf.Approximately(appliedJiggleBroadPhaseCellSize,
                Mathf.Max(0.01f, BasisSettingsDefaults.JiggleBroadPhaseCellSize.RawValue));

        public const string StaticTitleKey = "settings.title";
        public static string StaticTitle => BasisLocalization.Get(StaticTitleKey);
        public override string Title => StaticTitle;
        public override string IconAddress => AddressableAssets.Sprites.Settings;
        public override int Order => 10;
        public override bool Hidden => false;

        /// <summary>
        /// Opens the Settings menu and navigates directly to the specified tab.
        /// The <paramref name="tabKey"/> is the same localization key that was
        /// registered via <see cref="AddLazyTab"/>, so navigation is
        /// language-independent.
        /// </summary>
        public static void OpenToTab(string tabKey)
        {
            _pendingTabKey = tabKey;
            BasisMainMenu.OpenWithProvider(StaticTitle);
        }

        /// <summary>
        /// Opens the Settings menu and navigates directly to the Body Tracking tab.
        /// </summary>
        public static void OpenBodyTrackingTab()
        {
            OpenToTab("settings.tab.bodytracking");
        }

        public static void OpenChatComposer(string presetText, bool focusInput, bool playSound)
        {
            _pendingChatComposerText = BasisChatSanitizer.Sanitize(presetText);
            _pendingChatComposerFocus = focusInput;
            _pendingChatComposerPlaySound = playSound;
            OpenToTab(ChatTabKey);
        }

        private static void NavigateToTab(PanelTabGroup tabGroup, string tabKey)
        {
            if (string.IsNullOrEmpty(tabKey))
            {
                return;
            }

            if (_tabKeyToIndex.TryGetValue(tabKey, out int index) &&
                index >= 0 && index < tabGroup.SelectionButtons.Count)
            {
                PanelButton button = tabGroup.SelectionButtons[index];
                if (button == null || !button.gameObject.activeSelf)
                {
                    return;
                }
                button.OnClicked?.Invoke();

                if (tabKey == ChatTabKey)
                {
                    ApplyPendingChatComposerRequest();
                }
            }
        }

        private const string ModeratorTabKey = "settings.tab.moderator";
        private const string AdminTabKey = "settings.tab.admin";

        /// <summary>
        /// Shows or hides the permission-gated tabs to match the local player's current
        /// permissions. Runs at panel build and again whenever the server re-sends permissions,
        /// so being promoted or demoted while Settings is open updates the tab strip immediately.
        /// </summary>
        private static void ApplyPermissionGatedTabs()
        {
            PanelTabGroup tabGroup = _searchTabGroup;
            if (tabGroup == null)
            {
                return;
            }

            HashSet<string> perms = BasisNetworkManagement.LocalPermissions;
            SetTabVisible(tabGroup, ModeratorTabKey, perms != null && perms.Contains(PermNodes.PlayerModeration));
            SetTabVisible(tabGroup, AdminTabKey, perms != null && perms.Contains(PermNodes.PermissionsView));
        }

        private static void SetTabVisible(PanelTabGroup tabGroup, string tabKey, bool visible)
        {
            if (!_tabKeyToIndex.TryGetValue(tabKey, out int index) ||
                index < 0 || index >= tabGroup.SelectionButtons.Count)
            {
                return;
            }

            PanelButton button = tabGroup.SelectionButtons[index];
            if (button == null || button.gameObject.activeSelf == visible)
            {
                return;
            }

            button.gameObject.SetActive(visible);
            if (!visible && tabGroup.Value == index)
            {
                NavigateToTab(tabGroup, "settings.tab.general");
            }
        }

        public override void RunAction()
        {
            if (BasisMainMenu.ActiveMenuTitle == Title)
            {
                BasisMainMenu.CloseActivePanel();
                return;
            }

            BasisMenuPanel panel = BasisMainMenu.CreateActiveMenu(
                BasisMenuPanel.PanelData.Standard(Title),
                BasisMenuPanel.PanelStyles.Page,
                this);

            TextMeshProUGUI TitleLabel = panel.Descriptor.TitleLabel;
            BasisFrameRateVisualization FRV = TitleLabel.gameObject.AddComponent<BasisFrameRateVisualization>();
            FRV.Title = Title;
            FRV.fpsText = TitleLabel;

            BoundButton?.BindActiveStateToAddressablesInstance(panel);

            PanelTabGroup tabGroup = PanelTabGroup.CreateNew(panel.Descriptor.ContentParent, LayoutDirection.Vertical);
            _tabKeyToIndex.Clear();
            ResetSearch(tabGroup);

            // Search lives on the header, not on every page. The popup outlives nothing: switching to
            // another provider tears this panel down, and a palette still listing its settings would
            // navigate into a menu that is no longer there.
            BasisPanelMoveHandle.SetPanelSearch(panel, Title, CollectSearchResults);
            panel.OnInstanceReleased += BasisPanelSearchPopup.Close;

            // First tab is eager (shown immediately on open)
            const string generalKey = "settings.tab.general";
            _tabKeyToIndex[generalKey] = 0;
            PanelTabPage generalPage;
            BasisMenuStateMemory.BeginScope(generalKey);
            try
            {
                generalPage = GeneralTab(tabGroup);
            }
            finally
            {
                BasisMenuStateMemory.EndScope();
            }
            tabGroup.AddTab(BasisLocalization.Get(generalKey), () =>
            {
                _lastSelectedTabKey = generalKey;
                BindPageReset(generalKey, generalPage);
            }, generalPage);
            PanelScrollMemory.Attach(generalPage.Descriptor.ContentParent, generalKey);
            AttachTabSearch(generalKey, 0, generalPage);
            // Remaining tabs are lazy-loaded on first selection to reduce stuttering
            AddLazyTab(tabGroup, "settings.tab.audio", () => AudioTab(tabGroup));
            AddLazyTab(tabGroup, "settings.tab.microphone", () => MicrophoneTab(tabGroup));
            AddLazyTab(tabGroup, "settings.tab.graphics", () => GraphicsTab(tabGroup));
            AddLazyTab(tabGroup, "settings.tab.myavatar", () => SettingsProviderAvatarStats.AvatarStatsTab(tabGroup));
            AddLazyTab(tabGroup, "settings.tab.controls", () => SettingsProviderControllerConfig.OpenControllerConfig(tabGroup));
            AddLazyTab(tabGroup, ChatTabKey, () => ChatTab(tabGroup));
            AddLazyTab(tabGroup, "settings.tab.bodytracking", () => SettingsProviderIK.IKTab(tabGroup));
            AddLazyTab(tabGroup, "settings.tab.trackerlinking", () => SettingsProviderTrackerSettings.TrackerSettingsTab(tabGroup));
            AddLazyTab(tabGroup, "settings.tab.downloadsurls", () => SettingsProviderStorage.DownloadsUrlsTab(tabGroup));
          //  AddLazyTab(tabGroup, "settings.tab.uistyle", () => SettingsProviderUIStyle.UIStyleTab(tabGroup));
            if (BasisSettingsDefaults.ShowDeveloperTab.RawValue)
            {
                AddLazyTab(tabGroup, "settings.tab.developer", () => DeveloperTab(tabGroup));
            }
            if (SettingsProvider.LicensesBuilder != null)
            {
                AddLazyTab(tabGroup, "settings.tab.thirdpartylicenses", () =>
                {
                    PanelTabPage tab = PanelTabPage.CreateVertical(tabGroup.Descriptor.ContentParent);
                    PanelElementDescriptor descriptor = tab.Descriptor;
                    descriptor.SetIcon(AddressableAssets.Sprites.Settings);
                    descriptor.SetTitle(BasisLocalization.Get("settings.tab.thirdpartylicenses"));
                    descriptor.ForceRebuild();

                    SettingsProvider.LicensesBuilder.Invoke(tab.Descriptor.ContentParent);
                    return tab;
                });
            }

            // External package tabs (registered via SettingsProvider.ExternalTabs).
            // TabName is treated as a localization key — packages that don't localize
            // can still register with a plain English string, which falls back to itself.
            for (int i = 0; i < ExternalTabs.Count; i++)
            {
                var ext = ExternalTabs[i];
                AddLazyTab(tabGroup, ext.TabName, () => ext.Builder(tabGroup));
            }

            AddLazyTab(tabGroup, ModeratorTabKey, () => SettingsProviderModeratorTab.ModeratorTab(tabGroup));
            AddLazyTab(tabGroup, AdminTabKey, () => SettingsProviderAdminTab.AdminTab(tabGroup));
            ApplyPermissionGatedTabs();
            BasisNetworkManagement.OnlocalPermissionsChanged -= ApplyPermissionGatedTabs;
            BasisNetworkManagement.OnlocalPermissionsChanged += ApplyPermissionGatedTabs;

            // Navigate to a specific tab if requested via OpenToTab, otherwise
            // restore the tab the user was on the last time Settings was open.
            if (!string.IsNullOrEmpty(_pendingTabKey))
            {
                NavigateToTab(tabGroup, _pendingTabKey);
                _pendingTabKey = null;
            }
            else if (!string.IsNullOrEmpty(_lastSelectedTabKey))
            {
                NavigateToTab(tabGroup, _lastSelectedTabKey);
            }

            panel.Descriptor.ForceRebuild();
        }

        public override void OnReleaseEvent()
        {
            BasisNetworkManagement.OnlocalPermissionsChanged -= ApplyPermissionGatedTabs;
            ClearChatComposerReference();
            ResetSearch(null);
        }

        /// <summary>
        /// A tab whose page has not been built yet. Selecting it is the usual trigger, but search
        /// realizes them too — a setting the user has never opened the tab for still has to be
        /// findable — so building and showing are kept apart.
        /// </summary>
        private sealed class LazyTab
        {
            public string Key;
            public int Index;
            public Func<PanelTabPage> Builder;
            public PanelTabPage Placeholder;
            public PanelTabPage Page;
            public bool Built;
        }

        private static readonly List<LazyTab> _lazyTabs = new();

        /// <summary>
        /// Adds a tab with an empty placeholder page. On first selection the real
        /// content is built, the placeholder is released, and the Pages entry is swapped.
        /// <paramref name="tabKey"/> is the localization key used both for the
        /// displayed label and for stable navigation across language changes.
        /// </summary>
        private static void AddLazyTab(PanelTabGroup tabGroup, string tabKey, Func<PanelTabPage> builder)
        {
            PanelTabPage placeholder = PanelTabPage.CreateVertical(tabGroup.Descriptor.ContentParent);
            LazyTab tab = new LazyTab
            {
                Key = tabKey,
                Index = tabGroup.Pages.Count,
                Builder = builder,
                Placeholder = placeholder,
            };
            _lazyTabs.Add(tab);
            _tabKeyToIndex[tabKey] = tab.Index;

            tabGroup.AddTab(BasisLocalization.Get(tabKey), () =>
            {
                _lastSelectedTabKey = tabKey;
                PanelTabPage page = RealizeTab(tabGroup, tab, forSearch: false);
                BindPageReset(tabKey, page);
            }, placeholder);
        }

        /// <summary>
        /// Builds a lazy tab's real page and swaps it in for the placeholder. When
        /// <paramref name="forSearch"/> is set the page is built without being shown, so indexing a
        /// tab the user has not selected does not flash it over the one they are on.
        /// </summary>
        private static PanelTabPage RealizeTab(PanelTabGroup tabGroup, LazyTab tab, bool forSearch)
        {
            if (tab.Built) return tab.Page;
            tab.Built = true;

            PanelTabPage realPage;
            BasisMenuStateMemory.BeginScope(tab.Key);
            try
            {
                realPage = tab.Builder();
            }
            finally
            {
                BasisMenuStateMemory.EndScope();
            }

            tab.Page = realPage;
            tabGroup.Pages[tab.Index] = realPage;
            PanelScrollMemory.Attach(realPage.Descriptor.ContentParent, tab.Key);

            if (tab.Placeholder != null && !tab.Placeholder.IsReleased)
            {
                tab.Placeholder.ReleaseInstance();
            }
            tab.Placeholder = null;

            // Attach before hiding: the field and its rows initialize off OnEnable, which never runs
            // for anything instantiated under a page that is already switched off.
            AttachTabSearch(tab.Key, tab.Index, realPage);
            if (forSearch) realPage.HideImmediate();
            return realPage;
        }


        // ------------------
        // RESET HELPERS (ONE PER PAGE)
        // ------------------

        /// <summary>
        /// What "reset this page" means, per tab. Populated as each tab builds and read back when
        /// that tab is shown, so the panel's Reset button always offers the page in front of the
        /// user rather than whichever one happened to be built last.
        /// </summary>
        private static readonly Dictionary<string, Action> _pageResets = new();

        /// <summary>
        /// Registers this page's "back to defaults" action. Running it also closes the menu and
        /// reopens Settings on the same tab, which is how the page picks up the new values.
        /// <para>
        /// There is no longer a "Reset &lt;page&gt;" button in the page itself — the panel header's
        /// Reset offers it, alongside putting the panel back where it started, so one control covers
        /// both meanings of "reset" instead of two that are easy to confuse.
        /// </para>
        /// <paramref name="tabKey"/> is the localization key registered via <see cref="AddLazyTab"/>;
        /// it resolves the page label and navigates back after the reset.
        /// </summary>
        public static void RegisterPageReset(string tabKey, Action resetAction)
        {
            _pageResets[tabKey] = () =>
            {
                resetAction?.Invoke();
                BasisMainMenu.Close();
                OpenToTab(tabKey);
            };
        }

        /// <summary>
        /// Points the panel header's Reset at the page now on show. Runs on every tab selection,
        /// after the tab has been realized, so a lazily built page has already registered itself.
        /// </summary>
        private static void BindPageReset(string tabKey, PanelTabPage page)
        {
            if (BasisMainMenu.Instance == null)
            {
                return;
            }

            // Assign unconditionally, including the pages that have nothing to reset. Skipping those
            // would leave the last page's action in place and offer "Reset Graphics" to someone
            // looking at My Avatar.
            _pageResets.TryGetValue(tabKey, out Action reset);
            string pageName = reset == null ? null : BasisLocalization.Get(tabKey);

            BasisPanelMoveHandle.SetPanelReset(
                BasisMainMenu.Instance.ActiveMenu,
                reset == null ? null : BasisLocalization.Get("ui.resetPage.title", pageName),
                reset == null ? null : BasisLocalization.Get("menu.panel.reset.choose", pageName),
                reset,
                reset == null || page == null ? null : () => CollectChangedSettings(page));
        }

        /// <summary>
        /// Every bound control on <paramref name="page"/> whose setting is off its default, as
        /// rows for the reset dialogue's change list — the row's own title, then the value the way
        /// the control shows it next to the default it would go back to. Hidden and collapsed rows
        /// are included: a page reset puts those back too.
        /// </summary>
        private static List<BasisMenuDialoguePanel.DetailRow> CollectChangedSettings(PanelTabPage page)
        {
            if (page == null || page.IsReleased)
            {
                return null;
            }

            List<BasisMenuDialoguePanel.DetailRow> rows = new();
            HashSet<string> seen = new();
            string entryFormat = BasisLocalization.Get("menu.panel.reset.changed.entry");
            PanelComponent[] components = page.GetComponentsInChildren<PanelComponent>(true);
            for (int Index = 0; Index < components.Length; Index++)
            {
                if (!components[Index].TryDescribeSettingChange(out string label, out string current, out string standard) ||
                    !seen.Add(label + "\n" + current + "\n" + standard))
                {
                    continue;
                }

                rows.Add(new BasisMenuDialoguePanel.DetailRow(label, string.Format(entryFormat, current, standard)));
            }

            return rows;
        }

        // ------------------
        // GENERAL TAB (ONE RESET BUTTON)
        // ------------------
        public static PanelTabPage GeneralTab(PanelTabGroup tabGroup)
        {
            PanelTabPage tab = PanelTabPage.CreateVertical(tabGroup.Descriptor.ContentParent);
            PanelElementDescriptor descriptor = tab.Descriptor;
            descriptor.SetIcon(AddressableAssets.Sprites.Settings);
            descriptor.SetTitle(BasisLocalization.Get("settings.general.title"));

            RectTransform container = descriptor.ContentParent;

            SettingsProviderPlatform.BuildDeviceModeUI(container, _ => descriptor.ForceRebuild());

            BuildLanguageSelector(container, descriptor);

            // Range / visibility / audio-source-limit settings moved out of General:
            //   Avatar Range / Limit Avatars / View Cone Avatars → Graphics
            //   Hearing Range / Limit Audio Sources              → Audio
            //   Microphone Range                                 → Microphone

            PanelSectionToggleHelpers.CreateCollapsibleBoxedSection(container,
                BasisLocalization.Get("settings.general.interactions.title"), () =>
            {
                PanelToggle toggleRememberMenuState = PanelToggle.CreateNewEntry(container);
                toggleRememberMenuState.AssignBinding(BasisSettingsDefaults.RememberMenuState);
                toggleRememberMenuState.Descriptor.SetTitle(BasisLocalization.Get("settings.general.rememberMenuState"));
                toggleRememberMenuState.Descriptor.SetTooltip(BasisLocalization.Get("settings.general.rememberMenuState.tooltip"));

                PanelToggle toggleDisableSeats = PanelToggle.CreateNewEntry(container);
                toggleDisableSeats.AssignBinding(BasisSettingsDefaults.DisableSeats);
                toggleDisableSeats.Descriptor.SetTitle(BasisLocalization.Get("settings.general.disableSeats"));
                toggleDisableSeats.Descriptor.SetTooltip(BasisLocalization.Get("settings.general.disableSeats.tooltip"));

                PanelToggle toggleDisablePropPickup = PanelToggle.CreateNewEntry(container);
                toggleDisablePropPickup.AssignBinding(BasisSettingsDefaults.DisablePropPickup);
                toggleDisablePropPickup.Descriptor.SetTitle(BasisLocalization.Get("settings.general.disablePropPickup"));
                toggleDisablePropPickup.Descriptor.SetTooltip(BasisLocalization.Get("settings.general.disablePropPickup.tooltip"));

                PanelToggle toggleDisableVRAutoHold = PanelToggle.CreateNewEntry(container);
                toggleDisableVRAutoHold.AssignBinding(BasisSettingsDefaults.DisableVRAutoHold);
                toggleDisableVRAutoHold.Descriptor.SetTitle(BasisLocalization.Get("settings.general.disableVRAutoHold"));
                toggleDisableVRAutoHold.Descriptor.SetTooltip(BasisLocalization.Get("settings.general.disableVRAutoHold.tooltip"));

                PanelToggle toggleJiggleGrabInteractions = PanelToggle.CreateNewEntry(container);
                toggleJiggleGrabInteractions.AssignBinding(BasisSettingsDefaults.JiggleGrabInteractions);
                toggleJiggleGrabInteractions.Descriptor.SetTitle(BasisLocalization.Get("settings.general.jiggleGrabInteractions"));
                toggleJiggleGrabInteractions.Descriptor.SetTooltip(BasisLocalization.Get("settings.general.jiggleGrabInteractions.tooltip"));

                PanelToggle toggleUIHaptics = PanelToggle.CreateNewEntry(container);
                toggleUIHaptics.AssignBinding(BasisSettingsDefaults.UIHaptics);
                toggleUIHaptics.Descriptor.SetTitle(BasisLocalization.Get("settings.general.uiHaptics"));
                toggleUIHaptics.Descriptor.SetTooltip(BasisLocalization.Get("settings.general.uiHaptics.tooltip"));

                PanelToggle toggleHideRemoteCameras = PanelToggle.CreateNewEntry(container);
                toggleHideRemoteCameras.AssignBinding(BasisSettingsDefaults.HideRemoteCameraPucks);
                toggleHideRemoteCameras.Descriptor.SetTitle(BasisLocalization.Get("settings.general.hideRemoteCameras"));
                toggleHideRemoteCameras.Descriptor.SetTooltip(BasisLocalization.Get("settings.general.hideRemoteCameras.tooltip"));
            }, false, _ => descriptor.ForceRebuild());

            // HUD overlays — heads-up display elements rendered over the scene.
            PanelToggle toggleAvatarPreview = null;
            PanelToggle toggleAvatarPreviewMirror = null;
            PanelSectionToggleHelpers.CreateCollapsibleBoxedSection(container,
                BasisLocalization.Get("settings.general.hud.title"), () =>
            {
                PanelToggle toggleDesktopReticle = PanelToggle.CreateNewEntry(container);
                toggleDesktopReticle.AssignBinding(BasisSettingsDefaults.DesktopReticle);
                toggleDesktopReticle.Descriptor.SetTitle(BasisLocalization.Get("settings.general.desktopReticle"));
                toggleDesktopReticle.Descriptor.SetTooltip(BasisLocalization.Get("settings.general.desktopReticle.tooltip"));

                toggleAvatarPreview = PanelToggle.CreateNewEntry(container);
                toggleAvatarPreview.AssignBinding(BasisSettingsDefaults.AvatarPreview);
                toggleAvatarPreview.Descriptor.SetTitle(BasisLocalization.Get("settings.general.avatarPreview"));
                toggleAvatarPreview.Descriptor.SetTooltip(BasisLocalization.Get("settings.general.avatarPreview.tooltip"));

                toggleAvatarPreviewMirror = PanelToggle.CreateNewEntry(container);
                toggleAvatarPreviewMirror.AssignBinding(BasisSettingsDefaults.AvatarPreviewMirror);
                toggleAvatarPreviewMirror.Descriptor.SetTitle(BasisLocalization.Get("settings.general.avatarPreviewMirror"));
                toggleAvatarPreviewMirror.Descriptor.SetTooltip(BasisLocalization.Get("settings.general.avatarPreviewMirror.tooltip"));

                // Mirror is a sub-option of avatar preview — only show it when preview is on.
                toggleAvatarPreviewMirror.Descriptor.SetActive(BasisSettingsDefaults.AvatarPreview.RawValue);
                toggleAvatarPreview.OnValueChanged += val =>
                {
                    toggleAvatarPreviewMirror.Descriptor.SetActive(val);
                    descriptor.ForceRebuild();
                };
            }, false, visible =>
            {
                if (visible && toggleAvatarPreviewMirror != null)
                {
                    toggleAvatarPreviewMirror.Descriptor.SetActive(BasisSettingsDefaults.AvatarPreview.RawValue);
                }
                descriptor.ForceRebuild();
            });

            // Passthrough / mixed reality — standalone VR only (Quest).
            if (BasisDeviceManagement.IsMobileHardware() && BasisDeviceManagement.IsCurrentModeVR())
            {
                PanelSectionToggleHelpers.CreateCollapsibleBoxedSection(container,
                    BasisLocalization.Get("settings.general.passthrough.title"), () =>
                {
                    PanelToggle togglePassthrough = PanelToggle.CreateNewEntry(container);
                    togglePassthrough.AssignBinding(BasisSettingsDefaults.EnablePassthrough);
                    togglePassthrough.Descriptor.SetTitle(BasisLocalization.Get("settings.general.passthrough"));
                    togglePassthrough.Descriptor.SetTooltip(BasisLocalization.Get("settings.general.passthrough.tooltip"));
                }, false, _ => descriptor.ForceRebuild());
            }

            // Third-person camera is desktop-only; hide the entire section in VR/XR.
            if (BasisDeviceManagement.IsUserInDesktop())
            {
                PanelToggle toggleAudioFromHead = null;
                PanelSectionToggleHelpers.CreateCollapsibleBoxedSection(container,
                    BasisLocalization.Get("settings.general.camera.title"), () =>
                {
                    PanelToggle toggleThirdPerson = PanelToggle.CreateNewEntry(container);
                    toggleThirdPerson.AssignBinding(BasisSettingsDefaults.EnableThirdPersonCamera);
                    toggleThirdPerson.Descriptor.SetTitle(BasisLocalization.Get("settings.general.thirdPerson"));
                    toggleThirdPerson.Descriptor.SetTooltip(BasisLocalization.Get("settings.general.thirdPerson.tooltip"));

                    toggleAudioFromHead = PanelToggle.CreateNewEntry(container);
                    toggleAudioFromHead.AssignBinding(BasisSettingsDefaults.AudioListenerFollowsHead);
                    toggleAudioFromHead.Descriptor.SetTitle(BasisLocalization.Get("settings.general.thirdPerson.audioFromHead"));
                    toggleAudioFromHead.Descriptor.SetTooltip(BasisLocalization.Get("settings.general.thirdPerson.audioFromHead.tooltip"));

                    // Audio-from-head is a sub-option of third person — only show it when on.
                    toggleAudioFromHead.Descriptor.SetActive(BasisSettingsDefaults.EnableThirdPersonCamera.RawValue);
                    toggleThirdPerson.OnValueChanged += val =>
                    {
                        toggleAudioFromHead.Descriptor.SetActive(val);
                        descriptor.ForceRebuild();
                    };
                }, false, visible =>
                {
                    if (visible && toggleAudioFromHead != null)
                    {
                        toggleAudioFromHead.Descriptor.SetActive(BasisSettingsDefaults.EnableThirdPersonCamera.RawValue);
                    }
                    descriptor.ForceRebuild();
                });
            }

            BuildNetworkingSection(container, descriptor);

            BuildIdentitySection(container, descriptor);

            // ---- Backup & Restore ----
            // Lazy: the archive list is read off disk when the section opens, so a collapsed
            // section costs nothing and reopening it picks up files written since.
            PanelSectionToggleHelpers.CreateLazyBoxedSection(container,
                BasisLocalization.Get("settings.developer.backup.title"),
                () => SettingsProviderBackup.BuildSection(container, descriptor),
                false, _ => descriptor.ForceRebuild());

            PanelSectionToggleHelpers.CreateCollapsibleBoxedSection(container,
                BasisLocalization.Get("settings.general.developer.title"), () =>
            {
                PanelToggle toggleShowDeveloperTab = PanelToggle.CreateNewEntry(container);
                toggleShowDeveloperTab.AssignBinding(BasisSettingsDefaults.ShowDeveloperTab);
                toggleShowDeveloperTab.Descriptor.SetTitle(BasisLocalization.Get("settings.general.showDeveloperTab"));
                toggleShowDeveloperTab.Descriptor.SetTooltip(BasisLocalization.Get("settings.general.showDeveloperTab.tooltip"));
                toggleShowDeveloperTab.OnValueChanged += _ =>
                {
                    BasisMainMenu.Close();
                    OpenToTab("settings.tab.general");
                };
            }, false, _ => descriptor.ForceRebuild());

            // One reset button for this whole page
            RegisterPageReset("settings.tab.general", ResetGeneralDefaults);
            descriptor.ForceRebuild();
            return tab;
        }

        /// <summary>
        /// Builds the Language dropdown in the General tab. Each entry shows
        /// the native name (e.g. "日本語"); on selection the choice is persisted
        /// via BasisLocalization.SetLanguage and the menu is reopened so every
        /// string re-resolves against the new table.
        /// </summary>
        private static void BuildLanguageSelector(RectTransform container, PanelElementDescriptor tabDescriptor = null)
        {
            PanelSectionToggle languageToggle = PanelSectionToggle.CreateNewEntry(container);
            languageToggle.SetTitle(BasisLocalization.Get("settings.general.language.title"));
            int languageStart = container.childCount;

            PanelDropdown dropdownLanguage = PanelDropdown.CreateNewEntry(container);
            dropdownLanguage.Descriptor.SetTitle(BasisLocalization.Get("settings.general.language.title"));
            dropdownLanguage.Descriptor.SetTooltip(BasisLocalization.Get("settings.general.language.title.tooltip"));

            var languages = BasisLocalization.Available;
            var codes = new List<string>(languages.Count);
            var displayNames = new List<string>(languages.Count);
            int currentIndex = 0;
            for (int i = 0; i < languages.Count; i++)
            {
                codes.Add(languages[i].Code);
                displayNames.Add(languages[i].NativeName);
                if (languages[i].Code == BasisLocalization.CurrentLanguage)
                {
                    currentIndex = i;
                }
            }

            dropdownLanguage.AssignEntries(codes, displayNames);
            if (codes.Count > 0)
            {
                dropdownLanguage.SetValueWithoutNotify(codes[currentIndex]);
            }

            dropdownLanguage.OnValueChanged += (selected) =>
            {
                for (int i = 0; i < codes.Count; i++)
                {
                    if (codes[i] == selected)
                    {
                        BasisSettingsDefaults.Language.SetValue(codes[i]);
                        BasisLocalization.SetLanguage(codes[i]);
                        BasisMainMenu.Close();
                        OpenToTab("settings.tab.general");
                        return;
                    }
                }
            };

            PanelSectionToggleHelpers.FinalizeBoxedSectionFromIndex(languageToggle, container, languageStart, false,
                _ => tabDescriptor?.ForceRebuild());
        }

        private static void BuildNetworkingSection(RectTransform container, PanelElementDescriptor tabDescriptor = null)
        {
            // Open by default when at least one direct (P2P) connection is live.
            bool directConnected = BasisP2PManager.HasAnyConnectedSession();

            PanelSectionToggle networkingToggle = PanelSectionToggle.CreateNewEntry(container);
            networkingToggle.SetTitle(BasisLocalization.Get("settings.general.networking.title"));
            int networkingStart = container.childCount;

            PanelToggle toggleDirectConnections = PanelToggle.CreateNewEntry(container);
            toggleDirectConnections.Descriptor.SetTitle(BasisLocalization.Get("settings.general.networking.directConnections"));
            toggleDirectConnections.Descriptor.SetTooltip(BasisLocalization.Get("settings.general.networking.directConnections.tooltip"));
            toggleDirectConnections.SetValueWithoutNotify(!BasisSettingsDefaults.DisableDirectConnections.RawValue);

            // The in-app prompt turns this on for a player behind a Fake-IP proxy; it lives here so
            // it can be found (and turned back off) without waiting for a failed download to ask.
            PanelToggle toggleProxyBenchmarkRange = PanelToggle.CreateNewEntry(container);
            toggleProxyBenchmarkRange.AssignBinding(BasisSettingsDefaults.AllowProxyBenchmarkRange);
            toggleProxyBenchmarkRange.Descriptor.SetTitle(BasisLocalization.Get("settings.general.networking.proxyBenchmarkRange"));
            toggleProxyBenchmarkRange.Descriptor.SetTooltip(BasisLocalization.Get("settings.general.networking.proxyBenchmarkRange.tooltip"));

            PanelSlider sliderP2PRate = PanelSlider.CreateEntryAndBind(
                container,
                new PanelSlider.SliderSettings(
                    BasisLocalization.Get("settings.general.networking.p2pAvatarRate"),
                    BasisLocalization.Get("settings.general.networking.p2pAvatarRate.description"),
                    BasisP2PManager.MinAvatarSyncHz, BasisP2PManager.MaxAvatarSyncHz, true, 0, ValueDisplayMode.Hz),
                BasisSettingsDefaults.P2PAvatarSyncRate);
            sliderP2PRate.Descriptor.SetTooltip(BasisLocalization.Get("settings.general.networking.p2pAvatarRate.tooltip"));

            PanelToggle toggleP2PVoiceBitrateOverride = PanelToggle.CreateNewEntry(container);
            toggleP2PVoiceBitrateOverride.AssignBinding(BasisSettingsDefaults.P2PVoiceBitrateOverride);
            toggleP2PVoiceBitrateOverride.Descriptor.SetTitle(BasisLocalization.Get("settings.general.networking.p2pVoiceBitrateOverride"));
            toggleP2PVoiceBitrateOverride.Descriptor.SetTooltip(BasisLocalization.Get("settings.general.networking.p2pVoiceBitrateOverride.tooltip"));

            PanelSlider sliderP2PVoiceBitrate = PanelSlider.CreateEntryAndBind(
                container,
                new PanelSlider.SliderSettings(
                    BasisLocalization.Get("settings.general.networking.p2pVoiceBitrate"),
                    BasisLocalization.Get("settings.general.networking.p2pVoiceBitrate.description"),
                    6000f, 128000f, true, 0, ValueDisplayMode.Compact),
                BasisSettingsDefaults.P2PVoiceBitrate);
            sliderP2PVoiceBitrate.Descriptor.SetTooltip(BasisLocalization.Get("settings.general.networking.p2pVoiceBitrate.tooltip"));
            sliderP2PVoiceBitrate.OnValueChanged += _ => LocalOpusSettings.ReevaluateEffectiveBitrate();

            // Live encryption/avatar-rate status shown as its own row (the bar header
            // carries no description, so the status moves to a dedicated element).
            PanelElementDescriptor statusField = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, container);

            _avatarRateSlider = sliderP2PRate;
            _networkingGroup = statusField;
            _networkingTint = BasisPanelTint.Capture(statusField);
            _avatarRateTint = BasisPanelTint.Capture(sliderP2PRate.Descriptor);
            _avatarRateLastFps = -1;
            _avatarRateLastRate = -1;
            _avatarRateWarnShown = false;
            if (!_avatarRateTickSubscribed)
            {
                BasisFrameClock.OnTick += UpdateAvatarRateWarning;
                BasisFrameClock.AddRequest();
                _avatarRateTickSubscribed = true;
            }

            void RefreshDirectConnectionVisibility(bool directOn)
            {
                sliderP2PRate.Descriptor.SetActive(directOn);
                toggleP2PVoiceBitrateOverride.Descriptor.SetActive(directOn);
                sliderP2PVoiceBitrate.Descriptor.SetActive(directOn && toggleP2PVoiceBitrateOverride.Value);
                if (!directOn)
                {
                    _avatarRateWarnShown = false;
                }
                RefreshNetworkingStatus(_networkingStatusPainted);
                _networkingStatusPainted = true;
                statusField.ForceRebuild();
            }
            _networkingStatusPainted = false;
            RefreshDirectConnectionVisibility(toggleDirectConnections.Value);
            toggleDirectConnections.OnValueChanged += (directOn) =>
            {
                BasisSettingsDefaults.DisableDirectConnections.SetValue(!directOn);
                RefreshDirectConnectionVisibility(directOn);
            };
            // Inverted from the binding it writes (this toggle is "on" when DisableDirectConnections
            // is "off"), so it's wired manually rather than through AssignBinding — needs an explicit
            // reset default rather than getting one for free from a bound settings binding.
            toggleDirectConnections.SetResetDefault(!BasisSettingsDefaults.DisableDirectConnections.DefaultValue.GetDefault());
            toggleP2PVoiceBitrateOverride.OnValueChanged += _ =>
            {
                LocalOpusSettings.ReevaluateEffectiveBitrate();
                RefreshDirectConnectionVisibility(toggleDirectConnections.Value);
            };

            PanelSectionToggleHelpers.FinalizeFlatSectionFromIndex(networkingToggle, container, networkingStart, directConnected, visible =>
            {
                if (visible)
                {
                    RefreshDirectConnectionVisibility(toggleDirectConnections.Value);
                }
                tabDescriptor?.ForceRebuild();
            });
        }

        private static string BuildEncryptionStatusText()
        {
            string serverStatus = BasisLocalization.Get("settings.general.networking.encryption.serverOff");
            string p2pStatus = BasisSettingsDefaults.DisableDirectConnections.RawValue
                ? BasisLocalization.Get("settings.general.networking.encryption.p2pDisabled")
                : BasisLocalization.Get("settings.general.networking.encryption.p2pOn");

            return BasisLocalization.Get("settings.general.networking.encryption.serverLabel") + ": " + serverStatus + "\n"
                 + BasisLocalization.Get("settings.general.networking.encryption.p2pLabel") + ": " + p2pStatus;
        }

        private static void ResetGeneralDefaults()
        {
            BasisSettingsDefaults.AvatarPreview.ResetToDefault();
            BasisSettingsDefaults.AvatarPreviewMirror.ResetToDefault();
            BasisSettingsDefaults.DisableSeats.ResetToDefault();
            BasisSettingsDefaults.DisablePropPickup.ResetToDefault();
            BasisSettingsDefaults.DisableVRAutoHold.ResetToDefault();
            BasisSettingsDefaults.JiggleGrabInteractions.ResetToDefault();
            BasisSettingsDefaults.UIHaptics.ResetToDefault();
            BasisSettingsDefaults.HideRemoteCameraPucks.ResetToDefault();
            BasisSettingsDefaults.DesktopReticle.ResetToDefault();
            BasisSettingsDefaults.EnablePassthrough.ResetToDefault();
            BasisSettingsDefaults.EnableThirdPersonCamera.ResetToDefault();
            BasisSettingsDefaults.AudioListenerFollowsHead.ResetToDefault();
            BasisSettingsDefaults.DisableDirectConnections.ResetToDefault();
            BasisSettingsDefaults.P2PVoiceBitrateOverride.ResetToDefault();
            BasisSettingsDefaults.P2PVoiceBitrate.ResetToDefault();
            BasisSettingsDefaults.RememberMenuState.ResetToDefault();
            BasisSettingsDefaults.ShowDeveloperTab.ResetToDefault();
            BasisSettingsDefaults.UsePresenceSensor.ResetToDefault();
        }

        private static PanelSlider _avatarRateSlider;
        private static PanelElementDescriptor _networkingGroup;
        private static BasisPanelTint.Handle _networkingTint;
        private static BasisPanelTint.Handle _avatarRateTint;
        private static bool _avatarRateTickSubscribed;
        private static bool _networkingStatusPainted;
        private static int _avatarRateLastFps = -1;
        private static int _avatarRateLastRate = -1;
        private static bool _avatarRateWarnShown;
        private const int AvatarRateWarningPollInterval = 15;

        private static void RefreshNetworkingStatus(bool animateTint = true)
        {
            PanelElementDescriptor group = _networkingGroup;
            if (group == null)
            {
                return;
            }

            string status = BuildEncryptionStatusText();
            if (_avatarRateWarnShown)
            {
                status += "\n<color=#FFC747>"
                    + BasisLocalization.Get("settings.general.networking.p2pAvatarRate.fpsWarning", _avatarRateLastFps, _avatarRateLastRate)
                    + "</color>";
                BasisPanelTint.Apply(_networkingTint, BasisPanelTint.Caution, animateTint);
                BasisPanelTint.Apply(_avatarRateTint, BasisPanelTint.Caution, animateTint);
            }
            else
            {
                BasisPanelTint.Clear(_networkingTint, animateTint);
                BasisPanelTint.Clear(_avatarRateTint, animateTint);
            }
            group.SetRichDescription(status);
        }

        private static void UpdateAvatarRateWarning()
        {
            PanelElementDescriptor group = _networkingGroup;
            if (group == null)
            {
                BasisFrameClock.OnTick -= UpdateAvatarRateWarning;
                BasisFrameClock.RemoveRequest();
                _avatarRateTickSubscribed = false;
                return;
            }

            if (_avatarRateSlider == null || !_avatarRateSlider.gameObject.activeInHierarchy)
            {
                return;
            }

            if (Time.frameCount % AvatarRateWarningPollInterval != 0)
            {
                return;
            }

            int fps = Mathf.RoundToInt(BasisFrameClock.SmoothedFramesPerSecond);
            int rate = Mathf.RoundToInt(BasisSettingsDefaults.P2PAvatarSyncRate.RawValue);
            bool insufficient = fps > 0 && rate > Mathf.RoundToInt(fps * 1.02f);

            if (!insufficient)
            {
                if (_avatarRateWarnShown)
                {
                    _avatarRateWarnShown = false;
                    RefreshNetworkingStatus();
                    group.ForceRebuild();
                }
                return;
            }

            if (_avatarRateWarnShown && fps == _avatarRateLastFps && rate == _avatarRateLastRate)
            {
                return;
            }
            _avatarRateWarnShown = true;
            _avatarRateLastFps = fps;
            _avatarRateLastRate = rate;
            RefreshNetworkingStatus();
            group.ForceRebuild();
        }

        // ------------------
        // AUDIO TAB (ONE RESET BUTTON)
        // ------------------
        public static PanelTabPage AudioTab(PanelTabGroup tabGroup)
        {
            PanelTabPage tab = PanelTabPage.CreateVertical(tabGroup.Descriptor.ContentParent);
            PanelElementDescriptor descriptor = tab.Descriptor;

            descriptor.SetTitle(BasisLocalization.Get("settings.audio.title"));
            RectTransform container = descriptor.ContentParent;

            // MIXER GROUP
            PanelSectionToggle mixerToggle = PanelSectionToggle.CreateNewEntry(container);
            PanelElementDescriptor mixerGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                mixerToggle,
                container,
                BasisLocalization.Get("settings.audio.mixer.title"),
                showGroupTitle: false);

            PanelSlider sliderMainVolume = PanelSlider.CreateEntryAndBind(
                mixerGroup,
                PanelSlider.SliderSettings.Percentage(BasisLocalization.Get("settings.audio.mainVolume")),
                BasisSettingsDefaults.MainVolume);
            sliderMainVolume.Descriptor.SetTitle(BasisLocalization.Get("settings.audio.masterVolume"));
            sliderMainVolume.Descriptor.SetTooltip(BasisLocalization.Get("settings.audio.masterVolume.tooltip"));
            sliderMainVolume.SliderComponent.onValueChanged.AddListener(SMModuleAudio.ApplyMainVolume);

            PanelSlider sliderMenuVolume = PanelSlider.CreateEntryAndBind(
                mixerGroup,
                PanelSlider.SliderSettings.Percentage(BasisLocalization.Get("settings.audio.menuVolume")),
                BasisSettingsDefaults.MenuVolume);
            sliderMenuVolume.Descriptor.SetTooltip(BasisLocalization.Get("settings.audio.menuVolume.tooltip"));
            sliderMenuVolume.SliderComponent.onValueChanged.AddListener(SMModuleAudio.ApplyMenuVolume);

            PanelSlider sliderWorldVolume = PanelSlider.CreateEntryAndBind(
                mixerGroup,
                PanelSlider.SliderSettings.Percentage(BasisLocalization.Get("settings.audio.worldVolume")),
                BasisSettingsDefaults.WorldVolume);
            sliderWorldVolume.Descriptor.SetTooltip(BasisLocalization.Get("settings.audio.worldVolume.tooltip"));
            sliderWorldVolume.SliderComponent.onValueChanged.AddListener(SMModuleAudio.ApplyWorldVolume);

            PanelSlider sliderVoiceVolume = PanelSlider.CreateEntryAndBind(
                mixerGroup,
                PanelSlider.SliderSettings.Percentage(BasisLocalization.Get("settings.audio.voiceVolume")),
                BasisSettingsDefaults.VoiceVolume);
            sliderVoiceVolume.Descriptor.SetTooltip(BasisLocalization.Get("settings.audio.voiceVolume.tooltip"));
            sliderVoiceVolume.SliderComponent.onValueChanged.AddListener(SMModuleAudio.ApplyVoiceVolume);

            PanelSlider sliderAvatarVolume = PanelSlider.CreateEntryAndBind(
                mixerGroup,
                PanelSlider.SliderSettings.Percentage(BasisLocalization.Get("settings.audio.avatarVolume")),
                BasisSettingsDefaults.AvatarVolume);
            sliderAvatarVolume.Descriptor.SetTooltip(BasisLocalization.Get("settings.audio.avatarVolume.tooltip"));
            sliderAvatarVolume.SliderComponent.onValueChanged.AddListener(SMModuleAudio.ApplyAvatarVolume);

            PanelSlider sliderPropVolume = PanelSlider.CreateEntryAndBind(
                mixerGroup,
                PanelSlider.SliderSettings.Percentage(BasisLocalization.Get("settings.audio.propVolume")),
                BasisSettingsDefaults.PropVolume);
            sliderPropVolume.Descriptor.SetTooltip(BasisLocalization.Get("settings.audio.propVolume.tooltip"));
            sliderPropVolume.SliderComponent.onValueChanged.AddListener(SMModuleAudio.ApplyPropVolume);

            PanelSectionToggleHelpers.FinalizeCollapsibleGroup(mixerToggle, mixerGroup, true,
                _ => descriptor.ForceRebuild());

            AudioTabExtraBuilder?.Invoke(container);

            // OUTPUT DEVICE
            if (BasisAudioOutputDevices.IsSupported)
            {
                PanelSectionToggle outputToggle = PanelSectionToggle.CreateNewEntry(container);
                PanelElementDescriptor outputGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                    outputToggle,
                    container,
                    BasisLocalization.Get("settings.audio.output.title"),
                    showGroupTitle: false);

                PanelDropdown dropdownOutputDevice = PanelDropdown.CreateNewEntry(outputGroup);
                dropdownOutputDevice.Descriptor.SetTitle(BasisLocalization.Get("settings.audio.outputDevice"));
                dropdownOutputDevice.Descriptor.SetTooltip(BasisLocalization.Get("settings.audio.outputDevice.tooltip"));

                List<BasisAudioOutputDevices.OutputDevice> outputDevices = BasisAudioOutputDevices.GetDevices();
                List<string> outputIds = new List<string>(outputDevices.Count + 1) { string.Empty };
                List<string> outputNames = new List<string>(outputDevices.Count + 1) { BasisLocalization.Get("settings.audio.outputDevice.systemDefault") };
                for (int i = 0; i < outputDevices.Count; i++)
                {
                    outputIds.Add(outputDevices[i].Id);
                    outputNames.Add(outputDevices[i].Name);
                }
                dropdownOutputDevice.AssignEntries(outputIds, outputNames);
                dropdownOutputDevice.SetValueWithoutNotify(BasisAudioOutputDevices.GetRoutedDeviceId());

                void OutputDeviceChanged(string deviceId)
                {
                    if (!BasisAudioOutputDevices.SetRoutedDevice(deviceId))
                        BasisDebug.LogWarning("Failed to route audio to the selected output device.");
                }
                dropdownOutputDevice.OnValueChanged += OutputDeviceChanged;
                // Callback-driven (routes through BasisAudioOutputDevices, not a settings binding);
                // empty string is the "System Default" entry seeded at index 0 above.
                dropdownOutputDevice.SetResetDefault(string.Empty);

                PanelSectionToggleHelpers.FinalizeCollapsibleGroup(outputToggle, outputGroup, true,
                    _ => descriptor.ForceRebuild());
            }

            // Remote Players (Spatial Audio) — also hosts Hearing Range and the
            // Audio Source cap, since both are "how do I hear other players" controls.
            SettingsProviderRemoteAudio.BuildRemoteAudioUI(container);

            // One reset button for this whole page
            RegisterPageReset("settings.tab.audio", ResetAudioDefaults);
            descriptor.ForceRebuild();
            return tab;
        }

        private static void ResetAudioDefaults()
        {
            BasisSettingsDefaults.MainVolume.ResetToDefault();
            BasisSettingsDefaults.MenuVolume.ResetToDefault();
            BasisSettingsDefaults.WorldVolume.ResetToDefault();
            BasisSettingsDefaults.VoiceVolume.ResetToDefault();
            BasisSettingsDefaults.AvatarVolume.ResetToDefault();
            BasisSettingsDefaults.PropVolume.ResetToDefault();
            BasisSettingsDefaults.SoundHover.ResetToDefault();
            BasisSettingsDefaults.SoundPress.ResetToDefault();
            BasisSettingsDefaults.SoundGrab.ResetToDefault();
            BasisSettingsDefaults.SoundChat.ResetToDefault();
            BasisSettingsDefaults.SoundMicrophone.ResetToDefault();
            BasisSettingsDefaults.SoundMicrophonePushToTalk.ResetToDefault();
            BasisSettingsDefaults.SoundCamera.ResetToDefault();
            BasisSettingsDefaults.UseOpenLipSyncLimit.ResetToDefault();
            BasisSettingsDefaults.OpenLipSyncMaxSlots.ResetToDefault();
            BasisSettingsDefaults.HearingRange.ResetToDefault();
            BasisSettingsDefaults.UseMaxAudioSources.ResetToDefault();
            BasisSettingsDefaults.MaxAudioSources.ResetToDefault();
            SettingsProviderRemoteAudio.ResetRemoteAudioToDefaults();
        }

        // ------------------
        // MICROPHONE TAB
        // ------------------
        public static PanelTabPage MicrophoneTab(PanelTabGroup tabGroup)
        {
#if !BASIS_DISABLE_MICROPHONE
            SMDMicrophone.LoadInMicrophoneData(BasisDeviceManagement.StaticCurrentMode);
#endif

            PanelTabPage tab = PanelTabPage.CreateVertical(tabGroup.Descriptor.ContentParent);
            PanelElementDescriptor descriptor = tab.Descriptor;

            descriptor.SetTitle(BasisLocalization.Get("settings.microphone.title"));
            RectTransform container = descriptor.ContentParent;

            void RebuildFrom(RectTransform changed) =>
                PanelElementDescriptor.RebuildLayoutChain(changed, container);

#if !BASIS_DISABLE_MICROPHONE
            // Snapshot
            SMDMicrophone.MicSettings snap = SMDMicrophone.Current;

            // MICROPHONE GROUP
            PanelSectionToggle microphoneToggle = PanelSectionToggle.CreateNewEntry(container);
            PanelElementDescriptor microphoneGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                microphoneToggle,
                container,
                BasisLocalization.Get("settings.microphone.group.title"),
                showGroupTitle: false);

            // Microphone Volume (0..1)
            sliderMicrophoneVolume = PanelSlider.CreateEntryAndBind(
               microphoneGroup,
               PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.microphone.volume"), 0, 1, false, 4, ValueDisplayMode.Percentage),
               BasisSettingsDefaults.MicrophoneVolume);
            sliderMicrophoneVolume.Descriptor.SetTooltip(BasisLocalization.Get("settings.microphone.volume.tooltip"));
            sliderMicrophoneVolume.SetValueWithoutNotify(snap.Volume01);

            void MicrophoneVolumeChanged(float value)
            {
                if (SMDMicrophone.CurrentMode != BasisDeviceManagement.StaticCurrentMode)
                    SMDMicrophone.LoadInMicrophoneData(BasisDeviceManagement.StaticCurrentMode);

                SMDMicrophone.SetVolume(value);
            }
            sliderMicrophoneVolume.SliderComponent.onValueChanged.AddListener(MicrophoneVolumeChanged);

            SettingsProviderMicrophoneTest.BuildMicrophoneTestSection(
                microphoneGroup.ContentParent,
                _ => RebuildFrom(microphoneGroup.ContentParent));

            // Microphone Selection (device list)
            dropdownMicrophoneSelection = PanelDropdown.CreateNewEntry(microphoneGroup);
            dropdownMicrophoneSelection.Descriptor.SetTitle(BasisLocalization.Get("settings.microphone.selection"));
            dropdownMicrophoneSelection.Descriptor.SetTooltip(BasisLocalization.Get("settings.microphone.selection.tooltip"));
            dropdownMicrophoneSelection.AssignEntries(SMDMicrophone.MicrophoneDevices?.ToList() ?? new List<string>());
            dropdownMicrophoneSelection.SetValueWithoutNotify(snap.Microphone);

            void MicrophoneSelectionChanged(string name)
            {
                if (SMDMicrophone.CurrentMode != BasisDeviceManagement.StaticCurrentMode)
                    SMDMicrophone.LoadInMicrophoneData(BasisDeviceManagement.StaticCurrentMode);

                SMDMicrophone.SetMicrophone(name);
            }
            dropdownMicrophoneSelection.OnValueChanged += MicrophoneSelectionChanged;

            // Microphone broadcast range (relocated from General).
            PanelSlider sliderMicrophoneRange = PanelSlider.CreateEntryAndBind(
                microphoneGroup,
                PanelSlider.SliderSettings.Distance(BasisLocalization.Get("settings.general.microphoneRange"), BasisNetworkModeration.ServerMaxMicrophoneRangeMeters),
                BasisSettingsDefaults.MicrophoneRange);
            sliderMicrophoneRange.Descriptor.SetTooltip(BasisLocalization.Get("settings.general.microphoneRange.tooltip"));
            BasisAudioRangeSliderLimit.Attach(sliderMicrophoneRange, BasisAudioRangeSliderLimit.RangeKind.Microphone);

            PanelToggle toggleMicrophoneRangeIndicator = PanelToggle.CreateNewEntry(microphoneGroup);
            toggleMicrophoneRangeIndicator.AssignBinding(BasisSettingsDefaults.MicrophoneRangeIndicator);
            toggleMicrophoneRangeIndicator.Descriptor.SetTitle(BasisLocalization.Get("settings.microphone.rangeIndicator"));
            toggleMicrophoneRangeIndicator.Descriptor.SetTooltip(BasisLocalization.Get("settings.microphone.rangeIndicator.tooltip"));

            PanelToggle toggleMicrophoneDenoiser = PanelToggle.CreateNewEntry(microphoneGroup);
            toggleMicrophoneDenoiser.Descriptor.SetTitle(BasisLocalization.Get("settings.microphone.denoiser"));
            toggleMicrophoneDenoiser.Descriptor.SetTooltip(BasisLocalization.Get("settings.microphone.denoiser.tooltip"));
            toggleMicrophoneDenoiser.AssignBinding(BasisSettingsDefaults.MicrophoneDenoiser);

            PanelDropdown dropdownMicrophoneMode = PanelDropdown.CreateNewEntry(microphoneGroup);
            dropdownMicrophoneMode.Descriptor.SetTitle(BasisLocalization.Get("settings.microphone.mode"));
            dropdownMicrophoneMode.Descriptor.SetTooltip(BasisLocalization.Get("settings.microphone.mode.tooltip"));
            dropdownMicrophoneMode.AssignLocalizedEntries(
                new List<string> { "On Activation", "Push To Talk" },
                new List<string> { "settings.microphone.mode.onActivation", "settings.microphone.mode.pushToTalk" });
            dropdownMicrophoneMode.AssignBinding(BasisSettingsDefaults.MicrophoneMode);

            PanelDropdown dropdownMicrophoneIcon = PanelDropdown.CreateNewEntry(microphoneGroup);
            dropdownMicrophoneIcon.Descriptor.SetTitle(BasisLocalization.Get("settings.microphone.icon"));
            dropdownMicrophoneIcon.Descriptor.SetTooltip(BasisLocalization.Get("settings.microphone.icon.tooltip"));
            dropdownMicrophoneIcon.AssignLocalizedEntries(
                new List<string> { "AlwaysVisible", "ActivityDetection", "Hidden" },
                new List<string> { "settings.microphone.icon.alwaysVisible", "settings.microphone.icon.activityDetection", "settings.microphone.icon.hidden" });
            dropdownMicrophoneIcon.AssignBinding(BasisSettingsDefaults.MicrophoneIcon);

            PanelToggle toggleMicrophoneIconLevelRing = PanelToggle.CreateNewEntry(microphoneGroup);
            toggleMicrophoneIconLevelRing.Descriptor.SetTitle(BasisLocalization.Get("settings.microphone.icon.levelRing"));
            toggleMicrophoneIconLevelRing.Descriptor.SetTooltip(BasisLocalization.Get("settings.microphone.icon.levelRing.tooltip"));
            toggleMicrophoneIconLevelRing.AssignBinding(BasisSettingsDefaults.MicrophoneIconLevelRing);

            PanelSectionToggleHelpers.FinalizeCollapsibleGroup(microphoneToggle, microphoneGroup, true,
                _ => RebuildFrom(microphoneGroup.rectTransform));

            // -------------------- DSP SETTINGS (advanced) --------------------

            PanelSectionToggle toggleAdvanced = PanelSectionToggle.CreateNewEntry(container);
            PanelElementDescriptor advancedGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                toggleAdvanced, container, BasisLocalization.Get("ui.advanced"), showGroupTitle: false);
            RectTransform advancedContent = advancedGroup.ContentParent;

            // Mute & Start Behaviour (advanced)
            PanelSectionToggleHelpers.CreateCollapsibleBoxedSection(advancedContent,
                BasisLocalization.Get("settings.microphone.muteBehavior.title"), () =>
            {
                PanelDropdown dropdownMicStartBehavior = PanelDropdown.CreateNewEntry(advancedContent);
                dropdownMicStartBehavior.Descriptor.SetTitle(BasisLocalization.Get("settings.microphone.startBehavior"));
                dropdownMicStartBehavior.Descriptor.SetTooltip(BasisLocalization.Get("settings.microphone.startBehavior.tooltip"));
                dropdownMicStartBehavior.AssignLocalizedEntries(
                    new List<string>
                    {
                        BasisLocalMicrophoneDriver.SettingStartOff,
                        BasisLocalMicrophoneDriver.SettingStartOn,
                        BasisLocalMicrophoneDriver.SettingStartRememberLast,
                    },
                    new List<string> { "settings.microphone.start.muted", "settings.microphone.start.unmuted", "settings.microphone.start.rememberLast" });
                dropdownMicStartBehavior.AssignBinding(BasisSettingsDefaults.MicStartBehavior);

                PanelDropdown dropdownMicMuteBehavior = PanelDropdown.CreateNewEntry(advancedContent);
                dropdownMicMuteBehavior.Descriptor.SetTitle(BasisLocalization.Get("settings.microphone.muteBehavior"));
                dropdownMicMuteBehavior.Descriptor.SetTooltip(BasisLocalization.Get("settings.microphone.muteBehavior.tooltip"));
                dropdownMicMuteBehavior.AssignLocalizedEntries(
                    new List<string>
                    {
                        BasisLocalMicrophoneDriver.SettingMuteShutdown,
                        BasisLocalMicrophoneDriver.SettingMuteSuppress,
                    },
                    new List<string> { "settings.microphone.mute.shutdown", "settings.microphone.mute.keepOpen" });
                dropdownMicMuteBehavior.AssignBinding(BasisSettingsDefaults.MicMuteBehavior);

                PanelToggle toggleTalkToNoOne = PanelToggle.CreateNewEntry(advancedContent);
                toggleTalkToNoOne.Descriptor.SetTitle(BasisLocalization.Get("settings.microphone.talkToNoOne"));
                toggleTalkToNoOne.Descriptor.SetTooltip(BasisLocalization.Get("settings.microphone.talkToNoOne.tooltip"));
                toggleTalkToNoOne.AssignBinding(BasisSettingsDefaults.TalkToNoOne);
            }, false, _ => RebuildFrom(advancedContent));

            void LimitThresholdChanged(float v)
            {
                if (SMDMicrophone.CurrentMode != BasisDeviceManagement.StaticCurrentMode)
                    SMDMicrophone.LoadInMicrophoneData(BasisDeviceManagement.StaticCurrentMode);

                var s = SMDMicrophone.Current;
                SMDMicrophone.SetLimiter(v, s.LimitKnee);
            }
            void LimitKneeChanged(float v)
            {
                if (SMDMicrophone.CurrentMode != BasisDeviceManagement.StaticCurrentMode)
                    SMDMicrophone.LoadInMicrophoneData(BasisDeviceManagement.StaticCurrentMode);

                var s = SMDMicrophone.Current;
                SMDMicrophone.SetLimiter(s.LimitThreshold, v);
            }

            // Limiter
            PanelSectionToggleHelpers.CreateCollapsibleBoxedSection(advancedContent,
                BasisLocalization.Get("settings.microphone.limiter.title"), () =>
            {
                sliderLimitThreshold = PanelSlider.CreateEntryAndBind(
                   advancedContent,
                   PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.microphone.limiter.threshold"), 0f, 1f, false, 3, ValueDisplayMode.Percentage),
                   BasisSettingsDefaults.LimitThreshold);
                sliderLimitThreshold.Descriptor.SetTooltip(BasisLocalization.Get("settings.microphone.limiter.threshold.tooltip"));
                sliderLimitThreshold.SetValueWithoutNotify(snap.LimitThreshold);

                sliderLimitKnee = PanelSlider.CreateEntryAndBind(
                   advancedContent,
                   PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.microphone.limiter.knee"), 0f, 1f, false, 3, ValueDisplayMode.Percentage),
                   BasisSettingsDefaults.LimitKnee);
                sliderLimitKnee.Descriptor.SetTooltip(BasisLocalization.Get("settings.microphone.limiter.knee.tooltip"));
                sliderLimitKnee.SetValueWithoutNotify(snap.LimitKnee);

                sliderLimitThreshold.SliderComponent.onValueChanged.AddListener(LimitThresholdChanged);
                sliderLimitKnee.SliderComponent.onValueChanged.AddListener(LimitKneeChanged);
            }, false, _ => RebuildFrom(advancedContent));

            void DenoiseWetChanged(float v)
            {
                if (SMDMicrophone.CurrentMode != BasisDeviceManagement.StaticCurrentMode)
                    SMDMicrophone.LoadInMicrophoneData(BasisDeviceManagement.StaticCurrentMode);

                var s = SMDMicrophone.Current;
                SMDMicrophone.SetDenoiseParams(s.DenoiseMakeupDb, v);
            }
            void DenoiseMakeupChanged(float v)
            {
                if (SMDMicrophone.CurrentMode != BasisDeviceManagement.StaticCurrentMode)
                    SMDMicrophone.LoadInMicrophoneData(BasisDeviceManagement.StaticCurrentMode);

                var s = SMDMicrophone.Current;
                SMDMicrophone.SetDenoiseParams(v, s.DenoiseWet);
            }

            // Denoiser tuning
            PanelSectionToggleHelpers.CreateCollapsibleBoxedSection(advancedContent,
                BasisLocalization.Get("settings.microphone.denoiser.title"), () =>
            {
                sliderDenoiseWet = PanelSlider.CreateEntryAndBind(
                   advancedContent,
                   PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.microphone.denoiser.wet"), 0f, 1f, false, 3, ValueDisplayMode.Percentage),
                   BasisSettingsDefaults.DenoiseWet);
                sliderDenoiseWet.Descriptor.SetTooltip(BasisLocalization.Get("settings.microphone.denoiser.wet.tooltip"));
                sliderDenoiseWet.SetValueWithoutNotify(snap.DenoiseWet);

                sliderDenoiseMakeup = PanelSlider.CreateEntryAndBind(
                   advancedContent,
                   PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.microphone.denoiser.makeup"), -12f, 24f, false, 2, ValueDisplayMode.Raw),
                   BasisSettingsDefaults.DenoiseMakeupDb);
                sliderDenoiseMakeup.Descriptor.SetTooltip(BasisLocalization.Get("settings.microphone.denoiser.makeup.tooltip"));
                sliderDenoiseMakeup.SetValueWithoutNotify(snap.DenoiseMakeupDb);

                sliderDenoiseWet.SliderComponent.onValueChanged.AddListener(DenoiseWetChanged);
                sliderDenoiseMakeup.SliderComponent.onValueChanged.AddListener(DenoiseMakeupChanged);
            }, false, _ => RebuildFrom(advancedContent));

            // void AgcTargetChanged(float v)
            // {
            //     if (SMDMicrophone.CurrentMode != BasisDeviceManagement.StaticCurrentMode)
            //         SMDMicrophone.LoadInMicrophoneData(BasisDeviceManagement.StaticCurrentMode);
            //
            //     var s = SMDMicrophone.Current;
            //     SMDMicrophone.SetAgcParams(v, s.AgcMaxGainDb, s.AgcAttack, s.AgcRelease);
            // }
            void AgcMaxGainChanged(float v)
            {
                if (SMDMicrophone.CurrentMode != BasisDeviceManagement.StaticCurrentMode)
                    SMDMicrophone.LoadInMicrophoneData(BasisDeviceManagement.StaticCurrentMode);

                var s = SMDMicrophone.Current;
                SMDMicrophone.SetAgcParams(s.AgcTargetRms, v, s.AgcAttack, s.AgcRelease);
            }
            void AgcAttackChanged(float v)
            {
                if (SMDMicrophone.CurrentMode != BasisDeviceManagement.StaticCurrentMode)
                    SMDMicrophone.LoadInMicrophoneData(BasisDeviceManagement.StaticCurrentMode);

                var s = SMDMicrophone.Current;
                SMDMicrophone.SetAgcParams(s.AgcTargetRms, s.AgcMaxGainDb, v, s.AgcRelease);
            }
            void AgcReleaseChanged(float v)
            {
                if (SMDMicrophone.CurrentMode != BasisDeviceManagement.StaticCurrentMode)
                    SMDMicrophone.LoadInMicrophoneData(BasisDeviceManagement.StaticCurrentMode);

                var s = SMDMicrophone.Current;
                SMDMicrophone.SetAgcParams(s.AgcTargetRms, s.AgcMaxGainDb, s.AgcAttack, v);
            }

            // AGC tuning
            PanelSectionToggleHelpers.CreateCollapsibleBoxedSection(advancedContent,
                BasisLocalization.Get("settings.microphone.agc.title"), () =>
            {
                PanelToggle toggleAGC = PanelToggle.CreateNewEntry(advancedContent);
                toggleAGC.Descriptor.SetTitle(BasisLocalization.Get("settings.microphone.agc"));
                toggleAGC.Descriptor.SetTooltip(BasisLocalization.Get("settings.microphone.agc.tooltip"));
                toggleAGC.AssignBinding(BasisSettingsDefaults.UseAutomaticGain);

                // Target loudness is fixed in BasisMicrophoneAgc.DefaultTargetRms — see the binding.
                // sliderAgcTarget = PanelSlider.CreateEntryAndBind(
                //    advancedContent,
                //    PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.microphone.agc.targetRms"), 0.001f, 0.25f, false, 4, ValueDisplayMode.Raw),
                //    BasisSettingsDefaults.AgcTargetRms);
                // sliderAgcTarget.Descriptor.SetTooltip(BasisLocalization.Get("settings.microphone.agc.targetRms.tooltip"));
                // sliderAgcTarget.SetValueWithoutNotify(snap.AgcTargetRms);

                sliderAgcMaxGain = PanelSlider.CreateEntryAndBind(
                   advancedContent,
                   PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.microphone.agc.maxGain"), 0f, 36f, false, 1, ValueDisplayMode.Raw),
                   BasisSettingsDefaults.AgcMaxGainDb);
                sliderAgcMaxGain.Descriptor.SetTooltip(BasisLocalization.Get("settings.microphone.agc.maxGain.tooltip"));
                sliderAgcMaxGain.SetValueWithoutNotify(snap.AgcMaxGainDb);

                sliderAgcAttack = PanelSlider.CreateEntryAndBind(
                   advancedContent,
                   PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.microphone.agc.attack"), 0f, 1f, false, 3, ValueDisplayMode.Percentage),
                   BasisSettingsDefaults.AgcAttack);
                sliderAgcAttack.Descriptor.SetTooltip(BasisLocalization.Get("settings.microphone.agc.attack.tooltip"));
                sliderAgcAttack.SetValueWithoutNotify(snap.AgcAttack);

                sliderAgcRelease = PanelSlider.CreateEntryAndBind(
                   advancedContent,
                   PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.microphone.agc.release"), 0f, 1f, false, 3, ValueDisplayMode.Percentage),
                   BasisSettingsDefaults.AgcRelease);
                sliderAgcRelease.Descriptor.SetTooltip(BasisLocalization.Get("settings.microphone.agc.release.tooltip"));
                sliderAgcRelease.SetValueWithoutNotify(snap.AgcRelease);

                PanelElementDescriptor agcDebugField =
                    PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, advancedContent);
                agcDebugField.SetTitle(BasisLocalization.Get("settings.microphone.agc.debug"));
                agcDebugField.SetDescription(BasisLocalization.Get("settings.microphone.agc.debug.listening"));
                var agcDebugUpdater = agcDebugField.gameObject.AddComponent<Basis.Scripts.UI.UI_Panels.BasisMicAgcDebugUpdater>();
                agcDebugUpdater.Field = agcDebugField;

                // sliderAgcTarget.OnValueChanged += AgcTargetChanged;
                sliderAgcMaxGain.OnValueChanged += AgcMaxGainChanged;
                sliderAgcAttack.OnValueChanged += AgcAttackChanged;
                sliderAgcRelease.OnValueChanged += AgcReleaseChanged;
            }, false, _ => RebuildFrom(advancedContent));

            void NoiseGateThresholdChanged(float v)
            {
                if (SMDMicrophone.CurrentMode != BasisDeviceManagement.StaticCurrentMode)
                    SMDMicrophone.LoadInMicrophoneData(BasisDeviceManagement.StaticCurrentMode);

                var s = SMDMicrophone.Current;
                SMDMicrophone.SetNoiseGateParams(v, s.NoiseGateAttack, s.NoiseGateRelease);
            }
            void NoiseGateAttackChanged(float v)
            {
                if (SMDMicrophone.CurrentMode != BasisDeviceManagement.StaticCurrentMode)
                    SMDMicrophone.LoadInMicrophoneData(BasisDeviceManagement.StaticCurrentMode);

                var s = SMDMicrophone.Current;
                SMDMicrophone.SetNoiseGateParams(s.NoiseGateThreshold, v, s.NoiseGateRelease);
            }
            void NoiseGateReleaseChanged(float v)
            {
                if (SMDMicrophone.CurrentMode != BasisDeviceManagement.StaticCurrentMode)
                    SMDMicrophone.LoadInMicrophoneData(BasisDeviceManagement.StaticCurrentMode);

                var s = SMDMicrophone.Current;
                SMDMicrophone.SetNoiseGateParams(s.NoiseGateThreshold, s.NoiseGateAttack, v);
            }

            // Noise Gate
            PanelSectionToggleHelpers.CreateCollapsibleBoxedSection(advancedContent,
                BasisLocalization.Get("settings.microphone.noiseGate.title"), () =>
            {
                PanelToggle toggleNoiseGate = PanelToggle.CreateNewEntry(advancedContent);
                toggleNoiseGate.Descriptor.SetTitle(BasisLocalization.Get("settings.microphone.noiseGate.enable"));
                toggleNoiseGate.Descriptor.SetTooltip(BasisLocalization.Get("settings.microphone.noiseGate.enable.tooltip"));
                toggleNoiseGate.AssignBinding(BasisSettingsDefaults.UseNoiseGate);

                PanelToggle toggleAutoNoiseGate = PanelToggle.CreateNewEntry(advancedContent);
                toggleAutoNoiseGate.Descriptor.SetTitle(BasisLocalization.Get("settings.microphone.noiseGate.auto"));
                toggleAutoNoiseGate.Descriptor.SetTooltip(BasisLocalization.Get("settings.microphone.noiseGate.auto.tooltip"));
                toggleAutoNoiseGate.AssignBinding(BasisSettingsDefaults.AutoNoiseGate);

                sliderNoiseGateThreshold = PanelSlider.CreateEntryAndBind(
                   advancedContent,
                   PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.microphone.noiseGate.threshold"), 0f, 0.5f, false, 4, ValueDisplayMode.Raw),
                   BasisSettingsDefaults.NoiseGateThreshold);
                sliderNoiseGateThreshold.Descriptor.SetTooltip(BasisLocalization.Get("settings.microphone.noiseGate.threshold.tooltip"));
                sliderNoiseGateThreshold.SetValueWithoutNotify(snap.NoiseGateThreshold);

                sliderNoiseGateAttack = PanelSlider.CreateEntryAndBind(
                   advancedContent,
                   PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.microphone.noiseGate.attack"), 0f, 1f, false, 3, ValueDisplayMode.Percentage),
                   BasisSettingsDefaults.NoiseGateAttack);
                sliderNoiseGateAttack.Descriptor.SetTooltip(BasisLocalization.Get("settings.microphone.noiseGate.attack.tooltip"));
                sliderNoiseGateAttack.SetValueWithoutNotify(snap.NoiseGateAttack);

                sliderNoiseGateRelease = PanelSlider.CreateEntryAndBind(
                   advancedContent,
                   PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.microphone.noiseGate.release"), 0f, 1f, false, 3, ValueDisplayMode.Percentage),
                   BasisSettingsDefaults.NoiseGateRelease);
                sliderNoiseGateRelease.Descriptor.SetTooltip(BasisLocalization.Get("settings.microphone.noiseGate.release.tooltip"));
                sliderNoiseGateRelease.SetValueWithoutNotify(snap.NoiseGateRelease);

                sliderNoiseGateThreshold.OnValueChanged += NoiseGateThresholdChanged;
                sliderNoiseGateAttack.OnValueChanged += NoiseGateAttackChanged;
                sliderNoiseGateRelease.OnValueChanged += NoiseGateReleaseChanged;
            }, false, _ => RebuildFrom(advancedContent));

            // Mic Icon Position (advanced)
            PanelSectionToggleHelpers.CreateCollapsibleBoxedSection(advancedContent,
                BasisLocalization.Get("settings.microphone.iconPosition.title"), () =>
            {
                PanelSlider sliderMicIconOffsetX = PanelSlider.CreateEntryAndBind(
                    advancedContent,
                    PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.microphone.iconPosition.horizontal"), -0.5f, 0.5f, false, 2, ValueDisplayMode.Raw),
                    BasisSettingsDefaults.MicrophoneIconOffsetX);
                sliderMicIconOffsetX.Descriptor.SetTooltip(BasisLocalization.Get("settings.microphone.iconPosition.horizontal.tooltip"));

                PanelSlider sliderMicIconOffsetY = PanelSlider.CreateEntryAndBind(
                    advancedContent,
                    PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.microphone.iconPosition.vertical"), -0.5f, 0.5f, false, 2, ValueDisplayMode.Raw),
                    BasisSettingsDefaults.MicrophoneIconOffsetY);
                sliderMicIconOffsetY.Descriptor.SetTooltip(BasisLocalization.Get("settings.microphone.iconPosition.vertical.tooltip"));
            }, false, _ => RebuildFrom(advancedContent));

            PanelSectionToggleHelpers.FinalizeCollapsibleGroup(toggleAdvanced, advancedGroup, false,
                _ => RebuildFrom(advancedGroup.rectTransform));

            RegisterPageReset("settings.tab.microphone", ResetMicrophoneDefaults);
#endif
            RebuildFrom(container);
            return tab;
        }

        private static void ResetMicrophoneDefaults()
        {
#if !BASIS_DISABLE_MICROPHONE
            BasisSettingsDefaults.MicrophoneVolume.ResetToDefault();
            BasisSettingsDefaults.MicrophoneRange.ResetToDefault();
            BasisSettingsDefaults.MicrophoneDenoiser.ResetToDefault();
            BasisSettingsDefaults.UseAutomaticGain.ResetToDefault();
            BasisSettingsDefaults.MicrophoneMode.ResetToDefault();
            BasisSettingsDefaults.MicMuteBehavior.ResetToDefault();
            BasisSettingsDefaults.TalkToNoOne.ResetToDefault();
            BasisSettingsDefaults.ShoutMode.ResetToDefault();
            BasisSettingsDefaults.MicrophoneIcon.ResetToDefault();
            BasisSettingsDefaults.MicrophoneIconLevelRing.ResetToDefault();
            BasisSettingsDefaults.MicrophoneIconOffsetX.ResetToDefault();
            BasisSettingsDefaults.MicrophoneIconOffsetY.ResetToDefault();
            BasisSettingsDefaults.LimitThreshold.ResetToDefault();
            BasisSettingsDefaults.LimitKnee.ResetToDefault();
            BasisSettingsDefaults.DenoiseWet.ResetToDefault();
            BasisSettingsDefaults.DenoiseMakeupDb.ResetToDefault();
            // BasisSettingsDefaults.AgcTargetRms.ResetToDefault();
            BasisSettingsDefaults.AgcMaxGainDb.ResetToDefault();
            BasisSettingsDefaults.AgcAttack.ResetToDefault();
            BasisSettingsDefaults.AgcRelease.ResetToDefault();
            BasisSettingsDefaults.UseNoiseGate.ResetToDefault();
            BasisSettingsDefaults.AutoNoiseGate.ResetToDefault();
            BasisSettingsDefaults.NoiseGateThreshold.ResetToDefault();
            BasisSettingsDefaults.NoiseGateAttack.ResetToDefault();
            BasisSettingsDefaults.NoiseGateRelease.ResetToDefault();
            SyncUiFromSnapshot(SMDMicrophone.Current);
#endif
        }

#if !BASIS_DISABLE_MICROPHONE
        public static PanelSlider sliderMicrophoneVolume;
        public static PanelDropdown dropdownMicrophoneSelection;
        public static PanelSlider sliderLimitThreshold;
        public static PanelSlider sliderLimitKnee;
        public static PanelSlider sliderDenoiseWet;
        public static PanelSlider sliderDenoiseMakeup;
        public static PanelSlider sliderAgcTarget;
        public static PanelSlider sliderAgcMaxGain;
        public static PanelSlider sliderAgcAttack;
        public static PanelSlider sliderAgcRelease;
        public static PanelSlider sliderNoiseGateThreshold;
        public static PanelSlider sliderNoiseGateAttack;
        public static PanelSlider sliderNoiseGateRelease;

        /// <summary>
        /// allows us to get up to date information directly from the microphone
        /// </summary>
        public static void RefreshMicrophoneDeviceEntries()
        {
            if (dropdownMicrophoneSelection == null) return;
            dropdownMicrophoneSelection.AssignEntries(SMDMicrophone.MicrophoneDevices?.ToList() ?? new List<string>());
            dropdownMicrophoneSelection.SetValueWithoutNotify(SMDMicrophone.Current.Microphone);
        }

        public static void SyncUiFromSnapshot(SMDMicrophone.MicSettings s)
        {
            if (BasisMainMenu.ActiveMenuTitle == SettingsProvider.StaticTitle)
            {
                if (sliderMicrophoneVolume != null)
                    sliderMicrophoneVolume.SetValueWithoutNotify(s.Volume01);

                if (dropdownMicrophoneSelection != null)
                    dropdownMicrophoneSelection.SetValueWithoutNotify(s.Microphone);

                if (sliderLimitThreshold != null)
                    sliderLimitThreshold.SetValueWithoutNotify(s.LimitThreshold);

                if (sliderLimitKnee != null)
                    sliderLimitKnee.SetValueWithoutNotify(s.LimitKnee);

                if (sliderDenoiseWet != null)
                    sliderDenoiseWet.SetValueWithoutNotify(s.DenoiseWet);

                if (sliderDenoiseMakeup != null)
                    sliderDenoiseMakeup.SetValueWithoutNotify(s.DenoiseMakeupDb);

                // if (sliderAgcTarget != null)
                //     sliderAgcTarget.SetValueWithoutNotify(s.AgcTargetRms);

                if (sliderAgcMaxGain != null)
                    sliderAgcMaxGain.SetValueWithoutNotify(s.AgcMaxGainDb);

                if (sliderAgcAttack != null)
                    sliderAgcAttack.SetValueWithoutNotify(s.AgcAttack);

                if (sliderAgcRelease != null)
                    sliderAgcRelease.SetValueWithoutNotify(s.AgcRelease);

                if (sliderNoiseGateThreshold != null)
                    sliderNoiseGateThreshold.SetValueWithoutNotify(s.NoiseGateThreshold);

                if (sliderNoiseGateAttack != null)
                    sliderNoiseGateAttack.SetValueWithoutNotify(s.NoiseGateAttack);

                if (sliderNoiseGateRelease != null)
                    sliderNoiseGateRelease.SetValueWithoutNotify(s.NoiseGateRelease);
            }
        }
#endif

        // ------------------
        // GRAPHICS TAB
        // ------------------
        public static PanelTabPage GraphicsTab(PanelTabGroup tabGroup)
        {
            PanelTabPage tab = PanelTabPage.CreateVertical(tabGroup.Descriptor.ContentParent);
            PanelElementDescriptor descriptor = tab.Descriptor;
            descriptor.SetTitle(BasisLocalization.Get("settings.graphics.title"));

            RectTransform container = descriptor.ContentParent;

            BuildPerformanceModeSection(container);

            SettingsProviderFrameBottleneck.BuildFrameBottleneckGroup(container, descriptor);

            // Every segment here reads from Unity's Sampler/Recorder/ProfilerMarker APIs, which are
            // stripped/disabled outside the Editor and Development Builds — the whole breakdown would
            // just read zeros in a release build, so it isn't built there at all. Also gated behind the
            // Developer toggle (General tab) even in a Development Build, per the maintainer's call.
            if (Debug.isDebugBuild && BasisSettingsDefaults.ShowDeveloperTab.RawValue)
            {
                SettingsProviderPerformanceBar.BuildPerformanceBarGroup(container, descriptor);
            }

            // Renderer (+ its DX12-only PSO cache control) moved up here, right under the
            // performance breakdown, instead of sitting at the foot of the page.
            BuildRendererSection(container, descriptor);

            PanelSectionToggle qualityToggle = PanelSectionToggle.CreateNewEntry(container);
            PanelElementDescriptor qualityGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                qualityToggle,
                container,
                BasisLocalization.Get("settings.graphics.quality.title"),
                showGroupTitle: false);

            // Avatar visibility limits (relocated from General). Lives at the
            // top of the quality group so users see distance/limit controls
            // before per-pixel quality knobs.
            PanelSlider sliderAvatarRange = PanelSlider.CreateEntryAndBind(
                qualityGroup,
                PanelSlider.SliderSettings.Distance(BasisLocalization.Get("settings.general.avatarRange"), 250),
                BasisSettingsDefaults.AvatarRange);
            sliderAvatarRange.Descriptor.SetTooltip(BasisLocalization.Get("settings.general.avatarRange.tooltip"));

            PanelToggle toggleRangeIndicator = PanelToggle.CreateNewEntry(qualityGroup);
            toggleRangeIndicator.AssignBinding(BasisSettingsDefaults.AvatarRangeIndicator);
            toggleRangeIndicator.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.avatarRangeIndicator"));
            toggleRangeIndicator.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.avatarRangeIndicator.tooltip"));

            PanelToggle toggleLimitAvatars = PanelToggle.CreateNewEntry(qualityGroup);
            toggleLimitAvatars.AssignBinding(BasisSettingsDefaults.UseMaxVisibleAvatars);
            toggleLimitAvatars.Descriptor.SetTitle(BasisLocalization.Get("settings.general.limitAvatars"));
            toggleLimitAvatars.Descriptor.SetTooltip(BasisLocalization.Get("settings.general.limitAvatars.tooltip"));

            PanelSlider sliderMaxVisibleAvatars = PanelSlider.CreateEntryAndBind(
                qualityGroup,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.general.maxAvatars"), 0, 250, true, 0, ValueDisplayMode.Raw),
                BasisSettingsDefaults.MaxVisibleAvatars);
            sliderMaxVisibleAvatars.Descriptor.SetTooltip(BasisLocalization.Get("settings.general.maxAvatars.tooltip"));

            sliderMaxVisibleAvatars.Descriptor.SetActive(toggleLimitAvatars.Value);
            toggleLimitAvatars.OnValueChanged += (val) =>
            {
                sliderMaxVisibleAvatars.Descriptor.SetActive(val);
                qualityGroup.ForceRebuild();
            };

            PanelDropdown dropdownQualityLevel = PanelDropdown.CreateNewEntry(qualityGroup.ContentParent);
            dropdownQualityLevel.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.qualityLevel"));
            dropdownQualityLevel.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.qualityLevel.tooltip"));
            dropdownQualityLevel.AssignLocalizedEntries(
                new List<string> { "Very Low", "Low", "Medium", "High", "Ultra" },
                new List<string> { "settings.graphics.quality.veryLow", "settings.graphics.quality.low", "settings.graphics.quality.medium", "settings.graphics.quality.high", "settings.graphics.quality.ultra" });
            dropdownQualityLevel.AssignBinding(BasisSettingsDefaults.QualityLevel);

            PanelDropdown dropdownShadowQuality = PanelDropdown.CreateNewEntry(qualityGroup.ContentParent);
            dropdownShadowQuality.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.shadowQuality"));
            dropdownShadowQuality.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.shadowQuality.tooltip"));
            dropdownShadowQuality.AssignLocalizedEntries(
                new List<string> { "Very Low", "Low", "Medium", "High", "Ultra" },
                new List<string> { "settings.graphics.quality.veryLow", "settings.graphics.quality.low", "settings.graphics.quality.medium", "settings.graphics.quality.high", "settings.graphics.quality.ultra" });
            dropdownShadowQuality.AssignBinding(BasisSettingsDefaults.ShadowQuality);

            PanelDropdown dropdownAntialiasing = PanelDropdown.CreateNewEntry(qualityGroup.ContentParent);
            dropdownAntialiasing.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.antialiasing"));
            dropdownAntialiasing.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.antialiasing.tooltip"));
            dropdownAntialiasing.AssignLocalizedEntries(
                new List<string> { "Off","MSAA 2X","MSAA 4X","MSAA 8X","Linear","Point","FSR"/*,"STP"*/ },
                new List<string> { "ui.option.off", "settings.graphics.aa.msaa2x", "settings.graphics.aa.msaa4x", "settings.graphics.aa.msaa8x", "settings.graphics.aa.linear", "settings.graphics.aa.point", "settings.graphics.aa.fsr" },
                new List<string> { "settings.graphics.aa.off.tooltip" });
            dropdownAntialiasing.AssignBinding(BasisSettingsDefaults.Antialiasing);

            if (BasisDeviceManagement.IsUserInDesktop())
            {
                PanelDropdown dropdownVSync = PanelDropdown.CreateNewEntry(qualityGroup.ContentParent);
                dropdownVSync.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.verticalSync"));
                dropdownVSync.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.verticalSync.tooltip"));
                dropdownVSync.AssignLocalizedEntries(
                    new List<string> { "On", "Capped", "Off", "Half" },
                    new List<string> { "ui.option.on", "settings.graphics.vsync.capped", "ui.option.off", "settings.graphics.vsync.half" },
                    new List<string> { "settings.graphics.vsync.on.tooltip", null, "settings.graphics.vsync.off.tooltip" });
                dropdownVSync.AssignBinding(BasisSettingsDefaults.VSync);

                PanelTextField fpsCapField = PanelTextField.CreateNewEntry(qualityGroup.ContentParent);
                fpsCapField.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.frameRateCap"));
                fpsCapField.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.frameRateCap.tooltip"));
                fpsCapField.AssignBinding(BasisSettingsDefaults.VSyncCapFps);

                TMP_InputField fpsInput = fpsCapField._inputField;
                if (fpsInput != null)
                {
                    fpsInput.contentType = TMP_InputField.ContentType.IntegerNumber;
                    fpsInput.lineType = TMP_InputField.LineType.SingleLine;
                }

                fpsCapField.Descriptor.SetActive(dropdownVSync.Value == "Capped");

                dropdownVSync.OnValueChanged += (val) =>
                {
                    fpsCapField.Descriptor.SetActive(val == "Capped");
                    qualityGroup.ForceRebuild();
                };
            }

            if (BasisDeviceManagement.IsCurrentModeVR())
            {
                PanelDropdown dropdownRefreshRate = PanelDropdown.CreateNewEntry(qualityGroup.ContentParent);
                dropdownRefreshRate.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.refreshRate"));
                dropdownRefreshRate.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.refreshRate.tooltip"));
                dropdownRefreshRate.AssignLocalizedEntries(
                    new List<string> { "Auto", "72", "90", "120", "144" },
                    new List<string> { "settings.graphics.refreshRate.auto", "settings.graphics.refreshRate.72", "settings.graphics.refreshRate.90", "settings.graphics.refreshRate.120", "settings.graphics.refreshRate.144" },
                    new List<string> { "settings.graphics.refreshRate.auto.tooltip" });
                dropdownRefreshRate.AssignBinding(BasisSettingsDefaults.HeadsetRefreshRate);
            }

            // Moved in from the old standalone Rendering section — memory allocation, resolution and
            // screen mode now live here in Quality alongside the rest of the display/quality knobs.
            PanelDropdown dropdownMemoryAllocation = PanelDropdown.CreateNewEntry(qualityGroup.ContentParent);
            dropdownMemoryAllocation.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.memoryAllocation"));
            dropdownMemoryAllocation.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.memoryAllocation.tooltip"));
            dropdownMemoryAllocation.AssignLocalizedEntries(
                new List<string> { "Dynamic", "256", "512", "1024", "2048", "4096", "8192" },
                new List<string> { "settings.graphics.memoryAllocation.dynamic", "256", "512", "1024", "2048", "4096", "8192" });
            dropdownMemoryAllocation.AssignBinding(BasisSettingsDefaults.MemoryAllocation);

            dropdownResolution = PanelDropdown.CreateNewEntry(qualityGroup.ContentParent);
            dropdownResolution.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.resolution"));
            dropdownResolution.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.resolution.tooltip"));
            uniqueResolutions = new List<Vector2Int>();
            resolutionOptions = new List<string>();

            foreach (Resolution res in Screen.resolutions)
            {
                Vector2Int size = new Vector2Int(res.width, res.height);
                if (!uniqueResolutions.Contains(size))
                {
                    uniqueResolutions.Add(size);
                    resolutionOptions.Add(size.x + " x " + size.y);
                }
            }

            dropdownResolution.AssignEntries(resolutionOptions);
            dropdownResolution.DropdownComponent.onValueChanged.AddListener(ResolutionChanged);
            SettingsProviderBottleneckHints.Mark(dropdownResolution, BasisFrameCostSide.Gpu);

            int currentIndex = Mathf.Max(0, uniqueResolutions.FindIndex(r => r.x == Screen.width && r.y == Screen.height));
            dropdownResolution.DropdownComponent.SetValueWithoutNotify(currentIndex);

            dropdownScreenMode = PanelDropdown.CreateNewEntry(qualityGroup.ContentParent);
            // Screen mode entries stay as stable identifiers; GetScreenModeFromIndex
            // depends on fixed ordering, so these aren't localized.
            List<string> screenModeOptions = new List<string> { "Fullscreen", "Borderless Window", "Windowed" };

            dropdownScreenMode.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.screenMode"));
            dropdownScreenMode.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.screenMode.tooltip"));
            dropdownScreenMode.AssignLocalizedEntries(
                screenModeOptions,
                new List<string> { "settings.graphics.screenMode.fullscreen", "settings.graphics.screenMode.borderless", "settings.graphics.screenMode.windowed" });
            dropdownScreenMode.DropdownComponent.onValueChanged.AddListener(ScreenMode);
            dropdownScreenMode.DropdownComponent.SetValueWithoutNotify(GetIndexFromScreenMode(Screen.fullScreenMode));

            PanelSectionToggleHelpers.FinalizeCollapsibleGroup(qualityToggle, qualityGroup, true,
                _ => descriptor.ForceRebuild());

#if BASIS_HAS_GI && !UNITY_ANDROID
            {
                PanelSectionToggle giToggle = PanelSectionToggle.CreateNewEntry(container);
                PanelElementDescriptor giGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                    giToggle,
                    container,
                    BasisLocalization.Get("settings.graphics.gi.title"),
                    showGroupTitle: false);

                PanelToggle toggleGi = PanelToggle.CreateNewEntry(giGroup.ContentParent);
                toggleGi.AssignBinding(BasisSettingsDefaults.UseGlobalIllumination);
                toggleGi.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.gi.enable"));
                toggleGi.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.enable.tooltip"));
                SettingsProviderBottleneckHints.Mark(toggleGi, BasisFrameCostSide.Gpu);

                PanelDropdown dropdownGiPreset = PanelDropdown.CreateNewEntry(giGroup.ContentParent);
                dropdownGiPreset.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.gi.preset"));
                dropdownGiPreset.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.preset.tooltip"));
                dropdownGiPreset.AssignLocalizedEntries(
                    new List<string> { "Natural", "Unnatural" },
                    new List<string> { "settings.graphics.gi.preset.natural", "settings.graphics.gi.preset.unnatural" });
                dropdownGiPreset.AssignBinding(BasisSettingsDefaults.GlobalIlluminationPreset);

                PanelDropdown dropdownGiMode = PanelDropdown.CreateNewEntry(giGroup.ContentParent);
                dropdownGiMode.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.gi.mode"));
                dropdownGiMode.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.mode.tooltip"));
                dropdownGiMode.AssignLocalizedEntries(
                    new List<string> { "Screen Space", "Ray Traced" },
                    new List<string> { "settings.graphics.gi.mode.screenSpace", "settings.graphics.gi.mode.rayTraced" });
                dropdownGiMode.AssignBinding(BasisSettingsDefaults.GlobalIlluminationMode);
                SettingsProviderBottleneckHints.Mark(dropdownGiMode, BasisFrameCostSide.Gpu);

                PanelDropdown dropdownGiLayers = PanelDropdown.CreateNewEntry(giGroup.ContentParent);
                dropdownGiLayers.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.gi.layers"));
                dropdownGiLayers.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.layers.tooltip"));
                dropdownGiLayers.AssignLocalizedEntries(
                    new List<string> { "Avatars", "World", "World And Avatars" },
                    new List<string> { "settings.graphics.gi.layers.avatars", "settings.graphics.gi.layers.world", "settings.graphics.gi.layers.worldAndAvatars" });
                dropdownGiLayers.AssignBinding(BasisSettingsDefaults.GlobalIlluminationLayers);
                SettingsProviderBottleneckHints.Mark(dropdownGiLayers, BasisFrameCostSide.Gpu);

                PanelDropdown dropdownGiSkinned = PanelDropdown.CreateNewEntry(giGroup.ContentParent);
                dropdownGiSkinned.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.gi.skinned"));
                dropdownGiSkinned.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.skinned.tooltip"));
                dropdownGiSkinned.AssignLocalizedEntries(
                    new List<string> { "Off", "Proxy" },
                    new List<string> { "settings.graphics.gi.skinned.off", "settings.graphics.gi.skinned.proxy" });
                dropdownGiSkinned.AssignBinding(BasisSettingsDefaults.GlobalIlluminationSkinnedMeshes);
                SettingsProviderBottleneckHints.Mark(dropdownGiSkinned, BasisFrameCostSide.Cpu);

                PanelDropdown dropdownGiQuality = PanelDropdown.CreateNewEntry(giGroup.ContentParent);
                dropdownGiQuality.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.gi.quality"));
                dropdownGiQuality.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.quality.tooltip"));
                dropdownGiQuality.AssignLocalizedEntries(
                    new List<string> { "Low", "Medium", "High", "Ultra" },
                    new List<string> { "settings.graphics.quality.low", "settings.graphics.quality.medium", "settings.graphics.quality.high", "settings.graphics.quality.ultra" });
                dropdownGiQuality.AssignBinding(BasisSettingsDefaults.GlobalIlluminationQuality);
                SettingsProviderBottleneckHints.Mark(dropdownGiQuality, BasisFrameCostSide.Gpu);

                PanelDropdown dropdownGiResolution = PanelDropdown.CreateNewEntry(giGroup.ContentParent);
                dropdownGiResolution.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.gi.resolution"));
                dropdownGiResolution.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.resolution.tooltip"));
                dropdownGiResolution.AssignLocalizedEntries(
                    new List<string> { "Full", "Half", "Quarter" },
                    new List<string> { "settings.graphics.gi.resolution.full", "settings.graphics.gi.resolution.half", "settings.graphics.gi.resolution.quarter" });
                dropdownGiResolution.AssignBinding(BasisSettingsDefaults.GlobalIlluminationResolution);
                SettingsProviderBottleneckHints.Mark(dropdownGiResolution, BasisFrameCostSide.Gpu);

                PanelSlider sliderGiIntensity = PanelSlider.CreateEntryAndBind(
                    giGroup.ContentParent,
                    new PanelSlider.SliderSettings(BasisLocalization.Get("settings.graphics.gi.intensity"),
                        "",
                        BasisSettingsDefaults.GI_INTENSITY_MIN,
                        BasisSettingsDefaults.GI_INTENSITY_MAX,
                        false, 2, ValueDisplayMode.Raw),
                    BasisSettingsDefaults.GlobalIlluminationIntensity);
                sliderGiIntensity.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.intensity.tooltip"));


                // The tracing internals live behind their own fold. They are the values that were fixed
                // constants until an artifact needed explaining, and moving one is how you find out which
                // of them is responsible - but none of them is a setting anybody should need to touch to
                // get a good picture, so they do not sit in front of the people who do not.
                PanelSectionToggle giAdvancedToggle = PanelSectionToggle.CreateNewEntry(giGroup.ContentParent);
                PanelElementDescriptor giAdvanced = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                    giAdvancedToggle,
                    giGroup.ContentParent,
                    BasisLocalization.Get("settings.graphics.gi.advanced"),
                    showGroupTitle: false);
                PanelSlider sliderGiSaturation = PanelSlider.CreateEntryAndBind(
                    giAdvanced.ContentParent,
                    new PanelSlider.SliderSettings(BasisLocalization.Get("settings.graphics.gi.saturation"),
                        "",
                        BasisSettingsDefaults.GI_SATURATION_MIN,
                        BasisSettingsDefaults.GI_SATURATION_MAX,
                        false, 2, ValueDisplayMode.Raw),
                    BasisSettingsDefaults.GlobalIlluminationSaturation);
                sliderGiSaturation.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.saturation.tooltip"));

                PanelSlider sliderGiObscurance = PanelSlider.CreateEntryAndBind(
                    giAdvanced.ContentParent,
                    new PanelSlider.SliderSettings(BasisLocalization.Get("settings.graphics.gi.obscurance"),
                        "",
                        BasisSettingsDefaults.GI_OBSCURANCE_MIN,
                        BasisSettingsDefaults.GI_OBSCURANCE_MAX,
                        false, 2, ValueDisplayMode.Raw),
                    BasisSettingsDefaults.GlobalIlluminationObscurance);
                sliderGiObscurance.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.obscurance.tooltip"));

                PanelSlider sliderGiRayLength = PanelSlider.CreateEntryAndBind(
                    giAdvanced.ContentParent,
                    new PanelSlider.SliderSettings(BasisLocalization.Get("settings.graphics.gi.rayLength"),
                        "",
                        BasisSettingsDefaults.GI_RAY_LENGTH_MIN,
                        BasisSettingsDefaults.GI_RAY_LENGTH_MAX,
                        false, 1, ValueDisplayMode.Raw),
                    BasisSettingsDefaults.GlobalIlluminationRayLength);
                sliderGiRayLength.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.rayLength.tooltip"));
                SettingsProviderBottleneckHints.Mark(sliderGiRayLength, BasisFrameCostSide.Gpu);

                PanelSlider sliderGiSmoothing = PanelSlider.CreateEntryAndBind(
                    giAdvanced.ContentParent,
                    new PanelSlider.SliderSettings(BasisLocalization.Get("settings.graphics.gi.smoothing"),
                        "",
                        BasisSettingsDefaults.GI_SMOOTHING_MIN,
                        BasisSettingsDefaults.GI_SMOOTHING_MAX,
                        false, 2, ValueDisplayMode.Raw),
                    BasisSettingsDefaults.GlobalIlluminationSmoothing);
                sliderGiSmoothing.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.smoothing.tooltip"));

                PanelToggle toggleGiWideBlur = PanelToggle.CreateNewEntry(giAdvanced.ContentParent);
                toggleGiWideBlur.AssignBinding(BasisSettingsDefaults.GlobalIlluminationWideBlur);
                toggleGiWideBlur.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.gi.wideBlur"));
                toggleGiWideBlur.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.wideBlur.tooltip"));

                PanelToggle toggleGiTemporal = PanelToggle.CreateNewEntry(giAdvanced.ContentParent);
                toggleGiTemporal.AssignBinding(BasisSettingsDefaults.GlobalIlluminationTemporalFilter);
                toggleGiTemporal.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.gi.temporalFilter"));
                toggleGiTemporal.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.temporalFilter.tooltip"));

                PanelSlider sliderGiTemporalResponse = PanelSlider.CreateEntryAndBind(
                    giAdvanced.ContentParent,
                    new PanelSlider.SliderSettings(BasisLocalization.Get("settings.graphics.gi.temporalResponse"),
                        "",
                        BasisSettingsDefaults.GI_TEMPORAL_RESPONSE_MIN,
                        BasisSettingsDefaults.GI_TEMPORAL_RESPONSE_MAX,
                        false, 2, ValueDisplayMode.Raw),
                    BasisSettingsDefaults.GlobalIlluminationTemporalResponse);
                sliderGiTemporalResponse.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.temporalResponse.tooltip"));

                PanelDropdown dropdownGiFallback = PanelDropdown.CreateNewEntry(giAdvanced.ContentParent);
                dropdownGiFallback.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.gi.fallback"));
                dropdownGiFallback.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.fallback.tooltip"));
                dropdownGiFallback.AssignLocalizedEntries(
                    new List<string> { "None", "Sky", "Reflection Probe" },
                    new List<string> { "settings.graphics.gi.fallback.none", "settings.graphics.gi.fallback.sky", "settings.graphics.gi.fallback.probe" });
                dropdownGiFallback.AssignBinding(BasisSettingsDefaults.GlobalIlluminationFallback);

                // Ray traced only - screen space has no per-surface baked-emissive flag to check, it just
                // reads whatever is already on screen.
                PanelToggle toggleGiIgnoreBakedEmission = PanelToggle.CreateNewEntry(giAdvanced.ContentParent);
                toggleGiIgnoreBakedEmission.AssignBinding(BasisSettingsDefaults.GlobalIlluminationIgnoreBakedEmission);
                toggleGiIgnoreBakedEmission.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.gi.ignoreBakedEmission"));
                toggleGiIgnoreBakedEmission.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.ignoreBakedEmission.tooltip"));

                // Both modes: a lightmapped surface's own bounce and occlusion are already in its lightmap,
                // and this is how much of the realtime effect it still receives on top.
                PanelSlider sliderGiLightmappedReceive = PanelSlider.CreateEntryAndBind(
                    giAdvanced.ContentParent,
                    new PanelSlider.SliderSettings(BasisLocalization.Get("settings.graphics.gi.lightmappedReceive"),
                        "",
                        BasisSettingsDefaults.GI_LIGHTMAPPED_RECEIVE_MIN,
                        BasisSettingsDefaults.GI_LIGHTMAPPED_RECEIVE_MAX,
                        false, 2, ValueDisplayMode.Raw),
                    BasisSettingsDefaults.GlobalIlluminationLightmappedReceive);
                sliderGiLightmappedReceive.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.lightmappedReceive.tooltip"));

                PanelToggle toggleGiRayReuse = PanelToggle.CreateNewEntry(giAdvanced.ContentParent);
                toggleGiRayReuse.AssignBinding(BasisSettingsDefaults.GlobalIlluminationRayReuse);
                toggleGiRayReuse.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.gi.rayReuse"));
                toggleGiRayReuse.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.rayReuse.tooltip"));

                PanelToggle toggleGiEmitters = PanelToggle.CreateNewEntry(giAdvanced.ContentParent);
                toggleGiEmitters.AssignBinding(BasisSettingsDefaults.GlobalIlluminationEmitters);
                toggleGiEmitters.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.gi.emitters"));
                toggleGiEmitters.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.emitters.tooltip"));

                PanelSlider sliderGiEmitterIntensity = PanelSlider.CreateEntryAndBind(
                    giAdvanced.ContentParent,
                    new PanelSlider.SliderSettings(BasisLocalization.Get("settings.graphics.gi.emitterIntensity"),
                        "",
                        BasisSettingsDefaults.GI_EMITTER_INTENSITY_MIN,
                        BasisSettingsDefaults.GI_EMITTER_INTENSITY_MAX,
                        false, 2, ValueDisplayMode.Raw),
                    BasisSettingsDefaults.GlobalIlluminationEmitterIntensity);
                sliderGiEmitterIntensity.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.emitterIntensity.tooltip"));

                PanelToggle toggleGiReflectionProbes = PanelToggle.CreateNewEntry(giAdvanced.ContentParent);
                toggleGiReflectionProbes.AssignBinding(BasisSettingsDefaults.GlobalIlluminationReflectionProbes);
                toggleGiReflectionProbes.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.gi.reflectionProbes"));
                toggleGiReflectionProbes.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.reflectionProbes.tooltip"));
                SettingsProviderBottleneckHints.Mark(toggleGiReflectionProbes, BasisFrameCostSide.Gpu);

                PanelToggle toggleGiSpecular = PanelToggle.CreateNewEntry(giAdvanced.ContentParent);
                toggleGiSpecular.AssignBinding(BasisSettingsDefaults.GlobalIlluminationSpecular);
                toggleGiSpecular.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.gi.specular"));
                toggleGiSpecular.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.specular.tooltip"));
                SettingsProviderBottleneckHints.Mark(toggleGiSpecular, BasisFrameCostSide.Gpu);

                PanelSlider sliderGiSpecularIntensity = PanelSlider.CreateEntryAndBind(
                    giAdvanced.ContentParent,
                    new PanelSlider.SliderSettings(BasisLocalization.Get("settings.graphics.gi.specularIntensity"),
                        "",
                        BasisSettingsDefaults.GI_SPECULAR_INTENSITY_MIN,
                        BasisSettingsDefaults.GI_SPECULAR_INTENSITY_MAX,
                        false, 2, ValueDisplayMode.Raw),
                    BasisSettingsDefaults.GlobalIlluminationSpecularIntensity);
                sliderGiSpecularIntensity.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.specularIntensity.tooltip"));

                PanelSlider sliderGiSpecularMaxRoughness = PanelSlider.CreateEntryAndBind(
                    giAdvanced.ContentParent,
                    new PanelSlider.SliderSettings(BasisLocalization.Get("settings.graphics.gi.specularMaxRoughness"),
                        "",
                        BasisSettingsDefaults.GI_SPECULAR_MAX_ROUGHNESS_MIN,
                        BasisSettingsDefaults.GI_SPECULAR_MAX_ROUGHNESS_MAX,
                        false, 2, ValueDisplayMode.Raw),
                    BasisSettingsDefaults.GlobalIlluminationSpecularMaxRoughness);
                sliderGiSpecularMaxRoughness.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.specularMaxRoughness.tooltip"));

                PanelSlider sliderGiSpecularRayLength = PanelSlider.CreateEntryAndBind(
                    giAdvanced.ContentParent,
                    new PanelSlider.SliderSettings(BasisLocalization.Get("settings.graphics.gi.specularRayLength"),
                        "",
                        BasisSettingsDefaults.GI_SPECULAR_RAY_LENGTH_MIN,
                        BasisSettingsDefaults.GI_SPECULAR_RAY_LENGTH_MAX,
                        false, 0, ValueDisplayMode.Raw),
                    BasisSettingsDefaults.GlobalIlluminationSpecularRayLength);
                sliderGiSpecularRayLength.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.specularRayLength.tooltip"));
                SettingsProviderBottleneckHints.Mark(sliderGiSpecularRayLength, BasisFrameCostSide.Gpu);

                PanelSlider sliderGiSpecularFadeDistance = PanelSlider.CreateEntryAndBind(
                    giAdvanced.ContentParent,
                    new PanelSlider.SliderSettings(BasisLocalization.Get("settings.graphics.gi.specularFadeDistance"),
                        "",
                        BasisSettingsDefaults.GI_SPECULAR_FADE_DISTANCE_MIN,
                        BasisSettingsDefaults.GI_SPECULAR_FADE_DISTANCE_MAX,
                        false, 0, ValueDisplayMode.Raw),
                    BasisSettingsDefaults.GlobalIlluminationSpecularFadeDistance);
                sliderGiSpecularFadeDistance.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.specularFadeDistance.tooltip"));

                PanelSlider sliderGiObscuranceRadius = PanelSlider.CreateEntryAndBind(
                    giAdvanced.ContentParent,
                    new PanelSlider.SliderSettings(BasisLocalization.Get("settings.graphics.gi.obscuranceRadius"),
                        "",
                        BasisSettingsDefaults.GI_OBSCURANCE_RADIUS_MIN,
                        BasisSettingsDefaults.GI_OBSCURANCE_RADIUS_MAX,
                        false, 2, ValueDisplayMode.Raw),
                    BasisSettingsDefaults.GlobalIlluminationObscuranceRadius);
                sliderGiObscuranceRadius.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.obscuranceRadius.tooltip"));

                PanelSlider sliderGiFadeDistance = PanelSlider.CreateEntryAndBind(
                    giAdvanced.ContentParent,
                    new PanelSlider.SliderSettings(BasisLocalization.Get("settings.graphics.gi.fadeDistance"),
                        "",
                        BasisSettingsDefaults.GI_FADE_DISTANCE_MIN,
                        BasisSettingsDefaults.GI_FADE_DISTANCE_MAX,
                        false, 0, ValueDisplayMode.Raw),
                    BasisSettingsDefaults.GlobalIlluminationFadeDistance);
                sliderGiFadeDistance.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.fadeDistance.tooltip"));

                PanelSlider sliderGiNormalBias = PanelSlider.CreateEntryAndBind(
                    giAdvanced.ContentParent,
                    new PanelSlider.SliderSettings(BasisLocalization.Get("settings.graphics.gi.normalBias"),
                        "",
                        BasisSettingsDefaults.GI_NORMAL_BIAS_MIN,
                        BasisSettingsDefaults.GI_NORMAL_BIAS_MAX,
                        false, 3, ValueDisplayMode.Raw),
                    BasisSettingsDefaults.GlobalIlluminationNormalBias);
                sliderGiNormalBias.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.normalBias.tooltip"));

                PanelSlider sliderGiDistanceBias = PanelSlider.CreateEntryAndBind(
                    giAdvanced.ContentParent,
                    new PanelSlider.SliderSettings(BasisLocalization.Get("settings.graphics.gi.distanceBias"),
                        "",
                        BasisSettingsDefaults.GI_DISTANCE_BIAS_MIN,
                        BasisSettingsDefaults.GI_DISTANCE_BIAS_MAX,
                        false, 4, ValueDisplayMode.Raw),
                    BasisSettingsDefaults.GlobalIlluminationDistanceBias);
                sliderGiDistanceBias.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.distanceBias.tooltip"));

                PanelSlider sliderGiBounceThreshold = PanelSlider.CreateEntryAndBind(
                    giAdvanced.ContentParent,
                    new PanelSlider.SliderSettings(BasisLocalization.Get("settings.graphics.gi.bounceThreshold"),
                        "",
                        BasisSettingsDefaults.GI_BOUNCE_THRESHOLD_MIN,
                        BasisSettingsDefaults.GI_BOUNCE_THRESHOLD_MAX,
                        false, 3, ValueDisplayMode.Raw),
                    BasisSettingsDefaults.GlobalIlluminationBounceThreshold);
                sliderGiBounceThreshold.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.bounceThreshold.tooltip"));

                PanelSlider sliderGiFireflyClamp = PanelSlider.CreateEntryAndBind(
                    giAdvanced.ContentParent,
                    new PanelSlider.SliderSettings(BasisLocalization.Get("settings.graphics.gi.fireflyClamp"),
                        "",
                        BasisSettingsDefaults.GI_FIREFLY_CLAMP_MIN,
                        BasisSettingsDefaults.GI_FIREFLY_CLAMP_MAX,
                        false, 1, ValueDisplayMode.Raw),
                    BasisSettingsDefaults.GlobalIlluminationFireflyClamp);
                sliderGiFireflyClamp.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gi.fireflyClamp.tooltip"));

                PanelSectionToggleHelpers.FinalizeCollapsibleGroup(giAdvancedToggle, giAdvanced, false,
                    _ => RebuildGiLayout());

                // Snaps the three sliders below to a look rather than a value: Natural is each at its
                // own default, Unnatural is each at the extreme end of its own range. ApplyDrivenValue
                // rather than SetValue, because SetValue never touches the Slider component itself -
                // only ApplyDrivenValue (via SetValueWithoutNotify) moves the handle and fill to match,
                // the same way a thumbstick bind or a reset drives a control it does not own.
                void ApplyGiPreset(string preset)
                {
                    bool unnatural = preset == "Unnatural";
                    sliderGiIntensity.ApplyDrivenValue(unnatural
                        ? BasisSettingsDefaults.GI_INTENSITY_MAX
                        : BasisSettingsDefaults.GlobalIlluminationIntensity.DefaultValue.GetDefault());
                    sliderGiSaturation.ApplyDrivenValue(unnatural
                        ? BasisSettingsDefaults.GI_SATURATION_MAX
                        : BasisSettingsDefaults.GlobalIlluminationSaturation.DefaultValue.GetDefault());
                    sliderGiEmitterIntensity.ApplyDrivenValue(unnatural
                        ? BasisSettingsDefaults.GI_EMITTER_INTENSITY_MAX
                        : BasisSettingsDefaults.GlobalIlluminationEmitterIntensity.DefaultValue.GetDefault());
                }
                dropdownGiPreset.OnValueChanged += ApplyGiPreset;

                void SetGiRowsActive(bool val)
                {
                    bool rayTraced = val && dropdownGiMode.Value == "Ray Traced";
                    dropdownGiPreset.Descriptor.SetActive(val);
                    dropdownGiMode.Descriptor.SetActive(val);
                    // Both of these describe what goes into the acceleration structure, which only the ray
                    // traced path builds. On screen space the trace walks the depth buffer, so neither has
                    // anything to act on and showing them promises a control that does nothing.
                    dropdownGiLayers.Descriptor.SetActive(rayTraced);
                    dropdownGiSkinned.Descriptor.SetActive(rayTraced);
                    dropdownGiQuality.Descriptor.SetActive(val);
                    dropdownGiResolution.Descriptor.SetActive(val);
                    sliderGiIntensity.Descriptor.SetActive(val);
                    sliderGiSaturation.Descriptor.SetActive(val);
                    sliderGiObscurance.Descriptor.SetActive(val);
                    sliderGiRayLength.Descriptor.SetActive(val);
                    sliderGiSmoothing.Descriptor.SetActive(val);
                    toggleGiWideBlur.Descriptor.SetActive(val);
                    toggleGiTemporal.Descriptor.SetActive(val);
                    sliderGiTemporalResponse.Descriptor.SetActive(val && toggleGiTemporal.Value);
                    dropdownGiFallback.Descriptor.SetActive(val);
                    toggleGiIgnoreBakedEmission.Descriptor.SetActive(rayTraced);
                    sliderGiLightmappedReceive.Descriptor.SetActive(val);
                    toggleGiRayReuse.Descriptor.SetActive(val && !rayTraced);
                    toggleGiEmitters.Descriptor.SetActive(val);
                    sliderGiEmitterIntensity.Descriptor.SetActive(val && toggleGiEmitters.Value);
                    toggleGiReflectionProbes.Descriptor.SetActive(val);

                    // The fold itself goes with the effect, so turning global illumination off does not
                    // leave a section header standing over nothing.
                    giAdvancedToggle.Descriptor.SetActive(val);

                    // Obscurance radius, fade distance and the firefly ceiling are read by both traces.
                    sliderGiObscuranceRadius.Descriptor.SetActive(val);
                    sliderGiFadeDistance.Descriptor.SetActive(val);
                    sliderGiFireflyClamp.Descriptor.SetActive(val);

                    // The reflection controls follow their own toggle the way emitter intensity follows
                    // its own - both backends read all four, so none of them is gated on the mode.
                    bool specular = val && toggleGiSpecular.Value;
                    sliderGiSpecularIntensity.Descriptor.SetActive(specular);
                    sliderGiSpecularMaxRoughness.Descriptor.SetActive(specular);
                    sliderGiSpecularRayLength.Descriptor.SetActive(specular);
                    sliderGiSpecularFadeDistance.Descriptor.SetActive(specular);

                    // The ray origin offsets and the bounce cutoff only exist on the traced path - the
                    // screen space march walks the depth buffer and has no ray origin to push off a surface.
                    sliderGiNormalBias.Descriptor.SetActive(rayTraced);
                    sliderGiDistanceBias.Descriptor.SetActive(rayTraced);
                    sliderGiBounceThreshold.Descriptor.SetActive(rayTraced);
                }

                // giAdvanced nests inside giGroup, so a row toggled inside the fold needs giGroup
                // rebuilt too - rebuilding only the page root measures giGroup before it has resized.
                void RebuildGiLayout() =>
                    PanelElementDescriptor.RebuildLayoutChain(giGroup.ContentParent, container);

                SetGiRowsActive(toggleGi.Value);
                dropdownGiMode.OnValueChanged += (_) =>
                {
                    SetGiRowsActive(toggleGi.Value);
                    RebuildGiLayout();
                };
                toggleGiTemporal.OnValueChanged += (_) =>
                {
                    SetGiRowsActive(toggleGi.Value);
                    RebuildGiLayout();
                };
                toggleGiEmitters.OnValueChanged += (_) =>
                {
                    SetGiRowsActive(toggleGi.Value);
                    RebuildGiLayout();
                };
                toggleGiSpecular.OnValueChanged += (_) =>
                {
                    SetGiRowsActive(toggleGi.Value);
                    RebuildGiLayout();
                };
                toggleGi.OnValueChanged += (val) =>
                {
                    SetGiRowsActive(val);
                    RebuildGiLayout();
                };

                if (Application.platform == RuntimePlatform.Android)
                {
                    toggleGi.SetInteractable(false, BasisLocalization.Get("settings.graphics.gi.unsupported"));
                }

                PanelSectionToggleHelpers.FinalizeCollapsibleGroup(giToggle, giGroup, true,
                    _ => descriptor.ForceRebuild());
            }
#endif

#if BASIS_HAS_RTAO && !UNITY_ANDROID
            {
                PanelSectionToggle rtaoToggle = PanelSectionToggle.CreateNewEntry(container);
                PanelElementDescriptor rtaoGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                    rtaoToggle,
                    container,
                    BasisLocalization.Get("settings.graphics.rtao.title"),
                    showGroupTitle: false);

                PanelToggle toggleRtao = PanelToggle.CreateNewEntry(rtaoGroup.ContentParent);
                toggleRtao.AssignBinding(BasisSettingsDefaults.UseRayTracedAmbientOcclusion);
                toggleRtao.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.rtao.enable"));
                toggleRtao.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.rtao.enable.tooltip"));
                SettingsProviderBottleneckHints.Mark(toggleRtao, BasisFrameCostSide.Gpu);

                bool rtaoCanTrace = Basis.Rendering.RTAO.BasisRTAOContext.HardwareSupported;

                PanelDropdown dropdownRtaoMode = PanelDropdown.CreateNewEntry(rtaoGroup.ContentParent);
                dropdownRtaoMode.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.rtao.mode"));
                dropdownRtaoMode.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.rtao.mode.tooltip"));
                dropdownRtaoMode.AssignLocalizedEntries(
                    new List<string> { "Screen Space", "Ray Traced" },
                    new List<string> { "settings.graphics.rtao.mode.screenSpace", "settings.graphics.rtao.mode.rayTraced" });
                dropdownRtaoMode.AssignBinding(BasisSettingsDefaults.RayTracedAmbientOcclusionMode);
                SettingsProviderBottleneckHints.Mark(dropdownRtaoMode, BasisFrameCostSide.Gpu);

                PanelDropdown dropdownRtaoQuality = PanelDropdown.CreateNewEntry(rtaoGroup.ContentParent);
                dropdownRtaoQuality.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.rtao.quality"));
                dropdownRtaoQuality.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.rtao.quality.tooltip"));
                dropdownRtaoQuality.AssignLocalizedEntries(
                    new List<string> { "Low", "Medium", "High", "Ultra" },
                    new List<string> { "settings.graphics.quality.low", "settings.graphics.quality.medium", "settings.graphics.quality.high", "settings.graphics.quality.ultra" });
                dropdownRtaoQuality.AssignBinding(BasisSettingsDefaults.RayTracedAmbientOcclusionQuality);
                SettingsProviderBottleneckHints.Mark(dropdownRtaoQuality, BasisFrameCostSide.Gpu);

                PanelSlider sliderRtaoIntensity = PanelSlider.CreateEntryAndBind(
                    rtaoGroup.ContentParent,
                    new PanelSlider.SliderSettings(BasisLocalization.Get("settings.graphics.rtao.intensity"),
                        "",
                        BasisSettingsDefaults.RTAO_INTENSITY_MIN,
                        BasisSettingsDefaults.RTAO_INTENSITY_MAX,
                        false, 2, ValueDisplayMode.Raw),
                    BasisSettingsDefaults.RayTracedAmbientOcclusionIntensity);
                sliderRtaoIntensity.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.rtao.intensity.tooltip"));

                PanelSlider sliderRtaoRadius = PanelSlider.CreateEntryAndBind(
                    rtaoGroup.ContentParent,
                    new PanelSlider.SliderSettings(BasisLocalization.Get("settings.graphics.rtao.radius"),
                        "",
                        BasisSettingsDefaults.RTAO_RADIUS_MIN,
                        BasisSettingsDefaults.RTAO_RADIUS_MAX,
                        false, 2, ValueDisplayMode.Raw),
                    BasisSettingsDefaults.RayTracedAmbientOcclusionRadius);
                sliderRtaoRadius.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.rtao.radius.tooltip"));
                SettingsProviderBottleneckHints.Mark(sliderRtaoRadius, BasisFrameCostSide.Gpu);


                // Same fold, same reason as global illumination: these were fixed values on the renderer
                // feature, and moving one is how you find out which of them owns an artifact. The ray
                // start offsets matter more here than they look - avatars are traced as capsules rather
                // than as their real mesh, so a ray leaving a visible body surface can start inside that
                // body's capsule and report itself fully occluded.
                PanelSectionToggle rtaoAdvancedToggle = PanelSectionToggle.CreateNewEntry(rtaoGroup.ContentParent);
                PanelElementDescriptor rtaoAdvanced = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                    rtaoAdvancedToggle,
                    rtaoGroup.ContentParent,
                    BasisLocalization.Get("settings.graphics.rtao.advanced"),
                    showGroupTitle: false);
                PanelDropdown dropdownRtaoApply = PanelDropdown.CreateNewEntry(rtaoAdvanced.ContentParent);
                dropdownRtaoApply.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.rtao.apply"));
                dropdownRtaoApply.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.rtao.apply.tooltip"));
                dropdownRtaoApply.AssignLocalizedEntries(
                    new List<string> { "Lighting", "Final Image" },
                    new List<string> { "settings.graphics.rtao.apply.lighting", "settings.graphics.rtao.apply.finalImage" });
                dropdownRtaoApply.AssignBinding(BasisSettingsDefaults.RayTracedAmbientOcclusionApply);

                PanelDropdown dropdownRtaoDenoise = PanelDropdown.CreateNewEntry(rtaoAdvanced.ContentParent);
                dropdownRtaoDenoise.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.rtao.denoise"));
                dropdownRtaoDenoise.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.rtao.denoise.tooltip"));
                dropdownRtaoDenoise.AssignLocalizedEntries(
                    new List<string> { "Off", "Standard", "High", "Maximum" },
                    new List<string> { "ui.option.off", "settings.graphics.rtao.denoise.standard", "settings.graphics.rtao.denoise.high", "settings.graphics.rtao.denoise.maximum" });
                dropdownRtaoDenoise.AssignBinding(BasisSettingsDefaults.RayTracedAmbientOcclusionDenoise);
                SettingsProviderBottleneckHints.Mark(dropdownRtaoDenoise, BasisFrameCostSide.Gpu);

                PanelSlider sliderRtaoDirect = PanelSlider.CreateEntryAndBind(
                    rtaoAdvanced.ContentParent,
                    new PanelSlider.SliderSettings(BasisLocalization.Get("settings.graphics.rtao.directStrength"),
                        "",
                        BasisSettingsDefaults.RTAO_DIRECT_STRENGTH_MIN,
                        BasisSettingsDefaults.RTAO_DIRECT_STRENGTH_MAX,
                        false, 2, ValueDisplayMode.Raw),
                    BasisSettingsDefaults.RayTracedAmbientOcclusionDirectStrength);
                sliderRtaoDirect.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.rtao.directStrength.tooltip"));

                PanelToggle toggleRtaoOtherCameras = PanelToggle.CreateNewEntry(rtaoAdvanced.ContentParent);
                toggleRtaoOtherCameras.AssignBinding(BasisSettingsDefaults.RayTracedAmbientOcclusionOtherCameras);
                toggleRtaoOtherCameras.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.rtao.otherCameras"));
                toggleRtaoOtherCameras.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.rtao.otherCameras.tooltip"));
                SettingsProviderBottleneckHints.Mark(toggleRtaoOtherCameras, BasisFrameCostSide.Gpu);

                PanelDropdown dropdownRtaoLayers = PanelDropdown.CreateNewEntry(rtaoAdvanced.ContentParent);
                dropdownRtaoLayers.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.rtao.layers"));
                dropdownRtaoLayers.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.rtao.layers.tooltip"));
                dropdownRtaoLayers.AssignLocalizedEntries(
                    new List<string> { "Avatars", "World", "World And Avatars" },
                    new List<string> { "settings.graphics.rtao.layers.avatars", "settings.graphics.rtao.layers.world", "settings.graphics.rtao.layers.worldAndAvatars" });
                dropdownRtaoLayers.AssignBinding(BasisSettingsDefaults.RayTracedAmbientOcclusionLayers);
                SettingsProviderBottleneckHints.Mark(dropdownRtaoLayers, BasisFrameCostSide.Gpu);

                PanelDropdown dropdownRtaoSkinned = PanelDropdown.CreateNewEntry(rtaoAdvanced.ContentParent);
                dropdownRtaoSkinned.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.rtao.skinned"));
                dropdownRtaoSkinned.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.rtao.skinned.tooltip"));
                dropdownRtaoSkinned.AssignLocalizedEntries(
                    new List<string> { "Off", "Proxy" },
                    new List<string> { "settings.graphics.rtao.skinned.off", "settings.graphics.rtao.skinned.proxy" });
                dropdownRtaoSkinned.AssignBinding(BasisSettingsDefaults.RayTracedAmbientOcclusionSkinnedMeshes);
                SettingsProviderBottleneckHints.Mark(dropdownRtaoSkinned, BasisFrameCostSide.Cpu);

                PanelSlider sliderRtaoNormalBias = PanelSlider.CreateEntryAndBind(
                    rtaoAdvanced.ContentParent,
                    new PanelSlider.SliderSettings(BasisLocalization.Get("settings.graphics.rtao.normalBias"),
                        "",
                        BasisSettingsDefaults.RTAO_NORMAL_BIAS_MIN,
                        BasisSettingsDefaults.RTAO_NORMAL_BIAS_MAX,
                        false, 3, ValueDisplayMode.Raw),
                    BasisSettingsDefaults.RayTracedAmbientOcclusionNormalBias);
                sliderRtaoNormalBias.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.rtao.normalBias.tooltip"));

                PanelSlider sliderRtaoDistanceBias = PanelSlider.CreateEntryAndBind(
                    rtaoAdvanced.ContentParent,
                    new PanelSlider.SliderSettings(BasisLocalization.Get("settings.graphics.rtao.distanceBias"),
                        "",
                        BasisSettingsDefaults.RTAO_DISTANCE_BIAS_MIN,
                        BasisSettingsDefaults.RTAO_DISTANCE_BIAS_MAX,
                        false, 4, ValueDisplayMode.Raw),
                    BasisSettingsDefaults.RayTracedAmbientOcclusionDistanceBias);
                sliderRtaoDistanceBias.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.rtao.distanceBias.tooltip"));

                PanelSlider sliderRtaoFalloff = PanelSlider.CreateEntryAndBind(
                    rtaoAdvanced.ContentParent,
                    new PanelSlider.SliderSettings(BasisLocalization.Get("settings.graphics.rtao.falloff"),
                        "",
                        BasisSettingsDefaults.RTAO_FALLOFF_MIN,
                        BasisSettingsDefaults.RTAO_FALLOFF_MAX,
                        false, 2, ValueDisplayMode.Raw),
                    BasisSettingsDefaults.RayTracedAmbientOcclusionFalloff);
                sliderRtaoFalloff.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.rtao.falloff.tooltip"));

                PanelSlider sliderRtaoPower = PanelSlider.CreateEntryAndBind(
                    rtaoAdvanced.ContentParent,
                    new PanelSlider.SliderSettings(BasisLocalization.Get("settings.graphics.rtao.power"),
                        "",
                        BasisSettingsDefaults.RTAO_POWER_MIN,
                        BasisSettingsDefaults.RTAO_POWER_MAX,
                        false, 2, ValueDisplayMode.Raw),
                    BasisSettingsDefaults.RayTracedAmbientOcclusionPower);
                sliderRtaoPower.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.rtao.power.tooltip"));

                PanelSlider sliderRtaoFadeStart = PanelSlider.CreateEntryAndBind(
                    rtaoAdvanced.ContentParent,
                    new PanelSlider.SliderSettings(BasisLocalization.Get("settings.graphics.rtao.fadeStart"),
                        "",
                        BasisSettingsDefaults.RTAO_FADE_MIN,
                        BasisSettingsDefaults.RTAO_FADE_MAX,
                        false, 0, ValueDisplayMode.Raw),
                    BasisSettingsDefaults.RayTracedAmbientOcclusionFadeStart);
                sliderRtaoFadeStart.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.rtao.fadeStart.tooltip"));

                PanelSlider sliderRtaoFadeEnd = PanelSlider.CreateEntryAndBind(
                    rtaoAdvanced.ContentParent,
                    new PanelSlider.SliderSettings(BasisLocalization.Get("settings.graphics.rtao.fadeEnd"),
                        "",
                        BasisSettingsDefaults.RTAO_FADE_MIN,
                        BasisSettingsDefaults.RTAO_FADE_MAX,
                        false, 0, ValueDisplayMode.Raw),
                    BasisSettingsDefaults.RayTracedAmbientOcclusionFadeEnd);
                sliderRtaoFadeEnd.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.rtao.fadeEnd.tooltip"));

                PanelSlider sliderRtaoSpecularRelief = PanelSlider.CreateEntryAndBind(
                    rtaoAdvanced.ContentParent,
                    new PanelSlider.SliderSettings(BasisLocalization.Get("settings.graphics.rtao.specularRelief"),
                        "",
                        BasisSettingsDefaults.RTAO_SPECULAR_RELIEF_MIN,
                        BasisSettingsDefaults.RTAO_SPECULAR_RELIEF_MAX,
                        false, 2, ValueDisplayMode.Raw),
                    BasisSettingsDefaults.RayTracedAmbientOcclusionSpecularRelief);
                sliderRtaoSpecularRelief.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.rtao.specularRelief.tooltip"));

                PanelSectionToggleHelpers.FinalizeCollapsibleGroup(rtaoAdvancedToggle, rtaoAdvanced, false,
                    _ => RebuildRtaoLayout());

                void SetRtaoRowsActive(bool val)
                {
                    bool tracesGeometry = val && rtaoCanTrace && Basis.Rendering.RTAO.BasisRTAOTracing.IsRayTraced(
                        Basis.Rendering.RTAO.BasisRTAOTracing.Resolve(Basis.Rendering.RTAO.BasisRTAOSettingsMap.ReadMode(dropdownRtaoMode.Value)));
                    dropdownRtaoMode.Descriptor.SetActive(val && rtaoCanTrace);
                    dropdownRtaoQuality.Descriptor.SetActive(val);
                    sliderRtaoIntensity.Descriptor.SetActive(val);
                    sliderRtaoRadius.Descriptor.SetActive(val);
                    dropdownRtaoApply.Descriptor.SetActive(val);
                    dropdownRtaoDenoise.Descriptor.SetActive(val);
                    // Occlusion On Direct Light only means anything on the lighting path; the final image
                    // multiply dims everything by definition.
                    bool throughLighting = val && dropdownRtaoApply.Value != "Final Image";
                    sliderRtaoDirect.Descriptor.SetActive(throughLighting);
                    toggleRtaoOtherCameras.Descriptor.SetActive(val);
                    // Both describe the acceleration structure, which only the traced path builds. On the
                    // screen space estimator there is no structure for either to fill or filter, and on a
                    // GPU that cannot trace at all the mode row is hidden too, so tracesGeometry - not the
                    // dropdown alone - is what decides these.
                    dropdownRtaoLayers.Descriptor.SetActive(tracesGeometry);
                    dropdownRtaoSkinned.Descriptor.SetActive(tracesGeometry);

                    // The fold goes with the effect, so switching occlusion off does not leave a section
                    // header standing over nothing.
                    rtaoAdvancedToggle.Descriptor.SetActive(val);

                    // Falloff, contrast, the distance fade and the gloss relief shape the result whichever
                    // way it was gathered, so they stay for the screen space estimator too.
                    sliderRtaoFalloff.Descriptor.SetActive(val);
                    sliderRtaoPower.Descriptor.SetActive(val);
                    sliderRtaoFadeStart.Descriptor.SetActive(val);
                    sliderRtaoFadeEnd.Descriptor.SetActive(val);
                    sliderRtaoSpecularRelief.Descriptor.SetActive(val);

                    // The ray start offsets only exist where there are rays. The screen space estimator
                    // marches the depth buffer and has no origin to push off a surface.
                    sliderRtaoNormalBias.Descriptor.SetActive(tracesGeometry);
                    sliderRtaoDistanceBias.Descriptor.SetActive(tracesGeometry);
                }

                // rtaoAdvanced nests inside rtaoGroup, so a row toggled inside the fold needs rtaoGroup
                // rebuilt too - rebuilding only the page root measures rtaoGroup before it has resized.
                void RebuildRtaoLayout() =>
                    PanelElementDescriptor.RebuildLayoutChain(rtaoGroup.ContentParent, container);

                SetRtaoRowsActive(toggleRtao.Value);
                dropdownRtaoApply.OnValueChanged += (_) =>
                {
                    SetRtaoRowsActive(toggleRtao.Value);
                    RebuildRtaoLayout();
                };
                dropdownRtaoMode.OnValueChanged += (_) =>
                {
                    SetRtaoRowsActive(toggleRtao.Value);
                    RebuildRtaoLayout();
                };
                toggleRtao.OnValueChanged += (val) =>
                {
                    SetRtaoRowsActive(val);
                    RebuildRtaoLayout();
                };

                PanelSectionToggleHelpers.FinalizeCollapsibleGroup(rtaoToggle, rtaoGroup, true,
                    _ => descriptor.ForceRebuild());
            }
#endif

            // --- Overrides (mirror / bloom / fog / camera clip) ---
            // Dedicated content-parent (not the flat/sibling-sweep style) so this outer toggle only
            // ever shows/hides ONE container. The flat style re-registers and force-syncs every direct
            // child's active state on every outer toggle, which stomps each override's own independent
            // expand/collapse state now that they are nested PanelSectionToggles rather than plain rows.
            PanelSectionToggle overridesToggle = PanelSectionToggle.CreateNewEntry(container);
            PanelElementDescriptor overridesGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                overridesToggle,
                container,
                BasisLocalization.Get("settings.graphics.overrides.title"),
                showGroupTitle: false);
            RectTransform overridesContent = overridesGroup.ContentParent;

            // Each override nests its own content group inside overridesGroup.ContentParent, so a
            // row toggled inside one needs that chain rebuilt bottom-up too - rebuilding only the
            // page root measures the inner group before it has resized (same fix as RebuildGiLayout
            // / RebuildRtaoLayout above).
            void RebuildOverridesLayout() =>
                PanelElementDescriptor.RebuildLayoutChain(overridesGroup.ContentParent, container);

            // --- Mirror Quality Override ---
            PanelSectionToggle mirrorToggle = PanelSectionToggle.CreateNewEntry(overridesContent);
            mirrorToggle.SetTitle(BasisLocalization.Get("settings.graphics.mirrorQuality.title"));
            PanelElementDescriptor mirrorGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, overridesContent);

            PanelToggle toggleMirrorOverride = PanelToggle.CreateNewEntry(mirrorGroup.ContentParent);
            toggleMirrorOverride.AssignBinding(BasisSettingsDefaults.UseMirrorQualityOverride);
            toggleMirrorOverride.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.mirrorQuality.override"));
            toggleMirrorOverride.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.mirrorQuality.override.tooltip"));

            PanelDropdown dropdownMirrorQuality = PanelDropdown.CreateNewEntry(mirrorGroup.ContentParent);
            dropdownMirrorQuality.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.mirrorResolution"));
            dropdownMirrorQuality.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.mirrorResolution.tooltip"));
            dropdownMirrorQuality.AssignEntries(new List<string> { "256", "512", "1024", "2048", "4096", "8192" }, null, new List<string>
            {
                BasisLocalization.Get("settings.graphics.mirrorResolution.256.tooltip"),
                BasisLocalization.Get("settings.graphics.mirrorResolution.512.tooltip"),
                BasisLocalization.Get("settings.graphics.mirrorResolution.1024.tooltip"),
                BasisLocalization.Get("settings.graphics.mirrorResolution.2048.tooltip"),
                BasisLocalization.Get("settings.graphics.mirrorResolution.4096.tooltip"),
                BasisLocalization.Get("settings.graphics.mirrorResolution.8192.tooltip")
            });
            dropdownMirrorQuality.AssignBinding(BasisSettingsDefaults.MirrorQuality);

            dropdownMirrorQuality.Descriptor.SetActive(toggleMirrorOverride.Value);
            toggleMirrorOverride.OnValueChanged += (val) =>
            {
                dropdownMirrorQuality.Descriptor.SetActive(val);
                RebuildOverridesLayout();
            };
            mirrorToggle.RegisterContentContainer(mirrorGroup);
            PanelSectionToggleHelpers.FinalizeCollapsibleGroup(mirrorToggle, mirrorGroup, true,
                _ => RebuildOverridesLayout());

            // --- Accessibility: Bloom Override ---
            PanelSectionToggle bloomToggle = PanelSectionToggle.CreateNewEntry(overridesContent);
            bloomToggle.SetTitle(BasisLocalization.Get("settings.graphics.bloom.title"));
            PanelElementDescriptor bloomGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, overridesContent);

            PanelToggle toggleBloomOverride = PanelToggle.CreateNewEntry(bloomGroup.ContentParent);
            toggleBloomOverride.AssignBinding(BasisSettingsDefaults.UseBloomOverride);
            toggleBloomOverride.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.bloom.override"));
            toggleBloomOverride.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.bloom.override.tooltip"));

            PanelSlider sliderBloomIntensity = PanelSlider.CreateEntryAndBind(
                bloomGroup.ContentParent,
                new PanelSlider.SliderSettings(BasisLocalization.Get("settings.graphics.bloom.intensity"),
                    "",
                    BasisSettingsDefaults.BLOOM_INTENSITY_MIN,
                    BasisSettingsDefaults.BLOOM_INTENSITY_MAX,
                    false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.BloomIntensity);
            sliderBloomIntensity.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.bloom.intensity.tooltip"));

            sliderBloomIntensity.Descriptor.SetActive(toggleBloomOverride.Value);
            toggleBloomOverride.OnValueChanged += (val) =>
            {
                sliderBloomIntensity.Descriptor.SetActive(val);
                RebuildOverridesLayout();
            };
            bloomToggle.RegisterContentContainer(bloomGroup);
            PanelSectionToggleHelpers.FinalizeCollapsibleGroup(bloomToggle, bloomGroup, true,
                _ => RebuildOverridesLayout());

            // --- Accessibility: Volumetric Fog Override ---
            PanelSectionToggle fogToggle = PanelSectionToggle.CreateNewEntry(overridesContent);
            fogToggle.SetTitle(BasisLocalization.Get("settings.graphics.fog.title"));
            PanelElementDescriptor fogGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, overridesContent);

            PanelToggle toggleFogOverride = PanelToggle.CreateNewEntry(fogGroup.ContentParent);
            toggleFogOverride.AssignBinding(BasisSettingsDefaults.UseVolumetricFogOverride);
            toggleFogOverride.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.fog.override"));
            toggleFogOverride.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.fog.override.tooltip"));

            PanelSlider sliderFogDensity = PanelSlider.CreateEntryAndBind(
                fogGroup.ContentParent,
                new PanelSlider.SliderSettings(BasisLocalization.Get("settings.graphics.fog.density"),
                    "",
                    BasisSettingsDefaults.FOG_DENSITY_MIN,
                    BasisSettingsDefaults.FOG_DENSITY_MAX,
                    false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.VolumetricFogDensity);
            sliderFogDensity.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.fog.density.tooltip"));

            sliderFogDensity.Descriptor.SetActive(toggleFogOverride.Value);
            toggleFogOverride.OnValueChanged += (val) =>
            {
                sliderFogDensity.Descriptor.SetActive(val);
                RebuildOverridesLayout();
            };

            PanelToggle toggleFogBakedAPV = PanelToggle.CreateNewEntry(fogGroup.ContentParent);
            toggleFogBakedAPV.AssignBinding(BasisSettingsDefaults.VolumetricFogBakedAPV);
            toggleFogBakedAPV.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.fog.bakedapv"));
            toggleFogBakedAPV.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.fog.bakedapv.tooltip"));

            fogToggle.RegisterContentContainer(fogGroup);
            PanelSectionToggleHelpers.FinalizeCollapsibleGroup(fogToggle, fogGroup, true,
                _ => RebuildOverridesLayout());

            // --- Accessibility: Motion Blur Override ---
            PanelSectionToggle motionBlurToggle = PanelSectionToggle.CreateNewEntry(overridesContent);
            motionBlurToggle.SetTitle(BasisLocalization.Get("settings.graphics.motionBlur.title"));
            PanelElementDescriptor motionBlurGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, overridesContent);

            PanelToggle toggleMotionBlurOverride = PanelToggle.CreateNewEntry(motionBlurGroup.ContentParent);
            toggleMotionBlurOverride.AssignBinding(BasisSettingsDefaults.UseMotionBlurOverride);
            toggleMotionBlurOverride.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.motionBlur.override"));
            toggleMotionBlurOverride.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.motionBlur.override.tooltip"));

            PanelSlider sliderMotionBlurIntensity = PanelSlider.CreateEntryAndBind(
                motionBlurGroup.ContentParent,
                new PanelSlider.SliderSettings(BasisLocalization.Get("settings.graphics.motionBlur.intensity"),
                    "",
                    BasisSettingsDefaults.MOTION_BLUR_INTENSITY_MIN,
                    BasisSettingsDefaults.MOTION_BLUR_INTENSITY_MAX,
                    false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.MotionBlurIntensity);
            sliderMotionBlurIntensity.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.motionBlur.intensity.tooltip"));

            PanelSlider sliderMotionBlurClamp = PanelSlider.CreateEntryAndBind(
                motionBlurGroup.ContentParent,
                new PanelSlider.SliderSettings(BasisLocalization.Get("settings.graphics.motionBlur.clamp"),
                    "",
                    BasisSettingsDefaults.MOTION_BLUR_CLAMP_MIN,
                    BasisSettingsDefaults.MOTION_BLUR_CLAMP_MAX,
                    false, 3, ValueDisplayMode.Raw),
                BasisSettingsDefaults.MotionBlurClamp);
            sliderMotionBlurClamp.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.motionBlur.clamp.tooltip"));

            PanelDropdown dropdownMotionBlurQuality = PanelDropdown.CreateNewEntry(motionBlurGroup.ContentParent);
            dropdownMotionBlurQuality.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.motionBlur.quality"));
            dropdownMotionBlurQuality.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.motionBlur.quality.tooltip"));
            dropdownMotionBlurQuality.AssignLocalizedEntries(
                new List<string> { "Low", "Medium", "High" },
                new List<string> { "settings.graphics.quality.low", "settings.graphics.quality.medium", "settings.graphics.quality.high" });
            dropdownMotionBlurQuality.AssignBinding(BasisSettingsDefaults.MotionBlurQuality);

            PanelDropdown dropdownMotionBlurMode = PanelDropdown.CreateNewEntry(motionBlurGroup.ContentParent);
            dropdownMotionBlurMode.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.motionBlur.mode"));
            dropdownMotionBlurMode.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.motionBlur.mode.tooltip"));
            dropdownMotionBlurMode.AssignLocalizedEntries(
                new List<string> { "Camera Only", "Camera And Objects" },
                new List<string> { "settings.graphics.motionBlur.mode.cameraOnly", "settings.graphics.motionBlur.mode.cameraAndObjects" });
            dropdownMotionBlurMode.AssignBinding(BasisSettingsDefaults.MotionBlurMode);

            sliderMotionBlurIntensity.Descriptor.SetActive(toggleMotionBlurOverride.Value);
            sliderMotionBlurClamp.Descriptor.SetActive(toggleMotionBlurOverride.Value);
            dropdownMotionBlurQuality.Descriptor.SetActive(toggleMotionBlurOverride.Value);
            dropdownMotionBlurMode.Descriptor.SetActive(toggleMotionBlurOverride.Value);
            toggleMotionBlurOverride.OnValueChanged += (val) =>
            {
                sliderMotionBlurIntensity.Descriptor.SetActive(val);
                sliderMotionBlurClamp.Descriptor.SetActive(val);
                dropdownMotionBlurQuality.Descriptor.SetActive(val);
                dropdownMotionBlurMode.Descriptor.SetActive(val);
                RebuildOverridesLayout();
            };
            motionBlurToggle.RegisterContentContainer(motionBlurGroup);
            PanelSectionToggleHelpers.FinalizeCollapsibleGroup(motionBlurToggle, motionBlurGroup, true,
                _ => RebuildOverridesLayout());

            // --- Camera Near/Far Override ---
            PanelSectionToggle cameraClipToggle = PanelSectionToggle.CreateNewEntry(overridesContent);
            cameraClipToggle.SetTitle(BasisLocalization.Get("settings.graphics.cameraClip.title"));
            PanelElementDescriptor cameraClipGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, overridesContent);

            PanelToggle toggleCameraClipOverride = PanelToggle.CreateNewEntry(cameraClipGroup.ContentParent);
            toggleCameraClipOverride.AssignBinding(BasisSettingsDefaults.UseCameraClipOverride);
            toggleCameraClipOverride.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.cameraClip.override"));
            toggleCameraClipOverride.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.cameraClip.override.tooltip"));

            PanelSlider sliderCameraNear = PanelSlider.CreateEntryAndBind(
                cameraClipGroup,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.graphics.cameraClip.near"), 0.001f, 0.1f, false, 3, ValueDisplayMode.Meters),
                BasisSettingsDefaults.CameraClipNear);
            sliderCameraNear.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.cameraClip.near.tooltip"));

            PanelSlider sliderCameraFar = PanelSlider.CreateEntryAndBind(
                cameraClipGroup,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.graphics.cameraClip.far"), 10f, 5000f, true, 0, ValueDisplayMode.Meters),
                BasisSettingsDefaults.CameraClipFar);
            sliderCameraFar.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.cameraClip.far.tooltip"));

            sliderCameraNear.Descriptor.SetActive(toggleCameraClipOverride.Value);
            sliderCameraFar.Descriptor.SetActive(toggleCameraClipOverride.Value);
            toggleCameraClipOverride.OnValueChanged += (val) =>
            {
                sliderCameraNear.Descriptor.SetActive(val);
                sliderCameraFar.Descriptor.SetActive(val);
                RebuildOverridesLayout();
            };
            cameraClipToggle.RegisterContentContainer(cameraClipGroup);
            PanelSectionToggleHelpers.FinalizeCollapsibleGroup(cameraClipToggle, cameraClipGroup, true,
                _ => RebuildOverridesLayout());

            PanelSectionToggleHelpers.FinalizeCollapsibleGroup(overridesToggle, overridesGroup, false,
                _ => descriptor.ForceRebuild());

            // --- Variable Rate Shading (VR, gaze foveated) ---
            // Direct3D12 only; on every other graphics API the section is not built at all.
            PanelToggle toggleVrsVr = null;
            PanelSlider sliderVrsInner = null;
            PanelSlider sliderVrsOuter = null;
            if (BasisVariableRateShadingFeature.IsSupported)
            {
                PanelSectionToggleHelpers.CreateCollapsibleBoxedSection(container,
                    BasisLocalization.Get("settings.graphics.vrs.title"), () =>
                {
                    toggleVrsVr = PanelToggle.CreateNewEntry(container);
                    toggleVrsVr.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.vrs"));
                    toggleVrsVr.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.vrs.tooltip"));
                    toggleVrsVr.AssignBinding(BasisSettingsDefaults.DevVariableRateShading);

                    sliderVrsInner = PanelSlider.CreateEntryAndBind(
                        container,
                        new PanelSlider.SliderSettings(BasisLocalization.Get("settings.graphics.vrs.inner"),
                            "",
                            BasisSettingsDefaults.VRS_RADIUS_MIN, BasisSettingsDefaults.VRS_RADIUS_MAX, false, 0, ValueDisplayMode.Percentage),
                        BasisSettingsDefaults.VrsFovealInnerRadius);
                    sliderVrsInner.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.vrs.inner.tooltip"));

                    sliderVrsOuter = PanelSlider.CreateEntryAndBind(
                        container,
                        new PanelSlider.SliderSettings(BasisLocalization.Get("settings.graphics.vrs.outer"),
                            "",
                            BasisSettingsDefaults.VRS_RADIUS_MIN, BasisSettingsDefaults.VRS_RADIUS_MAX, false, 0, ValueDisplayMode.Percentage),
                        BasisSettingsDefaults.VrsFovealOuterRadius);
                    sliderVrsOuter.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.vrs.outer.tooltip"));

                    sliderVrsInner.Descriptor.SetActive(toggleVrsVr.Value);
                    sliderVrsOuter.Descriptor.SetActive(toggleVrsVr.Value);
                    toggleVrsVr.OnValueChanged += (val) =>
                    {
                        sliderVrsInner.Descriptor.SetActive(val);
                        sliderVrsOuter.Descriptor.SetActive(val);
                        descriptor.ForceRebuild();
                    };
                }, false, visible =>
                {
                    if (visible && toggleVrsVr != null)
                    {
                        sliderVrsInner.Descriptor.SetActive(toggleVrsVr.Value);
                        sliderVrsOuter.Descriptor.SetActive(toggleVrsVr.Value);
                    }
                    descriptor.ForceRebuild();
                });
            }

            PanelSectionToggle toggleAdvanced = PanelSectionToggle.CreateNewEntry(container);
            toggleAdvanced.SetTitle(BasisLocalization.Get("settings.graphics.advanced.showAdvanced"));
            int advancedStart = container.childCount;

            PanelSlider sliderRenderResolution = PanelSlider.CreateEntryAndBind(
                container,
                new PanelSlider.SliderSettings(BasisLocalization.Get("settings.graphics.renderScale"), "", 0.5f, 1.5f, false, 3, ValueDisplayMode.percentageFromZero),
                BasisSettingsDefaults.RenderResolution);
            sliderRenderResolution.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.renderScale.tooltip"));

            // PanelToggle toggleDynamicResolution = PanelToggle.CreateNewEntry(container);
            // toggleDynamicResolution.AssignBinding(BasisSettingsDefaults.DynamicResolutionEnabled);
            // toggleDynamicResolution.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.dynamicResolution"));
            // toggleDynamicResolution.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.dynamicResolution.tooltip"));

            // PanelSlider sliderDynamicMinimum = PanelSlider.CreateEntryAndBind(
            //     container,
            //     new PanelSlider.SliderSettings(BasisLocalization.Get("settings.graphics.dynamicResolution.minimum"),
            //         "",
            //         0.25f, 1f, false, 3, ValueDisplayMode.Percentage),
            //     BasisSettingsDefaults.DynamicResolutionMinimumScale);
            // sliderDynamicMinimum.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.dynamicResolution.minimum.tooltip"));

            // PanelSlider sliderDynamicMaximum = PanelSlider.CreateEntryAndBind(
            //     container,
            //     new PanelSlider.SliderSettings(BasisLocalization.Get("settings.graphics.dynamicResolution.maximum"),
            //         "",
            //         0.5f, 1.5f, false, 3, ValueDisplayMode.Percentage),
            //     BasisSettingsDefaults.DynamicResolutionMaximumScale);
            // sliderDynamicMaximum.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.dynamicResolution.maximum.tooltip"));

            // PanelToggle toggleDynamicTargetOverride = PanelToggle.CreateNewEntry(container);
            // toggleDynamicTargetOverride.AssignBinding(BasisSettingsDefaults.DynamicResolutionTargetOverride);
            // toggleDynamicTargetOverride.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.dynamicResolution.targetOverride"));
            // toggleDynamicTargetOverride.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.dynamicResolution.targetOverride.tooltip"));

            // PanelSlider sliderDynamicTarget = PanelSlider.CreateEntryAndBind(
            //     container,
            //     new PanelSlider.SliderSettings(BasisLocalization.Get("settings.graphics.dynamicResolution.target"),
            //         "",
            //         30, 240, true, 0, ValueDisplayMode.Hz),
            //     BasisSettingsDefaults.DynamicResolutionTargetFrameRate);
            // sliderDynamicTarget.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.dynamicResolution.target.tooltip"));

            PanelDropdown dropdownHDR = PanelDropdown.CreateNewEntry(container);
            dropdownHDR.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.hdrSupport"));
            dropdownHDR.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.hdrSupport.tooltip"));
            dropdownHDR.AssignLocalizedEntries(
                new List<string> { "Off", "32bit", "64bit" },
                new List<string> { "ui.option.off", "settings.graphics.hdr.32bit", "settings.graphics.hdr.64bit" },
                new List<string> { "settings.graphics.hdr.off.tooltip" });
            dropdownHDR.AssignBinding(BasisSettingsDefaults.HDRSupport);

            PanelSlider sliderFoveatedRendering = PanelSlider.CreateEntryAndBind(
                container,
                new PanelSlider.SliderSettings(BasisLocalization.Get("settings.graphics.foveated"),
                    "",
                    0, 1, false, 1, ValueDisplayMode.Percentage),
                BasisSettingsDefaults.FoveatedRendering);
            sliderFoveatedRendering.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.foveated.tooltip"));

            PanelSlider sliderFieldOfView = PanelSlider.CreateEntryAndBind(
                container,
                new PanelSlider.SliderSettings(BasisLocalization.Get("settings.graphics.fov"),
                    "",
                    BasisSettingsDefaults.FOV_MIN, BasisSettingsDefaults.FOV_MAX, true, 0, ValueDisplayMode.Degrees),
                BasisSettingsDefaults.FieldOfView);
            sliderFieldOfView.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.fov.tooltip"));

            PanelSlider sliderMeshLOD = PanelSlider.CreateEntryAndBind(
                container,
                new PanelSlider.SliderSettings(BasisLocalization.Get("settings.graphics.avatarLod"),
                    "",
                    0, 1, false, 3, ValueDisplayMode.Percentage),
                BasisSettingsDefaults.AvatarMeshLOD);
            sliderMeshLOD.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.avatarLod.tooltip"));

            PanelToggle toggleAvatarSkinLod = PanelToggle.CreateNewEntry(container);
            toggleAvatarSkinLod.AssignBinding(BasisSettingsDefaults.UseAvatarSkinLod);
            toggleAvatarSkinLod.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.avatarSkinLod"));
            toggleAvatarSkinLod.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.avatarSkinLod.tooltip"));

            PanelToggle toggleAvatarShadowLod = PanelToggle.CreateNewEntry(container);
            toggleAvatarShadowLod.AssignBinding(BasisSettingsDefaults.UseAvatarShadowLod);
            toggleAvatarShadowLod.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.avatarShadowLod"));
            toggleAvatarShadowLod.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.avatarShadowLod.tooltip"));

            PanelSlider sliderPoseLod = PanelSlider.CreateEntryAndBind(
                container,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.graphics.poseLod.bias"), 0, 5, true, 0, ValueDisplayMode.Raw),
                BasisSettingsDefaults.PoseLOD);
            sliderPoseLod.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.poseLod.bias.tooltip"));

            PanelToggle toggleAvatarVisibilityCull = PanelToggle.CreateNewEntry(container);
            toggleAvatarVisibilityCull.AssignBinding(BasisSettingsDefaults.UseAvatarVisibilityCull);
            toggleAvatarVisibilityCull.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.avatarVisibilityCull"));
            toggleAvatarVisibilityCull.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.avatarVisibilityCull.tooltip"));

            // Read once, when the GPU Resident Drawer is built at startup — the toggle saves now and
            // lands on the next launch. Not built at all where the drawer is off (Android, headless).
            if (BasisGpuOcclusionCulling.IsSupported)
            {
                PanelToggle toggleGpuOcclusionCulling = PanelToggle.CreateNewEntry(container);
                toggleGpuOcclusionCulling.AssignBinding(BasisSettingsDefaults.UseGpuOcclusionCulling);
                toggleGpuOcclusionCulling.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.gpuOcclusionCulling.tooltip"));

                void SyncGpuOcclusionRestartNotice(bool _)
                {
                    toggleGpuOcclusionCulling.Descriptor.SetTitle(BasisGpuOcclusionCulling.NeedsRestart
                        ? BasisLocalization.Get("settings.graphics.gpuOcclusionCulling.restart")
                        : BasisLocalization.Get("settings.graphics.gpuOcclusionCulling"));
                }

                SyncGpuOcclusionRestartNotice(false);
                BasisSettingsDefaults.UseGpuOcclusionCulling.OnChanged += SyncGpuOcclusionRestartNotice;
                toggleGpuOcclusionCulling.OnInstanceReleased += () =>
                    BasisSettingsDefaults.UseGpuOcclusionCulling.OnChanged -= SyncGpuOcclusionRestartNotice;

                // Offered on the control's own change only, so resetting the whole graphics tab
                // doesn't throw a relaunch prompt. Flipping back to the booted value clears
                // NeedsRestart and asks nothing.
                toggleGpuOcclusionCulling.OnValueChanged += _ =>
                {
                    if (!BasisGpuOcclusionCulling.NeedsRestart || !BasisAppRelaunch.IsSupported)
                    {
                        return;
                    }

                    if (BasisMainMenu.Instance == null)
                    {
                        return;
                    }

                    if (BasisMainMenu.Instance.Dialogue)
                    {
                        BasisMainMenu.Instance.Dialogue.ReleaseInstance();
                    }

                    BasisMainMenu.Instance.OpenDialogue(
                        BasisLocalization.Get("settings.graphics.gpuOcclusionCulling.restart.title"),
                        BasisLocalization.Get("settings.graphics.gpuOcclusionCulling.restart.prompt"),
                        BasisLocalization.Get("settings.graphics.gpuOcclusionCulling.restart.now"),
                        BasisLocalization.Get("settings.graphics.gpuOcclusionCulling.restart.later"),
                        accepted =>
                        {
                            if (accepted) BasisAppRelaunch.RebootAndReconnect();
                        });
                };
            }

            PanelSlider sliderGlobalMeshLOD = PanelSlider.CreateEntryAndBind(
                container,
                new PanelSlider.SliderSettings(BasisLocalization.Get("settings.graphics.worldLod"),
                    "",
                    0, 100, true, 0, ValueDisplayMode.Percentage),
                BasisSettingsDefaults.GlobalMeshLOD);
            sliderGlobalMeshLOD.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.worldLod.tooltip"));

            PanelToggle toggleLocalHeadBlendShapes = PanelToggle.CreateNewEntry(container);
            toggleLocalHeadBlendShapes.AssignBinding(BasisSettingsDefaults.LocalHeadBlendShapes);
            toggleLocalHeadBlendShapes.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.localHeadBlendShapes"));
            toggleLocalHeadBlendShapes.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.localHeadBlendShapes.tooltip"));

            void ApplyAdvancedPlatformVisibility()
            {
                // bool dynamicEnabled = toggleDynamicResolution.Value;
                // sliderDynamicMinimum.Descriptor.SetActive(dynamicEnabled);
                // sliderDynamicMaximum.Descriptor.SetActive(dynamicEnabled);
                // toggleDynamicTargetOverride.Descriptor.SetActive(dynamicEnabled);
                // sliderDynamicTarget.Descriptor.SetActive(dynamicEnabled && toggleDynamicTargetOverride.Value);
#if !UNITY_ANDROID
                sliderFoveatedRendering.Descriptor.SetActive(false);
#endif
            }

            // toggleDynamicResolution.OnValueChanged += (val) =>
            // {
            //     ApplyAdvancedPlatformVisibility();
            //     descriptor.ForceRebuild();
            // };
            // toggleDynamicTargetOverride.OnValueChanged += (val) =>
            // {
            //     ApplyAdvancedPlatformVisibility();
            //     descriptor.ForceRebuild();
            // };

            ApplyAdvancedPlatformVisibility();

            PanelSectionToggleHelpers.FinalizeBoxedSectionFromIndex(toggleAdvanced, container, advancedStart, false, visible =>
            {
                // Section expand re-shows every row; re-apply the platform gates.
                if (visible)
                {
                    ApplyAdvancedPlatformVisibility();
                }
                descriptor.ForceRebuild();
            });

            // Nameplates live in the same tab — one plate per remote player is a crowd cost like
            // the rest of this page, and Performance Mode drives their visibility.
            SettingsProviderNamePlate.BuildNamePlateContent(container, descriptor);

            // Performance limits live in the same tab — formerly its own page,
            // merged here so users see all rendering / quality / cost controls together.
            SettingsProviderPerformanceLimits.BuildPerformanceLimitsContent(container, true);

            // Every control this page carries now exists, so the bottleneck readout can find the
            // ones that move CPU or GPU cost and light them up while it is blaming that side.
            SettingsProviderBottleneckHints.Bind(descriptor);
            descriptor.OnInstanceReleased += () => SettingsProviderBottleneckHints.Release(descriptor);

            // One reset button for this whole page
            RegisterPageReset("settings.tab.graphics", ResetGraphicsDefaults);

            descriptor.ForceRebuild();
            return tab;
        }

        /// <summary>
        /// Performance Mode block at the head of the Graphics page: the level itself, whether it
        /// follows the instance population on its own, and whether a busy instance may offer it.
        /// The section is tinted by the active level so the page shows at a glance how hard the
        /// mode is cutting. Changing the level rewrites the settings the rest of this page shows,
        /// so the page is reopened afterwards the same way the reset button does it.
        /// </summary>
        private static void BuildPerformanceModeSection(RectTransform container)
        {
            BasisPerformanceMode.EnsureInitialized();

            PanelElementDescriptor group =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            group.SetTitle(BasisLocalization.Get("settings.performanceMode.title"));
            group.SetDescription(BasisLocalization.Get("settings.performanceMode.description"));

            PanelDropdown dropdownLevel = PanelDropdown.CreateNewEntry(group.ContentParent);
            dropdownLevel.Descriptor.SetTitle(BasisLocalization.Get("settings.performanceMode.level"));
            dropdownLevel.Descriptor.SetTooltip(BasisLocalization.Get("settings.performanceMode.level.tooltip"));

            // Each entry carries the occupant count that arms it, so the list reads as
            // "Light (250+)" — the tier the crowd prompt offers it at and the count
            // Follow Player Count switches to it on.
            List<string> levelIds = new List<string>
            {
                BasisPerformanceMode.LevelOff,
                BasisPerformanceMode.LevelLight,
                BasisPerformanceMode.LevelBalanced,
                BasisPerformanceMode.LevelAggressive
            };
            List<string> levelLabels = new List<string>(levelIds.Count);
            for (int Index = 0; Index < levelIds.Count; Index++)
            {
                levelLabels.Add(BasisPerformanceMode.DisplayNameWithThreshold(
                    BasisPerformanceMode.IdToLevel(levelIds[Index])));
            }
            dropdownLevel.AssignEntries(levelIds, levelLabels, new List<string>
            {
                BasisLocalization.Get("settings.performanceMode.level.off.tooltip"),
                BasisLocalization.Get("settings.performanceMode.level.light.tooltip"),
                BasisLocalization.Get("settings.performanceMode.level.balanced.tooltip"),
                BasisLocalization.Get("settings.performanceMode.level.aggressive.tooltip")
            });
            dropdownLevel.AssignBinding(BasisSettingsDefaults.PerformanceModeLevel);

            // Single dropdown combining what used to be two independent toggles (Follow Player Count /
            // Suggest Performance Mode) into one control covering all 4 real combinations, to save
            // vertical space. Nothing new is persisted — it just reads/writes the same two existing
            // bool bindings other code (BasisPerformanceMode.Simulate, the busy-instance prompt)
            // already keys off directly.
            PanelDropdown dropdownAssist = PanelDropdown.CreateNewEntry(group.ContentParent);
            dropdownAssist.Descriptor.SetTitle(BasisLocalization.Get("settings.performanceMode.assist"));
            dropdownAssist.Descriptor.SetTooltip(BasisLocalization.Get("settings.performanceMode.assist.tooltip"));
            dropdownAssist.AssignLocalizedEntries(
                new List<string> { "Off", "Suggest", "Follow", "Both" },
                new List<string>
                {
                    "ui.option.off", "settings.graphics.highPlayerCapSuggestions",
                    "settings.performanceMode.auto", "settings.performanceMode.assist.both"
                },
                new List<string>
                {
                    "settings.performanceMode.assist.off.tooltip", "settings.graphics.highPlayerCapSuggestions.tooltip",
                    "settings.performanceMode.auto.tooltip", "settings.performanceMode.assist.both.tooltip"
                });

            bool ComboIncludesAuto(string id) => id == "Follow" || id == "Both";
            bool ComboIncludesSuggest(string id) => id == "Suggest" || id == "Both";
            string ComboId(bool auto, bool suggest) => auto ? (suggest ? "Both" : "Follow") : (suggest ? "Suggest" : "Off");

            dropdownAssist.SetValueWithoutNotify(ComboId(
                BasisSettingsDefaults.PerformanceModeAuto.RawValue,
                BasisSettingsDefaults.HighPlayerCapSuggestions.RawValue));

            // Combines two real bindings rather than being bound to one itself, so the reset
            // gesture needs an explicit default built from each binding's own default value.
            dropdownAssist.SetResetDefault(ComboId(
                BasisSettingsDefaults.PerformanceModeAuto.DefaultValue.GetDefault(),
                BasisSettingsDefaults.HighPlayerCapSuggestions.DefaultValue.GetDefault()));

            string autoLocked = BasisLocalization.Get("settings.performanceMode.level.autoLocked");
            dropdownLevel.SetInteractable(!BasisSettingsDefaults.PerformanceModeAuto.RawValue, autoLocked);

            BasisPanelTint.Handle levelTint = BasisPanelTint.Capture(group);

            void ApplyLevelTint(BasisPerformanceLevel level, bool animate)
            {
                bool on = level != BasisPerformanceLevel.Off;

                group.SetTitle(on
                    ? BasisLocalization.Get("settings.performanceMode.title.on", BasisPerformanceMode.DisplayName(level))
                    : BasisLocalization.Get("settings.performanceMode.title"));

                if (on)
                {
                    BasisPanelTint.Apply(levelTint, BasisPerformanceMode.AccentColor(level), animate);
                }
                else
                {
                    BasisPanelTint.Clear(levelTint, animate);
                }
            }

            void OnLevelTintChanged(BasisPerformanceLevel level) => ApplyLevelTint(level, true);

            ApplyLevelTint(BasisPerformanceMode.ActiveLevel, false);
            BasisPerformanceMode.OnLevelChanged += OnLevelTintChanged;
            group.OnInstanceReleased += () => BasisPerformanceMode.OnLevelChanged -= OnLevelTintChanged;

            dropdownLevel.OnValueChanged += _ =>
            {
                BasisMainMenu.Close();
                OpenToTab("settings.tab.graphics");
            };

            dropdownAssist.OnValueChanged += id =>
            {
                bool wasAuto = BasisSettingsDefaults.PerformanceModeAuto.RawValue;
                bool auto = ComboIncludesAuto(id);
                bool suggest = ComboIncludesSuggest(id);

                if (BasisSettingsDefaults.PerformanceModeAuto.RawValue != auto)
                {
                    BasisSettingsDefaults.PerformanceModeAuto.SetValue(auto);
                }
                if (BasisSettingsDefaults.HighPlayerCapSuggestions.RawValue != suggest)
                {
                    BasisSettingsDefaults.HighPlayerCapSuggestions.SetValue(suggest);
                }

                dropdownLevel.SetInteractable(!auto, autoLocked);

                if (!auto || wasAuto)
                {
                    return;
                }

                BasisPerformanceMode.ApplyAutoNow();
                BasisMainMenu.Close();
                OpenToTab("settings.tab.graphics");
            };
        }

        /// <summary>
        /// Graphics API the player starts on, at the foot of the Graphics page. Only the APIs this
        /// build actually ships for the running platform are listed — a player cannot start on one
        /// it has no shaders for — and the entry the session is running is marked as such, so the
        /// list never claims a renderer that is not really on offer. Unity takes the API from the
        /// command line and nowhere else, so a change is carried by the same relaunch the rest of
        /// the menu uses; a single-choice platform never builds the section at all.
        /// </summary>
        private static void BuildRendererSection(RectTransform container, PanelElementDescriptor descriptor)
        {
            if (!BasisGraphicsApiSelection.IsOffered)
            {
                return;
            }

            PanelSectionToggle rendererToggle = PanelSectionToggle.CreateNewEntry(container);
            rendererToggle.SetTitle(BasisLocalization.Get("settings.graphics.renderer.title"));

            PanelElementDescriptor group =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            // The collapse bar carries the section title, so the group header is left holding the
            // running-API line alone rather than printing the name twice.
            group.SetTitle(string.Empty);
            group.SetDescription(BasisLocalization.Get("settings.graphics.renderer.description",
                BasisGraphicsApiSelection.CurrentDisplayName));
            rendererToggle.RegisterContentContainer(group);

            PanelDropdown dropdownGraphicsApi = PanelDropdown.CreateNewEntry(group.ContentParent);
            dropdownGraphicsApi.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.renderer.api.tooltip"));

            List<string> apiIds = new List<string>();
            List<string> apiLabels = new List<string>();
            BasisGraphicsApiSelection.BuildDropdownEntries(apiIds, apiLabels);
            dropdownGraphicsApi.AssignEntries(apiIds, apiLabels);

            // Assigned first so the control writes through the binding, then moved onto the running
            // API: with nothing saved the binding is empty, which is no entry in this list.
            dropdownGraphicsApi.AssignBinding(BasisSettingsDefaults.GraphicsApi);
            dropdownGraphicsApi.SetValueWithoutNotify(BasisGraphicsApiSelection.SelectedId);

            void SyncGraphicsApiRestartNotice(string _)
            {
                dropdownGraphicsApi.SetValueWithoutNotify(BasisGraphicsApiSelection.SelectedId);
                dropdownGraphicsApi.Descriptor.SetTitle(BasisGraphicsApiSelection.NeedsRestart
                    ? BasisLocalization.Get("settings.graphics.renderer.api.restart")
                    : BasisLocalization.Get("settings.graphics.renderer.api"));
            }

            SyncGraphicsApiRestartNotice(null);

            // Shown but locked in the editor: the graphics device the editor started on is held for
            // its whole session, and toggling play mode cannot hand it a different one.
            if (!BasisGraphicsApiSelection.IsSupported)
            {
                dropdownGraphicsApi.SetInteractable(false, BasisLocalization.Get("settings.graphics.renderer.api.editorLocked"));
            }
            else
            {
                BasisSettingsDefaults.GraphicsApi.OnChanged += SyncGraphicsApiRestartNotice;
                dropdownGraphicsApi.OnInstanceReleased += () =>
                    BasisSettingsDefaults.GraphicsApi.OnChanged -= SyncGraphicsApiRestartNotice;

                // Offered on the control's own change only, the same way GPU occlusion culling does it,
                // so resetting the whole graphics tab doesn't throw a relaunch prompt. Picking the API
                // already running clears NeedsRestart and asks nothing.
                dropdownGraphicsApi.OnValueChanged += _ =>
                {
                    if (!BasisGraphicsApiSelection.NeedsRestart)
                    {
                        return;
                    }

                    if (BasisMainMenu.Instance == null)
                    {
                        return;
                    }

                    if (BasisMainMenu.Instance.Dialogue)
                    {
                        BasisMainMenu.Instance.Dialogue.ReleaseInstance();
                    }

                    BasisMainMenu.Instance.OpenDialogue(
                        BasisLocalization.Get("settings.graphics.renderer.restart.title"),
                        BasisLocalization.Get("settings.graphics.renderer.restart.prompt",
                            BasisGraphicsApiSelection.SelectedDisplayName),
                        BasisLocalization.Get("settings.graphics.renderer.restart.now"),
                        BasisLocalization.Get("settings.graphics.renderer.restart.later"),
                        accepted =>
                        {
                            if (accepted) BasisAppRelaunch.RebootAndReconnect();
                        });
                };
            }

            // Nested inside the Renderer section, under the API dropdown: DX12-only, so it only
            // ever appears alongside the API that actually needs it, not as its own page-level row.
            BuildPsoCacheSection(group.ContentParent);

            PanelSectionToggleHelpers.FinalizeCollapsibleGroup(rendererToggle, group, false,
                _ => descriptor.ForceRebuild());
        }

        /// <summary>
        /// On-disk PSO cache budget. DX12-only: D3D11 builds pipeline state lazily on its own
        /// worker threads and never pays the synchronous first-draw cost this cache exists to
        /// avoid, so the control would be dead weight on every other API this project ships.
        /// See BasisGraphicsStatePrewarm.
        /// </summary>
        private static void BuildPsoCacheSection(RectTransform container)
        {
            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Direct3D12)
            {
                return;
            }

            PanelSlider sliderPsoCache = PanelSlider.CreateEntryAndBind(
                container,
                PanelSlider.SliderSettings.Advanced(
                    BasisLocalization.Get("settings.graphics.psoCacheSize"),
                    256, 51200, false, 0, ValueDisplayMode.MemorySize),
                BasisSettingsDefaults.PsoCacheSizeMb);
            sliderPsoCache.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.psoCacheSize.tooltip"));
        }

        private static void ResetGraphicsDefaults()
        {
            BasisSettingsDefaults.PsoCacheSizeMb.ResetToDefault();
            BasisPerformanceMode.SetLevel(BasisPerformanceLevel.Off);
            BasisSettingsDefaults.PerformanceModeAuto.ResetToDefault();
            BasisSettingsDefaults.PerformanceModeLevel.ResetToDefault();
            BasisSettingsDefaults.PerformanceModeBaseline.ResetToDefault();
            BasisSettingsDefaults.HighPlayerCapSuggestions.ResetToDefault();

            SettingsProviderPerformanceLimits.ResetPerformanceLimitDefaults();
            SettingsProviderNamePlate.ResetNamePlateDefaults();

            BasisSettingsDefaults.AvatarRange.ResetToDefault();
            BasisSettingsDefaults.UseMaxVisibleAvatars.ResetToDefault();
            BasisSettingsDefaults.MaxVisibleAvatars.ResetToDefault();

            BasisSettingsDefaults.QualityLevel.ResetToDefault();
            BasisSettingsDefaults.ShadowQuality.ResetToDefault();
            BasisSettingsDefaults.Antialiasing.ResetToDefault();
            BasisSettingsDefaults.VSync.ResetToDefault();
            BasisSettingsDefaults.VSyncCapFps.ResetToDefault();
            BasisSettingsDefaults.HeadsetRefreshRate.ResetToDefault();

            BasisSettingsDefaults.DevVariableRateShading.ResetToDefault();
            BasisSettingsDefaults.VrsFovealInnerRadius.ResetToDefault();
            BasisSettingsDefaults.VrsFovealOuterRadius.ResetToDefault();

            BasisSettingsDefaults.HDRSupport.ResetToDefault();
            BasisSettingsDefaults.MemoryAllocation.ResetToDefault();
            BasisSettingsDefaults.RenderResolution.ResetToDefault();
            BasisSettingsDefaults.DynamicResolutionEnabled.ResetToDefault();
            BasisSettingsDefaults.DynamicResolutionMinimumScale.ResetToDefault();
            BasisSettingsDefaults.DynamicResolutionMaximumScale.ResetToDefault();
            BasisSettingsDefaults.DynamicResolutionTargetOverride.ResetToDefault();
            BasisSettingsDefaults.DynamicResolutionTargetFrameRate.ResetToDefault();

            BasisSettingsDefaults.FoveatedRendering.ResetToDefault();
            BasisSettingsDefaults.FieldOfView.ResetToDefault();
            BasisSettingsDefaults.PoseLOD.ResetToDefault();
            BasisSettingsDefaults.AvatarMeshLOD.ResetToDefault();
            BasisSettingsDefaults.UseAvatarSkinLod.ResetToDefault();
            BasisSettingsDefaults.UseAvatarShadowLod.ResetToDefault();
            BasisSettingsDefaults.UseAvatarVisibilityCull.ResetToDefault();
            BasisSettingsDefaults.ShowPerformanceBar.ResetToDefault();
            BasisSettingsDefaults.PerformanceBarDetailed.ResetToDefault();
            BasisSettingsDefaults.UseGpuOcclusionCulling.ResetToDefault();
            BasisGraphicsApiSelection.ResetToRunning();
            BasisSettingsDefaults.GlobalMeshLOD.ResetToDefault();
            BasisSettingsDefaults.LocalHeadBlendShapes.ResetToDefault();

            BasisSettingsDefaults.UseMirrorQualityOverride.ResetToDefault();
            BasisSettingsDefaults.MirrorQuality.ResetToDefault();
            BasisSettingsDefaults.UseCameraClipOverride.ResetToDefault();
            BasisSettingsDefaults.CameraClipNear.ResetToDefault();
            BasisSettingsDefaults.CameraClipFar.ResetToDefault();

            BasisSettingsDefaults.UseBloomOverride.ResetToDefault();
            BasisSettingsDefaults.BloomIntensity.ResetToDefault();
            BasisSettingsDefaults.UseVolumetricFogOverride.ResetToDefault();
            BasisSettingsDefaults.VolumetricFogDensity.ResetToDefault();
            BasisSettingsDefaults.VolumetricFogBakedAPV.ResetToDefault();
            BasisSettingsDefaults.UseMotionBlurOverride.ResetToDefault();
            BasisSettingsDefaults.MotionBlurIntensity.ResetToDefault();
            BasisSettingsDefaults.MotionBlurClamp.ResetToDefault();
            BasisSettingsDefaults.MotionBlurQuality.ResetToDefault();
            BasisSettingsDefaults.MotionBlurMode.ResetToDefault();
            BasisSettingsDefaults.UseGlobalIllumination.ResetToDefault();
            BasisSettingsDefaults.GlobalIlluminationMode.ResetToDefault();
            BasisSettingsDefaults.GlobalIlluminationPreset.ResetToDefault();
            BasisSettingsDefaults.GlobalIlluminationLayers.ResetToDefault();
            BasisSettingsDefaults.GlobalIlluminationObscuranceRadius.ResetToDefault();
            BasisSettingsDefaults.GlobalIlluminationFadeDistance.ResetToDefault();
            BasisSettingsDefaults.GlobalIlluminationNormalBias.ResetToDefault();
            BasisSettingsDefaults.GlobalIlluminationDistanceBias.ResetToDefault();
            BasisSettingsDefaults.GlobalIlluminationBounceThreshold.ResetToDefault();
            BasisSettingsDefaults.GlobalIlluminationFireflyClamp.ResetToDefault();
            BasisSettingsDefaults.GlobalIlluminationSkinnedMeshes.ResetToDefault();
            BasisSettingsDefaults.GlobalIlluminationQuality.ResetToDefault();
            BasisSettingsDefaults.GlobalIlluminationResolution.ResetToDefault();
            BasisSettingsDefaults.GlobalIlluminationFallback.ResetToDefault();
            BasisSettingsDefaults.GlobalIlluminationIgnoreBakedEmission.ResetToDefault();
            BasisSettingsDefaults.GlobalIlluminationLightmappedReceive.ResetToDefault();
            BasisSettingsDefaults.GlobalIlluminationIntensity.ResetToDefault();
            BasisSettingsDefaults.GlobalIlluminationSaturation.ResetToDefault();
            BasisSettingsDefaults.GlobalIlluminationObscurance.ResetToDefault();
            BasisSettingsDefaults.GlobalIlluminationRayLength.ResetToDefault();
            BasisSettingsDefaults.GlobalIlluminationSmoothing.ResetToDefault();
            BasisSettingsDefaults.GlobalIlluminationTemporalResponse.ResetToDefault();
            BasisSettingsDefaults.GlobalIlluminationTemporalFilter.ResetToDefault();
            BasisSettingsDefaults.GlobalIlluminationWideBlur.ResetToDefault();
            BasisSettingsDefaults.GlobalIlluminationRayReuse.ResetToDefault();
            BasisSettingsDefaults.GlobalIlluminationEmitters.ResetToDefault();
            BasisSettingsDefaults.GlobalIlluminationEmitterIntensity.ResetToDefault();
            BasisSettingsDefaults.GlobalIlluminationReflectionProbes.ResetToDefault();
            BasisSettingsDefaults.GlobalIlluminationSpecular.ResetToDefault();
            BasisSettingsDefaults.GlobalIlluminationSpecularIntensity.ResetToDefault();
            BasisSettingsDefaults.GlobalIlluminationSpecularMaxRoughness.ResetToDefault();
            BasisSettingsDefaults.GlobalIlluminationSpecularRayLength.ResetToDefault();
            BasisSettingsDefaults.GlobalIlluminationSpecularFadeDistance.ResetToDefault();
            BasisSettingsDefaults.UseRayTracedAmbientOcclusion.ResetToDefault();
            BasisSettingsDefaults.RayTracedAmbientOcclusionMode.ResetToDefault();
            BasisSettingsDefaults.RayTracedAmbientOcclusionQuality.ResetToDefault();
            BasisSettingsDefaults.RayTracedAmbientOcclusionIntensity.ResetToDefault();
            BasisSettingsDefaults.RayTracedAmbientOcclusionRadius.ResetToDefault();
            BasisSettingsDefaults.RayTracedAmbientOcclusionLayers.ResetToDefault();
            BasisSettingsDefaults.RayTracedAmbientOcclusionNormalBias.ResetToDefault();
            BasisSettingsDefaults.RayTracedAmbientOcclusionDistanceBias.ResetToDefault();
            BasisSettingsDefaults.RayTracedAmbientOcclusionFalloff.ResetToDefault();
            BasisSettingsDefaults.RayTracedAmbientOcclusionPower.ResetToDefault();
            BasisSettingsDefaults.RayTracedAmbientOcclusionFadeStart.ResetToDefault();
            BasisSettingsDefaults.RayTracedAmbientOcclusionFadeEnd.ResetToDefault();
            BasisSettingsDefaults.RayTracedAmbientOcclusionSpecularRelief.ResetToDefault();
            BasisSettingsDefaults.RayTracedAmbientOcclusionSkinnedMeshes.ResetToDefault();
            BasisSettingsDefaults.RayTracedAmbientOcclusionDirectStrength.ResetToDefault();
            BasisSettingsDefaults.RayTracedAmbientOcclusionDenoise.ResetToDefault();
            BasisSettingsDefaults.RayTracedAmbientOcclusionOtherCameras.ResetToDefault();
            BasisSettingsDefaults.RayTracedAmbientOcclusionApply.ResetToDefault();

            // Note: Resolution & ScreenMode are not shown as BasisSettingsDefaults bindings in your snippet.
            // If you later add bindings for them, add them here.
        }

        public static PanelDropdown dropdownResolution;
        public static List<Vector2Int> uniqueResolutions;
        private static List<string> resolutionOptions;
        public static PanelDropdown dropdownScreenMode;

        private static void ScreenMode(int screenModeIndex)
        {
            FullScreenMode mode = GetScreenModeFromIndex(screenModeIndex);
            Vector2Int currentResolution = uniqueResolutions[dropdownResolution.DropdownComponent.value];

            Screen.SetResolution(currentResolution.x, currentResolution.y, mode);
            BasisDebug.Log("Changed Screen Mode: " + mode);
        }

        private static FullScreenMode GetScreenModeFromIndex(int index)
        {
            switch (index)
            {
                case 0: return FullScreenMode.ExclusiveFullScreen;
                case 1: return FullScreenMode.FullScreenWindow;
                case 2: return FullScreenMode.Windowed;
                default: return FullScreenMode.FullScreenWindow;
            }
        }

        private static int GetIndexFromScreenMode(FullScreenMode FullScreenMode)
        {
            switch (FullScreenMode)
            {
                case FullScreenMode.ExclusiveFullScreen: return 0;
                case FullScreenMode.FullScreenWindow: return 1;
                case FullScreenMode.Windowed: return 2;
                default: return 2;
            }
        }

        private static void ResolutionChanged(int resolutionIndex)
        {
            Vector2Int selectedResolution = uniqueResolutions[resolutionIndex];
            FullScreenMode mode = GetScreenModeFromIndex(dropdownScreenMode.DropdownComponent.value);

            Screen.SetResolution(selectedResolution.x, selectedResolution.y, mode);
            BasisDebug.Log("Changed Resolution: " + selectedResolution.x + "x" + selectedResolution.y);
        }
        // ------------------
        // Chat
        // ------------------
        public static PanelTabPage ChatTab(PanelTabGroup tabGroup)
        {
            PanelTabPage tab = PanelTabPage.CreateVertical(tabGroup.Descriptor.ContentParent);
            PanelElementDescriptor descriptor = tab.Descriptor;

            descriptor.SetTitle(BasisLocalization.Get("settings.tab.chat"));
            RectTransform container = descriptor.ContentParent;

            PanelTextField chatTextField = null;
            PanelSlider sliderChatSize = null;
            PanelSectionToggleHelpers.CreateCollapsibleBoxedSection(container,
                BasisLocalization.Get("settings.tab.chat"), () =>
            {
                PanelToggle toggleChatDisabled = PanelToggle.CreateNewEntry(container);
                toggleChatDisabled.Descriptor.SetTitle(BasisLocalization.Get("settings.chat.disable"));
                toggleChatDisabled.Descriptor.SetTooltip(BasisLocalization.Get("settings.chat.disable.tooltip"));
                toggleChatDisabled.AssignBinding(BasisSettingsDefaults.ChatDisabled);

                chatTextField = PanelTextField.CreateNewEntry(container);
                _chatTextField = chatTextField;
                chatTextField.Descriptor.SetTitle(BasisLocalization.Get("settings.chat.message"));
                chatTextField.Descriptor.SetTooltip(BasisLocalization.Get("settings.chat.message.tooltip"));
                chatTextField.SetValueWithoutNotify(string.Empty);
                chatTextField._inputField.characterLimit = BasisChatSanitizer.MaxMessageCharacters;
                chatTextField._inputField.onEndEdit.AddListener(OnEndEndit);
                chatTextField._inputField.onSubmit.AddListener(OnChatSubmitted);
                chatTextField._inputField.onValueChanged.AddListener(OnChatMessageChanged);
                ApplyPendingChatComposerRequest();

                sliderChatSize = PanelSlider.CreateEntryAndBind(
                    container,
                    PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.chat.textSize"), 0.5f, 3f, false, 2, ValueDisplayMode.Raw),
                    BasisSettingsDefaults.ChatSize);
                sliderChatSize.Descriptor.SetTooltip(BasisLocalization.Get("settings.chat.textSize.tooltip"));

                // Composer hides when the local player turned chat off OR the server locked it.
                // Re-evaluated each time the tab is built (the menu is rebuilt on every open), so
                // a lock flipped mid-session lands on the next open — SendChatMessage refuses in
                // the meantime, so nothing escapes either way.
                bool chatEnabled = !BasisSettingsDefaults.ChatDisabled.RawValue && !BasisNetworkHandleChat.LockedByServer;
                chatTextField.Descriptor.SetActive(chatEnabled);
                sliderChatSize.Descriptor.SetActive(chatEnabled);
                toggleChatDisabled.OnValueChanged += (val) =>
                {
                    bool enabled = !val && !BasisNetworkHandleChat.LockedByServer;
                    chatTextField.Descriptor.SetActive(enabled);
                    if (val)
                    {
                        BasisNetworkHandleChatTyping.SendTypingState(false);
                    }
                    sliderChatSize.Descriptor.SetActive(enabled);
                    descriptor.ForceRebuild();
                };
            }, false, visible =>
            {
                // Section expand re-shows every row; re-apply the chat-disabled gate.
                if (visible && chatTextField != null)
                {
                    bool chatOn = !BasisSettingsDefaults.ChatDisabled.RawValue && !BasisNetworkHandleChat.LockedByServer;
                    chatTextField.Descriptor.SetActive(chatOn);
                    sliderChatSize.Descriptor.SetActive(chatOn);
                }
                descriptor.ForceRebuild();
            });

            void OnEndEndit(string message)
            {
                BasisNetworkHandleChatTyping.SendTypingState(false);
            }

            void OnChatSubmitted(string message)
            {
                BasisNetworkHandleChatTyping.SendTypingState(false);
                if (!string.IsNullOrEmpty(message))
                {
                    BasisNetworkHandleChat.SendChatMessage(message, _chatComposerPlayNotificationSound);
                    chatTextField.SetValueWithoutNotify(string.Empty);
                    _chatComposerPlayNotificationSound = true;
                }
            }

            void OnChatMessageChanged(string message)
            {
                BasisNetworkHandleChatTyping.SendTypingState(!string.IsNullOrEmpty(message));
                if (string.IsNullOrEmpty(message))
                {
                    _chatComposerPlayNotificationSound = true;
                }
            }

            PanelSectionToggleHelpers.CreateCollapsibleBoxedSection(container,
                BasisLocalization.Get("settings.chat.notifications.title"), () =>
            {
                PanelToggle toggleJoinNotifications = PanelToggle.CreateNewEntry(container);
                toggleJoinNotifications.Descriptor.SetTitle(BasisLocalization.Get("settings.chat.joinNotifications"));
                toggleJoinNotifications.Descriptor.SetTooltip(BasisLocalization.Get("settings.chat.joinNotifications.tooltip"));
                toggleJoinNotifications.AssignBinding(BasisSettingsDefaults.JoinNotifications);

                PanelToggle toggleLeaveNotifications = PanelToggle.CreateNewEntry(container);
                toggleLeaveNotifications.Descriptor.SetTitle(BasisLocalization.Get("settings.chat.leaveNotifications"));
                toggleLeaveNotifications.Descriptor.SetTooltip(BasisLocalization.Get("settings.chat.leaveNotifications.tooltip"));
                toggleLeaveNotifications.AssignBinding(BasisSettingsDefaults.LeaveNotifications);
            }, false, _ => descriptor.ForceRebuild());

            // What gets written into a photo file used to sit here, under Chat. It belongs to the
            // camera, so it now lives in the Camera Settings panel's Advanced tab — see
            // BasisHandHeldCameraPanelProvider.BuildPhotoMetadataGroup. Its localization keys still
            // read settings.chat.camera.* because they carry sixteen translations.

            BuildAppearanceContent(container, descriptor);

            RegisterPageReset("settings.tab.chat", ResetChatDefaults);

            descriptor.ForceRebuild();
            return tab;
        }

        private static void BuildAppearanceContent(RectTransform container, PanelElementDescriptor tabDescriptor = null)
        {
            PanelSectionToggle menuStylesToggle = PanelSectionToggle.CreateNewEntry(container);
            menuStylesToggle.SetTitle(BasisLocalization.Get("settings.chat.menuStyles.title"));
            int menuStylesStart = container.childCount;
            RectTransform content = container;

            PanelSectionToggleHelpers.CreateCollapsibleBoxedSection(content,
                BasisLocalization.Get("settings.chat.raycast.title"), () =>
            {
                PanelSlider sliderRaycastSize = PanelSlider.CreateEntryAndBind(
                    content,
                    PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.chat.raycast.size"), 0.25f, 4f, false, 2, ValueDisplayMode.Raw),
                    BasisSettingsDefaults.RaycastLineWidth);
                sliderRaycastSize.Descriptor.SetTooltip(BasisLocalization.Get("settings.chat.raycast.size.tooltip"));
                sliderRaycastSize.SliderComponent.onValueChanged.AddListener(Basis.Scripts.UI.BasisRaycastLineCustomization.PreviewWidth);
            }, false, _ => tabDescriptor?.ForceRebuild());

            PanelSectionToggleHelpers.CreateCollapsibleBoxedSection(content,
                BasisLocalization.Get("settings.chat.uicolors.title"), () =>
            {
                Color raycastColorInit = Basis.Scripts.UI.BasisRaycastLineCustomization.ParseColor(BasisSettingsDefaults.RaycastLineColor.RawValue)
                    ?? new Color(0.3019608f, 0.09411766f, 0.2980392f);
                SettingsProviderUIStyle.AddBindingColorPicker(content,
                    BasisLocalization.Get("settings.chat.raycast.color"),
                    BasisSettingsDefaults.RaycastLineColor, raycastColorInit,
                    c => Basis.Scripts.UI.BasisRaycastLineCustomization.PreviewUiLineColor(c));

                Color highlightColorInit = Basis.Scripts.BasisSdk.Highlight.BasisHighlightConfigOverride.ParseColor(BasisSettingsDefaults.HighlightColor.RawValue)
                    ?? new Color(0.48365337f, 0.33490568f, 1f, 1f);
                SettingsProviderUIStyle.AddBindingColorPicker(content,
                    BasisLocalization.Get("settings.chat.pickup.highlightColor"),
                    BasisSettingsDefaults.HighlightColor, highlightColorInit,
                    c => Basis.Scripts.BasisSdk.Highlight.BasisHighlightConfigOverride.PreviewColor(c));

                Color pickupLineColorInit = Basis.Scripts.UI.BasisRaycastLineCustomization.ParseColor(BasisSettingsDefaults.PickupLineColor.RawValue)
                    ?? new Color(0.48365337f, 0.33490568f, 1f, 1f);
                SettingsProviderUIStyle.AddBindingColorPicker(content,
                    BasisLocalization.Get("settings.chat.pickup.lineColor"),
                    BasisSettingsDefaults.PickupLineColor, pickupLineColorInit,
                    c => Basis.Scripts.UI.BasisRaycastLineCustomization.PreviewInteractionLineColor(c));
            }, false, _ => tabDescriptor?.ForceRebuild());

            PanelSectionToggleHelpers.CreateCollapsibleBoxedSection(content,
                BasisLocalization.Get("settings.chat.menuEdge.title"), () =>
            {
                PanelToggle toggleWhiteEdge = PanelToggle.CreateNewEntry(content);
                toggleWhiteEdge.Descriptor.SetTitle(BasisLocalization.Get("settings.chat.menuEdge.white"));
                toggleWhiteEdge.Descriptor.SetTooltip(BasisLocalization.Get("settings.chat.menuEdge.white.tooltip"));
                toggleWhiteEdge.AssignBinding(BasisSettingsDefaults.MenuEdgeWhite);
                toggleWhiteEdge.OnValueChanged += (val) => SettingsProviderUIStyle.ApplyEdgeColor(val);
            }, false, _ => tabDescriptor?.ForceRebuild());

            BuildMenuBackgroundContent(content, tabDescriptor);

            PanelSectionToggleHelpers.FinalizeFlatSectionFromIndex(menuStylesToggle, container, menuStylesStart, false,
                _ => tabDescriptor?.ForceRebuild());
        }

        private static void BuildMenuBackgroundContent(RectTransform content, PanelElementDescriptor tabDescriptor = null)
        {
            PanelSectionToggleHelpers.CreateCollapsibleBoxedSection(content,
                BasisLocalization.Get("settings.chat.menuBackground.title"), () =>
            {
                AddMenuBackgroundSlider(content, "accentAmount", 0f, 1f, false, 2, ValueDisplayMode.Raw,
                    BasisSettingsDefaults.MenuBGAccentAmount, Basis.Scripts.UI.BasisUIBackgroundCustomization.PreviewAccentAmount);
                AddMenuBackgroundSlider(content, "accentFeather", 0f, 1f, false, 2, ValueDisplayMode.Raw,
                    BasisSettingsDefaults.MenuBGAccentFeather, Basis.Scripts.UI.BasisUIBackgroundCustomization.PreviewAccentFeather);
                AddMenuBackgroundSlider(content, "accentSoftness", 0.25f, 4f, false, 2, ValueDisplayMode.Raw,
                    BasisSettingsDefaults.MenuBGAccentSoftness, Basis.Scripts.UI.BasisUIBackgroundCustomization.PreviewAccentSoftness);
                AddMenuBackgroundSlider(content, "brandGradient", 0f, 1f, false, 2, ValueDisplayMode.Raw,
                    BasisSettingsDefaults.MenuBGBrandGradient, Basis.Scripts.UI.BasisUIBackgroundCustomization.PreviewBrandGradient);
                AddMenuBackgroundSlider(content, "gradientCycle", 2f, 60f, false, 1, ValueDisplayMode.Raw,
                    BasisSettingsDefaults.MenuBGGradientCycle, Basis.Scripts.UI.BasisUIBackgroundCustomization.PreviewGradientCycle);
                AddMenuBackgroundSlider(content, "animationSpeed", 0f, 4f, false, 2, ValueDisplayMode.Raw,
                    BasisSettingsDefaults.MenuBGAnimationSpeed, Basis.Scripts.UI.BasisUIBackgroundCustomization.PreviewAnimationSpeed);
                AddMenuBackgroundSlider(content, "sheen", 0f, 1f, false, 2, ValueDisplayMode.Raw,
                    BasisSettingsDefaults.MenuBGSheen, Basis.Scripts.UI.BasisUIBackgroundCustomization.PreviewSheen);

                Color cursorGlowColorInit = Basis.Scripts.UI.BasisUIBackgroundCustomization.ParseColor(BasisSettingsDefaults.MenuBGCursorGlowColor.RawValue)
                    ?? Basis.Scripts.UI.BasisUIBackgroundCustomization.DefaultCursorGlowSwatch;
                SettingsProviderUIStyle.AddBindingColorPicker(content,
                    BasisLocalization.Get("settings.chat.menuBackground.cursorGlowColor"),
                    BasisSettingsDefaults.MenuBGCursorGlowColor, cursorGlowColorInit,
                    c => Basis.Scripts.UI.BasisUIBackgroundCustomization.PreviewCursorGlowColor(c));
            }, false, _ => tabDescriptor?.ForceRebuild());

            PanelSectionToggleHelpers.CreateCollapsibleBoxedSection(content,
                BasisLocalization.Get("settings.chat.menuBackground.pointer.title"), () =>
            {
                AddMenuBackgroundSlider(content, "cursorGlow", 0f, 2f, false, 2, ValueDisplayMode.Raw,
                    BasisSettingsDefaults.MenuBGCursorGlow, Basis.Scripts.UI.BasisUIBackgroundCustomization.PreviewCursorGlow);
                AddMenuBackgroundSlider(content, "cursorGlowRadius", 0.02f, 2f, false, 2, ValueDisplayMode.Meters,
                    BasisSettingsDefaults.MenuBGCursorGlowRadius, Basis.Scripts.UI.BasisUIBackgroundCustomization.PreviewCursorGlowRadius);
            }, false, _ => tabDescriptor?.ForceRebuild());

            PanelSectionToggleHelpers.CreateCollapsibleBoxedSection(content,
                BasisLocalization.Get("settings.chat.menuBackground.finish.title"), () =>
            {
                AddMenuBackgroundSlider(content, "vignette", 0f, 1f, false, 2, ValueDisplayMode.Raw,
                    BasisSettingsDefaults.MenuBGVignette, Basis.Scripts.UI.BasisUIBackgroundCustomization.PreviewVignette);
                AddMenuBackgroundSlider(content, "exposure", 0.25f, 3f, false, 2, ValueDisplayMode.Raw,
                    BasisSettingsDefaults.MenuBGExposure, Basis.Scripts.UI.BasisUIBackgroundCustomization.PreviewExposure);
                AddMenuBackgroundSlider(content, "grain", 0f, 16f, false, 1, ValueDisplayMode.Raw,
                    BasisSettingsDefaults.MenuBGGrain, Basis.Scripts.UI.BasisUIBackgroundCustomization.PreviewGrain);
                AddMenuBackgroundSlider(content, "grainScale", 64f, 4096f, true, 0, ValueDisplayMode.Raw,
                    BasisSettingsDefaults.MenuBGGrainScale, Basis.Scripts.UI.BasisUIBackgroundCustomization.PreviewGrainScale);
            }, false, _ => tabDescriptor?.ForceRebuild());
        }

        private static void AddMenuBackgroundSlider(Component parent, string key, float min, float max,
            bool wholeNumbers, int decimalPlaces, ValueDisplayMode displayMode,
            BasisSettingsBinding<float> binding, UnityEngine.Events.UnityAction<float> preview)
        {
            PanelSlider slider = PanelSlider.CreateEntryAndBind(
                parent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.chat.menuBackground." + key),
                    min, max, wholeNumbers, decimalPlaces, displayMode),
                binding);
            slider.Descriptor.SetTooltip(BasisLocalization.Get("settings.chat.menuBackground." + key + ".tooltip"));
            slider.SliderComponent.onValueChanged.AddListener(preview);
        }

        private static void ResetChatDefaults()
        {
            BasisSettingsDefaults.JoinNotifications.ResetToDefault();
            BasisSettingsDefaults.LeaveNotifications.ResetToDefault();
            BasisSettingsDefaults.ChatDisabled.ResetToDefault();
            BasisSettingsDefaults.ChatSize.ResetToDefault();
            BasisSettingsDefaults.PhotoMetadataTagging.ResetToDefault();
            BasisSettingsDefaults.PhotoEmbedPersonDetails.ResetToDefault();
            BasisSettingsDefaults.PhotoEmbedCameraSettings.ResetToDefault();
            BasisSettingsDefaults.PhotoEmbedCaptureInfo.ResetToDefault();
            BasisSettingsDefaults.PhotoEmbedPhotographer.ResetToDefault();
            BasisSettingsDefaults.PhotoEmbedWorld.ResetToDefault();
            BasisSettingsDefaults.RaycastLineWidth.ResetToDefault();
            BasisSettingsDefaults.RaycastLineColor.ResetToDefault();
            BasisSettingsDefaults.HighlightColor.ResetToDefault();
            BasisSettingsDefaults.PickupLineColor.ResetToDefault();
            BasisSettingsDefaults.MenuBGAccentAmount.ResetToDefault();
            BasisSettingsDefaults.MenuBGAccentFeather.ResetToDefault();
            BasisSettingsDefaults.MenuBGAccentSoftness.ResetToDefault();
            BasisSettingsDefaults.MenuBGBrandGradient.ResetToDefault();
            BasisSettingsDefaults.MenuBGGradientCycle.ResetToDefault();
            BasisSettingsDefaults.MenuBGAnimationSpeed.ResetToDefault();
            BasisSettingsDefaults.MenuBGSheen.ResetToDefault();
            BasisSettingsDefaults.MenuBGCursorGlowColor.ResetToDefault();
            BasisSettingsDefaults.MenuBGCursorGlow.ResetToDefault();
            BasisSettingsDefaults.MenuBGCursorGlowRadius.ResetToDefault();
            BasisSettingsDefaults.MenuBGVignette.ResetToDefault();
            BasisSettingsDefaults.MenuBGExposure.ResetToDefault();
            BasisSettingsDefaults.MenuBGGrain.ResetToDefault();
            BasisSettingsDefaults.MenuBGGrainScale.ResetToDefault();
            SettingsProviderUIStyle.ResetUIStyleDefaults();
        }

        private static void ApplyPendingChatComposerRequest()
        {
            if (_chatTextField == null || _chatTextField._inputField == null)
            {
                return;
            }

            if (_pendingChatComposerText != null)
            {
                _chatTextField.SetValueWithoutNotify(_pendingChatComposerText);
                _pendingChatComposerText = null;
                _chatComposerPlayNotificationSound = _pendingChatComposerPlaySound;
                BasisNetworkHandleChatTyping.SendTypingState(!string.IsNullOrEmpty(_chatTextField._inputField.text));
            }

            if (_pendingChatComposerPlaySound)
            {
                BasisNetworkHandleChat.PlayChatNotification();
                _pendingChatComposerPlaySound = false;
            }

            if (_pendingChatComposerFocus)
            {
                TMP_InputField input = _chatTextField._inputField;
                input.Select();
                input.ActivateInputField();
                _pendingChatComposerFocus = false;
            }
        }

        private static void ClearChatComposerReference()
        {
            BasisNetworkHandleChatTyping.SendTypingState(false);
            _chatComposerPlayNotificationSound = true;
            _chatTextField = null;
        }

        // ------------------
        // DEVELOPER TAB (ONE RESET BUTTON)
        // ------------------
        public static PanelTabPage DeveloperTab(PanelTabGroup tabGroup)
        {
            PanelTabPage tab = PanelTabPage.CreateVertical(tabGroup.Descriptor.ContentParent);
            PanelElementDescriptor descriptor = tab.Descriptor;

            descriptor.SetTitle(BasisLocalization.Get("settings.developer.title"));
            RectTransform container = descriptor.ContentParent;

            // ---- Identity Key ----
            BuildIdentitySection(container, descriptor);

            // ---- Frame time (ms) readout, appended to the Time/FPS line every menu panel shows in
            // its header (BasisFrameRateVisualization) ----
            PanelToggle toggleShowFrameTimeMs = PanelToggle.CreateNewEntry(container);
            toggleShowFrameTimeMs.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.showFrameTimeMs"));
            toggleShowFrameTimeMs.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.showFrameTimeMs.tooltip"));
            toggleShowFrameTimeMs.AssignBinding(BasisSettingsDefaults.ShowFrameTimeMs);

            // ---- Gizmos & Overlays (per-gizmo toggles; rendering turns on when any are enabled) ----
            PanelSectionToggle gizmosToggle = PanelSectionToggle.CreateNewEntry(container);
            gizmosToggle.SetTitle(BasisLocalization.Get("settings.developer.group.gizmos"));
            int gizmosStart = container.childCount;

            PanelToggle toggleSkeletonLines = PanelToggle.CreateNewEntry(container);
            toggleSkeletonLines.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.skeletonLines"));
            toggleSkeletonLines.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.skeletonLines.tooltip"));
            toggleSkeletonLines.AssignBinding(BasisSettingsDefaults.GizmoSkeletonLines);

            PanelToggle toggleCalibrationSpheres = PanelToggle.CreateNewEntry(container);
            toggleCalibrationSpheres.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.calibrationSpheres"));
            toggleCalibrationSpheres.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.calibrationSpheres.tooltip"));
            toggleCalibrationSpheres.AssignBinding(BasisSettingsDefaults.GizmoCalibrationSpheres);

            PanelToggle toggleJiggleVisuals = PanelToggle.CreateNewEntry(container);
            toggleJiggleVisuals.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.jiggleVisuals"));
            toggleJiggleVisuals.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.jiggleVisuals.tooltip"));
            toggleJiggleVisuals.AssignBinding(BasisSettingsDefaults.GizmoJiggleVisuals);

            PanelToggle toggleAvatarProxy = PanelToggle.CreateNewEntry(container);
            toggleAvatarProxy.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.avatarProxy"));
            toggleAvatarProxy.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.avatarProxy.tooltip"));
            toggleAvatarProxy.AssignBinding(BasisSettingsDefaults.GizmoAvatarProxy);

            PanelToggle toggleTrackerGizmos = PanelToggle.CreateNewEntry(container);
            toggleTrackerGizmos.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.trackerGizmos"));
            toggleTrackerGizmos.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.trackerGizmos.tooltip"));
            toggleTrackerGizmos.AssignBinding(BasisSettingsDefaults.TrackerGizmos);

            PanelToggle toggleLinkedTrackerLines = PanelToggle.CreateNewEntry(container);
            toggleLinkedTrackerLines.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.linkedTrackerLines"));
            toggleLinkedTrackerLines.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.linkedTrackerLines.tooltip"));
            toggleLinkedTrackerLines.AssignBinding(BasisSettingsDefaults.LinkedTrackerLines);

            PanelToggle toggleEyeGazeGizmo = PanelToggle.CreateNewEntry(container);
            toggleEyeGazeGizmo.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.eyeGazeGizmo"));
            toggleEyeGazeGizmo.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.eyeGazeGizmo.tooltip"));
            toggleEyeGazeGizmo.AssignBinding(BasisSettingsDefaults.GizmoEyeGaze);

            PanelToggle toggleIKColliders = PanelToggle.CreateNewEntry(container);
            toggleIKColliders.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.ikColliders"));
            toggleIKColliders.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.ikColliders.tooltip"));
            toggleIKColliders.AssignBinding(BasisSettingsDefaults.GizmoIKColliders);

            PanelToggle togglePointerRay = PanelToggle.CreateNewEntry(container);
            togglePointerRay.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.pointerRay"));
            togglePointerRay.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.pointerRay.tooltip"));
            togglePointerRay.AssignBinding(BasisSettingsDefaults.GizmoPointerRay);

            PanelToggle toggleHintOffsets = PanelToggle.CreateNewEntry(container);
            toggleHintOffsets.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.hintOffsets"));
            toggleHintOffsets.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.hintOffsets.tooltip"));
            toggleHintOffsets.AssignBinding(BasisSettingsDefaults.GizmoHintOffsets);

            PanelToggle toggleFootPlacement = PanelToggle.CreateNewEntry(container);
            toggleFootPlacement.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.footPlacement"));
            toggleFootPlacement.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.footPlacement.tooltip"));
            toggleFootPlacement.AssignBinding(BasisSettingsDefaults.GizmoFootPlacement);

            PanelToggle toggleInteractionHover = PanelToggle.CreateNewEntry(container);
            toggleInteractionHover.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.interactionHover"));
            toggleInteractionHover.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.interactionHover.tooltip"));
            toggleInteractionHover.AssignBinding(BasisSettingsDefaults.GizmoInteractionHover);

            PanelToggle toggleFingerTouchGizmo = PanelToggle.CreateNewEntry(container);
            toggleFingerTouchGizmo.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.fingerTouch"));
            toggleFingerTouchGizmo.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.fingerTouch.tooltip"));
            toggleFingerTouchGizmo.AssignBinding(BasisSettingsDefaults.GizmoFingerTouch);

            PanelToggle toggleSeatTargets = PanelToggle.CreateNewEntry(container);
            toggleSeatTargets.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.seatTargets"));
            toggleSeatTargets.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.seatTargets.tooltip"));
            toggleSeatTargets.AssignBinding(BasisSettingsDefaults.GizmoSeatTargets);

            PanelToggle toggleJiggleGrabGizmo = PanelToggle.CreateNewEntry(container);
            toggleJiggleGrabGizmo.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.jiggleGrab"));
            toggleJiggleGrabGizmo.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.jiggleGrab.tooltip"));
            toggleJiggleGrabGizmo.AssignBinding(BasisSettingsDefaults.GizmoJiggleGrab);

            PanelToggle toggleHandGripGizmo = PanelToggle.CreateNewEntry(container);
            toggleHandGripGizmo.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.handGrip"));
            toggleHandGripGizmo.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.handGrip.tooltip"));
            toggleHandGripGizmo.AssignBinding(BasisSettingsDefaults.GizmoHandGrip);

            PanelToggle toggleMouthEyeGizmo = PanelToggle.CreateNewEntry(container);
            toggleMouthEyeGizmo.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.mouthEye"));
            toggleMouthEyeGizmo.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.mouthEye.tooltip"));
            toggleMouthEyeGizmo.AssignBinding(BasisSettingsDefaults.GizmoMouthEye);

            PanelToggle toggleAudioRanges = PanelToggle.CreateNewEntry(container);
            toggleAudioRanges.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.audioRanges"));
            toggleAudioRanges.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.audioRanges.tooltip"));
            toggleAudioRanges.AssignBinding(BasisSettingsDefaults.GizmoAudioRanges);

            PanelToggle toggleAudioCone = PanelToggle.CreateNewEntry(container);
            toggleAudioCone.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.audioListenerCone"));
            toggleAudioCone.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.audioListenerCone.tooltip"));
            toggleAudioCone.AssignBinding(BasisSettingsDefaults.GizmoAudioListenerCone);

            PanelToggle toggleAudioLevels = PanelToggle.CreateNewEntry(container);
            toggleAudioLevels.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.audioLevels"));
            toggleAudioLevels.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.audioLevels.tooltip"));
            toggleAudioLevels.AssignBinding(BasisSettingsDefaults.GizmoAudioLevels);

            PanelToggle toggleNetworkSync = PanelToggle.CreateNewEntry(container);
            toggleNetworkSync.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.networkSync"));
            toggleNetworkSync.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.networkSync.tooltip"));
            toggleNetworkSync.AssignBinding(BasisSettingsDefaults.GizmoNetworkSync);

            PanelToggle toggleNetworkSyncBandwidth = PanelToggle.CreateNewEntry(container);
            toggleNetworkSyncBandwidth.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.networkSyncBandwidth"));
            toggleNetworkSyncBandwidth.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.networkSyncBandwidth.tooltip"));
            toggleNetworkSyncBandwidth.AssignBinding(BasisSettingsDefaults.GizmoNetworkSyncBandwidth);

            PanelToggle toggleNetworkPlayers = PanelToggle.CreateNewEntry(container);
            toggleNetworkPlayers.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.networkPlayers"));
            toggleNetworkPlayers.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.networkPlayers.tooltip"));
            toggleNetworkPlayers.AssignBinding(BasisSettingsDefaults.GizmoNetworkPlayers);

            PanelToggle toggleNetworkPlayersBandwidth = PanelToggle.CreateNewEntry(container);
            toggleNetworkPlayersBandwidth.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.networkPlayersBandwidth"));
            toggleNetworkPlayersBandwidth.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.networkPlayersBandwidth.tooltip"));
            toggleNetworkPlayersBandwidth.AssignBinding(BasisSettingsDefaults.GizmoNetworkPlayersBandwidth);

            PanelToggle toggleNetworkAdditionalInfo = PanelToggle.CreateNewEntry(container);
            toggleNetworkAdditionalInfo.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.networkAdditionalInfo"));
            toggleNetworkAdditionalInfo.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.networkAdditionalInfo.tooltip"));
            toggleNetworkAdditionalInfo.AssignBinding(BasisSettingsDefaults.GizmoNetworkAdditionalInfo);

            PanelToggle toggleGizmoLabels = PanelToggle.CreateNewEntry(container);
            toggleGizmoLabels.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.gizmoLabels"));
            toggleGizmoLabels.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.gizmoLabels.tooltip"));
            toggleGizmoLabels.AssignBinding(BasisSettingsDefaults.GizmoLabels);

            PanelToggle toggleGizmoDrawOnTop = PanelToggle.CreateNewEntry(container);
            toggleGizmoDrawOnTop.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.gizmoDrawOnTop"));
            toggleGizmoDrawOnTop.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.gizmoDrawOnTop.tooltip"));
            toggleGizmoDrawOnTop.AssignBinding(BasisSettingsDefaults.GizmoDrawOnTop);

            PanelToggle toggleGizmoAllCameras = PanelToggle.CreateNewEntry(container);
            toggleGizmoAllCameras.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.gizmoAllCameras"));
            toggleGizmoAllCameras.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.gizmoAllCameras.tooltip"));
            toggleGizmoAllCameras.AssignBinding(BasisSettingsDefaults.GizmoRenderInAllCameras);

            PanelSectionToggleHelpers.FinalizeBoxedSectionFromIndex(gizmosToggle, container, gizmosStart, false,
                _ => descriptor.ForceRebuild());

            // ---- Tracking & Calibration (eye/face tracking sources + calibration dev switches) ----
            PanelSectionToggle trackingToggle = PanelSectionToggle.CreateNewEntry(container);
            trackingToggle.SetTitle(BasisLocalization.Get("settings.developer.group.tracking"));
            int trackingStart = container.childCount;

            PanelToggle togglePreferOscEye = PanelToggle.CreateNewEntry(container);
            togglePreferOscEye.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.eyeTracking.preferOsc"));
            togglePreferOscEye.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.eyeTracking.preferOsc.tooltip"));
            togglePreferOscEye.AssignBinding(BasisSettingsDefaults.EyeTrackingPreferOsc);

            PanelToggle toggleAutoFoveation = PanelToggle.CreateNewEntry(container);
            toggleAutoFoveation.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.eyeTracking.autoFoveation"));
            toggleAutoFoveation.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.eyeTracking.autoFoveation.tooltip"));
            toggleAutoFoveation.AssignBinding(BasisSettingsDefaults.EyeFoveationAutoManage);

            PanelToggle toggleFaceTrackLipSync = PanelToggle.CreateNewEntry(container);
            toggleFaceTrackLipSync.Descriptor.SetTitle(BasisLocalization.Get("settings.main.title.disableLipSyncForFaceTrackedPlayers"));
            toggleFaceTrackLipSync.Descriptor.SetTooltip(BasisLocalization.Get("settings.main.title.disableLipSyncForFaceTrackedPlayers.tooltip"));
            toggleFaceTrackLipSync.Descriptor.SetDescription(BasisLocalization.Get("settings.main.title.disableLipSyncForFaceTrackedPlayers.description"));
            toggleFaceTrackLipSync.AssignBinding(BasisSettingsDefaults.DisableLipSyncForFaceTracking);

            PanelToggle toggleAlwaysShowCalibration = PanelToggle.CreateNewEntry(container);
            toggleAlwaysShowCalibration.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.alwaysShowCalibration"));
            toggleAlwaysShowCalibration.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.alwaysShowCalibration.tooltip"));
            toggleAlwaysShowCalibration.AssignBinding(BasisSettingsDefaults.DevAlwaysShowCalibration);

            PanelToggle toggleCalibrationCsv = PanelToggle.CreateNewEntry(container);
            toggleCalibrationCsv.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.calibrationCsv"));
            toggleCalibrationCsv.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.calibrationCsv.tooltip"));
            toggleCalibrationCsv.AssignBinding(BasisSettingsDefaults.DumpCalibrationCsv);

            PanelToggle toggleCalibrationDebug = PanelToggle.CreateNewEntry(container);
            toggleCalibrationDebug.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.calibrationDebug"));
            toggleCalibrationDebug.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.calibrationDebug.tooltip"));
            toggleCalibrationDebug.AssignBinding(BasisSettingsDefaults.DevShowCalibrationDebug);

            // Auto-estimate scale before calibrating: guess standing height from the live HMD so an uncalibrated
            // VR session is roughly the right size. Superseded the moment you calibrate. (Developer-only.)
            // PanelToggle toggleAutoScale = PanelToggle.CreateNewEntry(container);
            // toggleAutoScale.Descriptor.SetTitle("Auto-estimate scale (uncalibrated)");
            // toggleAutoScale.Descriptor.SetTooltip("Before you calibrate, guess your height from the headset so the avatar isn't wildly mis-scaled. A real calibration overrides it.");
            // toggleAutoScale.AssignBinding(BasisSettingsDefaults.AutoScaleEstimateEnabled);

            PanelSectionToggleHelpers.FinalizeBoxedSectionFromIndex(trackingToggle, container, trackingStart, false,
                _ => descriptor.ForceRebuild());

            // ---- Rendering & Shaders (VRS, shader pipeline switches, off-screen camera rates) ----
            PanelSectionToggle renderingToggle = PanelSectionToggle.CreateNewEntry(container);
            renderingToggle.SetTitle(BasisLocalization.Get("settings.developer.group.rendering"));
            int renderingStart = container.childCount;

            // Only Direct3D12 can do VRS at all, so on DX11/Vulkan/Metal the row is not built
            // rather than shown greyed out.
            if (BasisVariableRateShadingFeature.IsSupported)
            {
                PanelToggle toggleVrsDesktop = PanelToggle.CreateNewEntry(container);
                toggleVrsDesktop.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.vrs.desktop"));
                toggleVrsDesktop.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.vrs.desktop.tooltip"));
                toggleVrsDesktop.AssignBinding(BasisSettingsDefaults.DevVariableRateShadingDesktop);
            }

#if BASIS_HAS_RTAO && !UNITY_ANDROID
            PanelToggle toggleRtaoDebug = PanelToggle.CreateNewEntry(container);
            toggleRtaoDebug.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.rtaoDebug"));
            toggleRtaoDebug.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.rtaoDebug.tooltip"));
            toggleRtaoDebug.AssignBinding(BasisSettingsDefaults.DevRtaoDebugView);

            PanelDropdown dropdownRtaoStage = PanelDropdown.CreateNewEntry(container);
            dropdownRtaoStage.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.rtaoStage"));
            dropdownRtaoStage.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.rtaoStage.tooltip"));
            dropdownRtaoStage.AssignLocalizedEntries(
                new List<string> { "Final", "Raw", "Temporal", "Denoised", "Position", "Normal" },
                new List<string>
                {
                    "settings.developer.rtaoStage.final", "settings.developer.rtaoStage.raw",
                    "settings.developer.rtaoStage.temporal", "settings.developer.rtaoStage.denoised",
                    "settings.developer.rtaoStage.position", "settings.developer.rtaoStage.normal"
                });
            dropdownRtaoStage.AssignBinding(BasisSettingsDefaults.DevRtaoDebugStage);
#endif

#if BASIS_HAS_GI && !UNITY_ANDROID
            PanelDropdown dropdownGiDebug = PanelDropdown.CreateNewEntry(container);
            dropdownGiDebug.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.giDebug"));
            dropdownGiDebug.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.giDebug.tooltip"));
            dropdownGiDebug.AssignLocalizedEntries(
                new List<string> { "Off", "Indirect", "Obscurance", "Normals", "Ray Hits", "Indirect Only" },
                new List<string> { "ui.option.off", "settings.developer.giDebug.indirect", "settings.developer.giDebug.obscurance", "settings.developer.giDebug.normals", "settings.developer.giDebug.rayHits", "settings.developer.giDebug.indirectOnly" });
            dropdownGiDebug.AssignBinding(BasisSettingsDefaults.DevGiDebugView);
#endif

            PanelToggle togglePrewarm = PanelToggle.CreateNewEntry(container);
            togglePrewarm.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.shaderPrewarm"));
            togglePrewarm.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.shaderPrewarm.tooltip"));
            togglePrewarm.AssignBinding(BasisSettingsDefaults.EnableShaderPrewarm);

            PanelToggle toggleMaterialCorrection = PanelToggle.CreateNewEntry(container);
            toggleMaterialCorrection.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.materialCorrection"));
            toggleMaterialCorrection.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.materialCorrection.tooltip"));
            toggleMaterialCorrection.AssignBinding(BasisSettingsDefaults.EnableMaterialCorrection);

            PanelToggle toggleShaderBlocklist = PanelToggle.CreateNewEntry(container);
            toggleShaderBlocklist.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.shaderBlocklist"));
            toggleShaderBlocklist.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.shaderBlocklist.tooltip"));
            toggleShaderBlocklist.AssignBinding(BasisSettingsDefaults.EnableShaderBlocklist);

            PanelTextField blocklistPatternsField = PanelTextField.CreateNewEntry(container);
            blocklistPatternsField.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.shaderBlocklistPatterns"));
            blocklistPatternsField.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.shaderBlocklistPatterns.tooltip"));
            blocklistPatternsField.AssignBinding(BasisSettingsDefaults.ShaderBlocklistPatterns);
            TMP_InputField blocklistInput = blocklistPatternsField._inputField;
            if (blocklistInput != null)
            {
                blocklistInput.contentType = TMP_InputField.ContentType.Standard;
                blocklistInput.characterLimit = 0;
            }

            PanelToggle toggleGraphicsStatePrewarm = PanelToggle.CreateNewEntry(container);
            toggleGraphicsStatePrewarm.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.graphicsStatePrewarm"));
            toggleGraphicsStatePrewarm.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.graphicsStatePrewarm.tooltip"));
            toggleGraphicsStatePrewarm.AssignBinding(BasisSettingsDefaults.EnableGraphicsStatePrewarm);

            // Moved to the Camera Settings panel's Performance section (BasisHandHeldCameraPanelProvider).
            // PanelToggle toggleHandHeldRate = PanelToggle.CreateNewEntry(container);
            // toggleHandHeldRate.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.handheldCameraRate.limit"));
            // toggleHandHeldRate.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.handheldCameraRate.limit.tooltip"));
            // toggleHandHeldRate.AssignBinding(BasisSettingsDefaults.LimitHandHeldCameraRate);
            //
            // PanelSlider sliderHandHeldRate = PanelSlider.CreateEntryAndBind(
            //     container,
            //     new PanelSlider.SliderSettings(
            //         BasisLocalization.Get("settings.developer.handheldCameraRate"),
            //         BasisLocalization.Get("settings.developer.handheldCameraRate.description"),
            //         1, 120, true, 0, ValueDisplayMode.Hz),
            //     BasisSettingsDefaults.HandHeldCameraRenderHz);
            // sliderHandHeldRate.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.handheldCameraRate.tooltip"));

            PanelToggle toggleAvatarPreviewRate = PanelToggle.CreateNewEntry(container);
            toggleAvatarPreviewRate.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.avatarPreviewRate.limit"));
            toggleAvatarPreviewRate.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.avatarPreviewRate.limit.tooltip"));
            toggleAvatarPreviewRate.AssignBinding(BasisSettingsDefaults.LimitAvatarPreviewRate);

            PanelSlider sliderAvatarPreviewRate = PanelSlider.CreateEntryAndBind(
                container,
                new PanelSlider.SliderSettings(
                    BasisLocalization.Get("settings.developer.avatarPreviewRate"),
                    BasisLocalization.Get("settings.developer.avatarPreviewRate.description"),
                    1, 120, true, 0, ValueDisplayMode.Hz),
                BasisSettingsDefaults.AvatarPreviewRenderHz);
            sliderAvatarPreviewRate.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.avatarPreviewRate.tooltip"));

            PanelSectionToggleHelpers.FinalizeBoxedSectionFromIndex(renderingToggle, container, renderingStart, false,
                _ => descriptor.ForceRebuild());

            // ---- Network Tuning (advanced overrides; the user-facing knobs live on General) ----
            PanelSectionToggle networkToggle = PanelSectionToggle.CreateNewEntry(container);
            networkToggle.SetTitle(BasisLocalization.Get("settings.developer.group.network"));
            int networkStart = container.childCount;

            PanelToggle toggleJitterBufferOverride = PanelToggle.CreateNewEntry(container);
            toggleJitterBufferOverride.AssignBinding(BasisSettingsDefaults.NetworkJitterBufferOverride);
            toggleJitterBufferOverride.Descriptor.SetTitle(BasisLocalization.Get("settings.general.networking.jitterBufferOverride"));
            toggleJitterBufferOverride.Descriptor.SetTooltip(BasisLocalization.Get("settings.general.networking.jitterBufferOverride.tooltip"));

            PanelSlider sliderJitterBuffer = PanelSlider.CreateEntryAndBind(
                container,
                new PanelSlider.SliderSettings(
                    BasisLocalization.Get("settings.general.networking.jitterBuffer"),
                    BasisLocalization.Get("settings.general.networking.jitterBuffer.description"),
                    0, 6, true, 0, ValueDisplayMode.Raw),
                BasisSettingsDefaults.NetworkJitterBufferDepth);
            sliderJitterBuffer.Descriptor.SetTooltip(BasisLocalization.Get("settings.general.networking.jitterBuffer.tooltip"));

            sliderJitterBuffer.Descriptor.SetActive(toggleJitterBufferOverride.Value);
            toggleJitterBufferOverride.OnValueChanged += (val) =>
            {
                sliderJitterBuffer.Descriptor.SetActive(val);
                descriptor.ForceRebuild();
            };

            PanelSectionToggleHelpers.FinalizeBoxedSectionFromIndex(networkToggle, container, networkStart, false, visible =>
            {
                if (visible)
                {
                    sliderJitterBuffer.Descriptor.SetActive(toggleJitterBufferOverride.Value);
                }
                descriptor.ForceRebuild();
            });

            // ---- Logging & Notifications (log filters, stat feeds, diagnostic popups) ----
            PanelSectionToggle loggingToggle = PanelSectionToggle.CreateNewEntry(container);
            loggingToggle.SetTitle(BasisLocalization.Get("settings.developer.group.logging"));
            int loggingStart = container.childCount;

            PanelToggle toggleStatistics = PanelToggle.CreateNewEntry(container);
            toggleStatistics.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.enableStatistics"));
            toggleStatistics.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.enableStatistics.tooltip"));
            toggleStatistics.AssignBinding(BasisSettingsDefaults.EnableStatistics);

            PanelToggle toggleStreamingMeta = PanelToggle.CreateNewEntry(container);
            toggleStreamingMeta.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.streamingMeta"));
            toggleStreamingMeta.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.streamingMeta.tooltip"));
            toggleStreamingMeta.AssignBinding(BasisSettingsDefaults.EnableStreamingMeta);

            PanelTextField streamingMetaPortField = PanelTextField.CreateNewEntry(container);
            streamingMetaPortField.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.streamingMetaPort"));
            streamingMetaPortField.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.streamingMetaPort.tooltip"));
            streamingMetaPortField.AssignBinding(BasisSettingsDefaults.StreamingMetaPort);

            TMP_InputField streamingMetaPortInput = streamingMetaPortField._inputField;
            if (streamingMetaPortInput != null)
            {
                streamingMetaPortInput.contentType = TMP_InputField.ContentType.IntegerNumber;
                streamingMetaPortInput.lineType = TMP_InputField.LineType.SingleLine;
                streamingMetaPortInput.characterLimit = 5;
            }

            streamingMetaPortField.Descriptor.SetActive(toggleStreamingMeta.Value);
            toggleStreamingMeta.OnValueChanged += enabled =>
            {
                streamingMetaPortField.Descriptor.SetActive(enabled);
                descriptor.ForceRebuild();
            };

            PanelToggle toggleDisableLogging = PanelToggle.CreateNewEntry(container);
            toggleDisableLogging.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.disableLogging"));
            toggleDisableLogging.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.disableLogging.tooltip"));
            toggleDisableLogging.AssignBinding(BasisSettingsDefaults.DisableLogging);

            PanelDropdown dropdownLogTagFilter = PanelDropdown.CreateNewEntry(container);
            dropdownLogTagFilter.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.logTagFilter"));
            dropdownLogTagFilter.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.logTagFilter.tooltip"));
            List<string> tagEntries = new List<string> { BasisSettingsDefaults.DebugLogFilterAll };
            tagEntries.AddRange(Enum.GetNames(typeof(BasisDebug.LogTag)));
            dropdownLogTagFilter.AssignEntries(tagEntries);
            dropdownLogTagFilter.AssignBinding(BasisSettingsDefaults.DebugLogTagFilter);

            PanelDropdown dropdownLogLevelFilter = PanelDropdown.CreateNewEntry(container);
            dropdownLogLevelFilter.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.logLevelFilter"));
            dropdownLogLevelFilter.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.logLevelFilter.tooltip"));
            dropdownLogLevelFilter.AssignLocalizedEntries(
                new List<string>
                {
                    BasisSettingsDefaults.DebugLogFilterAll,
                    BasisSettingsDefaults.DebugLogLevelWarningsAndErrors,
                    BasisSettingsDefaults.DebugLogLevelErrorsOnly,
                },
                new List<string> { "settings.developer.logLevel.all", "settings.developer.logLevel.warningsErrors", "settings.developer.logLevel.errorsOnly" });
            dropdownLogLevelFilter.AssignBinding(BasisSettingsDefaults.DebugLogLevelFilter);

            PanelToggle toggleContentPoliceLogging = PanelToggle.CreateNewEntry(container);
            toggleContentPoliceLogging.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.contentPoliceLogging"));
            toggleContentPoliceLogging.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.contentPoliceLogging.tooltip"));
            toggleContentPoliceLogging.AssignBinding(BasisSettingsDefaults.ContentPoliceLogging);

            PanelToggle toggleExceptionNotifications = PanelToggle.CreateNewEntry(container);
            toggleExceptionNotifications.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.exceptionNotifications"));
            toggleExceptionNotifications.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.exceptionNotifications.tooltip"));
            toggleExceptionNotifications.AssignBinding(BasisSettingsDefaults.ExceptionNotifications);

            PanelToggle toggleErrorNotifications = PanelToggle.CreateNewEntry(container);
            toggleErrorNotifications.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.errorNotifications"));
            toggleErrorNotifications.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.errorNotifications.tooltip"));
            toggleErrorNotifications.AssignBinding(BasisSettingsDefaults.ErrorNotifications);

            PanelSectionToggleHelpers.FinalizeBoxedSectionFromIndex(loggingToggle, container, loggingStart, false, visible =>
            {
                if (visible)
                {
                    streamingMetaPortField.Descriptor.SetActive(toggleStreamingMeta.Value);
                }
                descriptor.ForceRebuild();
            });

            SettingsProviderSpawnAnchors.Build(container, descriptor);

            // ---- Package Tools (sections contributed by feature packages, e.g. Avatar Recorder) ----
            if (DeveloperSectionBuilders.Count > 0)
            {
                PanelSectionToggle sectionsToggle = PanelSectionToggle.CreateNewEntry(container);
                sectionsToggle.SetTitle(BasisLocalization.Get("settings.developer.group.packages"));
                int sectionsStart = container.childCount;

                for (int i = 0; i < DeveloperSectionBuilders.Count; i++)
                {
                    try { DeveloperSectionBuilders[i]?.Invoke(container); }
                    catch (Exception ex) { BasisDebug.LogWarning($"Developer section builder failed: {ex.Message}"); }
                }

                PanelSectionToggleHelpers.FinalizeBoxedSectionFromIndex(sectionsToggle, container, sectionsStart, false,
                    _ => descriptor.ForceRebuild());
            }

            // ---- Remote Player Debug (voice range readout + per-player audio and avatar overlays) ----
            PanelSectionToggle remoteDebugToggle = PanelSectionToggle.CreateNewEntry(container);
            remoteDebugToggle.SetTitle(BasisLocalization.Get("settings.developer.group.remoteDebug"));
            int remoteDebugStart = container.childCount;

            PanelToggle voiceRangeToggle = PanelToggle.CreateNewEntry(container);
            voiceRangeToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.voiceRange.enable"));
            voiceRangeToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.voiceRange.enable.tooltip"));
            voiceRangeToggle.AssignBinding(BasisSettingsDefaults.ShowVoiceRange);

            PanelElementDescriptor voiceRangeStatusField =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            voiceRangeStatusField.SetBackgroundVisible(false);
            voiceRangeStatusField.SetTitle(BasisLocalization.Get("settings.developer.voiceRange.status.title"));
            voiceRangeStatusField.SetDescription(BasisLocalization.Get("settings.developer.voiceRange.empty"));
            // Isolate only this live status field (it re-batches every tick via the updater below),
            // not the whole group — otherwise the toggle above is trapped on the nested canvas and
            // the pointer can't select it. See the recorder note above for the hit-test detail.
            voiceRangeStatusField.IsolateAsCanvas();

            void RefreshVoiceRangeVisibility(bool on)
            {
                voiceRangeStatusField.SetActive(on);
                if (on) BasisVoiceRangePanelUpdater.Attach(voiceRangeStatusField);
                else BasisVoiceRangePanelUpdater.Detach();
                descriptor.ForceRebuild();
            }
            RefreshVoiceRangeVisibility(voiceRangeToggle.Value);
            voiceRangeToggle.OnValueChanged += RefreshVoiceRangeVisibility;

            PanelToggle toggleAudioDebug = PanelToggle.CreateNewEntry(container);
            toggleAudioDebug.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.audioDebug.enable"));
            toggleAudioDebug.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.audioDebug.enable.tooltip"));
            toggleAudioDebug.AssignBinding(BasisSettingsDefaults.AudioDebugEnabled);

            PanelToggle toggleAudioSource = PanelToggle.CreateNewEntry(container);
            toggleAudioSource.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.audioDebug.source"));
            toggleAudioSource.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.audioDebug.source.tooltip"));
            toggleAudioSource.AssignBinding(BasisSettingsDefaults.AudioDebugShowSource);

            PanelToggle toggleVolumeChain = PanelToggle.CreateNewEntry(container);
            toggleVolumeChain.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.audioDebug.volumeChain"));
            toggleVolumeChain.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.audioDebug.volumeChain.tooltip"));
            toggleVolumeChain.AssignBinding(BasisSettingsDefaults.AudioDebugShowVolume);

            PanelToggle toggleRingBuffer = PanelToggle.CreateNewEntry(container);
            toggleRingBuffer.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.audioDebug.ringBuffer"));
            toggleRingBuffer.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.audioDebug.ringBuffer.tooltip"));
            toggleRingBuffer.AssignBinding(BasisSettingsDefaults.AudioDebugShowRingBuffer);

            PanelToggle toggleJitter = PanelToggle.CreateNewEntry(container);
            toggleJitter.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.audioDebug.jitter"));
            toggleJitter.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.audioDebug.jitter.tooltip"));
            toggleJitter.AssignBinding(BasisSettingsDefaults.AudioDebugShowJitter);

            PanelToggle toggleSilence = PanelToggle.CreateNewEntry(container);
            toggleSilence.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.audioDebug.silence"));
            toggleSilence.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.audioDebug.silence.tooltip"));
            toggleSilence.AssignBinding(BasisSettingsDefaults.AudioDebugShowSilence);

            PanelToggle toggleViseme = PanelToggle.CreateNewEntry(container);
            toggleViseme.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.audioDebug.viseme"));
            toggleViseme.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.audioDebug.viseme.tooltip"));
            toggleViseme.AssignBinding(BasisSettingsDefaults.AudioDebugShowViseme);

            // Hide per-section sub-toggles when the master is off — they don't drive
            // any rendering unless the master is on, so leaving them visible just
            // clutters the page.
            void RefreshAudioDebugSubVisibility(bool masterOn)
            {
                toggleAudioSource.Descriptor.SetActive(masterOn);
                toggleVolumeChain.Descriptor.SetActive(masterOn);
                toggleRingBuffer.Descriptor.SetActive(masterOn);
                toggleJitter.Descriptor.SetActive(masterOn);
                toggleSilence.Descriptor.SetActive(masterOn);
                toggleViseme.Descriptor.SetActive(masterOn);
                descriptor.ForceRebuild();
            }
            RefreshAudioDebugSubVisibility(toggleAudioDebug.Value);
            toggleAudioDebug.OnValueChanged += RefreshAudioDebugSubVisibility;

            PanelToggle toggleAvatarDataDebug = PanelToggle.CreateNewEntry(container);
            toggleAvatarDataDebug.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.avatarDataDebug.enable"));
            toggleAvatarDataDebug.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.avatarDataDebug.enable.tooltip"));
            toggleAvatarDataDebug.AssignBinding(BasisSettingsDefaults.AvatarDataDebugEnabled);

            PanelToggle toggleAvatarReceive = PanelToggle.CreateNewEntry(container);
            toggleAvatarReceive.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.avatarDataDebug.receive"));
            toggleAvatarReceive.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.avatarDataDebug.receive.tooltip"));
            toggleAvatarReceive.AssignBinding(BasisSettingsDefaults.AvatarDataDebugShowReceive);

            PanelToggle toggleAvatarStaging = PanelToggle.CreateNewEntry(container);
            toggleAvatarStaging.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.avatarDataDebug.staging"));
            toggleAvatarStaging.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.avatarDataDebug.staging.tooltip"));
            toggleAvatarStaging.AssignBinding(BasisSettingsDefaults.AvatarDataDebugShowStaging);

            PanelToggle toggleAvatarInterp = PanelToggle.CreateNewEntry(container);
            toggleAvatarInterp.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.avatarDataDebug.interp"));
            toggleAvatarInterp.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.avatarDataDebug.interp.tooltip"));
            toggleAvatarInterp.AssignBinding(BasisSettingsDefaults.AvatarDataDebugShowInterp);

            PanelToggle toggleAvatarMeta = PanelToggle.CreateNewEntry(container);
            toggleAvatarMeta.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.avatarDataDebug.meta"));
            toggleAvatarMeta.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.avatarDataDebug.meta.tooltip"));
            toggleAvatarMeta.AssignBinding(BasisSettingsDefaults.AvatarDataDebugShowMeta);

            void RefreshAvatarDataDebugSubVisibility(bool masterOn)
            {
                toggleAvatarReceive.Descriptor.SetActive(masterOn);
                toggleAvatarStaging.Descriptor.SetActive(masterOn);
                toggleAvatarInterp.Descriptor.SetActive(masterOn);
                toggleAvatarMeta.Descriptor.SetActive(masterOn);
                descriptor.ForceRebuild();
            }
            RefreshAvatarDataDebugSubVisibility(toggleAvatarDataDebug.Value);
            toggleAvatarDataDebug.OnValueChanged += RefreshAvatarDataDebugSubVisibility;

            PanelSectionToggleHelpers.FinalizeBoxedSectionFromIndex(remoteDebugToggle, container, remoteDebugStart, false, visible =>
            {
                if (visible)
                {
                    voiceRangeStatusField.SetActive(voiceRangeToggle.Value);
                    RefreshAudioDebugSubVisibility(toggleAudioDebug.Value);
                    RefreshAvatarDataDebugSubVisibility(toggleAvatarDataDebug.Value);
                }
                descriptor.ForceRebuild();
            });

            // ---- Tracking Diagnostics (face/eye tracking readouts + tracker role bindings) ----
            // The face / eye tracking section builders are owned by the comms
            // package because they reference HVR types the framework can't see;
            // the framework holds Action<RectTransform> hooks they register into.
            PanelSectionToggleHelpers.CreateLazyFlatSection(container,
                BasisLocalization.Get("settings.developer.group.trackingDiagnostics"), () =>
                {
                    PanelElementDescriptor faceGroup = CreateDiagnosticSubGroup(container,
                        BasisLocalization.Get("settings.developer.debugFaceTracking"));
                    if (FaceTrackingDebugBuilder == null)
                    {
                        faceGroup.SetDescription(BasisLocalization.Get("settings.developer.debugFaceTracking.unavailable"));
                    }
                    else
                    {
                        FaceTrackingDebugBuilder(faceGroup.ContentParent);
                    }

                    PanelElementDescriptor eyeGroup = CreateDiagnosticSubGroup(container,
                        BasisLocalization.Get("settings.developer.debugEyeTracking"));
                    if (EyeTrackingDebugBuilder == null)
                    {
                        eyeGroup.SetDescription(BasisLocalization.Get("settings.developer.debugEyeTracking.unavailable"));
                    }
                    else
                    {
                        EyeTrackingDebugBuilder(eyeGroup.ContentParent);
                    }

                    PanelElementDescriptor trackerRoles = CreateDiagnosticSubGroup(container,
                        BasisLocalization.Get("settings.developer.assignedTrackers"));
                    SettingsProviderAvatarStats.PopulateTrackerRoles(trackerRoles);
                }, false, _ => descriptor.ForceRebuild());

            // ---- System Info & Statistics (texture/VRAM, build environment, live network stats) ----
            PanelSectionToggleHelpers.CreateLazyFlatSection(container,
                BasisLocalization.Get("settings.developer.group.systemInfo"), () =>
                {
                    PanelElementDescriptor textureGroup = CreateDiagnosticSubGroup(container,
                        BasisLocalization.Get("settings.developer.textureStats"));
                    SettingsProviderAvatarStats.PopulateStatsInto(textureGroup.ContentParent);

                    PanelElementDescriptor buildGroup = CreateDiagnosticSubGroup(container,
                        BasisLocalization.Get("settings.developer.buildInfo"));
                    CreateBuildInfoSection(buildGroup.ContentParent);

                    SettingsProviderNetworkTab.BuildNetworkStatsGroup(container, out _);
                }, false, _ => descriptor.ForceRebuild());

#if BASIS_HAS_OPENVR || BASIS_HAS_OPENXR
            // ---- Platform Auto-Swap ----
            PanelSectionToggleHelpers.CreateLazyFlatSection(container,
                BasisLocalization.Get("settings.platform.swapMode.title"),
                () => SettingsProviderPlatform.BuildAutoSwapUI(container),
                false, _ => descriptor.ForceRebuild());
#endif

            // Backup & Restore moved to the General tab — it is user data, not a developer tool.

            // ---- Console Log ----
            PanelSectionToggleHelpers.CreateLazyFlatSection(container,
                BasisLocalization.Get("settings.developer.console"),
                () => SettingsProviderConsoleTab.BuildConsoleUI(container),
                false, _ => descriptor.ForceRebuild());

            // One reset button for this whole page
            RegisterPageReset("settings.tab.developer", ResetDeveloperDefaults);

            descriptor.ForceRebuild();
            return tab;
        }

        /// <summary>
        /// Identity (DID) card, shared by the General and Developer tabs.
        /// The user's DID/UUID is the long-lived id the server keys ban,
        /// permission, and content-share entries against. We render it through
        /// PanelPasswordField so the value is masked by default and the user
        /// has to tap the eye icon to reveal — same UX as a server password.
        /// Read-only because DIDs are persisted to PlayerPrefs and rotated
        /// through BasisDIDAuthIdentityClient, not edited inline.
        /// </summary>
        private static void BuildIdentitySection(RectTransform container, PanelElementDescriptor tabDescriptor)
        {
            PanelSectionToggle didSectionToggle = PanelSectionToggle.CreateNewEntry(container);
            didSectionToggle.SetTitle(BasisLocalization.Get("settings.developer.didKey.title"));
            int didStart = container.childCount;

            PanelPasswordField didField = PanelPasswordField.CreateNewEntry(container);
            didField.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.didKey.field"));
            if (didField._inputField != null) didField._inputField.readOnly = true;
            try
            {
                didField.SetPassword(BasisDIDAuthIdentityClient.GetOrSaveDID() ?? string.Empty);
            }
            catch (Exception ex)
            {
                BasisDebug.LogWarning($"Failed to load DID for settings panel: {ex.Message}");
                didField.SetPassword(string.Empty);
            }

            PanelSectionToggleHelpers.FinalizeBoxedSectionFromIndex(didSectionToggle, container, didStart, false,
                _ => tabDescriptor?.ForceRebuild());
        }

        /// <summary>
        /// Titled, background-free wrapper used to keep the merged diagnostic sections
        /// readable — each contributed readout keeps its own heading inside one section.
        /// </summary>
        private static PanelElementDescriptor CreateDiagnosticSubGroup(RectTransform container, string title)
        {
            PanelElementDescriptor group = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, container);
            group.SetBackgroundVisible(false);
            group.SetTitle(title);
            return group;
        }

        private static void ResetDeveloperDefaults()
        {
            BasisSettingsDefaults.DevVariableRateShadingDesktop.ResetToDefault();
            BasisSettingsDefaults.DevGiDebugView.ResetToDefault();
            BasisSettingsDefaults.ExceptionNotifications.ResetToDefault();
            BasisSettingsDefaults.ErrorNotifications.ResetToDefault();
            BasisSettingsDefaults.ShowGizmos.ResetToDefault();
            BasisSettingsDefaults.GizmoSkeletonLines.ResetToDefault();
            BasisSettingsDefaults.GizmoCalibrationSpheres.ResetToDefault();
            BasisSettingsDefaults.GizmoJiggleVisuals.ResetToDefault();
            BasisSettingsDefaults.TrackerGizmos.ResetToDefault();
            BasisSettingsDefaults.LinkedTrackerLines.ResetToDefault();
            BasisSettingsDefaults.GizmoEyeGaze.ResetToDefault();
            BasisSettingsDefaults.GizmoIKColliders.ResetToDefault();
            BasisSettingsDefaults.GizmoPointerRay.ResetToDefault();
            BasisSettingsDefaults.GizmoHintOffsets.ResetToDefault();
            BasisSettingsDefaults.GizmoFootPlacement.ResetToDefault();
            BasisSettingsDefaults.GizmoInteractionHover.ResetToDefault();
            BasisSettingsDefaults.GizmoFingerTouch.ResetToDefault();
            BasisSettingsDefaults.GizmoSeatTargets.ResetToDefault();
            BasisSettingsDefaults.GizmoJiggleGrab.ResetToDefault();
            BasisSettingsDefaults.GizmoHandGrip.ResetToDefault();
            BasisSettingsDefaults.GizmoMouthEye.ResetToDefault();
            BasisSettingsDefaults.GizmoAudioRanges.ResetToDefault();
            BasisSettingsDefaults.GizmoAudioListenerCone.ResetToDefault();
            BasisSettingsDefaults.GizmoAudioLevels.ResetToDefault();
            BasisSettingsDefaults.GizmoNetworkSync.ResetToDefault();
            BasisSettingsDefaults.GizmoNetworkSyncBandwidth.ResetToDefault();
            BasisSettingsDefaults.GizmoNetworkPlayers.ResetToDefault();
            BasisSettingsDefaults.GizmoNetworkPlayersBandwidth.ResetToDefault();
            BasisSettingsDefaults.GizmoNetworkAdditionalInfo.ResetToDefault();
            BasisSettingsDefaults.GizmoLabels.ResetToDefault();
            BasisSettingsDefaults.GizmoDrawOnTop.ResetToDefault();
            BasisSettingsDefaults.GizmoRenderInAllCameras.ResetToDefault();
            BasisSettingsDefaults.AvatarRangeIndicator.ResetToDefault();
            BasisSettingsDefaults.HearingRangeIndicator.ResetToDefault();
            BasisSettingsDefaults.MicrophoneRangeIndicator.ResetToDefault();
            BasisSettingsDefaults.EnableStatistics.ResetToDefault();
            BasisSettingsDefaults.ShowVoiceRange.ResetToDefault();
            BasisSettingsDefaults.EnableStreamingMeta.ResetToDefault();
            BasisSettingsDefaults.StreamingMetaPort.ResetToDefault();
            BasisSettingsDefaults.DisableLogging.ResetToDefault();
            BasisSettingsDefaults.ContentPoliceLogging.ResetToDefault();
            BasisSettingsDefaults.DumpCalibrationCsv.ResetToDefault();
            BasisSettingsDefaults.DevShowCalibrationDebug.ResetToDefault();
            BasisSettingsDefaults.DevAlwaysShowCalibration.ResetToDefault();
            BasisSettingsDefaults.AutoScaleEstimateEnabled.ResetToDefault();
            BasisSettingsDefaults.EnableShaderPrewarm.ResetToDefault();
            BasisSettingsDefaults.EnableMaterialCorrection.ResetToDefault();
            BasisSettingsDefaults.EnableShaderBlocklist.ResetToDefault();
            BasisSettingsDefaults.ShaderBlocklistPatterns.ResetToDefault();
            BasisSettingsDefaults.EnableGraphicsStatePrewarm.ResetToDefault();
            BasisSettingsDefaults.LimitHandHeldCameraRate.ResetToDefault();
            BasisSettingsDefaults.HandHeldCameraRenderHz.ResetToDefault();
            BasisSettingsDefaults.LimitAvatarPreviewRate.ResetToDefault();
            BasisSettingsDefaults.AvatarPreviewRenderHz.ResetToDefault();
            BasisSettingsDefaults.AudioDebugEnabled.ResetToDefault();
            BasisSettingsDefaults.DisableLipSyncForFaceTracking.ResetToDefault();
            BasisSettingsDefaults.AudioDebugShowSource.ResetToDefault();
            BasisSettingsDefaults.AudioDebugShowVolume.ResetToDefault();
            BasisSettingsDefaults.AudioDebugShowRingBuffer.ResetToDefault();
            BasisSettingsDefaults.AudioDebugShowJitter.ResetToDefault();
            BasisSettingsDefaults.AudioDebugShowSilence.ResetToDefault();
            BasisSettingsDefaults.AudioDebugShowViseme.ResetToDefault();
            BasisSettingsDefaults.EyeTrackingPreferOsc.ResetToDefault();
            BasisSettingsDefaults.EyeFoveationAutoManage.ResetToDefault();
            BasisSettingsDefaults.SwapMode.ResetToDefault();
            BasisSettingsDefaults.SpawnAnchorHandles.ResetToDefault();
            BasisSettingsDefaults.SpawnAnchorSeatOnSurface.ResetToDefault();
            BasisSettingsDefaults.SpawnAnchorPositionSnap.ResetToDefault();
            BasisSettingsDefaults.SpawnAnchorPositionSnapSize.ResetToDefault();
            BasisSettingsDefaults.SpawnAnchorRotationSnap.ResetToDefault();
            BasisSettingsDefaults.SpawnAnchorRotationSnapDegrees.ResetToDefault();

            for (int i = 0; i < DeveloperResetActions.Count; i++)
            {
                try { DeveloperResetActions[i]?.Invoke(); }
                catch (Exception ex) { BasisDebug.LogWarning($"Developer reset action failed: {ex.Message}"); }
            }
        }

        private static void CreateBuildInfoSection(RectTransform parent)
        {
            PanelButton copyAll = PanelButton.CreateNew(parent);
            copyAll.Descriptor.SetTitle(BasisLocalization.Get("settings.main.title.copyBuildInfo"));
            copyAll.Descriptor.SetDescription(BasisLocalization.Get("settings.main.title.copyBuildInfo.description"));
            copyAll.OnClicked += () =>
            {
                BasisClipboard.Copy(BuildInfoString(), copyAll);
                BasisDebug.Log("Copied build info to clipboard.");
            };

            AddInfoRow(parent, "Version", Application.version);
            AddInfoRow(parent, "Unity", Application.unityVersion);
            AddInfoRow(parent, "Platform", Application.platform.ToString());
            AddInfoRow(parent, "Mode", BasisDeviceManagement.StaticCurrentMode.ToString());
            AddInfoRow(parent, "Build GUID", Application.buildGUID);
            AddInfoRow(parent, "Log Path", Application.consoleLogPath, false);
            AddInfoRow(parent, "Data Path", Application.dataPath, false);
        }

        private static PanelPasswordField AddInfoRow(RectTransform parent, string title, string value, bool ShownByDefault = true)
        {
            PanelPasswordField Password = PanelPasswordField.CreateNew(parent);
            Password.SetPassword(value);
            Password.SetValueWithoutNotify(ShownByDefault);
            Password.Descriptor.SetTitle(title);
            Password.Descriptor.SetDescription(string.Empty);
            return Password;
        }

        public static string BuildInfoString()
        {
            return
                $"Version: {Application.version}\n" +
                $"Unity: {Application.unityVersion}\n" +
                $"Platform: {Application.platform}\n" +
                $"Mode: {BasisDeviceManagement.StaticCurrentMode}\n" +
                $"Build GUID: {Application.buildGUID}\n";
        }
    }
}
