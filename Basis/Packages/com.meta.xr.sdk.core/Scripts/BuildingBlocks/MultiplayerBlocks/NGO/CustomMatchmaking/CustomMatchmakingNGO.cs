/*
 * Copyright (c) Meta Platforms, Inc. and affiliates.
 * All rights reserved.
 *
 * Licensed under the Oculus SDK License Agreement (the "License");
 * you may not use the Oculus SDK except in compliance with the License,
 * which is provided at the time of installation or download, or which
 * otherwise accompanies this software in either electronic or hard copy form.
 *
 * You may obtain a copy of the License at
 *
 * https://developer.oculus.com/licenses/oculussdk/
 *
 * Unless required by applicable law or agreed to in writing, the Oculus SDK
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

#if UNITY_MULTIPLAYER_SERVICES_MODULE_DEFINED && UNITY_NGO_MODULE_DEFINED
#define UNITY_SERVICES_INSTALLED
#endif

using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Meta.XR.MultiplayerBlocks.Shared;
using UnityEngine;

#if UNITY_SERVICES_INSTALLED
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Lobbies.Models;
using Unity.Services.Lobbies;
using Unity.Services.Relay;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
#endif

namespace Meta.XR.MultiplayerBlocks.NGO
{
    /// <summary>
    /// The class responsible for the matchmaking behaviour when using the Unity Netcode for Gameobjects networking framework.
    /// It implements the <see cref="CustomMatchmaking.ICustomMatchmakingBehaviour"/>
    /// interface and is used by <see cref="CustomMatchmaking"/> class which handles the
    /// non-networking logic.
    /// </summary>
    public class CustomMatchmakingNGO : MonoBehaviour, CustomMatchmaking.ICustomMatchmakingBehaviour
    {
        /// <summary>
        /// Indicates whether this component should do the Unity Services login automatically using the <see cref="SignIn"/> method.
        /// </summary>
        [Tooltip(
            "Indicates whether this component should do the Unity Services login automatically using the SignIn method.")]
        public bool automaticallySignIn = true;

        private const string RelayJoinCodeKey = "joinCode";

#if UNITY_SERVICES_INSTALLED
        // ReSharper disable once MemberCanBePrivate.Global
        protected Lobby ConnectedLobby;

        private static bool IsLobbyHost(Lobby lobby) =>
            lobby != null && lobby.HostId == AuthenticationService.Instance.PlayerId;

        private IEnumerator HeartbeatLobbyCoroutine(string lobbyId, float waitTimeSeconds)
        {
            var delay = new WaitForSecondsRealtime(waitTimeSeconds);
            while (ConnectedLobby != null)
            {
                LobbyService.Instance.SendHeartbeatPingAsync(lobbyId);
                yield return delay;
            }
        }
#endif // UNITY_SERVICES_INSTALLED

#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
        private async void Awake()
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
        {
            if (automaticallySignIn)
            {
                await SignIn();
            }
        }

        /// <summary>
        /// Signs the player in to the Unity Services. This method is called on awake if <see cref="automaticallySignIn"/> is set
        /// to <c>true</c>.
        /// </summary>
        /// <remarks>This method may be overriden if the developer wants the sign-in process to be done in a different way.</remarks>
        protected virtual async Task SignIn()
        {
#if UNITY_SERVICES_INSTALLED
            await UnityServices.InitializeAsync();
#if UNITY_EDITOR
            // Authentication service would reuse the same authentication ID in Unity Editor, this blocks developer
            // from testing multiplayer game sessions with two Unity Editor instances. Workaround by clearing the token.
            // This doesn't apply for builds in runtime, hence guard with Editor only.
            AuthenticationService.Instance.ClearSessionToken();
#endif // UNITY_EDITOR
            await AuthenticationService.Instance.SignInAnonymouslyAsync();
#else
            throw new InvalidOperationException(
                "It's required to install the Unity Game Services packages to use this component.");
#endif
        }

        private void OnDestroy()
        {
#if UNITY_SERVICES_INSTALLED
            try
            {
                if (ConnectedLobby == null)
                {
                    return;
                }

                LeaveRoom();
            }
            catch (Exception e)
            {
                Debug.Log($"Error shutting down lobby: {e}");
            }
#endif // UNITY_SERVICES_INSTALLED
        }

        #region ICustomMatchmakingBehaviour

        /// <summary>
        /// Creates a new room with the selected <paramref name="roomCreationOptions"/>.
        /// An implementation of the <see cref="CustomMatchmaking.ICustomMatchmakingBehaviour"/> interface.
        /// </summary>
        /// <param name="roomCreationOptions">The selected options for creating a new room.</param>
        /// <returns>A task you should await for and receive a result with the information on the newly created room.</returns>
        public async Task<CustomMatchmaking.RoomOperationResult> CreateRoom(
            CustomMatchmaking.RoomCreationOptions roomCreationOptions)
        {
#if UNITY_SERVICES_INSTALLED
            var hostAllocation =
                await RelayService.Instance.CreateAllocationAsync(roomCreationOptions.MaxPlayersPerRoom);
            var joinCode = await RelayService.Instance.GetJoinCodeAsync(hostAllocation.AllocationId);

            var options = new CreateLobbyOptions
            {
                IsPrivate = roomCreationOptions.IsPrivate,
                Data = new Dictionary<string, DataObject>
                    { { RelayJoinCodeKey, new DataObject(DataObject.VisibilityOptions.Public, joinCode) } },
                Password = roomCreationOptions.RoomPassword
            };

            try
            {
                ConnectedLobby = await LobbyService.Instance.CreateLobbyAsync(roomCreationOptions.LobbyName,
                    roomCreationOptions.MaxPlayersPerRoom, options);

                // Send a heartbeat every 15 seconds to keep the room alive
                StartCoroutine(HeartbeatLobbyCoroutine(ConnectedLobby.Id, 15));

                FindObjectOfType<UnityTransport>().SetHostRelayData(hostAllocation.RelayServer.IpV4,
                    (ushort)hostAllocation.RelayServer.Port, hostAllocation.AllocationIdBytes, hostAllocation.Key,
                    hostAllocation.ConnectionData);
                var startedHost = NetworkManager.Singleton.StartHost();
                NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnectCallback;

                if (!startedHost)
                {
                    LeaveRoom();
                    throw new InvalidOperationException("Failed to start game host");
                }

                return new CustomMatchmaking.RoomOperationResult
                {
                    RoomToken = ConnectedLobby.LobbyCode,
                    RoomPassword = options.Password
                };
            }
            catch (Exception e)
            {
                return new CustomMatchmaking.RoomOperationResult
                {
                    ErrorMessage = e.Message
                };
            }

#else
            throw new InvalidOperationException(
                "It's required to install the Unity Game Services packages to use this component.");
#endif // UNITY_SERVICES_INSTALLED
        }

        /// <summary>
        /// Joins a room with the selected <paramref name="roomToken"/> and <paramref name="roomPassword"/>.
        /// An implementation of the <see cref="CustomMatchmaking.ICustomMatchmakingBehaviour"/> interface.
        /// </summary>
        /// <param name="roomToken">Unique identifier of the room you want to join.
        /// This is obtained from the <see cref="CustomMatchmaking.RoomOperationResult"/> obtained after doing a <see cref="CreateRoom"/> operation.</param>
        /// <param name="roomPassword">Optional password for rooms that require one when joining.
        /// This is obtained from the <see cref="CustomMatchmaking.RoomOperationResult"/> obtained after doing a <see cref="CreateRoom"/> operation.</param>
        /// <returns>A task you should await for and receive a result with the information on the newly joined room.</returns>
        public async Task<CustomMatchmaking.RoomOperationResult> JoinRoom(string roomToken, string roomPassword = null)
        {
#if UNITY_SERVICES_INSTALLED
            try
            {
                var options = string.IsNullOrEmpty(roomPassword)
                    ? null
                    : new JoinLobbyByCodeOptions { Password = roomPassword };

                var lobby = await LobbyService.Instance.JoinLobbyByCodeAsync(roomToken, options);
                return await JoinLobby(lobby, roomPassword);
            }
            catch (Exception e)
            {
                return new CustomMatchmaking.RoomOperationResult
                {
                    ErrorMessage = e.Message
                };
            }
#else
            throw new InvalidOperationException(
                "It's required to install the Unity Game Services packages to use this component.");
#endif // UNITY_SERVICES_INSTALLED
        }

        /// <summary>
        /// Joins an open room with the selected <paramref name="lobbyName"/>.
        /// An implementation of the <see cref="CustomMatchmaking.ICustomMatchmakingBehaviour"/> interface.
        /// </summary>
        /// <param name="lobbyName">Identifier of the lobby name containing the rooms you want to join.
        /// This is the same lobby name used during the <see cref="CreateRoom(CustomMatchmaking.RoomCreationOptions)"/> operation.</param>
        /// <returns>A task you should await for and receive a result with the information on the newly joined room.</returns>
        public async Task<CustomMatchmaking.RoomOperationResult> JoinOpenRoom(string lobbyName)
        {
#if UNITY_SERVICES_INSTALLED
            try
            {
                var lobby = await LobbyService.Instance.QuickJoinLobbyAsync(new QuickJoinLobbyOptions
                {
                    Filter = new List<QueryFilter>
                    {
                        new(field: QueryFilter.FieldOptions.Name, op: QueryFilter.OpOptions.EQ, value: lobbyName),
                        new(field: QueryFilter.FieldOptions.AvailableSlots, op: QueryFilter.OpOptions.GT, value: "0"),
                        new(field: QueryFilter.FieldOptions.IsLocked, op: QueryFilter.OpOptions.EQ, value: "false"),
                        new(field: QueryFilter.FieldOptions.HasPassword, op: QueryFilter.OpOptions.EQ, value: "false")
                    }
                });

                return await JoinLobby(lobby);
            }
            catch (Exception e)
            {
                return new CustomMatchmaking.RoomOperationResult
                {
                    ErrorMessage = e.Message
                };
            }
#else
            throw new InvalidOperationException(
                "It's required to install the Unity Game Services packages to use this component.");
#endif // UNITY_SERVICES_INSTALLED
        }

#if UNITY_SERVICES_INSTALLED
        private async Task<CustomMatchmaking.RoomOperationResult> JoinLobby(Lobby joinedLobby, string roomPassword = null)
        {
            ConnectedLobby = joinedLobby;

            var joinAllocation =
                await RelayService.Instance.JoinAllocationAsync(joinCode: ConnectedLobby.Data[RelayJoinCodeKey]
                    .Value);
            FindObjectOfType<UnityTransport>().SetClientRelayData(joinAllocation.RelayServer.IpV4,
                (ushort)joinAllocation.RelayServer.Port,
                joinAllocation.AllocationIdBytes, joinAllocation.Key, joinAllocation.ConnectionData,
                joinAllocation.HostConnectionData);

            var startedClient = NetworkManager.Singleton.StartClient();
            NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnectCallback;

            if (!startedClient)
            {
                LeaveRoom();
                throw new InvalidOperationException("Failed to start game client");
            }

            return new CustomMatchmaking.RoomOperationResult
            {
                RoomToken = joinedLobby.LobbyCode,
                RoomPassword = roomPassword
            };
        }
#endif // UNITY_SERVICES_INSTALLED

        /// <summary>
        /// Indicates whether you're currently connected to a game room.
        /// An implementation of the <see cref="CustomMatchmaking.ICustomMatchmakingBehaviour"/> interface.
        /// </summary>
#if UNITY_SERVICES_INSTALLED
        public bool IsConnected => ConnectedLobby != null;
#else
        public bool IsConnected => false;
#endif

        /// <summary>
        /// Indicates the token of the game room you're currently connected to. If not connected this is null.
        /// An implementation of the <see cref="CustomMatchmaking.ICustomMatchmakingBehaviour"/> interface.
        /// </summary>
#if UNITY_SERVICES_INSTALLED
        public string ConnectedRoomToken => ConnectedLobby?.LobbyCode;
#else
        public string ConnectedRoomToken => null;
#endif

        /// <summary>
        /// Indicates whether the implementation of this interface supports room passwords. This is also used
        /// for controlling which fields are shown in the inspector UI of the <see cref="CustomMatchmaking"/> component.
        /// An implementation of the <see cref="CustomMatchmaking.ICustomMatchmakingBehaviour"/> interface.
        /// </summary>
        public bool SupportsRoomPassword => true;

        /// <summary>
        /// Leaves the room you're currently connected to.
        /// An implementation of the <see cref="CustomMatchmaking.ICustomMatchmakingBehaviour"/> interface.
        /// </summary>
        public void LeaveRoom()
        {
#if UNITY_SERVICES_INSTALLED
            if (NetworkManager.Singleton != null)
            {
                NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnectCallback;
                NetworkManager.Singleton.Shutdown();
            }

            if (ConnectedLobby == null)
            {
                return;
            }

            if (IsLobbyHost(ConnectedLobby))
            {
                LobbyService.Instance?.DeleteLobbyAsync(ConnectedLobby.Id);
            }
            else
            {
                LobbyService.Instance?.RemovePlayerAsync(ConnectedLobby.Id, AuthenticationService.Instance.PlayerId);
            }

            ConnectedLobby = null;
#else
            throw new InvalidOperationException(
                "It's required to install the Unity Game Services packages to use this component.");
#endif // UNITY_SERVICES_INSTALLED
        }

        #endregion

#if UNITY_SERVICES_INSTALLED
        private void OnClientDisconnectCallback(ulong clientId)
        {
            if (NetworkManager.Singleton != null && clientId == NetworkManager.Singleton.LocalClientId)
            {
                LeaveRoom();
            }
        }
#endif // UNITY_SERVICES_INSTALLED
    }
}
