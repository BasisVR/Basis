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
using UnityEngine;

/// <summary>
/// Colocation Sessions are a way for apps to connect "colocated" users (meaning users in real-world physical proximity),
/// enabling these users to share/load <see cref="OVRSpatialAnchor"/>s to/from the session's Uuid, rather than
/// with e.g. individual <see cref="OVRSpaceUser"/>s, which requires manual communication of Platform user IDs.
/// </summary>
/// <remarks>
/// This feature utilizes onboard bluetooth and a shared wifi network to get colocated users connected with each other.
/// Thus, users should be advised to have these device services enabled, and to remain connected to the same wifi
/// network, otherwise colocation session advertisements will not be receivable. <br/><br/>
/// Lastly, you the app developer should ensure your AndroidManifest.xml contains the following permission:
/// <code><![CDATA[
/// <uses-permission android:name="com.oculus.permission.USE_COLOCATION_DISCOVERY_API"/>
/// ]]></code>
/// which is automatically added when "Colocation Sessions" is enabled in your OculusProjectConfig asset
/// and you run the menu bar utility "Meta > Tools > Update AndroidManifest.xml".
/// </remarks>
public class OVRColocationSession
{
    /// <summary>
    /// An event that is invoked when a new nearby session has been discovered.
    /// </summary>
    /// <remarks>
    /// Only invokes while in a state of "active discovery", begun by successful calls to
    /// <see cref="StartDiscoveryAsync"/> and ended by successful calls to <see cref="StopDiscoveryAsync"/>.
    /// <br/><br/>
    /// NOTICE: <see cref="StopDiscoveryAsync"/> does NOT unregister listeners from this event. Leaving non-static
    /// listeners indefinitely attached is a potential dangling reference (GC memory leak), and/or may introduce
    /// undefined behaviors (including unhandled exceptions).
    /// <br/><br/>
    /// You should only need to sub/unsub a listener once, but ultimately listener lifetimes and validity are yours to
    /// consider and manage.
    /// </remarks>
    public static event Action<Data> ColocationSessionDiscovered;

    /// <summary>
    /// Data that is passed to <see cref="ColocationSessionDiscovered"/> when a nearby session has been discovered.
    /// </summary>
    /// <seealso cref="StartDiscoveryAsync"/>
    public struct Data
    {
        /// <summary>
        /// The maximum number of bytes allowed for the custom <see cref="Metadata"/> section of advertisements.
        /// </summary>
        /// <seealso cref="StartAdvertisementAsync"/>
        public const int MaxMetadataSize = 1024;

        /// <summary>
        /// A unique ID representing the colocated session, advertised by another user's call to <see cref="StartAdvertisementAsync"/>.
        /// </summary>
        /// <remarks>
        /// This value is identical to the Guid returned to the advertiser after they awaited <see cref="StartAdvertisementAsync"/>.
        /// <br/><br/>
        /// This UUID is useful as a "groupUuid", for various <see cref="OVRSpatialAnchor"/> sharing and loading APIs (see below).
        /// </remarks>
        /// <seealso cref="OVRSpatialAnchor.ShareAsync(IEnumerable{OVRSpatialAnchor},Guid)"/>
        /// <seealso cref="OVRSpatialAnchor.ShareAsync(IEnumerable{OVRSpatialAnchor},IEnumerable{Guid})"/>
        /// <seealso cref="OVRSpatialAnchor.LoadUnboundSharedAnchorsAsync(Guid,List{OVRSpatialAnchor.UnboundAnchor})"/>
        public Guid AdvertisementUuid { get; internal set; }

        /// <summary>
        /// The user-defined data block that the advertiser passed to <see cref="StartAdvertisementAsync"/>.
        /// </summary>
        /// <seealso cref="MaxMetadataSize"/>
        public byte[] Metadata { get; internal set; }
    }

