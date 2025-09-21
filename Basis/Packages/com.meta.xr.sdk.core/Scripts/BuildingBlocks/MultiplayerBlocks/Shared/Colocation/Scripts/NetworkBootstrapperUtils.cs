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
using Meta.XR.MultiplayerBlocks.Colocation;
using Meta.XR.BuildingBlocks;
using UnityEngine;
using Logger = Meta.XR.MultiplayerBlocks.Colocation.Logger;
using Object = UnityEngine.Object;
#if META_MR_UTILITY_KIT_DEFINED
using Meta.XR.MRUtilityKit;
#endif // META_MR_UTILITY_KIT_DEFINED

namespace Meta.XR.MultiplayerBlocks.Shared
{
    internal struct NetworkBootstrapperParams
    {
        public ulong myPlayerId;
        public ulong myOculusId;
        public OVRCameraRig ovrCameraRig;
        public SharedAnchorManager sharedAnchorManager;
        public AutomaticColocationLauncher colocationLauncher;
        public ColocationController colocationController;
        public Action setupColocationReadyEvents;
    }

    internal static class NetworkBootstrapperUtils
    {
#if META_PLATFORM_SDK_DEFINED
        public static void SetEntitlementIds(PlatformInfo info, ref NetworkBootstrapperParams param)
        {
            param.myOculusId = info.OculusUser.ID;
#if UNITY_EDITOR // In Editor developers likely using XRSim to open different sessions on the same device, need distinguish
            param.myPlayerId = (ulong)Guid.NewGuid().GetHashCode();
#else
            param.myPlayerId = (ulong)SystemInfo.deviceUniqueIdentifier.GetHashCode();
#endif
        }
#endif // META_PLATFORM_SDK_DEFINED

        public static void SetUpAndStartAutomaticColocation(ref NetworkBootstrapperParams param, GameObject anchorPrefab, INetworkData networkData, INetworkMessenger networkMessenger)
        {
            networkMessenger.RegisterLocalPlayer(param.myPlayerId);

#pragma warning disable CS0618 // Type or member is obsolete
            var ssaCore = Object.FindObjectOfType<SharedSpatialAnchorCore>();
#pragma warning restore CS0618 // Type or member is obsolete
            if (ssaCore == null)
            {
                Debug.LogWarning($"{nameof(SharedSpatialAnchorCore)} component is missing from the scene, add this component to allow anchor sharing.");
                return;
            }
            param.sharedAnchorManager = new SharedAnchorManager(ssaCore);

            if (param.colocationController != null && param.colocationController.DebuggingOptions.visualizeAlignmentAnchor)
            {
                param.sharedAnchorManager.AnchorPrefab = anchorPrefab ?? new GameObject();
            }

            NetworkAdapter.SetConfig(networkData, networkMessenger);
#if META_MR_UTILITY_KIT_DEFINED
            // If using Colocation, MRUK shouldn't have world lock enabled as both
            // of World Lock and Colocation are using CameraRig to adjust positions.
            // For colocated guest player, they shouldn't use Scene as their cameraRig
            // is aligned with host player and has offset with real-world scene anchors.
            // Host player should be the one interacting with scene as the source of truth.
            if (MRUK.Instance != null)
            {
                MRUK.Instance.EnableWorldLock = false;
            }
#endif // META_MR_UTILITY_KIT_DEFINED
            param.colocationLauncher = new AutomaticColocationLauncher();
            param.colocationLauncher.Init(
                NetworkAdapter.NetworkData,
                NetworkAdapter.NetworkMessenger,
                param.sharedAnchorManager,
                param.ovrCameraRig.gameObject,
                param.myPlayerId,
                param.myOculusId
            );

            param.setupColocationReadyEvents.Invoke(); // reference struct cannot be accessed in lambda
            param.colocationLauncher.ColocationFailed += OnColocationFailed;
            param.colocationLauncher.ColocateAutomatically();
        }

        private static void OnColocationFailed(ColocationFailedReason e)
        {
            Logger.Log($"Colocation failed - {e}", LogLevel.Error);
        }
    }
}
