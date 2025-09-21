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
using UnityEngine;

namespace Meta.XR.MultiplayerBlocks.Colocation
{
    /// <summary>
    ///     Class that handles creating and joining a colocated space
    /// </summary>
    internal class AutomaticColocationLauncher
    {
        public event Action ColocationReady;
        public event Action<ColocationFailedReason> ColocationFailed;

        private GameObject _cameraRig;
        private TaskCompletionSource<bool> _alignToAnchorTask;
        private OVRSpatialAnchor _myAlignmentAnchor;

        private ulong _myPlayerId;
        private ulong _myOculusId;

        private INetworkData _networkData;
        private INetworkMessenger _networkMessenger;

        private ulong _oculusIdToColocateTo;
        private SharedAnchorManager _sharedAnchorManager;

        public void Init(
            INetworkData networkData,
            INetworkMessenger networkMessenger,
            SharedAnchorManager sharedAnchorManager,
            GameObject cameraRig,
            ulong myPlayerId,
            ulong myOculusId
        )
        {
            Logger.Log($"{nameof(AutomaticColocationLauncher)}: Init function called", LogLevel.Verbose);
            _networkData = networkData;
            _networkMessenger = networkMessenger;

            _networkMessenger.AnchorShareRequestReceived += OnAnchorShareRequestReceived;
            _networkMessenger.AnchorShareRequestCompleted += OnAnchorShareRequestCompleted;

            _sharedAnchorManager = sharedAnchorManager;
            _cameraRig = cameraRig;

            _myPlayerId = myPlayerId;
            _myOculusId = myOculusId;
        }

        public void ColocateAutomatically()
        {
            ColocateAutomaticallyInternal();
        }

        public void ColocateByPlayerWithOculusId(ulong oculusId)
        {
            ColocateByPlayerWithOculusIdInternal(oculusId);
        }

        public void CreateColocatedSpace()
        {
            CreateColocatedSpaceInternal();
        }

        private async void ColocateAutomaticallyInternal()
        {
            Logger.Log($"{nameof(AutomaticColocationLauncher)}: Called Init Anchor Flow", LogLevel.Verbose);
            var successfullyAlignedToAnchor = false;

            List<Anchor> alignmentAnchors = GetAllAlignmentAnchors();
            foreach (var anchor in alignmentAnchors)
            {
                if (await ShareAndLocalizeAnchor(anchor))
                {
                    successfullyAlignedToAnchor = true;
                    Logger.Log(
                        $"{nameof(AutomaticColocationLauncher)}: successfully aligned to anchor with id: {anchor.automaticAnchorUuid}",
                        LogLevel.Info);
                    _networkData.AddPlayer(new Player(_myPlayerId, _myOculusId, anchor.colocationGroupId));
                    AlignPlayerToAnchor();
                    ColocationReady?.Invoke();
                    break;
                }
            }

            if (!successfullyAlignedToAnchor)
            {
                CreateNewColocatedSpace();
            }
        }

        private async void ColocateByPlayerWithOculusIdInternal(ulong oculusId)
        {
            Anchor? anchorToAlignTo = FindAlignmentAnchorUsedByOculusId(oculusId);
            if (anchorToAlignTo == null)
            {
                Logger.Log(
                    $"{nameof(AutomaticColocationLauncher)}: Unable to find alignment anchor used by oculusId {oculusId}",
                    LogLevel.Error);
                return;
            }

            bool result = await ShareAndLocalizeAnchor(anchorToAlignTo.Value);
            if (result)
            {
                Logger.Log(
                    $"{nameof(AutomaticColocationLauncher)}: successfully aligned to anchor with id: {anchorToAlignTo.Value.automaticAnchorUuid}",
                    LogLevel.Verbose);
                _networkData.AddPlayer(new Player(_myPlayerId, _myOculusId, anchorToAlignTo.Value.colocationGroupId));
            }
            else
            {
                Logger.Log(
                    $"{nameof(AutomaticColocationLauncher)}: ColocateByPlayerWithOculusIdInternal: Failed to ShareAndLocalizeToAnchor",
                    LogLevel.Verbose);
                return;
            }

            AlignPlayerToAnchor();
            ColocationReady?.Invoke();
        }

        private Anchor? FindAlignmentAnchorUsedByOculusId(ulong oculusId)
        {
            List<Player> players = _networkData.GetAllPlayers();
            uint? colocationGroupId = null;

            foreach (var player in players)
                if (oculusId == player.oculusId)
                {
                    colocationGroupId = player.colocationGroupId;
                }

            if (colocationGroupId == null)
            {
                Logger.Log(
                    $"{nameof(AutomaticColocationLauncher)}: Could not find the colocated group belonging to oculusId: {oculusId}",
                    LogLevel.Error);
                return null;
            }

            List<Anchor> anchors = _networkData.GetAllAnchors();
            foreach (var anchor in anchors)
                if (colocationGroupId.Value == anchor.colocationGroupId)
                {
                    return anchor;
                }

            Logger.Log(
                $"{nameof(AutomaticColocationLauncher)}: Could not find the anchor belonging on colocationGroupId: {colocationGroupId}",
                LogLevel.Error);
            return null;
        }

        private void CreateColocatedSpaceInternal()
        {
            CreateNewColocatedSpace();
        }

        private async void CreateNewColocatedSpace()
        {
            _myAlignmentAnchor = await _sharedAnchorManager.CreateAlignmentAnchor();
            if (_myAlignmentAnchor == null)
            {
                Logger.Log($"{nameof(AutomaticColocationLauncher)}: Could not create the anchor", LogLevel.Error);
                ColocationFailed?.Invoke(ColocationFailedReason.AutomaticFailedToCreateAnchor);
                return;
            }

            uint newColocationGroupId = _networkData.GetColocationGroupCount();
            _networkData.IncrementColocationGroupCount();
            _networkData.AddAnchor(new Anchor(true, true, _myOculusId, newColocationGroupId,
                _myAlignmentAnchor.Uuid));
            _networkData.AddPlayer(new Player(_myPlayerId, _myOculusId, newColocationGroupId));
            AlignPlayerToAnchor();
            ColocationReady?.Invoke();
        }

