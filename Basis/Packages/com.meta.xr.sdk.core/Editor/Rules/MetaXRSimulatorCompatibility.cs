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

using Meta.XR.Editor.Utils;
using UnityEditor;

namespace Meta.XR.Editor.Rules
{
    [InitializeOnLoad]
    internal static class MetaXRSimulatorCompatibility
    {
        static MetaXRSimulatorCompatibility()
        {
            // [Recommended] Oculus Integration and Meta XR Sim should match
            OVRProjectSetup.AddTask(
                level: OVRProjectSetup.TaskLevel.Recommended,
                group: OVRProjectSetup.TaskGroup.Compatibility,
                isDone: _ =>
                {
                    if (TryGetXRSimPackageVersion(out var xrSimVersion))
                    {
                        return xrSimVersion == SDKVersion || xrSimVersion == SDKVersion - 1;
                    }

                    return true;
                },
                conditionalValidity: _ =>
                {
#if UNITY_EDITOR_WIN
                    return true;
#else
                    return false;
#endif
                },
                conditionalMessage: _ => TryGetXRSimPackageVersion(out var xrSimVersion)
                    ? $"The Oculus Integration SDK (v{SDKVersion}) and Meta XR Simulator package (v{xrSimVersion}) versions must match to ensure correct functionality"
                    : "The Oculus Integration SDK and Meta XR Simulator package versions must match");
        }

        private static bool TryGetXRSimPackageVersion(out int version)
        {
            version = default;
            var package = PackageList.GetPackage("com.meta.xr.simulator");
            if (package == null)
            {
                return false;
            }

            var versionParts = package.version.Split('.');
            return versionParts.Length > 0 && int.TryParse(versionParts[0], out version);
        }

        private static int SDKVersion => OVRPlugin.version.Minor - 32;
    }
}
