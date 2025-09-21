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

using System;
using System.Collections.Generic;

namespace Meta.XR.BuildingBlocks.Editor
{
    internal struct CustomPackageDependencyInfo
    {
        internal string PackageDisplayName;
        internal Func<bool> IsPackageInstalled;
        internal string InstallationInstructions;
    }

    internal static class CustomPackageDependencyRegistry
    {
        private static readonly Dictionary<string, CustomPackageDependencyInfo> CustomPackageDependencies = new();

        internal static void RegisterCustomPackageDependency(string packageId, CustomPackageDependencyInfo customPackageDepInfo)
        {
            CustomPackageDependencies.TryAdd(packageId, customPackageDepInfo);
        }

        internal static bool IsPackageDepInCustomRegistry(string packageId)
        {
            return CustomPackageDependencies.ContainsKey(packageId);
        }

        internal static bool IsPackageInstalled(string packageId)
        {
            return CustomPackageDependencies.TryGetValue(packageId, out var value) && value.IsPackageInstalled();
        }

        internal static CustomPackageDependencyInfo GetPackageDepInfo(string packageId)
        {
            if (!CustomPackageDependencies.TryGetValue(packageId, out var value))
            {
                throw new InvalidOperationException(
                    "Try to get non-existed package dependency info from custom registry");
            }
            return value;
        }
    }
}
