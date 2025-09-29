using UnityEngine;

namespace Basis.VowganUI
{
    public class HomeRowActionSettings : MenuActionProvider
    {

        [RuntimeInitializeOnLoadMethod]
        public static void AddToMenu()
        {
            HomeRowMenu.AddActionProvider(new HomeRowActionSettings());
        }

        public override string Title => "Settings";
        public override Sprite Icon => null;
        public override int Order => 0;


        public override void RunAction() => ToggleActive();
        public override void OnActionDisabled() => ReleasePanelForAction();
        public override void OnActionEnabled() => CreateMenu();


        public void CreateMenu()
        {
            Panel panel = HomeRowMenu.Group.CreateRootPanelInGroup(new PanelData
                {
                    Title = this.Title,
                    PanelSize = new Vector2(800, 500)
                },
                Panel.Styles.Page);

            panel.OnReleased += DisableAction;

            PanelScrollView view = PanelScrollView.CreateNew(panel.ContentParent, LayoutDirection.Vertical);

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

    }
}
