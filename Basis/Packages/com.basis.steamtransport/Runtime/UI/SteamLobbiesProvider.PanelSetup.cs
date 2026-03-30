using Basis.BasisUI;
using Basis.Scripts.Common;
using UnityEngine;

namespace Basis.Scripts.Networking.Steam
{
    public partial class SteamLobbiesProvider
    {
        private void BuildPanelContents(BasisMenuPanel panel)
        {
            RectTransform container = panel.Descriptor.ContentParent;
            PanelElementDescriptor layout = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.ScrollViewVertical, container);
            container = layout.ContentParent;

            infoDescriptor = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            infoDescriptor.SetTitle("Steam");
            infoDescriptor.SetDescription("Create or browse Steam lobbies for Basis sessions.");

            usernameField = PanelTextField.CreateNewEntry(container);
            usernameField.Descriptor.SetTitle("Username");
            usernameField.SetValueWithoutNotify(BasisDataStore.LoadString(ServersProvider.LoadFileName, string.Empty));

            createGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            createGroup.SetTitle("Create Lobby");
            createGroup.SetDescription("Host a Basis session and attach a world BEE to the lobby.");

            lobbyNameField = PanelTextField.CreateNewEntry(createGroup.ContentParent);
            lobbyNameField.Descriptor.SetTitle("Lobby Name");
            lobbyNameField.SetValueWithoutNotify("Basis Steam Lobby");

            worldUrlField = PanelTextField.CreateNewEntry(createGroup.ContentParent);
            worldUrlField.Descriptor.SetTitle("World BEE URL");

            worldPasswordField = PanelPasswordField.CreateNewEntry(createGroup.ContentParent);
            worldPasswordField.Descriptor.SetTitle("World Password");

            BasisSteamSettings settings = BasisSteamBootstrap.ActiveSettings;

            useRelayToggle = PanelToggle.CreateNewEntry(createGroup.ContentParent);
            useRelayToggle.Descriptor.SetTitle("Use Relay");
            useRelayToggle.Descriptor.SetDescription("Prefer Steam relay for lobby sessions.");
            useRelayToggle.SetValueWithoutNotify(settings == null || settings.UseRelayByDefault);

            friendsOnlyToggle = PanelToggle.CreateNewEntry(createGroup.ContentParent);
            friendsOnlyToggle.Descriptor.SetTitle("Friends Only");
            friendsOnlyToggle.Descriptor.SetDescription("Only friends can discover and join.");
            friendsOnlyToggle.SetValueWithoutNotify(settings != null && settings.CreateFriendsOnlyByDefault);

            privateLobbyToggle = PanelToggle.CreateNewEntry(createGroup.ContentParent);
            privateLobbyToggle.Descriptor.SetTitle("Private Lobby");
            privateLobbyToggle.Descriptor.SetDescription("Create the lobby as private.");
            privateLobbyToggle.SetValueWithoutNotify(false);

            createLobbyButton = PanelButton.CreateNew(createGroup.ContentParent);
            createLobbyButton.Descriptor.SetTitle("Create Steam Lobby");
            createLobbyButton.Descriptor.SetHeight(80);
            createLobbyButton.OnClicked += () => _ = OnCreateLobbyButton();

            browserGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            browserGroup.SetTitle("Available Lobbies");
            browserGroup.SetDescription("Refresh Steam lobby metadata and inspect the selected world.");

            refreshLobbiesButton = PanelButton.CreateNew(browserGroup.ContentParent);
            refreshLobbiesButton.Descriptor.SetTitle("Refresh Lobbies");
            refreshLobbiesButton.OnClicked += () => _ = RefreshLobbiesAsync();

            lobbySelectionDropdown = PanelDropdown.CreateNew(PanelDropdown.DropdownStyles.EntryNoLabel, browserGroup.ContentParent);
            lobbySelectionDropdown.Descriptor.SetSize(new Vector2(60, 80));
            lobbySelectionDropdown.AssignEntries(new System.Collections.Generic.List<string> { "No lobbies loaded" });
            lobbySelectionDropdown.SetValueWithoutNotify("No lobbies loaded");
            lobbySelectionDropdown.OnValueChanged += _ => RefreshSelectedLobbyDescription();

            selectedLobbyDescriptor = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, browserGroup.ContentParent);
            selectedLobbyDescriptor.SetTitle("Lobby Preview");
            selectedLobbyDescriptor.SetDescription("Refresh Steam lobbies to inspect a world before joining.");

            joinLobbyButton = PanelButton.CreateNew(browserGroup.ContentParent);
            joinLobbyButton.Descriptor.SetTitle("Join Lobby");
            joinLobbyButton.OnClicked += () => _ = OnJoinLobbyButton();

            sessionGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            sessionGroup.SetTitle("Current Session");
            sessionGroup.SetDescription("Actions for the currently active Steam lobby.");

            inviteFriendsButton = PanelButton.CreateNew(sessionGroup.ContentParent);
            inviteFriendsButton.Descriptor.SetTitle("Invite Friends");
            inviteFriendsButton.Descriptor.SetDescription("Open the Steam overlay invite dialog for this lobby.");
            inviteFriendsButton.OnClicked += OnInviteFriendsButton;

            leaveLobbyButton = PanelButton.CreateNew(sessionGroup.ContentParent);
            leaveLobbyButton.Descriptor.SetTitle("Leave Current Lobby");
            leaveLobbyButton.OnClicked += OnLeaveLobbyButton;

            currentLobbyDescriptor = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, sessionGroup.ContentParent);
            currentLobbyDescriptor.SetTitle("Lobby Details");
            currentLobbyDescriptor.SetDescription("No Steam lobby selected.");
        }
    }
}
