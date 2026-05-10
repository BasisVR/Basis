using Basis.BasisUI;
using Basis.Network.Core;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Device_Management.Devices.Desktop;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Basis.Scripts.Networking.Steam
{
    public partial class SteamLobbiesProvider
    {
        private void SyncCurrentState()
        {
            if (BasisNetworkManagement.Instance != null)
            {
                useRelayToggle.SetValueWithoutNotify(BasisNetworkManagement.Instance.UseSteamRelay);
            }

            HandleLobbyStateChanged(BasisSteamLobbyService.State);
            RefreshSelectedLobbyDescription();
            ApplyUiState();
        }

        private async Task OnCreateLobbyButton()
        {
            SetBusy(createLobbyButton, true);

            try
            {
                if (!TryPrepareLocalPlayer(out string userName))
                {
                    return;
                }

                infoDescriptor.SetTitle("Steam");
                infoDescriptor.SetDescription("Validating world BEE...");

                BasisSteamBeeValidationResult validation = await BasisSteamBeeValidation.ValidateWorldAsync(worldUrlField.Value, worldPasswordField.Password);
                if (!validation.IsValid)
                {
                    infoDescriptor.SetTitle("World Error");
                    infoDescriptor.SetDescription(validation.ErrorMessage);
                    return;
                }

                BasisSteamLobbyState lobbyState = await BasisSteamLobbyService.CreateLobbyAsync(
                    lobbyNameField.Value,
                    validation,
                    friendsOnlyToggle.Value,
                    privateLobbyToggle.Value,
                    useRelayToggle.Value);

                if (lobbyState == null)
                {
                    infoDescriptor.SetTitle("Steam");
                    infoDescriptor.SetDescription("Steam lobby creation failed.");
                    return;
                }

                if (BasisNetworkManagement.Instance == null)
                {
                    infoDescriptor.SetTitle("Error");
                    infoDescriptor.SetDescription("BasisNetworkManagement is not available.");
                    BasisDebug.LogError("Steam CreateLobby: BasisNetworkManagement.Instance is null");
                    return;
                }

                await BasisNetworkConnection.ResetConnectionStateAsync(BasisNetworkManagement.Instance);

                BasisNetworkManagement.Instance.Transport = NetworkTransportType.Steam;
                BasisNetworkManagement.Instance.IsHostMode = true;
                BasisNetworkManagement.Instance.UpdateSteamLobbyState(lobbyState.LobbyId, lobbyState.HostSteamId, lobbyState.UseRelay, lobbyState.VirtualPort);
                BasisNetworkManagement.Instance.SetPendingSteamWorld(validation.WorldUrl, validation.WorldPassword, validation.WorldName);

                infoDescriptor.SetTitle("Steam");
                infoDescriptor.SetDescription("Starting local host session for the Steam lobby...");
                BasisMainMenu.Close();
                BasisCursorManagement.OnReset();
                BasisNetworkManagement.Instance.Connect();
                if (BasisDesktopEye.Instance != null)
                {
                    BasisDesktopEye.Instance.LockEye();
                }
            }
            catch (Exception ex)
            {
                infoDescriptor.SetTitle("Error");
                infoDescriptor.SetDescription("Steam lobby creation failed.");
                BasisDebug.LogError(ex.ToString());
            }
            finally
            {
                SetBusy(createLobbyButton, false);
                ApplyUiState();
            }
        }

        private async Task RefreshLobbiesAsync()
        {
            SetBusy(refreshLobbiesButton, true);

            try
            {
                infoDescriptor.SetTitle("Steam");
                infoDescriptor.SetDescription("Refreshing Steam lobbies...");

                IReadOnlyList<BasisSteamLobbyState> lobbies = await BasisSteamLobbyService.QueryLobbiesAsync();
                cachedLobbies.Clear();
                cachedLobbies.AddRange(lobbies);

                List<string> entries = new List<string>();
                for (int index = 0; index < cachedLobbies.Count; index++)
                {
                    BasisSteamLobbyState lobby = cachedLobbies[index];
                    string worldName = string.IsNullOrWhiteSpace(lobby.WorldName) ? "Unknown World" : lobby.WorldName;
                    string lobbyName = string.IsNullOrWhiteSpace(lobby.LobbyName) ? $"Lobby {index + 1}" : lobby.LobbyName;
                    entries.Add($"{lobbyName} | {worldName}");
                }

                if (entries.Count == 0)
                {
                    entries.Add("No lobbies found");
                }

                if (browserGroup)
                {
                    browserGroup.SetTitle(entries.Count == 1 && cachedLobbies.Count == 0
                        ? "Available Lobbies"
                        : $"Available Lobbies ({cachedLobbies.Count})");
                }

                lobbySelectionDropdown.AssignEntries(entries);
                lobbySelectionDropdown.SetValueWithoutNotify(entries[0]);
                RefreshSelectedLobbyDescription();
                infoDescriptor.SetTitle("Steam");
                infoDescriptor.SetDescription($"Found {cachedLobbies.Count} Steam lobbies.");
            }
            catch (Exception ex)
            {
                infoDescriptor.SetTitle("Error");
                infoDescriptor.SetDescription("Steam lobby refresh failed.");
                BasisDebug.LogError(ex.ToString());
            }
            finally
            {
                SetBusy(refreshLobbiesButton, false);
                ApplyUiState();
            }
        }

        private async Task OnJoinLobbyButton()
        {
            SetBusy(joinLobbyButton, true);

            try
            {
                if (!TryPrepareLocalPlayer(out string userName))
                {
                    return;
                }

                BasisSteamLobbyState selectedLobby = GetSelectedLobby();
                if (selectedLobby == null)
                {
                    infoDescriptor.SetTitle("Steam");
                    infoDescriptor.SetDescription("Select a Steam lobby first.");
                    return;
                }

                BasisSteamLobbyState joinedLobby = await BasisSteamLobbyService.JoinLobbyAsync(selectedLobby.LobbyId);
                if (joinedLobby == null)
                {
                    infoDescriptor.SetTitle("Steam");
                    infoDescriptor.SetDescription("Failed to join Steam lobby.");
                    return;
                }

                if (BasisNetworkManagement.Instance == null)
                {
                    infoDescriptor.SetTitle("Error");
                    infoDescriptor.SetDescription("BasisNetworkManagement is not available.");
                    BasisDebug.LogError("Steam JoinLobby: BasisNetworkManagement.Instance is null");
                    return;
                }

                await BasisNetworkConnection.ResetConnectionStateAsync(BasisNetworkManagement.Instance);

                BasisNetworkManagement.Instance.Transport = NetworkTransportType.Steam;
                BasisNetworkManagement.Instance.IsHostMode = false;
                BasisNetworkManagement.Instance.UpdateSteamLobbyState(joinedLobby.LobbyId, joinedLobby.HostSteamId, joinedLobby.UseRelay, joinedLobby.VirtualPort);
                BasisNetworkManagement.Instance.ClearPendingSteamWorld();

                infoDescriptor.SetTitle("Steam");
                infoDescriptor.SetDescription($"Connecting to Steam host {joinedLobby.HostSteamId}...");
                BasisMainMenu.Close();
                BasisCursorManagement.OnReset();
                BasisNetworkManagement.Instance.Connect();
                if (BasisDesktopEye.Instance != null)
                {
                    BasisDesktopEye.Instance.LockEye();
                }
            }
            catch (Exception ex)
            {
                infoDescriptor.SetTitle("Error");
                infoDescriptor.SetDescription("Steam lobby join failed.");
                BasisDebug.LogError(ex.ToString());
            }
            finally
            {
                SetBusy(joinLobbyButton, false);
                ApplyUiState();
            }
        }

        private async void OnLeaveLobbyButton()
        {
            try
            {
                if (BasisNetworkConnection.LocalPlayerIsConnected || BasisNetworkConnection.HasActiveClient())
                {
                    SetInfo("Disconnecting", "Disconnecting from the active Steam session...");
                    await BasisNetworkConnection.ResetConnectionStateAsync(BasisNetworkManagement.Instance);
                }
            }
            catch (Exception ex)
            {
                BasisDebug.LogError(ex.ToString());
            }

            BasisSteamLobbyService.LeaveLobby();
            cachedLobbies.Clear();
            lobbySelectionDropdown?.AssignEntries(new List<string> { "No lobbies loaded" });
            lobbySelectionDropdown?.SetValueWithoutNotify("No lobbies loaded");
            SetInfo("Steam", "Left the current Steam lobby.");
            RefreshSelectedLobbyDescription();
            ApplyUiState();
        }

        private void OnInviteFriendsButton()
        {
            if (!BasisSteamLobbyService.OpenInviteOverlay())
            {
                return;
            }

            SetInfo("Steam", "Opened the Steam invite overlay for the current lobby.");
        }

        private void HandleLobbyStateChanged(BasisSteamLobbyState lobbyState)
        {
            if (currentLobbyDescriptor == null)
            {
                return;
            }

            if (lobbyState == null || lobbyState.LobbyId == 0)
            {
                currentLobbyDescriptor.SetTitle("Lobby Details");
                currentLobbyDescriptor.SetDescription("No active Steam lobby.");
                ApplyUiState();
                return;
            }

            string role = lobbyState.IsHost ? "Host" : "Member";
            string lobbyName = string.IsNullOrWhiteSpace(lobbyState.LobbyName) ? "Unnamed Lobby" : lobbyState.LobbyName;
            string worldName = string.IsNullOrWhiteSpace(lobbyState.WorldName) ? "Unknown World" : lobbyState.WorldName;
            currentLobbyDescriptor.SetTitle(lobbyName);
            currentLobbyDescriptor.SetDescription($"Role: {role}\nWorld: {worldName}\nLobbyId: {lobbyState.LobbyId}\nHostSteamId: {lobbyState.HostSteamId}\nVirtualPort: {lobbyState.VirtualPort}");
            ApplyUiState();
        }

        private void HandleLobbyError(string error)
        {
            if (infoDescriptor == null)
            {
                return;
            }

            infoDescriptor.SetTitle("Steam Error");
            infoDescriptor.SetDescription(error);
        }

        private void RefreshSelectedLobbyDescription()
        {
            if (selectedLobbyDescriptor == null)
            {
                return;
            }

            BasisSteamLobbyState selectedLobby = GetSelectedLobby();
            if (selectedLobby == null)
            {
                selectedLobbyDescriptor.SetTitle("Lobby Preview");
                selectedLobbyDescriptor.SetDescription("Refresh Steam lobbies to inspect a world before joining.");
                ApplyUiState();
                return;
            }

            string lobbyName = string.IsNullOrWhiteSpace(selectedLobby.LobbyName) ? "Unnamed Lobby" : selectedLobby.LobbyName;
            string worldName = string.IsNullOrWhiteSpace(selectedLobby.WorldName) ? "Unknown World" : selectedLobby.WorldName;
            string worldUrl = string.IsNullOrWhiteSpace(selectedLobby.WorldUrl) ? "n/a" : selectedLobby.WorldUrl;
            selectedLobbyDescriptor.SetTitle(lobbyName);
            selectedLobbyDescriptor.SetDescription($"World: {worldName}\nLobbyId: {selectedLobby.LobbyId}\nHostSteamId: {selectedLobby.HostSteamId}\nVirtualPort: {selectedLobby.VirtualPort}\nWorldUrl: {worldUrl}");
            ApplyUiState();
        }

        private BasisSteamLobbyState GetSelectedLobby()
        {
            if (cachedLobbies.Count == 0 || lobbySelectionDropdown == null)
            {
                return null;
            }

            int selectedIndex = lobbySelectionDropdown.Index;
            if (selectedIndex < 0 || selectedIndex >= cachedLobbies.Count)
            {
                return null;
            }

            return cachedLobbies[selectedIndex];
        }

        private bool HasLobbySelection()
        {
            return GetSelectedLobby() != null;
        }

        private bool TryPrepareLocalPlayer(out string userName)
        {
            userName = usernameField.Value;
            if (string.IsNullOrWhiteSpace(userName))
            {
                infoDescriptor.SetTitle("Error");
                infoDescriptor.SetDescription("Display Name Was Empty");
                return false;
            }

            BasisLocalPlayer.Instance.DisplayName = userName;
            BasisLocalPlayer.Instance.SetSafeDisplayname();
            BasisDataStore.SaveString(BasisLocalPlayer.Instance.DisplayName, ServersProvider.UsernameFileName);
            return true;
        }

        private void ApplyUiState()
        {
            if (!HasLiveUi())
            {
                return;
            }

            bool hasLobby = BasisSteamLobbyService.State != null && BasisSteamLobbyService.State.LobbyId != 0;
            bool isConnected = BasisNetworkConnection.LocalPlayerIsConnected || BasisNetworkConnection.HasActiveClient();

            if (createGroup)
            {
                createGroup.SetActive(!hasLobby);
            }

            if (browserGroup)
            {
                browserGroup.SetActive(!hasLobby);
                browserGroup.SetTitle(cachedLobbies.Count > 0 ? $"Available Lobbies ({cachedLobbies.Count})" : "Available Lobbies");
            }

            if (sessionGroup)
            {
                sessionGroup.SetActive(hasLobby);
            }

            SetInteractable(friendsOnlyToggle?.ToggleComponent, !hasLobby);
            SetInteractable(privateLobbyToggle?.ToggleComponent, !hasLobby);
            SetInteractable(useRelayToggle?.ToggleComponent, !hasLobby);

            if (createLobbyButton)
            {
                createLobbyButton.Descriptor.SetActive(!hasLobby);
            }

            if (refreshLobbiesButton)
            {
                refreshLobbiesButton.Descriptor.SetActive(!hasLobby);
            }

            if (joinLobbyButton)
            {
                joinLobbyButton.Descriptor.SetActive(!hasLobby);
                joinLobbyButton.Descriptor.SetDescription(HasLobbySelection()
                    ? "Join the selected Steam lobby."
                    : "Refresh Steam lobbies and select one before joining.");

                if (joinLobbyButton.ButtonComponent != null)
                {
                    joinLobbyButton.ButtonComponent.interactable = !hasLobby && HasLobbySelection();
                }
            }

            if (leaveLobbyButton)
            {
                leaveLobbyButton.Descriptor.SetActive(hasLobby);
                leaveLobbyButton.Descriptor.SetTitle(isConnected ? "Disconnect And Leave Lobby" : "Leave Current Lobby");
                leaveLobbyButton.Descriptor.SetDescription(isConnected
                    ? "Disconnect from the active session and leave the Steam lobby."
                    : "Leave the current Steam lobby and clear its state.");
            }

            if (inviteFriendsButton)
            {
                inviteFriendsButton.Descriptor.SetActive(hasLobby);
                inviteFriendsButton.Descriptor.SetTitle("Invite Friends");
                inviteFriendsButton.Descriptor.SetDescription(hasLobby
                    ? "Open the Steam overlay invite dialog for this lobby."
                    : "Create or join a lobby before sending Steam invites.");

                if (inviteFriendsButton.ButtonComponent != null)
                {
                    inviteFriendsButton.ButtonComponent.interactable = hasLobby;
                }
            }

            if (sessionGroup)
            {
                sessionGroup.SetDescription(hasLobby
                    ? (isConnected ? "You are currently inside this Steam-backed Basis session." : "You are still inside the Steam lobby, but not connected to its Basis session.")
                    : "Actions for the currently active Steam lobby.");
            }
        }

        private static void SetBusy(PanelButton button, bool interactable)
        {
            if (button?.ButtonComponent != null)
            {
                button.ButtonComponent.interactable = !interactable;
            }
        }

        private static void SetInteractable(UnityEngine.UI.Selectable selectable, bool interactable)
        {
            if (selectable != null)
            {
                selectable.interactable = interactable;
            }
        }

        private void SetInfo(string title, string description)
        {
            if (infoDescriptor)
            {
                infoDescriptor.SetTitle(title);
                infoDescriptor.SetDescription(description);
            }
        }

        private bool HasLiveUi()
        {
            return infoDescriptor && currentLobbyDescriptor && selectedLobbyDescriptor;
        }
    }
}
