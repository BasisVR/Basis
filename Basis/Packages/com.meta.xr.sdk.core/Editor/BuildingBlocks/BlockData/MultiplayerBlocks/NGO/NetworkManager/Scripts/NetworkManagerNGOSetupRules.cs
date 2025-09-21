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
using UnityEditor;
using UnityEngine;
using Unity.Netcode;
using Unity.Netcode.Editor.Configuration;

namespace Meta.XR.MultiplayerBlocks.NGO.Editor
{
    [InitializeOnLoad]
    internal static class NetworkManagerNGOSetupRules
    {
        static NetworkManagerNGOSetupRules()
        {
            OVRProjectSetup.AddTask(
                level: OVRProjectSetup.TaskLevel.Required,
                group: OVRProjectSetup.TaskGroup.Features,
                isDone: _ =>
                {
                    var networkManagerBB = BuildingBlockNGOUtils.GetNetworkManagerBB();

                    if (networkManagerBB == null)
                    {
                        return true;
                    }

                    var networkManager = networkManagerBB.GetComponent<NetworkManager>();

                    if (networkManager == null || networkManager.NetworkConfig == null)
                    {
                        return true;
                    }

                    return networkManager
                        .NetworkConfig.Prefabs.NetworkPrefabsLists
                        .Count(list => list != null) > 0;
                },
                fix: _ =>
                {
                    var networkManagerBB = BuildingBlockNGOUtils.GetNetworkManagerBB();
                    var prefabsList = networkManagerBB.GetComponent<NetworkManager>()
                        .NetworkConfig.Prefabs.NetworkPrefabsLists;

                    while (prefabsList.Count > 0 && prefabsList[0] == null)
                    {
                        prefabsList.RemoveAt(0);
                    }

                    prefabsList.Add(GetOrCreateNetworkPrefabs());
                    EditorUtility.SetDirty(networkManagerBB);
                },
                fixMessage: "Assign a list of Network Prefabs to the Network Manager",
                message: "When using Unity's Network Manager you're required to have a list of Network Prefabs"
            );
        }

        private static NetworkPrefabsList GetOrCreateNetworkPrefabs()
        {
            var path = NetworkPrefabProcessor.DefaultNetworkPrefabsPath;
            var defaultPrefabs = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(path);
            if (defaultPrefabs != null)
            {
                return defaultPrefabs;
            }

            defaultPrefabs = ScriptableObject.CreateInstance<NetworkPrefabsList>();
            AssetDatabase.CreateAsset(defaultPrefabs, path);

            EditorUtility.SetDirty(defaultPrefabs);
            AssetDatabase.SaveAssetIfDirty(defaultPrefabs);
            return defaultPrefabs;
        }
    }
}
#endif // UNITY_NGO_MODULE_DEFINED
