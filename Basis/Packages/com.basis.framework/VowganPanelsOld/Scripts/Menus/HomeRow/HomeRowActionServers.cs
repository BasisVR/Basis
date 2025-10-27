using System.Collections.Generic;
using Basis.VowganUI;
using UnityEngine;

namespace Basis.VowganUIOld
{

    public class HomeRowActionServers : MenuActionProvider
    {
        [RuntimeInitializeOnLoadMethod]
        public static void AddToMenu()
        {
            HomeRowMenu.AddActionProvider(new HomeRowActionServers());
        }

        public override string Title => "Servers";
        public override Sprite Icon => null;
        public override int Order => 1;


        public override void RunAction() => ToggleActive();
        public override void OnActionDisabled() => ReleasePanelForAction();
        public override void OnActionEnabled() => CreateMenu();


        public void CreateMenu()
        {
            Panel panel = HomeRowMenu.Instance.Group.CreateRootPanelInGroup(new PanelData
                {
                    Title = this.Title,
                    PanelSize = new Vector2(300, 400)
                },
                Panel.Styles.Page);

            panel.OnInstanceReleased += DisableAction;

            PanelScrollView view = PanelScrollView.CreateNew(panel.ContentParent, LayoutDirection.Vertical);

            PanelButton.CreateNew(view.LayoutContainer);
            PanelButton.CreateNew(view.LayoutContainer);
            PanelButton.CreateNew(view.LayoutContainer);
            PanelButton.CreateNew(view.LayoutContainer);
            PanelButton.CreateNew(view.LayoutContainer);
            PanelButton.CreateNew(view.LayoutContainer);
            PanelButton.CreateNew(view.LayoutContainer);
            PanelButton.CreateNew(view.LayoutContainer);
            PanelButton.CreateNew(view.LayoutContainer);
            PanelButton.CreateNew(view.LayoutContainer);
            PanelButton.CreateNew(view.LayoutContainer);
            PanelButton.CreateNew(view.LayoutContainer);
            PanelButton.CreateNew(view.LayoutContainer);
            PanelButton.CreateNew(view.LayoutContainer);
            PanelButton.CreateNew(view.LayoutContainer);
            PanelButton.CreateNew(view.LayoutContainer);

        }

    }
}
