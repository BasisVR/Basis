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
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using static OVRPlugin;
using TaskResult = OVRResult<System.Collections.Generic.List<OVRAnchor>, OVRAnchor.FetchResult>;

/// <summary>
/// Represents an anchor.
/// </summary>
/// <remarks>
/// An <see cref="OVRAnchor"/> can represent either a
/// ["spatial anchor"](https://developer.oculus.com/documentation/unity/unity-spatial-anchors-overview/),
/// which is created and managed by the app, or a
/// ["scene anchor"](https://developer.oculus.com/documentation/unity/unity-scene-overview/),
/// which is created and managed by the system and used to represent the Scene Model.
///
/// This is a low-level, lightweight interface to access anchors. For more Unity-friendly components, see
/// <see cref="OVRSpatialAnchor"/> (for spatial anchors) or
/// [Access Scene data with OVRAnchor](https://developer.oculus.com/documentation/unity/unity-scene-ovranchor/) for Scene.
///
/// This API gives you access to all anchor functionality:
/// - <see cref="FetchAnchorsAsync(List{OVRAnchor},OVRAnchor.FetchOptions,Action{List{OVRAnchor},int})"/>: Query for anchors by UUID or by component types.
/// - <see cref="CreateSpatialAnchorAsync(Pose)"/>: Create a new spatial anchor (app owned)
/// - <see cref="GetComponent{T}"/>: Access a component (for example, plane or volume data) associated with an anchor.
/// - <see cref="ShareAsync(IEnumerable{OVRSpaceUser})"/>: Shares an anchor with one or more users.
/// - <see cref="SaveAsync()"/>: Saves the anchor to persistent storage.
/// - <see cref="EraseAsync()"/>: Erases the anchor from persistent storage.
/// - <see cref="Dispose"/>: Destroys the runtime instance of the anchor.
/// </remarks>
public readonly partial struct OVRAnchor : IEquatable<OVRAnchor>, IDisposable
{
    /// <summary>
    /// Possible results of a save operation.
    /// </summary>
    /// <remarks>
    /// Saving an anchor is an asynchronous operation that can fail for a number of reasons, enumerated here.
    ///
    /// <see cref="SaveResult"/> is used as the status for the <see cref="OVRResult"/> returned by
    /// <see cref="OVRAnchor.SaveAsync()"/>,
    /// <see cref="OVRAnchor.SaveAsync(IEnumerable{OVRAnchor})"/>,
    /// <see cref="OVRSpatialAnchor.SaveAnchorAsync"/>, and
    /// <see cref="OVRSpatialAnchor.SaveAnchorsAsync(IEnumerable{OVRSpatialAnchor})"/>.
    /// </remarks>
    /// <seealso cref="OVRAnchor.SaveAsync()"/>
    /// <seealso cref="OVRAnchor.SaveAsync(IEnumerable{OVRAnchor})"/>
    [OVRResultStatus]
    public enum SaveResult
    {
        /// <summary>
        /// The operation succeeded.
        /// </summary>
        Success = Result.Success,

        /// <summary>
        /// The operation failed in an unexpected way.
        /// </summary>
        Failure = Result.Failure,

        /// <summary>
        /// At least one anchor is invalid.
        /// </summary>
        /// <remarks>
        /// An <see cref="OVRAnchor"/> is invalid if it is default constructed,
        /// which is often the case before an <see cref="OVRSpatialAnchor"/> gets properly bound, or before it has
        /// a chance to invoke its <c>Start()</c> method.
        /// </remarks>
        FailureInvalidAnchor = Result.Failure_HandleInvalid,

        /// <summary>
        /// Typically indicates an uninitialized <see cref="OVRResult"/>, or a pending <see cref="OVRTask"/> which
        /// expected to have internal result data of a specific type set.
        /// </summary>
        FailureDataIsInvalid = Result.Failure_DataIsInvalid,

        /// <summary>
        /// Resource limitation prevented this operation from executing.
        /// </summary>
        /// <remarks>
        ///  Recommend retrying, perhaps after a short delay and/or reducing memory consumption.
        /// </remarks>
        FailureInsufficientResources = Result.Failure_SpaceInsufficientResources,

        /// <summary>
        /// The amount of device storage available for anchor data is insufficient for the requested save operation.
        /// </summary>
        /// <remarks>
        /// You can request that the user frees up space on their device, or your app can attempt to free up unused
        /// space / anchors under its control before retrying. You may also find partial success saving anchors in
        /// smaller batches, if not individually. However, anchors do have a relatively small disk footprint, typically
        /// occupying a single 4 kibibyte block per each.
        /// </remarks>
        FailureStorageAtCapacity = Result.Failure_SpaceStorageAtCapacity,

        /// <summary>
        /// Device's view of the physical space is insufficient.
        /// </summary>
        /// <remarks>
        /// The user needs to look around the environment more for anchor tracking to function.
        /// </remarks>
        FailureInsufficientView = Result.Failure_SpaceInsufficientView,

        /// <summary>
        /// User has not granted all the required permissions for the app to use this API.
        /// </summary>
        /// <remarks>
        /// You should confirm the status of the permission(s) needed for using anchor APIs, namely:
        /// <ul>
        /// <li><c>"com.oculus.permission.USE_ANCHOR_API"</c></li>
        /// </ul>
        /// This is handled by checking that your OculusProjectConfig asset enables "Anchor Support", and by
        /// subsequently running the Unity menu bar item "Meta > Tools > Update AndroidManifest.xml".
        /// </remarks>
        FailurePermissionInsufficient = Result.Failure_SpacePermissionInsufficient,

        /// <summary>
        /// Operation canceled due to rate limiting.
        /// </summary>
        /// <remarks>
        /// Your app is sending too many requests in a short amount of time. You should ensure that your request logic
        /// is well-formed and the number of outgoing requests is in range of what you expect. If everything is as you
        /// intended, it is recommended that you retry <see cref="FailureRateLimited"/> request(s) after several seconds
        /// of delay.
        /// </remarks>
        FailureRateLimited = Result.Failure_SpaceRateLimited,

        /// <summary>
        /// The environment is too dark to save the anchor.
        /// </summary>
        FailureTooDark = Result.Failure_SpaceTooDark,

        /// <summary>
        /// The environment is too bright to save the anchor.
        /// </summary>
        FailureTooBright = Result.Failure_SpaceTooBright,

        /// <summary>
        /// Save is not supported on this version or platform.
        /// </summary>
        FailureUnsupported = Result.Failure_Unsupported,

        /// <summary>
        /// One or more anchors do not have the <see cref="OVRStorable"/> component enabled, causing the save operation
        /// to fail.
        /// </summary>
        FailurePersistenceNotEnabled = Result.Failure_SpaceComponentNotEnabled,
    }

    /// <summary>
    /// Possible results of an erase operation.
    /// </summary>
    /// <remarks>
    /// Saving an anchor is an asynchronous operation that can fail for a number of reasons, enumerated here.
    ///
    /// <see cref="EraseResult"/> is used as the status for the <see cref="OVRResult"/> returned by
    /// <see cref="OVRAnchor.EraseAsync()"/>,
    /// <see cref="OVRAnchor.EraseAsync(IEnumerable{OVRAnchor},IEnumerable{Guid})"/>,
    /// <see cref="OVRSpatialAnchor.EraseAnchorAsync"/>, and
    /// <see cref="OVRSpatialAnchor.EraseAnchorsAsync(IEnumerable{OVRSpatialAnchor},IEnumerable{Guid})"/>.
    /// </remarks>
    /// <seealso cref="OVRAnchor.EraseAsync()"/>
    /// <seealso cref="OVRAnchor.EraseAsync(IEnumerable{OVRAnchor},IEnumerable{Guid})"/>
    [OVRResultStatus]
    public enum EraseResult
    {
        /// <summary>
        /// The operation succeeded.
        /// </summary>
        Success = Result.Success,

        /// <summary>
        /// The operation failed in an unexpected way.
        /// </summary>
        Failure = Result.Failure,

        /// <summary>
        /// At least one anchor is invalid.
        /// </summary>
        /// <remarks>
        /// An <see cref="OVRAnchor"/> is invalid if it is default constructed,
        /// which is often the case before an <see cref="OVRSpatialAnchor"/> gets properly bound, or before it has
        /// a chance to invoke its <c>Start()</c> method.
        /// </remarks>
        FailureInvalidAnchor = Result.Failure_HandleInvalid,

        /// <summary>
        /// Typically indicates an uninitialized <see cref="OVRResult"/>, or a pending <see cref="OVRTask"/> which
        /// expected to have internal result data of a specific type set.
        /// </summary>
        FailureDataIsInvalid = Result.Failure_DataIsInvalid,

        /// <summary>
        /// Resource limitation prevented this operation from executing.
        /// </summary>
        /// <remarks>
        ///  Recommend retrying, perhaps after a short delay and/or reducing memory consumption.
        /// </remarks>
        FailureInsufficientResources = Result.Failure_SpaceInsufficientResources,

        /// <summary>
        /// User has not granted all the required permissions for the app to use this API.
        /// </summary>
        /// <remarks>
        /// You should confirm the status of the permission(s) needed for using anchor APIs, namely:
        /// <ul>
        /// <li><c>"com.oculus.permission.USE_ANCHOR_API"</c></li>
        /// </ul>
        /// This is handled by checking that your OculusProjectConfig asset enables "Anchor Support", and by
        /// subsequently running the Unity menu bar item "Meta > Tools > Update AndroidManifest.xml".
        /// </remarks>
        FailurePermissionInsufficient = Result.Failure_SpacePermissionInsufficient,

        /// <summary>
        /// Operation canceled due to rate limiting.
        /// </summary>
        /// <remarks>
        /// Your app is sending too many requests in a short amount of time. You should ensure that your request logic
        /// is well-formed and the number of outgoing requests is in range of what you expect. If everything is as you
        /// intended, it is recommended that you retry <see cref="FailureRateLimited"/> request(s) after several seconds
        /// of delay.
        /// </remarks>
        FailureRateLimited = Result.Failure_SpaceRateLimited,

        /// <summary>
        /// Erase is not supported on this version or platform.
        /// </summary>
        FailureUnsupported = Result.Failure_Unsupported,

        /// <summary>
        /// One or more anchors do not have the <see cref="OVRStorable"/> component enabled, causing the erase operation
        /// to fail.
        /// </summary>
        FailurePersistenceNotEnabled = Result.Failure_SpaceComponentNotEnabled,
    }

    /// <summary>
    /// Possible results of a fetch operation.
    /// </summary>
    /// <remarks>
    /// Use <see cref="OVRAnchor.FetchAnchorsAsync(System.Collections.Generic.List{OVRAnchor},OVRAnchor.FetchOptions,System.Action{System.Collections.Generic.List{OVRAnchor},int})"/>
    /// to query for anchors. When that operation completes, use the resulting status code to determine whether the operation succeeded,
    /// or why it failed.
    /// </remarks>
    [OVRResultStatus]
    public enum FetchResult
    {
        /// <summary>
        /// The operation succeeded.
        /// </summary>
        Success = Result.Success,

        /// <summary>
        /// The operation failed in an unexpected way.
        /// </summary>
        Failure = Result.Failure,

        /// <summary>
        /// Typically indicates an uninitialized <see cref="OVRResult"/>, or a pending <see cref="OVRTask"/> which
        /// expected to have internal result data set.
        /// </summary>
        FailureDataIsInvalid = Result.Failure_DataIsInvalid,

        /// <summary>
        /// One of the <see cref="FetchOptions"/> was invalid.
        /// </summary>
        /// <remarks>
        /// This can happen, for example, if you query for an invalid component type, or if you try requesting more than
        /// <see cref="OVRSpaceQuery.MaxResultsForAnchors"/> anchors in a single call.
        /// </remarks>
        FailureInvalidOption = Result.Failure_InvalidParameter,

        /// <summary>
        /// Resource limitation prevented this operation from executing.
        /// </summary>
        /// <remarks>
        ///  Recommend retrying, perhaps after a short delay and/or reducing memory consumption.
        /// </remarks>
        FailureInsufficientResources = Result.Failure_SpaceInsufficientResources,

        /// <summary>
        /// Device's view of the physical space is insufficient.
        /// </summary>
        /// <remarks>
        /// The user needs to look around the environment more for anchor tracking to function.
        /// </remarks>
        FailureInsufficientView = Result.Failure_SpaceInsufficientView,

        /// <summary>
        /// User has not granted all the required permissions for the app to use this API.
        /// </summary>
        /// <remarks>
        /// You should confirm the status of the permission(s) needed for using anchor APIs, namely:
        /// <ul>
        /// <li><c>"com.oculus.permission.USE_ANCHOR_API"</c></li>
        /// <li><c>"com.oculus.permission.IMPORT_EXPORT_IOT_MAP_DATA"</c> (only required for fetching shared anchors)</li>
        /// </ul>
        /// This is handled by checking that your OculusProjectConfig asset enables "Anchor Support" and/or
        /// "Anchor and Space Sharing Support", and by subsequently running the Unity menu bar item
        /// "Meta > Tools > Update AndroidManifest.xml".
        /// </remarks>
        FailurePermissionInsufficient = Result.Failure_SpacePermissionInsufficient,

        /// <summary>
        /// Operation canceled due to rate limiting.
        /// </summary>
        /// <remarks>
        /// Your app is sending too many requests in a short amount of time. You should ensure that your request logic
        /// is well-formed and the number of outgoing requests is in range of what you expect. If everything is as you
        /// intended, it is recommended that you retry <see cref="FailureRateLimited"/> request(s) after several seconds
        /// of delay.
        /// </remarks>
        FailureRateLimited = Result.Failure_SpaceRateLimited,

        /// <summary>
        /// The environment is too dark to load anchors.
        /// </summary>
        FailureTooDark = Result.Failure_SpaceTooDark,

        /// <summary>
        /// The environment is too bright to load anchors.
        /// </summary>
        FailureTooBright = Result.Failure_SpaceTooBright,

        /// <summary>
        /// Fetch is not supported in this version or on this platform.
        /// </summary>
        FailureUnsupported = Result.Failure_Unsupported,
    }

    /// <summary>
    /// Possible results of a share operation.
    /// </summary>
    /// <remarks>
    /// Sharing an anchor is an asynchronous operation that can fail for a number of reasons, enumerated here.
    ///
    /// <see cref="ShareResult"/> is used as the status for the <see cref="OVRResult"/> returned by
    /// <see cref="OVRAnchor.ShareAsync(IEnumerable{OVRSpaceUser})"/>,
    /// <see cref="OVRAnchor.ShareAsync(IEnumerable{OVRAnchor},IEnumerable{OVRSpaceUser})"/>,
    /// <see cref="OVRSpatialAnchor.ShareAsync(Guid)"/>,
    /// <see cref="OVRSpatialAnchor.ShareAsync(IEnumerable{OVRSpatialAnchor}, Guid)"/>, and
    /// <see cref="OVRSpatialAnchor.ShareAsync(IEnumerable{OVRSpatialAnchor}, IEnumerable{Guid})"/>
    /// </remarks>
    /// <seealso cref="OVRAnchor.ShareAsync(IEnumerable{OVRSpaceUser})"/>
    /// <seealso cref="OVRAnchor.ShareAsync(IEnumerable{OVRAnchor},IEnumerable{OVRSpaceUser})"/>
    /// <seealso cref="OVRSpatialAnchor.ShareAsync(IEnumerable{OVRSpatialAnchor}, Guid)"/>
    /// <seealso cref="OVRSpatialAnchor.ShareAsync(IEnumerable{OVRSpatialAnchor}, IEnumerable{Guid})"/>
    [OVRResultStatus]
    public enum ShareResult
    {
        /// <summary>
        /// The operation succeeded.
        /// </summary>
        Success = Result.Success,

        /// <summary>
        /// The operation failed in an unexpected way.
        /// </summary>
        Failure = Result.Failure,

        /// <summary>
        /// The operation failed for unspecified reasons.
        /// </summary>
        /// <remarks>
        /// Although distinct from <see cref="FailureInvalidParameter"/>, this result can often indicate something is
        /// wrong with your input parameters; the OVR backend was unable to distinguish this.
        /// </remarks>
        FailureOperationFailed = Result.Failure_OperationFailed,

        /// <summary>
        /// API call was given an invalid parameter.
        /// </summary>
        /// <remarks>
        /// Try ensuring that any <see cref="System.Guid"/> parameters are not <see cref="Guid.Empty"/> (aka default),
        /// and that any <see cref="IEnumerable{T}"/> collections are not empty.
        /// </remarks>
        FailureInvalidParameter = Result.Failure_InvalidParameter,

        /// <summary>
        /// One or more invalid handles were provided to the API.
        /// </summary>
        /// <remarks>
        /// This usually refers to anchor handles. An <see cref="OVRAnchor"/> is invalid if it is default constructed,
        /// which is often the case before an <see cref="OVRSpatialAnchor"/> gets properly bound, or before it has
        /// a chance to invoke its <c>Start()</c> method.
        /// </remarks>
        FailureHandleInvalid = Result.Failure_HandleInvalid,

        /// <summary>
        /// Typically indicates an uninitialized <see cref="OVRResult"/>, or a pending <see cref="OVRTask"/> which
        /// expected to have internal result data of a specific type set.
        /// </summary>
        FailureDataIsInvalid = Result.Failure_DataIsInvalid,

        /// <summary>
        /// A network timeout occurred.
        /// </summary>
        /// <remarks>
        /// Ensure your network connectivity is stable, and check that you aren't being blocked or limited by firewalls,
        /// custom DNS, VPN, etc.
        /// </remarks>
        FailureNetworkTimeout = Result.Failure_SpaceNetworkTimeout,

        /// <summary>
        /// Network request failed.
        /// </summary>
        /// <remarks>
        /// Recommend ensuring network connectivity.
        /// </remarks>
        FailureNetworkRequestFailed = Result.Failure_SpaceNetworkRequestFailed,

        /// <summary>
        /// The device has not built a sufficient map of the environment to save the anchor(s).
        /// </summary>
        /// <remarks>
        /// Users should move and look around their space some more, and ensure their environment is sufficiently lit,
        /// before retrying.
        /// </remarks>
        FailureMappingInsufficient = Result.Failure_SpaceMappingInsufficient,

        /// <summary>
        /// The device was not able to localize the anchor(s) being shared.
        /// </summary>
        /// <remarks>
        /// Make sure that the <see cref="OVRLocatable"/> component has been added and enabled on this anchor before attempting to share.
        /// </remarks>
        FailureLocalizationFailed = Result.Failure_SpaceLocalizationFailed,

        /// <summary>
        /// Sharable component not enabled on the anchor(s) being shared.
        /// </summary>
        /// <remarks>
        /// Make sure that the <see cref="OVRSharable"/> component has been added and enabled on this anchor before attempting to share.
        /// </remarks>
        FailureSharableComponentNotEnabled = Result.Failure_SpaceComponentNotEnabled,

        /// <summary>
        /// Sharing failed because the user has not enabled the "Share Point Cloud Data" setting.
        /// </summary>
        /// <remarks>
        /// Users can enable this setting in OS Settings &gt; Privacy and Safety &gt; Device Permissions
        /// &gt; Share Point Cloud Data.
        /// <br/><br/>
        /// Once per app launch, the OS may also attempt to provide users a permission request popup over your app when
        /// this result is about to be returned. If the user acquiesces, <see cref="Success"/> would be returned instead
        /// of <see cref="FailureCloudStorageDisabled"/> once your app regains focus.
        /// </remarks>
        FailureCloudStorageDisabled = Result.Failure_SpaceCloudStorageDisabled,

        /// <summary>
        /// User has not granted all the required permissions for the app to use this API.
        /// </summary>
        /// <remarks>
        /// You should confirm the status of the permission(s) needed for using anchor APIs, namely:
        /// <ul>
        /// <li><c>"com.oculus.permission.USE_ANCHOR_API"</c></li>
        /// <li><c>"com.oculus.permission.IMPORT_EXPORT_IOT_MAP_DATA"</c> (required for sharing)</li>
        /// </ul>
        /// This is handled by checking that your OculusProjectConfig asset enables "Anchor and Space Sharing Support",
        /// and by subsequently running the Unity menu bar item "Meta > Tools > Update AndroidManifest.xml".
        /// </remarks>
        FailurePermissionInsufficient = Result.Failure_SpacePermissionInsufficient,

        /// <summary>
        /// Anchor Sharing is not supported with this version or on this platform.
        /// </summary>
        FailureUnsupported = Result.Failure_Unsupported,
    }

    #region Static

    /// <summary>
    /// Represents a `null` anchor
    /// </summary>
    /// <remarks>
    /// Because <see cref="OVRAnchor"/> is a value-type, it is not C#-nullable. However, if you default-construct an
    /// <see cref="OVRAnchor"/> (instead of obtaining one from a query or creation method) then it will be equal to
    /// <see cref="Null"/>. You can test for null by comparing against <see cref="Null"/>:
    /// <example><code><![CDATA[
    /// async void CreateAnchor(Pose pose) {
    ///   var anchor = await OVRAnchor.CreateSpatialAnchorAsync(pose);
    ///   if (anchor == OVRAnchor.Null) {
    ///     Debug.LogError("Anchor creation failed!");
    ///   } else {
    ///     // anchor is valid
    ///   }
    /// }
    /// ]]></code></example>
    /// </remarks>
    public static readonly OVRAnchor Null = new(0, Guid.Empty);

    // Called by OVRManager event loop
    internal static void OnSpaceDiscoveryComplete(OVRDeserialize.SpaceDiscoveryCompleteData data)
    {
        TaskResult result;
        if (!OVRTask.TryGetPendingTask<TaskResult>(data.RequestId, out var task))
        {
            // Not for us; someone else initiated this request.
            return;
        }

        if (task.TryGetInternalData<FetchTaskData>(out var taskData))
        {
            Telemetry.GetMarker(Telemetry.MarkerId.DiscoverSpaces, data.RequestId)
                ?.AddAnnotation(Telemetry.Annotation.ResultsCount, taskData.Anchors?.Count ?? 0);
            result = OVRResult.From(taskData.Anchors, (FetchResult)data.Result);
        }
        else
        {
            Debug.LogError($"SpaceDiscovery completed but its task does not have an associated anchor List. " +
                           $"RequestId={data.RequestId}, Result={data.Result}");
            result = OVRResult.From((List<OVRAnchor>)null, (FetchResult)data.Result);
        }

        Telemetry.SetAsyncResultAndSend(Telemetry.MarkerId.DiscoverSpaces, data.RequestId, data.Result);

        task.SetResult(result);
    }

    // Called by OVRManager event loop
    internal static void OnSpaceDiscoveryResultsAvailable(OVRDeserialize.SpaceDiscoveryResultsData data)
    {
        var requestId = data.RequestId;

        // never calls task.SetResult() as that completes the task
        if (!OVRTask.TryGetPendingTask<TaskResult>(requestId, out var task))
            return;

        if (!task.TryGetInternalData<FetchTaskData>(out var taskData))
            return;

        NativeArray<SpaceDiscoveryResult> results = default;
        Result result;
        int count;

        unsafe
        {
            result = RetrieveSpaceDiscoveryResults(requestId, null, 0, out count);
            if (!result.IsSuccess()) return;

            do
            {
                if (results.IsCreated)
                {
                    results.Dispose();
                }

                results = new NativeArray<SpaceDiscoveryResult>(count, Allocator.Temp);
                result = RetrieveSpaceDiscoveryResults(requestId, (SpaceDiscoveryResult*)results.GetUnsafePtr(),
                    results.Length, out count);
            } while (result == Result.Failure_InsufficientSize);
        }

        var startingIndex = taskData.Anchors.Count;

        using (results)
        {
            if (!result.IsSuccess() || count == 0)
            {
                return;
            }

            // always add to anchors, as the results are consumed
            for (var i = 0; i < count; i++)
            {
                var item = results[i];
                taskData.Anchors.Add(new OVRAnchor(item.Space, item.Uuid));
            }
        }

        // notify potential subscribers to the incremental results
        taskData.IncrementalResultsCallback?.Invoke(taskData.Anchors, startingIndex);
    }

    /// \cond
    private struct FetchTaskData
    {
        public List<OVRAnchor> Anchors;
        public Action<List<OVRAnchor>, int> IncrementalResultsCallback;
    }
    /// \endcond

    /// <summary>
    /// Fetch anchors matching a query.
    /// </summary>
    /// <remarks>
    /// This method queries for anchors that match the corresponding <paramref name="options"/>. This method is
    /// asynchronous; use the returned <see cref="OVRTask"/> to check for completion.
    ///
    /// Anchors may be returned in batches. If <paramref name="incrementalResultsCallback"/> is not `null`, then this
    /// delegate is invoked whenever results become available prior to the completion of the entire operation. New anchors
    /// are appended to <paramref name="anchors"/>. The delegate receives a reference to <paramref name="anchors"/> and
    /// the starting index of the anchors that have been added. The parameters are:
    /// - `anchors`: The same `List` provided by <paramref name="anchors"/>.
    /// - `index`: The starting index of the newly available anchors
    /// </remarks>
    /// <param name="anchors">A buffer to store the results.
    /// This container is cleared before any async requests are made.
    /// </param>
    /// <param name="options">Options describing which anchors to fetch.</param>
    /// <param name="incrementalResultsCallback">(Optional) A callback invoked when incremental results are available.</param>
    /// <returns>Returns an <see cref="OVRTask"/> that can be used to track the asynchronous fetch.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="anchors"/> is `null`.</exception>
    public static OVRTask<TaskResult> FetchAnchorsAsync(
        List<OVRAnchor> anchors, FetchOptions options, Action<List<OVRAnchor>, int> incrementalResultsCallback = null)
    {
        if (anchors == null)
            throw new ArgumentNullException(nameof(anchors));

        anchors.Clear();

        return OVRTask.Build(
            options.DiscoverSpaces(out var requestId), requestId)
            .ToTask<List<OVRAnchor>, FetchResult>()
            .WithInternalData(new FetchTaskData
            {
                Anchors = anchors,
                IncrementalResultsCallback = incrementalResultsCallback,
            });
    }

    /// <summary>
    /// Loads all anchors shared with a group by its UUID.
    /// <seealso cref="OVRSpatialAnchor.LoadUnboundSharedAnchorsAsync(Guid,List{OVRSpatialAnchor.UnboundAnchor})"/>
    /// </summary>
    /// <param name="groupUuid">
    /// The group UUID from which to load any associated shared anchors.
    /// <seealso cref="ShareAsync(IEnumerable{OVRAnchor},Guid)"/>
    /// </param>
    /// <param name="anchors">
    /// A non-null buffer to store the loaded anchors. This container is cleared before being populated.
    /// </param>
    /// <returns>
    /// Returns an <see cref="OVRResult"/>&lt;<see cref="List{OVRAnchor}"/>,<see cref="FetchResult"/>&gt;,
    /// which indicates the status of the load operation, as well as returning a now-populated reference to the
    /// <paramref name="anchors"/> buffer list originally provided to this call.
    /// <br/>
    /// This result's Status will be <see cref="FetchResult.FailureInvalidOption"/> if <paramref name="groupUuid"/>
    /// is <see cref="Guid.Empty"/>.
    /// </returns>
    /// <remarks>
    /// This method is asynchronous. The returned <see cref="OVRTask"/> wrapper completes when all results are
    /// available.
    /// <br/><br/>
    /// In order to be loaded, the anchor must have previously been shared with the group, e.g., with
    /// <see cref="ShareAsync(IEnumerable{OVRAnchor}, Guid)"/> or <see cref="ShareAsync(Guid)"/>.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="anchors"/> is null.</exception>
    public static async OVRTask<TaskResult> FetchSharedAnchorsAsync(
        Guid groupUuid,
        List<OVRAnchor> anchors)
    {
        if (anchors == null)
        {
            throw new ArgumentNullException(nameof(anchors));
        }

        var query = OVRSpaceQuery.ForGroupThrow(groupUuid, nameof(groupUuid));

        return OVRResult.From(anchors, (FetchResult)(await FetchAnchors(anchors, query)));
    }

    /// <summary>
    /// Loads all anchors shared with a group by its UUID.
    /// <seealso cref="OVRSpatialAnchor.LoadUnboundSharedAnchorsAsync(Guid,IEnumerable{Guid},List{OVRSpatialAnchor.UnboundAnchor})"/>
    /// </summary>
    /// <param name="groupUuid">
    /// The group UUID from which to load any associated shared anchors.
    /// <seealso cref="ShareAsync(IEnumerable{OVRAnchor},Guid)"/>
    /// </param>
    /// <param name="allowedAnchorUuids">
    /// A non-null, non-empty set of known anchor UUIDs to load from the group.
    /// They will not be loaded if:
    /// - they never existed
    /// - they've been erased from cloud storage
    /// - they were never shared to the given <paramref name="groupUuid"/>
    /// Any anchor not specified will be omitted from the results in <paramref name="anchors"/>.
    /// <br/>
    /// The elements in this set will NOT be individually validated; you should be sure that none of them are
    /// <see cref="Guid.Empty"/> before calling this API.
    /// </param>
    /// <param name="anchors">
    /// A non-null buffer to store the loaded anchors.
    /// This container is always cleared unless an exception is thrown.
    /// </param>
    /// <returns>
    /// Returns an <see cref="OVRResult"/>&lt;<see cref="List{OVRAnchor}"/>,<see cref="FetchResult"/>&gt;,
    /// which indicates the status of the load operation, as well as returning a now-populated reference to the
    /// <paramref name="anchors"/> buffer list originally provided to this call.
    /// <br/>
    /// This result's Status will be <see cref="FetchResult.FailureInvalidOption"/> if <paramref name="groupUuid"/>
    /// is <see cref="Guid.Empty"/>, or <paramref name="allowedAnchorUuids"/> is larger than
    /// <see cref="OVRSpaceQuery.MaxResultsForAnchors"/>.
    /// </returns>
    /// <remarks>
    /// This method is asynchronous. The returned <see cref="OVRTask"/> wrapper completes when all results are
    /// available.
    /// <br/><br/>
    /// In order to be loaded, the anchor must have previously been shared with the group, e.g., with
    /// <see cref="ShareAsync(IEnumerable{OVRAnchor}, Guid)"/> or <see cref="ShareAsync(Guid)"/>.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    ///     Thrown if either <paramref name="allowedAnchorUuids"/> or <paramref name="anchors"/> is null.
    /// </exception>
    public static async OVRTask<TaskResult> FetchSharedAnchorsAsync(
        Guid groupUuid,
        IEnumerable<Guid> allowedAnchorUuids,
        List<OVRAnchor> anchors)
    {
        if (allowedAnchorUuids == null)
        {
            throw new ArgumentNullException(nameof(allowedAnchorUuids));
        }
        if (anchors == null)
        {
            throw new ArgumentNullException(nameof(anchors));
        }

        var query = OVRSpaceQuery.ForGroupThrow(groupUuid, nameof(groupUuid), allowedAnchorUuids);

        return OVRResult.From(anchors, (FetchResult)(await FetchAnchors(anchors, query)));
    }

    /// <summary>
    /// Creates a new spatial anchor.
    /// </summary>
    /// <remarks>
    /// Spatial anchor creation is asynchronous. This method initiates a request to create a spatial anchor at
    /// <paramref name="trackingSpacePose"/>. The returned <see cref="OVRTask"/>&lt;<see cref="OVRAnchor"/>&gt; can be awaited or used to
    /// track the completion of the request.
    ///
    /// If spatial anchor creation fails, the resulting <see cref="OVRAnchor"/> will be <see cref="OVRAnchor.Null"/>.
    /// </remarks>
    /// <param name="trackingSpacePose">The pose, in tracking space, at which you wish to create the spatial anchor.</param>
    /// <returns>A task which can be used to track completion of the request.</returns>
    public static OVRTask<OVRAnchor> CreateSpatialAnchorAsync(Pose trackingSpacePose) => OVRTask
        .Build(
            CreateSpatialAnchor(new SpatialAnchorCreateInfo
            {
                BaseTracking = GetTrackingOriginType(),
                PoseInSpace = new Posef
                {
                    Orientation = trackingSpacePose.rotation.ToFlippedZQuatf(),
                    Position = trackingSpacePose.position.ToFlippedZVector3f(),
                },
                Time = GetTimeInSeconds(),
            }, out var requestId), requestId)
        .ToTask(Null);

    /// <summary>
    /// Creates a new spatial anchor.
    /// </summary>
    /// <remarks>
    /// Spatial anchor creation is asynchronous. This method initiates a request to create a spatial anchor at
    /// <paramref name="transform"/>. The returned <see cref="OVRTask"/>&lt;<see cref="OVRAnchor"/>&gt; can be awaited or used to
    /// track the completion of the request.
    ///
    /// If spatial anchor creation fails, the resulting <see cref="OVRAnchor"/> will be <see cref="OVRAnchor.Null"/>.
    /// </remarks>
    /// <param name="transform">The transform at which you wish to create the spatial anchor.</param>
    /// <param name="centerEyeCamera">The `Camera` associated with the Meta Quest's center eye.</param>
    /// <returns>A task which can be used to track completion of the request.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="transform"/> is `null`.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="centerEyeCamera"/> is `null`.</exception>
    public static OVRTask<OVRAnchor> CreateSpatialAnchorAsync(Transform transform, Camera centerEyeCamera)
    {
        if (transform == null)
            throw new ArgumentNullException(nameof(transform));

        if (centerEyeCamera == null)
            throw new ArgumentNullException(nameof(centerEyeCamera));

        var pose = transform.ToTrackingSpacePose(centerEyeCamera);
        return CreateSpatialAnchorAsync(new Pose
        {
            position = pose.position,
            rotation = pose.orientation,
        });
    }

    #endregion

    /// <summary>
    /// Save this anchor.
    /// </summary>
    /// <remarks>
    /// This method persists the anchor so that it may be retrieved later, e.g., by using
    /// <see cref="FetchAnchorsAsync(List{OVRAnchor},FetchOptions,Action{List{OVRAnchor},int})"/>.
    ///
    /// This operation is asynchronous. Use the returned <see cref="OVRTask"/> to track the result of the
    /// asynchronous operation.
    ///
    /// NOTE: When saving multiple anchors, it is more efficient to save them in a batch using
    /// <see cref="SaveAsync(IEnumerable{OVRAnchor})"/>.
    /// </remarks>
    /// <returns>An awaitable <see cref="OVRTask"/> representing the asynchronous request.</returns>
    /// <seealso cref="FetchAnchorsAsync(List{OVRAnchor},FetchOptions,Action{List{OVRAnchor},int})"/>
    /// <seealso cref="SaveAsync(IEnumerable{OVRAnchor})"/>
    /// <seealso cref="EraseAsync()"/>
    public OVRTask<OVRResult<SaveResult>> SaveAsync()
    {
        var handle = Handle;
        unsafe
        {
            return SaveSpacesAsync(new(&handle, 1));
        }
    }

    /// <summary>
    /// Save a collection of anchors.
    /// </summary>
    /// <remarks>
    /// This method persists anchors so that they may be retrieved later.
    ///
    /// This operation is asynchronous. Use the returned <see cref="OVRTask"/> to track the result of the
    /// asynchronous operation.
    /// </remarks>
    /// <param name="anchors">A collection of anchors to persist.</param>
    /// <returns>Returns an awaitable <see cref="OVRTask"/> representing the asynchronous request.</returns>
    /// <seealso cref="FetchAnchorsAsync(List{OVRAnchor},FetchOptions,Action{List{OVRAnchor},int})"/>
    /// <seealso cref="SaveAsync()"/>
    /// <seealso cref="EraseAsync(IEnumerable{OVRAnchor},IEnumerable{Guid})"/>
    public static OVRTask<OVRResult<SaveResult>> SaveAsync(IEnumerable<OVRAnchor> anchors)
    {
        using var spaces = OVRNativeList.WithSuggestedCapacityFrom(anchors).AllocateEmpty<ulong>(Allocator.Temp);
        foreach (var anchor in anchors.ToNonAlloc())
        {
            spaces.Add(anchor.Handle);
        }

        if (spaces.Count == 0)
        {
            return OVRTask.FromResult(OVRResult.From(SaveResult.Success));
        }

        return SaveSpacesAsync(spaces);
    }

    internal static unsafe OVRTask<OVRResult<SaveResult>> SaveSpacesAsync(ReadOnlySpan<ulong> spaces)
    {
        var telemetryMarker = OVRTelemetry
            .Start((int)Telemetry.MarkerId.SaveSpaces)
            .AddAnnotation(Telemetry.Annotation.SpaceCount, (long)spaces.Length);

        fixed (ulong* ptr = spaces)
        {
            var result = SaveSpaces(ptr, spaces.Length, out var requestId);
            Telemetry.SetSyncResult(telemetryMarker, requestId, result);

            return OVRTask.Build(result, requestId).ToResultTask<SaveResult>();
        }
    }

    // Invoked by OVRManager event loop
    internal static void OnSaveSpacesResult(OVRDeserialize.SpacesSaveResultData eventData)
        => Telemetry.SetAsyncResultAndSend(Telemetry.MarkerId.SaveSpaces, eventData.RequestId, (long)eventData.Result);

    /// <summary>
    /// Erases this anchor.
    /// </summary>
    /// <remarks>
    /// This method removes the anchor from persistent storage. Note this does not destroy the current instance.
    ///
    /// This operation is asynchronous. Use the returned <see cref="OVRTask"/> to track the result of the
    /// asynchronous operation.
    ///
    /// NOTE: When erasing multiple anchors, it is more efficient to erase them in a batch using
    /// <see cref="EraseAsync(IEnumerable{OVRAnchor},IEnumerable{Guid})"/>.
    /// </remarks>
    /// <returns>An awaitable <see cref="OVRTask"/> representing the asynchronous request.</returns>
    /// <seealso cref="SaveAsync()"/>
    /// <seealso cref="EraseAsync(IEnumerable{OVRAnchor},IEnumerable{Guid})"/>
    public OVRTask<OVRResult<EraseResult>> EraseAsync()
    {
        var uuid = Uuid;
        unsafe
        {
            return EraseSpacesAsync(default, new(&uuid, 1));
        }
    }

    /// <summary>
    /// Erase a collection of anchors.
    /// </summary>
    /// <remarks>
    /// This method removes anchors from persistent storage.
    ///
    /// This operation is asynchronous. Use the returned <see cref="OVRTask"/> to track the result of the
    /// asynchronous operation.
    /// </remarks>
    /// <param name="anchors">(Optional) A collection of anchors to remove from persistent storage.</param>
    /// <param name="uuids">(Optional) A collection of uuids to remove from persistent storage.</param>
    /// <returns>Returns an awaitable <see cref="OVRTask"/> representing the asynchronous request.</returns>
    /// <exception cref="ArgumentException">Thrown if both <paramref name="anchors"/> and <paramref name="uuids"/> are `null`.</exception>
    /// <seealso cref="SaveAsync(IEnumerable{OVRAnchor})"/>
    public static OVRTask<OVRResult<EraseResult>> EraseAsync(IEnumerable<OVRAnchor> anchors, IEnumerable<Guid> uuids)
    {
        if (anchors == null && uuids == null)
            throw new ArgumentException($"One of {nameof(anchors)} or {nameof(uuids)} must not be null.");

        using var spaces = OVRNativeList.WithSuggestedCapacityFrom(anchors).AllocateEmpty<ulong>(Allocator.Temp);
        foreach (var anchor in anchors.ToNonAlloc())
        {
            spaces.Add(anchor.Handle);
        }

        using var ids = uuids.ToNativeList(Allocator.Temp);

        if (spaces.Count == 0 && ids.Count == 0)
        {
            return OVRTask.FromResult(OVRResult.From(EraseResult.Success));
        }

        return EraseSpacesAsync(spaces, ids);
    }

    private static unsafe OVRTask<OVRResult<EraseResult>> EraseSpacesAsync(ReadOnlySpan<ulong> spaces, ReadOnlySpan<Guid> uuids)
    {
        var telemetryMarker = OVRTelemetry
            .Start((int)Telemetry.MarkerId.EraseSpaces)
            .AddAnnotation(Telemetry.Annotation.SpaceCount, spaces.Length)
            .AddAnnotation(Telemetry.Annotation.UuidCount, uuids.Length);

        fixed (ulong* spacesPtr = spaces)
        fixed (Guid* uuidsPtr = uuids)
        {
            var result = EraseSpaces((uint)spaces.Length, spacesPtr, (uint)uuids.Length, uuidsPtr, out var requestId);
            Telemetry.SetSyncResult(telemetryMarker, requestId, result);

            return OVRTask.Build(result, requestId).ToResultTask<EraseResult>();
        }
    }

    internal static void OnEraseSpacesResult(OVRDeserialize.SpacesEraseResultData eventData)
        => Telemetry.SetAsyncResultAndSend(Telemetry.MarkerId.EraseSpaces, eventData.RequestId, (long)eventData.Result);

    /// <summary>
    /// Share this anchor with the specified users.
    /// </summary>
    /// <remarks>
    ///
    /// This method shares the anchor with a collection of <see cref="OVRSpaceUser"/>.
    ///
    /// This operation is asynchronous. Use the returned <see cref="OVRTask"/> to track the result of the
    /// asynchronous operation.
    /// </remarks>
    /// <param name="users"> A collection of users with whom anchors will be shared.</param>
    /// <returns>An awaitable <see cref="OVRTask"/> representing the asynchronous request.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="users"/> is `null`.</exception>
    /// <exception cref="ArgumentException">Thrown if <paramref name="users"/> count is less than one.</exception>
    /// <seealso cref="ShareAsync(IEnumerable{OVRAnchor},IEnumerable{OVRSpaceUser})"/>
    public OVRTask<OVRResult<ShareResult>> ShareAsync(IEnumerable<OVRSpaceUser> users)
    {
        if (users == null)
            throw new ArgumentNullException(nameof(users));

        unsafe
        {
            using var userList = OVRNativeList.WithSuggestedCapacityFrom(users).AllocateEmpty<ulong>(Allocator.Temp);
            foreach (var user in users.ToNonAlloc())
            {
                userList.Add(user._handle);
            }

            if (userList.Count < 1)
                throw new ArgumentException($"{nameof(users)} must contain at least one user.");

            var handle = Handle;
            return ShareSpacesAsync(new(&handle, 1), userList);
        }
    }

    /// <summary>
    /// Share a collection of anchors with a collection of users.
    /// </summary>
    /// <remarks>
    /// This method shares a collection of anchors with a collection of users.
    ///
    /// This operation is asynchronous. Use the returned <see cref="OVRTask"/> to track the result of the
    /// asynchronous operation.
    /// </remarks>
    /// <param name="anchors">A collection of anchors to share.</param>
    /// <param name="users"> A collection of users with whom anchors will be shared.</param>
    /// <returns>Returns an awaitable <see cref="OVRTask"/> representing the asynchronous request.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="anchors"/> or <paramref name="users"/> are `null`.</exception>
    /// <exception cref="ArgumentException">Thrown if <paramref name="users"/> count is less than one.</exception>
    /// <seealso cref="ShareAsync(IEnumerable{OVRSpaceUser})"/>
    public static OVRTask<OVRResult<ShareResult>> ShareAsync(
        IEnumerable<OVRAnchor> anchors,
        IEnumerable<OVRSpaceUser> users)
    {
        if (anchors == null)
            throw new ArgumentNullException(nameof(anchors));

        if (users == null)
            throw new ArgumentNullException(nameof(users));

        using var spaceList = OVRNativeList.WithSuggestedCapacityFrom(anchors).AllocateEmpty<ulong>(Allocator.Temp);
        foreach (var anchor in anchors.ToNonAlloc())
        {
            spaceList.Add(anchor.Handle);
        }

        using var userList = OVRNativeList.WithSuggestedCapacityFrom(users).AllocateEmpty<ulong>(Allocator.Temp);
        foreach (var user in users.ToNonAlloc())
        {
            userList.Add(user._handle);
        }

        if (userList.Count < 1)
            throw new ArgumentException($"{nameof(users)} must contain at least one user.");

        if (spaceList.Count == 0)
            return OVRTask.FromResult(OVRResult.From(ShareResult.Success));

        return ShareSpacesAsync(spaces: spaceList, users: userList);
    }

    private static unsafe OVRTask<OVRResult<ShareResult>> ShareSpacesAsync(ReadOnlySpan<ulong> spaces,
        ReadOnlySpan<ulong> users)
    {
        fixed (ulong* spacePtr = spaces)
        fixed (ulong* userPtr = users)
        {
            var result = ShareSpaces(
                spaces: spacePtr,
                numSpaces: (uint)spaces.Length,
                userHandles: userPtr,
                numUsers: (uint)users.Length,
                out var requestId);

            return OVRTask.Build(result, requestId).ToResultTask<ShareResult>();
        }
    }

    /// <summary>
    /// Shares this anchor with the group associated with the given UUID.
    /// </summary>
    /// <param name="groupUuid">
    /// A UUID of a group to share the anchor with.
    /// Anchors shared to this <see cref="groupUuid"/> can be loaded by other clients via
    /// <see cref="OVRSpatialAnchor.LoadUnboundSharedAnchorsAsync(Guid,List{OVRSpatialAnchor.UnboundAnchor})"/>.
    /// <br/>
    /// NOTE: You may arbitrarily generate your own UUIDs (e.g. with <see cref="System.Guid.NewGuid"/>), or you may use
    /// UUIDs provided by colocation APIs such as in <see cref="OVRColocationSession"/>.
    /// </param>
    /// <seealso cref="OVRColocationSession.StartAdvertisementAsync"/>
    /// <seealso cref="OVRColocationSession.Data.AdvertisementUuid"/>
    /// <returns>
    /// Returns an <see cref="OVRResult"/>&lt;<see cref="OVRAnchor.ShareResult"/>&gt; indicating the status of the share
    /// operation.
    /// </returns>
    /// <remarks>
    /// This method is asynchronous; use the returned <see cref="OVRTask"/> wrapper to be notified of completion.
    ///
    /// The <paramref name="groupUuid"/> parameter can be any valid Guid, which excludes the default value Guid, that is,
    /// <see cref="Guid.Empty"/>.
    /// </remarks>
    public OVRTask<OVRResult<ShareResult>> ShareAsync(Guid groupUuid)
    {
        ulong handle = Handle;
        unsafe
        {
            var handleSpan = new ReadOnlySpan<ulong>(&handle, 1);
            var groupUuidSpan = new ReadOnlySpan<Guid>(&groupUuid, 1);
            return ShareAsyncInternal(handleSpan, groupUuidSpan);
        }
    }

    /// <summary>
    /// Shares a collection of anchors to a group.
    /// </summary>
    /// <param name="anchors">
    /// The collection of anchors to share.
    /// </param>
    /// <param name="groupUuid">
    /// A UUID of a group to share the anchors with.
    /// Anchors shared to this <see cref="groupUuid"/> can be loaded by other clients via
    /// <see cref="LoadUnboundSharedAnchorsAsync(Guid,List{UnboundAnchor})"/>.
    /// <br/>
    /// NOTE: You may arbitrarily generate your own UUIDs (e.g. with <see cref="System.Guid.NewGuid"/>), or you may use
    /// UUIDs provided by colocation APIs such as in <see cref="OVRColocationSession"/>.
    /// </param>
    /// <returns>
    /// Returns an <see cref="OVRResult"/>&lt;<see cref="OVRAnchor.ShareResult"/>&gt; indicating the status of the share
    /// operation.
    /// </returns>
    /// <remarks>
    /// This method is asynchronous; use the returned <see cref="OVRTask"/> wrapper to be notified of completion.
    /// <br/><br/>
    /// The <paramref name="groupUuid"/> parameter can be any valid Guid, which excludes the default value Guid, AKA
    /// <see cref="Guid.Empty"/>.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="anchors"/> is null.</exception>
    public static OVRTask<OVRResult<ShareResult>> ShareAsync(IEnumerable<OVRAnchor> anchors, Guid groupUuid)
    {
        if (anchors is null)
            throw new ArgumentNullException(nameof(anchors));

        var anchorIter = anchors.ToNonAlloc();
        using var anchorNativeList = new OVRNativeList<ulong>(anchorIter.Count, Allocator.Temp);
        foreach (var a in anchorIter)
        {
            anchorNativeList.Add(a.Handle);
        }
        unsafe
        {
            var groupUuidPtr = new ReadOnlySpan<Guid>(&groupUuid, 1);
            return ShareAsyncInternal(anchorNativeList, groupUuidPtr);
        }
    }

    internal static unsafe OVRTask<OVRResult<ShareResult>> ShareAsyncInternal(ReadOnlySpan<ulong> anchors,
        ReadOnlySpan<Guid> groupUuids)
    {
        var info = new OVRPlugin.ShareSpacesInfo();
        info.RecipientType = OVRPlugin.ShareSpacesRecipientType.Group;

        fixed (ulong* spacesPtr = anchors)
        fixed (Guid* uuidsPtr = groupUuids)
        {
            info.Spaces = spacesPtr;
            info.SpaceCount = (uint)anchors.Length;
            var shareSpacesGroupRecipientInfo = new OVRPlugin.ShareSpacesGroupRecipientInfo()
            {
                GroupCount = (uint)groupUuids.Length,
                GroupUuids = uuidsPtr
            };

            info.RecipientInfo = (ShareSpacesRecipientInfoBase*)(&shareSpacesGroupRecipientInfo);
            return OVRTask
                .Build(ShareSpaces(in info, out var requestId), requestId)
                .ToResultTask<ShareResult>();
        }
    }

    internal static void OnShareAnchorsToGroupsComplete(UInt64 requestId, Result result)
    {
        OVRTask.SetResult(requestId, OVRResult.From((ShareResult)result));
    }

    internal ulong Handle { get; }

    /// <summary>
    /// The unique identifier of this anchor.
    /// </summary>
    /// <remarks>
    /// UUIDs persist across sessions. If you load a persisted anchor, you can use the UUID to identify it.
    /// </remarks>
    public Guid Uuid { get; }

    internal OVRAnchor(ulong handle, Guid uuid)
    {
        Handle = handle;
        Uuid = uuid;
    }

    /// <summary>
    /// Gets the anchor's component of a specific type.
    /// </summary>
    /// <typeparam name="T">The type of the component.</typeparam>
    /// <returns>The requested component.</returns>
    /// <remarks>Make sure the anchor supports the specified type of component using <see cref="SupportsComponent{T}"/></remarks>
    /// <exception cref="InvalidOperationException">Thrown if the anchor doesn't support the specified type of component.</exception>
    /// <seealso cref="TryGetComponent{T}"/>
    /// <seealso cref="SupportsComponent{T}"/>
    public T GetComponent<T>() where T : struct, IOVRAnchorComponent<T>
    {
        if (!TryGetComponent<T>(out var component))
        {
            throw new InvalidOperationException($"Anchor {Uuid} does not have component {typeof(T).Name}");
        }

        return component;
    }

    /// <summary>
    /// Tries to get the anchor's component of a specific type.
    /// </summary>
    /// <param name="component">The requested component, as an <c>out</c> parameter.</param>
    /// <typeparam name="T">The type of the component.</typeparam>
    /// <returns>Whether or not the request succeeded. It may fail if the anchor doesn't support this type of component.</returns>
    /// <seealso cref="GetComponent{T}"/>
    public bool TryGetComponent<T>(out T component) where T : struct, IOVRAnchorComponent<T>
    {
        component = default;
        if (!GetSpaceComponentStatusInternal(Handle, component.Type, out _, out _).IsSuccess())
        {
            return false;
        }

        component = component.FromAnchor(this);
        return true;
    }

    /// <summary>
    /// Tests whether or not the anchor supports a specific type of component.
    /// </summary>
    /// <remarks>
    /// For performance reasons, we use xrGetSpaceComponentStatusFB, which can
    /// result in an error in the logs when the component is not available.
    ///
    /// This error does not have impact on the control flow. The alternative method,
    /// <seealso cref="GetSupportedComponents(List{SpaceComponentType})"/> avoids
    /// this error reporting, but does have performance constraints.
    /// </remarks>
    /// <typeparam name="T">The type of the component.</typeparam>
    /// <returns>Whether or not the specified type of component is supported.</returns>
    public bool SupportsComponent<T>() where T : struct, IOVRAnchorComponent<T>
        => GetSpaceComponentStatusInternal(Handle, default(T).Type, out _, out _).IsSuccess();

    /// <summary>
    /// Get all the supported components of an anchor.
    /// </summary>
    /// <param name="components">The list to populate with the supported components. The list is cleared first.</param>
    /// <returns>`true` if the supported components could be retrieved, otherwise `false`.</returns>
    public bool GetSupportedComponents(List<SpaceComponentType> components)
    {
        components.Clear();

        unsafe
        {
            if (!EnumerateSpaceSupportedComponents(Handle, 0, out var count, null).IsSuccess())
                return false;

            var buffer = stackalloc SpaceComponentType[(int)count];
            if (!EnumerateSpaceSupportedComponents(Handle, count, out count, buffer).IsSuccess())
                return false;

            for (uint i = 0; i < count; i++)
            {
                components.Add(buffer[i]);
            }

            return true;
        }
    }

    /// <summary>
    /// Compares two anchors for equality.
    /// </summary>
    /// <param name="other">The anchor to compare with this one.</param>
    /// <returns>Returns `true` if both anchor UUIDs are the same and they are the same runtime instance.
    /// That is, they are the same instance of the same anchor.</returns>
    public bool Equals(OVRAnchor other) => Handle.Equals(other.Handle) && Uuid.Equals(other.Uuid);

    /// <summary>
    /// Compares this anchor with an object for equality.
    /// </summary>
    /// <param name="obj">The `object` to compare with this anchor.</param>
    /// <returns>Returns `true` if <paramref name="obj"/> is an <see cref="OVRAnchor"/> and
    /// <see cref="Equals(OVRAnchor)"/> is also `true`, otherwise `false`.</returns>
    public override bool Equals(object obj) => obj is OVRAnchor other && Equals(other);

    /// <summary>
    /// Compares two anchors for equality.
    /// </summary>
    /// <remarks>
    /// This is the same equality test as <see cref="Equals(OVRAnchor)"/>.
    /// </remarks>
    /// <param name="lhs">The anchor to compare with <paramref name="rhs"/>.</param>
    /// <param name="rhs">The anchor to compare with <paramref name="lhs"/>.</param>
    /// <returns>Returns `true` if <paramref name="lhs"/> is equal to <paramref name="rhs"/>, otherwise `false`.</returns>
    public static bool operator ==(OVRAnchor lhs, OVRAnchor rhs) => lhs.Equals(rhs);

    /// <summary>
    /// Compares two anchors for inequality.
    /// </summary>
    /// <remarks>
    /// This is the logical negation of <see cref="Equals(OVRAnchor)"/>.
    /// </remarks>
    /// <param name="lhs">The anchor to compare with <paramref name="rhs"/>.</param>
    /// <param name="rhs">The anchor to compare with <paramref name="lhs"/>.</param>
    /// <returns>Returns `true` if <paramref name="lhs"/> is not equal to <paramref name="rhs"/>, otherwise `false`.</returns>
    public static bool operator !=(OVRAnchor lhs, OVRAnchor rhs) => !lhs.Equals(rhs);

    /// <summary>
    /// Generates a hash code suitable for use in a `Dictionary` or `HashSet`
    /// </summary>
    /// <returns>Returns a hash code suitable for use in a `Dictionary` or `HashSet`</returns>
    public override int GetHashCode() => unchecked(Handle.GetHashCode() * 486187739 + Uuid.GetHashCode());

    /// <summary>
    /// Generates a string representation of this anchor, based on its <see cref="Uuid"/>.
    /// </summary>
    /// <returns>Returns the stringification of this anchor's <see cref="Uuid"/>.</returns>
    public override string ToString() => Uuid.ToString();

    /// <summary>
    /// Disposes of an anchor.
    /// </summary>
    /// <remarks>
    /// Calling this method will destroy the anchor so that it won't be managed by internal systems until
    /// the next time it is fetched again.
    /// </remarks>
    public void Dispose() => OVRPlugin.DestroySpace(Handle);

    [RuntimeInitializeOnLoadMethod]
    internal static void Init()
    {
        _deferredTasks.Clear();
        Telemetry.OnInit();
    }

    internal static OVRTask<Result> FetchAnchors(IList<OVRAnchor> anchors, SpaceQueryInfo2 queryInfo)
    {
        if (anchors == null)
        {
            throw new ArgumentNullException(nameof(anchors));
        }

        anchors.Clear();

        var telemetryMarker = OVRTelemetry
            .Start((int)Telemetry.MarkerId.QuerySpaces)
            .AddAnnotation(Telemetry.Annotation.Timeout, (double)queryInfo.Timeout)
            .AddAnnotation(Telemetry.Annotation.MaxResults, (long)queryInfo.MaxQuerySpaces)
            .AddAnnotation(Telemetry.Annotation.StorageLocation, (long)queryInfo.Location);

        switch (queryInfo.FilterType)
        {
            case SpaceQueryFilterType.Components:
                unsafe
                {
                    var componentTypes = stackalloc long[1]
                    {
                        (long)queryInfo.ComponentsInfo.Components[0]
                    };
                    telemetryMarker.AddAnnotation(Telemetry.Annotation.ComponentTypes, componentTypes,
                        queryInfo.ComponentsInfo.NumComponents);
                }
                break;
            case SpaceQueryFilterType.Group:
                telemetryMarker.AddAnnotation(Telemetry.Annotation.GroupCount, 1);
                break;
            case SpaceQueryFilterType.Ids:
                telemetryMarker.AddAnnotation(Telemetry.Annotation.UuidCount, queryInfo.IdInfo.NumIds);
                break;
        }

        var result = QuerySpaces2(queryInfo, out var requestId);
        Telemetry.SetSyncResult(telemetryMarker, requestId, result);

        return OVRTask
            .Build(result, requestId)
            .ToTask()
            .WithInternalData(anchors);
    }

}
