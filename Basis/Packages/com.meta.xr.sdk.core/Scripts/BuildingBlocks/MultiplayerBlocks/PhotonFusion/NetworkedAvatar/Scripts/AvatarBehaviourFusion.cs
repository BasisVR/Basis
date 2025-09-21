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
using UnityEngine;
using Fusion;
using Meta.XR.MultiplayerBlocks.Shared;

namespace Meta.XR.MultiplayerBlocks.Fusion
{
    /// <summary>
    /// An implementation of the <see cref="IAvatarBehaviour"/> interface to network Avatar states using Photon Fusion.
    /// This class gets or creates an <see cref="AvatarEntity"/> in the same game object and will handle the networking for it.
    /// For more information on the Meta Avatars SDK, see https://developer.oculus.com/documentation/unity/meta-avatars-overview/.
    /// </summary>
    public class AvatarBehaviourFusion : NetworkBehaviour, IAvatarBehaviour
    {
        private const float LERP_TIME = 0.5f;
        private const int AvatarDataStreamMaxCapacity = 900; // Enough storage for an avatar in high LOD
        private Transform _cameraRig;
        private byte[] _buffer;

        [Networked, OnChangedRender(nameof(OnAvatarIdChanged))] public ulong OculusId { get; set; }
        [Networked, OnChangedRender(nameof(OnAvatarIdChanged))] public int LocalAvatarIndex { get; set; }
        [Networked] private int AvatarDataStreamLength { get; set; }

        [Networked]
        [Capacity(AvatarDataStreamMaxCapacity)]
        [OnChangedRender(nameof(OnAvatarDataStreamChanged))]
        private NetworkArray<byte> AvatarDataStream => default;

#if META_AVATAR_SDK_DEFINED
        private AvatarEntity _avatar;
#endif // META_AVATAR_SDK_DEFINED

        /// <summary>
        /// Called by the Photon Fusion networking framework when the network object is spawned.
        /// This method does the required setup for the Avatar state synchronization.
        /// </summary>
        public override void Spawned()
        {
            if (OVRManager.instance)
            {
                _cameraRig = OVRManager.instance.GetComponentInChildren<OVRCameraRig>().transform;
            }
#if META_AVATAR_SDK_DEFINED
            _avatar = GetComponent<AvatarEntity>();
            if (_avatar == null)
            {
                _avatar = gameObject.AddComponent<AvatarEntity>();
            }
#endif // META_AVATAR_SDK_DEFINED
        }

        private void OnAvatarIdChanged()
        {
#if META_AVATAR_SDK_DEFINED
            if (_avatar != null)
            {
                _avatar.ReloadAvatarManually();
            }
#endif // META_AVATAR_SDK_DEFINED
        }

        private void OnAvatarDataStreamChanged()
        {
#if META_AVATAR_SDK_DEFINED
            if (Object.HasStateAuthority)
            {
                return;
            }

            if (_buffer == null || _buffer.Length != AvatarDataStreamLength)
            {
                _buffer = new byte[AvatarDataStreamLength];
            }

            AvatarDataStream.CopyTo(_buffer);
            _avatar.SetStreamData(_buffer);
#endif // META_AVATAR_SDK_DEFINED
        }

        /// <summary>
        /// Called by the Photon Fusion networking framework on every network update tick.
        /// This method syncs the Avatar position to the player's Camera Rig.
        /// </summary>
        public override void FixedUpdateNetwork()
        {
            if (!Object.HasStateAuthority)
            {
                return;
            }

            if (_cameraRig == null)
            {
                return;
            }

            var t = transform;
            transform.position = Vector3.Lerp(t.position, _cameraRig.position, LERP_TIME);
            transform.rotation = Quaternion.Lerp(t.rotation, _cameraRig.rotation, LERP_TIME);
        }

        #region IAvatarBehaviour

        /// <summary>
        /// Loads a serialized avatar state and update it in the local client.
        /// An implementation of the <see cref="IAvatarBehaviour"/> interface.
        /// </summary>
        /// <param name="bytes">A byte stream containing the serialized avatar state.</param>
        public void ReceiveStreamData(byte[] bytes)
        {
            if (bytes.Length > AvatarDataStreamMaxCapacity)
            {
                Debug.LogError($"[{nameof(AvatarBehaviourFusion)}] Cannot send a stream data of length {bytes.Length} greater than the max capacity of {AvatarDataStreamMaxCapacity}");
                return;
            }

            AvatarDataStreamLength = bytes.Length;
            AvatarDataStream.CopyFrom(bytes, 0, bytes.Length);
        }

        #endregion
    }
}
