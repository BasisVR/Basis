using Basis.VowganUI;
using UnityEngine;

namespace Basis.VowganUIOld
{
    public class SettingsMenu : MenuPanelBase<HomeRowMenu>
    {


        public override PanelData Data => new PanelData
        {
            Title = "Settings",
            PanelSize = new Vector2(800, 500)
        };

        public override Panel CreatePanel()
        {
            return Instance.Group.CreateRootPanelInGroup(Data, Panel.Styles.Page);
        }

        public override void CreateItems()
        {
            PanelScrollView view = PanelScrollView.CreateNew(Panel.ContentParent, LayoutDirection.Vertical);

            view.LayoutContainer.ChildLayout.StretchItemWidth = true;
            view.LayoutContainer.ApplyLayoutOptions();

            PanelSlider.CreateNew(view.LayoutContainer, "Setting 1");
            PanelSlider.CreateNew(view.LayoutContainer, "Option 2");
            PanelSlider.CreateNew(view.LayoutContainer, "Choice 3");
            PanelDropdown.CreateNew(
                view.LayoutContainer,
                "Dropdown",
                new[] { "Option A", "Choice B", "Selection 3" });
            PanelSlider.CreateNew(view.LayoutContainer, "Woah");
            PanelSlider.CreateNew(view.LayoutContainer, "Woah");
            PanelSlider.CreateNew(view.LayoutContainer, "Woah");
        }

        public override void BindItems()
        {

        }
    }
}
