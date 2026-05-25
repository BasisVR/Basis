using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices.Desktop;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using System;
using System.Globalization;
using System.Threading.Tasks;
using UnityEngine;

namespace Basis.Scripts.Networking.Steam
{
    public static class BasisSteamNetworkIntegration
    {
        private const ushort DefaultPort = 4296;

        private static bool isSubscribed;
        private static ulong bootstrappedLobbyId;
        private static ulong pendingLobbyId;
        private static string pendingWorldUrl = string.Empty;
        private static string pendingWorldPassword = string.Empty;
        private static string pendingWorldName = string.Empty;
        private static bool suppressNextLobbyLeave;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        private static void Initialize()
        {
            BasisSteamNetworkStack.Register();
            Subscribe();
            SyncLobbyStateToTransportConfig(BasisSteamLobbyService.State);
        }

        private static void Subscribe()
        {
            if (isSubscribed)
            {
                return;
            }

            isSubscribed = true;
            BasisSteamLobbyService.OnLobbyStateChanged += SyncLobbyStateToTransportConfig;
            BasisSteamLobbyService.OnLobbyJoinRequested += HandleLobbyJoinRequested;
            BasisNetworkManagement.OnIstanceCreated += OnNetworkManagementCreated;
        }

        private static void SubscribeNetworkEvents()
        {
            BasisNetworkPlayer.OnLocalPlayerJoined -= HandleLocalPlayerJoined;
            BasisNetworkPlayer.OnLocalPlayerJoined += HandleLocalPlayerJoined;
            BasisNetworkPlayer.OnLocalPlayerLeft -= HandleLocalPlayerLeft;
            BasisNetworkPlayer.OnLocalPlayerLeft += HandleLocalPlayerLeft;
        }

        private static void OnNetworkManagementCreated()
        {
            SyncLobbyStateToTransportConfig(BasisSteamLobbyService.State);
        }

        public static void PrepareSteamConnection(BasisSteamLobbyState lobbyState, bool isHost, string worldUrl = "", string worldPassword = "", string worldName = "")
        {
            SyncLobbyStateToTransportConfig(lobbyState);
            SubscribeNetworkEvents();

            BasisNetworkManagement.NetworkStackId = BasisSteamNetworkStack.StackId;
            BasisNetworkManagement.IsHostMode = isHost;
            BasisNetworkManagement.Ip = isHost ? "localhost" : (lobbyState?.HostSteamId.ToString(CultureInfo.InvariantCulture) ?? "steam");
            if (BasisNetworkManagement.Port == 0)
            {
                BasisNetworkManagement.Port = DefaultPort;
            }

            if (isHost)
            {
                SetPendingSteamWorld(lobbyState?.LobbyId ?? 0, worldUrl, worldPassword, worldName);
            }
            else
            {
                ClearPendingSteamWorld();
            }
        }

        public static async Task ResetNetworkStateAsync(bool keepSteamLobby)
        {
            suppressNextLobbyLeave = keepSteamLobby;
            try
            {
                if (BasisNetworkConnection.LocalPlayerIsConnected)
                {
                    await BasisNetworkLifeCycle.Destroy();
                    BasisNetworkLifeCycle.Initalize();
                    return;
                }

                if (!BasisNetworkManagement.IsInitialized)
                {
                    BasisNetworkLifeCycle.Initalize();
                }
            }
            finally
            {
                suppressNextLobbyLeave = false;
            }
        }

        public static void SyncLobbyStateToTransportConfig(BasisSteamLobbyState lobbyState)
        {
            BasisSteamTransportConfig config = BasisSteamNetworkStack.GetConfig();
            if (config == null)
            {
                return;
            }

            if (lobbyState == null || lobbyState.LobbyId == 0)
            {
                config.Clear();
                return;
            }

            config.LobbyId = lobbyState.LobbyId;
            config.HostSteamId = lobbyState.HostSteamId;
            config.UseSteamRelay = lobbyState.UseRelay;
            config.VirtualPort = lobbyState.VirtualPort;
        }

        public static void ClearTransportState()
        {
            BasisSteamNetworkStack.GetConfig()?.Clear();
            if (string.Equals(BasisNetworkManagement.NetworkStackId, BasisSteamNetworkStack.StackId, StringComparison.OrdinalIgnoreCase))
            {
                BasisNetworkManagement.NetworkStackId = string.Empty;
            }
        }

