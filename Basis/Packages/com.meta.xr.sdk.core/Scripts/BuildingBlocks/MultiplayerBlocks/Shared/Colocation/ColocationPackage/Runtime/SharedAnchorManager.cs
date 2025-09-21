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
using System.Threading.Tasks;
using Meta.XR.BuildingBlocks;
using UnityEngine;
using UnityEngine.Assertions;

namespace Meta.XR.MultiplayerBlocks.Colocation
{
    internal class SharedAnchorManager
    {
        /// <summary>
        ///   Handles interacting with the SharedSpatialAnchorCore API to create, save, localize, and share a OVRSpatialAnchor
        /// </summary>
        private readonly List<OVRSpatialAnchor> _localAnchors = new();

        private readonly List<OVRSpatialAnchor> _sharedAnchors = new();

        private readonly HashSet<OVRSpaceUser> _userShareList = new();

        private const int SaveAnchorWaitTimeThreshold = 10000;
        private bool _saveAnchorSaveToCloudIsSuccessful;

        private const int ShareAnchorWaitTimeThreshold = 10000;
        private bool _shareAnchorIsSuccessful;

        private const int RetrieveAnchorWaitTimeThreshold = 10000;
        private bool _retrieveAnchorIsSuccessful;

        private List<Task> _localizationTasks;
        private List<TaskCompletionSource<bool>> _localizationTcsList;

        public GameObject AnchorPrefab { get; set; }
        public IReadOnlyList<OVRSpatialAnchor> LocalAnchors => _localAnchors;
        private SharedSpatialAnchorCore _ssaCore;

        public SharedAnchorManager(SharedSpatialAnchorCore ssaCore)
        {
            _ssaCore = ssaCore;
        }

        #region Create Anchors
        public async Task<OVRSpatialAnchor> CreateAlignmentAnchor()
        {
            var (anchor, result) = await CreateAnchor(Vector3.zero, Quaternion.identity);
            if (anchor == null)
            {
                Logger.Log($"{nameof(AutomaticColocationLauncher)}: _sharedAnchorManager.CreateAnchor returned null",
                    LogLevel.Error);
                return null;
            }

            bool isAnchorSavedToCloud = result is not
                (OVRSpatialAnchor.OperationResult.Failure_SpaceNetworkTimeout
                or OVRSpatialAnchor.OperationResult.Failure_SpaceCloudStorageDisabled
                or OVRSpatialAnchor.OperationResult.Failure_SpaceNetworkRequestFailed);

            if (!isAnchorSavedToCloud)
            {
                Logger.Log($"{nameof(AutomaticColocationLauncher)}: We did not save the local anchor to the cloud", LogLevel.SharedSpatialAnchorsError);
                return null;
            }

            if (result != OVRSpatialAnchor.OperationResult.Success)
            {
                Logger.Log($"{nameof(AutomaticColocationLauncher)}: Anchor creation failed with result: {result}.", LogLevel.SharedSpatialAnchorsError);
                return null;
            }

            Logger.Log($"ColocationLauncher: Anchor created: {anchor.Uuid}", LogLevel.Verbose);

            return anchor;
        }

        private async Task<(OVRSpatialAnchor, OVRSpatialAnchor.OperationResult)> CreateAnchor(Vector3 position, Quaternion orientation)
        {
            Logger.Log($"{nameof(SharedAnchorManager)}: Attempt to InstantiateAnchor", LogLevel.Verbose);

            var (anchor, result) = await AnchorCreationTask(position, orientation);
            if (!anchor || !anchor.Created || result != OVRSpatialAnchor.OperationResult.Success)
            {
                Logger.Log($"{nameof(SharedAnchorManager)}: Anchor creation failed with result: {result}", LogLevel.SharedSpatialAnchorsError);
                return (null, result);
            }

            Logger.Log($"{nameof(SharedAnchorManager)}: Created anchor with id {anchor.Uuid}", LogLevel.Info);

            _localAnchors.Add(anchor);
            return (anchor, result);
        }

