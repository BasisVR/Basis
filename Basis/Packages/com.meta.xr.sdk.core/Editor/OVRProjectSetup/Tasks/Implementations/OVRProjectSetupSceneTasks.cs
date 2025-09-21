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
internal static class OVRProjectSetupSceneTasks
{
    private const OVRProjectSetup.TaskGroup Group = OVRProjectSetup.TaskGroup.Features;

    static OVRProjectSetupSceneTasks()
    {
        // OVRSceneManager is deprecated but we still keep the project setup task for projects that are still using it
        OVRProjectSetup.AddTask(
            level: OVRProjectSetup.TaskLevel.Required,
            group: Group,
#pragma warning disable CS0618 // Type or member is obsolete
            isDone: buildTargetGroup => OVRProjectSetupUtils.FindComponentInScene<OVRSceneManager>() == null ||
#pragma warning restore CS0618 // Type or member is obsolete
                                        OVRProjectConfig.CachedProjectConfig.sceneSupport != OVRProjectConfig.FeatureSupport.None,
            message: "When using Scene in your project it's required to enable its capability in the project config",
            fix: buildTargetGroup =>
            {
                // we also need anchorSupport
                var projectConfig = OVRProjectConfig.CachedProjectConfig;
                projectConfig.sceneSupport = OVRProjectConfig.FeatureSupport.Required;
                projectConfig.anchorSupport = OVRProjectConfig.AnchorSupport.Enabled;
                OVRProjectConfig.CommitProjectConfig(projectConfig);
            },
            fixMessage: "Enable Scene Support in the project config"
        );

        OVRProjectSetup.AddTask(
            level: OVRProjectSetup.TaskLevel.Optional,
            group: Group,
            isDone: buildTargetGroup =>
            {
                var usingScene = OVRProjectConfig.CachedProjectConfig.sceneSupport !=
                    OVRProjectConfig.FeatureSupport.None;
                if (!usingScene)
                    return true;

                var ovrManager = OVRProjectSetupUtils.FindComponentInScene<OVRManager>();
                if (ovrManager == null)
                    return true;

                return ovrManager.requestScenePermissionOnStartup;
            },
            message: "When using Scene in your project, it's required to perform a runtime permission request. " +
                "Hit Apply to have OVRManager request the permission automatically on app startup. It is " +
                "recommended to hit Ignore and manage the runtime permission yourself.",
            fix: buildTargetGroup =>
            {
                var ovrManager = OVRProjectSetupUtils.FindComponentInScene<OVRManager>();
                if (ovrManager == null) return;
                ovrManager.requestScenePermissionOnStartup = true;
            },
            fixMessage: "OVRManager will request Scene runtime permission on app startup"
        );

    }
}