    /// <summary>
    /// An enum that represents the possible results from calling a public async function in <see cref="OVRColocationSession"/>.
    /// </summary>
    [OVRResultStatus]
    public enum Result
    {
        /// <summary>The API call succeeded</summary>
        Success = OVRPlugin.Result.Success,

        /// <summary>A Colocation Session already is advertising, a no-op occurs</summary>
        AlreadyAdvertising = OVRPlugin.Result.Success_ColocationSessionAlreadyAdvertising,

        /// <summary>Colocation Sessions are already being discovered, a no-op occurs</summary>
        AlreadyDiscovering = OVRPlugin.Result.Success_ColocationSessionAlreadyDiscovering,

        /// <summary>The API call failed with no additional details</summary>
        Failure = OVRPlugin.Result.Failure,

        /// <summary>Colocation Session is not supported</summary>
        Unsupported = OVRPlugin.Result.Failure_Unsupported,

        /// <summary>The API call operation failed</summary>
        OperationFailed = OVRPlugin.Result.Failure_OperationFailed,

        /// <summary>The result received does not represent the API call</summary>
        InvalidData = OVRPlugin.Result.Failure_DataIsInvalid,

        /// <summary>The user is not connected to a network</summary>
        NetworkFailed = OVRPlugin.Result.Failure_ColocationSessionNetworkFailed,

        /// <summary>The user does not have discovery methods such as bluetooth enabled</summary>
        NoDiscoveryMethodAvailable = OVRPlugin.Result.Failure_ColocationSessionNoDiscoveryMethodAvailable
    }

    /// <summary>
    /// Starts advertising a new colocation session with associated custom data.
    /// </summary>
    /// <param name="colocationSessionData">
    /// A span of bytes (or null) representing user-defined data to associate with the new Colocation Session. <br/>
    /// It must be no longer than <see cref="Data.MaxMetadataSize"/> bytes. <br/>
    /// This custom data is immutable per advertisement, and will be receivable by discoverers of the advertisement (via
    /// <see cref="Data.Metadata"/>; see event parameter for <see cref="ColocationSessionDiscovered"/>).
    /// </param>
    /// <returns>
    /// Returns an <see cref="OVRResult"/>&lt;<see cref="System.Guid"/>,<see cref="Result"/>&gt; indicating the success or failure of starting a new colocated
    /// session advertisement. <br/>
    /// The Guid portion of the result (from <see cref="OVRResult{Guid,Result}.Value"/> or
    /// <see cref="OVRResult{Guid,Result}.TryGetValue"/>) is the colocation session's UUID, which is useful as a
    /// "groupUuid" for various <see cref="OVRSpatialAnchor"/> sharing and loading APIs (listed below).
    /// <seealso cref="Data.AdvertisementUuid"/>
    /// <seealso cref="OVRSpatialAnchor.ShareAsync(IEnumerable{OVRSpatialAnchor},Guid)"/>
    /// <seealso cref="OVRSpatialAnchor.ShareAsync(IEnumerable{OVRSpatialAnchor},IEnumerable{Guid})"/>
    /// <seealso cref="OVRSpatialAnchor.LoadUnboundSharedAnchorsAsync(Guid,List{OVRSpatialAnchor.UnboundAnchor})"/>
    /// </returns>
    /// <exception cref="System.ArgumentException">
    /// is thrown if <paramref name="colocationSessionData"/> refers to more than <see cref="Data.MaxMetadataSize"/>
    /// bytes of data.
    /// </exception>
    /// <example>Example:
    /// <code><![CDATA[
    /// async void TryStartSession()
    /// {
    ///     byte[] data = null; // your custom data here
    ///
    ///     var result = await OVRColocationSession.StartAdvertisementAsync(data);
    ///     if (!result.TryGetValue(out var sessionUuid))
    ///     {
    ///         Debug.LogError($"Unable to start colocation session! status={result.Status}");
    ///         return;
    ///     }
    ///
    ///     await ShareCurrentAnchorsWith(sessionUuid); // your impl.
    /// }
    /// ]]></code>
    /// </example>
    /// <remarks>
    /// This method is asynchronous; use the returned <see cref="OVRTask{T}"/> wrapper to be notified of completion.
    /// <br/><br/>
    /// If a session advertisement is already successfully started and running, then the returned
    /// <see cref="OVRResult{Result}.Status"/> will be <see cref="Result.AlreadyAdvertising"/>, which is a special
    /// "success" status.<br/>
    /// In this case, <paramref name="colocationSessionData"/> is ignored even if it is new/updated data; in order to
    /// update your custom data, you must await <see cref="StopAdvertisementAsync"/> before starting a new session
    /// advertisement. The new session would need to be re-discovered by peers, and any anchors shared to the previous
    /// session would need to be re-shared with the new UUID.
    /// </remarks>
    public static OVRTask<OVRResult<Guid, Result>> StartAdvertisementAsync(ReadOnlySpan<byte> colocationSessionData)
    {
        if (colocationSessionData.Length > Data.MaxMetadataSize)
        {
            throw new ArgumentException($"Colocation Session Advertisement can only store up to {Data.MaxMetadataSize} bytes of data");
        }

        OVRPlugin.ColocationSessionStartAdvertisementInfo info = new OVRPlugin.ColocationSessionStartAdvertisementInfo();
        unsafe
        {
            fixed (byte* colocationSessionDataPtr = colocationSessionData)
            {
                info.GroupMetadata = colocationSessionDataPtr;
                info.PeerMetadataCount = (uint)colocationSessionData.Length;
                return OVRTask.Build(
                    OVRPlugin.StartColocationSessionAdvertisement(info, out var requestId), requestId)
                    .ToTask<Guid, Result>();
            }
        }
    }

