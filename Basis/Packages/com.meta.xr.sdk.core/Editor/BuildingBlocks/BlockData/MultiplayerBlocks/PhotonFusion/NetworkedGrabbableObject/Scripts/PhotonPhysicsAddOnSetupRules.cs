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

#if FUSION_WEAVER && FUSION2 && PHOTON_FUSION_PHYSICS_ADDON_DEFINED
using System.Linq;
using Fusion;
using Fusion.Addons.Physics;
using Meta.XR.BuildingBlocks;
using Meta.XR.BuildingBlocks.Editor;
using UnityEditor;

namespace Meta.XR.MultiplayerBlocks.Fusion.Editor
{
    [InitializeOnLoad]
    internal static class PhotonPhysicsAddOnSetupRules
    {
        static PhotonPhysicsAddOnSetupRules()
        {
            OVRProjectSetup.AddTask(
                level: OVRProjectSetup.TaskLevel.Required,
                group: OVRProjectSetup.TaskGroup.Features,
                isDone: _ =>
                {
                    var grabbableBlocks = Utils.GetBlocksInScene().Where(b =>
                        b.BlockId == BlockDataIds.INetworkedGrabbableObject &&
                        b.InstallationRoutineCheckpoint.InstallationRoutineId ==
                        BlockDataIds.NetworkedGrabbableObjectFusionInstallationRoutine);
                    if (!grabbableBlocks.Any() || grabbableBlocks.All(block => block.GetComponent<NetworkRigidbody3D>() == null)) return true;
                    // Has grabbable block and used NetworkRigidbody3D
                    var networkRunnerBlock = FusionNetworkRunnerBlock;
                    return networkRunnerBlock == null ||
                           networkRunnerBlock.GetComponent<RunnerSimulatePhysics3D>() != null;
                },
                message:
                "When using Fusion Networked Grabbable Object block with PhysicsAddOn it's required for RunnerSimulatePhysics component in NetworkRunner",
                fix: _ =>
                {
                    var networkRunnerBlock = FusionNetworkRunnerBlock;
                    networkRunnerBlock.gameObject.AddComponent<RunnerSimulatePhysics3D>();
                },
                fixMessage: $"Add {nameof(RunnerSimulatePhysics3D)} component to {nameof(NetworkRunner)}"
            );
        }

        private static BuildingBlock FusionNetworkRunnerBlock => Utils.GetBlocksInScene().FirstOrDefault(b =>
            b.BlockId == BlockDataIds.INetworkManager &&
            b.InstallationRoutineCheckpoint.InstallationRoutineId == BlockDataIds.NetworkManagerFusionInstallationRoutine);
    }
}
#endif // FUSION_WEAVER && FUSION2 && PHOTON_FUSION_PHYSICS_ADDON_DEFINED
