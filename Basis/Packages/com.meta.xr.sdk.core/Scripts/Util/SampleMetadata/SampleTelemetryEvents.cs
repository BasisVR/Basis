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

using static OVRTelemetry;

namespace Meta.XR.Samples.Telemetry
{
    internal static class SampleTelemetryEvents
    {
        [Markers]
        public static class EventTypes
        {
            // Attention : Need to be kept in sync with QPL Event Ids
            public const int Open = 163055403;
            public const int Close = 163056880;
            public const int Run = 163061602;
        }

        public static class AnnotationTypes
        {
            public const string Sample = "Sample";
            public const string TimeSpent = "TimeSpent";
            public const string TimeSinceEditorStart = "TimeSinceEditorStart";
            public const string BuildTarget = "BuildTarget";
            public const string RuntimePlatform = "RuntimePlatform";
            public const string InEditor = "InEditor";
        }
    }

}
