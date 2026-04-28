using Basis.Scripts.Device_Management;
using Basis.Scripts.Networking;
using BasisPermissions;
using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

namespace Basis.BasisUI
{
    public partial class SettingsProvider : BasisMenuActionProvider<BasisMainMenu>
    {
        private static string _pendingTabKey;
        private static string _lastSelectedTabKey;

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
        /// When set by an external package, replaces the default My Avatar tab builder.
        /// </summary>
        public static Func<PanelTabGroup, PanelTabPage> MyAvatarTabOverride;

        [RuntimeInitializeOnLoadMethod]
        public static void AddToMenu()
        {
            BasisMenuBase<BasisMainMenu>.AddProvider(new SettingsProvider());
#if !BASIS_DISABLE_MICROPHONE
            SMDMicrophone.OnMicrophoneSettingsChanged += SyncUiFromSnapshot;
#endif
            ApplyOpenLipSyncMaxSlots();
            BasisSettingsSystem.OnSettingsFinishedChanges += ApplyOpenLipSyncMaxSlots;
        }

        private static void ApplyOpenLipSyncMaxSlots()
        {
            BasisOpenLipSyncDriver.UseSlotLimit = BasisSettingsDefaults.UseOpenLipSyncLimit.RawValue;
            BasisOpenLipSyncDriver.MaxSlots = Mathf.Max(0, (int)BasisSettingsDefaults.OpenLipSyncMaxSlots.RawValue);
            BasisOpenLipSyncDriver.EnforceSlotLimit();
        }

