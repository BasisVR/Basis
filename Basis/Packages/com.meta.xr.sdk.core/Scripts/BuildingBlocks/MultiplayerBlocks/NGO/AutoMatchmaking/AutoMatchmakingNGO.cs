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

#if UNITY_SERVICES_INSTALLED
using System.Collections;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Lobbies.Models;
using Unity.Services.Lobbies;
using Unity.Services.Relay;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
#endif

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace Meta.XR.MultiplayerBlocks.NGO
{
    /// <summary>
    /// The class for doing auto-matchmaking for your game when using the Unity Netcode for Gameobjects networking framework.
    /// On <c>Awake()</c> this class will look for an available game room or create a new one if none is found.
    /// This room will then be automatically joined and the game session is ready to be started.
    /// </summary>
    /// <remarks>This behaviour of auto-matchmaking is best suited for projects in the prototype phase and should
    /// be replaced by another matchmaking system once your project is closer to production.</remarks>
    public class AutoMatchmakingNGO : MonoBehaviour
    {
        /// <summary>
        /// The maximum number of players allowed per game room.
        /// </summary>
        public int maxPlayersPerRoom = 4;

        /// <summary>
        /// The name of the lobby that will be automatically created when no available game rooms are found to be joined.
        /// </summary>
        public string lobbyName = "lobbyName";

        /// <summary>
        /// The maximum number of retries this class will attempt to connect to a game room if a connection attempt fails.
        /// </summary>
        public int maxRetries = 3;

        /// <summary>
        /// The interval range, in seconds, from which a random value will be chosen and awaited before attempting a new
        /// connection after the previous attempt failed.
        /// </summary>
        public Vector2 retryInterval = new(1f, 3f);
        private const string JoinCodeKey = "joinCode";

#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
        private async void Awake()
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
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

            _connectedLobby = await CreateOrJoinLobby();
            if (IsLobbyHost(_connectedLobby))
            {
                // Send a heartbeat every 15 seconds to keep the room alive
                StartCoroutine(HeartbeatLobbyCoroutine(_connectedLobby.Id, 15));
            }

#else
            throw new InvalidOperationException(
                "It's required to install the Unity Game Services packages to use this component.");
#endif
        }

#if UNITY_SERVICES_INSTALLED
        private Lobby _connectedLobby;
        private static bool IsLobbyHost(Lobby lobby) => lobby != null && lobby.HostId == AuthenticationService.Instance.PlayerId;

        private async Task<Lobby> CreateOrJoinLobby()
        {
            try
            {
                return await TryTask(JoinLobby, retryInterval, maxRetries);
            }
            catch (Exception e)
            {
                if (e is LobbyServiceException lobbyServiceException && lobbyServiceException.Reason != LobbyExceptionReason.NoOpenLobbies)
                {
                    Debug.LogWarning($"Cannot join lobby because {lobbyServiceException.Reason}");
                }

                // No lobbies available, create a new one instead
                return await TryTask(CreateLobby, retryInterval, maxRetries);
            }
        }

        private static async Task<Lobby> TryTask(Func<Task<Lobby>> task, Vector2 retryInterval, int maxRetries)
        {
            var exception = new Exception();

            for (var i = 0; i < maxRetries; i++)
            {
                try
                {
                    return await task();
                }
                catch (Exception e)
                {
                    exception = e;
                    await Task.Delay(TimeSpan.FromSeconds(UnityEngine.Random.Range(retryInterval.x, retryInterval.y)));
                }
            }

            throw exception;
        }

        private static async Task<Lobby> JoinLobby()
        {
            var lobby = await LobbyService.Instance.QuickJoinLobbyAsync();
            var joinAllocation = await RelayService.Instance.JoinAllocationAsync(joinCode: lobby.Data[JoinCodeKey].Value);

            FindObjectOfType<UnityTransport>().SetClientRelayData(joinAllocation.RelayServer.IpV4, (ushort)joinAllocation.RelayServer.Port,
                joinAllocation.AllocationIdBytes, joinAllocation.Key, joinAllocation.ConnectionData, joinAllocation.HostConnectionData);

            NetworkManager.Singleton.StartClient();
            return lobby;
        }

        private async Task<Lobby> CreateLobby()
        {
            var allocation = await RelayService.Instance.CreateAllocationAsync(maxPlayersPerRoom);
            var joinCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);

            var lobby = await LobbyService.Instance.CreateLobbyAsync(lobbyName, maxPlayersPerRoom, new CreateLobbyOptions
            {
                Data = new Dictionary<string, DataObject> { { JoinCodeKey, new DataObject(DataObject.VisibilityOptions.Public, joinCode) } },
                IsPrivate = false
            });

            FindObjectOfType<UnityTransport>().SetHostRelayData(allocation.RelayServer.IpV4, (ushort)allocation.RelayServer.Port, allocation.AllocationIdBytes, allocation.Key, allocation.ConnectionData);
            NetworkManager.Singleton.StartHost();
            return lobby;
        }

        private IEnumerator HeartbeatLobbyCoroutine(string lobbyId, float waitTimeSeconds) {
            var delay = new WaitForSecondsRealtime(waitTimeSeconds);
            while (_connectedLobby != null) {
                LobbyService.Instance.SendHeartbeatPingAsync(lobbyId);
                yield return delay;
            }
        }

        private void OnDestroy() {
            try {
                if (_connectedLobby == null)
                {
                    return;
                }

                LeaveLobby();
            }
            catch (Exception e) {
                Debug.Log($"Error shutting down lobby: {e}");
            }
        }

        private void LeaveLobby()
        {
            if (IsLobbyHost(_connectedLobby))
            {
                LobbyService.Instance.DeleteLobbyAsync(_connectedLobby.Id);
            }
            else
            {
                LobbyService.Instance.RemovePlayerAsync(_connectedLobby.Id, AuthenticationService.Instance.PlayerId);
            }

            _connectedLobby = null;
        }
#endif // UNITY_SERVICES_INSTALLED
    }
}
