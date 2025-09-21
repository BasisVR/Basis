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

using System;
using System.Threading.Tasks;
using Meta.XR.BuildingBlocks;
using Meta.XR.ImmersiveDebugger;
using UnityEngine;
using UnityEngine.Events;

// ReSharper disable UnusedMember.Global
// ReSharper disable UnusedMethodReturnValue.Global

namespace Meta.XR.MultiplayerBlocks.Shared
{
    /// <summary>
    /// Component responsible for creating, joining and leaving game rooms upon developer request.
    /// <remarks>
    /// When using this component you'll also need to add another component to the same gameobject
    /// that implements the <see cref="ICustomMatchmakingBehaviour"/> interface.</remarks>
    /// </summary>
    [ExecuteAlways]
    public class CustomMatchmaking : MonoBehaviour
    {
        /// <summary>
        /// The interface used for implementing the matchmaking behaviour over different networking frameworks.
        /// </summary>
        /// <remarks>Currently there are implementations for Photon Fusion
        /// and Unity Netcode for Gameobjects but more may be added using this interface.</remarks>
        public interface ICustomMatchmakingBehaviour
        {
            /// <summary>
            /// Creates a new room with the selected <paramref name="options"/>.
            /// </summary>
            /// <param name="options">The selected options for creating a new room.</param>
            /// <returns>A task you should await for and receive a result with the information on the newly created room.</returns>
            public Task<RoomOperationResult> CreateRoom(RoomCreationOptions options);

            /// <summary>
            /// Joins a room with the selected <paramref name="roomToken"/> and <paramref name="roomPassword"/>.
            /// </summary>
            /// <param name="roomToken">Unique identifier of the room you want to join.
            /// This is obtained from the <see cref="RoomOperationResult"/> obtained after doing a <see cref="CreateRoom()"/> operation.</param>
            /// <param name="roomPassword">Optional password for rooms that require one when joining.
            /// This is obtained from the <see cref="RoomOperationResult"/> obtained after doing a <see cref="CreateRoom()"/> operation.</param>
            /// <returns>A task you should await for and receive a result with the information on the newly joined room.</returns>
            public Task<RoomOperationResult> JoinRoom(string roomToken, string roomPassword = null);

            /// <summary>
            /// Joins an open room with the selected <paramref name="lobbyName"/>.
            /// </summary>
            /// <param name="lobbyName">Identifier of the lobby name containing the rooms you want to join.
            /// This is the same lobby name used during the <see cref="CreateRoom()"/> operation.</param>
            /// <returns>A task you should await for and receive a result with the information on the newly joined room.</returns>
            public Task<RoomOperationResult> JoinOpenRoom(string lobbyName);

            /// <summary>
            /// Leaves the room you're currently connected to.
            /// </summary>
            public void LeaveRoom();

            /// <summary>
            /// Indicates whether you're currently connected to a game room.
            /// </summary>
            public bool IsConnected { get; }

            /// <summary>
            /// Indicates the token of the game room you're currently connected to. If not connected this is null.
            /// </summary>
            public string ConnectedRoomToken { get; }

            /// <summary>
            /// Indicates whether the implementation of this interface supports room passwords. This is also used
            /// for controlling which fields are shown in the inspector UI of the <see cref="CustomMatchmaking"/> component.
            /// </summary>
            public bool SupportsRoomPassword { get; }
        }

        /// <summary>
        /// Set of options to be passed to an <see cref="ICustomMatchmakingBehaviour.CreateRoom"/> operation
        /// indicating the desired room configuration.
        /// </summary>
        public struct RoomCreationOptions
        {
            /// <summary>
            /// Optional password to be required for other players to be able to join this game room.
            /// </summary>
            /// <remarks>If no password is required this field should not be set or set to <c>null</c></remarks>
            public string RoomPassword;

            /// <summary>
            /// The maximum number of players allowed per game room.
            /// </summary>
            public int MaxPlayersPerRoom;

            /// <summary>
            /// Indicates whether this game room is private. This causes the room to be hidden from a list of available rooms
            /// to other players and to only be joined by players who know it's unique identifier and password when one is set.
            /// </summary>
            public bool IsPrivate;

            /// <summary>
            /// Name of the game lobby this room belongs to. Lobbies are often used by networking frameworks to group
            /// game rooms by type or map.
            /// </summary>
            public string LobbyName;
        }

