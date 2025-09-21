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

using Meta.XR.MultiplayerBlocks.Shared;
using Unity.Netcode;

namespace Meta.XR.MultiplayerBlocks.NGO
{
    /// <summary>
    /// The class responsible for the networking part of transferring ownership of a networked game object when using
    /// the Unity Netcode for Gameobjects networking framework. It implements the <see cref="ITransferOwnership"/> interface
    /// and is used by <see cref="TransferOwnershipOnSelect"/> which handles the non-networking logic.
    /// </summary>
    public class TransferOwnershipNGO : NetworkBehaviour, ITransferOwnership
    {
        /// <summary>
        /// Transfers the ownership of the networked game object to the local player.
        /// An implementation of the <see cref="ITransferOwnership"/> interface.
        /// </summary>
        public void TransferOwnershipToLocalPlayer()
        {
            TransferOwnershipToLocalPlayerServerRpc();
        }

        /// <summary>
        /// Indicates whether the local player has ownership of the networked game object.
        /// An implementation of the <see cref="ITransferOwnership"/> interface.
        /// </summary>
        /// <returns>'true' if the local player has ownership of the networked game object</returns>
        public bool HasOwnership()
        {
            return IsOwner;
        }

        [ServerRpc(RequireOwnership = false)]
        private void TransferOwnershipToLocalPlayerServerRpc(ServerRpcParams serverRpcParams = default)
        {
            NetworkObject.ChangeOwnership(serverRpcParams.Receive.SenderClientId);
        }
    }
}