    /// <summary>
    /// Stops the current colocation session advertisement started by <see cref="StartAdvertisementAsync"/>.
    /// </summary>
    /// <returns>
    /// Returns an <see cref="OVRResult"/>&lt;<see cref="Result"/>&gt; indicating the success or failure of this request to stop
    /// advertising.
    /// </returns>
    /// <remarks>
    /// This method is asynchronous; use the returned <see cref="OVRTask{T}"/> wrapper to be notified of completion.
    /// </remarks>
    public static OVRTask<OVRResult<Result>> StopAdvertisementAsync() => OVRTask.Build(
        OVRPlugin.StopColocationSessionAdvertisement(out var requestId), requestId)
        .ToResultTask<Result>();

    /// <summary>
    /// Starts discovering nearby advertised colocated sessions.
    /// </summary>
    /// <returns>
    /// Returns an <see cref="OVRResult"/>&lt;<see cref="Result"/>&gt; indicating the success or failure of starting
    /// colocated advertisement discovery.
    /// </returns>
    /// <example>Example:
    /// <code><![CDATA[
    /// void OnDiscoveredSession(OVRColocationSession.Data data)
    /// {
    ///     if (!RegisterNewSession(data.AdvertisementUuid)) // your impl.
    ///         return;
    ///
    ///     Debug.Log($"Discovered new colocated session: {data.AdvertisementUuid}");
    ///
    ///     // e.g.:
    ///     LoadMetadata(data.Metadata); // your impl.
    ///     LoadSharedAnchorsFrom(data.AdvertisementUuid); // your impl.
    /// }
    ///
    /// async void StartDiscoveringSessions()
    /// {
    ///     OVRColocationSession.ColocationSessionDiscovered -= OnDiscoveredSession; // prevents duplicate listeners
    ///     OVRColocationSession.ColocationSessionDiscovered += OnDiscoveredSession;
    ///
    ///     var result = await OVRColocationSession.StartDiscoveryAsync();
    ///     if (result.Success)
    ///         return;
    ///
    ///     Debug.LogError($"Unable to start discovering colocation sessions! status={result.Status}");
    ///     OVRColocationSession.ColocationSessionDiscovered -= OnDiscoveredSession;
    /// }
    /// ]]></code>
    /// </example>
    /// <remarks>
    /// Before you call this method, you need to register a listener to <see cref="ColocationSessionDiscovered"/> in
    /// order to actually receive discovered sessions.
    /// <br/><br/>
    /// This method is asynchronous; use the returned <see cref="OVRTask{T}"/> wrapper to be notified of completion.
    /// <br/><br/>
    /// If this method has already been invoked with a successful return status, then the returned
    /// <see cref="OVRResult{Result}.Status"/> of subsequent calls will be <see cref="Result.AlreadyDiscovering"/>,
    /// which is a special "success" status indicating no-op.
    /// </remarks>
    /// <seealso cref="StopDiscoveryAsync"/>
    public static OVRTask<OVRResult<Result>> StartDiscoveryAsync() => OVRTask.Build(
        OVRPlugin.StartColocationSessionDiscovery(out var requestId), requestId)
        .ToResultTask<Result>();

