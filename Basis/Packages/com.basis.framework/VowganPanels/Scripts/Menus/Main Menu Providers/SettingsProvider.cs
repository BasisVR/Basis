using UnityEngine;

namespace Basis.VowganUI
{
    public class SettingsProvider : BasisMenuActionProvider<BasisMainMenu>
    {

        [RuntimeInitializeOnLoadMethod]
        public static void AddToMenu()
        {
            BasisMenuBase<BasisMainMenu>.AddProvider(new SettingsProvider());
        }

        public override string Title => "Settings";
        public override Sprite Icon => null;
        public override int Order => 0;

        public override void RunAction()
        {
            if (BasisMainMenu.ActiveMenuName == Title) return;

            BasisMenuPanel panel = BasisMainMenu.CreateActiveMenu(
                BasisMenuPanel.PanelData.Standard(Title),
                BasisMenuPanel.Styles.Page);
            BoundButton?.BindActiveStateToAddressablesInstance(panel);

            PanelLayoutContainer layout = PanelLayoutContainer.CreateNew(panel.ContentParent, LayoutDirection.Vertical);
            layout.ChildLayoutOptions = new LayoutContainerOptions
            {
                Alignment = TextAnchor.UpperLeft,
                Constrained = false,
                StretchItemWidth = true,
                StretchItemHeight = false,
                SpreadItemWidth = true,
                SpreadItemHeight = false,
            };
            layout.ApplyLayoutOptions();

            layout.LayoutGroup.padding = new RectOffset(
                40,
                40,
                10,
                10);
            layout.LayoutGroup.spacing = 10;

            PanelElement group = PanelElement.CreateGroup(layout.ContentParent);
            group.SetTitle("Volume Mixer");

            PanelSlider sliderMainVolume = PanelSlider.CreateNew(group.ContentParent);
            sliderMainVolume.SetSliderSettings(new PanelSlider.SliderSettings
            {
                Title = "Main Volume",
                SliderMin = 0,
                SliderMax = 100,
                UseWholeNumbers = true,
                DecimalPlaces = 0,
                DisplayMode = ValueDisplayMode.Percentage,
            });
            sliderMainVolume.BindValue("main volume");

            PanelSlider sliderMenuVolume = PanelSlider.CreateNew(group.ContentParent);
            sliderMenuVolume.SetSliderSettings(new PanelSlider.SliderSettings
            {
                Title = "Menu Volume",
                SliderMin = 0,
                SliderMax = 100,
                UseWholeNumbers = true,
                DecimalPlaces = 0,
                DisplayMode = ValueDisplayMode.Percentage,
            });
            sliderMenuVolume.BindValue("menu volume");

            /*
            PanelSlider.CreateNew(layout.ContentParent);
            PanelToggle.CreateNew(layout.ContentParent);
            PanelDropdown.CreateNew(layout.ContentParent);
            */

        }
    }
}
