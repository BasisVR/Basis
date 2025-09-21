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

partial struct OVRAnchor
{
    // This utility helps with the asynchronous nature of a marker which starts with one API call and completes when an
    // event is received, probably on a future frame.
    private
    static class Telemetry
    {
        // requestIds are not necessarily unique across separate APIs, so we need to construct a unique key
        // to map (markerId, requestId) pairs to a particular OVRTelemetryMarker
        private readonly struct Key : IEquatable<Key>
        {
            private readonly int _markerId;
            private readonly ulong _requestId;
            public Key(MarkerId markerId, ulong requestId) => (_markerId, _requestId) = ((int)markerId, requestId);
            public Key(OVRTelemetryMarker marker, ulong requestId) => (_markerId, _requestId) = (marker.MarkerId, requestId);

            public bool Equals(Key other) => _markerId == other._markerId && _requestId == other._requestId;
            public override bool Equals(object obj) => obj is Key other && Equals(other);
            public override int GetHashCode() => unchecked(_markerId.GetHashCode() * 486187739 + _requestId.GetHashCode());
        }

        private static Dictionary<Key, OVRTelemetryMarker> s_markers = new();

        // Called from OVRAnchor.Init
        public static void OnInit() => s_markers.Clear();

        public static void AddMarker(ulong requestId, OVRTelemetryMarker marker)
            => s_markers.Add(new(marker, requestId), marker);

        public static OVRTelemetryMarker Start(MarkerId markerId, ulong requestId, OVRPlugin.Result result)
        {
            var marker = OVRTelemetry.Start((int)markerId);
            SetSyncResult(marker, requestId, result);
            return marker;
        }

        // Set the result of the synchronous function call (the one that initiates the async request).
        // If successful, the marker is stored in a map so that we can mark it complete later.
        // If result indicates failure, then the marker is completed immediately.
        public static void SetSyncResult(OVRTelemetryMarker marker, ulong requestId, OVRPlugin.Result result)
        {
            marker.AddAnnotation(Annotation.SynchronousResult, (long)result);
            if (result.IsSuccess())
            {
                if (requestId == 0)
                {
                    throw new ArgumentException($"{nameof(requestId)} must not be zero if the {nameof(OVRPlugin)} " +
                                             $"method returns a successful result.", nameof(requestId));
                }

                s_markers.Add(new(marker, requestId), marker);
            }
            else
            {
                marker.SetResult(OVRPlugin.Qpl.ResultType.Fail).Send();
            }
        }

        // Sets the asynchronous result (usually received in the OpenXR event queue) and ends the marker.
        public static void SetAsyncResultAndSend(MarkerId markerId, ulong requestId, long result)
            => SetAsyncResult(markerId, requestId, result)?.Send();

        // Sets the asynchronous result (usually received in the OpenXR event queue) but does not end the marker.
        public static OVRTelemetryMarker? SetAsyncResult(MarkerId markerId, ulong requestId, long result)
            => s_markers.Remove(new(markerId, requestId), out var marker)
            ? marker
                .AddAnnotation(Annotation.AsynchronousResult, result)
                .SetResult(result >= 0 ? OVRPlugin.Qpl.ResultType.Success : OVRPlugin.Qpl.ResultType.Fail)
            : null;

        public static OVRTelemetryMarker? GetMarker(MarkerId markerId, ulong requestId)
            => TryGetMarker(markerId, requestId, out var marker) ? marker : null;

        public static bool TryGetMarker(MarkerId markerId, ulong requestId, out OVRTelemetryMarker marker)
            => s_markers.TryGetValue(new(markerId, requestId), out marker);

        public static bool Remove(MarkerId markerId, ulong requestId, out OVRTelemetryMarker marker)
            => s_markers.Remove(new(markerId, requestId), out marker);

        public static OVRTelemetryMarker? GetRemove(MarkerId markerId, ulong requestId)
            => Remove(markerId, requestId, out var marker) ? marker : null;

        internal enum MarkerId
        {
            // XR_META_spatial_entity_discovery
            DiscoverSpaces = 163054959,
            // XR_META_spatial_entity_persistence
            SaveSpaces = 163056974,
            EraseSpaces = 163061838,
            // XR_FB_spatial_entity_query
            QuerySpaces = 163069062,
            // XR_FB_spatial_entity_storage_batch
            SaveSpaceList = 163065048,
            // XR_FB_spatial_entity_storage
            EraseSingleSpace = 163062284,
            // XR_META_dynamic_object_tracker
            ConfigureTracker = 163068237,
        }

        internal static class Annotation
        {
            public const string ComponentTypes = "component_types";
            public const string UuidCount = "uuid_count";
            public const string SpaceCount = "space_count";
            public const string TotalFilterCount = "total_filter_count";
            public const string ResultsCount = "results_count";
            public const string SynchronousResult = "sync_result";
            public const string AsynchronousResult = "async_result";
            public const string StorageLocation = "storage_location";
            public const string Timeout = "timeout";
            public const string MaxResults = "max_results";
            public const string GroupCount = "group_count";
            public const string DynamicObjectClasses = "dynamic_object_classes";
            public const string MarkerTypes = "marker_types";
        }
    }
}
