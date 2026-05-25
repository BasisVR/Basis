using Steamworks;
using Steamworks.Data;
using Basis.Network.Core;
using Basis.Scripts.Networking;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using UnityEngine;

namespace Basis.Scripts.Networking.Steam
{
    public static class BasisSteamLobbyService
    {
        public static readonly BasisSteamLobbyState State = new BasisSteamLobbyState();
        private static Lobby? currentLobby;
        private static bool steamCallbacksSubscribed;

        public static event Action<BasisSteamLobbyState> OnLobbyStateChanged;
        public static event Action<string> OnLobbyError;
        public static event Action<ulong> OnLobbyJoinRequested;

        public static bool EnsureReady()
        {
            if (!BasisSteamBootstrap.IsInitialized)
            {
                if (!BasisSteamBootstrap.EnsureInitialized(BasisSteamBootstrap.ActiveSettings))
                {
                    OnLobbyError?.Invoke("Steam is not initialized.");
                    return false;
                }
            }

            if (!SteamClient.IsLoggedOn)
            {
                OnLobbyError?.Invoke("Steam user is not logged in.");
                return false;
            }

            SubscribeToSteamCallbacks();
            return true;
        }

        public static async Task<BasisSteamLobbyState> CreateLobbyAsync(string lobbyName, BasisSteamBeeValidationResult world, bool friendsOnly, bool isPrivate, bool useRelay)
        {
            if (!EnsureReady())
            {
                return null;
            }

            BasisSteamSettings settings = BasisSteamBootstrap.ActiveSettings;
            int maxMembers = Mathf.Clamp(settings != null ? settings.DefaultMaxLobbyMembers : 32, 2, 250);
            int virtualPort = settings != null ? settings.RelayVirtualPort : 0;

            Lobby? created = await SteamMatchmaking.CreateLobbyAsync(maxMembers);
            if (!created.HasValue)
            {
                OnLobbyError?.Invoke("Failed to create Steam lobby.");
                return null;
            }

            Lobby lobby = created.Value;
            lobby.SetData(BasisSteamLobbyMetadata.Transport, BasisSteamNetworkStack.StackId);
            lobby.SetData(BasisSteamLobbyMetadata.Version, BasisNetworkVersion.ServerVersion.ToString(CultureInfo.InvariantCulture));
            lobby.SetData(BasisSteamLobbyMetadata.WorldUrl, world.WorldUrl);
            lobby.SetData(BasisSteamLobbyMetadata.WorldName, world.WorldName);
            lobby.SetData(BasisSteamLobbyMetadata.HostSteamId, SteamClient.SteamId.ToString());
            lobby.SetData(BasisSteamLobbyMetadata.VirtualPort, virtualPort.ToString(CultureInfo.InvariantCulture));
            lobby.SetData(BasisSteamLobbyMetadata.UseRelay, useRelay ? "1" : "0");
            lobby.SetData(BasisSteamLobbyMetadata.Name, string.IsNullOrWhiteSpace(lobbyName) ? SteamClient.Name : lobbyName);

            if (isPrivate)
            {
                lobby.SetPrivate();
            }
            else if (friendsOnly)
            {
                lobby.SetFriendsOnly();
            }
            else
            {
                lobby.SetPublic();
            }

            lobby.SetJoinable(true);

            ApplyState(lobby, true, useRelay);
            return CloneState();
        }

        public static async Task<BasisSteamLobbyState> JoinLobbyAsync(ulong lobbyId)
        {
            if (!EnsureReady())
            {
                return null;
            }

            Lobby? joined = await SteamMatchmaking.JoinLobbyAsync(lobbyId);
            if (!joined.HasValue)
            {
                OnLobbyError?.Invoke("Failed to join Steam lobby.");
                return null;
            }

            Lobby lobby = joined.Value;
            bool useRelay = ReadBool(lobby.GetData(BasisSteamLobbyMetadata.UseRelay), BasisSteamBootstrap.ActiveSettings == null || BasisSteamBootstrap.ActiveSettings.UseRelayByDefault);
            ApplyState(lobby, lobby.Owner.Id == SteamClient.SteamId, useRelay);
            return CloneState();
        }

