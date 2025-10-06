using System;
using System.Collections.Generic;
using UnityEngine;

namespace Basis.VowganUI
{
    public class BasisMainMenu : BasisMenuBase
    {

        public static BasisMainMenu Instance;

        public BasisMenuPanel TabMenu;

        public PanelLayoutContainer TabContainer;


        public BasisMainMenu()
        {
            TabMenu = BasisMenuPanel.CreateNew(
                BasisMenuPanel.PanelData.Hotbar("Main Menu"),
                MenuInstance.PanelRoot);

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
                Instance.MenuInstance.PanelRoot,
                style);
            return Instance.ActiveMenu;
        }

        #region Action Providers

        public static List<BasisMenuActionProvider> ActionProviders = new();
        public List<PanelButton> ProviderButtons = new();

        public static void AddActionProvider(BasisMenuActionProvider provider)
        {
            ActionProviders.Add(provider);
            ActionProviders.Sort();
            if (Instance) Instance.BindProvidersToButtons();
        }

        public static void RemoveActionProvider(BasisMenuActionProvider provider)
        {
            ActionProviders.Remove(provider);
            ActionProviders.Sort();
            if (Instance) Instance.BindProvidersToButtons();
        }

        public void BindProvidersToButtons()
        {
            BindProvidersToButtonsInContainer(TabContainer.ContentParent);
        }

        public void BindProvidersToButtonsInContainer(Component container)
        {
            foreach (PanelButton button in ProviderButtons)
                button.ReleaseInstance();

            ProviderButtons.Clear();

            foreach (BasisMenuActionProvider action in ActionProviders)
            {
                PanelButton button = PanelButton.CreateNew(container, action.Title);
                action.BindToButton(this, button);
                ProviderButtons.Add(button);
            }
        }

        #endregion

    }
}
