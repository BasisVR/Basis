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

using System.Collections;
using Meta.XR.BuildingBlocks;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Meta.XR.MultiplayerBlocks.Shared
{
    /// <summary>
    /// The interface used for spawning name tags for connected players over different networking frameworks.
    /// </summary>
    /// <remarks>Currently there are implementations for Photon Fusion (<see cref="PlayerNameTagSpawnerFusion"/>) and Unity Netcode for Gameobjects (<see cref="PlayerNameTagSpawnerNGO"/>)
    /// for spawning name tags, but more may be added using this interface.</remarks>
    public interface INameTagSpawner
    {
        /// <summary>
        /// Indicates whether this player has fully connected to the game/app room.
        /// You can use this to determine when to spawn the name tag.
        /// </summary>
        public bool IsConnected { get; }

        /// <summary>
        /// Spawns the name tag with the given username for this player.
        /// </summary>
        /// <param name="playerName">The selected username for this player.</param>
        public void Spawn(string playerName);
    }


    /// <summary>
    /// The class responsible for the logic of spawning player name tags, (excluding the networking part which is handled via the
    /// <see cref="INameTagSpawner"/> interface).
    /// The player name used will be one of the following:
    /// <list type="bullet">
    /// <item>
    /// <description>If it's possible to fetch the player's oculus account we use its username.</description>
    /// </item>
    /// <item>
    /// <description>If it's not possible we create a randomized name based on the available <see cref="namePrefix"/> and <see cref="namePostfix"/> serialized lists.</description>
    /// </item>
    /// </list>
    /// </summary>
    /// <remarks><see cref="namePrefix"/> and <see cref="namePostfix"/> come with a small set of initial values for demonstration purposes
    /// which can be changed to best fit your project's needs.</remarks>
    public class PlayerNameTagSpawner : MonoBehaviour
    {
        [Header("Randomized name for non-entitled folks eg. 'HappyHippo'", order = 1)]

        [SerializeField]
        private string[] namePrefix = { "Happy", "Running", "Laughing", "Smiling" };

        [SerializeField]
        private string[] namePostfix = { "Cat", "Dog", "Hippo", "Bird" };

        private INameTagSpawner _nameTagSpawner;

        private void Start()
        {
            _nameTagSpawner = this.GetInterfaceComponent<INameTagSpawner>();

#if META_PLATFORM_SDK_DEFINED
            PlatformInit.GetEntitlementInformation(OnEntitlementFinished);
#else
            Debug.LogWarning("Meta Platform SDK not installed, cannot retrieve user name, use randomized names instead");
            StartCoroutine(SpawnCoroutine(GetRandomName()));
#endif // META_PLATFORM_SDK_DEFINED
        }

        private IEnumerator SpawnCoroutine(string playerName)
        {
            if (_nameTagSpawner == null)
            {
                yield break;
            }

            while (!_nameTagSpawner.IsConnected)
            {
                yield return null;
            }

            _nameTagSpawner.Spawn(playerName);
        }

#if META_PLATFORM_SDK_DEFINED
        private void OnEntitlementFinished(PlatformInfo info)
        {
            Debug.Log($"Entitlement callback: isEntitled: {info.IsEntitled} Name: {info.OculusUser?.OculusID} UserID: {info.OculusUser?.ID}");
            var playerName = info.IsEntitled ? info.OculusUser?.OculusID : GetRandomName();
            StartCoroutine(SpawnCoroutine(playerName));
        }
#endif // META_PLATFORM_SDK_DEFINED

        private string GetRandomName()
        {
            if (namePrefix.Length <= 0 || namePostfix.Length <= 0)
            {
                return null;
            }

            var prefix = namePrefix[Random.Range(0, namePrefix.Length - 1)];
            var postfix = namePostfix[Random.Range(0, namePostfix.Length - 1)];
            return $"{prefix} {postfix}";
        }
    }
}
