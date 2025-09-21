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

#if UNITY_NGO_MODULE_DEFINED

using System.Linq;
using Meta.XR.BuildingBlocks;
using Meta.XR.BuildingBlocks.Editor;
using Unity.Netcode;
using UnityEditor;
using UnityEngine;

namespace Meta.XR.MultiplayerBlocks.NGO.Editor
{
    internal static class BuildingBlockNGOUtils
    {
        public static BuildingBlock GetNetworkManagerBB() =>
            Utils.GetBlocksInScene()
                .FirstOrDefault(b =>
                    b.BlockId == BlockDataIds.INetworkManager
                    && b.InstallationRoutineCheckpoint.InstallationRoutineId == BlockDataIds.NetworkManagerNGOInstallationRoutine
                );

        public static bool IsPrefabContainedInNetworkManagerBB(GameObject prefab)
        {
            var networkManagerBB = GetNetworkManagerBB();
            return networkManagerBB == null || IsPrefabInTheNetworkPrefabsList(networkManagerBB.GetComponent<NetworkManager>(), prefab);
        }

        private static bool IsPrefabInTheNetworkPrefabsList(NetworkManager networkManager, GameObject prefab)
        {
            if (prefab == null)
            {
                return true;
            }

            return networkManager
                .NetworkConfig.Prefabs.NetworkPrefabsLists
                .Where(list => list != null)
                .Any(list => list.Contains(prefab));
        }

        public static void AddPrefabsToNetworkManagerBB(params GameObject[] prefabs)
        {
            var networkManagerBB = GetNetworkManagerBB();

            if (networkManagerBB == null)
            {
                return;
            }

            var networkManager = networkManagerBB.GetComponent<NetworkManager>();
            var list = networkManager
                .NetworkConfig.Prefabs.NetworkPrefabsLists
                .FirstOrDefault(list => list != null);

            if (list == null)
            {
                return;
            }

            var modified = false;

            foreach (var prefab in prefabs)
            {
                if (IsPrefabInTheNetworkPrefabsList(networkManager, prefab))
                {
                    continue;
                }

                modified = true;
                list.Add(new NetworkPrefab { Prefab = prefab});
            }

            if (modified)
            {
                EditorUtility.SetDirty(list);
            }
        }
    }
}

#endif // UNITY_NGO_MODULE_DEFINED
