using Basis.Scripts.Device_Management;
using Basis.Scripts.UI.UI_Panels;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

namespace Basis.BasisUI
{
    public class SettingsProvider : BasisMenuActionProvider<BasisMainMenu>
    {
        [RuntimeInitializeOnLoadMethod]
        public static void AddToMenu()
        {
            BasisMenuBase<BasisMainMenu>.AddProvider(new SettingsProvider());
        }

        public override string Title => "Settings";
        public override string IconAddress => AddressableAssets.Sprites.Settings;
        public override int Order => 0;

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
            tabGroup.AddTab("Developer", null, DeveloperTab(tabGroup));
            tabGroup.AddTab("Avatar", null, AvatarTab(tabGroup));

            tabGroup.AddExtraAction("Admin", OpenAdminPanel);
            tabGroup.AddExtraAction("Console", OpenConsoleLogger);
            tabGroup.AddExtraAction("Bindings", OpenControllerConfig);

            tabGroup.AddExtraAction("Switch To OpenVR", SwitchToOpenVR);
            tabGroup.AddExtraAction("Switch To OpenXR", SwitchToOpenXR);
            tabGroup.AddExtraAction("Switch To Desktop", SwitchToDesktop);

            // tabGroup.AddExtraAction("Statistics", OpenControllerConfig);

            tabGroup.AssignBinding(new BasisSettingsBinding<int>("BasisVR/SettingsTabs"));

            panel.Descriptor.ForceRebuild();
        }

        public async void SwitchToOpenVR()
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

        public async void SwitchToOpenXR()
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

        public async void SwitchToDesktop()
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

        public static void OpenAdminPanel()
        {
            BasisMainMenu.Close();
            BasisUIAdminPanel.OpenThisMenu(BasisUIAdminPanel.Path);
        }

        public static void OpenConsoleLogger()
        {
            BasisMainMenu.Close();
            BasisUIBase.OpenMenuNow("BasisConsoleLogger");
        }

        public static void OpenControllerConfig()
        {
            BasisMainMenu.Close();
            BasisUIActionBindingsPanel.OpenMenuNow("Packages/com.basis.sdk/Prefabs/UI/ControllerConfig.prefab");
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

            // GENERAL INPUT / GAMEPLAY GROUP
            PanelElementDescriptor generalGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            generalGroup.SetIcon(AddressableAssets.Sprites.Settings);
            generalGroup.SetTitle("Gameplay & Input");
            generalGroup.SetDescription("General controls and comfort settings.");

            // Invert Mouse
            PanelToggle toggleInvertMouse = PanelToggle.CreateNewEntry(generalGroup);
            toggleInvertMouse.Descriptor.SetTitle("Invert Mouse");
            toggleInvertMouse.AssignBinding(BasisSettingsDefaults.InvertMouse);

            // Controller Dead Zone
            PanelSlider sliderControllerDeadZone = PanelSlider.CreateEntryAndBind(
                generalGroup,
                PanelSlider.SliderSettings.Percentage("Controller Dead Zone"),
                BasisSettingsDefaults.ControllerDeadZone);

            // Snap Turn Angle
            PanelSlider sliderSnapTurnAngle = PanelSlider.CreateEntryAndBind(
                generalGroup,
                PanelSlider.SliderSettings.Advanced("Snap Turn Angle", -1, 120, true, 0, ValueDisplayMode.Degrees),
                BasisSettingsDefaults.SnapTurnAngle);

            // Seated Mode
            PanelDropdown dropdownSeatedMode = PanelDropdown.CreateNewEntry(generalGroup);
            dropdownSeatedMode.Descriptor.SetTitle("Seated Mode");
            // Options inferred from default
            dropdownSeatedMode.AssignEntries(new List<string>
            {
                "Standing Mode",
                "Seated Mode"
            });
            dropdownSeatedMode.AssignBinding(BasisSettingsDefaults.SeatedMode);

            // RANGE SETTINGS GROUP
            PanelElementDescriptor rangeGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            rangeGroup.SetTitle("Ranges");
            rangeGroup.SetDescription("Visibility and hearing ranges.");

            // Avatar Visibility Range
            PanelSlider sliderAvatarRange = PanelSlider.CreateEntryAndBind(
                rangeGroup,
                PanelSlider.SliderSettings.Distance("Avatar Visibility Range", 100),
                BasisSettingsDefaults.AvatarRange);

            // Hearing Range
            PanelSlider sliderHearingRange = PanelSlider.CreateEntryAndBind(
                rangeGroup,
                PanelSlider.SliderSettings.Distance("Hearing Range", 25),
                BasisSettingsDefaults.HearingRange);

            descriptor.ForceRebuild();
            return tab;
        }

