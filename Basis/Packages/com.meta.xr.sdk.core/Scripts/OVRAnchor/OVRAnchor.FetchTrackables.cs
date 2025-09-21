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
using static OVRPlugin;

partial struct OVRAnchor
{
    /// <summary>
    /// The type of trackable associated with an anchor, if applicable.
    /// </summary>
    /// <remarks>
    /// A "trackable" is an <see cref="OVRAnchor"/> that can be detected at runtime.
    ///
    /// Use <see cref="OVRAnchor.GetTrackableType"/> to determine an anchor's trackable type.
    /// </remarks>
    public enum TrackableType
    {
        /// <summary>
        /// The anchor is not a trackable.
        /// </summary>
        None,

        /// <summary>
        /// The anchor is a tracked keyboard.
        /// </summary>
        Keyboard,

        /// <summary>
        /// The anchor is a QR Code.
        /// </summary>
        QRCode,
    }

    /// <summary>
    /// Gets the type of trackable this anchor represents, or <see cref="TrackableType.None"/>.
    /// </summary>
    /// <returns>Returns the type of trackable this anchor represents.</returns>
    public TrackableType GetTrackableType()
    {
        using (new OVRObjectPool.ListScope<SpaceComponentType>(out var supportedComponentTypes))
        {
            if (!GetSupportedComponents(supportedComponentTypes))
            {
                return TrackableType.None;
            }

            foreach (var componentType in supportedComponentTypes)
            {
                switch (componentType)
                {
                    case SpaceComponentType.DynamicObject:
                    {
                        var component = GetComponent<OVRDynamicObject>();
                        if (!component.IsEnabled) continue;
                        return component.TrackableType;
                    }
                    case SpaceComponentType.MarkerPayload:
                    {
                        var component = GetComponent<OVRMarkerPayload>();
                        if (component.IsEnabled && component.PayloadType.IsQRCode())
                        {
                            return TrackableType.QRCode;
                        }

                        break;
                    }
                }
            }
        }

        return TrackableType.None;
    }

    private
    static void GetRequiredComponents(IEnumerable<TrackableType> trackableTypes,
        HashSet<TrackableType> trackableTypesOut, HashSet<SpaceComponentType> requiredComponentsOut)
    {
        foreach (var trackableType in trackableTypes.ToNonAlloc())
        {
            trackableTypesOut.Add(trackableType);

            switch (trackableType)
            {
                case TrackableType.Keyboard:
                {
                    requiredComponentsOut.Add(SpaceComponentType.DynamicObject);
                    break;
                }
                case TrackableType.QRCode:
                {
                    requiredComponentsOut.Add(SpaceComponentType.MarkerPayload);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Fetch anchors by <see cref="TrackableType"/>.
    /// </summary>
    /// <remarks>
    /// This method queries for anchors of the given <paramref name="trackableTypes"/>. This method is asynchronous;
    /// use the returned <see cref="OVRTask"/> to check for completion.
    ///
    /// Anchors may be returned in batches. If <paramref name="incrementalResultsCallback"/> is not `null`, then this
    /// delegate is invoked whenever results become available prior to the completion of the entire operation. New
    /// anchors are appended to <paramref name="anchors"/>. The delegate receives a reference to
    /// <paramref name="anchors"/> and the starting index of the anchors that have been added. The parameters are:
    /// - `anchors`: The same `List` provided by <paramref name="anchors"/>.
    /// - `index`: The starting index of the newly available anchors
    /// </remarks>
    /// <param name="anchors">Container to store the results. The list is cleared before adding any anchors.</param>
    /// <param name="trackableTypes">The type of trackables to fetch.</param>
    /// <param name="incrementalResultsCallback">(Optional) A callback invoked when incremental results are available.</param>
    /// <returns>Returns an <see cref="OVRTask"/> that can be used to track the asynchronous fetch.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="anchors"/> is `null`.</exception>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="trackableTypes"/> is `null`.</exception>
    public static async OVRTask<OVRResult<List<OVRAnchor>, FetchResult>> FetchTrackablesAsync(List<OVRAnchor> anchors,
        IEnumerable<TrackableType> trackableTypes, Action<List<OVRAnchor>, int> incrementalResultsCallback = null)
    {
        if (anchors == null)
            throw new ArgumentNullException(nameof(anchors));

        if (trackableTypes == null)
            throw new ArgumentNullException(nameof(trackableTypes));

        anchors.Clear();

        using (new OVRObjectPool.HashSetScope<TrackableType>(out var trackableTypeSet))
        using (new OVRObjectPool.HashSetScope<SpaceComponentType>(out var requiredComponents))
        {
            GetRequiredComponents(trackableTypes, trackableTypeSet, requiredComponents);

            using (new OVRObjectPool.TaskScope<Result>(out var tasks, out var results))
            {
                foreach (var requiredComponent in requiredComponents)
                {
                    tasks.Add(QuerySingleComponentAsync(anchors, trackableTypeSet, requiredComponent,
                        incrementalResultsCallback));
                }

                foreach (var result in await OVRTask.WhenAll(tasks, results))
                {
                    if (!result.IsSuccess())
                    {
                        return OVRResult.From(anchors, (FetchResult)result);
                    }
                }
            }

            return OVRResult.From(anchors, FetchResult.Success);
        }

        static async OVRTask<Result> QuerySingleComponentAsync(List<OVRAnchor> anchors,
            HashSet<TrackableType> trackableTypes,
            SpaceComponentType componentType, Action<List<OVRAnchor>, int> incrementalResultsCallback)
        {
            using (new OVRObjectPool.ListScope<OVRAnchor>(out var anchorsWithComponent))
            {
                var query = OVRSpaceQuery.ForComponentUnchecked(componentType);
                query.Location = SpaceStorageLocation.Local;

                var result = await FetchAnchors(anchorsWithComponent, query);
                if (!result.IsSuccess())
                {
                    return result;
                }

                var startingIndex = anchors.Count;
                foreach (var anchor in anchorsWithComponent)
                {
                    if (DoesComponentMatchTrackableType(trackableTypes, anchor, componentType))
                    {
                        anchors.Add(anchor);
                    }
                }

                if (startingIndex != anchors.Count)
                {
                    incrementalResultsCallback?.Invoke(anchors, startingIndex);
                }

                return result;
            }
        }

        static bool DoesComponentMatchTrackableType(HashSet<TrackableType> trackableTypes, OVRAnchor anchor,
            SpaceComponentType componentType)
        {
            switch (componentType)
            {
                case SpaceComponentType.DynamicObject:
                {
                    return trackableTypes.Contains(anchor.GetComponent<OVRDynamicObject>().TrackableType);
                }
                case SpaceComponentType.MarkerPayload:
                {
                    var component = anchor.GetComponent<OVRMarkerPayload>();
                    if (component.PayloadType.IsQRCode())
                    {
                        return trackableTypes.Contains(TrackableType.QRCode);
                    }
                    break;
                }
            }

            return false;
        }
    }
}
