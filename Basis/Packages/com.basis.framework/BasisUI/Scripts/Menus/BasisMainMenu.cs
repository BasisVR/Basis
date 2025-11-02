using UnityEngine;

namespace Basis.BasisUI
{
    public class BasisMainMenu : BasisMenuBase<BasisMainMenu>
    {

        public static string MenuTitle => "Main";

        public static string ActiveMenuTitle
        {
            get
            {
                if (!Instance || !Instance.ActiveMenu) return string.Empty;
                return Instance.ActiveMenu.Data.Title;
            }
        }

        public BasisMenuPanel TabMenu;
        public PanelLayoutContainer TabContainer;

        public override Component ProviderButtonParent => TabContainer;

        public BasisMainMenu()
        {
            TabMenu = BasisMenuPanel.CreateNew(
                BasisMenuPanel.PanelData.Toolbar(MenuTitle),
                MenuObjectInstance.PanelRoot);

            TabContainer = PanelLayoutContainer.CreateNew(TabMenu.ContentParent, LayoutDirection.Horizontal);

            TabContainer.CopyLayoutOptions(new LayoutContainerOptions
            {
                Alignment = TextAnchor.MiddleCenter,
                Constrained = false,
                StretchItemWidth = true,
                StretchItemHeight = true,
                SpreadItemWidth = true,
                SpreadItemHeight = true
            });

            // TabContainer.LayoutGroup.padding = new RectOffset(100, 100, 0, 0);

            BindProvidersToButtons();
        }

        public static void Open()
        {
            if (Instance) Instance.Release();
            Instance = new BasisMainMenu();
            BasisCursorManagement.UnlockCursor(nameof(BasisMainMenu));
        }

        public static void Toggle()
        {
            if (Instance) Close();
            else Open();
        }

        public static void Close()
        {
            if (!Instance) return;
            Instance.Release();
            Instance = null;
            BasisCursorManagement.LockCursor(nameof(BasisMainMenu));
        }

        public static BasisMenuPanel CreateActiveMenu(BasisMenuPanel.PanelData data, string style)
        {
            if (Instance.Dialogue)
            {
                Instance.Dialogue.ReleaseInstance();
            }
            if (Instance.ActiveMenu)
            {
                if (Instance.ActiveMenu.Data.Title == data.Title)
                    return Instance.ActiveMenu;
                else
                    Instance.ActiveMenu.ReleaseInstance();
            }

            Instance.ActiveMenu = BasisMenuPanel.CreateNew(
                data,
                Instance.MenuObjectInstance.PanelRoot,
                style);
            return Instance.ActiveMenu;
        }

        public static BasisMenuPanel CreateActiveTabMenu(BasisMenuPanel.PanelData data, string style, out PanelTabGroup tabGroup)
        {
            tabGroup = null;

            if (Instance.Dialogue)
            {
                Instance.Dialogue.ReleaseInstance();
            }
            if (Instance.ActiveMenu)
            {
                if (Instance.ActiveMenu.Data.Title == data.Title)
                {
                    tabGroup = null;
                    return Instance.ActiveMenu;
                }
                else
                    Instance.ActiveMenu.ReleaseInstance();
            }

            Instance.ActiveMenu = BasisMenuPanel.CreateNewTabPage(
                data,
                Instance.MenuObjectInstance.PanelRoot,
                out tabGroup);

            return Instance.ActiveMenu;
        }

    }
}
