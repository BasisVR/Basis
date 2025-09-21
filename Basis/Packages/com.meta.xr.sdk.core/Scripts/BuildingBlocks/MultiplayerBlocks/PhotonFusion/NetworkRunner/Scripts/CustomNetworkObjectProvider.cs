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
using System.Collections.Generic;
using Fusion;
using UnityEngine;

namespace Meta.XR.MultiplayerBlocks.Fusion
{
    using CustomNetworkObjectSpawnFunc = Func<GameObject>;
    /// <summary>
    /// A custom provider to allow composing NetworkObjects programmatically (instead of placing in-Scene or spawning from Prefabs).
    /// This object should be attached onto a NetworkRunner object. This is a helper component when using the Photon Fusion networking framework.
    /// </summary>
    public class CustomNetworkObjectProvider : NetworkObjectProviderDefault
    {
        private static NetworkObjectBaker _baker;
        private static NetworkObjectBaker Baker => _baker ??= new NetworkObjectBaker();

        private static readonly Dictionary<uint, CustomNetworkObjectSpawnFunc> CustomSpawnDict = new();
        /// <summary>
        /// Register your custom network object spawning function before the object is spawned.
        /// e.g. register in InitializeOnLoad function
        /// </summary>
        /// <param name="customPrefabID"> Used to identify the custom object. Should be unique and larger than 100000 </param>
        /// <param name="func"> Function that compose a GameObject (NetworkObject) programmatically </param>
        public static void RegisterCustomNetworkObject(uint customPrefabID, CustomNetworkObjectSpawnFunc func)
        {
            if (CustomSpawnDict.ContainsKey(customPrefabID))
            {
                Debug.LogError($"The requested customPrefabID {customPrefabID} already existed, aborting registration");
            }
            CustomSpawnDict[customPrefabID] = func;
        }

        public override NetworkObjectAcquireResult AcquirePrefabInstance(NetworkRunner runner, in NetworkPrefabAcquireContext context, out NetworkObject result)
        {
            if (CustomSpawnDict.TryGetValue(context.PrefabId.RawValue, out var spawnFunc))
            {
                var spawnedGameObject = spawnFunc();
                var networkObject = spawnedGameObject.GetComponent<NetworkObject>();
                if (networkObject == null)
                {
                    networkObject = spawnedGameObject.AddComponent<NetworkObject>();
                }

                Baker.Bake(spawnedGameObject);

                if (context.DontDestroyOnLoad)
                {
                    runner.MakeDontDestroyOnLoad(spawnedGameObject);
                }
                else
                {
                    runner.MoveToRunnerScene(spawnedGameObject);
                }

                result = networkObject;
                return NetworkObjectAcquireResult.Success;
            }

            return base.AcquirePrefabInstance(runner, context, out result);
        }
    }
}