        public const string StaticTitleKey = "settings.title";
        public static string StaticTitle => BasisLocalization.Get(StaticTitleKey);
        public override string Title => StaticTitle;
        public override string IconAddress => AddressableAssets.Sprites.Settings;
        public override int Order => 0;
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
                button?.OnClicked?.Invoke();
            }
        }

        public override void RunAction()
        {
            if (BasisMainMenu.ActiveMenuTitle == Title) return;

            BasisMenuPanel panel = BasisMainMenu.CreateActiveMenu(
                BasisMenuPanel.PanelData.Standard(Title),
                BasisMenuPanel.PanelStyles.Page);

            TextMeshProUGUI TitleLabel = panel.Descriptor.TitleLabel;
            BasisFrameRateVisualization FRV = TitleLabel.gameObject.AddComponent<BasisFrameRateVisualization>();
            FRV.Title = Title;
            FRV.fpsText = TitleLabel;

            BoundButton?.BindActiveStateToAddressablesInstance(panel);

            PanelTabGroup tabGroup = PanelTabGroup.CreateNew(panel.Descriptor.ContentParent, LayoutDirection.Vertical);
            _tabKeyToIndex.Clear();

            // First tab is eager (shown immediately on open)
            const string generalKey = "settings.tab.general";
            _tabKeyToIndex[generalKey] = 0;
            tabGroup.AddTab(BasisLocalization.Get(generalKey), () => _lastSelectedTabKey = generalKey, GeneralTab(tabGroup));
            // Remaining tabs are lazy-loaded on first selection to reduce stuttering
            AddLazyTab(tabGroup, "settings.tab.audio", () => AudioTab(tabGroup));
            AddLazyTab(tabGroup, "settings.tab.microphone", () => MicrophoneTab(tabGroup));
            AddLazyTab(tabGroup, "settings.tab.graphics", () => GraphicsTab(tabGroup));
            AddLazyTab(tabGroup, "settings.tab.controls", () => SettingsProviderControllerConfig.OpenControllerConfig(tabGroup));
            AddLazyTab(tabGroup, "settings.tab.chat", () => ChatTab(tabGroup));
            AddLazyTab(tabGroup, "settings.tab.bodytracking", () => SettingsProviderIK.IKTab(tabGroup));
            AddLazyTab(tabGroup, "settings.tab.myavatar", () =>
                MyAvatarTabOverride != null
                    ? MyAvatarTabOverride(tabGroup)
                    : SettingsProviderAvatarStats.AvatarStatsTab(tabGroup));
            AddLazyTab(tabGroup, "settings.tab.downloadscache", () => SettingsProviderStorage.StorageTab(tabGroup));
            AddLazyTab(tabGroup, "settings.tab.trustedurls", () => SettingsProviderTrustedUrls.TrustedUrlsTab(tabGroup));
          //  AddLazyTab(tabGroup, "settings.tab.uistyle", () => SettingsProviderUIStyle.UIStyleTab(tabGroup));
            AddLazyTab(tabGroup, "settings.tab.developer", () => DeveloperTab(tabGroup));

            // External package tabs (registered via SettingsProvider.ExternalTabs).
            // TabName is treated as a localization key — packages that don't localize
            // can still register with a plain English string, which falls back to itself.
            for (int i = 0; i < ExternalTabs.Count; i++)
            {
                var ext = ExternalTabs[i];
                AddLazyTab(tabGroup, ext.TabName, () => ext.Builder(tabGroup));
            }

            if (BasisNetworkManagement.LocalPermissions.Contains(PermNodes.PermissionsView))
            {
                AddLazyTab(tabGroup, "settings.tab.admin", () => SettingsProviderAdminTab.AdminTab(tabGroup));
            }

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

        /// <summary>
        /// Adds a tab with an empty placeholder page. On first selection the real
        /// content is built, the placeholder is released, and the Pages entry is swapped.
        /// <paramref name="tabKey"/> is the localization key used both for the
        /// displayed label and for stable navigation across language changes.
        /// </summary>
        private static void AddLazyTab(PanelTabGroup tabGroup, string tabKey, Func<PanelTabPage> builder)
        {
            PanelTabPage placeholder = PanelTabPage.CreateVertical(tabGroup.Descriptor.ContentParent);
            int index = tabGroup.Pages.Count;
            _tabKeyToIndex[tabKey] = index;
            bool built = false;

            tabGroup.AddTab(BasisLocalization.Get(tabKey), () =>
            {
                _lastSelectedTabKey = tabKey;

                if (built) return;
                built = true;

                PanelTabPage realPage = builder();
                tabGroup.Pages[index] = realPage;
                placeholder.ReleaseInstance();
            }, placeholder);
        }


        // ------------------
        // RESET BUTTON HELPERS (ONE PER PAGE)
        // ------------------
        /// <summary>
        /// Adds a "Reset &lt;page name&gt;" button that, on confirm, runs
        /// <paramref name="resetAction"/>, closes the menu, and reopens Settings
        /// on the same tab. <paramref name="tabKey"/> is the localization key
        /// registered via <see cref="AddLazyTab"/>; it's used both to resolve
        /// the page label and to navigate back after the reset.
        /// </summary>
        public static void AddResetPageButton(RectTransform parent, string tabKey, Action resetAction)
        {
            string pageName = BasisLocalization.Get(tabKey);

            PanelButton reset = PanelButton.CreateNew(parent);
            reset.Descriptor.SetTitle(BasisLocalization.Get("ui.resetPage.title", pageName));
            reset.Descriptor.SetDescription(BasisLocalization.Get("ui.resetPage.description"));
            reset.OnClicked += () =>
            {
                BasisMainMenu.Instance.OpenDialogue(
                    BasisLocalization.Get("ui.resetPage.title", pageName),
                    BasisLocalization.Get("ui.resetPage.confirm", pageName),
                    BasisLocalization.Get("ui.reset"),
                    BasisLocalization.Get("ui.cancel"),
                    value =>
                    {
                        if (!value)
                        {
                            return;
                        }

                        resetAction?.Invoke();
                        BasisMainMenu.Close();
                        OpenToTab(tabKey);
                    });
            };
        }

        // ------------------
        // GENERAL TAB (ONE RESET BUTTON)
        // ------------------
        private static void BuildSettingsSearch(RectTransform container, PanelTabGroup tabGroup)
        {
            PanelTextField searchField = PanelTextField.CreateNewEntry(container);
            searchField.Descriptor.SetTitle("Search Menus");
            searchField.Descriptor.SetDescription("Type to find a settings menu, then click a result to open it.");

            PanelElementDescriptor resultsGroup = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, container);
            resultsGroup.SetTitle("Results");
            resultsGroup.SetActive(false);

            List<PanelButton> resultButtons = new List<PanelButton>();

            searchField.OnValueChanged += query =>
            {
                for (int i = 0; i < resultButtons.Count; i++)
                {
                    PanelButton existing = resultButtons[i];
                    if (existing != null && !existing.IsReleased)
                    {
                        existing.ReleaseInstance();
                    }
                }
                resultButtons.Clear();

                string filter = query == null ? string.Empty : query.Trim().ToLowerInvariant();
                if (filter.Length == 0)
                {
                    resultsGroup.SetActive(false);
                    return;
                }

                for (int i = 0; i < tabGroup.SelectionButtons.Count; i++)
                {
                    PanelButton tabButton = tabGroup.SelectionButtons[i];
                    if (tabButton == null || tabButton.Descriptor == null || tabButton.Descriptor.TitleLabel == null) continue;

                    // Skip the General tab itself — user is already here.
                    if (i == 0) continue;

                    string tabTitle = tabButton.Descriptor.TitleLabel.text;
                    if (string.IsNullOrEmpty(tabTitle)) continue;
                    if (!tabTitle.ToLowerInvariant().Contains(filter)) continue;

                    PanelButton resultButton = PanelButton.CreateNew(resultsGroup.ContentParent);
                    resultButton.Descriptor.SetTitle(tabTitle);
                    PanelButton capturedTabButton = tabButton;
                    resultButton.OnClicked += () => capturedTabButton.OnClicked?.Invoke();
                    resultButtons.Add(resultButton);
                }

                resultsGroup.SetActive(resultButtons.Count > 0);
            };
        }

        public static PanelTabPage GeneralTab(PanelTabGroup tabGroup)
        {
            PanelTabPage tab = PanelTabPage.CreateVertical(tabGroup.Descriptor.ContentParent);
            PanelElementDescriptor descriptor = tab.Descriptor;
            descriptor.SetIcon(AddressableAssets.Sprites.Settings);
            descriptor.SetTitle(BasisLocalization.Get("settings.general.title"));

            RectTransform container = descriptor.ContentParent;

            // BuildSettingsSearch(container, tabGroup); // disabled — UI needs visual polish before re-enabling

            SettingsProviderPlatform.BuildDeviceModeUI(container);

            BuildLanguageSelector(container);

            // Range / visibility / audio-source-limit settings moved out of General:
            //   Avatar Range / Limit Avatars / View Cone Avatars → Graphics
            //   Hearing Range / Limit Audio Sources              → Audio
            //   Microphone Range                                 → Microphone

            PanelElementDescriptor interactionsGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            interactionsGroup.SetTitle(BasisLocalization.Get("settings.general.interactions.title"));

            PanelToggle toggleDisableSeats = PanelToggle.CreateNewEntry(interactionsGroup);
            toggleDisableSeats.AssignBinding(BasisSettingsDefaults.DisableSeats);
            toggleDisableSeats.Descriptor.SetTitle(BasisLocalization.Get("settings.general.disableSeats"));
            toggleDisableSeats.Descriptor.SetDescription(BasisLocalization.Get("settings.general.disableSeats.description"));

            // One reset button for this whole page
            AddResetPageButton(container, "settings.tab.general", ResetGeneralDefaults);
            descriptor.ForceRebuild();
            return tab;
        }

        /// <summary>
        /// Builds the Language dropdown in the General tab. Each entry shows
        /// the native name (e.g. "日本語"); on selection the choice is persisted
        /// via BasisLocalization.SetLanguage and the menu is reopened so every
        /// string re-resolves against the new table.
        /// </summary>
        private static void BuildLanguageSelector(RectTransform container)
        {
            PanelElementDescriptor languageGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            languageGroup.SetTitle(BasisLocalization.Get("settings.general.language.title"));
            languageGroup.SetDescription(BasisLocalization.Get("settings.general.language.description"));

            PanelDropdown dropdownLanguage = PanelDropdown.CreateNewEntry(languageGroup);
            dropdownLanguage.Descriptor.SetTitle(BasisLocalization.Get("settings.general.language.title"));

            var languages = BasisLocalization.Available;
            var displayNames = new List<string>(languages.Count);
            int currentIndex = 0;
            for (int i = 0; i < languages.Count; i++)
            {
                displayNames.Add(languages[i].NativeName);
                if (languages[i].Code == BasisLocalization.CurrentLanguage)
                {
                    currentIndex = i;
                }
            }

            dropdownLanguage.AssignEntries(displayNames);
            if (displayNames.Count > 0)
            {
                dropdownLanguage.SetValueWithoutNotify(displayNames[currentIndex]);
            }

            dropdownLanguage.OnValueChanged += (selected) =>
            {
                for (int i = 0; i < languages.Count; i++)
                {
                    if (languages[i].NativeName == selected)
                    {
                        BasisSettingsDefaults.Language.SetValue(languages[i].Code);
                        BasisLocalization.SetLanguage(languages[i].Code);
                        BasisMainMenu.Close();
                        OpenToTab("settings.tab.general");
                        return;
                    }
                }
            };

            PanelElementDescriptor helpTranslateGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            helpTranslateGroup.SetTitle(BasisLocalization.Get("settings.general.language.help_translate.title"));
            helpTranslateGroup.SetDescription(BasisLocalization.Get("settings.general.language.help_translate.description"));
        }

        private static void ResetGeneralDefaults()
        {
            BasisSettingsDefaults.AvatarPreview.ResetToDefault();
            BasisSettingsDefaults.DisableSeats.ResetToDefault();
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
            PanelElementDescriptor mixerGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            mixerGroup.SetTitle(BasisLocalization.Get("settings.audio.mixer.title"));
            mixerGroup.SetDescription(BasisLocalization.Get("settings.audio.mixer.description"));

            PanelSlider sliderMainVolume = PanelSlider.CreateEntryAndBind(
                mixerGroup,
                PanelSlider.SliderSettings.Percentage(BasisLocalization.Get("settings.audio.mainVolume")),
                BasisSettingsDefaults.MainVolume);
            sliderMainVolume.Descriptor.SetTitle(BasisLocalization.Get("settings.audio.masterVolume"));
            sliderMainVolume.Descriptor.SetDescription(BasisLocalization.Get("settings.audio.masterVolume.description"));

            PanelSlider sliderMenuVolume = PanelSlider.CreateEntryAndBind(
                mixerGroup,
                PanelSlider.SliderSettings.Percentage(BasisLocalization.Get("settings.audio.menuVolume")),
                BasisSettingsDefaults.MenuVolume);

            PanelSlider sliderWorldVolume = PanelSlider.CreateEntryAndBind(
                mixerGroup,
                PanelSlider.SliderSettings.Percentage(BasisLocalization.Get("settings.audio.worldVolume")),
                BasisSettingsDefaults.WorldVolume);

            PanelSlider sliderVideoVolume = PanelSlider.CreateEntryAndBind(
                mixerGroup,
                PanelSlider.SliderSettings.Percentage(BasisLocalization.Get("settings.audio.mediaVolume")),
                BasisSettingsDefaults.MediaVolume);

            PanelSlider sliderVoiceVolume = PanelSlider.CreateEntryAndBind(
                mixerGroup,
                PanelSlider.SliderSettings.Percentage(BasisLocalization.Get("settings.audio.voiceVolume")),
                BasisSettingsDefaults.VoiceVolume);

            PanelSlider sliderAvatarVolume = PanelSlider.CreateEntryAndBind(
                mixerGroup,
                PanelSlider.SliderSettings.Percentage(BasisLocalization.Get("settings.audio.avatarVolume")),
                BasisSettingsDefaults.AvatarVolume);

            PanelSlider sliderPropVolume = PanelSlider.CreateEntryAndBind(
                mixerGroup,
                PanelSlider.SliderSettings.Percentage(BasisLocalization.Get("settings.audio.propVolume")),
                BasisSettingsDefaults.PropVolume);

            // Remote Players (Spatial Audio) — also hosts Hearing Range and the
            // Audio Source cap, since both are "how do I hear other players" controls.
            SettingsProviderRemoteAudio.BuildRemoteAudioUI(container);

            // One reset button for this whole page
            AddResetPageButton(container, "settings.tab.audio", ResetAudioDefaults);
            descriptor.ForceRebuild();
            return tab;
        }

        private static void ResetAudioDefaults()
        {
            BasisSettingsDefaults.MainVolume.ResetToDefault();
            BasisSettingsDefaults.MenuVolume.ResetToDefault();
            BasisSettingsDefaults.WorldVolume.ResetToDefault();
            BasisSettingsDefaults.MediaVolume.ResetToDefault();
            BasisSettingsDefaults.VoiceVolume.ResetToDefault();
            BasisSettingsDefaults.AvatarVolume.ResetToDefault();
            BasisSettingsDefaults.PropVolume.ResetToDefault();
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

#if !BASIS_DISABLE_MICROPHONE
            // Snapshot
            SMDMicrophone.MicSettings snap = SMDMicrophone.Current;

            // MICROPHONE GROUP
            PanelElementDescriptor microphoneGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            microphoneGroup.SetTitle(BasisLocalization.Get("settings.microphone.group.title"));
            microphoneGroup.SetDescription(BasisLocalization.Get("settings.microphone.group.description"));

            // Microphone Volume (0..1)
            sliderMicrophoneVolume = PanelSlider.CreateEntryAndBind(
               microphoneGroup,
               PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.microphone.volume"), 0, 1, false, 4, ValueDisplayMode.Percentage),
               BasisSettingsDefaults.MicrophoneVolume);
            sliderMicrophoneVolume.SetValueWithoutNotify(snap.Volume01);

            void MicrophoneVolumeChanged(float value)
            {
                if (SMDMicrophone.CurrentMode != BasisDeviceManagement.StaticCurrentMode)
                    SMDMicrophone.LoadInMicrophoneData(BasisDeviceManagement.StaticCurrentMode);

                SMDMicrophone.SetVolume(value);
            }
            sliderMicrophoneVolume.SliderComponent.onValueChanged.AddListener(MicrophoneVolumeChanged);

            BasisLocalVolumeMeterUIDescriptor volumeMeter =
                BasisLocalVolumeMeterUIDescriptor.CreateNew(
                    BasisLocalVolumeMeterUIDescriptor.ElementStyles.Horizontal,
                    microphoneGroup.ContentParent);

            // Microphone Selection (device list)
            dropdownMicrophoneSelection = PanelDropdown.CreateNewEntry(microphoneGroup);
            dropdownMicrophoneSelection.Descriptor.SetTitle(BasisLocalization.Get("settings.microphone.selection"));
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
                PanelSlider.SliderSettings.Distance(BasisLocalization.Get("settings.general.microphoneRange"), 25),
                BasisSettingsDefaults.MicrophoneRange);

            PanelToggle toggleMicrophoneDenoiser = PanelToggle.CreateNewEntry(microphoneGroup);
            toggleMicrophoneDenoiser.Descriptor.SetTitle(BasisLocalization.Get("settings.microphone.denoiser"));
            toggleMicrophoneDenoiser.AssignBinding(BasisSettingsDefaults.MicrophoneDenoiser);

            PanelToggle toggleAGC = PanelToggle.CreateNewEntry(microphoneGroup);
            toggleAGC.Descriptor.SetTitle(BasisLocalization.Get("settings.microphone.agc"));
            toggleAGC.AssignBinding(BasisSettingsDefaults.UseAutomaticGain);

            PanelDropdown dropdownMicrophoneMode = PanelDropdown.CreateNewEntry(microphoneGroup);
            dropdownMicrophoneMode.Descriptor.SetTitle(BasisLocalization.Get("settings.microphone.mode"));
            dropdownMicrophoneMode.AssignEntries(new List<string>
            {
                "On Activation",
                "Push To Talk"
            });
            dropdownMicrophoneMode.AssignBinding(BasisSettingsDefaults.MicrophoneMode);

            PanelDropdown dropdownMicrophoneIcon = PanelDropdown.CreateNewEntry(microphoneGroup);
            dropdownMicrophoneIcon.Descriptor.SetTitle(BasisLocalization.Get("settings.microphone.icon"));
            dropdownMicrophoneIcon.AssignEntries(new List<string>
            {
                "AlwaysVisible",
                "ActivityDetection",
                "Hidden"
            });
            dropdownMicrophoneIcon.AssignBinding(BasisSettingsDefaults.MicrophoneIcon);

            PanelDropdown dropdownMicStartBehavior = PanelDropdown.CreateNewEntry(microphoneGroup);
            dropdownMicStartBehavior.Descriptor.SetTitle(BasisLocalization.Get("settings.microphone.startBehavior"));
            dropdownMicStartBehavior.AssignEntries(new List<string>
            {
                BasisLocalMicrophoneDriver.SettingStartOff,
                BasisLocalMicrophoneDriver.SettingStartOn,
                BasisLocalMicrophoneDriver.SettingStartRememberLast,
            });
            dropdownMicStartBehavior.AssignBinding(BasisSettingsDefaults.MicStartBehavior);

            // -------------------- DSP SETTINGS --------------------

            // Limiter
            PanelElementDescriptor limiterGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            limiterGroup.SetTitle(BasisLocalization.Get("settings.microphone.limiter.title"));
            limiterGroup.SetDescription(BasisLocalization.Get("settings.microphone.limiter.description"));

            sliderLimitThreshold = PanelSlider.CreateEntryAndBind(
               limiterGroup,
               PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.microphone.limiter.threshold"), 0f, 1f, false, 3, ValueDisplayMode.Percentage),
               BasisSettingsDefaults.LimitThreshold);
            sliderLimitThreshold.SetValueWithoutNotify(snap.LimitThreshold);

            sliderLimitKnee = PanelSlider.CreateEntryAndBind(
               limiterGroup,
               PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.microphone.limiter.knee"), 0f, 1f, false, 3, ValueDisplayMode.Percentage),
               BasisSettingsDefaults.LimitKnee);
            sliderLimitKnee.SetValueWithoutNotify(snap.LimitKnee);

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
            sliderLimitThreshold.SliderComponent.onValueChanged.AddListener(LimitThresholdChanged);
            sliderLimitKnee.SliderComponent.onValueChanged.AddListener(LimitKneeChanged);

            // Denoiser tuning
            PanelElementDescriptor denoiseGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            denoiseGroup.SetTitle(BasisLocalization.Get("settings.microphone.denoiser.title"));
            denoiseGroup.SetDescription(BasisLocalization.Get("settings.microphone.denoiser.description"));

            sliderDenoiseWet = PanelSlider.CreateEntryAndBind(
               denoiseGroup,
               PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.microphone.denoiser.wet"), 0f, 1f, false, 3, ValueDisplayMode.Percentage),
               BasisSettingsDefaults.DenoiseWet);
            sliderDenoiseWet.SetValueWithoutNotify(snap.DenoiseWet);

            sliderDenoiseMakeup = PanelSlider.CreateEntryAndBind(
               denoiseGroup,
               PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.microphone.denoiser.makeup"), -12f, 24f, false, 2, ValueDisplayMode.Raw),
               BasisSettingsDefaults.DenoiseMakeupDb);
            sliderDenoiseMakeup.SetValueWithoutNotify(snap.DenoiseMakeupDb);

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
            sliderDenoiseWet.SliderComponent.onValueChanged.AddListener(DenoiseWetChanged);
            sliderDenoiseMakeup.SliderComponent.onValueChanged.AddListener(DenoiseMakeupChanged);

            // AGC tuning
            PanelElementDescriptor agcGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            agcGroup.SetTitle(BasisLocalization.Get("settings.microphone.agc.title"));
            agcGroup.SetDescription(BasisLocalization.Get("settings.microphone.agc.description"));

            sliderAgcTarget = PanelSlider.CreateEntryAndBind(
               agcGroup,
               PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.microphone.agc.targetRms"), 0.001f, 0.25f, false, 4, ValueDisplayMode.Raw),
               BasisSettingsDefaults.AgcTargetRms);
            sliderAgcTarget.SetValueWithoutNotify(snap.AgcTargetRms);

            sliderAgcMaxGain = PanelSlider.CreateEntryAndBind(
               agcGroup,
               PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.microphone.agc.maxGain"), 0f, 36f, false, 1, ValueDisplayMode.Raw),
               BasisSettingsDefaults.AgcMaxGainDb);
            sliderAgcMaxGain.SetValueWithoutNotify(snap.AgcMaxGainDb);

            sliderAgcAttack = PanelSlider.CreateEntryAndBind(
               agcGroup,
               PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.microphone.agc.attack"), 0f, 1f, false, 3, ValueDisplayMode.Percentage),
               BasisSettingsDefaults.AgcAttack);
            sliderAgcAttack.SetValueWithoutNotify(snap.AgcAttack);

            sliderAgcRelease = PanelSlider.CreateEntryAndBind(
               agcGroup,
               PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.microphone.agc.release"), 0f, 1f, false, 3, ValueDisplayMode.Percentage),
               BasisSettingsDefaults.AgcRelease);
            sliderAgcRelease.SetValueWithoutNotify(snap.AgcRelease);

            void AgcTargetChanged(float v)
            {
                if (SMDMicrophone.CurrentMode != BasisDeviceManagement.StaticCurrentMode)
                    SMDMicrophone.LoadInMicrophoneData(BasisDeviceManagement.StaticCurrentMode);

                var s = SMDMicrophone.Current;
                SMDMicrophone.SetAgcParams(v, s.AgcMaxGainDb, s.AgcAttack, s.AgcRelease);
            }
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

            sliderAgcTarget.OnValueChanged += AgcTargetChanged;
            sliderAgcMaxGain.OnValueChanged += AgcMaxGainChanged;
            sliderAgcAttack.OnValueChanged += AgcAttackChanged;
            sliderAgcRelease.OnValueChanged += AgcReleaseChanged;

            // Noise Gate
            PanelElementDescriptor noiseGateGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            noiseGateGroup.SetTitle(BasisLocalization.Get("settings.microphone.noiseGate.title"));
            noiseGateGroup.SetDescription(BasisLocalization.Get("settings.microphone.noiseGate.description"));

            PanelToggle toggleNoiseGate = PanelToggle.CreateNewEntry(noiseGateGroup);
            toggleNoiseGate.Descriptor.SetTitle(BasisLocalization.Get("settings.microphone.noiseGate.enable"));
            toggleNoiseGate.AssignBinding(BasisSettingsDefaults.UseNoiseGate);

            sliderNoiseGateThreshold = PanelSlider.CreateEntryAndBind(
               noiseGateGroup,
               PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.microphone.noiseGate.threshold"), 0f, 0.5f, false, 4, ValueDisplayMode.Raw),
               BasisSettingsDefaults.NoiseGateThreshold);
            sliderNoiseGateThreshold.SetValueWithoutNotify(snap.NoiseGateThreshold);

            sliderNoiseGateAttack = PanelSlider.CreateEntryAndBind(
               noiseGateGroup,
               PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.microphone.noiseGate.attack"), 0f, 1f, false, 3, ValueDisplayMode.Percentage),
               BasisSettingsDefaults.NoiseGateAttack);
            sliderNoiseGateAttack.SetValueWithoutNotify(snap.NoiseGateAttack);

            sliderNoiseGateRelease = PanelSlider.CreateEntryAndBind(
               noiseGateGroup,
               PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.microphone.noiseGate.release"), 0f, 1f, false, 3, ValueDisplayMode.Percentage),
               BasisSettingsDefaults.NoiseGateRelease);
            sliderNoiseGateRelease.SetValueWithoutNotify(snap.NoiseGateRelease);

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

            sliderNoiseGateThreshold.OnValueChanged += NoiseGateThresholdChanged;
            sliderNoiseGateAttack.OnValueChanged += NoiseGateAttackChanged;
            sliderNoiseGateRelease.OnValueChanged += NoiseGateReleaseChanged;

            // Mic Icon Position (advanced)
            PanelElementDescriptor micIconGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            micIconGroup.SetTitle(BasisLocalization.Get("settings.microphone.iconPosition.title"));
            micIconGroup.SetDescription(BasisLocalization.Get("settings.microphone.iconPosition.description"));

            PanelSlider sliderMicIconOffsetX = PanelSlider.CreateEntryAndBind(
                micIconGroup,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.microphone.iconPosition.horizontal"), -0.5f, 0.5f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.MicrophoneIconOffsetX);

            PanelSlider sliderMicIconOffsetY = PanelSlider.CreateEntryAndBind(
                micIconGroup,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.microphone.iconPosition.vertical"), -0.5f, 0.5f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.MicrophoneIconOffsetY);

            // Hide advanced groups by default
            limiterGroup.SetActive(false);
            denoiseGroup.SetActive(false);
            agcGroup.SetActive(false);
            noiseGateGroup.SetActive(false);
            micIconGroup.SetActive(false);

            PanelToggle toggleAdvanced = PanelToggle.CreateNewEntry(microphoneGroup);
            toggleAdvanced.Descriptor.SetTitle(BasisLocalization.Get("ui.advanced"));
            toggleAdvanced.SetValueWithoutNotify(false);
            toggleAdvanced.OnValueChanged += (val) =>
            {
                limiterGroup.SetActive(val);
                denoiseGroup.SetActive(val);
                agcGroup.SetActive(val);
                noiseGateGroup.SetActive(val);
                micIconGroup.SetActive(val);
                descriptor.ForceRebuild();
            };

            AddResetPageButton(container, "settings.tab.microphone", ResetMicrophoneDefaults);
#endif
            descriptor.ForceRebuild();
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
            BasisSettingsDefaults.MicrophoneIcon.ResetToDefault();
            BasisSettingsDefaults.MicrophoneIconOffsetX.ResetToDefault();
            BasisSettingsDefaults.MicrophoneIconOffsetY.ResetToDefault();
            BasisSettingsDefaults.LimitThreshold.ResetToDefault();
            BasisSettingsDefaults.LimitKnee.ResetToDefault();
            BasisSettingsDefaults.DenoiseWet.ResetToDefault();
            BasisSettingsDefaults.DenoiseMakeupDb.ResetToDefault();
            BasisSettingsDefaults.AgcTargetRms.ResetToDefault();
            BasisSettingsDefaults.AgcMaxGainDb.ResetToDefault();
            BasisSettingsDefaults.AgcAttack.ResetToDefault();
            BasisSettingsDefaults.AgcRelease.ResetToDefault();
            BasisSettingsDefaults.UseNoiseGate.ResetToDefault();
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

                if (sliderAgcTarget != null)
                    sliderAgcTarget.SetValueWithoutNotify(s.AgcTargetRms);

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


            PanelElementDescriptor qualityGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            qualityGroup.SetTitle(BasisLocalization.Get("settings.graphics.quality.title"));
            qualityGroup.SetDescription(BasisLocalization.Get("settings.graphics.quality.description"));

            // Avatar visibility limits (relocated from General). Lives at the
            // top of the quality group so users see distance/limit controls
            // before per-pixel quality knobs.
            PanelSlider sliderAvatarRange = PanelSlider.CreateEntryAndBind(
                qualityGroup,
                PanelSlider.SliderSettings.Distance(BasisLocalization.Get("settings.general.avatarRange"), 100),
                BasisSettingsDefaults.AvatarRange);

            PanelToggle toggleLimitAvatars = PanelToggle.CreateNewEntry(qualityGroup);
            toggleLimitAvatars.AssignBinding(BasisSettingsDefaults.UseMaxVisibleAvatars);
            toggleLimitAvatars.Descriptor.SetTitle(BasisLocalization.Get("settings.general.limitAvatars"));

            PanelSlider sliderMaxVisibleAvatars = PanelSlider.CreateEntryAndBind(
                qualityGroup,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.general.maxAvatars"), 0, 250, true, 0, ValueDisplayMode.Raw),
                BasisSettingsDefaults.MaxVisibleAvatars);

            sliderMaxVisibleAvatars.Descriptor.SetActive(toggleLimitAvatars.Value);
            toggleLimitAvatars.OnValueChanged += (val) =>
            {
                sliderMaxVisibleAvatars.Descriptor.SetActive(val);
                qualityGroup.ForceRebuild();
            };

            PanelToggle toggleViewCone = PanelToggle.CreateNewEntry(qualityGroup);
            toggleViewCone.AssignBinding(BasisSettingsDefaults.UseViewConeAvatars);
            toggleViewCone.Descriptor.SetTitle(BasisLocalization.Get("settings.general.viewCone"));
            toggleViewCone.Descriptor.SetDescription(BasisLocalization.Get("settings.general.viewCone.description"));

            PanelSlider sliderViewConeAngle = PanelSlider.CreateEntryAndBind(
                qualityGroup,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.general.viewConeAngle"), 30, 360, true, 0, ValueDisplayMode.Raw),
                BasisSettingsDefaults.ViewConeAngle);

            sliderViewConeAngle.Descriptor.SetActive(toggleViewCone.Value);
            toggleViewCone.OnValueChanged += (val) =>
            {
                sliderViewConeAngle.Descriptor.SetActive(val);
                qualityGroup.ForceRebuild();
            };

            PanelDropdown dropdownQualityLevel = PanelDropdown.CreateNewEntry(qualityGroup.ContentParent);
            dropdownQualityLevel.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.qualityLevel"));
            dropdownQualityLevel.AssignEntries(new List<string> { "Very Low", "Low", "Medium", "High", "Ultra" });
            dropdownQualityLevel.AssignBinding(BasisSettingsDefaults.QualityLevel);

            PanelDropdown dropdownShadowQuality = PanelDropdown.CreateNewEntry(qualityGroup.ContentParent);
            dropdownShadowQuality.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.shadowQuality"));
            dropdownShadowQuality.AssignEntries(new List<string> { "Very Low", "Low", "Medium", "High", "Ultra" });
            dropdownShadowQuality.AssignBinding(BasisSettingsDefaults.ShadowQuality);

            PanelDropdown dropdownAntialiasing = PanelDropdown.CreateNewEntry(qualityGroup.ContentParent);
            dropdownAntialiasing.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.antialiasing"));
            dropdownAntialiasing.AssignEntries(new List<string>
            {
                "Off","MSAA 2X","MSAA 4X","MSAA 8X","Linear","Point","FSR"//,"STP"
            });
            dropdownAntialiasing.AssignBinding(BasisSettingsDefaults.Antialiasing);

            PanelDropdown dropdownVSync = PanelDropdown.CreateNewEntry(qualityGroup.ContentParent);
            dropdownVSync.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.verticalSync"));
            dropdownVSync.Descriptor.SetDescription(BasisLocalization.Get("settings.graphics.verticalSync.description"));
            dropdownVSync.AssignEntries(new List<string> { "On", "Capped", "Off", "Half" });
            dropdownVSync.AssignBinding(BasisSettingsDefaults.VSync);

            PanelTextField fpsCapField = PanelTextField.CreateNewEntry(qualityGroup.ContentParent);
            fpsCapField.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.frameRateCap"));
            fpsCapField.Descriptor.SetDescription(BasisLocalization.Get("settings.graphics.frameRateCap.description"));
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

            PanelElementDescriptor renderingGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            renderingGroup.SetTitle(BasisLocalization.Get("settings.graphics.rendering.title"));
            renderingGroup.SetDescription(BasisLocalization.Get("settings.graphics.rendering.description"));

            PanelDropdown dropdownMemoryAllocation = PanelDropdown.CreateNewEntry(renderingGroup.ContentParent);
            dropdownMemoryAllocation.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.memoryAllocation"));
            dropdownMemoryAllocation.AssignEntries(new List<string> { "Dynamic", "256", "512", "1024", "2048", "4096", "8192" });
            dropdownMemoryAllocation.AssignBinding(BasisSettingsDefaults.MemoryAllocation);

            dropdownResolution = PanelDropdown.CreateNewEntry(renderingGroup.ContentParent);
            dropdownResolution.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.resolution"));
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

            int currentIndex = Mathf.Max(0, uniqueResolutions.FindIndex(r => r.x == Screen.width && r.y == Screen.height));
            dropdownResolution.DropdownComponent.SetValueWithoutNotify(currentIndex);

            dropdownScreenMode = PanelDropdown.CreateNewEntry(renderingGroup.ContentParent);
            // Screen mode entries stay as stable identifiers; GetScreenModeFromIndex
            // depends on fixed ordering, so these aren't localized.
            List<string> screenModeOptions = new List<string> { "Fullscreen", "Borderless Window", "Windowed" };

            dropdownScreenMode.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.screenMode"));
            dropdownScreenMode.AssignEntries(screenModeOptions);
            dropdownScreenMode.DropdownComponent.onValueChanged.AddListener(ScreenMode);
            dropdownScreenMode.DropdownComponent.SetValueWithoutNotify(GetIndexFromScreenMode(Screen.fullScreenMode));

            // --- Mirror Quality Override ---
            PanelElementDescriptor mirrorGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            mirrorGroup.SetTitle(BasisLocalization.Get("settings.graphics.mirrorQuality.title"));

            PanelToggle toggleMirrorOverride = PanelToggle.CreateNewEntry(mirrorGroup.ContentParent);
            toggleMirrorOverride.AssignBinding(BasisSettingsDefaults.UseMirrorQualityOverride);
            toggleMirrorOverride.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.mirrorQuality.override"));
            toggleMirrorOverride.Descriptor.SetDescription(BasisLocalization.Get("settings.graphics.mirrorQuality.override.description"));

            PanelDropdown dropdownMirrorQuality = PanelDropdown.CreateNewEntry(mirrorGroup.ContentParent);
            dropdownMirrorQuality.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.mirrorResolution"));
            dropdownMirrorQuality.AssignEntries(new List<string> { "256", "512", "1024", "2048", "4096", "8192" });
            dropdownMirrorQuality.AssignBinding(BasisSettingsDefaults.MirrorQuality);

            dropdownMirrorQuality.Descriptor.SetActive(toggleMirrorOverride.Value);
            toggleMirrorOverride.OnValueChanged += (val) =>
            {
                dropdownMirrorQuality.Descriptor.SetActive(val);
                mirrorGroup.ForceRebuild();
                descriptor.ForceRebuild();
            };

            // --- Accessibility: Bloom Override ---
            PanelElementDescriptor bloomGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            bloomGroup.SetTitle(BasisLocalization.Get("settings.graphics.bloom.title"));

            PanelToggle toggleBloomOverride = PanelToggle.CreateNewEntry(bloomGroup.ContentParent);
            toggleBloomOverride.AssignBinding(BasisSettingsDefaults.UseBloomOverride);
            toggleBloomOverride.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.bloom.override"));
            toggleBloomOverride.Descriptor.SetDescription(BasisLocalization.Get("settings.graphics.bloom.override.description"));

            PanelSlider sliderBloomIntensity = PanelSlider.CreateEntryAndBind(
                bloomGroup.ContentParent,
                new PanelSlider.SliderSettings(BasisLocalization.Get("settings.graphics.bloom.intensity"),
                    "",
                    BasisSettingsDefaults.BLOOM_INTENSITY_MIN,
                    BasisSettingsDefaults.BLOOM_INTENSITY_MAX,
                    false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.BloomIntensity);

            sliderBloomIntensity.Descriptor.SetActive(toggleBloomOverride.Value);
            toggleBloomOverride.OnValueChanged += (val) =>
            {
                sliderBloomIntensity.Descriptor.SetActive(val);
                bloomGroup.ForceRebuild();
                descriptor.ForceRebuild();
            };

            // --- Camera Near/Far Override ---
            PanelElementDescriptor cameraClipGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            cameraClipGroup.SetTitle(BasisLocalization.Get("settings.graphics.cameraClip.title"));

            PanelToggle toggleCameraClipOverride = PanelToggle.CreateNewEntry(cameraClipGroup.ContentParent);
            toggleCameraClipOverride.AssignBinding(BasisSettingsDefaults.UseCameraClipOverride);
            toggleCameraClipOverride.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.cameraClip.override"));
            toggleCameraClipOverride.Descriptor.SetDescription(BasisLocalization.Get("settings.graphics.cameraClip.override.description"));

            PanelSlider sliderCameraNear = PanelSlider.CreateEntryAndBind(
                cameraClipGroup,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.graphics.cameraClip.near"), 0.001f, 0.1f, false, 3, ValueDisplayMode.Meters),
                BasisSettingsDefaults.CameraClipNear);

            PanelSlider sliderCameraFar = PanelSlider.CreateEntryAndBind(
                cameraClipGroup,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.graphics.cameraClip.far"), 10f, 5000f, true, 0, ValueDisplayMode.Meters),
                BasisSettingsDefaults.CameraClipFar);

            sliderCameraNear.Descriptor.SetActive(toggleCameraClipOverride.Value);
            sliderCameraFar.Descriptor.SetActive(toggleCameraClipOverride.Value);
            toggleCameraClipOverride.OnValueChanged += (val) =>
            {
                sliderCameraNear.Descriptor.SetActive(val);
                sliderCameraFar.Descriptor.SetActive(val);
                cameraClipGroup.ForceRebuild();
                descriptor.ForceRebuild();
            };

            PanelElementDescriptor poseLodGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            poseLodGroup.SetTitle(BasisLocalization.Get("settings.graphics.poseLod.title"));
            poseLodGroup.SetDescription(BasisLocalization.Get("settings.graphics.poseLod.bias.description"));

            PanelSlider sliderPoseLod = PanelSlider.CreateEntryAndBind(
                poseLodGroup.ContentParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.graphics.poseLod.bias"), 0, 5, true, 0, ValueDisplayMode.Raw),
                BasisSettingsDefaults.PoseLOD);

            PanelElementDescriptor advancedGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            advancedGroup.SetTitle(BasisLocalization.Get("ui.advanced"));
            advancedGroup.SetDescription(BasisLocalization.Get("settings.graphics.advanced.description"));

            PanelToggle toggleAdvanced = PanelToggle.CreateNewEntry(advancedGroup.ContentParent);
            toggleAdvanced.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.advanced.showAdvanced"));
            toggleAdvanced.SetValueWithoutNotify(false);

            PanelSlider sliderRenderResolution = PanelSlider.CreateEntryAndBind(
                advancedGroup.ContentParent,
                new PanelSlider.SliderSettings(BasisLocalization.Get("settings.graphics.renderScale"), "", 0, 1.5f, false, 3, ValueDisplayMode.percentageFromZero),
                BasisSettingsDefaults.RenderResolution);

            PanelDropdown dropdownHDR = PanelDropdown.CreateNewEntry(advancedGroup.ContentParent);
            dropdownHDR.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.hdrSupport"));
            dropdownHDR.AssignEntries(new List<string> { "Off", "32bit", "64bit" });
            dropdownHDR.AssignBinding(BasisSettingsDefaults.HDRSupport);

            PanelSlider sliderFoveatedRendering = PanelSlider.CreateEntryAndBind(
                advancedGroup.ContentParent,
                new PanelSlider.SliderSettings(BasisLocalization.Get("settings.graphics.foveated"),
                    "",
                    0, 1, false, 1, ValueDisplayMode.Percentage),
                BasisSettingsDefaults.FoveatedRendering);

            PanelSlider sliderFieldOfView = PanelSlider.CreateEntryAndBind(
                advancedGroup.ContentParent,
                new PanelSlider.SliderSettings(BasisLocalization.Get("settings.graphics.fov"),
                    "",
                    BasisSettingsDefaults.FOV_MIN, BasisSettingsDefaults.FOV_MAX, true, 0, ValueDisplayMode.Degrees),
                BasisSettingsDefaults.FieldOfView);

            PanelSlider sliderMeshLOD = PanelSlider.CreateEntryAndBind(
                advancedGroup.ContentParent,
                new PanelSlider.SliderSettings(BasisLocalization.Get("settings.graphics.avatarLod"),
                    "",
                    0, 1, false, 3, ValueDisplayMode.Percentage),
                BasisSettingsDefaults.AvatarMeshLOD);

            PanelSlider sliderGlobalMeshLOD = PanelSlider.CreateEntryAndBind(
                advancedGroup.ContentParent,
                new PanelSlider.SliderSettings(BasisLocalization.Get("settings.graphics.worldLod"),
                    "",
                    0, 100, true, 0, ValueDisplayMode.Percentage),
                BasisSettingsDefaults.GlobalMeshLOD);

            PanelToggle toggleLocalHeadBlendShapes = PanelToggle.CreateNewEntry(advancedGroup.ContentParent);
            toggleLocalHeadBlendShapes.AssignBinding(BasisSettingsDefaults.LocalHeadBlendShapes);
            toggleLocalHeadBlendShapes.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.localHeadBlendShapes"));
            toggleLocalHeadBlendShapes.Descriptor.SetDescription(BasisLocalization.Get("settings.graphics.localHeadBlendShapes.description"));

            sliderRenderResolution.Descriptor.SetActive(false);
            dropdownHDR.Descriptor.SetActive(false);
            sliderFoveatedRendering.Descriptor.SetActive(false);
            sliderFieldOfView.Descriptor.SetActive(false);
            sliderMeshLOD.Descriptor.SetActive(false);
            sliderGlobalMeshLOD.Descriptor.SetActive(false);
            toggleLocalHeadBlendShapes.Descriptor.SetActive(false);

            toggleAdvanced.OnValueChanged += (val) =>
            {
                sliderRenderResolution.Descriptor.SetActive(val);
                dropdownHDR.Descriptor.SetActive(val);
                sliderFoveatedRendering.Descriptor.SetActive(val);
                sliderFieldOfView.Descriptor.SetActive(val);
                sliderMeshLOD.Descriptor.SetActive(val);
                sliderGlobalMeshLOD.Descriptor.SetActive(val);
                toggleLocalHeadBlendShapes.Descriptor.SetActive(val);
                advancedGroup.ForceRebuild();
                descriptor.ForceRebuild();
            };

            // Performance limits live in the same tab — formerly its own page,
            // merged here so users see all rendering / quality / cost controls together.
            SettingsProviderPerformanceLimits.BuildPerformanceLimitsContent(container);

            // One reset button for this whole page
            AddResetPageButton(container, "settings.tab.graphics", ResetGraphicsDefaults);

            descriptor.ForceRebuild();
            return tab;
        }

        private static void ResetGraphicsDefaults()
        {
            SettingsProviderPerformanceLimits.ResetPerformanceLimitDefaults();

            BasisSettingsDefaults.AvatarRange.ResetToDefault();
            BasisSettingsDefaults.UseMaxVisibleAvatars.ResetToDefault();
            BasisSettingsDefaults.MaxVisibleAvatars.ResetToDefault();
            BasisSettingsDefaults.UseViewConeAvatars.ResetToDefault();
            BasisSettingsDefaults.ViewConeAngle.ResetToDefault();

            BasisSettingsDefaults.QualityLevel.ResetToDefault();
            BasisSettingsDefaults.ShadowQuality.ResetToDefault();
            BasisSettingsDefaults.Antialiasing.ResetToDefault();
            BasisSettingsDefaults.VSync.ResetToDefault();
            BasisSettingsDefaults.VSyncCapFps.ResetToDefault();

            BasisSettingsDefaults.HDRSupport.ResetToDefault();
            BasisSettingsDefaults.MemoryAllocation.ResetToDefault();
            BasisSettingsDefaults.RenderResolution.ResetToDefault();

            BasisSettingsDefaults.FoveatedRendering.ResetToDefault();
            BasisSettingsDefaults.FieldOfView.ResetToDefault();
            BasisSettingsDefaults.PoseLOD.ResetToDefault();
            BasisSettingsDefaults.AvatarMeshLOD.ResetToDefault();
            BasisSettingsDefaults.GlobalMeshLOD.ResetToDefault();
            BasisSettingsDefaults.LocalHeadBlendShapes.ResetToDefault();

            BasisSettingsDefaults.UseMirrorQualityOverride.ResetToDefault();
            BasisSettingsDefaults.MirrorQuality.ResetToDefault();
            BasisSettingsDefaults.UseCameraClipOverride.ResetToDefault();
            BasisSettingsDefaults.CameraClipNear.ResetToDefault();
            BasisSettingsDefaults.CameraClipFar.ResetToDefault();

            BasisSettingsDefaults.UseBloomOverride.ResetToDefault();
            BasisSettingsDefaults.BloomIntensity.ResetToDefault();

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

            PanelElementDescriptor notificationGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            notificationGroup.SetTitle(BasisLocalization.Get("settings.chat.notifications.title"));
            notificationGroup.SetDescription(BasisLocalization.Get("settings.chat.notifications.description"));

            PanelToggle toggleJoinNotifications = PanelToggle.CreateNewEntry(notificationGroup);
            toggleJoinNotifications.Descriptor.SetTitle(BasisLocalization.Get("settings.chat.joinNotifications"));
            toggleJoinNotifications.AssignBinding(BasisSettingsDefaults.JoinNotifications);

            PanelToggle toggleLeaveNotifications = PanelToggle.CreateNewEntry(notificationGroup);
            toggleLeaveNotifications.Descriptor.SetTitle(BasisLocalization.Get("settings.chat.leaveNotifications"));
            toggleLeaveNotifications.AssignBinding(BasisSettingsDefaults.LeaveNotifications);

            PanelElementDescriptor chatGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            chatGroup.SetTitle(BasisLocalization.Get("settings.tab.chat"));
            chatGroup.SetDescription(BasisLocalization.Get("settings.chat.group.description"));

            PanelToggle toggleChatDisabled = PanelToggle.CreateNewEntry(chatGroup);
            toggleChatDisabled.Descriptor.SetTitle(BasisLocalization.Get("settings.chat.disable"));
            toggleChatDisabled.Descriptor.SetDescription(BasisLocalization.Get("settings.chat.disable.description"));
            toggleChatDisabled.AssignBinding(BasisSettingsDefaults.ChatDisabled);

            PanelTextField chatTextField = PanelTextField.CreateNewEntry(chatGroup);
            chatTextField.Descriptor.SetTitle(BasisLocalization.Get("settings.chat.message"));
            chatTextField.SetValueWithoutNotify(string.Empty);
            chatTextField._inputField.onEndEdit.AddListener(OnEndEndit);

            chatTextField.Descriptor.SetActive(!BasisSettingsDefaults.ChatDisabled.RawValue);
            toggleChatDisabled.OnValueChanged += (val) =>
            {
                chatTextField.Descriptor.SetActive(!val);
                chatGroup.ForceRebuild();
            };

            void OnEndEndit(string message)
            {
                if (!string.IsNullOrEmpty(message))
                {
                    BasisNetworkHandleChat.SendChatMessage(message);
                    chatTextField.SetValueWithoutNotify(string.Empty);
                }
            }

            // Nameplates live in the same tab — formerly its own page, merged here so
            // chat-adjacent presence settings (notifications, name visibility) are colocated.
            SettingsProviderNamePlate.BuildNamePlateContent(container);

            AddResetPageButton(container, "settings.tab.chat", ResetChatDefaults);

            descriptor.ForceRebuild();
            return tab;
        }

        private static void ResetChatDefaults()
        {
            BasisSettingsDefaults.JoinNotifications.ResetToDefault();
            BasisSettingsDefaults.LeaveNotifications.ResetToDefault();
            BasisSettingsDefaults.ChatDisabled.ResetToDefault();
            SettingsProviderNamePlate.ResetNamePlateDefaults();
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


            PanelElementDescriptor debugGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            debugGroup.SetTitle(BasisLocalization.Get("settings.developer.visualHelpers.title"));
            debugGroup.SetDescription(BasisLocalization.Get("settings.developer.visualHelpers.description"));

            PanelToggle toggleBoneTracking = PanelToggle.CreateNewEntry(debugGroup.ContentParent);
            toggleBoneTracking.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.boneTracking"));
            toggleBoneTracking.Descriptor.SetDescription(BasisLocalization.Get("settings.developer.boneTracking.description"));
            toggleBoneTracking.AssignBinding(BasisSettingsDefaults.DebugVisuals);

            PanelToggle toggleTrackerGizmos = PanelToggle.CreateNewEntry(debugGroup.ContentParent);
            toggleTrackerGizmos.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.trackerGizmos"));
            toggleTrackerGizmos.Descriptor.SetDescription(BasisLocalization.Get("settings.developer.trackerGizmos.description"));
            toggleTrackerGizmos.AssignBinding(BasisSettingsDefaults.TrackerGizmos);

            PanelToggle toggleAvatarDistance = PanelToggle.CreateNewEntry(debugGroup.ContentParent);
            toggleAvatarDistance.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.avatarDistance"));
            toggleAvatarDistance.Descriptor.SetDescription(BasisLocalization.Get("settings.developer.avatarDistance.description"));
            bool avatarDistOn = !string.Equals(BasisSettingsDefaults.VisualState.RawValue, "off", StringComparison.OrdinalIgnoreCase);
            toggleAvatarDistance.SetValueWithoutNotify(avatarDistOn);
            toggleAvatarDistance.OnValueChanged += (val) =>
            {
                BasisSettingsDefaults.VisualState.SetValue(val ? "only avatar distance" : "off");
            };

            PanelToggle toggleStatistics = PanelToggle.CreateNewEntry(debugGroup.ContentParent);
            toggleStatistics.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.enableStatistics"));
            toggleStatistics.Descriptor.SetDescription(BasisLocalization.Get("settings.developer.enableStatistics.description"));
            toggleStatistics.AssignBinding(BasisSettingsDefaults.EnableStatistics);

            PanelToggle toggleStreamingMeta = PanelToggle.CreateNewEntry(debugGroup.ContentParent);
            toggleStreamingMeta.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.streamingMeta"));
            toggleStreamingMeta.Descriptor.SetDescription(BasisLocalization.Get("settings.developer.streamingMeta.description"));
            toggleStreamingMeta.AssignBinding(BasisSettingsDefaults.EnableStreamingMeta);

            PanelTextField streamingMetaPortField = PanelTextField.CreateNewEntry(debugGroup.ContentParent);
            streamingMetaPortField.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.streamingMetaPort"));
            streamingMetaPortField.Descriptor.SetDescription(BasisLocalization.Get("settings.developer.streamingMetaPort.description"));
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
                debugGroup.ForceRebuild();
            };

            PanelToggle toggleDisableLogging = PanelToggle.CreateNewEntry(debugGroup.ContentParent);
            toggleDisableLogging.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.disableLogging"));
            toggleDisableLogging.Descriptor.SetDescription(BasisLocalization.Get("settings.developer.disableLogging.description"));
            toggleDisableLogging.AssignBinding(BasisSettingsDefaults.DisableLogging);

            // ---- Section Visibility Toggles ----
            PanelElementDescriptor sectionTogglesGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            sectionTogglesGroup.SetTitle(BasisLocalization.Get("settings.developer.sections.title"));
            sectionTogglesGroup.SetDescription(BasisLocalization.Get("settings.developer.sections.description"));

            PanelToggle toggleBuildInfo = PanelToggle.CreateNewEntry(sectionTogglesGroup.ContentParent);
            toggleBuildInfo.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.buildInfo"));
            toggleBuildInfo.Descriptor.SetDescription(BasisLocalization.Get("settings.developer.buildInfo.description"));
            toggleBuildInfo.AssignBinding(BasisSettingsDefaults.DevShowBuildInfo);

            PanelToggle toggleConsole = PanelToggle.CreateNewEntry(sectionTogglesGroup.ContentParent);
            toggleConsole.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.console"));
            toggleConsole.Descriptor.SetDescription(BasisLocalization.Get("settings.developer.console.description"));
            toggleConsole.AssignBinding(BasisSettingsDefaults.DevShowConsole);

            PanelToggle toggleEuroFilter = PanelToggle.CreateNewEntry(sectionTogglesGroup.ContentParent);
            toggleEuroFilter.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.euroFilter"));
            toggleEuroFilter.Descriptor.SetDescription(BasisLocalization.Get("settings.developer.euroFilter.description"));
            toggleEuroFilter.AssignBinding(BasisSettingsDefaults.DevShowEuroFilter);

            PanelToggle toggleNetStats = PanelToggle.CreateNewEntry(sectionTogglesGroup.ContentParent);
            toggleNetStats.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.netStats"));
            toggleNetStats.Descriptor.SetDescription(BasisLocalization.Get("settings.developer.netStats.description"));
            toggleNetStats.AssignBinding(BasisSettingsDefaults.DevShowNetStats);

            // ---- Remote Audio Debug ----
            PanelElementDescriptor audioDebugGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            audioDebugGroup.SetTitle(BasisLocalization.Get("settings.developer.remoteAudioDebug.title"));
            audioDebugGroup.SetDescription(BasisLocalization.Get("settings.developer.remoteAudioDebug.description"));

            PanelToggle toggleAudioDebug = PanelToggle.CreateNewEntry(audioDebugGroup.ContentParent);
            toggleAudioDebug.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.audioDebug.enable"));
            toggleAudioDebug.Descriptor.SetDescription(BasisLocalization.Get("settings.developer.audioDebug.enable.description"));
            toggleAudioDebug.AssignBinding(BasisSettingsDefaults.AudioDebugEnabled);

            PanelToggle toggleAudioSource = PanelToggle.CreateNewEntry(audioDebugGroup.ContentParent);
            toggleAudioSource.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.audioDebug.source"));
            toggleAudioSource.Descriptor.SetDescription(BasisLocalization.Get("settings.developer.audioDebug.source.description"));
            toggleAudioSource.AssignBinding(BasisSettingsDefaults.AudioDebugShowSource);

            PanelToggle toggleVolumeChain = PanelToggle.CreateNewEntry(audioDebugGroup.ContentParent);
            toggleVolumeChain.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.audioDebug.volumeChain"));
            toggleVolumeChain.Descriptor.SetDescription(BasisLocalization.Get("settings.developer.audioDebug.volumeChain.description"));
            toggleVolumeChain.AssignBinding(BasisSettingsDefaults.AudioDebugShowVolume);

            PanelToggle toggleRingBuffer = PanelToggle.CreateNewEntry(audioDebugGroup.ContentParent);
            toggleRingBuffer.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.audioDebug.ringBuffer"));
            toggleRingBuffer.Descriptor.SetDescription(BasisLocalization.Get("settings.developer.audioDebug.ringBuffer.description"));
            toggleRingBuffer.AssignBinding(BasisSettingsDefaults.AudioDebugShowRingBuffer);

            PanelToggle toggleJitter = PanelToggle.CreateNewEntry(audioDebugGroup.ContentParent);
            toggleJitter.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.audioDebug.jitter"));
            toggleJitter.Descriptor.SetDescription(BasisLocalization.Get("settings.developer.audioDebug.jitter.description"));
            toggleJitter.AssignBinding(BasisSettingsDefaults.AudioDebugShowJitter);

            PanelToggle toggleSilence = PanelToggle.CreateNewEntry(audioDebugGroup.ContentParent);
            toggleSilence.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.audioDebug.silence"));
            toggleSilence.Descriptor.SetDescription(BasisLocalization.Get("settings.developer.audioDebug.silence.description"));
            toggleSilence.AssignBinding(BasisSettingsDefaults.AudioDebugShowSilence);

            PanelToggle toggleViseme = PanelToggle.CreateNewEntry(audioDebugGroup.ContentParent);
            toggleViseme.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.audioDebug.viseme"));
            toggleViseme.Descriptor.SetDescription(BasisLocalization.Get("settings.developer.audioDebug.viseme.description"));
            toggleViseme.AssignBinding(BasisSettingsDefaults.AudioDebugShowViseme);

            // ---- Collapsible sections (toggled by section visibility) ----
            // Helper: collect all new children added to container by a builder call
            static List<GameObject> CollectNewChildren(RectTransform parent, int countBefore)
            {
                var result = new List<GameObject>();
                for (int i = countBefore; i < parent.childCount; i++)
                    result.Add(parent.GetChild(i).gameObject);
                return result;
            }

            static void DestroyList(List<GameObject> list)
            {
                for (int i = 0; i < list.Count; i++)
                    if (list[i] != null) UnityEngine.Object.Destroy(list[i]);
                list.Clear();
            }

            // Build & Environment
            PanelElementDescriptor infoGroup = null;
            void CreateBuildInfo()
            {
                infoGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
                infoGroup.SetTitle(BasisLocalization.Get("settings.developer.buildInfo"));
                infoGroup.SetDescription(BasisLocalization.Get("settings.developer.buildInfo.section.description"));
                CreateBuildInfoSection(infoGroup.ContentParent);
            }
            if (BasisSettingsDefaults.DevShowBuildInfo.RawValue) CreateBuildInfo();
            toggleBuildInfo.OnValueChanged += on =>
            {
                if (infoGroup != null) { UnityEngine.Object.Destroy(infoGroup.gameObject); infoGroup = null; }
                if (on) CreateBuildInfo();
            };

            // Network Euro Filter
            List<GameObject> euroObjects = new();
            void CreateEuroFilter()
            {
                int before = container.childCount;
                SettingsProviderNetworkTab.BuildNetworkEuroFilterGroup(container);
                euroObjects = CollectNewChildren(container, before);
            }
            if (BasisSettingsDefaults.DevShowEuroFilter.RawValue) CreateEuroFilter();
            toggleEuroFilter.OnValueChanged += on =>
            {
                DestroyList(euroObjects);
                if (on) CreateEuroFilter();
            };

            // Network & Statistics
            List<GameObject> netObjects = new();
            void CreateNetStats()
            {
                int before = container.childCount;
                SettingsProviderNetworkTab.BuildNetworkStatsGroup(container, out _);
                netObjects = CollectNewChildren(container, before);
            }
            if (BasisSettingsDefaults.DevShowNetStats.RawValue) CreateNetStats();
            toggleNetStats.OnValueChanged += on =>
            {
                DestroyList(netObjects);
                if (on) CreateNetStats();
            };

            SettingsProviderPlatform.BuildAutoSwapUI(container);

            // One reset button for this whole page
            AddResetPageButton(container, "settings.tab.developer", ResetDeveloperDefaults);

            // Console Log (BuildConsoleUI creates 2 groups: controls + output)
            List<GameObject> consoleObjects = new();
            void CreateConsole()
            {
                int before = container.childCount;
                SettingsProviderConsoleTab.BuildConsoleUI(container);
                consoleObjects = CollectNewChildren(container, before);
            }
            if (BasisSettingsDefaults.DevShowConsole.RawValue) CreateConsole();
            toggleConsole.OnValueChanged += on =>
            {
                DestroyList(consoleObjects);
                if (on) CreateConsole();
            };

            descriptor.ForceRebuild();
            return tab;
        }

        private static void ResetDeveloperDefaults()
        {
            BasisSettingsDefaults.DebugVisuals.ResetToDefault();
            BasisSettingsDefaults.TrackerGizmos.ResetToDefault();
            BasisSettingsDefaults.VisualState.SetValue("off");
            BasisSettingsDefaults.EnableStatistics.ResetToDefault();
            BasisSettingsDefaults.EnableStreamingMeta.ResetToDefault();
            BasisSettingsDefaults.StreamingMetaPort.ResetToDefault();
            BasisSettingsDefaults.DisableLogging.ResetToDefault();
            BasisSettingsDefaults.DevShowBuildInfo.ResetToDefault();
            BasisSettingsDefaults.DevShowConsole.ResetToDefault();
            BasisSettingsDefaults.DevShowEuroFilter.ResetToDefault();
            BasisSettingsDefaults.DevShowNetStats.ResetToDefault();
            BasisSettingsDefaults.NetEuroMinCutoff.ResetToDefault();
            BasisSettingsDefaults.NetEuroBeta.ResetToDefault();
            BasisSettingsDefaults.NetEuroDerivativeCutoff.ResetToDefault();
            BasisSettingsDefaults.AudioDebugEnabled.ResetToDefault();
            BasisSettingsDefaults.AudioDebugShowSource.ResetToDefault();
            BasisSettingsDefaults.AudioDebugShowVolume.ResetToDefault();
            BasisSettingsDefaults.AudioDebugShowRingBuffer.ResetToDefault();
            BasisSettingsDefaults.AudioDebugShowJitter.ResetToDefault();
            BasisSettingsDefaults.AudioDebugShowSilence.ResetToDefault();
            BasisSettingsDefaults.AudioDebugShowViseme.ResetToDefault();
            BasisSettingsDefaults.SwapMode.ResetToDefault();
        }

        private static void CreateBuildInfoSection(RectTransform parent)
        {
            PanelButton copyAll = PanelButton.CreateNew(parent);
            copyAll.Descriptor.SetTitle("Copy Build Info");
            copyAll.Descriptor.SetDescription("Copies all fields to clipboard.");
            copyAll.OnClicked += () =>
            {
                GUIUtility.systemCopyBuffer = BuildInfoString();
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

        private static string BuildInfoString()
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