        /// <summary>
        /// Carries the information about the <see cref="ICustomMatchmakingBehaviour.CreateRoom"/> operation.
        /// </summary>
        /// <remarks>
        /// When successful it holds the unique identifier and password other players
        /// will use to <see cref="ICustomMatchmakingBehaviour.JoinRoom"/> this game room.
        /// When unsuccessful it holds the error message specifying what went wrong.
        /// </remarks>
        public struct RoomOperationResult
        {
            /// <summary>
            /// Indicates whether the <see cref="ICustomMatchmakingBehaviour.CreateRoom"/> operation succeeded.
            /// </summary>
            public bool IsSuccess => string.IsNullOrEmpty(ErrorMessage);

            /// <summary>
            /// Indicates what went wrong in the <see cref="ICustomMatchmakingBehaviour.CreateRoom"/> operation.
            /// On success this is <c>null</c>.
            /// </summary>
            public string ErrorMessage;

            /// <summary>
            /// Unique identifier of the created room to be used on <see cref="ICustomMatchmakingBehaviour.JoinRoom"/> operations.
            /// </summary>
            public string RoomToken;

            /// <summary>
            /// Optional password of the created room to be used on <see cref="ICustomMatchmakingBehaviour.JoinRoom"/> operations.
            /// When not used this is <c>null</c>.
            /// </summary>
            public string RoomPassword;
        }

        /// <summary>
        /// Event called when a <see cref="CreateRoom()"/> operation finished.
        /// </summary>
        [HideInInspector, Tooltip("Event called when a CreateRoom operation finished")]
        public UnityEvent<RoomOperationResult> onRoomCreationFinished;

        /// <summary>
        /// Event called when a <see cref="JoinRoom"/> operation finished.
        /// </summary>
        [HideInInspector, Tooltip("Event called when a JoinRoom operation finished")]
        public UnityEvent<RoomOperationResult> onRoomJoinFinished;

        /// <summary>
        /// Event called when a <see cref="LeaveRoom"/> operation finished.
        /// </summary>
        [HideInInspector, Tooltip("Event called when a LeaveRoom operation finished")]
        public UnityEvent onRoomLeaveFinished;

        /// <summary>
        /// Name of the game lobby the created room belongs to. Lobbies are often used by networking frameworks to group
        /// game rooms by type or map.
        /// </summary>
        [SerializeField, HideInInspector, Tooltip("Name of the game lobby the created room belongs to.")]
        private string lobbyName = "myLobby";
        public string LobbyName
        {
            get => lobbyName;
            set => lobbyName = value;
        }

        /// <summary>
        /// Indicates whether this game room is private. This causes the room to be hidden from a list of available rooms
        /// to other players and to only be joined by players who know it's unique identifier and password when one is set.
        /// </summary>
        [SerializeField, HideInInspector, Tooltip("Indicates whether this game room is private.")]
        private bool isPrivate;

        public bool IsPrivate
        {
            get => isPrivate;
            set => isPrivate = value;
        }

        /// <summary>
        /// The maximum number of players allowed in this game room.
        /// </summary>
        [SerializeField, HideInInspector, Tooltip("The maximum number of players allowed in this game room.")]
        private int maxPlayersPerRoom = 4;

        public int MaxPlayersPerRoom
        {
            get => maxPlayersPerRoom;
            set => maxPlayersPerRoom = value;
        }

        /// <summary>
        /// Indicates whether a password should be required for other players to be able to join this game room.
        /// </summary>
        [SerializeField, HideInInspector, Tooltip("Indicates whether a password should be required for other players to be able to join this game room.")]
        private bool isPasswordProtected;

        public bool IsPasswordProtected
        {
            get => isPasswordProtected;
            set => isPasswordProtected = value;
        }

        // ReSharper disable once MemberCanBePrivate.Global
        protected ICustomMatchmakingBehaviour MatchmakingBehaviour;
        private const string DebugCategory = "Custom Matchmaking";

        private void OnEnable()
        {
            MatchmakingBehaviour = this.GetInterfaceComponent<ICustomMatchmakingBehaviour>();
            if (MatchmakingBehaviour == null && Application.isPlaying)
            {
                throw new InvalidOperationException(
                    $"Using {nameof(CustomMatchmaking)} without an {nameof(ICustomMatchmakingBehaviour)} present in the game object.");
            }
        }

        /// <summary>
        /// Creates a new room with the selected options in the component public fields.
        /// </summary>
        /// <returns>A task you should await for and receive a result with the information on the newly created room.</returns>
        [DebugMember(Category = DebugCategory)]
        public async Task<RoomOperationResult> CreateRoom()
        {
            return await CreateRoom(new RoomCreationOptions
            {
                RoomPassword = IsPasswordProtected && SupportsRoomPassword ? GenerateRoomPassword() : null,
                MaxPlayersPerRoom = MaxPlayersPerRoom,
                LobbyName = LobbyName,
                IsPrivate = IsPrivate
            });
        }