        private void AlignPlayerToAnchor()
        {
            Logger.Log($"{nameof(AutomaticColocationLauncher)} AlignPlayerToAnchor was called", LogLevel.Verbose);

            AlignCameraToAnchor alignCamera = _cameraRig.AddComponent<AlignCameraToAnchor>();
            alignCamera.CameraAlignmentAnchor = _myAlignmentAnchor;
            alignCamera.RealignToAnchor();
        }

        private List<Anchor> GetAllAlignmentAnchors()
        {
            var alignmentAnchors = new List<Anchor>();
            List<Anchor> allAnchors = _networkData.GetAllAnchors();
            foreach (var anchor in allAnchors)
            {
                if (anchor.isAlignmentAnchor)
                {
                    alignmentAnchors.Add(anchor);
                }
            }

            return alignmentAnchors;
        }

        private Task<bool> ShareAndLocalizeAnchor(Anchor anchor)
        {
            _alignToAnchorTask = new TaskCompletionSource<bool>();
            SendAnchorShareRequest(anchor);
            return _alignToAnchorTask.Task;
        }

        private void SendAnchorShareRequest(Anchor anchor)
        {
            Logger.Log(
                $"{nameof(AutomaticColocationLauncher)}: Called {nameof(SendAnchorShareRequest)} with anchor id: {anchor.automaticAnchorUuid}, playerId: {_myPlayerId}, oculusId: {_myOculusId}",
                LogLevel.Verbose
            );

            Player? owner = _networkData.GetPlayerWithOculusId(anchor.ownerOculusId);
            if (owner == null)
            {
                Logger.Log(
                    $"{nameof(AutomaticColocationLauncher)}: Anchor owner {anchor.ownerOculusId} isn't connected.",
                    LogLevel.Error);
                _alignToAnchorTask.TrySetResult(false);
            }

            ulong ownerPlayerId = owner.Value.playerId;

            Logger.Log(
                $"{nameof(AutomaticColocationLauncher)}: Request anchor sharing from playerId: {ownerPlayerId}, oculusId: {anchor.ownerOculusId}",
                LogLevel.Info);

            _networkMessenger.SendAnchorShareRequest(ownerPlayerId,
                new ShareAndLocalizeParams(_myPlayerId, _myOculusId, anchor.automaticAnchorUuid));
        }

        private async void OnAnchorShareRequestReceived(ShareAndLocalizeParams shareAndLocalizeParams)
        {
            Logger.Log(
                $"{nameof(AutomaticColocationLauncher)}: Called {nameof(OnAnchorShareRequestReceived)} with playerId: {_myPlayerId}, oculusId: {_myOculusId}",
                LogLevel.Info);

            bool isAnchorSharedSuccessfully =
                await _sharedAnchorManager.ShareAnchorsWithUser(shareAndLocalizeParams.requestingPlayerOculusId);
            Logger.Log($"{nameof(AutomaticColocationLauncher)}: Anchor Shared: {isAnchorSharedSuccessfully}",
                LogLevel.Info);

            shareAndLocalizeParams.anchorFlowSucceeded = isAnchorSharedSuccessfully;
            _networkMessenger.SendAnchorShareCompleted(shareAndLocalizeParams.requestingPlayerId,
                shareAndLocalizeParams);
        }

        private void OnAnchorShareRequestCompleted(ShareAndLocalizeParams shareAndLocalizeParams)
        {
            Logger.Log(
                $"{nameof(AutomaticColocationLauncher)}: Called {nameof(OnAnchorShareRequestCompleted)} with playerId: {_myPlayerId}, oculusId: {_myOculusId}",
                LogLevel.Info);

            if (!shareAndLocalizeParams.anchorFlowSucceeded)
            {
                Logger.Log($"{nameof(AutomaticColocationLauncher)}: Anchor flow failed.", LogLevel.Error);
                ColocationFailed?.Invoke(ColocationFailedReason.AutomaticFailedToShareAnchor);
                _alignToAnchorTask.TrySetResult(false);
                return;
            }

            var sharedAnchorId = new Guid(shareAndLocalizeParams.anchorUUID.ToString());
            LocalizeAnchor(sharedAnchorId);
        }

        private async void LocalizeAnchor(Guid anchorToLocalize)
        {
            Logger.Log($"{nameof(AutomaticColocationLauncher)}: Localize Anchor Called id: {_myOculusId}", LogLevel.Verbose);
            IReadOnlyList<OVRSpatialAnchor> sharedAnchors = null;
            var anchorIds = new List<Guid> { anchorToLocalize };
            sharedAnchors = await _sharedAnchorManager.RetrieveAnchors(anchorIds);
            if (sharedAnchors == null || sharedAnchors.Count == 0)
            {
                Logger.Log($"{nameof(AutomaticColocationLauncher)}: Retrieving Anchors Failed", LogLevel.Error);
                ColocationFailed?.Invoke(ColocationFailedReason.AutomaticFailedToLocalizeAnchor);
                _alignToAnchorTask.TrySetResult(false);
            }
            else
            {
                Logger.Log($"{nameof(AutomaticColocationLauncher)}: Localizing Anchors is Successful",
                    LogLevel.Verbose);
                // Here we take the first anchor that was shared
                _myAlignmentAnchor = sharedAnchors[0];
                _alignToAnchorTask.TrySetResult(true);
            }
        }
    }
}
