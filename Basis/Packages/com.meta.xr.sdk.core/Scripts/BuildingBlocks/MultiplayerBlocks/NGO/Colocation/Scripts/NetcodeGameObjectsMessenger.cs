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
using System.Collections.Generic;
using System.Text;
using Meta.XR.MultiplayerBlocks.Colocation;
using Unity.Netcode;
using LogLevel = Meta.XR.MultiplayerBlocks.Colocation.LogLevel;

namespace Meta.XR.MultiplayerBlocks.NGO
{
    /// <summary>
    ///     A Netcode for GameObjects concrete implementation of INetworkMessenger
    ///     Used to send the RPC calls needed for a player to join another player's colocated space
    /// </summary>
    internal class NetcodeGameObjectsMessenger : NetworkBehaviour, INetworkMessenger
    {
        public event Action<ShareAndLocalizeParams> AnchorShareRequestReceived;
        public event Action<ShareAndLocalizeParams> AnchorShareRequestCompleted;

        private enum MessageEvent
        {
            AnchorShareRequest,
            AnchorShareComplete
        }

        private NetworkList<ulong> _networkIds;
        private NetworkList<ulong> _playerIds;

        private void Awake()
        {
            _playerIds = new NetworkList<ulong>();
            _networkIds = new NetworkList<ulong>();
        }

        public void RegisterLocalPlayer(ulong localPlayerId)
        {
            AddPlayerIdServerRPC(localPlayerId, NetworkManager.Singleton.LocalClientId);
        }

        public void SendAnchorShareRequest(ulong targetPlayerId, ShareAndLocalizeParams shareAndLocalizeParams)
        {
            Logger.Log(
                $"{nameof(NetcodeGameObjectsMessenger)}: Sending anchor share request to player {targetPlayerId}. (anchorID {shareAndLocalizeParams.anchorUUID})",
                LogLevel.Verbose);

            SendMessageToPlayer(
                MessageEvent.AnchorShareRequest, targetPlayerId,
                new NGOShareAndLocalizeParams() { Data = shareAndLocalizeParams });
        }

        public void SendAnchorShareCompleted(ulong targetPlayerId, ShareAndLocalizeParams shareAndLocalizeParams)
        {
            Logger.Log(
                $"{nameof(NetcodeGameObjectsMessenger)}: Sending anchor share completed to player {targetPlayerId}. (anchorID {shareAndLocalizeParams.anchorUUID})",
                LogLevel.Verbose);

            SendMessageToPlayer(
                MessageEvent.AnchorShareComplete, targetPlayerId,
                new NGOShareAndLocalizeParams() { Data = shareAndLocalizeParams });
        }

        private void SendMessageToPlayer(MessageEvent eventCode, ulong playerId, NGOShareAndLocalizeParams ngoData)
        {
            if (TryGetNetworkId(playerId, out ulong networkId))
            {
                SendMessageServerRPC(eventCode, networkId, ngoData);
            }
            else
            {
                Logger.Log(
                    $"{nameof(NetcodeGameObjectsMessenger)}: SendMessageToPlayer - networkID for player with ID {playerId} not found.",
                    LogLevel.Error);
                PrintIDDictionary();
            }
        }

        [ServerRpc(RequireOwnership = false)]
        private void AddPlayerIdServerRPC(ulong playerId, ulong networkId)
        {
            _playerIds.Add(playerId);
            _networkIds.Add(networkId);
        }

        private bool TryGetNetworkId(ulong playerId, out ulong networkId)
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
            return false;
        }

        [ServerRpc(RequireOwnership = false)]
        private void SendMessageServerRPC(MessageEvent eventCode, ulong netcodeId, NGOShareAndLocalizeParams data,
            ServerRpcParams serverRpcParams = default)
        {
            Logger.Log($"{nameof(NetcodeGameObjectsMessenger)} called FindRPCToCallServerRPC", LogLevel.Verbose);
            var clientRpcParams = new ClientRpcParams
            {
                Send = new ClientRpcSendParams { TargetClientIds = new List<ulong> { netcodeId } }
            };

            HandleMessageClientRPC(eventCode, data, clientRpcParams);
        }

        [ClientRpc]
        private void HandleMessageClientRPC(MessageEvent eventCode, NGOShareAndLocalizeParams ngoData,
            ClientRpcParams clientRpcParams = default)
        {
            switch (eventCode)
            {
                case MessageEvent.AnchorShareRequest:
                    AnchorShareRequestReceived?.Invoke(ngoData.Data);
                    break;
                case MessageEvent.AnchorShareComplete:
                    AnchorShareRequestCompleted?.Invoke(ngoData.Data);
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

            Logger.Log($"{nameof(NetcodeGameObjectsMessenger)}: ID dictionary is {stringBuilder.ToString()}",
                LogLevel.Verbose);
        }
    }
}
