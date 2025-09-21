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

#if META_AVATAR_SDK_DEFINED
using Fusion;
using Oculus.Avatar2;
#endif // META_AVATAR_SDK_DEFINED

using Meta.XR.MultiplayerBlocks.Shared;
using System.Collections;

namespace Meta.XR.MultiplayerBlocks.Fusion
{
    /// <summary>
    /// The class responsible for spawning an Avatar when using Photon Fusion.
    /// The spawning may happen on <c>Awake()</c> if <see cref="loadAvatarWhenConnected"/> is set to <c>true</c> or at any
    /// time chosen by the developer by calling <see cref="SpawnAvatar"/>.
    /// For more information on the Meta Avatars, see https://developer.oculus.com/documentation/unity/meta-avatars-overview/.
    /// </summary>
    public class AvatarSpawnerFusion : MonoBehaviour
    {
#pragma warning disable CS0414 // If Avatar SDK not installed these fields are not used, disable warning but retain serialization
        [Tooltip("Control when you want to load the avatar.")]
        [SerializeField]
        private bool loadAvatarWhenConnected = true;
        [SerializeField] private GameObject avatarBehavior;
        [SerializeField] private GameObject avatarBehaviorSdk28Plus;
        [Tooltip("Specify the number of preset avatars available in the project. The maximum size depends on the SDK version.")]
        // Developers might want to delete some avatars from the sample asset zip
        // e.g. the game has a maximum player count, they won't need more unique sample avatars
        [SerializeField] private int preloadedSampleAvatarSize = 6;

        [Tooltip("Adjust the level of detail used when streaming the avatars.")]
        [SerializeField] private AvatarStreamLOD avatarStreamLOD = AvatarStreamLOD.Medium;

        [Tooltip("Adjust the update interval used when streaming the avatars.")]
        [SerializeField] private float avatarUpdateIntervalInSec = 0.08f;
#pragma warning restore CS0414

#if META_AVATAR_SDK_DEFINED
        private NetworkRunner _networkRunner;
        private bool _sceneLoaded;
        private bool _entitlementCompleted;
        private PlatformInfo _platformInfo;

        private void HandleAvatarSpawned(IAvatarStreamConfig streamConfig)
        {
            streamConfig.SetAvatarStreamLOD(avatarStreamLOD);
            streamConfig.SetAvatarUpdateIntervalInS(avatarUpdateIntervalInSec);
        }

        private void Awake()
        {
#if META_PLATFORM_SDK_DEFINED
            PlatformInit.GetEntitlementInformation(OnEntitlementFinished);
#else
            if (loadAvatarWhenConnected)
            {
                Debug.LogWarning("Meta Platform SDK not installed, using test avatar instead");
                SpawnAvatar();
            }
#endif // META_PLATFORM_SDK_DEFINED
        }

        private void OnEnable()
        {
            FusionBBEvents.OnSceneLoadDone += OnLoaded;
            AvatarEntity.OnSpawned += HandleAvatarSpawned;
        }

        private void OnDisable()
        {
            FusionBBEvents.OnSceneLoadDone -= OnLoaded;
            AvatarEntity.OnSpawned -= HandleAvatarSpawned;
        }

        private void OnLoaded(NetworkRunner networkRunner)
        {
            _sceneLoaded = true;
            _networkRunner = networkRunner;
        }

        // ReSharper disable once MemberCanBePrivate.Global
        /// <summary>
        /// Spawns the Avatar.
        /// </summary>
        /// <remarks>This is called automatically on <c>Awake()</c> if <see cref="loadAvatarWhenConnected"/> is set to <c>true</c>.</remarks>
        public void SpawnAvatar()
        {
            StartCoroutine(SpawnAvatarRoutine());
        }

#if META_PLATFORM_SDK_DEFINED
        private void OnEntitlementFinished(PlatformInfo info)
        {
            _platformInfo = info;
            Debug.Log(
                $"Entitlement callback:isEntitled: {info.IsEntitled} oculusName: {info.OculusUser?.OculusID} oculusID: {info.OculusUser?.ID}");

            if (info.IsEntitled)
            {
                OvrAvatarEntitlement.SetAccessToken(info.Token);
            }

            _entitlementCompleted = true;

            if (loadAvatarWhenConnected)
            {
                SpawnAvatar();
            }
        }
#endif // META_PLATFORM_SDK_DEFINED

        private IEnumerator SpawnAvatarRoutine()
        {
            while (_networkRunner == null || !_sceneLoaded || !_entitlementCompleted)
            {
                yield return null;
            }

            // Spawn Avatar
            _networkRunner.Spawn(
#if META_AVATAR_SDK_28_OR_NEWER
                avatarBehaviorSdk28Plus,
#else
                avatarBehavior,
#endif
                Vector3.zero,
                Quaternion.identity,
                _networkRunner.LocalPlayer,
                (_, obj) => // onBeforeSpawned
                {
                    var avatarBehaviourFusion = obj.GetComponent<AvatarBehaviourFusion>();
                    avatarBehaviourFusion.LocalAvatarIndex = Random.Range(0, preloadedSampleAvatarSize);
                    if (_platformInfo.IsEntitled)
                    {
                        avatarBehaviourFusion.OculusId = _platformInfo.OculusUser?.ID ?? 0;
                    }
                }
            );
        }
#endif // META_AVATAR_SDK_DEFINED
    }
}
