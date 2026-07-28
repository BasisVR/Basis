using System;
using System.Collections.Generic;
using UnityEngine;

namespace Basis.BasisUI
{
    /// <summary>
    /// This is the backing data that supports and manages the MenuInstance in the scene.
    /// </summary>
    [Serializable]
    public abstract class BasisMenuBase<TMenu> where TMenu : BasisMenuBase<TMenu>
    {

        public static implicit operator bool(BasisMenuBase<TMenu> menu) => menu != null;

        #region Providers

        public static List<BasisMenuActionProvider<TMenu>> Providers = new();

        private static readonly HashSet<Type> SuppressedProviderTypes = new();

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
            if (provider == null || SuppressedProviderTypes.Contains(provider.GetType())) return;
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

        /// <summary>
        /// Prevent a provider type from appearing on this menu, regardless of when its
        /// registrar runs. Order-independent: removes the provider if already present and
        /// blocks any later registration, rebinding the menu if it is open.
        /// </summary>
        public static void SuppressProvider<T>() where T : BasisMenuActionProvider<TMenu>
            => SuppressProvider(typeof(T));

        public static void SuppressProvider(Type providerType)
        {
            if (providerType == null)
            {
                BasisDebug.LogError("SuppressProvider was called with a null providerType; nothing suppressed.");
                return;
            }

            SuppressedProviderTypes.Add(providerType);

            if (Providers.RemoveAll(p => providerType.IsInstanceOfType(p)) > 0)
            {
                Providers.Sort();
                if (Instance) Instance.BindProvidersToButtons();
                BasisDebug.Log($"SuppressProvider removed and blocked provider '{providerType.Name}'.");
            }
            else
            {
                BasisDebug.LogWarning($"SuppressProvider could not find a registered provider of type '{providerType.Name}' to remove; it is now blocked from registering later.");
            }
        }

        public static void AllowProvider(Type providerType)
        {
            if (providerType != null) SuppressedProviderTypes.Remove(providerType);
        }


        public void BindProvidersToButtons()
        {
            foreach (PanelButton button in ProviderButtons)
            {
                if (button) button.ReleaseInstance();
            }

            ProviderButtons.Clear();

            Component parent = ProviderButtonParent;
            if (parent == null) return;

            foreach (BasisMenuActionProvider<TMenu> provider in Providers)
            {
                if (provider.Hidden == false)
                {
                    PanelButton button = PanelButton.CreateNew(
                        PanelButton.ButtonStyles.Hotbar,
                        parent);

                    if (button == null) continue;

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

        /// <summary>
        /// The provider that currently owns the active menu panel.
        /// </summary>
        [System.NonSerialized] public BasisMenuActionProvider<TMenu> ActiveProvider;

        public virtual void Release()
        {
            // if the active provider exists, notify it that its panel is being released
            if (ActiveProvider != null)
            {
                ActiveProvider.OnReleaseEvent();
            }
            if (MenuObjectInstance) MenuObjectInstance.ReleaseInstance();
        }

        public void OpenDialogue(
            string title,
            string description,
            string accept,
            string deny,
            Action<bool> callback,
            bool divertible = false)
        {
            bool route = divertible && BasisNotificationCenter.RouteToNotifications;
            if (!route && Dialogue)
            {
                BasisDebug.LogWarning("An existing Dialogue window is already active.");
                return;
            }

            BasisMenuDialoguePanel created = BasisMenuDialoguePanel.CreateNew(title,
                description,
                accept,
                deny,
                callback,
                divertible);
            if (!route) Dialogue = created;
        }

        public void OpenDialogue(
            string title,
            string description,
            string accept,
            Action<bool> callback,
            bool divertible = false)
        {
            bool route = divertible && BasisNotificationCenter.RouteToNotifications;
            if (!route && Dialogue)
            {
                BasisDebug.LogWarning("An existing Dialogue window is already active.");
                return;
            }

            BasisMenuDialoguePanel created = BasisMenuDialoguePanel.CreateNew(title,
                description,
                accept,
                callback,
                divertible);
            if (!route) Dialogue = created;
        }
    }
}
