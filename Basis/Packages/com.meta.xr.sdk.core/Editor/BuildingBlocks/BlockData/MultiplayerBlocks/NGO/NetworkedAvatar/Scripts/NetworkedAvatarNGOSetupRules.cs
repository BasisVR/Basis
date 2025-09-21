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

#if UNITY_NGO_MODULE_DEFINED && META_AVATAR_SDK_DEFINED

using System.Linq;
using Meta.XR.BuildingBlocks;
using Meta.XR.BuildingBlocks.Editor;
using UnityEditor;

namespace Meta.XR.MultiplayerBlocks.NGO.Editor
{
    [InitializeOnLoad]
    internal static class NetworkedAvatarNGOSetupRules
    {
        static NetworkedAvatarNGOSetupRules()
        {
            OVRProjectSetup.AddTask(
                level: OVRProjectSetup.TaskLevel.Required,
                group: OVRProjectSetup.TaskGroup.Features,
                isDone: _ =>
                {
                    var networkedAvatarBb = GetNetworkedAvatarBB();

                    if (networkedAvatarBb == null)
                    {
                        return true;
                    }

                    return BuildingBlockNGOUtils.IsPrefabContainedInNetworkManagerBB(networkedAvatarBb.GetComponent<AvatarSpawnerNGO>().avatarPrefab)
                        && BuildingBlockNGOUtils.IsPrefabContainedInNetworkManagerBB(networkedAvatarBb.GetComponent<AvatarSpawnerNGO>().avatarPrefabSdk28Plus);
                },
                fix: _ =>
                {
                    BuildingBlockNGOUtils.AddPrefabsToNetworkManagerBB(
                        GetNetworkedAvatarBB().GetComponent<AvatarSpawnerNGO>().avatarPrefab,
                        GetNetworkedAvatarBB().GetComponent<AvatarSpawnerNGO>().avatarPrefabSdk28Plus);
                },
                fixMessage: "Add the Networked Avatar prefab to the network prefabs list",
                message:
                "When using the Networked Avatar Building Block you must add its prefabs to the network prefabs list"
            );
        }

        private static BuildingBlock GetNetworkedAvatarBB() =>
            Utils.GetInterfaceBlocksInScene(BlockDataIds.INetworkedAvatar,
                    BlockDataIds.NetworkedAvatarNGOInstallationRoutine)
                .FirstOrDefault();
    }
}

#endif // UNITY_NGO_MODULE_DEFINED && META_AVATAR_SDK_DEFINED
