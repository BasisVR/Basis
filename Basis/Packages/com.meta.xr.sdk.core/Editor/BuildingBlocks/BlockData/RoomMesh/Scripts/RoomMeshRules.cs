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

using System.Linq;
using UnityEditor;

namespace Meta.XR.BuildingBlocks.Editor
{
    [InitializeOnLoad]
    internal static class RoomMeshRules
    {
        static RoomMeshRules()
        {
            OVRProjectSetup.AddTask(
                level: OVRProjectSetup.TaskLevel.Required,
                group: OVRProjectSetup.TaskGroup.Features,
                isDone: _ => !RoomMeshExist() ||
                             OVRProjectConfig.CachedProjectConfig.targetDeviceTypes.Contains(OVRProjectConfig.DeviceType.Quest3),
                message: "Room Mesh is only available to Quest 3 devices",
                fix: _ =>
                {
                    var projectConfig = OVRProjectConfig.CachedProjectConfig;
                    projectConfig.targetDeviceTypes.Add(OVRProjectConfig.DeviceType.Quest3);
                },
                fixMessage: "Set Quest 3 as a target device"
            );

            OVRProjectSetup.AddTask(
                level: OVRProjectSetup.TaskLevel.Required,
                group: OVRProjectSetup.TaskGroup.Features,
                isDone: _ => !RoomMeshExist() || OVRProjectConfig.CachedProjectConfig.sceneSupport != OVRProjectConfig.FeatureSupport.None,
                message: "When using Room Mesh in your project it's required to enable its capability in the project config",
                fix: _ =>
                {
                    var projectConfig = OVRProjectConfig.CachedProjectConfig;
                    projectConfig.sceneSupport = OVRProjectConfig.FeatureSupport.Supported;
                    OVRProjectConfig.CommitProjectConfig(projectConfig);
                },
                fixMessage: "Enable Room Mesh Support in the project config"
            );
        }

        private static bool RoomMeshExist() => Utils.GetBlocksInScene().Any(b => b.blockId == BlockDataIds.RoomMesh);
    }
}
