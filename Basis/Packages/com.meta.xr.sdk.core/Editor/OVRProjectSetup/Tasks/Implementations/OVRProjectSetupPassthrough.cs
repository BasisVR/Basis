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

using Meta.XR.Editor.Rules;
using UnityEditor;

[InitializeOnLoad]
internal static class OVRProjectSetupPassthrough
{
    private const OVRProjectSetup.TaskGroup Group = OVRProjectSetup.TaskGroup.Features;

    static OVRProjectSetupPassthrough()
    {
        OVRProjectSetup.AddTask(
            level: OVRProjectSetup.TaskLevel.Required,
            group: Group,
            isDone: buildTargetGroup => AndroidCompatibility.IsTargetingARM64,
            conditionalValidity: buildTargetGroup => OVRProjectConfig.CachedProjectConfig.insightPassthroughSupport !=
                                                     OVRProjectConfig.FeatureSupport.None,
            message: "When enabling the Passthrough capability in your project it's required to use ARM64 as " +
                     "the target architecture",
            fix: AndroidCompatibility.SetARM64Target,
            fixMessage: "PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64"
        );

        OVRProjectSetup.AddTask(
            level: OVRProjectSetup.TaskLevel.Required,
            group: Group,
            isDone: _ =>
            {
                var ovrCameraRig = OVRProjectSetupUtils.FindComponentInScene<OVRCameraRig>();
                return ovrCameraRig != null &&
                       OVRPassthroughHelper.HasCentralCamera(ovrCameraRig) &&
                       OVRPassthroughHelper.IsBackgroundClear(ovrCameraRig);
            },
            message: "When using Passthrough as an underlay it's required set the camera background to transparent",
            fix: _ =>
            {
                var ovrCameraRig = OVRProjectSetupUtils.FindComponentInScene<OVRCameraRig>();
                if (ovrCameraRig != null && OVRPassthroughHelper.HasCentralCamera(ovrCameraRig))
                {
                    OVRPassthroughHelper.ClearBackground(ovrCameraRig);
                }
            },
            fixMessage: "Clear background of OVRCameraRig"
        );

#if UNITY_PRO_LICENSE || UNITY_6000_0_OR_NEWER // Users can't remove the unity splash screen unless they have a license OR are on Unity 6+
        OVRProjectSetup.AddTask(
            level: OVRProjectSetup.TaskLevel.Recommended,
            group: Group,
            isDone: _ => OVRProjectConfig.CachedProjectConfig.systemLoadingScreenBackground !=
                         OVRProjectConfig.SystemLoadingScreenBackground.ContextualPassthrough ||
                         PlayerSettings.virtualRealitySplashScreen == null,
            message: "For MR apps it's recommended to disable Virtual Reality Splash Screen " +
                     "(in Player Settings). Otherwise it will result in an inconsistent experience",
            fix: _ =>
            {
                PlayerSettings.virtualRealitySplashScreen = null;
            },
            fixMessage: "Disable Virtual Reality Splash Screen (in Player Settings)"
        );

        OVRProjectSetup.AddTask(
            level: OVRProjectSetup.TaskLevel.Recommended,
            group: Group,
            isDone: _ => OVRProjectConfig.CachedProjectConfig.systemLoadingScreenBackground !=
                         OVRProjectConfig.SystemLoadingScreenBackground.ContextualPassthrough ||
                         !PlayerSettings.SplashScreen.show,
            message: "For MR apps it's recommended to disable Show Splash Screen " +
                     "(in Player Settings). Otherwise it will result in an inconsistent experience",
            fix: _ =>
            {
                PlayerSettings.SplashScreen.show = false;
            },
            fixMessage: "Disable Show Splash Screen (in Player Settings)"
        );

        // This rule is partially backed by the OVRProjectConfigEditor logic, which auto-corrects the project configuration
        // once a developer tries to break away from the rule. However, on project start OVRProjectConfigEditor is powerless,
        // and then this rule steps in. In other words, this rule is useful only on project start.
        OVRProjectSetup.AddTask(
            level: OVRProjectSetup.TaskLevel.Required,
            group: Group,
            isDone: _ =>
                OVRProjectConfig.CachedProjectConfig.insightPassthroughSupport == OVRProjectConfig.FeatureSupport.None ||
                OVRProjectConfig.CachedProjectConfig.systemLoadingScreenBackground == OVRProjectConfig.SystemLoadingScreenBackground.ContextualPassthrough,
            message: "Black System Splash Screen background is prohibited for MR apps",
            fix: _ =>
            {
                var projectConfig = OVRProjectConfig.CachedProjectConfig;
                projectConfig.systemLoadingScreenBackground = OVRProjectConfig.SystemLoadingScreenBackground.ContextualPassthrough;
                OVRProjectConfig.CommitProjectConfig(projectConfig);
            },
            fixMessage: "Set System Splash Screen background to \"Passthrough (Contextual)\""
        );
#endif

    }
}
