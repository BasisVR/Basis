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
using System.Threading.Tasks;
using Meta.XR.Editor.Features;
using UnityEditor;
using UnityEngine.SceneManagement;

namespace Meta.XR.Telemetry
{
    [InitializeOnLoad]
    internal class FeatureStatusReporter
    {
        private static readonly string NextTriggerTimeKey = "NextTriggerTime";
        private static DateTime _nextTriggerTime;
        private static int _timeIntervalSeconds = 60 * 60; // 1 hour

        private static string SavedTicks
        {
            get => EditorPrefs.GetString(NextTriggerTimeKey, DateTime.UtcNow.Ticks.ToString());
            set => EditorPrefs.SetString(NextTriggerTimeKey, value);
        }

        static FeatureStatusReporter()
        {
            _nextTriggerTime = new(Convert.ToInt64(SavedTicks));
            EditorApplication.update += Update;
        }

        private static async void Update()
        {
            var utcNow = DateTime.UtcNow;
            // Check if the current time is past the next trigger time
            if (utcNow >= _nextTriggerTime)
            {
                // Update next trigger time
                _nextTriggerTime = utcNow.AddSeconds(_timeIntervalSeconds);
                SavedTicks = _nextTriggerTime.Ticks.ToString();

                // Send report
                await ReportFeaturesStatus();
            }
        }

        private static async Task ReportFeaturesStatus()
        {
            var scene = SceneManager.GetActiveScene();
            var guid = AssetDatabase.AssetPathToGUID(scene.path);
            if (string.IsNullOrEmpty(guid)) return;
            var featuresInScene = await FeatureManager.GetFeaturesInScene(scene);

            OVRTelemetry.Start(OVRTelemetryConstants.Editor.MarkerId.FeaturesInScene)
                            .AddAnnotation(OVRTelemetryConstants.Scene.AnnotationType.Guid,
                                            guid)
                            .AddAnnotation(OVRTelemetryConstants.Scene.AnnotationType.BuildTarget,
                                            EditorUserBuildSettings.selectedBuildTargetGroup.ToString())
                            .AddAnnotation(OVRTelemetryConstants.Scene.AnnotationType.Features,
                                            featuresInScene)
                            .AddAnnotation(OVRTelemetryConstants.Scene.AnnotationType.EnabledSettings,
                                            FeatureManager.GetFeatureStatusInSettings())
                            .Send();
        }
    }
}
