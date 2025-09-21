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

using UnityEngine;
using Unity.Netcode.Components;

namespace Meta.XR.MultiplayerBlocks.NGO
{
    /// <summary>
    /// The class for networking the transform of a game object in a client-authoritative fashion when using the
    /// Unity Netcode for Gameobjects networking framework.
    /// For more information, see the <see cref="NetworkTransform"/> documentation page https://docs-multiplayer.unity3d.com/netcode/current/components/networktransform/.
    /// </summary>
    /// <remarks>This class puts the trust in the client to determine the transform value which may be undesirable
    /// if you're developing a game that is security/cheating sensitive.</remarks>
    [DisallowMultipleComponent]
    public class ClientNetworkTransform : NetworkTransform
    {
        protected override bool OnIsServerAuthoritative()
        {
            return false;
        }
    }
}
