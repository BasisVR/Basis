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

namespace Meta.XR.MultiplayerBlocks.NGO.Editor
{
    [InitializeOnLoad]
    internal static class PlayerNameTagNGOSetupRules
    {
        static PlayerNameTagNGOSetupRules()
        {
            OVRProjectSetup.AddTask(
                level: OVRProjectSetup.TaskLevel.Required,
                group: OVRProjectSetup.TaskGroup.Features,
                isDone: _ =>
                {
                    var playerNameTagBB = GetPlayerNameTagBB();
                    return playerNameTagBB == null
                           || BuildingBlockNGOUtils.IsPrefabContainedInNetworkManagerBB(playerNameTagBB.GetComponent<PlayerNameTagSpawnerNGO>().playerNameTagPrefab);
                },
                fix: _ =>
                {
                    BuildingBlockNGOUtils.AddPrefabsToNetworkManagerBB(
                        GetPlayerNameTagBB().GetComponent<PlayerNameTagSpawnerNGO>().playerNameTagPrefab);
                },
                fixMessage: "Add the Player Name Tag prefab to the network prefabs list",
                message:
                "When using the Player Name Tag Building Block you must add its prefab to the network prefabs list"
            );
        }

        private static BuildingBlock GetPlayerNameTagBB() =>
            Utils.GetInterfaceBlocksInScene(BlockDataIds.IPlayerNameTag,
                    BlockDataIds.PlayerNameTagNGOInstallationRoutine)
                .FirstOrDefault();
    }
}

#endif // UNITY_NGO_MODULE_DEFINED
