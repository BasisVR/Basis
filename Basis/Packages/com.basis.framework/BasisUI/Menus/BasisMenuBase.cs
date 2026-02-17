using System;
using System.Collections.Generic;
using UnityEngine;

namespace Basis.BasisUI
{
    /// <summary>
    /// This is the backing data that supports and manages the MenuInstance in the scene.
    /// </summary>
    [Serializable]
    public abstract class BasisMenuBase<TMenu> where TMenu: BasisMenuBase<TMenu>
    {

        public static implicit operator bool(BasisMenuBase<TMenu> menu) => menu != null;

        #region Providers

        public static List<BasisMenuActionProvider<TMenu>> Providers = new();

        /// <summary>
        /// Parent component used containing Action Provider buttons.
        /// </summary>
        public abstract Component ProviderButtonParent { get; }

        /// <summary>
        /// All current buttons used by the ActionProviders.
        /// </summary>
        public List<PanelButton> ProviderButtons = new();

        /// <summary>
        /// Add a provider that will supply an action through a button press on this menu.
        /// </summary>
        public static void AddProvider(BasisMenuActionProvider<TMenu> provider)
        {
            Providers.Add(provider);
            Providers.Sort();
            if (Instance) Instance.BindProvidersToButtons();
        }

        /// <summary>
        /// Remove a provider, removing its button on this menu as well.
        /// </summary>
        public static void RemoveProvider(BasisMenuActionProvider<TMenu> provider)
        {
            Providers.Remove(provider);
            Providers.Sort();
            if (Instance) Instance.BindProvidersToButtons();
        }


        public void BindProvidersToButtons()
        {
            foreach (PanelButton button in ProviderButtons)
            {
                button.ReleaseInstance();
            }

            ProviderButtons.Clear();

            foreach (BasisMenuActionProvider<TMenu> provider in Providers)
            {
                if (provider.Hidden == false)
                {
                    PanelButton button = PanelButton.CreateNew(
                        PanelButton.ButtonStyles.Hotbar,
                        ProviderButtonParent);

                    button.Descriptor.SetTitle(provider.Title);
                    button.SetIcon(provider.IconAddress);
                    provider.BindToButton(this, button);
                    ProviderButtons.Add(button);
                    provider.OnButtonCreated(button);
                }
            }
        }

        #endregion

        public static BasisMenuBase<TMenu> Instance;
        public BasisMenuInstance MenuObjectInstance = BasisMenuInstance.CreateNew();

        public BasisMenuPanel ActiveMenu;
        public BasisMenuDialoguePanel Dialogue;

        public virtual void Release()
        {
            if (MenuObjectInstance) MenuObjectInstance.ReleaseInstance();
        }

        public void OpenDialogue(
            string title,
            string description,
            string accept,
            string deny,
            Action<bool> callback)
        {
            if (Dialogue)
            {
                BasisDebug.LogWarning("An existing Dialogue window is already active.");
                return;
            }

            Dialogue = BasisMenuDialoguePanel.CreateNew(title,
                description,
                accept,
                deny,
                callback);
        }

        public void OpenDialogue(
            string title,
            string description,
            string accept,
            Action<bool> callback)
        {
            if (Dialogue)
            {
                BasisDebug.LogWarning("An existing Dialogue window is already active.");
                return;
            }

            Dialogue = BasisMenuDialoguePanel.CreateNew(title,
                description,
                accept,
                callback);
        }
    }
}
