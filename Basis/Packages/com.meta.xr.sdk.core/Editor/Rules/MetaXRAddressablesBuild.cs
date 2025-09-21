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

#if ADDRESSABLES_1_18_19_OR_NEWER
using UnityEditor;
using UnityEditor.AddressableAssets.Build;
using UnityEngine;

namespace Meta.XR.Editor.Rules
{
    [InitializeOnLoad]
    internal static class MetaXRAddressablesBuild
    {
        static MetaXRAddressablesBuild()
        {
            BuildScript.buildCompleted += OnAddressablesBuildCompleted;

            OVRProjectSetup.AddTask(
                level: OVRProjectSetup.TaskLevel.Optional,
                group: OVRProjectSetup.TaskGroup.Miscellaneous,
                isDone: _ => EditorPrefs.GetBool(LastBuildHadShortDuration, true),
                message: message,
                url: "https://developer.oculus.com/documentation/unity/po-unity-iteration/#fix-long-addressables-build-time"
            );
        }

        private static readonly string LastBuildHadShortDuration = Application.productName + ".MetaXRAddressablesBuild.LastBuildHadShortDuration";
        private const string message = "Speed up your addressables build times by increasing the scriptable build " +
                                     "pipeline's maximum cache size at 'Edit > Preferences > Scriptable Build Pipeline'";
        private const double TEN_MINUTES = 600;

        private static void OnAddressablesBuildCompleted(AddressableAssetBuildResult buildResult)
        {
            // Disregard build results that aren't from building the Addressables content
            if (buildResult is not AddressablesPlayerBuildResult)
            {
                return;
            }

            if (buildResult.Duration > TEN_MINUTES)
            {
                EditorPrefs.SetBool(LastBuildHadShortDuration, false);
            } else
            {
                EditorPrefs.SetBool(LastBuildHadShortDuration, true);
            }
        }
    }
}
#endif
