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

namespace Meta.XR.MultiplayerBlocks.Shared
{
    /// <summary>
    /// Interface for serializing the Avatar state.
    /// </summary>
    /// <remarks>Currently there are implementations for Photon Fusion and Unity Netcode for Gameobjects
    /// for networking the Avatar state, but more may be added using this interface.</remarks>
    public interface IAvatarBehaviour
    {
        /// <summary>
        /// Represents the id of the Oculus account logged in the current headset. Defaults to 0 if it could not be fetched.
        /// </summary>
        public ulong OculusId { get; }

        /// <summary>
        /// Index of the Avatar type used when the user defined one could not be loaded.
        /// </summary>
        /// <remarks>Usually this is chosen randomly between 0 and the number of Sample Assets available for Avatars.</remarks>
        public int LocalAvatarIndex { get; }

        /// <summary>
        /// Boolean indicating whether the user has input authority over this Avatar.
        /// </summary>
        public bool HasInputAuthority { get; }

        /// <summary>
        /// Method to load a serialized avatar state and update it in the local client.
        /// </summary>
        /// <param name="bytes">Byte stream containing the serialized avatar state.</param>
        public void ReceiveStreamData(byte[] bytes);
    }
}
