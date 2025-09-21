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
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

using UnityEngine;

namespace Meta.XR.Editor.Callbacks
{
    /// <summary>
    ///   Post-build processor tasked with ensuring a clean project state after every build.
    /// </summary>
    class OVRPostBuildCleanup : IPostprocessBuildWithReport
    {
        public int callbackOrder => 1000;

        public void OnPostprocessBuild(BuildReport report)
        {
            LogInternal(nameof(FlushPlayerSettings));
            {
                FlushPlayerSettings();
            }
        }

        //
        // implementation:

        /// <summary>
        ///   This is the fix for Unity's XR Plugin Manager leaving a dirty ProjectSettings.asset post-build.
        /// </summary>
        static void FlushPlayerSettings()
        {
            // unfortunately, it does not work to use SaveAssetIfDirty on the PlayerSettings instance. (it's special.)
            AssetDatabase.SaveAssets(); // .. so we have to save everything.

            AssetDatabase.Refresh(); // if we saved everything, we should refresh.

            // the project settings asset should be forced to reimport:
            AssetDatabase.ImportAsset("ProjectSettings/ProjectSettings.asset", ImportAssetOptions.ForceUpdate);
        }

        static void LogInternal(object log = null, LogType type = LogType.Log)
        {
            _ = log;
            _ = type;
        }
    } // end class OVRPostBuildCleanup
}
