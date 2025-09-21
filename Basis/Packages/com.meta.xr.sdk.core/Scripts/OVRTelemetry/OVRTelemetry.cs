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

#if !(UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN || UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX || (UNITY_ANDROID && !UNITY_EDITOR))
#define OVRPLUGIN_UNSUPPORTED_PLATFORM
#endif

using System;
using UnityEngine;

internal static partial class OVRTelemetry
{
    internal static bool IsActive
    {
        get
        {
#pragma warning disable CS0162 // Unreachable code detected

#if OVRPLUGIN_UNSUPPORTED_PLATFORM
            return false;
#endif

            return true;
#pragma warning restore CS0162 // Unreachable code detected
        }
    }

    private static readonly TelemetryClient InactiveClient = new NullTelemetryClient();
    public static readonly TelemetryClient ActiveClient = new QPLTelemetryClient();
    public static TelemetryClient Client => IsActive ? ActiveClient : InactiveClient;

    public readonly struct MarkerPoint : IDisposable
    {
        public int NameHandle { get; }

        public MarkerPoint(string name)
        {
            Client.CreateMarkerHandle(name, out var nameHandle);
            NameHandle = nameHandle;
        }

        public void Dispose()
        {
            Client.DestroyMarkerHandle(NameHandle);
        }
    }

    public abstract class TelemetryClient
    {
        public abstract void MarkerStart(int markerId, int instanceKey = OVRPlugin.Qpl.DefaultInstanceKey,
            long timestampMs = OVRPlugin.Qpl.AutoSetTimestampMs, string joinId = null);

        public abstract void MarkerPointCached(int markerId, int nameHandle,
            int instanceKey = OVRPlugin.Qpl.DefaultInstanceKey, long timestampMs = OVRPlugin.Qpl.AutoSetTimestampMs);

        public abstract void MarkerPoint(int markerId, string name,
            int instanceKey = OVRPlugin.Qpl.DefaultInstanceKey, long timestampMs = OVRPlugin.Qpl.AutoSetTimestampMs);

        public abstract unsafe void MarkerPoint(int markerId, string name, OVRPlugin.Qpl.Annotation* annotations,
            int annotationCount, int instanceKey = OVRPlugin.Qpl.DefaultInstanceKey,
            long timestampMs = OVRPlugin.Qpl.AutoSetTimestampMs);

        public abstract void MarkerAnnotation(int markerId, string key, OVRPlugin.Qpl.Variant value,
            int instanceKey = OVRPlugin.Qpl.DefaultInstanceKey);

        public abstract void MarkerAnnotation(int markerId, string annotationKey, string annotationValue,
            int instanceKey = OVRPlugin.Qpl.DefaultInstanceKey);

        public void MarkerAnnotation(int markerId, string annotationKey,
            bool annotationValue, int instanceKey = OVRPlugin.Qpl.DefaultInstanceKey)
            => MarkerAnnotation(markerId, annotationKey, OVRPlugin.Qpl.Variant.From(annotationValue), instanceKey);

        public void MarkerAnnotation(int markerId, string annotationKey,
            long annotationValue, int instanceKey = OVRPlugin.Qpl.DefaultInstanceKey)
            => MarkerAnnotation(markerId, annotationKey, OVRPlugin.Qpl.Variant.From(annotationValue), instanceKey);

        public void MarkerAnnotation(int markerId, string annotationKey,
            double annotationValue, int instanceKey = OVRPlugin.Qpl.DefaultInstanceKey)
            => MarkerAnnotation(markerId, annotationKey, OVRPlugin.Qpl.Variant.From(annotationValue), instanceKey);

        public unsafe void MarkerAnnotation(int markerId, string annotationKey,
            byte** annotationValues, int count, int instanceKey = OVRPlugin.Qpl.DefaultInstanceKey)
            => MarkerAnnotation(markerId, annotationKey, OVRPlugin.Qpl.Variant.From(annotationValues, count), instanceKey);

        public unsafe void MarkerAnnotation(int markerId, string annotationKey,
            long* annotationValues, int count, int instanceKey = OVRPlugin.Qpl.DefaultInstanceKey)
            => MarkerAnnotation(markerId, annotationKey, OVRPlugin.Qpl.Variant.From(annotationValues, count), instanceKey);

        public unsafe void MarkerAnnotation(int markerId, string annotationKey,
            double* annotationValues, int count, int instanceKey = OVRPlugin.Qpl.DefaultInstanceKey)
            => MarkerAnnotation(markerId, annotationKey, OVRPlugin.Qpl.Variant.From(annotationValues, count), instanceKey);

        public unsafe void MarkerAnnotation(int markerId, string annotationKey,
            OVRPlugin.Bool* annotationValues, int count, int instanceKey = OVRPlugin.Qpl.DefaultInstanceKey)
            => MarkerAnnotation(markerId, annotationKey, OVRPlugin.Qpl.Variant.From(annotationValues, count), instanceKey);

        public abstract void MarkerEnd(int markerId,
            OVRPlugin.Qpl.ResultType resultTypeId = OVRPlugin.Qpl.ResultType.Success,
            int instanceKey = OVRPlugin.Qpl.DefaultInstanceKey, long timestampMs = OVRPlugin.Qpl.AutoSetTimestampMs);

        public abstract bool CreateMarkerHandle(string name, out int nameHandle);
        public abstract bool DestroyMarkerHandle(int nameHandle);
    }

    private class NullTelemetryClient : TelemetryClient
    {
        public override void MarkerStart(int markerId, int instanceKey = OVRPlugin.Qpl.DefaultInstanceKey,
            long timestampMs = OVRPlugin.Qpl.AutoSetTimestampMs, string joinId = null)
        {
        }

