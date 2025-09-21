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

using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace Meta.XR.ImmersiveDebugger.Editor
{
    internal class RuntimeSettingsBuildProcessor : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        public int callbackOrder => 0;

        bool m_AssetWasAutoAdded;

        public void OnPreprocessBuild(BuildReport report)
        {
            var runtimeSettingInstance = RuntimeSettings.Instance;
            if (!runtimeSettingInstance)
                return;

            if (runtimeSettingInstance.ImmersiveDebuggerEnabled)
                m_AssetWasAutoAdded = runtimeSettingInstance.AddToPreloadedAssets();
            else
                _ = runtimeSettingInstance.RemoveFromPreloadedAssets();
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            // remove it after building so project states don't get auto-dirtied
            if (m_AssetWasAutoAdded && RuntimeSettings.Instance)
                RuntimeSettings.Instance.RemoveFromPreloadedAssets();
        }
    }
}
