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
using System.Text;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using static OVRTelemetry;

#if UNITY_EDITOR
using UnityEditor;
#endif

internal struct OVRTelemetryMarker : IDisposable
{
    internal struct OVRTelemetryMarkerState
    {
        public bool Sent { get; set; }
        public OVRPlugin.Qpl.ResultType Result { get; set; }

        public OVRTelemetryMarkerState(bool sent, OVRPlugin.Qpl.ResultType result)
        {
            Result = result;
            Sent = sent;
        }
    }



    private OVRTelemetryMarkerState State { get; set; }

    public bool Sent => State.Sent;
    public OVRPlugin.Qpl.ResultType Result => State.Result;

    public int MarkerId { get; }
    public int InstanceKey { get; }

    private readonly TelemetryClient _client;

    public OVRTelemetryMarker(
        int markerId,
        int instanceKey = OVRPlugin.Qpl.DefaultInstanceKey,
        long timestampMs = OVRPlugin.Qpl.AutoSetTimestampMs, string joindId = null)
        : this(
            OVRTelemetry.Client,
            markerId,
            instanceKey,
            timestampMs, joindId)
    {
    }

    internal OVRTelemetryMarker(
        TelemetryClient client,
        int markerId,
        int instanceKey = OVRPlugin.Qpl.DefaultInstanceKey,
        long timestampMs = OVRPlugin.Qpl.AutoSetTimestampMs,
        string joinId = null)
    {
        MarkerId = markerId;
        InstanceKey = instanceKey;
        _client = client;
        State = new OVRTelemetryMarkerState(false, OVRPlugin.Qpl.ResultType.Success);

        _client.MarkerStart(markerId, instanceKey, timestampMs, joinId);
    }

    public OVRTelemetryMarker SetResult(OVRPlugin.Qpl.ResultType result)
    {
        State = new OVRTelemetryMarkerState(Sent, result);
        return this;
    }

    public OVRTelemetryMarker AddAnnotation(string annotationKey, string annotationValue)
    {
        if (string.IsNullOrEmpty(annotationKey))
        {
            return this;
        }

        annotationValue ??= string.Empty;

        _client.MarkerAnnotation(MarkerId, annotationKey, annotationValue, InstanceKey);
        return this;
    }

    public OVRTelemetryMarker AddAnnotation(string annotationKey, bool annotationValue)
    {
        if (string.IsNullOrEmpty(annotationKey))
        {
            return this;
        }

        _client.MarkerAnnotation(MarkerId, annotationKey, annotationValue, InstanceKey);
        return this;
    }

    public OVRTelemetryMarker AddAnnotation(string annotationKey, double annotationValue)
    {
        if (string.IsNullOrEmpty(annotationKey))
        {
            return this;
        }

        _client.MarkerAnnotation(MarkerId, annotationKey, annotationValue, InstanceKey);
        return this;
    }

    public OVRTelemetryMarker AddAnnotation(string annotationKey, long annotationValue)
    {
        if (string.IsNullOrEmpty(annotationKey))
        {
            return this;
        }

        _client.MarkerAnnotation(MarkerId, annotationKey, annotationValue, InstanceKey);
        return this;
    }

    public unsafe OVRTelemetryMarker AddAnnotation(string annotationKey, byte** annotationValues, int count)
    {
        if (string.IsNullOrEmpty(annotationKey))
        {
            return this;
        }

        _client.MarkerAnnotation(MarkerId, annotationKey, annotationValues, count, InstanceKey);
        return this;
    }

    public unsafe OVRTelemetryMarker AddAnnotation(string annotationKey, ReadOnlySpan<long> annotationValues)
    {
        fixed (long* ptr = annotationValues)
        {
            return AddAnnotation(annotationKey, ptr, annotationValues.Length);
        }
    }

    public unsafe OVRTelemetryMarker AddAnnotation(string annotationKey, long* annotationValues, int count)
    {
        if (string.IsNullOrEmpty(annotationKey))
        {
            return this;
        }

        _client.MarkerAnnotation(MarkerId, annotationKey, annotationValues, count, InstanceKey);
        return this;
    }

    public unsafe OVRTelemetryMarker AddAnnotation<T>(string annotationKey, ReadOnlySpan<T> annotationValues) where T : unmanaged, Enum
    {
        // If the underlying type is already a long or ulong, we can just cast it.
        var underlyingType = Enum.GetUnderlyingType(typeof(T));
        if (underlyingType == typeof(long) || underlyingType == typeof(ulong))
        {
            fixed (T* values = annotationValues)
            {
                return AddAnnotation(annotationKey, (long*)values, annotationValues.Length);
            }
        }

        // Otherwise, we need to make a copy.
        var longs = new NativeArray<long>(annotationValues.Length, Allocator.Temp);
        try
        {
            for (var i = 0; i < annotationValues.Length; i++)
            {
                longs[i] = UnsafeUtility.EnumToInt(annotationValues[i]);
            }

            return AddAnnotation(annotationKey, (long*)longs.GetUnsafeReadOnlyPtr(), longs.Length);
        }
        finally
        {
            longs.Dispose();
        }
    }