        public override void MarkerPointCached(int markerId, int nameHandle,
            int instanceKey = OVRPlugin.Qpl.DefaultInstanceKey, long timestampMs = OVRPlugin.Qpl.AutoSetTimestampMs)
        {
        }

        public override void MarkerPoint(int markerId, string name, int instanceKey = OVRPlugin.Qpl.DefaultInstanceKey,
            long timestampMs = OVRPlugin.Qpl.AutoSetTimestampMs)
        {
        }

        public override unsafe void MarkerPoint(int markerId, string name, OVRPlugin.Qpl.Annotation* annotations,
            int annotationCount, int instanceKey = OVRPlugin.Qpl.DefaultInstanceKey,
            long timestampMs = OVRPlugin.Qpl.AutoSetTimestampMs)
        {
        }

        public override void MarkerAnnotation(int markerId, string key, OVRPlugin.Qpl.Variant value,
            int instanceKey = OVRPlugin.Qpl.DefaultInstanceKey)
        {
        }

        public override void MarkerAnnotation(int markerId, string annotationKey, string annotationValue,
            int instanceKey = OVRPlugin.Qpl.DefaultInstanceKey)
        {
        }

        public override void MarkerEnd(int markerId,
            OVRPlugin.Qpl.ResultType resultTypeId = OVRPlugin.Qpl.ResultType.Success,
            int instanceKey = OVRPlugin.Qpl.DefaultInstanceKey, long timestampMs = OVRPlugin.Qpl.AutoSetTimestampMs)
        {
        }

        public override bool CreateMarkerHandle(string name, out int nameHandle)
        {
            nameHandle = default;
            return false;
        }

        public override bool DestroyMarkerHandle(int nameHandle) => false;
    }

    private class QPLTelemetryClient : TelemetryClient
    {
        public override void MarkerStart(int markerId, int instanceKey = OVRPlugin.Qpl.DefaultInstanceKey,
            long timestampMs = OVRPlugin.Qpl.AutoSetTimestampMs, string joinId = null)
        {
            if (string.IsNullOrEmpty(joinId))
            {
                OVRPlugin.Qpl.MarkerStart(markerId, instanceKey, timestampMs);
            }
            else
            {
                OVRPlugin.Qpl.MarkerStartForJoin(markerId, joinId, OVRPlugin.Bool.False, instanceKey, timestampMs);
            }
        }

        public override void MarkerPointCached(int markerId, int nameHandle,
            int instanceKey = OVRPlugin.Qpl.DefaultInstanceKey, long timestampMs = OVRPlugin.Qpl.AutoSetTimestampMs)
        {
            OVRPlugin.Qpl.MarkerPointCached(markerId, nameHandle, instanceKey, timestampMs);
        }

        public override void MarkerPoint(int markerId, string name, int instanceKey = OVRPlugin.Qpl.DefaultInstanceKey,
            long timestampMs = OVRPlugin.Qpl.AutoSetTimestampMs)
        {
            OVRPlugin.Qpl.MarkerPoint(markerId, name, instanceKey, timestampMs);
        }

        public override unsafe void MarkerPoint(int markerId, string name, OVRPlugin.Qpl.Annotation* annotations,
            int annotationCount, int instanceKey = OVRPlugin.Qpl.DefaultInstanceKey,
            long timestampMs = OVRPlugin.Qpl.AutoSetTimestampMs)
        {
            OVRPlugin.Qpl.MarkerPoint(markerId, name, annotations, annotationCount, instanceKey, timestampMs);
        }

        public override void MarkerAnnotation(int markerId, string annotationKey,
            string annotationValue, int instanceKey = OVRPlugin.Qpl.DefaultInstanceKey)
        {
            OVRPlugin.Qpl.MarkerAnnotation(markerId, annotationKey, annotationValue, instanceKey);
        }

        public override void MarkerAnnotation(int markerId, string key, OVRPlugin.Qpl.Variant value,
            int instanceKey = OVRPlugin.Qpl.DefaultInstanceKey)
        {
            OVRPlugin.Qpl.MarkerAnnotation(markerId, key, value, instanceKey);
        }

        public override void MarkerEnd(int markerId,
            OVRPlugin.Qpl.ResultType resultTypeId = OVRPlugin.Qpl.ResultType.Success,
            int instanceKey = OVRPlugin.Qpl.DefaultInstanceKey, long timestampMs = OVRPlugin.Qpl.AutoSetTimestampMs)
        {
            OVRPlugin.Qpl.MarkerEnd(markerId, resultTypeId, instanceKey, timestampMs);
        }

        public override bool CreateMarkerHandle(string name, out int nameHandle)
        {
            return OVRPlugin.Qpl.CreateMarkerHandle(name, out nameHandle);
        }

        public override bool DestroyMarkerHandle(int nameHandle)
        {
            return OVRPlugin.Qpl.DestroyMarkerHandle(nameHandle);
        }
    }

    public static OVRTelemetryMarker Start(int markerId,
        int instanceKey = OVRPlugin.Qpl.DefaultInstanceKey,
        long timestampMs = OVRPlugin.Qpl.AutoSetTimestampMs)
    {
        return new OVRTelemetryMarker(markerId, instanceKey, timestampMs);
    }

    public static void SendEvent(int markerId,
        OVRPlugin.Qpl.ResultType result = OVRPlugin.Qpl.ResultType.Success)
    {
        Start(markerId).SetResult(result).Send();
    }
}
