using System;
using System.Collections.Generic;
using Basis.BasisUI;
using UnityEngine;

namespace Basis.VowganUIOld
{
    public abstract class MenuPanelBase<TMenu> where TMenu : MenuPanelBase<TMenu>, new()
    {
        public PanelGroup Group;
        public Panel Panel;

        public static TMenu Instance;

        public abstract PanelData Data { get; }

        /// <summary>
        /// Create a menu, creating a new panel group for it.
        /// </summary>
        public static void CreateMenu() => CreateMenu(PanelGroup.CreateNew());


        public static List<MenuActionProvider> ActionProviders = new();

        public static void AddActionProvider(MenuActionProvider provider)
        {
            ActionProviders.Add(provider);
            ActionProviders.Sort();
            if (Instance) CreateMenu();
        }

        public static void RemoveActionProvider(MenuActionProvider provider)
        {
            ActionProviders.Remove(provider);
            ActionProviders.Sort();
            if (Instance) CreateMenu();
        }

        public static implicit operator bool(MenuPanelBase<TMenu> obj)
        {
            return obj != null;
        }

        public static void ToggleMenu()
        {
            if (Instance) Instance.DestroyMenu();
            else CreateMenu();
        }

        /// <summary>
        /// Create a menu, passing in panel group for it.
        /// </summary>
        public static void CreateMenu(PanelGroup group)
        {
            Debug.Log($"Instance: {Instance}");
            Instance?.DestroyMenu();
            Instance = new TMenu();
            Instance.Group = group;
            Instance.RebuildMenu();
        }

        public abstract Panel CreatePanel();
        public abstract void CreateItems();
        public abstract void BindItems();

        public void RebuildMenu()
        {
            Instance.Panel = Instance.CreatePanel();
            Instance.CreateItems();
            Instance.BindItems();
        }


        public void DestroyMenu()
        {
            Panel.ReleaseInstance();
            if (Instance == this) Instance = null;
            // Further destruction is handled via GC.
        }

        public void BindProvidersToButtonsInContainer(Component container)
        {
            foreach (MenuActionProvider action in ActionProviders)
            {
                PanelButton button = PanelButton.CreateNew(container);
                button.SetTitle(action.Title);
                action.BindToButton(button);
            }
        }
    }
}