    public unsafe OVRTelemetryMarker AddAnnotation(string annotationKey, ReadOnlySpan<double> annotationValues)
    {
        fixed (double* ptr = annotationValues)
        {
            return AddAnnotation(annotationKey, ptr, annotationValues.Length);
        }
    }

    public unsafe OVRTelemetryMarker AddAnnotation(string annotationKey, double* annotationValues, int count)
    {
        if (string.IsNullOrEmpty(annotationKey))
        {
            return this;
        }

        _client.MarkerAnnotation(MarkerId, annotationKey, annotationValues, count, InstanceKey);
        return this;
    }

    public unsafe OVRTelemetryMarker AddAnnotation(string annotationKey, ReadOnlySpan<OVRPlugin.Bool> annotationValues)
    {
        fixed (OVRPlugin.Bool* ptr = annotationValues)
        {
            return AddAnnotation(annotationKey, ptr, annotationValues.Length);
        }
    }

    public unsafe OVRTelemetryMarker AddAnnotation(string annotationKey, OVRPlugin.Bool* annotationValues, int count)
    {
        if (string.IsNullOrEmpty(annotationKey))
        {
            return this;
        }

        _client.MarkerAnnotation(MarkerId, annotationKey, annotationValues, count, InstanceKey);
        return this;
    }

    public OVRTelemetryMarker AddAnnotationIfNotNullOrEmpty(string annotationKey, string annotationValue)
    {
        return string.IsNullOrEmpty(annotationValue) ? this : AddAnnotation(annotationKey, annotationValue);
    }

    private static string _applicationIdentifier;
    private static string ApplicationIdentifier => _applicationIdentifier ??= Application.identifier;

    private static string _unityVersion;
    private static string UnityVersion => _unityVersion ??= Application.unityVersion;

    private static bool? _isBatchMode;
    private static bool IsBatchMode => _isBatchMode ??= Application.isBatchMode;

    private const string TelemetryEnabledKey = "OVRTelemetry.TelemetryEnabled";

    private bool GetOVRTelemetryConsent()
    {
        const bool defaultTelemetryStatus = false;
#if !UNITY_EDITOR
        return defaultTelemetryStatus;
#else
        return EditorPrefs.GetBool(TelemetryEnabledKey, defaultTelemetryStatus);
#endif
    }

    public OVRTelemetryMarker Send()
    {

        AddAnnotation(OVRTelemetryConstants.OVRManager.AnnotationTypes.ProjectName, ApplicationIdentifier);
        AddAnnotation(OVRTelemetryConstants.OVRManager.AnnotationTypes.ProjectGuid, OVRRuntimeSettings.Instance.TelemetryProjectGuid);
        AddAnnotation(OVRTelemetryConstants.OVRManager.AnnotationTypes.BatchMode, IsBatchMode);
        AddAnnotation(OVRTelemetryConstants.OVRManager.AnnotationTypes.ProcessorType, SystemInfo.processorType);

        State = new OVRTelemetryMarkerState(true, Result);
        _client.MarkerEnd(MarkerId, Result, InstanceKey);

        return this;
    }

    public OVRTelemetryMarker SendIf(bool condition)
    {
        if (condition)
        {
            return Send();
        }

        State = new OVRTelemetryMarkerState(true, Result);
        return this;
    }

    public OVRTelemetryMarker AddPoint(OVRTelemetry.MarkerPoint point)
    {
        _client.MarkerPointCached(MarkerId, point.NameHandle, InstanceKey);
        return this;
    }

    public OVRTelemetryMarker AddPoint(string name)
    {
        _client.MarkerPoint(MarkerId, name, InstanceKey);
        return this;
    }

    public unsafe OVRTelemetryMarker AddPoint(string name, OVRPlugin.Qpl.Annotation.Builder annotationBuilder)
    {
        using var array = annotationBuilder.ToNativeArray();
        return AddPoint(name, (OVRPlugin.Qpl.Annotation*)array.GetUnsafeReadOnlyPtr(), array.Length);
    }

    public unsafe OVRTelemetryMarker AddPoint(string name, OVRPlugin.Qpl.Annotation* annotations, int annotationCount)
    {
        _client.MarkerPoint(MarkerId, name, annotations, annotationCount, InstanceKey);
        return this;
    }

    public void Dispose()
    {
        if (!Sent)
        {
            Send();
        }
    }

}
