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
            BasisMenuPanel panel = BasisMainMenu.CreateActiveMenu(
                BasisMenuPanel.PanelData.Standard(Title),
                BasisMenuPanel.Styles.Page);
            BoundButton?.BindActiveStateToAddressablesInstance(panel);

            PanelLayoutContainer layout = PanelLayoutContainer.CreateNew(panel.ContentParent, LayoutDirection.Vertical);
            layout.ChildLayoutOptions = new LayoutContainerOptions
            {
                Alignment = TextAnchor.UpperLeft,
                Constrained = false,
                StretchItemWidth = false,
                StretchItemHeight = false,
                SpreadItemWidth = true,
                SpreadItemHeight = true,
            };

            PanelSlider.CreateNew(layout.ContentParent);
            PanelSlider.CreateNew(layout.ContentParent);
            PanelToggle.CreateNew(layout.ContentParent);
            PanelDropdown.CreateNew(layout.ContentParent);

        }
    }
}
