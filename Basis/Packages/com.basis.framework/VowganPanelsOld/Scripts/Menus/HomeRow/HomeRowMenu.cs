using System.Collections.Generic;
using Basis.VowganUI;
using UnityEngine;

namespace Basis.VowganUIOld
{

    public class HomeRowMenu : MenuPanelBase<HomeRowMenu>
    {

        // public static HomeRowMenu Instance;

        public override PanelData Data => new PanelData
        {
            Title = "Home Row",
            PanelSize = new Vector2(800, 150),
        };

        public static Vector3 PanelOffset = new Vector3(0, -350, -1);

        public override Panel CreatePanel()
        {
            Panel = Group.CreateStaticPanelInGroup(
                Data,
                PanelOffset,
                Panel.Styles.Page);

            Panel.OnInstanceReleased += () => Group.ReleaseInstance();

            return Panel;
        }

        public override void CreateItems()
        {
            PanelLayoutContainer layout = PanelLayoutContainer.CreateNew(Panel.ContentParent, LayoutDirection.Horizontal);
            layout.CopyLayoutOptions(new LayoutContainerOptions
            {
                Alignment = TextAnchor.MiddleCenter,
                Constrained = false,
                StretchItemWidth = false,
                StretchItemHeight = false,
                SpreadItemWidth = true,
                SpreadItemHeight = true
            });

            BindProvidersToButtonsInContainer(layout);
        }

        public override void BindItems() { }

    }
}
