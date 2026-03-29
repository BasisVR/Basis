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

        [RuntimeInitializeOnLoadMethod]
        public static void AddToMenu()
        {
            BasisMenuBase<BasisMainMenu>.AddProvider(new SettingsProvider());
#if !BASIS_DISABLE_MICROPHONE
            SMDMicrophone.OnMicrophoneSettingsChanged += SyncUiFromSnapshot;
#endif
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
            AddLazyTab(tabGroup, "Downloads & Cache", () => SettingsProviderStorage.StorageTab(tabGroup));
            AddLazyTab(tabGroup, "Developer", () => DeveloperTab(tabGroup));


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
                        BasisMainMenu.OpenWithProvider(StaticTitle);
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
            BasisSettingsDefaults.UseViewConeAvatars.ResetToDefault();
            BasisSettingsDefaults.ViewConeAngle.ResetToDefault();
            BasisSettingsDefaults.HearingRange.ResetToDefault();
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

            PanelSlider sliderMainVolume = PanelSlider.CreateAndBind(
                container,
                PanelSlider.SliderSettings.Percentage("Main Volume"),
                BasisSettingsDefaults.MainVolume);
            sliderMainVolume.Descriptor.SetTitle("Master Volume");
            sliderMainVolume.Descriptor.SetDescription("Overall game volume.");

            // MIXER GROUP
            PanelElementDescriptor mixerGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            mixerGroup.SetTitle("Volume Mixer");
            mixerGroup.SetDescription("Control individual channel volumes.");

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

            // Remote Players (Spatial Audio)
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

            PanelElementDescriptor renderingGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            renderingGroup.SetTitle("Rendering");
            renderingGroup.SetDescription("Resolution, HDR and performance-related options.");

            PanelDropdown dropdownHDR = PanelDropdown.CreateNewEntry(renderingGroup.ContentParent);
            dropdownHDR.Descriptor.SetTitle("HDR Support");
            dropdownHDR.AssignEntries(new List<string> { "Off", "32bit", "64bit" });
            dropdownHDR.AssignBinding(BasisSettingsDefaults.HDRSupport);

            PanelDropdown dropdownMemoryAllocation = PanelDropdown.CreateNewEntry(renderingGroup.ContentParent);
            dropdownMemoryAllocation.Descriptor.SetTitle("Memory Allocation");
            dropdownMemoryAllocation.AssignEntries(new List<string> { "Dynamic", "256", "512", "1024", "2048", "4096", "8192" });
            dropdownMemoryAllocation.AssignBinding(BasisSettingsDefaults.MemoryAllocation);

            PanelSlider sliderRenderResolution = PanelSlider.CreateEntryAndBind(
                renderingGroup.ContentParent,
                new PanelSlider.SliderSettings("Render Scale", "", 0, 1.5f, false, 3, ValueDisplayMode.percentageFromZero),
                BasisSettingsDefaults.RenderResolution);

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

            PanelElementDescriptor advancedGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            advancedGroup.SetTitle("Advanced Rendering");
            advancedGroup.SetDescription("Change how things look vs how smooth they run.");

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

            PanelElementDescriptor lodGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            lodGroup.SetTitle("LOD Bias");
            lodGroup.SetDescription("Higher = less detail at distance, better performance.");

            PanelSlider sliderMeshLOD = PanelSlider.CreateEntryAndBind(
                lodGroup.ContentParent,
                new PanelSlider.SliderSettings("Avatar",
                    "",
                    0, 1, false, 3, ValueDisplayMode.Percentage),
                BasisSettingsDefaults.AvatarMeshLOD);

            PanelSlider sliderGlobalMeshLOD = PanelSlider.CreateEntryAndBind(
                lodGroup.ContentParent,
                new PanelSlider.SliderSettings("World",
                    "",
                    0, 100, true, 0, ValueDisplayMode.Percentage),
                BasisSettingsDefaults.GlobalMeshLOD);

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
            BasisSettingsDefaults.AvatarMeshLOD.ResetToDefault();
            BasisSettingsDefaults.GlobalMeshLOD.ResetToDefault();

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

            PanelElementDescriptor infoGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            infoGroup.SetTitle("Build & Environment");
            infoGroup.SetDescription("Useful identifiers for debugging builds.");

            CreateBuildInfoSection(infoGroup.ContentParent);

            // Network & Statistics (live-updating)
            SettingsProviderNetworkTab.BuildNetworkStatsGroup(container, out var netUpdater);

            // One reset button for this whole page
            AddResetPageButton(container, "Developer", ResetDeveloperDefaults);

            // Inline console
            SettingsProviderConsoleTab.BuildConsoleUI(container);

            descriptor.ForceRebuild();
            return tab;
        }

        private static void ResetDeveloperDefaults()
        {
            BasisSettingsDefaults.DebugVisuals.ResetToDefault();
            BasisSettingsDefaults.VisualState.SetValue("off");
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
