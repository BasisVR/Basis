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
using System.Text;
using Fusion;

namespace Meta.XR.MultiplayerBlocks.Colocation.Fusion
{
    /// <summary>
    ///     A Photon Fusion concrete implementation of INetworkMessenger
    ///     Used to send the RPC calls needed for a player to join another player's colocated space
    /// </summary>
    internal class FusionMessenger : NetworkBehaviour, INetworkMessenger
    {
        [Networked, Capacity(10)] private NetworkLinkedList<int> _networkIds { get; }

        [Networked, Capacity(10)] private NetworkLinkedList<ulong> _playerIds { get; }

        public event Action<ShareAndLocalizeParams> AnchorShareRequestReceived;
        public event Action<ShareAndLocalizeParams> AnchorShareRequestCompleted;

        private enum MessageEvent
        {
            AnchorShareRequest,
            AnchorShareComplete
        }

        public void RegisterLocalPlayer(ulong localPlayerId)
        {
            Logger.Log($"{nameof(FusionMessenger)}: RegisterLocalPlayer: localPlayerId {localPlayerId}",
                LogLevel.Verbose);
            Logger.Log($"{nameof(FusionMessenger)} RegisterLocalPlayer: fusionId {Runner.LocalPlayer.PlayerId}",
                LogLevel.Verbose);
            AddPlayerIdHostRPC(localPlayerId, Runner.LocalPlayer.PlayerId);
        }

        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        private void AddPlayerIdHostRPC(ulong localPlayerId, int localNetworkId)
        {
            Logger.Log("Add Player Id Host RPC: player id", LogLevel.Verbose);
            _playerIds.Add(localPlayerId);
            Logger.Log("Add Player Id Host RPC: network id", LogLevel.Verbose);
            _networkIds.Add(localNetworkId);

            PrintIDDictionary();
        }

        private bool TryGetNetworkId(ulong playerId, out int networkId)
        {
            for (var i = 0; i < _playerIds.Count; i++)
            {
                if (playerId == _playerIds[i])
                {
                    networkId = _networkIds[i];
                    return true;
                }
            }

            networkId = 0;
            Logger.Log($"FusionMessenger: playerId {playerId} got invalid networkId {networkId}", LogLevel.Error);
            return false;
        }

        public void SendAnchorShareRequest(ulong targetPlayerId, ShareAndLocalizeParams shareAndLocalizeParams)
        {
            Logger.Log(
                $"{nameof(FusionMessenger)}: Sending anchor share request to player {targetPlayerId}. (anchorID {shareAndLocalizeParams.anchorUUID})",
                LogLevel.Verbose);
            var fusionData = new FusionShareAndLocalizeParams(shareAndLocalizeParams);
            SendMessageToPlayer(MessageEvent.AnchorShareRequest, targetPlayerId, fusionData);
        }

        public void SendAnchorShareCompleted(ulong targetPlayerId, ShareAndLocalizeParams shareAndLocalizeParams)
        {
            Logger.Log(
                $"{nameof(FusionMessenger)}: Sending anchor share completed to player {targetPlayerId}. (anchorID {shareAndLocalizeParams.anchorUUID})",
                LogLevel.Verbose);
            var fusionData = new FusionShareAndLocalizeParams(shareAndLocalizeParams);
            SendMessageToPlayer(MessageEvent.AnchorShareComplete, targetPlayerId, fusionData);
        }

        private void SendMessageToPlayer(MessageEvent eventCode, ulong playerId,
            FusionShareAndLocalizeParams fusionData)
        {
            Logger.Log($"Calling SendMessageToPlayer with MessageEvent: {eventCode}, to playerId {playerId}",
                LogLevel.Verbose);
            if (TryGetNetworkId(playerId, out int fusionId))
            {
                Logger.Log($"Calling FindRPCToCallServerRPC playerId {playerId} maps to fusionId {fusionId}",
                    LogLevel.Verbose);
                FindRPCToCallServerRPC(eventCode, fusionId, fusionData);
            }
            else
            {
                Logger.Log($"Could not find fusionId for playerId {playerId}", LogLevel.Error);
            }
        }

        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        private void FindRPCToCallServerRPC(MessageEvent eventCode, int fusionId,
            FusionShareAndLocalizeParams fusionData, RpcInfo info = default)
        {
            Logger.Log("FindRPCToCallServerRPC called", LogLevel.Verbose);
            PlayerRef fusionPlayerRef = PlayerRef.FromIndex(fusionId);
            Logger.Log("Created PlayerRef right before calling HandleMessageClientRPC", LogLevel.Verbose);
            HandleMessageClientRPC(fusionPlayerRef, eventCode, fusionData);
        }

        [Rpc(RpcSources.All, RpcTargets.All)]
        private void HandleMessageClientRPC([RpcTarget] PlayerRef playerRef, MessageEvent eventCode,
            FusionShareAndLocalizeParams fusionData)
        {
            Logger.Log($"HandleMessageClientRPC: {eventCode.ToString()}", LogLevel.Verbose);
            switch (eventCode)
            {
                case MessageEvent.AnchorShareRequest:
                    AnchorShareRequestReceived?.Invoke(fusionData.GetShareAndLocalizeParams());
                    break;
                case MessageEvent.AnchorShareComplete:
                    AnchorShareRequestCompleted?.Invoke(fusionData.GetShareAndLocalizeParams());
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(eventCode), eventCode, null);
            }
        }

        private void PrintIDDictionary()
        {
            var stringBuilder = new StringBuilder();
            for (var i = 0; i < _playerIds.Count; i++)
            {
                stringBuilder.Append($"[{_playerIds[i]},{_networkIds[i]}]");
                if (i < _playerIds.Count - 1)
                {
                    stringBuilder.Append(",");
                }
            }

            Logger.Log($"{nameof(FusionMessenger)}: ID dictionary is {stringBuilder.ToString()}", LogLevel.Verbose);
        }
    }
}