    /// <summary>
    /// Stops the discovery of nearby colocation session advertisements.
    /// </summary>
    /// <returns>
    /// Returns an <see cref="OVRResult"/>&lt;<see cref="Result"/>&gt; indicating the success or failure of this request
    /// to stop discovering.
    /// </returns>
    /// <remarks>
    /// This method is asynchronous; use the returned <see cref="OVRTask{T}"/> wrapper to be notified of completion.
    /// <br/><br/>
    /// NOTICE: Calling this method does not affect any listeners still attached to
    /// <see cref="ColocationSessionDiscovered"/>. Leaving non-static listeners indefinitely attached is a potential
    /// dangling reference (GC memory leak), and/or may introduce undefined behaviors (including unhandled exceptions).
    /// </remarks>
    /// <seealso cref="StartDiscoveryAsync"/>
    public static OVRTask<OVRResult<Result>> StopDiscoveryAsync() => OVRTask.Build(
        OVRPlugin.StopColocationSessionDiscovery(out var requestId), requestId)
        .ToResultTask<Result>();

    #region Internal Impl.

    internal static void OnColocationSessionStartAdvertisementComplete(ulong requestId, OVRPlugin.Result result, Guid uuid)
    {
        OVRTask.SetResult(requestId, OVRResult<Guid, Result>.From(uuid, (Result)result));
    }

    internal static void OnColocationSessionStopAdvertisementComplete(ulong requestId, OVRPlugin.Result result)
    {
        OVRTask.SetResult(requestId, OVRResult<Result>.From((Result)result));
    }

    internal static void OnColocationSessionStartDiscoveryComplete(ulong requestId, OVRPlugin.Result result)
    {
        OVRTask.SetResult(requestId, OVRResult<Result>.From((Result)result));
    }

    internal static void OnColocationSessionStopDiscoveryComplete(ulong requestId, OVRPlugin.Result result)
    {
        OVRTask.SetResult(requestId, OVRResult<Result>.From((Result)result));
    }

    internal static unsafe void OnColocationSessionDiscoveryResult(ulong requestId, Guid uuid, uint metaDataCount, byte* metaDataPtr)
    {
        byte[] metaData = new byte[metaDataCount];
        Marshal.Copy((IntPtr)metaDataPtr, metaData, 0, (int)metaDataCount);

        Data colocationSessionData = new Data()
        {
            AdvertisementUuid = uuid,
            Metadata = metaData,
        };

        ColocationSessionDiscovered?.Invoke(colocationSessionData);
    }

    internal static void OnColocationSessionAdvertisementComplete(ulong requestId, OVRPlugin.Result result)
    {
        if (result != OVRPlugin.Result.Success)
        {
            Debug.LogWarning($"Colocation Session Advertisement unexpectedly completed with result: {result}");
        }
    }

    internal static void OnColocationSessionDiscoveryComplete(ulong requestId, OVRPlugin.Result result)
    {
        if (result != OVRPlugin.Result.Success)
        {
            Debug.LogWarning($"Colocation Session Discovery unexpectedly completed with result: {result}");
        }
    }

    #endregion Internal Impl.
}
