using System;
using System.Collections.Generic;
using UnityEngine;

namespace Basis.VowganUI
{
    public class BasisMainMenu : BasisMenuBase<BasisMainMenu>
    {

        public static string ActiveMenuName
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
                BasisMenuPanel.PanelData.Hotbar("Main Menu"),
                MenuObjectInstance.PanelRoot);

            TabContainer = PanelLayoutContainer.CreateNew(TabMenu.ContentParent, LayoutDirection.Horizontal);

            TabContainer.CopyLayoutOptions(new LayoutContainerOptions
            {
                Alignment = TextAnchor.MiddleCenter,
                Constrained = false,
                StretchItemWidth = false,
                StretchItemHeight = false,
                SpreadItemWidth = true,
                SpreadItemHeight = true
            });

            TabContainer.LayoutGroup.padding = new RectOffset(100, 100, 0, 0);

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

    }
}
