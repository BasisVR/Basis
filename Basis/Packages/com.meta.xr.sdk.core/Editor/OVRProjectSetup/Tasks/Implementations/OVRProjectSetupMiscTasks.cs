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

[InitializeOnLoad]
internal static class OVRProjectSetupMiscTasks
{
    private const OVRProjectSetup.TaskGroup Group = OVRProjectSetup.TaskGroup.Features;

    static OVRProjectSetupMiscTasks()
    {
        OVRProjectSetup.AddTask(
            level: OVRProjectSetup.TaskLevel.Required,
            group: Group,
            isDone: _ =>
            {
                var ovrManager = OVRProjectSetupUtils.FindComponentInScene<OVRManager>();
                if (ovrManager == null)
                    return true;

                var usingStationary = ovrManager.trackingOriginType == OVRManager.TrackingOrigin.Stationary;
                var experimentalEnabled = OVRProjectConfig.CachedProjectConfig.experimentalFeaturesEnabled;

                return !usingStationary || experimentalEnabled;
            },
            message: "Stationary tracking origin can only be used when experimental features are enabled.",
            fix: _ =>
            {
                var projectConfig = OVRProjectConfig.CachedProjectConfig;
                projectConfig.experimentalFeaturesEnabled = true;
                OVRProjectConfig.CommitProjectConfig(projectConfig);
            },
            fixMessage: "Enabled experimental features to use Stationary tracking origin."
        );
    }
}