        // ------------------
        // AUDIO TAB
        // ------------------
        public static PanelTabPage AudioTab(PanelTabGroup tabGroup)
        {
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

            PanelSlider sliderMicrophoneVolume = PanelSlider.CreateEntryAndBind(
                microphoneGroup,
                PanelSlider.SliderSettings.Advanced("Microphone Volume", 0, 1, false, 4, ValueDisplayMode.Percentage),
                BasisSettingsDefaults.MicrophoneVolume);
            sliderMicrophoneVolume.SetValueWithoutNotify(SMDMicrophone.SelectedVolumeMicrophone);

            void MicrophoneVolumeChanged(float value)
            {
                SMDMicrophone.SaveVolumeSettings(BasisDeviceManagement.StaticCurrentMode, value);
            }

            sliderMicrophoneVolume.SliderComponent.onValueChanged.AddListener(MicrophoneVolumeChanged);

            // Microphone Range
            PanelSlider sliderMicrophoneRange = PanelSlider.CreateEntryAndBind(
                microphoneGroup,
                PanelSlider.SliderSettings.Distance("Microphone Range", 25),
                BasisSettingsDefaults.MicrophoneRange);





            // Microphone Mode
            PanelDropdown dropdownMicrophoneSelection = PanelDropdown.CreateNewEntry(microphoneGroup);
            dropdownMicrophoneSelection.Descriptor.SetTitle("Microphone Selection");
            // Options inferred from default naming – adjust to your actual system values if needed
            dropdownMicrophoneSelection.AssignEntries(SMDMicrophone.MicrophoneDevices.ToList());
            // dropdownMicrophoneSelection.DropdownComponent.value = dropdownMicrophoneSelection.StringValueToIndex(SMDMicrophone.SelectedMicrophone);
            dropdownMicrophoneSelection.SetValueWithoutNotify(SMDMicrophone.SelectedMicrophone);

            void MicrophoneSelectionChanged(string Name)
            {
                SMDMicrophone.SaveMicrophoneData(BasisDeviceManagement.StaticCurrentMode, Name);
            }

            dropdownMicrophoneSelection.OnValueChanged += MicrophoneSelectionChanged;



            // Microphone Denoiser
            PanelToggle toggleMicrophoneDenoiser = PanelToggle.CreateNewEntry(microphoneGroup);
            toggleMicrophoneDenoiser.Descriptor.SetTitle("Microphone Denoiser");
            toggleMicrophoneDenoiser.AssignBinding(BasisSettingsDefaults.MicrophoneDenoiser);

            // Automatic Gain Control
            PanelToggle toggleAGC = PanelToggle.CreateNewEntry(microphoneGroup);
            toggleAGC.Descriptor.SetTitle("Automatic Gain (AGC)");
            toggleAGC.AssignBinding(BasisSettingsDefaults.UseAutomaticGain);

            // Microphone Mode
            PanelDropdown dropdownMicrophoneMode = PanelDropdown.CreateNewEntry(microphoneGroup);
            dropdownMicrophoneMode.Descriptor.SetTitle("Microphone Mode");

            // Options inferred from default naming – adjust to your actual system values if needed
            dropdownMicrophoneMode.AssignEntries(new List<string>
            {
                "On Activation",
                "Push To Talk"
            });
            dropdownMicrophoneMode.AssignBinding(BasisSettingsDefaults.MicrophoneMode);

            // Microphone Icon
            PanelDropdown dropdownMicrophoneIcon = PanelDropdown.CreateNewEntry(microphoneGroup);
            dropdownMicrophoneIcon.Descriptor.SetTitle("Microphone Icon");
            dropdownMicrophoneIcon.AssignEntries(new List<string>
            {
                "AlwaysVisible",
                "ActivityDetection",
                "Hidden"
            });
            dropdownMicrophoneIcon.AssignBinding(BasisSettingsDefaults.MicrophoneIcon);

            descriptor.ForceRebuild();
            return tab;
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

            // QUALITY GROUP
            PanelElementDescriptor qualityGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            qualityGroup.SetTitle("Quality");
            qualityGroup.SetDescription("Overall render quality and post-processing.");

            // Quality Level
            PanelDropdown dropdownQualityLevel = PanelDropdown.CreateNewEntry(qualityGroup.ContentParent);
            dropdownQualityLevel.Descriptor.SetTitle("Quality Level");
            dropdownQualityLevel.AssignEntries(new List<string>
            {
                "Very Low", "Low", "Medium", "High", "Ultra"
            });
            dropdownQualityLevel.AssignBinding(BasisSettingsDefaults.QualityLevel);

            // Shadow Quality
            PanelDropdown dropdownShadowQuality = PanelDropdown.CreateNewEntry(qualityGroup.ContentParent);
            dropdownShadowQuality.Descriptor.SetTitle("Shadow Quality");
            dropdownShadowQuality.AssignEntries(new List<string>
            {
                "Very Low", "Low", "Medium", "High", "Ultra"
            });
            dropdownShadowQuality.AssignBinding(BasisSettingsDefaults.ShadowQuality);

            // Antialiasing
            PanelDropdown dropdownAntialiasing = PanelDropdown.CreateNewEntry(qualityGroup.ContentParent);
            dropdownAntialiasing.Descriptor.SetTitle("Antialiasing");
            dropdownAntialiasing.AssignEntries(new List<string>
            {
                "Off",
                "MSAA 2X",
                "MSAA 4X",
                "MSAA 8X",
                "Linear",
                "Point",
                "FSR",
            });
            dropdownAntialiasing.AssignBinding(BasisSettingsDefaults.Antialiasing);

            // VSync
            PanelDropdown dropdownVSync = PanelDropdown.CreateNewEntry(qualityGroup.ContentParent);
            dropdownVSync.Descriptor.SetTitle("Vertical Sync");
            dropdownVSync.AssignEntries(new List<string>
            {
                "On",
                "Capped",
                "Off",
                "Half",
            });
            dropdownVSync.AssignBinding(BasisSettingsDefaults.VSync);

            // RENDERING GROUP
            PanelElementDescriptor renderingGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            renderingGroup.SetTitle("Rendering");
            renderingGroup.SetDescription("Resolution, HDR and performance-related options.");

            // HDR Support
            PanelDropdown dropdownHDR = PanelDropdown.CreateNewEntry(renderingGroup.ContentParent);
            dropdownHDR.Descriptor.SetTitle("HDR Support");
            dropdownHDR.AssignEntries(new List<string>
            {
                "off",
                "32bit",
                "64bit"
            });
            dropdownHDR.AssignBinding(BasisSettingsDefaults.HDRSupport);

            // Memory Allocation
            PanelDropdown dropdownMemoryAllocation = PanelDropdown.CreateNewEntry(renderingGroup.ContentParent);
            dropdownMemoryAllocation.Descriptor.SetTitle("Memory Allocation");
            dropdownMemoryAllocation.AssignEntries(new List<string>
            {
                "Dynamic",
                "256",
                "512",
                "1024",
                "2048",
                "4096",
                "8192",
            });
            dropdownMemoryAllocation.AssignBinding(BasisSettingsDefaults.MemoryAllocation);

            // Render Scale
            PanelSlider sliderRenderResolution = PanelSlider.CreateEntryAndBind(
                renderingGroup.ContentParent,
                new PanelSlider.SliderSettings("Render Scale", "", 0, 1, false, 3, ValueDisplayMode.Percentage),
                BasisSettingsDefaults.RenderResolution);

            // Resolution (logical / display resolution)
            PanelDropdown dropdownResolution = PanelDropdown.CreateNewEntry(renderingGroup.ContentParent);
            dropdownResolution.Descriptor.SetTitle("Resolution");
            List<Vector2Int> uniqueResolutions = new List<Vector2Int>();
            List<string> resolutionOptions = new List<string>();

            foreach (Resolution res in Screen.resolutions)
            {
                Vector2Int size = new Vector2Int(res.width, res.height);

                // Only add if not already in the list (removes duplicates with different refresh rates)
                if (!uniqueResolutions.Contains(size))
                {
                    uniqueResolutions.Add(size);
                    resolutionOptions.Add(size.x + " x " + size.y);
                }
            }

            // NOTE: in many systems this will be populated by platform code – tweak/remove entries as needed
            dropdownResolution.AssignEntries(resolutionOptions);
            dropdownResolution.AssignBinding(BasisSettingsDefaults.Resolution);

            // Monitor
            PanelDropdown dropdownMonitor = PanelDropdown.CreateNewEntry(renderingGroup.ContentParent);

            List<string> monitorOptions = new List<string>();

            for (int Index = 0; Index < Display.displays.Length; Index++)
            {
                monitorOptions.Add("Monitor " + (Index + 1));
            }

            dropdownMonitor.Descriptor.SetTitle("Monitor");
            dropdownMonitor.AssignEntries(monitorOptions);
            dropdownMonitor.AssignBinding(BasisSettingsDefaults.Monitor);


            // Monitor
            PanelDropdown dropdownScreenMode = PanelDropdown.CreateNewEntry(renderingGroup.ContentParent);
            List<string> screenModeOptions = new List<string>
            {
                "Fullscreen",
                "Borderless Window",
                "Windowed"
            };

            dropdownScreenMode.Descriptor.SetTitle("ScreenMode");
            dropdownScreenMode.AssignEntries(screenModeOptions);
            dropdownScreenMode.AssignBinding(BasisSettingsDefaults.ScreenMode);

            // ADVANCED / FOVEATION GROUP
            PanelElementDescriptor advancedGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            advancedGroup.SetTitle("Advanced Rendering");
            advancedGroup.SetDescription("Foveation, FOV and LOD controls.");

            // Foveated Rendering
            PanelSlider sliderFoveatedRendering = PanelSlider.CreateEntryAndBind(
                advancedGroup.ContentParent,
                PanelSlider.SliderSettings.Advanced("Foveated Rendering", 0, 1, false, 1, ValueDisplayMode.Percentage),
                BasisSettingsDefaults.FoveatedRendering);

            // Field Of View
            PanelSlider sliderFieldOfView = PanelSlider.CreateEntryAndBind(
                advancedGroup.ContentParent,
                PanelSlider.SliderSettings.Degrees("Field Of View", BasisSettingsDefaults.FOV_MIN, BasisSettingsDefaults.FOV_MAX, true, 0),
                BasisSettingsDefaults.FieldOfView);

            // Mesh LOD
            PanelSlider sliderMeshLOD = PanelSlider.CreateEntryAndBind(
                advancedGroup.ContentParent,
                new PanelSlider.SliderSettings("Mesh LOD Bias", "", 0, 1, false, 3, ValueDisplayMode.Percentage),
                BasisSettingsDefaults.MeshLOD);

            // Global Mesh LOD
            PanelSlider sliderGlobalMeshLOD = PanelSlider.CreateEntryAndBind(
                advancedGroup.ContentParent,
                PanelSlider.SliderSettings.Percentage("Mesh Lod Multiplier"),
                BasisSettingsDefaults.GlobalMeshLOD);

            descriptor.ForceRebuild();
            return tab;
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

            // DEBUG VISUALS GROUP
            PanelElementDescriptor debugGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            debugGroup.SetTitle("Debug Visuals");
            debugGroup.SetDescription("Debug rendering modes and overlays.");

            // Debug Visuals Toggle
            PanelToggle toggleDebugVisuals = PanelToggle.CreateNewEntry(debugGroup.ContentParent);
            toggleDebugVisuals.Descriptor.SetTitle("Debug Visuals Enabled");
            toggleDebugVisuals.AssignBinding(BasisSettingsDefaults.DebugVisuals);

            // Visual State Mode
            PanelDropdown dropdownVisualState = PanelDropdown.CreateNewEntry(debugGroup.ContentParent);
            dropdownVisualState.Descriptor.SetTitle("Visual State");
            dropdownVisualState.AssignEntries(new List<string>
            {
                "Off",
                "all visuals",
                "only avatar distance"
            });
            dropdownVisualState.AssignBinding(BasisSettingsDefaults.VisualState);

            descriptor.ForceRebuild();
            return tab;
        }

        // ------------------
        // AVATAR TAB
        // ------------------
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

            // Avatar Scale
            PanelSlider sliderFieldOfView = PanelSlider.CreateEntryAndBind(
                debugGroup.ContentParent,
                PanelSlider.SliderSettings.Advanced("Avatar Scale", 0.1f, 5, false, 2, ValueDisplayMode.Meters),
                BasisSettingsDefaults.AvatarScale);

            sliderFieldOfView.OnValueChanged += AvatarScaleChanged;

            descriptor.ForceRebuild();
            return tab;
        }

        public static void AvatarScaleChanged(float value)
        {
            BasisHeightDriver.SetCustomPlayerHeight(value);
        }
    }
}
