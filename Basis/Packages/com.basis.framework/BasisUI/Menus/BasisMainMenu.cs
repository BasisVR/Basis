using UnityEngine;

namespace Basis.BasisUI
{
    public class BasisMainMenu : BasisMenuBase<BasisMainMenu>
    {

        public static string MenuTitle => "Main Menu";

        public static string ActiveMenuTitle
        {
            get
            {
                if (!Instance || !Instance.ActiveMenu)
                {
                    return string.Empty;
                }

                return Instance.ActiveMenu.Data.Title;
            }
        }

        public BasisMenuPanel HotbarMenu;
        public PanelElementDescriptor HorizontalLayout;

        public override Component ProviderButtonParent => HorizontalLayout.ContentParent;

        public BasisMainMenu()
        {
            HotbarMenu = BasisMenuPanel.CreateNew(BasisMenuPanel.PanelData.Toolbar(MenuTitle), MenuObjectInstance.PanelRoot);

            HorizontalLayout = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.ScrollViewHorizontal, HotbarMenu.Descriptor.ContentParent);

            BindProvidersToButtons();
        }

        public static void Open()
        {
            BasisUIManagement.CloseAllMenus();

            if (Instance)
            {
                Instance.Release();
            }

            Instance = new BasisMainMenu();
            BasisCursorManagement.UnlockCursor(nameof(BasisMainMenu));
        }

        public static void Toggle()
        {
            if (Instance)
            {
                Close();
            }
            else
            {
                Open();
            }
        }

        public static void Close()
        {
            if (!Instance)
            {
                return;
            }

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
                {
                    return Instance.ActiveMenu;
                }
                else
                {
                    Instance.ActiveMenu.ReleaseInstance();
                }
            }

            Instance.ActiveMenu = BasisMenuPanel.CreateNew( data, Instance.MenuObjectInstance.PanelRoot, style);
            return Instance.ActiveMenu;
        }
    }
}
