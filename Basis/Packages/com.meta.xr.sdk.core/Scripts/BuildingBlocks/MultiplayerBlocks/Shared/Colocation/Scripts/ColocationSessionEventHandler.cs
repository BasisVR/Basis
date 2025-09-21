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

using Meta.XR.BuildingBlocks;
#if META_MR_UTILITY_KIT_DEFINED
using Meta.XR.MRUtilityKit;
#endif // META_MR_UTILITY_KIT_DEFINED
using Meta.XR.MultiplayerBlocks.Colocation;
using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Android;
using Logger = Meta.XR.MultiplayerBlocks.Colocation.Logger;
namespace Meta.XR.MultiplayerBlocks.Shared
{
    /// <summary>
    /// This <see cref="MonoBehaviour"/> is responsible for handling colocation events generated
    /// from <see cref="LocalMatchmaking"/> so we can sequentially complete the colocation process:
    /// As host: upon session created, we create alignment anchor and share with the group
    /// As guest: upon session discovered, we query the shared anchor from group and align camera with it
    /// Both scenarios will report colocation ready to the <see cref="ColocationController.ColocationReadyCallbacks"/>.
    ///
    /// For more information about the group-based Shared Spatial Anchors, checkout the [official documentation](https://developers.meta.com/horizon/documentation/unity/unity-shared-spatial-anchors/#understanding-group-based-vs-user-based-spatial-anchor-sharing-and-loading)
    /// </summary>
    public class ColocationSessionEventHandler : MonoBehaviour
    {
        internal enum Basis
        {
            SharedSpatialAnchor, // Based on single SSA alignment
            RoomAnchors // Space Sharing for Scene anchors
        }
        [Tooltip("The basis alignment/common reference approach for colocation")]
        [SerializeField]
        internal Basis basis = Basis.SharedSpatialAnchor;
        [SerializeField]
        private GameObject AnchorPrefab;
        private ColocationController _colocationController;
        private SharedAnchorManager _sharedAnchorManager;
        private AlignCameraToAnchor _alignCameraToAnchor;
        private OVRCameraRig _cameraRig;

        [Serializable]
        private struct SpaceSharingInfo
        {
            internal Guid RoomId;
            internal Pose FloorAnchor;
        }

        private void Awake()
        {
#pragma warning disable CS0618 // Type or member is obsolete
            _colocationController = FindObjectOfType<ColocationController>();
            _cameraRig = FindObjectOfType<OVRCameraRig>();
#pragma warning restore CS0618 // Type or member is obsolete
#if META_MR_UTILITY_KIT_DEFINED
            if (basis == Basis.RoomAnchors)
            {
                LocalMatchmaking.BeforeStartHost = SpaceSharingBeforeHostStart;
            }
#endif // META_MR_UTILITY_KIT_DEFINED
        }

