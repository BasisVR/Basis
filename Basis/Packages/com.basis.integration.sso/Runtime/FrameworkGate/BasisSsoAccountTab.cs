using Basis.BasisUI;
using Basis.Integration.Sso;
using UnityEngine;

namespace Basis.Integration.Sso.FrameworkGate
{
    /// <summary>
    /// Adds an "Account" tab to Settings showing the signed-in identity with Sign out / Switch
    /// account actions. Registered through <see cref="SettingsProvider.ExternalTabs"/> so no edit to
    /// the framework's SettingsProvider is required. Sign-out defers re-login to <see cref="BasisSsoGate"/>
    /// (which re-arms the connection block and re-presents the prompt), avoiding a second parallel flow.
    /// </summary>
    public static class BasisSsoAccountTab
    {
        private static bool _registered;

        internal static void Register()
        {
            if (_registered) return;
            _registered = true;
            // TabName is treated as a localization key that falls back to itself, so a plain
            // string shows as-is when no localization entry exists.
            SettingsProvider.ExternalTabs.Add(("Account", BuildTab));
        }

        private static PanelTabPage BuildTab(PanelTabGroup tabGroup)
        {
            PanelTabPage tab = PanelTabPage.CreateVertical(tabGroup.Descriptor.ContentParent);
            PanelElementDescriptor descriptor = tab.Descriptor;
            descriptor.SetIcon(AddressableAssets.Sprites.Settings);
            descriptor.SetTitle("Account");

            RectTransform container = descriptor.ContentParent;

            PanelElementDescriptor group = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, container);

            if (BasisSsoAuthController.IsSignedIn)
            {
                string sub = BasisSsoAuthController.Current?.Sub ?? string.Empty;
                group.SetTitle("Signed in");
                group.SetDescription($"{BasisSsoAuthController.ActiveDisplayName}\n<size=85%>{sub}</size>");

                PanelButton switchButton = PanelButton.CreateNew(container);
                switchButton.Descriptor.SetTitle("Switch account");
                switchButton.OnClicked += () => Confirm(
                    "Switch account",
                    "Sign out and sign in with a different account?",
                    () =>
                    {
                        BasisSsoAuthController.PendingPrompt = "login";
                        BasisSsoAuthController.SignOut();
                        CloseMenu();
                    });

                PanelButton signOutButton = PanelButton.CreateNew(container);
                signOutButton.Descriptor.SetTitle("Sign out");
                signOutButton.OnClicked += () => Confirm(
                    "Sign out",
                    "You will need to sign in again to connect to any server.",
                    () =>
                    {
                        BasisSsoAuthController.SignOut();
                        CloseMenu();
                    });
            }
            else
            {
                group.SetTitle("Not signed in");
                group.SetDescription(BasisSsoAuthController.IsConfigured
                    ? "Sign-in is required to connect."
                    : "SSO is not configured for this client.");
            }

            descriptor.ForceRebuild();
            return tab;
        }

        private static void Confirm(string title, string body, System.Action onConfirmed)
        {
            if (!BasisMainMenu.Instance)
            {
                onConfirmed?.Invoke();
                return;
            }
            BasisMainMenu.Instance.OpenDialogue(title, body, "Confirm", "Cancel", ok =>
            {
                if (ok) onConfirmed?.Invoke();
            });
        }

        private static void CloseMenu()
        {
            // Drop the settings panel so the gate's sign-in dialog owns the screen.
            BasisMainMenu.Close();
        }
    }
}
