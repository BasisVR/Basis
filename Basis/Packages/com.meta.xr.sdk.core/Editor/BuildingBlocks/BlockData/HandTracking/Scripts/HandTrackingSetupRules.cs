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
    internal static class HandTrackingSetupRules
    {
        static HandTrackingSetupRules()
        {

            OVRProjectSetup.AddTask(
                level: OVRProjectSetup.TaskLevel.Required,
                group: OVRProjectSetup.TaskGroup.Compatibility,
                isDone: _ =>
                {
                    if (!HandTrackingBuildingBlockExists())
                    {
                        return true;
                    }

                    var projectConfig = OVRProjectConfig.CachedProjectConfig;
                    return projectConfig.handTrackingSupport != OVRProjectConfig.HandTrackingSupport.ControllersOnly;

                },
                message: $"Hand Tracking must be enabled in OVRManager when using its {Utils.BlockPublicName}",
                fix: _ =>
                {
                    var projectConfig = OVRProjectConfig.CachedProjectConfig;
                    projectConfig.handTrackingSupport = OVRProjectConfig.HandTrackingSupport.ControllersAndHands;
                    OVRProjectConfig.CommitProjectConfig(projectConfig);
                },
                fixMessage: $"Enable Hand Tracking must be enabled in OVRManager"
            );

            OVRProjectSetup.AddTask(
                level: OVRProjectSetup.TaskLevel.Required,
                group: OVRProjectSetup.TaskGroup.Compatibility,
                isDone: _ =>
                {
                    if (!HandTrackingBuildingBlockExists())
                    {
                        return true;
                    }

                    var projectConfig = OVRProjectConfig.CachedProjectConfig;
                    return projectConfig.handTrackingVersion == OVRProjectConfig.HandTrackingVersion.V2;

                },
                message: $"Hand Tracking V2 is required when using the Hand Tracking {Utils.BlockPublicName}",
                fix: _ =>
                {
                    var projectConfig = OVRProjectConfig.CachedProjectConfig;
                    projectConfig.handTrackingVersion = OVRProjectConfig.HandTrackingVersion.V2;
                    OVRProjectConfig.CommitProjectConfig(projectConfig);
                },
                fixMessage: $"Select Hand Tracking V2"
            );
        }

        private static bool HandTrackingBuildingBlockExists()
        {
            var handObjects = OVRProjectSetupUtils.FindComponentsInScene<OVRHand>();
            return handObjects.Any(hand => hand.GetComponent<BuildingBlock>());
        }
    }
}
