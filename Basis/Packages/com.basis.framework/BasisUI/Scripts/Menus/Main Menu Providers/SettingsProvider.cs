using System.Collections.Generic;
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
        public override Sprite Icon => AddressableAssets.GetSprite(AddressableAssets.Sprites.Settings);
        public override bool IconIsAddressable => true;
        public override int Order => 0;


        public override void RunAction()
        {
            if (BasisMainMenu.ActiveMenuTitle == Title) return;

            BasisMenuPanel panel = BasisMainMenu.CreateActiveMenu(
                BasisMenuPanel.PanelData.Standard(Title),
                BasisMenuPanel.PanelStyles.Page);

            BoundButton?.BindActiveStateToAddressablesInstance(panel);

            PanelTabGroup tabGroup = PanelTabGroup.CreateNew(panel.Descriptor.ContentParent, LayoutDirection.Vertical);

            tabGroup.AddTab("General", null, GeneralTab(tabGroup));
            tabGroup.AddTab("Audio", null, AudioTab(tabGroup));
            tabGroup.AddTab("Graphics", null, GraphicsTab(tabGroup));
            tabGroup.AddTab("Developer", null, SettingsTab(tabGroup));

            tabGroup.AssignBinding(new BasisSettingsBinding<int>("BasisVR/SettingsTabs"));

            panel.Descriptor.ForceRebuild();
        }

        public static PanelTabPage GeneralTab(PanelTabGroup tabGroup)
        {
            PanelTabPage tab = PanelTabPage.CreateVertical(tabGroup.Descriptor.ContentParent);
            PanelElementDescriptor descriptor = tab.Descriptor;

            descriptor.SetTitle("General Settings");
            RectTransform container = descriptor.ContentParent;

            PanelElementDescriptor settingsGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            settingsGroup.SetTitle("Settings");
            settingsGroup.SetDescription("General settings for testing purposes.");

            PanelDropdown dropdownQualityLevel = PanelDropdown.CreateNewEntry(settingsGroup);
            dropdownQualityLevel.Descriptor.SetTitle("Quality Level");
            dropdownQualityLevel.AssignEntries(new List<string>(){"Very Low", "Low", "Medium", "High", "Ultra"});
            dropdownQualityLevel.AssignBinding(BasisSettingsDefaults.QualityLevel);

            PanelToggle toggleInvertMouse = PanelToggle.CreateNewEntry(settingsGroup);
            toggleInvertMouse.Descriptor.SetTitle("Invert Mouse");
            toggleInvertMouse.AssignBinding(BasisSettingsDefaults.InvertMouse);

            PanelToggle toggleMicrophoneDenoiser = PanelToggle.CreateNewEntry(settingsGroup);
            toggleMicrophoneDenoiser.Descriptor.SetTitle("Microphone Denoiser");
            toggleMicrophoneDenoiser.AssignBinding(BasisSettingsDefaults.MicrophoneDenoiser);

            PanelToggle toggleDebugVisuals = PanelToggle.CreateNewEntry(settingsGroup);
            toggleDebugVisuals.Descriptor.SetTitle("Debug Visuals");
            toggleDebugVisuals.AssignBinding(BasisSettingsDefaults.DebugVisuals);

            PanelElementDescriptor sliderGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            sliderGroup.SetTitle("Slider Settings");
            sliderGroup.SetDescription("Test category for settings with sliders in them.");

            PanelSlider sliderAvatarRange = PanelSlider.CreateEntryAndBind(sliderGroup,
                PanelSlider.SliderSettings.Distance("Avatar Visibility Range", 100),
                BasisSettingsDefaults.AvatarRange);

            PanelSlider sliderHearingRange = PanelSlider.CreateEntryAndBind(sliderGroup,
                PanelSlider.SliderSettings.Distance("Hearing Range" , 25),
                BasisSettingsDefaults.HearingRange);

            descriptor.ForceRebuild();
            return tab;
        }

        public static PanelTabPage AudioTab(PanelTabGroup tabGroup)
        {
            PanelTabPage tab = PanelTabPage.CreateVertical(tabGroup.Descriptor.ContentParent);
            PanelElementDescriptor descriptor = tab.Descriptor;

            descriptor.SetTitle("Volume Mixer");
            RectTransform container = descriptor.ContentParent;

            PanelElementDescriptor settingsGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            settingsGroup.SetTitle("Volume Mixer");
            settingsGroup.SetDescription("Various volume options for purposes.");

            PanelSlider sliderMainVolume = PanelSlider.CreateEntryAndBind(settingsGroup,
                PanelSlider.SliderSettings.Percentage("Main Volume"),
                BasisSettingsDefaults.MainVolume);

            PanelSlider sliderMenuVolume = PanelSlider.CreateEntryAndBind(settingsGroup,
                PanelSlider.SliderSettings.Percentage("Menu Volume"),
                BasisSettingsDefaults.MenuVolume);

            PanelSlider sliderWorldVolume = PanelSlider.CreateEntryAndBind(settingsGroup,
                PanelSlider.SliderSettings.Percentage("World Volume"),
                BasisSettingsDefaults.WorldVolume);

            PanelSlider sliderPlayerVolume = PanelSlider.CreateEntryAndBind(settingsGroup,
                PanelSlider.SliderSettings.Percentage("Player Volume"),
                BasisSettingsDefaults.PlayerVolume);

            descriptor.ForceRebuild();
            return tab;
        }

        public static PanelTabPage GraphicsTab(PanelTabGroup tabGroup)
        {
            PanelTabPage tab = PanelTabPage.CreateVertical(tabGroup.Descriptor.ContentParent);
            PanelElementDescriptor descriptor = tab.Descriptor;

            PanelSlider.CreateNew(descriptor.ContentParent);
            PanelSlider.CreateNew(descriptor.ContentParent);

            descriptor.ForceRebuild();
            return tab;
        }

        public static PanelTabPage SettingsTab(PanelTabGroup tabGroup)
        {
            PanelTabPage tab = PanelTabPage.CreateVertical(tabGroup.Descriptor.ContentParent);
            PanelElementDescriptor descriptor = tab.Descriptor;

            PanelSlider.CreateNew(descriptor.ContentParent);
            PanelSlider.CreateNew(descriptor.ContentParent);

            descriptor.ForceRebuild();
            return tab;
        }

    }
}
