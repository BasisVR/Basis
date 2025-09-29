using System.Collections.Generic;
using UnityEngine;

namespace Basis.VowganUI
{
    public static class HomeRowMenu
    {

        public static PanelGroup Group;
        public static Panel RootPanel;
        public static Vector2 PanelSize = new Vector2(800, 150);
        public static Vector3 PanelOffset = new Vector3(0, -350, -1);

        public static List<MenuActionProvider> ActionProviders = new();

        public static void AddActionProvider(MenuActionProvider provider)
        {
            ActionProviders.Add(provider);
            ActionProviders.Sort();
            if (Group) CreateMenu();
        }

        public static void RemoveActionProvider(MenuActionProvider provider)
        {
            ActionProviders.Remove(provider);
            ActionProviders.Sort();
            if (Group) CreateMenu();
        }

        public static void ToggleMenu()
        {
            if (Group) Group.ReleaseInstance();
            else CreateMenu();
        }

        public static void CreateMenu()
        {
            if (Group) Group.ReleaseInstance();
            Group = PanelGroup.CreateNew();
            CreateItems();
        }

        private static void CreateItems()
        {
            RootPanel = Group.CreateStaticPanelInGroup(new PanelData
                {
                    Title = "Home Row",
                    PanelSize = HomeRowMenu.PanelSize,
                },
                PanelOffset,
                Panel.Styles.Page);

            PanelLayoutContainer layout = PanelLayoutContainer.CreateNew(RootPanel.ContentParent, LayoutDirection.Horizontal);
            layout.CopyLayoutOptions(new LayoutContainerOptions
            {
                Alignment = TextAnchor.MiddleCenter,
                Constrained = false,
                StretchItemWidth = false,
                StretchItemHeight = false,
                SpreadItemWidth = true,
                SpreadItemHeight = true
            });

            foreach (MenuActionProvider action in ActionProviders)
            {
                PanelButton button = PanelButton.CreateNew(layout, action.Title);
                action.BindToButton(button);
            }
        }
    }
}