        private void Start()
        {
            switch (basis)
            {
                case Basis.SharedSpatialAnchor:
#pragma warning disable CS0618 // Type or member is obsolete
                    var ssaCore = FindObjectOfType<SharedSpatialAnchorCore>();
#pragma warning restore CS0618 // Type or member is obsolete
                    if (ssaCore == null)
                    {
                        throw new InvalidOperationException($"{nameof(SharedSpatialAnchorCore)} component is missing " +
                                                            "from the scene, add this component to allow anchor sharing.");
                    }
                    _sharedAnchorManager = new SharedAnchorManager(ssaCore);
                    if (_colocationController.DebuggingOptions.visualizeAlignmentAnchor)
                    {
                        _sharedAnchorManager.AnchorPrefab = AnchorPrefab;
                    }
                    LocalMatchmaking.OnSessionCreateSucceeded.AddListener(OnSessionCreatedWithSpatialAnchor);
                    LocalMatchmaking.OnSessionDiscoverSucceeded.AddListener(OnSessionDiscoveredWithSpatialAnchor);
                    break;
                case Basis.RoomAnchors:
#if META_MR_UTILITY_KIT_DEFINED
                    LocalMatchmaking.OnSessionCreateSucceeded.AddListener(OnSessionCreatedWithSpaceSharing);
                    LocalMatchmaking.OnSessionDiscoverSucceeded.AddListener(OnSessionDiscoveredWithSpaceSharing);
#endif // META_MR_UTILITY_KIT_DEFINED
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
            LocalMatchmaking.OnSessionCreateFailed.AddListener(Debug.LogError);
        }

        #region Event Handling for SSA based alignment
        // event handling as host
        private async void OnSessionCreatedWithSpatialAnchor(Guid groupUuid)
        {
            _ = await _sharedAnchorManager.CreateAlignmentAnchor();
            if (await _sharedAnchorManager.ShareAnchorsWithGroup(groupUuid))
            {
                _colocationController.ColocationReadyCallbacks?.Invoke();
                Logger.Log("Host has created and shared the alignment anchor, " +
                           "and is ready for colocation", LogLevel.Info);
            }
        }

        // event handling as guest
        private async void OnSessionDiscoveredWithSpatialAnchor(Guid groupUuid)
        {
            var anchors = await _sharedAnchorManager.RetrieveAnchorsFromGroup(groupUuid);
            if (anchors.Count != 0)
            {
                // align camera to anchors
                var alignCamera = _cameraRig.gameObject.AddComponent<AlignCameraToAnchor>();
                alignCamera.CameraAlignmentAnchor = anchors[0];
                alignCamera.RealignToAnchor();
                _colocationController.ColocationReadyCallbacks?.Invoke();
                Logger.Log("Guest has retrieved and aligned with the alignment anchor, " +
                           "and is ready for colocation", LogLevel.Info);
            }
        }
        #endregion

        #region Event Handling for Space Sharing based alignment

#if META_MR_UTILITY_KIT_DEFINED
        private async Task<bool> SpaceSharingBeforeHostStart()
        {
            if (!await RequestScenePermissionIfNeeded()) {
                return false;
            }
            return await LoadScene();
        }

        private async Task<bool> RequestScenePermissionIfNeeded() {
            if (!OVRPermissionsRequester.IsPermissionGranted(OVRPermissionsRequester.Permission.Scene))
            {
                var taskCompletion = new TaskCompletionSource<bool>();
                var permissionCallbacks = new PermissionCallbacks();
                permissionCallbacks.PermissionDenied += _ =>
                {
                    Logger.Log($"Host failed to load scene from device as permission denied by user", LogLevel.Error);
                    taskCompletion.SetResult(false);
                };
                permissionCallbacks.PermissionGranted += _ =>
                {
                    taskCompletion.SetResult(true);
                };
                Permission.RequestUserPermissions(new[]
                {
                    OVRPermissionsRequester.GetPermissionId(OVRPermissionsRequester.Permission.Scene)
                }, permissionCallbacks);
                await taskCompletion.Task;
                if (!taskCompletion.Task.Result)
                {
                    return false;
                }
            }
            return true;
        }

        private async Task<bool> LoadScene()
        {
            MRUK.Instance.RegisterSceneLoadedCallback(() =>
            {
                var currentRoom = MRUK.Instance.GetCurrentRoom();
                var roomAnchorTransform = currentRoom.FloorAnchor.transform;
                var spaceSharingInfo = new SpaceSharingInfo
                {
                    RoomId = currentRoom.Anchor.Uuid,
                    FloorAnchor = new Pose(roomAnchorTransform.position, roomAnchorTransform.rotation)
                };
                LocalMatchmaking.ExtraData = SerializationUtils.SerializeToString(spaceSharingInfo);
            });
            if (!MRUK.Instance.IsInitialized)
            {
                var loadDeviceResult = await MRUK.Instance.LoadSceneFromDevice();
                if (loadDeviceResult != MRUK.LoadDeviceResult.Success)
                {
                    Logger.Log($"Host failed to load scene from device: {loadDeviceResult}", LogLevel.Error);
                    return false;
                }
            }
            return true;
        }

        private async void OnSessionCreatedWithSpaceSharing(Guid groupUuid)
        {
            var currentRoom = MRUK.Instance.GetCurrentRoom();
            var shareResult = await MRUK.Instance.ShareRoomsAsync(new[]
            {
                currentRoom // we don't share all rooms but only current room as guest doesn't have a way to know all the room IDs.
            }, groupUuid);
            if (!shareResult.Success)
            {
                Logger.Log($"Host failed to share rooms with group {groupUuid} : {shareResult.Status}", LogLevel.Error);
            }
            else
            {
                if (_colocationController.DebuggingOptions.visualizeAlignmentAnchor)
                {
                    Instantiate(AnchorPrefab, currentRoom.FloorAnchor.transform);
                }
                Logger.Log($"Host successfully shared rooms with group {groupUuid}", LogLevel.Info);
            }
        }

        private async void OnSessionDiscoveredWithSpaceSharing(Guid groupUuid)
        {
            if (string.IsNullOrEmpty(LocalMatchmaking.ExtraData))
            {
                Logger.Log($"Guest failed to load the data for space sharing from group sharing", LogLevel.Error);
                return;
            }
            SpaceSharingInfo spaceSharingInfo;
            try
            {
                spaceSharingInfo = SerializationUtils.DeserializeFromString<SpaceSharingInfo>(LocalMatchmaking.ExtraData);
            }
            catch (Exception e)
            {
                Logger.Log($"Guest failed to parse the data for space sharing from group : {LocalMatchmaking.ExtraData}, {e}", LogLevel.Error);
                return;
            }
            var loadResult = await MRUK.Instance.LoadSceneFromSharedRooms(new[]
            {
                spaceSharingInfo.RoomId // note only current room is shared
            }, groupUuid, (spaceSharingInfo.RoomId, spaceSharingInfo.FloorAnchor));
            if (loadResult != MRUK.LoadDeviceResult.Success)
            {
                Logger.Log($"Failed to load scene from shared room: {loadResult}", LogLevel.Error);
            }
            else
            {
                Logger.Log("Guest has successfully loaded the shared room and is ready for colocation", LogLevel.Info);
                if (_colocationController.DebuggingOptions.visualizeAlignmentAnchor)
                {
                    Instantiate(AnchorPrefab, MRUK.Instance.GetCurrentRoom().FloorAnchor.transform);
                }
            }
        }
#endif // META_MR_UTILITY_KIT_DEFINED
        #endregion
        private void OnDestroy()
        {
            switch (basis)
            {
                case Basis.SharedSpatialAnchor:
                    LocalMatchmaking.OnSessionCreateSucceeded.RemoveListener(OnSessionCreatedWithSpatialAnchor);
                    LocalMatchmaking.OnSessionDiscoverSucceeded.RemoveListener(OnSessionDiscoveredWithSpatialAnchor);
                    break;
                case Basis.RoomAnchors:
#if META_MR_UTILITY_KIT_DEFINED
                    LocalMatchmaking.OnSessionCreateSucceeded.RemoveListener(OnSessionCreatedWithSpaceSharing);
                    LocalMatchmaking.OnSessionDiscoverSucceeded.RemoveListener(OnSessionDiscoveredWithSpaceSharing);
                    LocalMatchmaking.BeforeStartHost = null;
#endif // META_MR_UTILITY_KIT_DEFINED
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }
}