        private async Task<(OVRSpatialAnchor, OVRSpatialAnchor.OperationResult)> AnchorCreationTask(Vector3 position, Quaternion orientation)
        {
            _saveAnchorSaveToCloudIsSuccessful = false;
            CheckIfSavingAnchorsServiceHung();
            var task = new TaskCompletionSource<(OVRSpatialAnchor, OVRSpatialAnchor.OperationResult)>();
            void CreateCompletedCallback(OVRSpatialAnchor anchor, OVRSpatialAnchor.OperationResult result)
            {
                _saveAnchorSaveToCloudIsSuccessful = true;
                task.TrySetResult((anchor, result));
            }
            _ssaCore.OnAnchorCreateCompleted.AddListener(CreateCompletedCallback);
            _ssaCore.InstantiateSpatialAnchor(AnchorPrefab, position, orientation);
            var result = await task.Task;
            _ssaCore.OnAnchorCreateCompleted.RemoveListener(CreateCompletedCallback);
            return result;
        }

        private async void CheckIfSavingAnchorsServiceHung()
        {
            await Task.Delay(SaveAnchorWaitTimeThreshold);
            if (!_saveAnchorSaveToCloudIsSuccessful)
            {
                Logger.Log(
                    $"SharedAnchorManager: It has been {SaveAnchorWaitTimeThreshold}ms since attempting to save to the cloud. Anchors service may have failed",
                    LogLevel.Warning);
            }
        }

        #endregion

        #region Retrieve Anchors

        public async Task<IReadOnlyList<OVRSpatialAnchor>> RetrieveAnchorsFromGroup(Guid groupUuid)
        {
            var task = new TaskCompletionSource<IReadOnlyList<OVRSpatialAnchor>>();
            _retrieveAnchorIsSuccessful = false;
            CheckIfRetrievingAnchorServiceHung();
            void LoadCompletedCallback(List<OVRSpatialAnchor> loadedAnchors, OVRSpatialAnchor.OperationResult result)
            {
                if (result == OVRSpatialAnchor.OperationResult.Success)
                {
                    _retrieveAnchorIsSuccessful = true;
                    _sharedAnchors.AddRange(loadedAnchors);
                    task.TrySetResult(loadedAnchors);
                }
            }
            _ssaCore.OnSharedSpatialAnchorsLoadCompleted.AddListener(LoadCompletedCallback);
            _ssaCore.LoadAndInstantiateAnchorsFromGroup(AnchorPrefab, groupUuid);
            var result = await task.Task;
            _ssaCore.OnSharedSpatialAnchorsLoadCompleted.RemoveListener(LoadCompletedCallback);
            return result;
        }
        public async Task<IReadOnlyList<OVRSpatialAnchor>> RetrieveAnchors(List<Guid> anchorIds)
        {
            Assert.IsTrue(anchorIds.Count <= OVRPlugin.SpaceFilterInfoIdsMaxSize,
                "SpaceFilterInfoIdsMaxSize exceeded.");

            var task = new TaskCompletionSource<IReadOnlyList<OVRSpatialAnchor>>();
            _retrieveAnchorIsSuccessful = false;
            CheckIfRetrievingAnchorServiceHung();
            Logger.Log($"{nameof(SharedAnchorManager)}: Querying anchors: {string.Join(", ", anchorIds)}", LogLevel.Verbose);
            void LoadCompletedCallback(List<OVRSpatialAnchor> loadedAnchors, OVRSpatialAnchor.OperationResult result)
            {
                if (result == OVRSpatialAnchor.OperationResult.Success)
                {
                    _retrieveAnchorIsSuccessful = true;
                    _sharedAnchors.AddRange(loadedAnchors);
                    task.TrySetResult(loadedAnchors);
                }
            }
            _ssaCore.OnSharedSpatialAnchorsLoadCompleted.AddListener(LoadCompletedCallback);
            _ssaCore.LoadAndInstantiateAnchors(AnchorPrefab, anchorIds);
            var result = await task.Task;
            _ssaCore.OnSharedSpatialAnchorsLoadCompleted.RemoveListener(LoadCompletedCallback);
            return result;
        }

