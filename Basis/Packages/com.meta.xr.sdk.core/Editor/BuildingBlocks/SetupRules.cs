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

using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;

namespace Meta.XR.BuildingBlocks.Editor
{
    [InitializeOnLoad]
    internal static class SetupRules
    {
        static SetupRules()
        {
            // [Required] All block dependencies must be present
            OVRProjectSetup.AddTask(
                level: OVRProjectSetup.TaskLevel.Required,
                group: OVRProjectSetup.TaskGroup.Compatibility,
                isDone: _ =>
                {
                    var blockSet = GetSceneBlockSet();

                    return GetSceneBlocks()
                        .Select(block => block.GetBlockData())
                        .SelectMany(blockData => blockData != null ? blockData.Dependencies : Enumerable.Empty<BlockData>())
                        .Where(dependency => dependency != null)
                        .All(dependency => blockSet.Contains(dependency.Id));
                },
                message: $"All {Utils.BlocksPublicName} dependencies must be present in the scene",
                fix: _ =>
                {
                    var blocks = GetSceneBlocks();
                    var blockSet = GetSceneBlockSet();

                    foreach (var blockData in blocks.Select(block => block.GetBlockData()))
                    {
                        if (blockData == null)
                        {
                            continue;
                        }

                        foreach (var dependency in blockData.Dependencies)
                        {
                            if (dependency == null || blockSet.Contains(dependency.Id))
                            {
                                continue;
                            }

#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                            dependency.AddToProject();
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                            blockSet = GetSceneBlockSet();
                        }
                    }
                },
                fixMessage: "Install the missing dependencies"
            );

            // [Required] All block package dependencies must be present with the correct versions
            OVRProjectSetup.AddTask(
                level: OVRProjectSetup.TaskLevel.Required,
                group: OVRProjectSetup.TaskGroup.Compatibility,
                isDone: _ => !GetMissingPackageIds().Any(),
                message: BuildFixMessageForMissingPackageDependencies(GetMissingPackageIds())
            );
        }

        private static IEnumerable<string> GetMissingPackageIds()
        {
            return GetSceneBlocks()
                .Select(block => block.GetBlockData())
                .Where(blockData => blockData != null)
                .Distinct()
                .SelectMany(blockData => blockData.GetMissingPackageDependencies);
        }

        private static string BuildFixMessageForMissingPackageDependencies(IEnumerable<string> missingPackageIds)
        {
            var str = new StringBuilder();
            str.Append($"All {Utils.BlocksPublicName} package dependencies must be present in the scene with valid versions. Missing packages:");

            foreach (var packageId in missingPackageIds)
            {
                str.Append($"\n  • {packageId}");
            }

            return str.ToString();
        }

        private static IEnumerable<BuildingBlock> GetSceneBlocks()
        {
            return OVRProjectSetupUtils.FindComponentsInScene<BuildingBlock>();
        }

        private static HashSet<string> GetSceneBlockSet()
        {
            return new HashSet<string>(GetSceneBlocks().Select(block => block.BlockId));
        }
    }
}
