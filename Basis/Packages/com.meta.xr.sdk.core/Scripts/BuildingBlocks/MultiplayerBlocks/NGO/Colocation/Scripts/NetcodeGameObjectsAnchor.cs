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
using Unity.Netcode;
using Meta.XR.MultiplayerBlocks.Colocation;

namespace Meta.XR.MultiplayerBlocks.NGO
{
    /// <summary>
    ///     A Netcode for GameObjects wrapper for the Anchor class
    ///     Used to be able to serialize and send the Anchor data over the network
    /// </summary>
    internal struct NetcodeGameObjectsAnchor : INetworkSerializeByMemcpy, IEquatable<NetcodeGameObjectsAnchor>
    {
        public Anchor Anchor;

        public NetcodeGameObjectsAnchor(Anchor anchor)
        {
            this.Anchor = anchor;
        }

        public bool Equals(NetcodeGameObjectsAnchor other)
        {
            return Anchor.Equals(other.Anchor);
        }
    }
}
