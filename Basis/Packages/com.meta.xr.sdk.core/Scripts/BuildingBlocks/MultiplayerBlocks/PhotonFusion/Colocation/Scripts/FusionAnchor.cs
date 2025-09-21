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
using Fusion;

namespace Meta.XR.MultiplayerBlocks.Colocation.Fusion
{
    /// <summary>
    ///     A Photon Fusion Wrapper for the Anchor class
    ///     Used to be able to serialize and send the Anchor data over the network
    /// </summary>
    [Serializable]
    internal struct FusionAnchor : INetworkStruct, IEquatable<FusionAnchor>
    {
        public NetworkBool isAutomaticAnchor;
        public NetworkBool isAlignmentAnchor;
        public ulong ownerOculusId;
        public uint colocationGroupId;
        public NetworkString<_64> automaticAnchorUuid;

        public FusionAnchor(Anchor anchor)
        {
            isAutomaticAnchor = anchor.isAutomaticAnchor;
            isAlignmentAnchor = anchor.isAlignmentAnchor;
            ownerOculusId = anchor.ownerOculusId;
            colocationGroupId = anchor.colocationGroupId;
            automaticAnchorUuid = anchor.automaticAnchorUuid.ToString();
        }

        public Anchor GetAnchor()
        {
            if (!Guid.TryParse(automaticAnchorUuid.ToString(), out var uuid))
            {
                Logger.Log("Failed to parse Anchor UUID string", LogLevel.Error);
            }
            return new Anchor(isAutomaticAnchor, isAlignmentAnchor, ownerOculusId, colocationGroupId, uuid);
        }

        public bool Equals(FusionAnchor other)
        {
            return GetAnchor().Equals(other.GetAnchor());
        }
    }
}
