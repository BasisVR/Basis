using Basis.Network.Core;
using Basis.BasisUI;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices.Desktop;
using Basis.Scripts.Networking;
using System;
using System.Threading.Tasks;
using UnityEngine;

namespace Basis.Scripts.Networking.Steam
{
    public static class BasisSteamNetworkIntegration
    {
        private static bool isSubscribed;
        private static ulong bootstrappedLobbyId;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        private static void Initialize()
        {
            Subscribe();
            SyncLobbyStateToNetworkManagement(BasisSteamLobbyService.State);
        }

        private static void Subscribe()
        {
            if (isSubscribed)
            {
                return;
            }

            isSubscribed = true;
            BasisSteamLobbyService.OnLobbyStateChanged += SyncLobbyStateToNetworkManagement;
            BasisSteamLobbyService.OnLobbyJoinRequested += HandleLobbyJoinRequested;
            BasisNetworkManagement.OnIstanceCreated += OnNetworkManagementCreated;
            BasisNetworkConnection.OnConnectedToServer += HandleConnectedToServer;
            BasisNetworkConnection.OnDisconnectedFromServer += HandleDisconnectedFromServer;
        }

        private static void OnNetworkManagementCreated()
        {
            SyncLobbyStateToNetworkManagement(BasisSteamLobbyService.State);
        }

        private static void SyncLobbyStateToNetworkManagement(BasisSteamLobbyState lobbyState)
        {
            BasisNetworkManagement management = BasisNetworkManagement.Instance;
            if (management == null)
            {
                return;
            }

            if (lobbyState == null || lobbyState.LobbyId == 0)
            {
                management.ClearSteamLobbyState();
                return;
            }

            management.UpdateSteamLobbyState(lobbyState.LobbyId, lobbyState.HostSteamId, lobbyState.UseRelay, lobbyState.VirtualPort);
        }

        private static void HandleConnectedToServer(NetPeer peer)
        {
            BasisDeviceManagement.EnqueueOnMainThread(() =>
            {
                BasisNetworkManagement management = BasisNetworkManagement.Instance;
                if (management == null)
                {
                    return;
                }

                if (management.Transport != NetworkTransportType.Steam)
                {
                    return;
                }

                if (BasisSteamLobbyService.State.IsHost == false || management.CurrentSteamLobbyId == 0)
                {
                    return;
                }

                if (management.HasPendingSteamWorld() == false)
                {
                    return;
                }

                if (bootstrappedLobbyId == management.CurrentSteamLobbyId)
                {
                    return;
                }

                if (BasisNetworkSpawnItem.RequestSceneLoad(
                    management.PendingSteamWorldPassword,
                    management.PendingSteamWorldUrl,
                    true,
                    false,
                    out _,
                    2))
                {
                    bootstrappedLobbyId = management.CurrentSteamLobbyId;
                    BasisDebug.Log($"Steam host bootstrap queued world load for {management.PendingSteamWorldName}", BasisDebug.LogTag.Networking);
                }
                else
                {
                    BasisDebug.LogError("Steam host bootstrap failed to queue world load request.", BasisDebug.LogTag.Networking);
                }
            });
        }

        private static void HandleDisconnectedFromServer(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            bootstrappedLobbyId = 0;

            BasisNetworkManagement management = BasisNetworkManagement.Instance;
            if (management == null || management.Transport != NetworkTransportType.Steam)
            {
                return;
            }

            if (!BasisNetworkConnection.SuppressNextDisconnectUi)
            {
                BasisSteamLobbyService.LeaveLobby();
            }
        }

        private static void HandleLobbyJoinRequested(ulong lobbyId)
        {
            BasisDeviceManagement.EnqueueOnMainThread(() => _ = JoinRequestedLobbyOnMainThreadAsync(lobbyId));
        }

        private static async Task JoinRequestedLobbyOnMainThreadAsync(ulong lobbyId)
        {
            if (lobbyId == 0)
            {
                return;
            }

            BasisNetworkManagement management = BasisNetworkManagement.Instance;
            if (management == null)
            {
                return;
            }

            if (BasisSteamLobbyService.State.LobbyId == lobbyId &&
                management.Transport == NetworkTransportType.Steam &&
                (BasisNetworkConnection.LocalPlayerIsConnected || BasisNetworkConnection.HasActiveClient()))
            {
                return;
            }

            try
            {
                if (BasisSteamLobbyService.State.LobbyId != 0 && BasisSteamLobbyService.State.LobbyId != lobbyId)
                {
                    BasisSteamLobbyService.LeaveLobby();
                }

                await BasisNetworkConnection.ResetConnectionStateAsync(management);

                BasisSteamLobbyState joinedLobby = await BasisSteamLobbyService.JoinLobbyAsync(lobbyId);
                if (joinedLobby == null)
                {
                    BasisDebug.LogError($"Steam lobby invite join failed for lobby {lobbyId}.", BasisDebug.LogTag.Networking);
                    return;
                }

                management.Transport = NetworkTransportType.Steam;
                management.IsHostMode = false;
                management.UpdateSteamLobbyState(joinedLobby.LobbyId, joinedLobby.HostSteamId, joinedLobby.UseRelay, joinedLobby.VirtualPort);
                management.ClearPendingSteamWorld();

                BasisDebug.Log($"Joining Steam lobby from invite {joinedLobby.LobbyId}.", BasisDebug.LogTag.Networking);
                BasisMainMenu.Close();
                BasisCursorManagement.OnReset();
                management.Connect();
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
