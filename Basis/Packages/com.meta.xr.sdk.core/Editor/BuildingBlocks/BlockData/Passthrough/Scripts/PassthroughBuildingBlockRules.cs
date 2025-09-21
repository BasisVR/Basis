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
    public class PassthroughBuildingBlockRules
    {
        static PassthroughBuildingBlockRules()
        {
            OVRProjectSetup.AddTask(
                level: OVRProjectSetup.TaskLevel.Required,
                group: OVRProjectSetup.TaskGroup.Features,
                isDone: _ =>
                {
                    OVRCameraRig cameraRigBuildingBlock = Utils.GetBlocksWithType<OVRCameraRig>().FirstOrDefault();
                    if (!PassthroughBuildingBlockExists() || cameraRigBuildingBlock == null ||
                        !OVRPassthroughHelper.HasCentralCamera(cameraRigBuildingBlock))
                    {
                        return true;
                    }

                    return OVRPassthroughHelper.IsBackgroundClear(cameraRigBuildingBlock);
                },
                message: "When using Passthrough Building Block as an underlay it's required set the camera background to transparent",
                fix: _ =>
                {
                    OVRCameraRig cameraRigBuildingBlock = Utils.GetBlocksWithType<OVRCameraRig>().FirstOrDefault();
                    if (cameraRigBuildingBlock && OVRPassthroughHelper.HasCentralCamera(cameraRigBuildingBlock))
                    {
                        OVRPassthroughHelper.ClearBackground(cameraRigBuildingBlock);
                    }
                },
                fixMessage: "Clear background of OVRCameraRig"
            );
        }

        private static bool PassthroughBuildingBlockExists()
        {
            return false;
        }
    }
}
