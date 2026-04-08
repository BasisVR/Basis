using Basis.BTween;
using Basis.Scripts.Drivers;
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
            AnimateMenuEntrance();
        }

        private void AnimateMenuEntrance()
        {
            // Fade in the hotbar panel
           // UIAnimations.FadeIn(HotbarMenu, 0.2f, 0f, Easing.OutCubic);

            // Stagger the hotbar buttons with fade + slide up
            if (ProviderButtons.Count > 0)
            {
                RectTransform[] buttonTransforms = new RectTransform[ProviderButtons.Count];
                for (int i = 0; i < ProviderButtons.Count; i++)
                {
                    buttonTransforms[i] = ProviderButtons[i].rectTransform;
                }
                UIAnimations.StaggerEntrance(buttonTransforms, 0.04f, 0.2f, -15f);
            }
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
            SetMicrophoneIconHudVisible(false);
        }
        public static void OpenWithProvider(string ProviderTitle)
        {
            Open();
            int count = BasisMainMenu.Providers.Count;
            for (int Index = 0; Index < count; Index++)
            {
                BasisMenuActionProvider<BasisMainMenu> provider = BasisMainMenu.Providers[Index];
                if (provider.Title == ProviderTitle)
                {
                    provider.RunAction();
                    return;
                }
            }
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
            SetMicrophoneIconHudVisible(true);
        }

        private static void SetMicrophoneIconHudVisible(bool visible)
        {
#if !BASIS_DISABLE_MICROPHONE
            if (BasisLocalCameraDriver.Instance != null)
            {
                BasisLocalCameraDriver.Instance.microphoneIconDriver.HardEnableVisuals(visible);
            }
#endif
        }

        public static BasisMenuPanel CreateActiveMenu(BasisMenuPanel.PanelData data, string style, BasisMenuActionProvider<BasisMainMenu> provider = null)
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
                    // Notify the previous provider that its panel is being released
                    if (Instance.ActiveProvider != null)
                    {
                        Instance.ActiveProvider.OnReleaseEvent();
                    }

                    Instance.ActiveMenu.ReleaseInstance();
                }
            }

            Instance.ActiveMenu = BasisMenuPanel.CreateNew(data, Instance.MenuObjectInstance.PanelRoot, style);
            Instance.ActiveProvider = provider;

            // Animate content panel entrance
            UIAnimations.PanelIn(Instance.ActiveMenu);

            return Instance.ActiveMenu;
        }
    }
}
