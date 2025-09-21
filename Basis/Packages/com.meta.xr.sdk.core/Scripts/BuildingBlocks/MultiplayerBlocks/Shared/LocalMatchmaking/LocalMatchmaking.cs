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
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Events;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Meta.XR.MultiplayerBlocks.Shared
{
    /// <summary>
    /// This class provides a ready-to-use encapsulation that leverages the <see cref="OVRColocationSession"/> APIs
    /// to host and discover local / nearby Colocation sessions and join them via Bluetooth and WiFi.
    ///
    /// Comparing to networking framework based matchmaking: this workflow ensures users are all locally nearby, and there's no
    /// need for using Oculus user ids when colocating within the group (this requires working together with the Colocation block
    /// with the "Use Colocation Session" option enabled).
    ///
    /// All players wish to participate in the same session need to have Bluetooth enabled and join the same WiFi network.
    ///
    /// As the local session provided by <see cref="OVRColocationSession"/> is just a mechanism to find nearby players and send limited metadata
    /// for matchmaking, it doesn't really allow synchronizing networked objects and support multiplayer game logic.
    /// Hence, for the benefit of bootstrapping a local multiplayer session easily, this implementation also depends on the <see cref="CustomMatchmaking"/>
    /// block to create and join networked rooms provided by 3p networking frameworks (such as Photon Fusion2, Unity Netcode for GameObjects).
    ///
    /// This class provides an automatic mechanism to host/join local sessions on Start() depending on whether an existing colocation sessions available.
    ///
    /// For more information about the <see cref="OVRColocationSession"/> API, checkout the [official documentation](https://developers.meta.com/horizon/documentation/unity/unity-colocation-discovery)
    ///
    /// <remarks>If wish to explicitly host / join the session with own game UI, developers could also use several async functions provided to trigger manually.</remarks>
    /// </summary>
    public class LocalMatchmaking : MonoBehaviour
    {
        [Tooltip("On Start(), players will automatically discover local sessions and start hosting if no sessions found.")]
        [SerializeField]
        private bool automaticHostOrJoin = true;

        [Tooltip("Seconds to wait for discovering local sessions, if not found then creating their own session")]
        [SerializeField]
        private int timeDiscoveringInSec = 5;

        /// <summary>
        /// Event to notify when a colocation session has been created and advertised successfully by the host.
        /// Guid: group uuid that can be used to share anchors via <see cref="OVRSpatialAnchor.ShareAsync"/>
        /// </summary>
        public static readonly UnityEvent<Guid> OnSessionCreateSucceeded = new();
        /// <summary>
        /// Event to notify when failed to create and advertise the colocation session.
        /// string: failure reason, could be potentially used to display instruction to users
        /// </summary>
        public static readonly UnityEvent<string> OnSessionCreateFailed = new();
        /// <summary>
        /// Event to notify when a colocation session has been discovered successfully by the guest.
        /// Guid: group uuid that can be used to retrieve anchors via <see cref="OVRSpatialAnchor.LoadUnboundSharedAnchorsAsync"/>
        /// </summary>
        public static readonly UnityEvent<Guid> OnSessionDiscoverSucceeded = new();
        /// <summary>
        /// Event to notify when failed to discover the colocation session.
        /// string: failure reason, could be potentially used to display instruction to users
        /// </summary>
        public static readonly UnityEvent<string> OnSessionDiscoverFailed = new();

        internal static Func<Task<bool>> BeforeStartHost;
        // Should be only set in BeforeStartHost, used for broadcasting, guest can fetch this ExtraData and decode.
        internal static string ExtraData = null;

        private CustomMatchmaking _customMatchmaking;
        private bool _discoveredLocalSessionAsGuest;

        #region Lifecyle Events
        private void Awake()
        {
#pragma warning disable CS0618 // Type or member is obsolete
            _customMatchmaking = FindObjectOfType<CustomMatchmaking>();
#pragma warning restore CS0618 // Type or member is obsolete
            if (_customMatchmaking == null)
            {
                throw new InvalidOperationException($"{nameof(LocalMatchmaking)}] No {nameof(CustomMatchmaking)} component was found in the scene");
            }
        }

        private void OnEnable()
        {
            if (_customMatchmaking == null)
            {
                return;
            }
            _customMatchmaking.onRoomCreationFinished.AddListener(OnRoomCreationFinished);
        }

        private void OnDisable()
        {
            if (_customMatchmaking == null)
            {
                return;
            }
            _customMatchmaking.onRoomCreationFinished.RemoveListener(OnRoomCreationFinished);
        }

        private void Start()
        {
            if (automaticHostOrJoin)
            {
                HostOrJoinSessionAutomatically();
            }
        }
        #endregion

        #region CustomMatchmaking Block Integration
        /// <summary>
        /// Manually start as host, this will create a networked room
        /// under the hood by <see cref="CustomMatchmaking"/>, and in turn this class
        /// listens for the networked room creation event then broadcast the room
        /// information via the <see cref="OVRColocationSession"/> API.
        /// </summary>
        public async Task StartAsHost()
        {
            if (_customMatchmaking != null)
            {
                if (BeforeStartHost != null)
                {
                    if (!await BeforeStartHost.Invoke())
                    {
                        Debug.LogError("Failed to start Colocation Session as BeforeStartHost task execution failed.");
                        return;
                    }
                }
                await _customMatchmaking.CreateRoom();
            }
        }

        /// <summary>
        /// Manually start as guest, this will discover colocation sessions via the
        /// <see cref="OVRColocationSession"/> API, then stop after
        /// <see cref="timeDiscoveringInSec"/> if setting
        /// <see cref="stopAfterTimeout"/> is set to true.
        /// </summary>
        /// <param name="stopAfterTimeout">Boolean to determine whether to automatically
        /// stop the discovery after <see cref="timeDiscoveringInSec"/></param>
        public async Task StartAsGuest(bool stopAfterTimeout = true)
        {
            StartDiscoveringColocationSessions(OnColocationSessionFound);
            if (!stopAfterTimeout)
            {
                return;
            }
            await Task.Delay(timeDiscoveringInSec * 1000);
            if (!_discoveredLocalSessionAsGuest)
            {
                StopDiscoveringColocationSessions(OnColocationSessionFound);
            }
        }

        private async void HostOrJoinSessionAutomatically()
        {
            _discoveredLocalSessionAsGuest = false;
            await StartAsGuest();
            if (!_discoveredLocalSessionAsGuest && _customMatchmaking != null)
            {
                Debug.Log("Didn't found an existing local session, starting to create a network room");
                await StartAsHost();
            }
        }

        private void OnRoomCreationFinished(CustomMatchmaking.RoomOperationResult result)
        {
            if (result.IsSuccess)
            {
                byte[] roomInfo = Encoding.UTF8.GetBytes(CustomMatchmakingUtils.EncodeMatchInfoWithStruct(result.RoomToken, result.RoomPassword, ExtraData));
                StartAdvertisingColocationSession(roomInfo);
            }
        }

        private async void OnColocationSessionFound(OVRColocationSession.Data data)
        {
            if (_customMatchmaking == null)
            {
                return;
            }
            var matchInfo = CustomMatchmakingUtils.DecodeMatchInfoWithStruct(Encoding.UTF8.GetString(data.Metadata));
            if (string.IsNullOrEmpty(matchInfo.RoomId))
            {
                return;
            }
            _discoveredLocalSessionAsGuest = true;
            await _customMatchmaking.JoinRoom(matchInfo.RoomId, matchInfo.RoomPassword);
            ExtraData = matchInfo.Extra;
            ReportDiscoverEvent(data);
            // stop when session found
            StopDiscoveringColocationSessions(OnColocationSessionFound);
        }

        #endregion

        #region Public Functions / OVRColocationSession Usages
        /// <summary>
        /// Wraps around the <see cref="OVRColocationSession"/> function to start advertise the colocation session
        /// with the byte data.
        /// The public events of <see cref="OnSessionCreateSucceeded"/> and <see cref="OnSessionCreateFailed"/> will
        /// be notified accordingly.
        /// </summary>
        /// <param name="data">metadata that's included in the advertisement,
        /// could pass up to <see cref="OVRColocationSession.Data.MaxMetadataSize"/> bytes.</param>
        public async static void StartAdvertisingColocationSession(byte[] data)
        {
            var result = await OVRColocationSession.StartAdvertisementAsync(data);
            switch (result.Status)
            {
                case OVRColocationSession.Result.Success:
                    OnSessionCreateSucceeded?.Invoke(result.Value);
                    break;
                case OVRColocationSession.Result.NetworkFailed:
                    OnSessionCreateFailed?.Invoke("Failed to create the local session as connected network, " +
                                                  "please make sure the headset has joined WiFi");
                    break;
                case OVRColocationSession.Result.AlreadyAdvertising:
                    OnSessionCreateFailed?.Invoke("Failed to create the local session as session is already being advertised, " +
                                                  "there'll be no-op for this duplicated request");
                    break;
                case OVRColocationSession.Result.Unsupported:
                    OnSessionCreateFailed?.Invoke("Failed to create the local session as the feature is unsupported, " +
                                                  "please make sure this feature is required in OVRManager > Colocation Session Support");
                    break;
                case OVRColocationSession.Result.AlreadyDiscovering:
                case OVRColocationSession.Result.Failure:
                case OVRColocationSession.Result.OperationFailed:
                case OVRColocationSession.Result.InvalidData:
                case OVRColocationSession.Result.NoDiscoveryMethodAvailable:
                default:
                    OnSessionCreateFailed?.Invoke($"Failed to create the local session, reason: {result.Status}");
                    break;
            }
        }

        /// <summary>
        /// Wraps around the <see cref="OVRColocationSession"/> function to stop advertise the local session.
        /// </summary>
        public async static void StopAdvertisingColocationSession()
        {
            var result = await OVRColocationSession.StopAdvertisementAsync();
            if (result.Status != OVRColocationSession.Result.Success)
            {
                Debug.LogError($"Failed to stop advertisement for the colocation session: {result.Status}");
            }
        }

        /// <summary>
        /// Wraps around the <see cref="OVRColocationSession"/> function to start discovering local sessions
        /// with custom callback.
        /// The public events of <see cref="OnSessionDiscoverSucceeded"/> and <see cref="OnSessionDiscoverFailed"/> will
        /// be notified accordingly.
        /// </summary>
        /// <param name="onGroupFound">Custom callback to parse the data received from the local session advertisement.</param>
        public async static void StartDiscoveringColocationSessions(Action<OVRColocationSession.Data> onGroupFound)
        {
            OVRColocationSession.ColocationSessionDiscovered -= onGroupFound;
            OVRColocationSession.ColocationSessionDiscovered += onGroupFound;
            var result = await OVRColocationSession.StartDiscoveryAsync();
            switch (result.Status)
            {
                case OVRColocationSession.Result.Success:
                    // success report is done in the callback registered for the ColocationSessionDiscovered event.
                    break;
                case OVRColocationSession.Result.NoDiscoveryMethodAvailable:
                    OnSessionDiscoverFailed?.Invoke("Failed to discover the local session as no available method, " +
                                                    "please make sure the headset has enabled bluetooth");
                    break;
                case OVRColocationSession.Result.AlreadyDiscovering:
                    OnSessionDiscoverFailed?.Invoke("Failed to discover the local session as sessions are already being discovered, " +
                                                    "there'll be no-op for this duplicated request");
                    break;
                case OVRColocationSession.Result.AlreadyAdvertising:
                case OVRColocationSession.Result.Failure:
                case OVRColocationSession.Result.Unsupported:
                case OVRColocationSession.Result.OperationFailed:
                case OVRColocationSession.Result.InvalidData:
                case OVRColocationSession.Result.NetworkFailed:
                default:
                    OnSessionDiscoverFailed?.Invoke($"Failed to start discovering nearby session: {result.Status}");
                    break;
            }
        }

        /// <summary>
        /// Wraps around the <see cref="OVRColocationSession"/> function to stop discovering the local session.
        /// </summary>
        /// <param name="onGroupFound">Custom callback to remove from the discovery subscription.</param>
        public async static void StopDiscoveringColocationSessions(Action<OVRColocationSession.Data> onGroupFound)
        {
            var result = await OVRColocationSession.StopDiscoveryAsync();
            if (result.Status == OVRColocationSession.Result.Success)
            {
                OVRColocationSession.ColocationSessionDiscovered -= onGroupFound;
            }
            else
            {
                Debug.LogError($"Failed to stop discovering nearby session: {result.Status}");
            }
        }

        private static void ReportDiscoverEvent(OVRColocationSession.Data data)
        {
            OnSessionDiscoverSucceeded?.Invoke(data.AdvertisementUuid);
        }
        #endregion

    }
}
