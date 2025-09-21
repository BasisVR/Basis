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

using Unity.Netcode;
using UnityEngine;
using Meta.XR.MultiplayerBlocks.Shared;
using Random = UnityEngine.Random;

#if META_AVATAR_SDK_DEFINED
using Oculus.Avatar2;
#endif // META_AVATAR_SDK_DEFINED

namespace Meta.XR.MultiplayerBlocks.NGO
{
    /// <summary>
    /// The class responsible for spawning an Avatar when using Unity Netcode for Gameobjects.
    /// The spawning may happen on <c>Awake()</c> if <see cref="loadAvatarWhenConnected"/> is set to <c>true</c> or at any
    /// time chosen by the developer by calling <see cref="SpawnAvatar"/>.
    /// For more information on the Meta Avatars SDK, see https://developer.oculus.com/documentation/unity/meta-avatars-overview/.
    /// </summary>
    public class AvatarSpawnerNGO : NetworkBehaviour
    {
#pragma warning disable CS0414 // If Avatar SDK not installed these fields are not used, disable warning but retain serialization
        [Tooltip("Control when you want to load the avatar.")]
        [SerializeField]
        private bool loadAvatarWhenConnected = true;

        [SerializeField] internal GameObject avatarPrefab;
        [SerializeField] internal GameObject avatarPrefabSdk28Plus;

        [Tooltip("Specify the number of preset avatars available in the project. The maximum size depends on the SDK version.")]
        // Developers might want to delete some avatars from the sample asset zip
        // e.g. the game has a maximum player count, they won't need more unique sample avatars
        [SerializeField] private int preloadedSampleAvatarSize = 6;

        [Tooltip("Adjust the level of detail used when streaming the avatars.")]
        [SerializeField]
        private AvatarStreamLOD avatarStreamLOD = AvatarStreamLOD.Medium;

        [Tooltip("Adjust the update interval used when streaming the avatars.")]
        [SerializeField]
        private float avatarUpdateIntervalInSec = 0.08f;
#pragma warning restore CS0414

#if META_AVATAR_SDK_DEFINED
        private PlatformInfo? _platformInfo;

        private void OnEnable()
        {
            AvatarEntity.OnSpawned += HandleAvatarSpawned;
        }

        private void OnDisable()
        {
            AvatarEntity.OnSpawned -= HandleAvatarSpawned;
        }

        private void HandleAvatarSpawned(IAvatarStreamConfig streamConfig)
        {
            streamConfig.SetAvatarStreamLOD(avatarStreamLOD);
            streamConfig.SetAvatarUpdateIntervalInS(avatarUpdateIntervalInSec);
        }

        private void Awake()
        {
#if META_PLATFORM_SDK_DEFINED
            PlatformInit.GetEntitlementInformation(OnEntitlementFinished);
#endif // META_PLATFORM_SDK_DEFINED
        }

        public override void OnNetworkSpawn()
        {
#if META_PLATFORM_SDK_DEFINED
            if (_platformInfo.HasValue && loadAvatarWhenConnected)
            {
                SpawnAvatar();
            }
#else
            if (loadAvatarWhenConnected) {
                Debug.LogWarning("Meta Platform SDK not installed, using test avatar instead");
                SpawnAvatar();
            }
#endif // META_PLATFORM_SDK_DEFINED
        }

#if META_PLATFORM_SDK_DEFINED
        private void OnEntitlementFinished(PlatformInfo info)
        {
            _platformInfo = info;
            Debug.Log(
                $"Entitlement callback: isEntitled: {info.IsEntitled} Name: {info.OculusUser?.OculusID} UserID: {info.OculusUser?.ID}");

            if (info.IsEntitled)
            {
                OvrAvatarEntitlement.SetAccessToken(info.Token);
            }

            if (IsSpawned && loadAvatarWhenConnected)
            {
                SpawnAvatar();
            }
        }
#endif // META_PLATFORM_SDK_DEFINED

        // ReSharper disable once MemberCanBePrivate.Global
        /// <summary>
        /// Spawns the Avatar.
        /// </summary>
        /// <remarks>This is called automatically on <c>Awake()</c> if <see cref="loadAvatarWhenConnected"/> is set to <c>true</c>.</remarks>
        public void SpawnAvatar()
        {
            ulong oculusId = 0;
            if (_platformInfo is { IsEntitled: true, OculusUser: not null })
            {
                oculusId = _platformInfo.Value.OculusUser!.ID;
            }

            SpawnAvatarServerRpc(oculusId);
        }

        [ServerRpc(RequireOwnership = false)]
        private void SpawnAvatarServerRpc(ulong oculusId, ServerRpcParams serverRpcParams = default)
        {
#if META_AVATAR_SDK_28_OR_NEWER
            var go = Instantiate(avatarPrefabSdk28Plus);
#else
            var go = Instantiate(avatarPrefab);
#endif

            go.GetComponent<NetworkObject>().SpawnWithOwnership(serverRpcParams.Receive.SenderClientId);
            go.GetComponent<AvatarBehaviourNGO>().LocalAvatarIndex = Random.Range(0, preloadedSampleAvatarSize);
            go.GetComponent<AvatarBehaviourNGO>().OculusId = oculusId;
        }
#endif // META_AVATAR_SDK_DEFINED
    }
}