        private async void CheckIfRetrievingAnchorServiceHung()
        {
            await Task.Delay(RetrieveAnchorWaitTimeThreshold);
            if (!_retrieveAnchorIsSuccessful)
            {
                Logger.Log(
                    $"{nameof(SharedAnchorManager)}: It has been {RetrieveAnchorWaitTimeThreshold}ms since attempting to retrieve anchor(s). Anchors service may have failed",
                    LogLevel.Warning);
            }
        }
        #endregion

        #region Share Anchors

        public async Task<bool> ShareAnchorsWithGroup(Guid groupUuid)
        {
            _shareAnchorIsSuccessful = false;
            CheckIfSharingAnchorServiceHung();
            var task = new TaskCompletionSource<bool>();
            void ShareToGroupCompletedCallback(List<OVRSpatialAnchor> _, OVRAnchor.ShareResult result)
            {
                Logger.Log($"{nameof(SharedAnchorManager)}: result of sharing the anchor is {result}", LogLevel.Verbose);
                task.TrySetResult(result == OVRAnchor.ShareResult.Success);
                _shareAnchorIsSuccessful = true;
            }
            _ssaCore.OnSpatialAnchorsShareToGroupCompleted.AddListener(ShareToGroupCompletedCallback);
            _ssaCore.ShareSpatialAnchors(_localAnchors, groupUuid);
            bool result = await task.Task;
            _ssaCore.OnSpatialAnchorsShareToGroupCompleted.RemoveListener(ShareToGroupCompletedCallback);
            return result;
        }

        public async Task<bool> ShareAnchorsWithUser(ulong userId)
        {
            if (!OVRSpaceUser.TryCreate(userId, out var spaceUser))
            {
                Logger.Log($"{nameof(SharedAnchorManager)}: Failed to create space user using user id {userId}.", LogLevel.Warning);
                return false;
            }

            _userShareList.Add(spaceUser);


            if (_localAnchors.Count == 0)
            {
                Logger.Log($"{nameof(SharedAnchorManager)}: No anchors to share.", LogLevel.Warning);
                return true;
            }

            Logger.Log($"{nameof(SharedAnchorManager)}: Sharing {_localAnchors.Count} anchors with users: {userId}",
                LogLevel.Verbose);
            var users = new List<OVRSpaceUser>();
            users.AddRange(_userShareList);

            _shareAnchorIsSuccessful = false;
            CheckIfSharingAnchorServiceHung();
            var task = new TaskCompletionSource<bool>();
            void ShareCompleteCallback(List<OVRSpatialAnchor> _, OVRSpatialAnchor.OperationResult result)
            {
                Logger.Log($"{nameof(SharedAnchorManager)}: result of sharing the anchor is {result}", LogLevel.Verbose);
                task.TrySetResult(result == OVRSpatialAnchor.OperationResult.Success);
                _shareAnchorIsSuccessful = true;
            }
            _ssaCore.OnSpatialAnchorsShareCompleted.AddListener(ShareCompleteCallback);
            _ssaCore.ShareSpatialAnchors(_localAnchors, users);
            bool result = await task.Task;
            _ssaCore.OnSpatialAnchorsShareCompleted.RemoveListener(ShareCompleteCallback);
            return result;
        }

        private async void CheckIfSharingAnchorServiceHung()
        {
            await Task.Delay(ShareAnchorWaitTimeThreshold);
            if (!_shareAnchorIsSuccessful)
            {
                Logger.Log(
                    $"{nameof(SharedAnchorManager)}: It has been {ShareAnchorWaitTimeThreshold}ms since attempting to share anchor(s). Anchors service may have failed",
                    LogLevel.Warning);
            }
        }

        public void StopSharingAnchorsWithUser(ulong userId)
        {
            _userShareList.RemoveWhere(el => el.Id == userId);
        }
        #endregion
    }
}
