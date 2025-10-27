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
        public override Sprite Icon => AddressableAssets.GetSprite(AddressableAssets.Sprites.Settings);
        public override bool IconIsAddressable => true;
        public override int Order => 0;

        public override void RunAction()
        {
            if (BasisMainMenu.ActiveMenuTitle == Title) return;

            BasisMenuPanel panel = BasisMainMenu.CreateActiveTabMenu(
                BasisMenuPanel.PanelData.Standard(Title),
                BasisMenuPanel.Styles.Page, out PanelTabGroup tabGroup);
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
                20,
                20,
                10,
                10);
            layout.LayoutGroup.spacing = 10;

            tabGroup.AddTab("General", null, false, GeneralTab(layout.ContentParent));
            tabGroup.AddTab("Audio", null, false, AudioTab(layout.ContentParent));
            tabGroup.AddTab("Graphics", null, false, GraphicsTab(layout.ContentParent));
            tabGroup.AddTab("Settings", null, false, SettingsTab(layout.ContentParent));


            /*
            PanelSlider.CreateNew(layout.ContentParent);
            PanelToggle.CreateNew(layout.ContentParent);
            PanelDropdown.CreateNew(layout.ContentParent);
            */

            tabGroup.BindValue("BasisVR/SettingsTabs");
        }

        public static PanelLayoutContainer GeneralTab(Component parent)
        {
            PanelLayoutContainer tab = PanelLayoutContainer.CreateNew(parent, LayoutDirection.Vertical);

            PanelSlider.CreateNew(tab.ContentParent);
            PanelSlider.CreateNew(tab.ContentParent);

            return tab;
        }

        public static PanelLayoutContainer AudioTab(Component parent)
        {
            PanelLayoutContainer tab = PanelLayoutContainer.CreateNew(parent, LayoutDirection.Vertical);
            tab.ChildLayoutOptions = new LayoutContainerOptions
            {
                Alignment = TextAnchor.UpperLeft,
                Constrained = false,
                StretchItemWidth = true,
                StretchItemHeight = false,
                SpreadItemWidth = false,
                SpreadItemHeight = false
            };
            tab.ApplyLayoutOptions();


            PanelElement group = PanelTabPageGroup.CreateNew(tab.ContentParent);
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


            return tab;
        }

        public static PanelLayoutContainer GraphicsTab(Component parent)
        {
            PanelLayoutContainer tab = PanelLayoutContainer.CreateNew(parent, LayoutDirection.Vertical);

            PanelSlider.CreateNew(tab.ContentParent);
            PanelSlider.CreateNew(tab.ContentParent);

            return tab;
        }

        public static PanelLayoutContainer SettingsTab(Component parent)
        {
            PanelLayoutContainer tab = PanelLayoutContainer.CreateNew(parent, LayoutDirection.Vertical);

            PanelSlider.CreateNew(tab.ContentParent);
            PanelSlider.CreateNew(tab.ContentParent);

            return tab;
        }

    }
}