        public static async Task<IReadOnlyList<BasisSteamLobbyState>> QueryLobbiesAsync(int maxResults = 30)
        {
            if (!EnsureReady())
            {
                return Array.Empty<BasisSteamLobbyState>();
            }

            Lobby[] lobbies = await SteamMatchmaking.LobbyList
                .WithKeyValue(BasisSteamLobbyMetadata.Transport, BasisSteamNetworkStack.StackId)
                .WithMaxResults(maxResults)
                .RequestAsync();

            if (lobbies == null || lobbies.Length == 0)
            {
                return Array.Empty<BasisSteamLobbyState>();
            }

            List<BasisSteamLobbyState> results = new List<BasisSteamLobbyState>(lobbies.Length);
            for (int index = 0; index < lobbies.Length; index++)
            {
                Lobby lobby = lobbies[index];
                BasisSteamLobbyState item = new BasisSteamLobbyState
                {
                    LobbyId = lobby.Id,
                    HostSteamId = ParseUlong(lobby.GetData(BasisSteamLobbyMetadata.HostSteamId)),
                    LobbyName = lobby.GetData(BasisSteamLobbyMetadata.Name),
                    WorldUrl = lobby.GetData(BasisSteamLobbyMetadata.WorldUrl),
                    WorldName = lobby.GetData(BasisSteamLobbyMetadata.WorldName),
                    VirtualPort = ParseInt(lobby.GetData(BasisSteamLobbyMetadata.VirtualPort)),
                    UseRelay = ReadBool(lobby.GetData(BasisSteamLobbyMetadata.UseRelay), true),
                    IsHost = false
                };
                results.Add(item);
            }

            return results;
        }

        public static void LeaveLobby()
        {
            if (State.LobbyId != 0)
            {
                if (currentLobby.HasValue)
                {
                    Lobby lobby = currentLobby.Value;
                    lobby.Leave();
                }
            }

            currentLobby = null;
            BasisSteamNetworkIntegration.ClearPendingSteamWorld();
            BasisSteamNetworkIntegration.ClearTransportState();
            State.Reset();
            OnLobbyStateChanged?.Invoke(CloneState());
        }

        public static bool OpenInviteOverlay()
        {
            if (!EnsureReady())
            {
                return false;
            }

            if (State.LobbyId == 0)
            {
                OnLobbyError?.Invoke("Create or join a Steam lobby first.");
                return false;
            }

            SteamFriends.OpenGameInviteOverlay((SteamId)State.LobbyId);
            return true;
        }

        public static bool TrySetHostSteamId(ulong hostSteamId)
        {
            if (State.LobbyId == 0)
            {
                return false;
            }

            if (!currentLobby.HasValue)
            {
                return false;
            }

            Lobby lobby = currentLobby.Value;
            lobby.SetData(BasisSteamLobbyMetadata.HostSteamId, hostSteamId.ToString(CultureInfo.InvariantCulture));
            currentLobby = lobby;
            State.HostSteamId = hostSteamId;
            OnLobbyStateChanged?.Invoke(CloneState());
            return true;
        }

        public static bool TrySetUseRelay(bool useRelay)
        {
            if (State.LobbyId == 0)
            {
                return false;
            }

            if (!currentLobby.HasValue)
            {
                return false;
            }

            Lobby lobby = currentLobby.Value;
            lobby.SetData(BasisSteamLobbyMetadata.UseRelay, useRelay ? "1" : "0");
            currentLobby = lobby;
            State.UseRelay = useRelay;
            OnLobbyStateChanged?.Invoke(CloneState());
            return true;
        }

