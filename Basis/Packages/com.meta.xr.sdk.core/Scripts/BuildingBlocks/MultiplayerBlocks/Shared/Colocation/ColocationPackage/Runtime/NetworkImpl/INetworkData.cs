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
using Unity.Collections;

namespace Meta.XR.MultiplayerBlocks.Colocation
{
    /// <summary>
    ///     An interface that is used to keep track of all players and anchors and can be accessed by any player
    /// </summary>
    internal interface INetworkData
    {
        public void AddPlayer(Player player);
        public void RemovePlayer(Player player);
        public Player? GetPlayerWithPlayerId(ulong playerId);
        public Player? GetPlayerWithOculusId(ulong oculusId);
        public List<Player> GetAllPlayers();

        public void AddAnchor(Anchor anchor);
        public void RemoveAnchor(Anchor anchor);

        public Anchor? GetAnchor(ulong ownerOculusId);
        public List<Anchor> GetAllAnchors();

        public uint GetColocationGroupCount();

        public void IncrementColocationGroupCount();
    }
}
