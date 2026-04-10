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
        private static string _pendingTabName;

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

        public static string StaticTitle => "Settings";
        public override string Title => StaticTitle;
        public override string IconAddress => AddressableAssets.Sprites.Settings;
        public override int Order => 0;
        public override bool Hidden => false;

        /// <summary>
        /// Opens the Settings menu and navigates directly to the specified tab by name.
        /// </summary>
        public static void OpenToTab(string tabName)
        {
            _pendingTabName = tabName;
            BasisMainMenu.OpenWithProvider(StaticTitle);
        }

        /// <summary>
        /// Opens the Settings menu and navigates directly to the Body Tracking tab.
        /// </summary>
        public static void OpenBodyTrackingTab()
        {
            OpenToTab("Body Tracking");
        }

        private static void NavigateToTab(PanelTabGroup tabGroup, string tabName)
        {
            for (int i = 0; i < tabGroup.SelectionButtons.Count; i++)
            {
                PanelButton button = tabGroup.SelectionButtons[i];
                if (button != null && button.Descriptor != null &&
                    button.Descriptor.TitleLabel != null &&
                    button.Descriptor.TitleLabel.text == tabName)
                {
                    button.OnClicked?.Invoke();
                    return;
                }
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

            // First tab is eager (shown immediately on open)
            tabGroup.AddTab("General", null, GeneralTab(tabGroup));
            // Remaining tabs are lazy-loaded on first selection to reduce stuttering
            AddLazyTab(tabGroup, "Audio", () => AudioTab(tabGroup));
            AddLazyTab(tabGroup, "Microphone", () => MicrophoneTab(tabGroup));
            AddLazyTab(tabGroup, "Graphics", () => GraphicsTab(tabGroup));
            AddLazyTab(tabGroup, "Controls", () => SettingsProviderControllerConfig.OpenControllerConfig(tabGroup));
            AddLazyTab(tabGroup, "Chat", () => ChatTab(tabGroup));
            AddLazyTab(tabGroup, "Body Tracking", () => SettingsProviderIK.IKTab(tabGroup));
            AddLazyTab(tabGroup, "Nameplates", () => SettingsProviderNamePlate.NamePlateTab(tabGroup));
            AddLazyTab(tabGroup, "My Avatar", () =>
                MyAvatarTabOverride != null
                    ? MyAvatarTabOverride(tabGroup)
                    : SettingsProviderAvatarStats.AvatarStatsTab(tabGroup));
            AddLazyTab(tabGroup, "Downloads & Cache", () => SettingsProviderStorage.StorageTab(tabGroup));
            AddLazyTab(tabGroup, "Trusted URLs", () => SettingsProviderTrustedUrls.TrustedUrlsTab(tabGroup));
          //  AddLazyTab(tabGroup, "UI Style", () => SettingsProviderUIStyle.UIStyleTab(tabGroup));
            AddLazyTab(tabGroup, "Developer", () => DeveloperTab(tabGroup));

            // External package tabs (registered via SettingsProvider.ExternalTabs)
            for (int i = 0; i < ExternalTabs.Count; i++)
            {
                var ext = ExternalTabs[i];
                AddLazyTab(tabGroup, ext.TabName, () => ext.Builder(tabGroup));
            }

            if (BasisNetworkManagement.LocalPermissions.Contains(PermNodes.PermissionsView))
            {
                AddLazyTab(tabGroup, "Admin", () => SettingsProviderAdminTab.AdminTab(tabGroup));
            }

            // Navigate to a specific tab if requested via OpenToTab
            if (!string.IsNullOrEmpty(_pendingTabName))
            {
                NavigateToTab(tabGroup, _pendingTabName);
                _pendingTabName = null;
            }

            panel.Descriptor.ForceRebuild();
        }

        /// <summary>
        /// Adds a tab with an empty placeholder page. On first selection the real
        /// content is built, the placeholder is released, and the Pages entry is swapped.
        /// </summary>
        private static void AddLazyTab(PanelTabGroup tabGroup, string tabName, Func<PanelTabPage> builder)
        {
            PanelTabPage placeholder = PanelTabPage.CreateVertical(tabGroup.Descriptor.ContentParent);
            int index = tabGroup.Pages.Count;
            bool built = false;

            tabGroup.AddTab(tabName, () =>
            {
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
        public static void AddResetPageButton(RectTransform parent, string pageName, Action resetAction)
        {
            PanelButton reset = PanelButton.CreateNew(parent);
            reset.Descriptor.SetTitle($"Reset {pageName}");
            reset.Descriptor.SetDescription("Resets this page to defaults.");
            reset.OnClicked += () =>
            {
                BasisMainMenu.Instance.OpenDialogue(
                    $"Reset {pageName}",
                    $"Reset all {pageName} settings to defaults?",
                    "Reset",
                    "Cancel",
                    value =>
                    {
                        if (!value)
                        {
                            return;
                        }

                        resetAction?.Invoke();
                        BasisMainMenu.Close();
                        OpenToTab(pageName);
                    });
            };
        }

        // ------------------
        // GENERAL TAB (ONE RESET BUTTON)
        // ------------------
        public static PanelTabPage GeneralTab(PanelTabGroup tabGroup)
        {
            PanelTabPage tab = PanelTabPage.CreateVertical(tabGroup.Descriptor.ContentParent);
            PanelElementDescriptor descriptor = tab.Descriptor;
            descriptor.SetIcon(AddressableAssets.Sprites.Settings);
            descriptor.SetTitle("General Settings");

            RectTransform container = descriptor.ContentParent;

            SettingsProviderPlatform.BuildDeviceModeUI(container);

            PanelElementDescriptor rangeGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            rangeGroup.SetTitle("Ranges");
            rangeGroup.SetDescription("Visibility and hearing ranges.");

            PanelSlider sliderAvatarRange = PanelSlider.CreateEntryAndBind(
                rangeGroup,
                PanelSlider.SliderSettings.Distance("Avatar Visibility Range", 100),
                BasisSettingsDefaults.AvatarRange);

            PanelSlider sliderHearingRange = PanelSlider.CreateEntryAndBind(
    rangeGroup,
    PanelSlider.SliderSettings.Distance("Hearing Range", 25),
    BasisSettingsDefaults.HearingRange);

#if !BASIS_DISABLE_MICROPHONE
            PanelSlider sliderMicrophoneRange = PanelSlider.CreateEntryAndBind(
                rangeGroup,
                PanelSlider.SliderSettings.Distance("Microphone Range", 25),
                BasisSettingsDefaults.MicrophoneRange);
#endif

            PanelToggle toggleLimitAvatars = PanelToggle.CreateNewEntry(rangeGroup);
            toggleLimitAvatars.AssignBinding(BasisSettingsDefaults.UseMaxVisibleAvatars);

            toggleLimitAvatars.Descriptor.SetTitle("Limit Avatars");


            PanelSlider sliderMaxVisibleAvatars = PanelSlider.CreateEntryAndBind(
                rangeGroup,
                PanelSlider.SliderSettings.Advanced("Max Avatars", 0, 250, true, 0, ValueDisplayMode.Raw),
                BasisSettingsDefaults.MaxVisibleAvatars);

            sliderMaxVisibleAvatars.Descriptor.SetActive(toggleLimitAvatars.Value);

            toggleLimitAvatars.OnValueChanged += (val) =>
            {
                sliderMaxVisibleAvatars.Descriptor.SetActive(val);
                rangeGroup.ForceRebuild();
            };

            PanelToggle toggleViewCone = PanelToggle.CreateNewEntry(rangeGroup);
            toggleViewCone.AssignBinding(BasisSettingsDefaults.UseViewConeAvatars);
            toggleViewCone.Descriptor.SetTitle("View Cone Avatars");
            toggleViewCone.Descriptor.SetDescription("Only show avatars in the direction you are looking.");

            PanelSlider sliderViewConeAngle = PanelSlider.CreateEntryAndBind(
                rangeGroup,
                PanelSlider.SliderSettings.Advanced("View Cone Angle", 30, 360, true, 0, ValueDisplayMode.Raw),
                BasisSettingsDefaults.ViewConeAngle);

            sliderViewConeAngle.Descriptor.SetActive(toggleViewCone.Value);

            toggleViewCone.OnValueChanged += (val) =>
            {
                sliderViewConeAngle.Descriptor.SetActive(val);
                rangeGroup.ForceRebuild();
            };

            PanelToggle toggleLimitAudio = PanelToggle.CreateNewEntry(rangeGroup);
            toggleLimitAudio.AssignBinding(BasisSettingsDefaults.UseMaxAudioSources);
            toggleLimitAudio.Descriptor.SetTitle("Limit Audio Sources");

            PanelSlider sliderMaxAudioSources = PanelSlider.CreateEntryAndBind(
                rangeGroup,
                PanelSlider.SliderSettings.Advanced("Max Audio Sources", 0, 250, true, 0, ValueDisplayMode.Raw),
                BasisSettingsDefaults.MaxAudioSources);

            sliderMaxAudioSources.Descriptor.SetActive(toggleLimitAudio.Value);

            toggleLimitAudio.OnValueChanged += (val) =>
            {
                sliderMaxAudioSources.Descriptor.SetActive(val);
                rangeGroup.ForceRebuild();
            };

            // TODO: re-enable when avatar preview is finished
            // PanelToggle toggleAvatarPreview = PanelToggle.CreateNewEntry(rangeGroup);
            // toggleAvatarPreview.AssignBinding(BasisSettingsDefaults.AvatarPreview);
            // toggleAvatarPreview.Descriptor.SetTitle("Avatar Preview");
            // toggleAvatarPreview.Descriptor.SetDescription("Show a live preview of your avatar on the HUD.");

            PanelElementDescriptor interactionsGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            interactionsGroup.SetTitle("Interactions");

            PanelToggle toggleDisableSeats = PanelToggle.CreateNewEntry(interactionsGroup);
            toggleDisableSeats.AssignBinding(BasisSettingsDefaults.DisableSeats);
            toggleDisableSeats.Descriptor.SetTitle("Disable Seats");
            toggleDisableSeats.Descriptor.SetDescription("Prevent sitting in seats placed in the world.");

            SettingsProviderPlatform.BuildAutoSwapUI(container);

            // One reset button for this whole page
            AddResetPageButton(container, "General", ResetGeneralDefaults);
            descriptor.ForceRebuild();
            return tab;
        }

        private static void ResetGeneralDefaults()
        {
            BasisSettingsDefaults.AvatarRange.ResetToDefault();
            BasisSettingsDefaults.MaxVisibleAvatars.ResetToDefault();
            BasisSettingsDefaults.MaxAudioSources.ResetToDefault();
            BasisSettingsDefaults.UseMaxAudioSources.ResetToDefault();
            BasisSettingsDefaults.UseViewConeAvatars.ResetToDefault();
            BasisSettingsDefaults.ViewConeAngle.ResetToDefault();
            BasisSettingsDefaults.HearingRange.ResetToDefault();
            BasisSettingsDefaults.AvatarPreview.ResetToDefault();
            BasisSettingsDefaults.DisableSeats.ResetToDefault();
            BasisSettingsDefaults.SwapMode.ResetToDefault();
#if !BASIS_DISABLE_MICROPHONE
            BasisSettingsDefaults.MicrophoneRange.ResetToDefault();
#endif
        }

        // ------------------
        // AUDIO TAB (ONE RESET BUTTON)
        // ------------------
        public static PanelTabPage AudioTab(PanelTabGroup tabGroup)
        {
            PanelTabPage tab = PanelTabPage.CreateVertical(tabGroup.Descriptor.ContentParent);
            PanelElementDescriptor descriptor = tab.Descriptor;

            descriptor.SetTitle("Audio Settings");
            RectTransform container = descriptor.ContentParent;

            // MIXER GROUP
            PanelElementDescriptor mixerGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            mixerGroup.SetTitle("Volume Mixer");
            mixerGroup.SetDescription("Control individual channel volumes.");

            PanelSlider sliderMainVolume = PanelSlider.CreateEntryAndBind(
                mixerGroup,
                PanelSlider.SliderSettings.Percentage("Main Volume"),
                BasisSettingsDefaults.MainVolume);
            sliderMainVolume.Descriptor.SetTitle("Master Volume");
            sliderMainVolume.Descriptor.SetDescription("Overall game volume.");

            PanelSlider sliderMenuVolume = PanelSlider.CreateEntryAndBind(
                mixerGroup,
                PanelSlider.SliderSettings.Percentage("Menu Volume"),
                BasisSettingsDefaults.MenuVolume);

            PanelSlider sliderWorldVolume = PanelSlider.CreateEntryAndBind(
                mixerGroup,
                PanelSlider.SliderSettings.Percentage("World Volume"),
                BasisSettingsDefaults.WorldVolume);

            PanelSlider sliderVideoVolume = PanelSlider.CreateEntryAndBind(
                mixerGroup,
                PanelSlider.SliderSettings.Percentage("Media Volume"),
                BasisSettingsDefaults.MediaVolume);

            PanelSlider sliderVoiceVolume = PanelSlider.CreateEntryAndBind(
                mixerGroup,
                PanelSlider.SliderSettings.Percentage("Voice Volume"),
                BasisSettingsDefaults.VoiceVolume);

            PanelSlider sliderAvatarVolume = PanelSlider.CreateEntryAndBind(
                mixerGroup,
                PanelSlider.SliderSettings.Percentage("Avatar Volume"),
                BasisSettingsDefaults.AvatarVolume);

            PanelSlider sliderPropVolume = PanelSlider.CreateEntryAndBind(
                mixerGroup,
                PanelSlider.SliderSettings.Percentage("Prop Volume"),
                BasisSettingsDefaults.PropVolume);

            // Remote Players (Spatial Audio) — includes its own Advanced toggle
            SettingsProviderRemoteAudio.BuildRemoteAudioUI(container);

            // One reset button for this whole page
            AddResetPageButton(container, "Audio", ResetAudioDefaults);
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

            descriptor.SetTitle("Microphone Settings");
            RectTransform container = descriptor.ContentParent;

#if !BASIS_DISABLE_MICROPHONE
            // Snapshot
            SMDMicrophone.MicSettings snap = SMDMicrophone.Current;

            // MICROPHONE GROUP
            PanelElementDescriptor microphoneGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            microphoneGroup.SetTitle("Microphone");
            microphoneGroup.SetDescription("Microphone Related Settings");

            // Microphone Volume (0..1)
            sliderMicrophoneVolume = PanelSlider.CreateEntryAndBind(
               microphoneGroup,
               PanelSlider.SliderSettings.Advanced("Microphone Volume", 0, 1, false, 4, ValueDisplayMode.Percentage),
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
            dropdownMicrophoneSelection.Descriptor.SetTitle("Microphone Selection");
            dropdownMicrophoneSelection.AssignEntries(SMDMicrophone.MicrophoneDevices?.ToList() ?? new List<string>());
            dropdownMicrophoneSelection.SetValueWithoutNotify(snap.Microphone);

            void MicrophoneSelectionChanged(string name)
            {
                if (SMDMicrophone.CurrentMode != BasisDeviceManagement.StaticCurrentMode)
                    SMDMicrophone.LoadInMicrophoneData(BasisDeviceManagement.StaticCurrentMode);

                SMDMicrophone.SetMicrophone(name);
            }
            dropdownMicrophoneSelection.OnValueChanged += MicrophoneSelectionChanged;

            PanelToggle toggleMicrophoneDenoiser = PanelToggle.CreateNewEntry(microphoneGroup);
            toggleMicrophoneDenoiser.Descriptor.SetTitle("Microphone Denoiser");
            toggleMicrophoneDenoiser.AssignBinding(BasisSettingsDefaults.MicrophoneDenoiser);

            PanelToggle toggleAGC = PanelToggle.CreateNewEntry(microphoneGroup);
            toggleAGC.Descriptor.SetTitle("Automatic Gain (AGC)");
            toggleAGC.AssignBinding(BasisSettingsDefaults.UseAutomaticGain);

            PanelDropdown dropdownMicrophoneMode = PanelDropdown.CreateNewEntry(microphoneGroup);
            dropdownMicrophoneMode.Descriptor.SetTitle("Microphone Mode");
            dropdownMicrophoneMode.AssignEntries(new List<string>
            {
                "On Activation",
                "Push To Talk"
            });
            dropdownMicrophoneMode.AssignBinding(BasisSettingsDefaults.MicrophoneMode);

            PanelDropdown dropdownMicrophoneIcon = PanelDropdown.CreateNewEntry(microphoneGroup);
            dropdownMicrophoneIcon.Descriptor.SetTitle("Microphone Icon");
            dropdownMicrophoneIcon.AssignEntries(new List<string>
            {
                "AlwaysVisible",
                "ActivityDetection",
                "Hidden"
            });
            dropdownMicrophoneIcon.AssignBinding(BasisSettingsDefaults.MicrophoneIcon);

            PanelDropdown dropdownMicStartBehavior = PanelDropdown.CreateNewEntry(microphoneGroup);
            dropdownMicStartBehavior.Descriptor.SetTitle("Mic Start Behavior");
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
            limiterGroup.SetTitle("Limiter");
            limiterGroup.SetDescription("Prevents clipping by soft-limiting peaks.");

            sliderLimitThreshold = PanelSlider.CreateEntryAndBind(
               limiterGroup,
               PanelSlider.SliderSettings.Advanced("Limit Threshold", 0f, 1f, false, 3, ValueDisplayMode.Percentage),
               BasisSettingsDefaults.LimitThreshold);
            sliderLimitThreshold.SetValueWithoutNotify(snap.LimitThreshold);

            sliderLimitKnee = PanelSlider.CreateEntryAndBind(
               limiterGroup,
               PanelSlider.SliderSettings.Advanced("Limit Knee", 0f, 1f, false, 3, ValueDisplayMode.Percentage),
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
            denoiseGroup.SetTitle("Denoiser Tuning");
            denoiseGroup.SetDescription("Adjust denoiser blend and makeup gain.");

            sliderDenoiseWet = PanelSlider.CreateEntryAndBind(
               denoiseGroup,
               PanelSlider.SliderSettings.Advanced("Denoise Wet", 0f, 1f, false, 3, ValueDisplayMode.Percentage),
               BasisSettingsDefaults.DenoiseWet);
            sliderDenoiseWet.SetValueWithoutNotify(snap.DenoiseWet);

            sliderDenoiseMakeup = PanelSlider.CreateEntryAndBind(
               denoiseGroup,
               PanelSlider.SliderSettings.Advanced("Denoise Makeup (dB)", -12f, 24f, false, 2, ValueDisplayMode.Raw),
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
            agcGroup.SetTitle("AGC Tuning");
            agcGroup.SetDescription("Target loudness and responsiveness (only applies when AGC is enabled).");

            sliderAgcTarget = PanelSlider.CreateEntryAndBind(
               agcGroup,
               PanelSlider.SliderSettings.Advanced("AGC Target RMS", 0.001f, 0.25f, false, 4, ValueDisplayMode.Raw),
               BasisSettingsDefaults.AgcTargetRms);
            sliderAgcTarget.SetValueWithoutNotify(snap.AgcTargetRms);

            sliderAgcMaxGain = PanelSlider.CreateEntryAndBind(
               agcGroup,
               PanelSlider.SliderSettings.Advanced("AGC Max Gain (dB)", 0f, 36f, false, 1, ValueDisplayMode.Raw),
               BasisSettingsDefaults.AgcMaxGainDb);
            sliderAgcMaxGain.SetValueWithoutNotify(snap.AgcMaxGainDb);

            sliderAgcAttack = PanelSlider.CreateEntryAndBind(
               agcGroup,
               PanelSlider.SliderSettings.Advanced("AGC Attack", 0f, 1f, false, 3, ValueDisplayMode.Percentage),
               BasisSettingsDefaults.AgcAttack);
            sliderAgcAttack.SetValueWithoutNotify(snap.AgcAttack);

            sliderAgcRelease = PanelSlider.CreateEntryAndBind(
               agcGroup,
               PanelSlider.SliderSettings.Advanced("AGC Release", 0f, 1f, false, 3, ValueDisplayMode.Percentage),
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
            noiseGateGroup.SetTitle("Noise Gate");
            noiseGateGroup.SetDescription("Mutes audio below a threshold to cut background noise.");

            PanelToggle toggleNoiseGate = PanelToggle.CreateNewEntry(noiseGateGroup);
            toggleNoiseGate.Descriptor.SetTitle("Enable Noise Gate");
            toggleNoiseGate.AssignBinding(BasisSettingsDefaults.UseNoiseGate);

            sliderNoiseGateThreshold = PanelSlider.CreateEntryAndBind(
               noiseGateGroup,
               PanelSlider.SliderSettings.Advanced("Gate Threshold", 0f, 0.5f, false, 4, ValueDisplayMode.Raw),
               BasisSettingsDefaults.NoiseGateThreshold);
            sliderNoiseGateThreshold.SetValueWithoutNotify(snap.NoiseGateThreshold);

            sliderNoiseGateAttack = PanelSlider.CreateEntryAndBind(
               noiseGateGroup,
               PanelSlider.SliderSettings.Advanced("Gate Attack", 0f, 1f, false, 3, ValueDisplayMode.Percentage),
               BasisSettingsDefaults.NoiseGateAttack);
            sliderNoiseGateAttack.SetValueWithoutNotify(snap.NoiseGateAttack);

            sliderNoiseGateRelease = PanelSlider.CreateEntryAndBind(
               noiseGateGroup,
               PanelSlider.SliderSettings.Advanced("Gate Release", 0f, 1f, false, 3, ValueDisplayMode.Percentage),
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
            micIconGroup.SetTitle("Mic Icon Position");
            micIconGroup.SetDescription("Adjust the microphone icon position on screen.");

            PanelSlider sliderMicIconOffsetX = PanelSlider.CreateEntryAndBind(
                micIconGroup,
                PanelSlider.SliderSettings.Advanced("Horizontal Offset", -0.5f, 0.5f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.MicrophoneIconOffsetX);

            PanelSlider sliderMicIconOffsetY = PanelSlider.CreateEntryAndBind(
                micIconGroup,
                PanelSlider.SliderSettings.Advanced("Vertical Offset", -0.5f, 0.5f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.MicrophoneIconOffsetY);

            // Hide advanced groups by default
            limiterGroup.SetActive(false);
            denoiseGroup.SetActive(false);
            agcGroup.SetActive(false);
            noiseGateGroup.SetActive(false);
            micIconGroup.SetActive(false);

            PanelToggle toggleAdvanced = PanelToggle.CreateNewEntry(microphoneGroup);
            toggleAdvanced.Descriptor.SetTitle("Advanced");
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

            AddResetPageButton(container, "Microphone", ResetMicrophoneDefaults);
#endif
            descriptor.ForceRebuild();
            return tab;
        }

        private static void ResetMicrophoneDefaults()
        {
#if !BASIS_DISABLE_MICROPHONE
            BasisSettingsDefaults.MicrophoneVolume.ResetToDefault();
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
            descriptor.SetTitle("Graphics Settings");

            RectTransform container = descriptor.ContentParent;


            PanelElementDescriptor qualityGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            qualityGroup.SetTitle("Quality");
            qualityGroup.SetDescription("Overall render quality and post-processing.");

            PanelDropdown dropdownQualityLevel = PanelDropdown.CreateNewEntry(qualityGroup.ContentParent);
            dropdownQualityLevel.Descriptor.SetTitle("Quality Level");
            dropdownQualityLevel.AssignEntries(new List<string> { "Very Low", "Low", "Medium", "High", "Ultra" });
            dropdownQualityLevel.AssignBinding(BasisSettingsDefaults.QualityLevel);

            PanelDropdown dropdownShadowQuality = PanelDropdown.CreateNewEntry(qualityGroup.ContentParent);
            dropdownShadowQuality.Descriptor.SetTitle("Shadow Quality");
            dropdownShadowQuality.AssignEntries(new List<string> { "Very Low", "Low", "Medium", "High", "Ultra" });
            dropdownShadowQuality.AssignBinding(BasisSettingsDefaults.ShadowQuality);

            PanelDropdown dropdownAntialiasing = PanelDropdown.CreateNewEntry(qualityGroup.ContentParent);
            dropdownAntialiasing.Descriptor.SetTitle("Antialiasing");
            dropdownAntialiasing.AssignEntries(new List<string>
            {
                "Off","MSAA 2X","MSAA 4X","MSAA 8X","Linear","Point","FSR","STP"
            });
            dropdownAntialiasing.AssignBinding(BasisSettingsDefaults.Antialiasing);

            PanelDropdown dropdownVSync = PanelDropdown.CreateNewEntry(qualityGroup.ContentParent);
            dropdownVSync.Descriptor.SetTitle("Vertical Sync");
            dropdownVSync.Descriptor.SetDescription("VR uses headset refreshrate");
            dropdownVSync.AssignEntries(new List<string> { "On", "Capped", "Off", "Half" });
            dropdownVSync.AssignBinding(BasisSettingsDefaults.VSync);

            PanelTextField fpsCapField = PanelTextField.CreateNewEntry(qualityGroup.ContentParent);
            fpsCapField.Descriptor.SetTitle("Frame Rate Cap (FPS)");
            fpsCapField.Descriptor.SetDescription("Used only when Vertical Sync is set to Capped.");
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
            renderingGroup.SetTitle("Rendering");
            renderingGroup.SetDescription("Resolution and performance-related options.");

            PanelDropdown dropdownMemoryAllocation = PanelDropdown.CreateNewEntry(renderingGroup.ContentParent);
            dropdownMemoryAllocation.Descriptor.SetTitle("Memory Allocation");
            dropdownMemoryAllocation.AssignEntries(new List<string> { "Dynamic", "256", "512", "1024", "2048", "4096", "8192" });
            dropdownMemoryAllocation.AssignBinding(BasisSettingsDefaults.MemoryAllocation);

            dropdownResolution = PanelDropdown.CreateNewEntry(renderingGroup.ContentParent);
            dropdownResolution.Descriptor.SetTitle("Resolution");
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
            List<string> screenModeOptions = new List<string> { "Fullscreen", "Borderless Window", "Windowed" };

            dropdownScreenMode.Descriptor.SetTitle("Screen Mode");
            dropdownScreenMode.AssignEntries(screenModeOptions);
            dropdownScreenMode.DropdownComponent.onValueChanged.AddListener(ScreenMode);
            dropdownScreenMode.DropdownComponent.SetValueWithoutNotify(GetIndexFromScreenMode(Screen.fullScreenMode));

            // --- Mirror Quality Override ---
            PanelElementDescriptor mirrorGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            mirrorGroup.SetTitle("Mirror Quality");

            PanelToggle toggleMirrorOverride = PanelToggle.CreateNewEntry(mirrorGroup.ContentParent);
            toggleMirrorOverride.AssignBinding(BasisSettingsDefaults.UseMirrorQualityOverride);
            toggleMirrorOverride.Descriptor.SetTitle("Override Mirror Quality");
            toggleMirrorOverride.Descriptor.SetDescription("Override the resolution used by mirrors.");

            PanelDropdown dropdownMirrorQuality = PanelDropdown.CreateNewEntry(mirrorGroup.ContentParent);
            dropdownMirrorQuality.Descriptor.SetTitle("Mirror Resolution");
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
            bloomGroup.SetTitle("Bloom");

            PanelToggle toggleBloomOverride = PanelToggle.CreateNewEntry(bloomGroup.ContentParent);
            toggleBloomOverride.AssignBinding(BasisSettingsDefaults.UseBloomOverride);
            toggleBloomOverride.Descriptor.SetTitle("Override Bloom Intensity");
            toggleBloomOverride.Descriptor.SetDescription("Override the scene bloom intensity.");

            PanelSlider sliderBloomIntensity = PanelSlider.CreateEntryAndBind(
                bloomGroup.ContentParent,
                new PanelSlider.SliderSettings("Bloom Intensity",
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
            cameraClipGroup.SetTitle("Camera Clip Distances");

            PanelToggle toggleCameraClipOverride = PanelToggle.CreateNewEntry(cameraClipGroup.ContentParent);
            toggleCameraClipOverride.AssignBinding(BasisSettingsDefaults.UseCameraClipOverride);
            toggleCameraClipOverride.Descriptor.SetTitle("Override Camera Clip Distances");
            toggleCameraClipOverride.Descriptor.SetDescription("Force near and far clip plane distances on the camera.");

            PanelSlider sliderCameraNear = PanelSlider.CreateEntryAndBind(
                cameraClipGroup,
                PanelSlider.SliderSettings.Advanced("Near Clip", 0.001f, 0.1f, false, 3, ValueDisplayMode.Meters),
                BasisSettingsDefaults.CameraClipNear);

            PanelSlider sliderCameraFar = PanelSlider.CreateEntryAndBind(
                cameraClipGroup,
                PanelSlider.SliderSettings.Advanced("Far Clip", 10f, 5000f, true, 0, ValueDisplayMode.Meters),
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
            poseLodGroup.SetTitle("Pose LOD");
            poseLodGroup.SetDescription("Control how frequently distant player poses update.");

            PanelSlider sliderPoseLod = PanelSlider.CreateEntryAndBind(
                poseLodGroup.ContentParent,
                PanelSlider.SliderSettings.Advanced("Pose LOD Bias", 0, 5, true, 0, ValueDisplayMode.Raw),
                BasisSettingsDefaults.PoseLOD);
            sliderPoseLod.Descriptor.SetDescription(
                "Reduces CPU cost by updating distant player poses less frequently.\n" +
                "0 = off (all players update every frame).\n" +
                "1-2 = subtle reduction, barely visible.\n" +
                "3-5 = noticeable on distant players, significant CPU savings.");

            PanelElementDescriptor advancedGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            advancedGroup.SetTitle("Advanced");
            advancedGroup.SetDescription("Fine-tune rendering, LOD, and performance options.");

            PanelToggle toggleAdvanced = PanelToggle.CreateNewEntry(advancedGroup.ContentParent);
            toggleAdvanced.Descriptor.SetTitle("Show Advanced Settings");
            toggleAdvanced.SetValueWithoutNotify(false);

            PanelSlider sliderRenderResolution = PanelSlider.CreateEntryAndBind(
                advancedGroup.ContentParent,
                new PanelSlider.SliderSettings("Render Scale", "", 0, 1.5f, false, 3, ValueDisplayMode.percentageFromZero),
                BasisSettingsDefaults.RenderResolution);

            PanelDropdown dropdownHDR = PanelDropdown.CreateNewEntry(advancedGroup.ContentParent);
            dropdownHDR.Descriptor.SetTitle("HDR Support");
            dropdownHDR.AssignEntries(new List<string> { "Off", "32bit", "64bit" });
            dropdownHDR.AssignBinding(BasisSettingsDefaults.HDRSupport);

            PanelSlider sliderFoveatedRendering = PanelSlider.CreateEntryAndBind(
                advancedGroup.ContentParent,
                new PanelSlider.SliderSettings("Foveated Percentage",
                    "",
                    0, 1, false, 1, ValueDisplayMode.Percentage),
                BasisSettingsDefaults.FoveatedRendering);

            PanelSlider sliderFieldOfView = PanelSlider.CreateEntryAndBind(
                advancedGroup.ContentParent,
                new PanelSlider.SliderSettings("Field of View",
                    "",
                    BasisSettingsDefaults.FOV_MIN, BasisSettingsDefaults.FOV_MAX, true, 0, ValueDisplayMode.Degrees),
                BasisSettingsDefaults.FieldOfView);

            PanelSlider sliderMeshLOD = PanelSlider.CreateEntryAndBind(
                advancedGroup.ContentParent,
                new PanelSlider.SliderSettings("Avatar LOD",
                    "",
                    0, 1, false, 3, ValueDisplayMode.Percentage),
                BasisSettingsDefaults.AvatarMeshLOD);

            PanelSlider sliderGlobalMeshLOD = PanelSlider.CreateEntryAndBind(
                advancedGroup.ContentParent,
                new PanelSlider.SliderSettings("World LOD",
                    "",
                    0, 100, true, 0, ValueDisplayMode.Percentage),
                BasisSettingsDefaults.GlobalMeshLOD);

            sliderRenderResolution.Descriptor.SetActive(false);
            dropdownHDR.Descriptor.SetActive(false);
            sliderFoveatedRendering.Descriptor.SetActive(false);
            sliderFieldOfView.Descriptor.SetActive(false);
            sliderMeshLOD.Descriptor.SetActive(false);
            sliderGlobalMeshLOD.Descriptor.SetActive(false);

            toggleAdvanced.OnValueChanged += (val) =>
            {
                sliderRenderResolution.Descriptor.SetActive(val);
                dropdownHDR.Descriptor.SetActive(val);
                sliderFoveatedRendering.Descriptor.SetActive(val);
                sliderFieldOfView.Descriptor.SetActive(val);
                sliderMeshLOD.Descriptor.SetActive(val);
                sliderGlobalMeshLOD.Descriptor.SetActive(val);
                advancedGroup.ForceRebuild();
                descriptor.ForceRebuild();
            };

            // One reset button for this whole page
            AddResetPageButton(container, "Graphics", ResetGraphicsDefaults);

            descriptor.ForceRebuild();
            return tab;
        }

        private static void ResetGraphicsDefaults()
        {
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

            descriptor.SetTitle("Chat");
            RectTransform container = descriptor.ContentParent;

            PanelElementDescriptor notificationGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            notificationGroup.SetTitle("Notifications");
            notificationGroup.SetDescription("Toggle join and leave notifications.");

            PanelToggle toggleJoinNotifications = PanelToggle.CreateNewEntry(notificationGroup);
            toggleJoinNotifications.Descriptor.SetTitle("Join Notifications");
            toggleJoinNotifications.AssignBinding(BasisSettingsDefaults.JoinNotifications);

            PanelToggle toggleLeaveNotifications = PanelToggle.CreateNewEntry(notificationGroup);
            toggleLeaveNotifications.Descriptor.SetTitle("Leave Notifications");
            toggleLeaveNotifications.AssignBinding(BasisSettingsDefaults.LeaveNotifications);

            PanelElementDescriptor chatGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            chatGroup.SetTitle("Chat");
            chatGroup.SetDescription("Send a text message that appears above your nameplate.");

            PanelTextField chatTextField = PanelTextField.CreateNewEntry(chatGroup);
            chatTextField.Descriptor.SetTitle("Chat Message");
            chatTextField.SetValueWithoutNotify(string.Empty);
            chatTextField._inputField.onEndEdit.AddListener(OnEndEndit);

            void OnEndEndit(string message)
            {
                if (!string.IsNullOrEmpty(message))
                {
                    BasisNetworkHandleChat.SendChatMessage(message);
                    chatTextField.SetValueWithoutNotify(string.Empty);
                }
            }

            descriptor.ForceRebuild();
            return tab;
        }

        // ------------------
        // DEVELOPER TAB (ONE RESET BUTTON)
        // ------------------
        public static PanelTabPage DeveloperTab(PanelTabGroup tabGroup)
        {
            PanelTabPage tab = PanelTabPage.CreateVertical(tabGroup.Descriptor.ContentParent);
            PanelElementDescriptor descriptor = tab.Descriptor;

            descriptor.SetTitle("Developer & Debug");
            RectTransform container = descriptor.ContentParent;


            PanelElementDescriptor debugGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            debugGroup.SetTitle("Visual Helpers");
            debugGroup.SetDescription("Toggle individual debug visualizations.");

            PanelToggle toggleBoneTracking = PanelToggle.CreateNewEntry(debugGroup.ContentParent);
            toggleBoneTracking.Descriptor.SetTitle("Bone Tracking");
            toggleBoneTracking.Descriptor.SetDescription("Show sphere gizmos at tracked bone positions.");
            toggleBoneTracking.AssignBinding(BasisSettingsDefaults.DebugVisuals);

            PanelToggle toggleAvatarDistance = PanelToggle.CreateNewEntry(debugGroup.ContentParent);
            toggleAvatarDistance.Descriptor.SetTitle("Avatar Distance");
            toggleAvatarDistance.Descriptor.SetDescription("Show avatar visibility range circle at your feet.");
            bool avatarDistOn = !string.Equals(BasisSettingsDefaults.VisualState.RawValue, "off", StringComparison.OrdinalIgnoreCase);
            toggleAvatarDistance.SetValueWithoutNotify(avatarDistOn);
            toggleAvatarDistance.OnValueChanged += (val) =>
            {
                BasisSettingsDefaults.VisualState.SetValue(val ? "only avatar distance" : "off");
            };

            PanelToggle toggleStatistics = PanelToggle.CreateNewEntry(debugGroup.ContentParent);
            toggleStatistics.Descriptor.SetTitle("Enable Statistics");
            toggleStatistics.Descriptor.SetDescription("Enable network statistics recording. Takes effect on next connection.");
            toggleStatistics.AssignBinding(BasisSettingsDefaults.EnableStatistics);

            // ---- Section Visibility Toggles ----
            PanelElementDescriptor sectionTogglesGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            sectionTogglesGroup.SetTitle("Developer Sections");
            sectionTogglesGroup.SetDescription("Toggle which developer sections are visible below.");

            PanelToggle toggleBuildInfo = PanelToggle.CreateNewEntry(sectionTogglesGroup.ContentParent);
            toggleBuildInfo.Descriptor.SetTitle("Build & Environment");
            toggleBuildInfo.Descriptor.SetDescription("Show build identifiers and environment info.");
            toggleBuildInfo.AssignBinding(BasisSettingsDefaults.DevShowBuildInfo);

            PanelToggle toggleConsole = PanelToggle.CreateNewEntry(sectionTogglesGroup.ContentParent);
            toggleConsole.Descriptor.SetTitle("Console Log");
            toggleConsole.Descriptor.SetDescription("Show inline console log output.");
            toggleConsole.AssignBinding(BasisSettingsDefaults.DevShowConsole);

            PanelToggle toggleEuroFilter = PanelToggle.CreateNewEntry(sectionTogglesGroup.ContentParent);
            toggleEuroFilter.Descriptor.SetTitle("Network Euro Filter");
            toggleEuroFilter.Descriptor.SetDescription("Show One Euro filter tuning for remote interpolation.");
            toggleEuroFilter.AssignBinding(BasisSettingsDefaults.DevShowEuroFilter);

            PanelToggle toggleNetStats = PanelToggle.CreateNewEntry(sectionTogglesGroup.ContentParent);
            toggleNetStats.Descriptor.SetTitle("Network & Statistics");
            toggleNetStats.Descriptor.SetDescription("Show live connection and bandwidth diagnostics.");
            toggleNetStats.AssignBinding(BasisSettingsDefaults.DevShowNetStats);

            // ---- Remote Audio Debug ----
            PanelElementDescriptor audioDebugGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            audioDebugGroup.SetTitle("Remote Audio Debug");
            audioDebugGroup.SetDescription("Controls which audio debug sections appear in per-player panels.");

            PanelToggle toggleAudioDebug = PanelToggle.CreateNewEntry(audioDebugGroup.ContentParent);
            toggleAudioDebug.Descriptor.SetTitle("Enable Audio Debug");
            toggleAudioDebug.Descriptor.SetDescription("Show audio debug info in individual player panels.");
            toggleAudioDebug.AssignBinding(BasisSettingsDefaults.AudioDebugEnabled);

            PanelToggle toggleAudioSource = PanelToggle.CreateNewEntry(audioDebugGroup.ContentParent);
            toggleAudioSource.Descriptor.SetTitle("Audio Source");
            toggleAudioSource.Descriptor.SetDescription("Show AudioSource state (enabled, playing, spatial settings).");
            toggleAudioSource.AssignBinding(BasisSettingsDefaults.AudioDebugShowSource);

            PanelToggle toggleVolumeChain = PanelToggle.CreateNewEntry(audioDebugGroup.ContentParent);
            toggleVolumeChain.Descriptor.SetTitle("Volume Chain");
            toggleVolumeChain.Descriptor.SetDescription("Show volume multipliers (source, dampening, main, effective).");
            toggleVolumeChain.AssignBinding(BasisSettingsDefaults.AudioDebugShowVolume);

            PanelToggle toggleRingBuffer = PanelToggle.CreateNewEntry(audioDebugGroup.ContentParent);
            toggleRingBuffer.Descriptor.SetTitle("Ring Buffer");
            toggleRingBuffer.Descriptor.SetDescription("Show voice ring buffer fill level and state.");
            toggleRingBuffer.AssignBinding(BasisSettingsDefaults.AudioDebugShowRingBuffer);

            PanelToggle toggleJitter = PanelToggle.CreateNewEntry(audioDebugGroup.ContentParent);
            toggleJitter.Descriptor.SetTitle("Jitter Buffer");
            toggleJitter.Descriptor.SetDescription("Show jitter buffer packets, fill state, and playback status.");
            toggleJitter.AssignBinding(BasisSettingsDefaults.AudioDebugShowJitter);

            PanelToggle toggleSilence = PanelToggle.CreateNewEntry(audioDebugGroup.ContentParent);
            toggleSilence.Descriptor.SetTitle("Silence Tracking");
            toggleSilence.Descriptor.SetDescription("Show silence duration and gap detection.");
            toggleSilence.AssignBinding(BasisSettingsDefaults.AudioDebugShowSilence);

            PanelToggle toggleViseme = PanelToggle.CreateNewEntry(audioDebugGroup.ContentParent);
            toggleViseme.Descriptor.SetTitle("Viseme Driver");
            toggleViseme.Descriptor.SetDescription("Show viseme/lip-sync driver state.");
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
                infoGroup.SetTitle("Build & Environment");
                infoGroup.SetDescription("Useful identifiers for debugging builds.");
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

            // One reset button for this whole page
            AddResetPageButton(container, "Developer", ResetDeveloperDefaults);

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
            BasisSettingsDefaults.VisualState.SetValue("off");
            BasisSettingsDefaults.EnableStatistics.ResetToDefault();
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
