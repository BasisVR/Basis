using Basis.BasisUI;
using Basis.Network.Core;
using System.Collections.Generic;
using UnityEngine;

namespace Basis.Scripts.Networking.Steam
{
    public partial class SteamLobbiesProvider : BasisMenuActionProvider<BasisMainMenu>
    {
        [RuntimeInitializeOnLoadMethod]
        public static void AddToMenu()
        {
            BasisMenuBase<BasisMainMenu>.AddProvider(new SteamLobbiesProvider());
        }

        public override string Title => "Steam Lobbies";
        public override string IconAddress => AddressableAssets.Sprites.Servers;
        public override int Order => 2;
        public override bool Hidden => false;

        private static readonly List<BasisSteamLobbyState> cachedLobbies = new List<BasisSteamLobbyState>();

        private PanelTextField usernameField;
        private PanelTextField lobbyNameField;
        private PanelTextField worldUrlField;
        private PanelPasswordField worldPasswordField;
        private PanelToggle friendsOnlyToggle;
        private PanelToggle privateLobbyToggle;
        private PanelToggle useRelayToggle;
        private PanelElementDescriptor createGroup;
        private PanelElementDescriptor browserGroup;
        private PanelElementDescriptor sessionGroup;
        private PanelButton createLobbyButton;
        private PanelButton refreshLobbiesButton;
        private PanelButton joinLobbyButton;
        private PanelButton leaveLobbyButton;
        private PanelButton inviteFriendsButton;
        private PanelDropdown lobbySelectionDropdown;
        private PanelElementDescriptor infoDescriptor;
        private PanelElementDescriptor selectedLobbyDescriptor;
        private PanelElementDescriptor currentLobbyDescriptor;
        private bool isSubscribedToLobbyEvents;

        public override void RunAction()
        {
            if (BasisMainMenu.ActiveMenuTitle == Title)
            {
                OnReleaseEvent();
                BasisMainMenu.Instance.ActiveMenu.ReleaseInstance();
                return;
            }

            BasisMenuPanel panel = BasisMainMenu.CreateActiveMenu(
                new BasisMenuPanel.PanelData
                {
                    Title = Title,
                    PanelSize = new Vector2(650, 950),
                    PanelPosition = default
                },
                BasisMenuPanel.PanelStyles.Page,
                this);
            BoundButton?.BindActiveStateToAddressablesInstance(panel);

            BuildPanelContents(panel);
            SubscribeToLobbyEvents();
            SyncCurrentState();

            if (BasisSteamLobbyService.State == null || BasisSteamLobbyService.State.LobbyId == 0)
            {
                _ = RefreshLobbiesAsync();
            }
        }

        public override void OnReleaseEvent()
        {
            UnsubscribeFromLobbyEvents();
            ClearUiReferences();
        }

        private void SubscribeToLobbyEvents()
        {
            if (isSubscribedToLobbyEvents)
            {
                return;
            }

            BasisSteamLobbyService.OnLobbyStateChanged += HandleLobbyStateChanged;
            BasisSteamLobbyService.OnLobbyError += HandleLobbyError;
            isSubscribedToLobbyEvents = true;
        }

        private void UnsubscribeFromLobbyEvents()
        {
            if (!isSubscribedToLobbyEvents)
            {
                return;
            }

            BasisSteamLobbyService.OnLobbyStateChanged -= HandleLobbyStateChanged;
            BasisSteamLobbyService.OnLobbyError -= HandleLobbyError;
            isSubscribedToLobbyEvents = false;
        }

        private void ClearUiReferences()
        {
            usernameField = null;
            lobbyNameField = null;
            worldUrlField = null;
            worldPasswordField = null;
            friendsOnlyToggle = null;
            privateLobbyToggle = null;
            useRelayToggle = null;
            createGroup = null;
            browserGroup = null;
            sessionGroup = null;
            createLobbyButton = null;
            refreshLobbiesButton = null;
            joinLobbyButton = null;
            leaveLobbyButton = null;
            inviteFriendsButton = null;
            lobbySelectionDropdown = null;
            infoDescriptor = null;
            selectedLobbyDescriptor = null;
            currentLobbyDescriptor = null;
        }
    }
}
