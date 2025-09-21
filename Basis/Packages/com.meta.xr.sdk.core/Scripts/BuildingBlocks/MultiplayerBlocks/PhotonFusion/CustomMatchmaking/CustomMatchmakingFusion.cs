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

using Fusion;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Meta.XR.MultiplayerBlocks.Shared;
using UnityEngine;
using Meta.XR.BuildingBlocks;
using UnityEngine.SceneManagement;

namespace Meta.XR.MultiplayerBlocks.Fusion
{
    /// <summary>
    /// The class responsible for the matchmaking behaviour when using the Photon Fusion networking framework.
    /// It implements the <see cref="CustomMatchmaking.ICustomMatchmakingBehaviour"/>
    /// interface and is used by <see cref="CustomMatchmaking"/> class which handles the
    /// non-networking logic.
    /// </summary>
    public class CustomMatchmakingFusion : MonoBehaviour, CustomMatchmaking.ICustomMatchmakingBehaviour
    {
        /// <summary>
        /// Indicates the chosen game mode to be used. Defaults to <c>GameMode.Shared</c>.
        /// </summary>
        [SerializeField, Tooltip("Indicates the chosen game mode to be used.")]
        private GameMode gameMode = GameMode.Shared;

        public GameMode GameMode
        {
            get => gameMode;
            set => gameMode = value;
        }

        /// <summary>
        /// Amount of time in seconds to wait for receiving the session list of a lobby before timing out.
        /// </summary>
        [SerializeField, Tooltip("Amount of time in seconds to wait for receiving the session list of a lobby before timing out.")]
        private int getSessionListTimeoutS = 10;

        private NetworkRunner _runnerPrefab;
        private List<SessionInfo> _sessionList;

        private void Awake()
        {
            _runnerPrefab = FindFirstObjectByType<NetworkRunner>();
            if (_runnerPrefab == null)
            {
                throw new InvalidOperationException($"Fusion {nameof(NetworkRunner)} not found");
            }
        }

        private void OnEnable()
        {
            FusionBBEvents.OnSessionListUpdated += OnSessionListUpdated;
        }

        private void OnDisable()
        {
            FusionBBEvents.OnSessionListUpdated -= OnSessionListUpdated;
        }

        /// <summary>Initializes the network runner to be used in the room session.</summary>
        private NetworkRunner InitializeNetworkRunner()
        {
            // Replicating the runtime duplication of the network runner pattern from FusionBootstrap.cs
            _runnerPrefab.gameObject.SetActive(false);
            var runner = Instantiate(_runnerPrefab);
            runner.gameObject.SetActive(true);
            DontDestroyOnLoad(runner);
            runner.name = "Temporary Runner Prefab";
            return runner;
        }

#region ICustomMatchmakingBehaviour

        /// <summary>
        /// Creates a new room with the selected <paramref name="options"/>.
        /// An implementation of the <see cref="CustomMatchmaking.ICustomMatchmakingBehaviour"/> interface.
        /// </summary>
        /// <param name="options">The selected options for creating a new room.</param>
        /// <returns>A task you should await for and receive a result with the information on the newly created room.</returns>
        public async Task<CustomMatchmaking.RoomOperationResult> CreateRoom(
            CustomMatchmaking.RoomCreationOptions options)
        {
            var sessionName = RunTimeUtils.GenerateRandomString(6, false, true, false);
            var runner = InitializeNetworkRunner();

            var result = await runner.StartGame(new StartGameArgs
            {
                GameMode = gameMode,
                Scene = GetSceneInfo(),
                CustomLobbyName = options.LobbyName,
                SessionName = sessionName,
                PlayerCount = options.MaxPlayersPerRoom,
                IsVisible = !options.IsPrivate
            });

            return new CustomMatchmaking.RoomOperationResult
            {
                ErrorMessage = result.Ok
                    ? null
                    : $"Failed to Start: {result.ShutdownReason}, Error Message: {result.ErrorMessage}",
                RoomToken = sessionName
            };
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
            var runner = InitializeNetworkRunner();
            var result = await runner.StartGame(new StartGameArgs
            {
                GameMode = gameMode,
                Scene = GetSceneInfo(),
                SessionName = roomToken
            });

            return new CustomMatchmaking.RoomOperationResult
            {
                ErrorMessage = result.Ok
                    ? null
                    : $"Failed to Start: {result.ShutdownReason}, Error Message: {result.ErrorMessage}",
                RoomToken = roomToken,
                RoomPassword = roomPassword
            };
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
            ClearSessionList();

            var runner = InitializeNetworkRunner();
            var joinLobbyResult = await runner.JoinSessionLobby(SessionLobby.Custom, lobbyName);

            if (!joinLobbyResult.Ok)
            {
                return new CustomMatchmaking.RoomOperationResult
                {
                    ErrorMessage = $"Failed to Start: {joinLobbyResult.ShutdownReason}, Error Message: {joinLobbyResult.ErrorMessage}"
                };
            }

            var sessionList = await GetSessionList(timeoutS: getSessionListTimeoutS);

            if (sessionList == null)
            {
                return new CustomMatchmaking.RoomOperationResult
                {
                    ErrorMessage = $"Failed to fetch the session list from the Lobby {lobbyName}"
                };
            }

            if (sessionList.Count == 0)
            {
                return new CustomMatchmaking.RoomOperationResult
                {
                    ErrorMessage = $"No available sessions to join in Lobby {lobbyName}"
                };
            }

            var session = SelectSessionToJoinFromList(sessionList);

            if (session == null)
            {
                return new CustomMatchmaking.RoomOperationResult
                {
                    ErrorMessage = $"Failed to select a session to join from the session list"
                };
            }

            var startGameResult = await runner.StartGame(new StartGameArgs
            {
                GameMode = gameMode,
                Scene = GetSceneInfo(),
                SessionName = session.Name
            });

            return new CustomMatchmaking.RoomOperationResult
            {
                ErrorMessage = startGameResult.Ok
                    ? null
                    : $"Failed to Start: {startGameResult.ShutdownReason}, Error Message: {startGameResult.ErrorMessage}",
                RoomToken = session.Name
            };
        }

