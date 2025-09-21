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

using System.Collections.Generic;
using Meta.XR.MultiplayerBlocks.Colocation;
using Unity.Netcode;

namespace Meta.XR.MultiplayerBlocks.NGO
{
    /// <summary>
    ///     A Netcode for GameObjects concrete implementation of INetworkData
    ///     Used to manage a Player and Anchor list that all players that colocated are in
    /// </summary>
    internal class NetcodeGameObjectsNetworkData : NetworkBehaviour, INetworkData
    {
        private readonly NetworkVariable<uint> colocationGroupCount = new();

        private NetworkList<NetcodeGameObjectsAnchor> _ngoAnchorList;
        private NetworkList<NetcodeGameObjectsPlayer> _ngoPlayerList;

        private void Awake()
        {
            _ngoAnchorList = new NetworkList<NetcodeGameObjectsAnchor>();
            _ngoPlayerList = new NetworkList<NetcodeGameObjectsPlayer>();
        }

        public void AddPlayer(Player player)
        {
            var ngoPlayer = new NetcodeGameObjectsPlayer(player);
            AddNGOPlayer(ngoPlayer);
        }

        public void RemovePlayer(Player player)
        {
            var ngoPlayer = new NetcodeGameObjectsPlayer(player);
            RemoveNGOPlayer(ngoPlayer);
        }

        public Player? GetPlayerWithPlayerId(ulong playerId)
        {
            foreach (NetcodeGameObjectsPlayer ngoPlayer in _ngoPlayerList)
            {
                if (ngoPlayer.Player.playerId == playerId)
                {
                    return ngoPlayer.Player;
                }
            }

            return null;
        }

        public Player? GetPlayerWithOculusId(ulong oculusId)
        {
            foreach (var ngoPlayer in _ngoPlayerList)
            {
                if (ngoPlayer.Player.oculusId == oculusId)
                {
                    return ngoPlayer.Player;
                }
            }

            return null;
        }

        public List<Player> GetAllPlayers()
        {
            var allPlayers = new List<Player>();
            foreach (var ngoPlayer in _ngoPlayerList) allPlayers.Add(ngoPlayer.Player);

            return allPlayers;
        }

        public void AddAnchor(Anchor anchor)
        {
            AddNGOAnchor(new NetcodeGameObjectsAnchor(anchor));
        }

        public void RemoveAnchor(Anchor anchor)
        {
            RemoveNGOAnchor(new NetcodeGameObjectsAnchor(anchor));
        }

        public Anchor? GetAnchor(ulong ownerOculusId)
        {
            foreach (var ngoAnchor in _ngoAnchorList)
                if (ngoAnchor.Anchor.ownerOculusId == ownerOculusId)
                {
                    return ngoAnchor.Anchor;
                }

            return null;
        }

        public List<Anchor> GetAllAnchors()
        {
            var anchors = new List<Anchor>();
            foreach (var ngoAnchor in _ngoAnchorList) anchors.Add(ngoAnchor.Anchor);

            return anchors;
        }

        public uint GetColocationGroupCount()
        {
            return colocationGroupCount.Value;
        }

        public void IncrementColocationGroupCount()
        {
            if (NetworkManager.Singleton.IsHost)
            {
                colocationGroupCount.Value = colocationGroupCount.Value + 1;
            }
            else
            {
                IncrementColocationGroupCountServerRpc();
            }
        }

        private void AddNGOPlayer(NetcodeGameObjectsPlayer ngoPlayer)
        {
            if (NetworkManager.Singleton.IsHost)
            {
                _ngoPlayerList.Add(ngoPlayer);
            }
            else
            {
                AddNGOPlayerServerRpc(ngoPlayer);
            }
        }

        private void RemoveNGOPlayer(NetcodeGameObjectsPlayer ngoPlayer)
        {
            if (NetworkManager.Singleton.IsHost)
            {
                _ngoPlayerList.Remove(ngoPlayer);
            }
            else
            {
                RemoveNGOPlayerServerRpc(ngoPlayer);
            }
        }

        private void AddNGOAnchor(NetcodeGameObjectsAnchor ngoAnchor)
        {
            if (NetworkManager.Singleton.IsHost)
            {
                _ngoAnchorList.Add(ngoAnchor);
            }
            else
            {
                AddNGOAnchorServerRpc(ngoAnchor);
            }
        }

        private void RemoveNGOAnchor(NetcodeGameObjectsAnchor ngoAnchor)
        {
            if (NetworkManager.Singleton.IsHost)
            {
                _ngoAnchorList.Remove(ngoAnchor);
            }
            else
            {
                RemoveNGOAnchorServerRpc(ngoAnchor);
            }
        }

        #region ServerRPCs

        [ServerRpc(RequireOwnership = false)]
        private void AddNGOPlayerServerRpc(NetcodeGameObjectsPlayer ngoPlayer)
        {
            AddNGOPlayer(ngoPlayer);
        }

        [ServerRpc(RequireOwnership = false)]
        private void RemoveNGOPlayerServerRpc(NetcodeGameObjectsPlayer ngoPlayer)
        {
            RemoveNGOPlayer(ngoPlayer);
        }

        [ServerRpc(RequireOwnership = false)]
        private void AddNGOAnchorServerRpc(NetcodeGameObjectsAnchor ngoAnchor)
        {
            AddNGOAnchor(ngoAnchor);
        }

        [ServerRpc(RequireOwnership = false)]
        private void RemoveNGOAnchorServerRpc(NetcodeGameObjectsAnchor ngoAnchor)
        {
            RemoveNGOAnchor(ngoAnchor);
        }

        [ServerRpc(RequireOwnership = false)]
        private void IncrementColocationGroupCountServerRpc()
        {
            IncrementColocationGroupCount();
        }

        #endregion
    }
}