        public static void SetPendingSteamWorld(ulong lobbyId, string worldUrl, string worldPassword, string worldName)
        {
            pendingLobbyId = lobbyId;
            pendingWorldUrl = worldUrl ?? string.Empty;
            pendingWorldPassword = worldPassword ?? string.Empty;
            pendingWorldName = worldName ?? string.Empty;
        }

        public static void ClearPendingSteamWorld()
        {
            pendingLobbyId = 0;
            pendingWorldUrl = string.Empty;
            pendingWorldPassword = string.Empty;
            pendingWorldName = string.Empty;
        }

        public static bool HasPendingSteamWorld()
        {
            return !string.IsNullOrWhiteSpace(pendingWorldUrl) && !string.IsNullOrWhiteSpace(pendingWorldPassword);
        }

        private static void HandleLocalPlayerJoined(BasisNetworkPlayer networkedPlayer, BasisLocalPlayer localPlayer)
        {
            BasisDeviceManagement.EnqueueOnMainThread(() =>
            {
                if (!IsSteamStackActive())
                {
                    return;
                }

                if (BasisSteamLobbyService.State.IsHost == false || pendingLobbyId == 0)
                {
                    return;
                }

                if (HasPendingSteamWorld() == false)
                {
                    return;
                }

                if (bootstrappedLobbyId == pendingLobbyId)
                {
                    return;
                }

                if (BasisNetworkSpawnItem.RequestSceneLoad(
                    pendingWorldPassword,
                    pendingWorldUrl,
                    true,
                    false,
                    out _,
                    2))
                {
                    bootstrappedLobbyId = pendingLobbyId;
                    BasisDebug.Log($"Steam host bootstrap queued world load for {pendingWorldName}", BasisDebug.LogTag.Networking);
                }
                else
                {
                    BasisDebug.LogError("Steam host bootstrap failed to queue world load request.", BasisDebug.LogTag.Networking);
                }
            });
        }

        private static void HandleLocalPlayerLeft(BasisNetworkPlayer networkedPlayer, BasisLocalPlayer localPlayer)
        {
            bootstrappedLobbyId = 0;

            if (!IsSteamStackActive() || suppressNextLobbyLeave)
            {
                return;
            }

            BasisSteamLobbyService.LeaveLobby();
        }

        private static bool IsSteamStackActive()
        {
            return string.Equals(BasisNetworkManagement.NetworkStackId, BasisSteamNetworkStack.StackId, StringComparison.OrdinalIgnoreCase);
        }

        private static void HandleLobbyJoinRequested(ulong lobbyId)
        {
            BasisDeviceManagement.EnqueueOnMainThread(() => _ = JoinRequestedLobbyOnMainThreadAsync(lobbyId));
        }

        private static async Task JoinRequestedLobbyOnMainThreadAsync(ulong lobbyId)
        {
            if (lobbyId == 0)
            {
                BasisDebug.LogError("Steam JoinRequestedLobby called with lobbyId=0", BasisDebug.LogTag.Networking);
                return;
            }

            if (BasisSteamLobbyService.State.LobbyId == lobbyId &&
                IsSteamStackActive() &&
                BasisNetworkConnection.LocalPlayerIsConnected)
            {
                return;
            }

            try
            {
                if (BasisSteamLobbyService.State.LobbyId != 0 && BasisSteamLobbyService.State.LobbyId != lobbyId)
                {
                    BasisSteamLobbyService.LeaveLobby();
                }

                await ResetNetworkStateAsync(keepSteamLobby: true);

                BasisSteamLobbyState joinedLobby = await BasisSteamLobbyService.JoinLobbyAsync(lobbyId);
                if (joinedLobby == null)
                {
                    BasisDebug.LogError($"Steam lobby invite join failed for lobby {lobbyId}.", BasisDebug.LogTag.Networking);
                    return;
                }

                PrepareSteamConnection(joinedLobby, false);

                BasisDebug.Log($"Joining Steam lobby from invite {joinedLobby.LobbyId}.", BasisDebug.LogTag.Networking);
                BasisMainMenu.Close();
                BasisCursorManagement.OnReset();
                BasisNetworkManagement.Connect();
                if (BasisDesktopEye.Instance != null)
                {
                    BasisDesktopEye.Instance.LockEye();
                }
            }
            catch (Exception ex)
            {
                BasisDebug.LogError(ex.ToString(), BasisDebug.LogTag.Networking);
            }
        }
    }
}