        public static void HandleSteamShutdown()
        {
            if (steamCallbacksSubscribed)
            {
                SteamFriends.OnGameLobbyJoinRequested -= HandleGameLobbyJoinRequested;
                SteamMatchmaking.OnLobbyDataChanged -= HandleLobbyDataChanged;
                SteamMatchmaking.OnLobbyMemberLeave -= HandleLobbyMemberLeave;
                SteamMatchmaking.OnLobbyMemberDisconnected -= HandleLobbyMemberDisconnected;
                steamCallbacksSubscribed = false;
            }

            currentLobby = null;
            State.Reset();
        }

        private static void ApplyState(Lobby lobby, bool isHost, bool useRelay)
        {
            currentLobby = lobby;
            State.LobbyId = lobby.Id;
            State.HostSteamId = ParseUlong(lobby.GetData(BasisSteamLobbyMetadata.HostSteamId));
            State.LobbyName = lobby.GetData(BasisSteamLobbyMetadata.Name);
            State.WorldUrl = lobby.GetData(BasisSteamLobbyMetadata.WorldUrl);
            State.WorldName = lobby.GetData(BasisSteamLobbyMetadata.WorldName);
            State.VirtualPort = ParseInt(lobby.GetData(BasisSteamLobbyMetadata.VirtualPort));
            State.UseRelay = useRelay;
            State.IsHost = isHost;
            OnLobbyStateChanged?.Invoke(CloneState());
        }

        private static BasisSteamLobbyState CloneState()
        {
            return new BasisSteamLobbyState
            {
                LobbyId = State.LobbyId,
                HostSteamId = State.HostSteamId,
                LobbyName = State.LobbyName,
                WorldUrl = State.WorldUrl,
                WorldName = State.WorldName,
                VirtualPort = State.VirtualPort,
                UseRelay = State.UseRelay,
                IsHost = State.IsHost
            };
        }

        private static int ParseInt(string value)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : 0;
        }

        private static ulong ParseUlong(string value)
        {
            return ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong parsed) ? parsed : 0;
        }

        private static bool ReadBool(string value, bool defaultValue)
        {
            if (string.IsNullOrEmpty(value))
            {
                return defaultValue;
            }

            return value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static void SubscribeToSteamCallbacks()
        {
            if (steamCallbacksSubscribed)
            {
                return;
            }

            steamCallbacksSubscribed = true;
            SteamFriends.OnGameLobbyJoinRequested += HandleGameLobbyJoinRequested;
            SteamMatchmaking.OnLobbyDataChanged += HandleLobbyDataChanged;
            SteamMatchmaking.OnLobbyMemberLeave += HandleLobbyMemberLeave;
            SteamMatchmaking.OnLobbyMemberDisconnected += HandleLobbyMemberDisconnected;
        }

        private static void HandleGameLobbyJoinRequested(Lobby lobby, SteamId steamId)
        {
            OnLobbyJoinRequested?.Invoke(lobby.Id);
        }

        private static void HandleLobbyDataChanged(Lobby lobby)
        {
            if (State.LobbyId == 0 || lobby.Id != State.LobbyId)
            {
                return;
            }

            bool useRelay = ReadBool(lobby.GetData(BasisSteamLobbyMetadata.UseRelay), State.UseRelay);
            bool isHost = lobby.Owner.Id == SteamClient.SteamId;
            ApplyState(lobby, isHost, useRelay);
        }

        private static void HandleLobbyMemberLeave(Lobby lobby, Friend member)
        {
            HandleLobbyMemberRemoved(lobby, member);
        }

        private static void HandleLobbyMemberDisconnected(Lobby lobby, Friend member)
        {
            HandleLobbyMemberRemoved(lobby, member);
        }

        private static void HandleLobbyMemberRemoved(Lobby lobby, Friend member)
        {
            if (State.LobbyId == 0 || lobby.Id != State.LobbyId || State.IsHost)
            {
                return;
            }

            if ((ulong)member.Id != State.HostSteamId)
            {
                return;
            }

            OnLobbyError?.Invoke("Steam lobby host left the lobby.");
            LeaveLobby();
        }
    }
}
