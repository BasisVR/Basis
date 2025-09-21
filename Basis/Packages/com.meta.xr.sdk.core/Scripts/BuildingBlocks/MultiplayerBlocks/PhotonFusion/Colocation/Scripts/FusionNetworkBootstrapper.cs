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

using Meta.XR.MultiplayerBlocks.Colocation.Fusion;
using Fusion;
using Meta.XR.MultiplayerBlocks.Shared;
using UnityEngine;
using UnityAssert = UnityEngine.Assertions.Assert;
using Logger = Meta.XR.MultiplayerBlocks.Colocation.Logger;
using LogLevel = Meta.XR.MultiplayerBlocks.Colocation.LogLevel;

namespace Meta.XR.MultiplayerBlocks.Fusion
{
    /// <summary>
    /// Helper class that handles setting up and initializing colocation when using the Photon Fusion networking framework.
    /// Once this networked game object is spawned this class starts the process for getting the players oculus account and starts
    /// the colocation routines.
    /// </summary>
    public class FusionNetworkBootstrapper : NetworkBehaviour
    {
        [SerializeField] private GameObject anchorPrefab;
        [SerializeField] private FusionNetworkData networkData;
        [SerializeField] private FusionMessenger networkMessenger;

        private NetworkBootstrapperParams _params;

        private void Awake()
        {
            UnityAssert.IsNotNull(networkData, $"{nameof(networkData)} cannot be null.");
            UnityAssert.IsNotNull(networkMessenger, $"{nameof(networkMessenger)} cannot be null.");

            _params.ovrCameraRig = FindObjectOfType<OVRCameraRig>();
            _params.colocationController = FindObjectOfType<ColocationController>();
            _params.setupColocationReadyEvents = () =>
            {
                _params.colocationLauncher.ColocationReady += OnColocationReady;
            };
        }

        public override void Spawned()
        {
#if META_PLATFORM_SDK_DEFINED
            PlatformInit.GetEntitlementInformation(info =>
            {
                if (info.OculusUser != null)
                {
                    NetworkBootstrapperUtils.SetEntitlementIds(info, ref _params);
                    NetworkBootstrapperUtils.SetUpAndStartAutomaticColocation(ref _params, anchorPrefab, networkData, networkMessenger);
                }
            });
#else
            Logger.Log("Meta Platform SDK is not installed, cannot retrieve user name for Colocation block", LogLevel.Error);
#endif // META_PLATFORM_SDK_DEFINED
        }

        private void OnColocationReady()
        {
            if (_params.colocationController != null)
            {
                _params.colocationController.ColocationReadyCallbacks.Invoke();
            }
            Logger.Log($"{nameof(FusionNetworkBootstrapper)}: Colocation is successful and ready", LogLevel.Info);
        }
    }
}
