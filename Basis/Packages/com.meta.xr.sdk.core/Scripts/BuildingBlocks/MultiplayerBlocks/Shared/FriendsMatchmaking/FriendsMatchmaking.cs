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

#if META_PLATFORM_SDK_DEFINED

using System;
using System.Threading.Tasks;
using Meta.XR.ImmersiveDebugger;
using Oculus.Platform;
using Oculus.Platform.Models;
using UnityEngine;
using UnityEngine.Events;

// ReSharper disable ClassWithVirtualMembersNeverInherited.Global
// ReSharper disable UnusedMethodReturnValue.Global
// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedMember.Global

namespace Meta.XR.MultiplayerBlocks.Shared
{
    /// <summary>
    /// Component responsible for doing matchmaking with friends from a player's account. This class
    /// makes it possible to invite friends to play together and uses the <see cref="CustomMatchmaking"/>
    /// class to join their game rooms.
    /// <remarks>
    /// <para>The player's Group Presence is set
    /// automatically based on the callbacks provided by the <see cref="CustomMatchmaking"/> Building Block.
    /// For more information please visit: https://developers.meta.com/horizon/documentation/unity/ps-group-presence-overview</para>
    /// <para>Upon <see cref="Awake"/> this component looks for available friend requests and automatically joins them.</para>
    /// <para>These behaviours may be modified by creating a class that inherits from this one and overriding the protected virtual members.</para>
    /// </remarks>
    /// </summary>
    public class FriendsMatchmaking : MonoBehaviour
    {
        /// <summary>
        /// API name of the Destination to be used when setting a player's Group Presence. This is obtained
        /// from developer.oculus.com under Engagement > Destinations after creating a new Destination.
        /// </summary>
        [SerializeField,
         Tooltip("Destination's API name obtained from developer.oculus.com under Engagement > Destinations.")]
        private string destinationApi = "destinationApi";

        /// <summary>
        /// API name of the Destination to be used when setting a player's Group Presence. This is obtained
        /// from developer.oculus.com under Engagement > Destinations after creating a new Destination.
        /// </summary>
        public string DestinationApi
        {
            get => destinationApi;
            set => destinationApi = value;
        }

        /// <summary>
        /// Optional message to be sent when inviting friends to join a game room.
        /// </summary>
        [SerializeField, Tooltip("Optional message to be sent when inviting friends to join a game room.")]
        private string inviteMessage = "Let's play together!";

        /// <summary>
        /// Optional message to be sent when inviting friends to join a game room.
        /// </summary>
        public string InviteMessage
        {
            get => inviteMessage;
            set => inviteMessage = value;
        }

        /// <summary>
        /// Maximum number of retries should a Platform SDK request fail.
        /// </summary>
        [SerializeField, Tooltip("Maximum number of retries should a Platform SDK request fail.")]
        private uint maxRetries = 3;

        /// <summary>
        /// Maximum number of retries should a Platform SDK request fail.
        /// </summary>
        public uint MaxRetries
        {
            get => maxRetries;
            set => maxRetries = value;
        }

        [SerializeField] private UnityEvent<CustomMatchmaking.RoomOperationResult> onMatchRequestFound;
        [SerializeField] private UnityEvent<Message<LaunchInvitePanelFlowResult>> onInvitationsSent;
        [SerializeField] private UnityEvent<Message<GroupPresenceLeaveIntent>> onLeaveIntentReceived;

        private CustomMatchmaking _customMatchmaking;
        private const string DebugCategory = "Friends Matchmaking";

#region Event Functions

        private void Awake()
        {
#pragma warning disable CS0618 // Type or member is obsolete
            _customMatchmaking = FindObjectOfType<CustomMatchmaking>();
#pragma warning restore CS0618 // Type or member is obsolete

            if (_customMatchmaking == null)
            {
                throw new InvalidOperationException(
                    $"{nameof(FriendsMatchmaking)}] No {nameof(CustomMatchmaking)} component was found in the scene as is a requirement of {nameof(FriendsMatchmaking)}");
            }

            PlatformInit.GetEntitlementInformation(OnEntitlementFinished);
        }

