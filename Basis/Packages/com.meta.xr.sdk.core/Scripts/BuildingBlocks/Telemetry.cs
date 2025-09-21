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

using System.IO;
using UnityEngine.SceneManagement;
using static OVRTelemetryConstants.BB;

namespace Meta.XR.BuildingBlocks
{
    internal static class Telemetry
    {
        public static OVRTelemetryMarker AddBlockInfo(this OVRTelemetryMarker marker, BuildingBlock block) =>
            marker.AddAnnotation(AnnotationType.BlockId, block.BlockId)
                .AddAnnotation(AnnotationType.InstanceId, block.InstanceId)
                .AddAnnotation(AnnotationType.BlockName, block.gameObject.name)
                .AddAnnotation(AnnotationType.Version, block.Version.ToString())
                .AddBlockVariantInfo(block);

        private static OVRTelemetryMarker AddBlockVariantInfo(this OVRTelemetryMarker marker, BuildingBlock block)
        {
            if (block.InstallationRoutineCheckpoint == null || string.IsNullOrEmpty(block.InstallationRoutineCheckpoint.InstallationRoutineId))
            {
                return marker;
            }

            return marker
                .AddAnnotation(AnnotationType.InstallationRoutineId,
                    block.InstallationRoutineCheckpoint.InstallationRoutineId)
                .AddInstallationRoutineInfo(block.InstallationRoutineCheckpoint);
        }

        private static OVRTelemetryMarker AddInstallationRoutineInfo(this OVRTelemetryMarker marker, InstallationRoutineCheckpoint checkpoint)
        {
            if (checkpoint == null)
            {
                return marker;
            }

            using (new OVRObjectPool.ListScope<string>(out var dataList))
            {
                // ReSharper disable once ForeachCanBeConvertedToQueryUsingAnotherGetEnumerator
                foreach (var variantCheckpoint in checkpoint.InstallationVariants)
                {
                    if (variantCheckpoint == null)
                    {
                        continue;
                    }

                    dataList.Add($"{variantCheckpoint.MemberName}:{variantCheckpoint.Value}");
                }

                if (dataList.Count > 0)
                {
                    marker.AddAnnotation(AnnotationType.InstallationRoutineData, string.Join(',', dataList));
                }
            }

            return marker;
        }

        public static OVRTelemetryMarker AddSceneInfo(this OVRTelemetryMarker marker, Scene scene)
        {
            long sceneSizeInB = 0;

            if (File.Exists(scene.path))
            {
                sceneSizeInB = new FileInfo(scene.path).Length;
            }

            return marker.AddAnnotation(AnnotationType.SceneSizeInB, sceneSizeInB.ToString());
        }
    }
}
