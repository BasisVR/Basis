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

namespace Meta.XR.MultiplayerBlocks.Colocation
{
    internal static class NetworkDataUtils
    {
        public static ulong? GetOculusIdOfColocatedGroupOwnerFromColocationGroupId(uint colocationGroupId)
        {
            INetworkData data = NetworkAdapter.NetworkData;
            List<Anchor> anchors = data.GetAllAnchors();
            foreach (Anchor anchor in anchors)
            {
                if (colocationGroupId == anchor.colocationGroupId)
                {
                    return anchor.ownerOculusId;
                }
            }

            return null;
        }

        public static List<Player> GetAllPlayersFromColocationGroupId(uint colocationGroupId)
        {
            INetworkData data = NetworkAdapter.NetworkData;
            List<Player> players = data.GetAllPlayers();
            List<Player> colocatedPlayers = new List<Player>();
            foreach (Player player in players)
            {
                if (colocationGroupId == player.colocationGroupId)
                {
                    colocatedPlayers.Add(player);
                }
            }

            return colocatedPlayers;
        }

        public static List<Player> GetAllPlayersColocatedWith(ulong oculusId, bool includeMyself)
        {
            INetworkData data = NetworkAdapter.NetworkData;
            List<Player> players = data.GetAllPlayers();
            Player currentPlayer = data.GetPlayerWithOculusId(oculusId).Value;
            uint colocatedGroupId = currentPlayer.colocationGroupId;
            List<Player> playersColocatedWithMe = new List<Player>();

            foreach (Player player in players)
            {
                if (player.colocationGroupId == colocatedGroupId)
                {
                    playersColocatedWithMe.Add(player);
                    if (!includeMyself && player.oculusId == oculusId)
                    {
                        playersColocatedWithMe.RemoveAt(playersColocatedWithMe.Count - 1);
                    }
                }
            }

            return playersColocatedWithMe;
        }

        public static Player? GetPlayerFromOculusId(ulong oculusId)
        {
            INetworkData data = NetworkAdapter.NetworkData;
            return data.GetPlayerWithOculusId(oculusId);
        }
    }
}
