using Basis.Scripts.Device_Management;
using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

namespace Basis.BasisUI
{
    public partial class SettingsProvider : BasisMenuActionProvider<BasisMainMenu>
    {
        [RuntimeInitializeOnLoadMethod]
        public static void AddToMenu()
        {
            BasisMenuBase<BasisMainMenu>.AddProvider(new SettingsProvider());
            SMDMicrophone.OnMicrophoneSettingsChanged += SyncUiFromSnapshot;
        }
        public static string StaticTitle => "Settings";
        public override string Title => StaticTitle;
        public override string IconAddress => AddressableAssets.Sprites.Settings;
        public override int Order => 0;
        public override bool Hidden => false;

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

            tabGroup.AddTab("General", null, GeneralTab(tabGroup));
            tabGroup.AddTab("Audio", null, AudioTab(tabGroup));
            tabGroup.AddTab("Graphics", null, GraphicsTab(tabGroup));
            tabGroup.AddTab("Avatar", null, AvatarTab(tabGroup));
            tabGroup.AddTab("Calibration", null, SettingsProviderIK.IKTab(tabGroup));
            tabGroup.AddTab("Bindings", null, SettingsProviderControllerConfig.OpenControllerConfig(tabGroup));
            tabGroup.AddTab("Console", null, SettingsProviderConsoleTab.ConsoleTab(tabGroup));
            tabGroup.AddTab("Admin", null, SettingsProviderAdminTab.AdminTab(tabGroup));
            tabGroup.AddTab("Developer", null, DeveloperTab(tabGroup));

            tabGroup.AddExtraAction("Switch To OpenVR", SwitchToOpenVR);
            tabGroup.AddExtraAction("Switch To OpenXR", SwitchToOpenXR);
            tabGroup.AddExtraAction("Switch To Desktop", SwitchToDesktop);

            panel.Descriptor.ForceRebuild();
        }

        public void SwitchToOpenVR()
        {
            BasisMainMenu.Instance.OpenDialogue("Switch To OpenVR",
                "Are you sure you want to swap to OpenVR?",
                "Switch To OpenVR",
                "Cancel",
                async value =>
                {
                    if (!value) return;
                    await BasisDeviceManagement.Instance.SwitchSetMode(BasisConstants.OpenVRLoader);
                });
        }

        public void SwitchToOpenXR()
        {
            BasisMainMenu.Instance.OpenDialogue("Switch To OpenXR",
                "Are you sure you want to swap to OpenXR?",
                "Switch To OpenXR",
                "Cancel",
                async value =>
                {
                    if (!value) return;
                    await BasisDeviceManagement.Instance.SwitchSetMode(BasisConstants.OpenXRLoader);
                });
        }

        public void SwitchToDesktop()
        {
            BasisMainMenu.Instance.OpenDialogue("Switch To Desktop",
                "Are you sure you want to swap to Desktop?",
                "Switch To Desktop",
                "Cancel",
                async value =>
                {
                    if (!value) return;
                    await BasisDeviceManagement.Instance.SwitchSetMode(BasisConstants.Desktop);
                });
        }

        // ------------------
        // GENERAL TAB
        // ------------------
        public static PanelTabPage GeneralTab(PanelTabGroup tabGroup)
        {
            PanelTabPage tab = PanelTabPage.CreateVertical(tabGroup.Descriptor.ContentParent);
            PanelElementDescriptor descriptor = tab.Descriptor;
            descriptor.SetIcon(AddressableAssets.Sprites.Settings);
            descriptor.SetTitle("General Settings");

            RectTransform container = descriptor.ContentParent;

            PanelElementDescriptor generalGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            generalGroup.SetIcon(AddressableAssets.Sprites.Settings);
            generalGroup.SetTitle("Gameplay & Input");
            generalGroup.SetDescription("General controls and comfort settings.");

            PanelToggle toggleInvertMouse = PanelToggle.CreateNewEntry(generalGroup);
            toggleInvertMouse.Descriptor.SetTitle("Invert Mouse");
            toggleInvertMouse.AssignBinding(BasisSettingsDefaults.InvertMouse);

            PanelSlider mousesensitivty = PanelSlider.CreateEntryAndBind(
                generalGroup,
                PanelSlider.SliderSettings.Advanced("Mouse Sensitivity", 0, 2f, false, 2, ValueDisplayMode.Percentage),
                BasisSettingsDefaults.mousesensitivty);

            PanelToggle smoothlocomotion = PanelToggle.CreateNewEntry(generalGroup);
            smoothlocomotion.Descriptor.SetTitle("Use SnapTurn locomotion");
            smoothlocomotion.AssignBinding(BasisSettingsDefaults.usesnapturn);

            PanelSlider sliderSnapTurnAngle = PanelSlider.CreateEntryAndBind(
                generalGroup,
                PanelSlider.SliderSettings.Advanced("Snap Turn Angle", 0, 120, true, 0, ValueDisplayMode.Degrees),
                BasisSettingsDefaults.SnapTurnAngle);

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

            PanelSlider sliderMicrophoneRange = PanelSlider.CreateEntryAndBind(
                rangeGroup,
                PanelSlider.SliderSettings.Distance("Microphone Range", 25),
                BasisSettingsDefaults.MicrophoneRange);

            PanelElementDescriptor generalGroupDeadZone =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);

