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
internal static class OVRProjectSetupCompatibilityTasks
{

    static OVRProjectSetupCompatibilityTasks()
    {
        const OVRProjectSetup.TaskGroup compatibilityTaskGroup = OVRProjectSetup.TaskGroup.Compatibility;

        // [Required] Platform has to be supported
        OVRProjectSetup.AddTask(
            level: OVRProjectSetup.TaskLevel.Required,
            group: compatibilityTaskGroup,
            isDone: OVRProjectSetup.IsPlatformSupported,
            conditionalMessage: buildTargetGroup =>
                OVRProjectSetup.IsPlatformSupported(buildTargetGroup)
                    ? $"Build Target ({buildTargetGroup}) is supported"
                    : $"Build Target ({buildTargetGroup}) is not supported"
        );

        // [Recommended] No Alpha or Beta for production
        OVRProjectSetup.AddTask(
            level: OVRProjectSetup.TaskLevel.Recommended,
            group: compatibilityTaskGroup,
            isDone: _ => !OVRManager.IsUnityAlphaOrBetaVersion(),
            message: $"We recommend using a stable version for {OVREditorUtils.MetaXRPublicName} Development"
        );
    }
}