        private void OnEnable()
        {
            _customMatchmaking.onRoomLeaveFinished.AddListener(ClearGroupPresenceCallback);
            _customMatchmaking.onRoomCreationFinished.AddListener(OnRoomOperationResult);
            _customMatchmaking.onRoomJoinFinished.AddListener(OnRoomOperationResult);
        }

        private void OnDisable()
        {
            _customMatchmaking.onRoomLeaveFinished.RemoveListener(ClearGroupPresenceCallback);
            _customMatchmaking.onRoomCreationFinished.RemoveListener(OnRoomOperationResult);
            _customMatchmaking.onRoomJoinFinished.RemoveListener(OnRoomOperationResult);
        }

#endregion

#region Public API

        /// <summary>
        /// Launches the Friends Invite Panel so the player can invite their friends to play together. See <see cref="LaunchFriendsInvitePanelAsync"/>
        /// for the advanced version of this functionality.
        /// </summary>
        [DebugMember(Category = DebugCategory)]
        public void LaunchFriendsInvitePanel()
        {
            LaunchFriendsInvitePanelAsync();
        }

        /// <summary>
        /// Launches the Friends Invite Panel so the player can invite their friends to play together.
        /// </summary>
        /// <param name="inviteOptions">Optional parameter indicating the desired options for launching
        /// the Friends Invite Panel.</param>
        /// <remarks>More information available in https://developers.meta.com/horizon/documentation/unity/ps-invite-overview.</remarks>
        /// <returns>A task you should await for and receive a <see cref="Message"/> with the information on the result of the request.</returns>
        public Task<Message<InvitePanelResultInfo>> LaunchFriendsInvitePanelAsync(InviteOptions inviteOptions = null)
        {
            var tcs = new TaskCompletionSource<Message<InvitePanelResultInfo>>();

            GroupPresence.LaunchInvitePanel(inviteOptions ?? new InviteOptions()).OnComplete(message =>
            {
                if (message.IsError)
                {
                    Debug.LogError(
                        $"[{nameof(FriendsMatchmaking)}] {nameof(LaunchFriendsInvitePanelAsync)} failed: {message.GetError().Message}");
                }

                tcs.SetResult(message);
            });

            return tcs.Task;
        }

        /// <summary>
        /// Launches the Roster Panel to the player can view the other players who have joined their game room. See <see cref="LaunchRosterPanelAsync"/>
        /// for the advanced version of this functionality.
        /// </summary>
        [DebugMember(Category = DebugCategory)]
        public void LaunchRosterPanel()
        {
            LaunchRosterPanelAsync();
        }

        /// <summary>
        /// Launches the Roster Panel to the player can view the other players who have joined their game room.
        /// </summary>
        /// <param name="rosterOptions">Optional parameter indicating the desired options for launching
        /// the Roster Panel.</param>
        /// <remarks>More information available in https://developers.meta.com/horizon/documentation/unity/ps-roster.</remarks>
        /// <returns>A task you should await for and receive a <see cref="Message"/> with the information on the result of the request.</returns>
        public Task<Message> LaunchRosterPanelAsync(RosterOptions rosterOptions = null)
        {
            var tcs = new TaskCompletionSource<Message>();

            GroupPresence.LaunchRosterPanel(rosterOptions ?? new RosterOptions()).OnComplete(message =>
            {
                if (message.IsError)
                {
                    Debug.LogError(
                        $"[{nameof(FriendsMatchmaking)}] {nameof(LaunchRosterPanelAsync)} failed: {message.GetError().Message}");
                }

                tcs.SetResult(message);
            });

            return tcs.Task;
        }

#endregion

#region CustomMatchmaking interaction