        /// <summary>
        /// Leaves the room you're currently connected to.
        /// An implementation of the <see cref="CustomMatchmaking.ICustomMatchmakingBehaviour"/> interface.
        /// </summary>
        public void LeaveRoom()
        {
            for (var i = NetworkRunner.Instances.Count - 1; i >= 0; i--)
            {
                var runner = NetworkRunner.Instances[i];
                if (runner == null || !runner.IsRunning)
                {
                    continue;
                }

                runner.Shutdown();
                Destroy(runner.gameObject);
            }
        }

        /// <summary>
        /// Indicates whether the implementation of this interface supports room passwords. This is also used
        /// for controlling which fields are shown in the inspector UI of the <see cref="CustomMatchmaking"/> component.
        /// An implementation of the <see cref="CustomMatchmaking.ICustomMatchmakingBehaviour"/> interface.
        /// </summary>
        public bool SupportsRoomPassword => false;

        /// <summary>
        /// Indicates whether you're currently connected to a game room.
        /// An implementation of the <see cref="CustomMatchmaking.ICustomMatchmakingBehaviour"/> interface.
        /// </summary>
        public bool IsConnected => GetActiveNetworkRunner() != null;

        /// <summary>
        /// Indicates the token of the game room you're currently connected to. If not connected this is null.
        /// An implementation of the <see cref="CustomMatchmaking.ICustomMatchmakingBehaviour"/> interface.
        /// </summary>
        public string ConnectedRoomToken => GetActiveNetworkRunner()?.SessionInfo.Name;

        private static NetworkRunner GetActiveNetworkRunner()
        {
            for (var i = NetworkRunner.Instances.Count - 1; i >= 0; i--)
            {
                var runner = NetworkRunner.Instances[i];
                if (runner != null && runner.IsRunning)
                {
                    return runner;
                }
            }

            return null;
        }

        private static NetworkSceneInfo GetSceneInfo()
        {
            SceneRef sceneRef = default;
            if (TryGetActiveSceneRef(out var activeSceneRef))
            {
                sceneRef = activeSceneRef;
            }
            var sceneInfo = new NetworkSceneInfo();
            if (sceneRef.IsValid) {
                sceneInfo.AddSceneRef(sceneRef, LoadSceneMode.Additive);
            }
            return sceneInfo;
        }

        private static bool TryGetActiveSceneRef(out SceneRef sceneRef)
        {
            var activeScene = SceneManager.GetActiveScene();
            if (activeScene.buildIndex < 0 || activeScene.buildIndex >= SceneManager.sceneCountInBuildSettings) {
                sceneRef = default;
                return false;
            }
            sceneRef = SceneRef.FromIndex(activeScene.buildIndex);
            return true;
        }

#endregion // ICustomMatchmakingBehaviour

#region SessionListMethods

        private void ClearSessionList()
        {
            _sessionList = null;
        }

        private async Task<List<SessionInfo>> GetSessionList(float timeoutS)
        {
            var tcs = new TaskCompletionSource<List<SessionInfo>>();
            var cancellationTokenSource = new CancellationTokenSource();
            cancellationTokenSource.CancelAfter(TimeSpan.FromSeconds(timeoutS));
            cancellationTokenSource.Token.Register(() =>
            {
                tcs.TrySetResult(null);
            });
            while (_sessionList == null && !cancellationTokenSource.Token.IsCancellationRequested)
            {
                // ReSharper disable once MethodSupportsCancellation
                await Task.Delay(100);
            }
            if (_sessionList != null)
            {
                tcs.TrySetResult(_sessionList);
            }
            return await tcs.Task;
        }

        protected virtual SessionInfo SelectSessionToJoinFromList(List<SessionInfo> sessionList)
        {
            return sessionList.Count == 0 ? null : sessionList[0];
        }

#endregion

#region INetworkRunnerCallbacks

        private void OnSessionListUpdated(NetworkRunner runner, List<SessionInfo> sessionList)
        {
            _sessionList = sessionList;
        }

#endregion
    }
}