        /// <summary>
        /// Creates a new room with the selected options.
        /// </summary>
        /// <param name="options">The selected options for creating a new room.</param>
        /// <returns>A task you should await for and receive a result with the information on the newly created room.</returns>
        // ReSharper disable once MemberCanBePrivate.Global
        public async Task<RoomOperationResult> CreateRoom(RoomCreationOptions options)
        {
            var result = await MatchmakingBehaviour.CreateRoom(options);

            if (!result.IsSuccess)
            {
                Debug.LogWarning($"[{nameof(CustomMatchmaking)}] Room creation failed: {result.ErrorMessage}");
            }

            onRoomCreationFinished?.Invoke(result);
            return result;
        }

        /// <summary>
        /// Joins a room with the selected <paramref name="roomToken"/> and <paramref name="roomPassword"/>.
        /// </summary>
        /// <param name="roomToken">Unique identifier of the room you want to join.
        /// This is obtained from the <see cref="RoomOperationResult"/> obtained after doing a <see cref="CreateRoom()"/> operation.</param>
        /// <param name="roomPassword">Optional password for rooms that require one when joining.
        /// This is obtained from the <see cref="RoomOperationResult"/> obtained after doing a <see cref="CreateRoom()"/> operation.</param>
        /// <returns>A task you should await for and receive a result with the information on the newly joined room.</returns>
        public async Task<RoomOperationResult> JoinRoom(string roomToken, string roomPassword)
        {
            var result = await MatchmakingBehaviour.JoinRoom(roomToken, roomPassword);

            if (!result.IsSuccess)
            {
                Debug.LogWarning($"[{nameof(CustomMatchmaking)}] Room join failed: {result.ErrorMessage}");
            }

            onRoomJoinFinished?.Invoke(result);
            return result;
        }

        /// <summary>
        /// Joins a room with the selected <paramref name="roomLobby"/>.
        /// </summary>
        /// <param name="roomLobby">Identifier of the room lobby you want to join.
        /// This is the same lobby name used in the <see cref="CreateRoom()"/> and <see cref="CreateRoom(RoomCreationOptions)"/> operations.</param>
        /// <returns>A task you should await for and receive a result with the information on the newly joined room.</returns>
        public async Task<RoomOperationResult> JoinOpenRoom(string roomLobby)
        {
            var result = await MatchmakingBehaviour.JoinOpenRoom(roomLobby);

            if (!result.IsSuccess)
            {
                Debug.LogWarning($"[{nameof(CustomMatchmaking)}] Join open room failed: {result.ErrorMessage}");
            }

            onRoomJoinFinished?.Invoke(result);
            return result;
        }

        /// <summary>
        /// Leaves the room you're currently connected to.
        /// </summary>
        public void LeaveRoom()
        {
            MatchmakingBehaviour.LeaveRoom();
            onRoomLeaveFinished?.Invoke();
        }

        /// <summary>
        /// Indicates whether you're currently connected to a game room.
        /// </summary>
        [DebugMember(Category = DebugCategory, Tweakable = false)]
        public bool IsConnected => MatchmakingBehaviour is { IsConnected: true };

        /// <summary>
        /// Indicates the token of the game room you're currently connected to. If not connected this is null.
        /// </summary>
        [DebugMember(Category = DebugCategory, Tweakable = false)]
        public string ConnectedRoomToken => MatchmakingBehaviour?.ConnectedRoomToken ?? string.Empty;

        /// <summary>
        /// Generates a random room password to be used in the created room when it's requested in a
        /// <see cref="CreateRoom()"/> operation.
        /// </summary>
        /// <returns>A random room password to be used in the created room.</returns>
        /// <remarks>May be overriden to implement your own random password generation.</remarks>
        protected virtual string GenerateRoomPassword() => RunTimeUtils.GenerateRandomString(16);

        /// <summary>
        /// Indicates whether the used implementation of the <see cref="ICustomMatchmakingBehaviour"/> interface supports room passwords. This is also used
        /// for controlling which fields are shown in the inspector UI of the <see cref="CustomMatchmaking"/> component.
        /// </summary>
        internal bool SupportsRoomPassword => MatchmakingBehaviour is { SupportsRoomPassword: true };
    }
}