        protected virtual async void OnRoomOperationResult(CustomMatchmaking.RoomOperationResult result)
        {
            if (result.IsSuccess)
            {
                await RegisterGameRoom(result.RoomToken, result.RoomPassword);
            }
        }

        protected virtual async Task JoinRoom(string roomId, string roomPassword)
        {
            if (_customMatchmaking.IsConnected && _customMatchmaking.ConnectedRoomToken != roomId)
            {
                _customMatchmaking.LeaveRoom();
            }

            await _customMatchmaking.JoinRoom(roomId, roomPassword);
        }

        protected virtual void ClearGroupPresenceCallback()
        {
            ClearGroupPresence();
        }

#endregion

#region Platform SDK interaction

        private async Task RegisterGameRoom(string roomId, string roomPassword = null)
        {
            Message lastResult = null;

            for (var i = 0; i < MaxRetries; i++)
            {
                lastResult = await SetGroupPresence(GetGroupPresenceOptions(roomId, roomPassword));

                if (!lastResult.IsError)
                {
                    return;
                }
            }

            Debug.LogError(
                $"[{nameof(FriendsMatchmaking)}] Max retries reached, failed to register game room: {lastResult?.GetError().Message}");
        }

        private static Task<Message> ClearGroupPresence()
        {
            var tcs = new TaskCompletionSource<Message>();

            GroupPresence.Clear().OnComplete(message =>
            {
                if (message.IsError)
                {
                    Debug.LogError(
                        $"[{nameof(FriendsMatchmaking)}] {nameof(ClearGroupPresence)} failed: {message.GetError().Message}");
                }

                tcs.SetResult(message);
            });

            return tcs.Task;
        }

        private static Task<Message> SetGroupPresence(GroupPresenceOptions groupPresenceOptions)
        {
            var tcs = new TaskCompletionSource<Message>();

            GroupPresence.Set(groupPresenceOptions).OnComplete(message => { tcs.SetResult(message); });

            return tcs.Task;
        }

        private void OnEntitlementFinished(PlatformInfo info)
        {
            GroupPresence.SetJoinIntentReceivedNotificationCallback(OnJoinIntentReceived);
            GroupPresence.SetInvitationsSentNotificationCallback(OnInvitationsSent);
            GroupPresence.SetLeaveIntentReceivedNotificationCallback(OnLeaveIntentNotification);
        }

        protected virtual async void OnJoinIntentReceived(Message<GroupPresenceJoinIntent> message)
        {
            var (roomId, roomPassword) =
                CustomMatchmakingUtils.ExtractMatchInfoFromSessionId(message.Data.MatchSessionId);
            onMatchRequestFound?.Invoke(new CustomMatchmaking.RoomOperationResult
            {
                RoomToken = roomId,
                RoomPassword = roomPassword,
                ErrorMessage = message.IsError ? message.GetError().Message : null
            });

            await JoinRoom(roomId, roomPassword);
        }

        private void OnInvitationsSent(Message<LaunchInvitePanelFlowResult> message)
        {
            onInvitationsSent?.Invoke(message);
        }

        private void OnLeaveIntentNotification(Message<GroupPresenceLeaveIntent> message)
        {
            onLeaveIntentReceived?.Invoke(message);
        }

        protected virtual GroupPresenceOptions GetGroupPresenceOptions(string roomId, string roomPassword = null)
        {
            var options = new GroupPresenceOptions();
            options.SetIsJoinable(true);
            options.SetDestinationApiName(DestinationApi);

            var matchInfo = CustomMatchmakingUtils.EncodeMatchInfoToSessionId(roomId, roomPassword);
            options.SetLobbySessionId(matchInfo);
            options.SetMatchSessionId(matchInfo);

            if (!string.IsNullOrEmpty(InviteMessage))
            {
                options.SetDeeplinkMessageOverride(InviteMessage);
            }

            return options;
        }

#endregion
    }
}

#endif // META_PLATFORM_SDK_DEFINED