            generalGroupDeadZone.SetTitle("General");
            generalGroupDeadZone.SetDescription("Basic filtering applied to the whole stick. (excluding look)");

            PanelSlider controllerDeadZoneSlider = PanelSlider.CreateEntryAndBind(
                generalGroupDeadZone,
                PanelSlider.SliderSettings.Advanced("Radial Dead Zone", 0f, 1f, false, 3, ValueDisplayMode.Percentage),
                BasisSettingsDefaults.ControllerDeadZone);

            PanelElementDescriptor horizontalGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);

            horizontalGroup.SetTitle("Horizontal (Yaw) Comfort");
            horizontalGroup.SetDescription("Prevents forward/back stick pressure from causing accidental left/right drift (\"butterfly wings\").");

            PanelSlider minHorizontalDeadZoneSlider = PanelSlider.CreateEntryAndBind(
                horizontalGroup,
                PanelSlider.SliderSettings.Advanced("X Dead Zone (Min)", 0f, 1f, false, 3, ValueDisplayMode.Percentage),
                BasisSettingsDefaults.Basexdeadzone);

            PanelSlider horizontalGateStrengthSlider = PanelSlider.CreateEntryAndBind(
                horizontalGroup,
                PanelSlider.SliderSettings.Advanced("X Gate (At Full Y)", 0f, 1f, false, 3, ValueDisplayMode.Percentage),
                BasisSettingsDefaults.Extraxdeadzoneatfully);

            PanelSlider wingCurveSlider = PanelSlider.CreateEntryAndBind(
                horizontalGroup,
                PanelSlider.SliderSettings.Advanced("Gate Curve", 0f, 3f, false, 3, ValueDisplayMode.Percentage),
                BasisSettingsDefaults.Wingexponent);

            PanelElementDescriptor verticalGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);

            verticalGroup.SetTitle("Vertical (Pitch / Other)");
            verticalGroup.SetDescription("look joystick Y Dead Zone");

            PanelSlider verticalDeadZoneSlider = PanelSlider.CreateEntryAndBind(
                verticalGroup,
                PanelSlider.SliderSettings.Advanced("Look Y Dead Zone", 0f, 1f, false, 3, ValueDisplayMode.Percentage),
                BasisSettingsDefaults.Ydeadzone);

            controllerDeadZoneSlider.OnValueChanged += _ => UpdatePreview();
            minHorizontalDeadZoneSlider.OnValueChanged += _ => UpdatePreview();
            horizontalGateStrengthSlider.OnValueChanged += _ => UpdatePreview();
            verticalDeadZoneSlider.OnValueChanged += _ => UpdatePreview();
            wingCurveSlider.OnValueChanged += _ => UpdatePreview();

            descriptor.ForceRebuild();
            return tab;
        }

        private static void UpdatePreview()
        {
            // wire up to butterflygatepreview one day
        }

        // ------------------
        // AUDIO TAB
        // ------------------
        public static PanelTabPage AudioTab(PanelTabGroup tabGroup)
        {
            // Ensure current mode snapshot is loaded (and keeps SMDMicrophone.Current accurate)
            SMDMicrophone.LoadInMicrophoneData(BasisDeviceManagement.StaticCurrentMode);

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

            PanelSlider sliderPlayerVolume = PanelSlider.CreateEntryAndBind(
                mixerGroup,
                PanelSlider.SliderSettings.Percentage("Player Volume"),
                BasisSettingsDefaults.PlayerVolume);

            // MICROPHONE GROUP
            PanelElementDescriptor microphoneGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            microphoneGroup.SetTitle("Microphone");
            microphoneGroup.SetDescription("Microphone Related Settings");

            // Snapshot
            SMDMicrophone.MicSettings snap = SMDMicrophone.Current;

            // Microphone Volume (0..1)
             sliderMicrophoneVolume = PanelSlider.CreateEntryAndBind(
                microphoneGroup,
                PanelSlider.SliderSettings.Advanced("Microphone Volume", 0, 1, false, 4, ValueDisplayMode.Percentage),
                BasisSettingsDefaults.MicrophoneVolume);
            sliderMicrophoneVolume.SetValueWithoutNotify(snap.Volume01);

            // IMPORTANT: Use new setters (single-source-of-truth), not Save* and not Selected*
            void MicrophoneVolumeChanged(float value)
            {
                // If mode changes while tab open, keep in sync:
                if (SMDMicrophone.CurrentMode != BasisDeviceManagement.StaticCurrentMode)
                    SMDMicrophone.LoadInMicrophoneData(BasisDeviceManagement.StaticCurrentMode);

                SMDMicrophone.SetVolume(value);
            }
            sliderMicrophoneVolume.SliderComponent.onValueChanged.AddListener(MicrophoneVolumeChanged);

            BasisLocalVolumeMeterUIDescriptor rangeGroup =
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

            // Microphone Denoiser (binding can remain; it will call ValidSettingsChange which calls SetDenoiser)
            PanelToggle toggleMicrophoneDenoiser = PanelToggle.CreateNewEntry(microphoneGroup);
            toggleMicrophoneDenoiser.Descriptor.SetTitle("Microphone Denoiser");
            toggleMicrophoneDenoiser.AssignBinding(BasisSettingsDefaults.MicrophoneDenoiser);

            // Automatic Gain Control (binding remains)
            PanelToggle toggleAGC = PanelToggle.CreateNewEntry(microphoneGroup);
            toggleAGC.Descriptor.SetTitle("Automatic Gain (AGC)");
            toggleAGC.AssignBinding(BasisSettingsDefaults.UseAutomaticGain);

            // Microphone Mode (binding remains)
            PanelDropdown dropdownMicrophoneMode = PanelDropdown.CreateNewEntry(microphoneGroup);
            dropdownMicrophoneMode.Descriptor.SetTitle("Microphone Mode");
            dropdownMicrophoneMode.AssignEntries(new List<string>
            {
                "On Activation",
                "Push To Talk"
            });
            dropdownMicrophoneMode.AssignBinding(BasisSettingsDefaults.MicrophoneMode);

            // Microphone Icon (binding remains)
            PanelDropdown dropdownMicrophoneIcon = PanelDropdown.CreateNewEntry(microphoneGroup);
            dropdownMicrophoneIcon.Descriptor.SetTitle("Microphone Icon");
            dropdownMicrophoneIcon.AssignEntries(new List<string>
            {
                "AlwaysVisible",
                "ActivityDetection",
                "Hidden"
            });
            dropdownMicrophoneIcon.AssignBinding(BasisSettingsDefaults.MicrophoneIcon);

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
                {
                    SMDMicrophone.LoadInMicrophoneData(BasisDeviceManagement.StaticCurrentMode);
                }

                var s = SMDMicrophone.Current;
                SMDMicrophone.SetLimiter(v, s.LimitKnee);
            }
            void LimitKneeChanged(float v)
            {
                if (SMDMicrophone.CurrentMode != BasisDeviceManagement.StaticCurrentMode)
                {
                    SMDMicrophone.LoadInMicrophoneData(BasisDeviceManagement.StaticCurrentMode);
                }

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



            descriptor.ForceRebuild();
            return tab;
        }
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
        /// <summary>
        /// allows us to get up to date information directly from the microphone
        /// </summary>
        /// <param name="s"></param>
        public static void SyncUiFromSnapshot(SMDMicrophone.MicSettings s)
        {
            if (BasisMainMenu.ActiveMenuTitle == SettingsProvider.StaticTitle)
            {
                if (sliderMicrophoneVolume != null)
                {
                    sliderMicrophoneVolume.SetValueWithoutNotify(s.Volume01);
                }

                if (dropdownMicrophoneSelection != null)
                {
                    dropdownMicrophoneSelection.SetValueWithoutNotify(s.Microphone);
                }

                if (sliderLimitThreshold != null)
                {
                    sliderLimitThreshold.SetValueWithoutNotify(s.LimitThreshold);
                }

                if (sliderLimitKnee != null)
                {
                    sliderLimitKnee.SetValueWithoutNotify(s.LimitKnee);
                }

                if (sliderDenoiseWet != null)
                {
                    sliderDenoiseWet.SetValueWithoutNotify(s.DenoiseWet);
                }

                if (sliderDenoiseMakeup != null)
                {
                    sliderDenoiseMakeup.SetValueWithoutNotify(s.DenoiseMakeupDb);
                }

                if (sliderAgcTarget != null)
                {
                    sliderAgcTarget.SetValueWithoutNotify(s.AgcTargetRms);
                }

                if (sliderAgcMaxGain != null)
                {
                    sliderAgcMaxGain.SetValueWithoutNotify(s.AgcMaxGainDb);
                }

                if (sliderAgcAttack != null)
                {
                    sliderAgcAttack.SetValueWithoutNotify(s.AgcAttack);
                }

                if (sliderAgcRelease != null)
                {
                    sliderAgcRelease.SetValueWithoutNotify(s.AgcRelease);
                }
            }
        }
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

            dropdownScreenMode.Descriptor.SetTitle("ScreenMode");
            dropdownScreenMode.AssignEntries(screenModeOptions);
            dropdownScreenMode.DropdownComponent.onValueChanged.AddListener(ScreenMode);
            dropdownScreenMode.DropdownComponent.SetValueWithoutNotify(GetIndexFromScreenMode(Screen.fullScreenMode));

            PanelElementDescriptor advancedGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            advancedGroup.SetTitle("Advanced Rendering");
            advancedGroup.SetDescription("Foveation, FOV and LOD controls.");

            PanelSlider sliderFoveatedRendering = PanelSlider.CreateEntryAndBind(
                advancedGroup.ContentParent,
                PanelSlider.SliderSettings.Advanced("Foveated Rendering", 0, 1, false, 1, ValueDisplayMode.Percentage),
                BasisSettingsDefaults.FoveatedRendering);

            PanelSlider sliderFieldOfView = PanelSlider.CreateEntryAndBind(
                advancedGroup.ContentParent,
                PanelSlider.SliderSettings.Degrees("Field Of View", BasisSettingsDefaults.FOV_MIN, BasisSettingsDefaults.FOV_MAX, true, 0),
                BasisSettingsDefaults.FieldOfView);

            PanelSlider sliderMeshLOD = PanelSlider.CreateEntryAndBind(
                advancedGroup.ContentParent,
                new PanelSlider.SliderSettings("Avatar LOD Multiplier", "", 0, 1, false, 3, ValueDisplayMode.Percentage),
                BasisSettingsDefaults.AvatarMeshLOD);

            PanelSlider sliderGlobalMeshLOD = PanelSlider.CreateEntryAndBind(
                advancedGroup.ContentParent,
                PanelSlider.SliderSettings.Percentage("World LOD Multiplier"),
                BasisSettingsDefaults.GlobalMeshLOD);

            descriptor.ForceRebuild();
            return tab;
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
        // DEVELOPER TAB
        // ------------------
        public static PanelTabPage DeveloperTab(PanelTabGroup tabGroup)
        {
            PanelTabPage tab = PanelTabPage.CreateVertical(tabGroup.Descriptor.ContentParent);
            PanelElementDescriptor descriptor = tab.Descriptor;

            descriptor.SetTitle("Developer & Debug");
            RectTransform container = descriptor.ContentParent;

            PanelElementDescriptor debugGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            debugGroup.SetTitle("Debug Visuals");
            debugGroup.SetDescription("Debug Systems running through visuals in 3D space");

            PanelToggle toggleDebugVisuals = PanelToggle.CreateNewEntry(debugGroup.ContentParent);
            toggleDebugVisuals.Descriptor.SetTitle("Debug Visuals Enabled");
            toggleDebugVisuals.AssignBinding(BasisSettingsDefaults.DebugVisuals);

            PanelDropdown Visual = PanelDropdown.CreateNewEntry(debugGroup.ContentParent);
            Visual.Descriptor.SetTitle("Visual Helpers");
            Visual.AssignEntries(new List<string> { "Off", "All Visuals", "Only Avatar Distance" });
            Visual.AssignBinding(BasisSettingsDefaults.VisualState);

            PanelElementDescriptor infoGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            infoGroup.SetTitle("Build & Environment");
            infoGroup.SetDescription("Useful identifiers for debugging builds.");

            CreateBuildInfoSection(infoGroup.ContentParent);

            descriptor.ForceRebuild();
            return tab;
        }

        public static PanelTabPage AvatarTab(PanelTabGroup tabGroup)
        {
            PanelTabPage tab = PanelTabPage.CreateVertical(tabGroup.Descriptor.ContentParent);
            PanelElementDescriptor descriptor = tab.Descriptor;

            descriptor.SetTitle("Avatar Settings");
            RectTransform container = descriptor.ContentParent;

            PanelElementDescriptor debugGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            debugGroup.SetTitle("Avatar Settings");
            debugGroup.SetDescription("Configuration settings for avatars.");

            PanelSlider AvatarDownloadSize = PanelSlider.CreateEntryAndBind(
                debugGroup.ContentParent,
                PanelSlider.SliderSettings.Advanced("Avatar Download Size", 5, 1024, false, 0, ValueDisplayMode.MemorySize),
                BasisSettingsDefaults.AvatarDownloadSize);

            descriptor.ForceRebuild();
            return tab;
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
