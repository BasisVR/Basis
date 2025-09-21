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

using UnityEditor;

namespace Meta.XR.BuildingBlocks.Editor
{
    [InitializeOnLoad]
    internal static class SpatialAnchorCoreSetupRules
    {
        static SpatialAnchorCoreSetupRules()
        {
            OVRProjectSetup.AddTask(
                level: OVRProjectSetup.TaskLevel.Required,
                group: OVRProjectSetup.TaskGroup.Features,
                isDone: buildTargetGroup => OVRProjectSetupUtils.FindComponentInScene<SpatialAnchorCoreBuildingBlock>() == null ||
                                            OVRProjectConfig.CachedProjectConfig.anchorSupport ==
                                            OVRProjectConfig.AnchorSupport.Enabled,
                message: "When using Spatial Anchor in your project it's required to enable its capability in the project config",
                fix: buildTargetGroup =>
                {
                    var projectConfig = OVRProjectConfig.CachedProjectConfig;
                    projectConfig.anchorSupport = OVRProjectConfig.AnchorSupport.Enabled;
                    OVRProjectConfig.CommitProjectConfig(projectConfig);
                },
                fixMessage: "Enable Spatial Anchor Support in the project config"
            );
        }
    }
}
