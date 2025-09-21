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

using Meta.XR.MultiplayerBlocks.Shared;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Assertions;
using Logger = Meta.XR.MultiplayerBlocks.Colocation.Logger;
using LogLevel = Meta.XR.MultiplayerBlocks.Colocation.LogLevel;

namespace Meta.XR.MultiplayerBlocks.NGO
{
    /// <summary>
    /// A helper class that handles setting up and initializing colocation when using the Unity Netcode for Gameobjects networking framework.
    /// Once this networked game object is spawned this class starts the process for getting the player's Meta account and starts
    /// the colocation routines.
    /// </summary>
    public class NGONetworkBootstrapper : NetworkBehaviour
    {
        [SerializeField] private GameObject anchorPrefab;
        [SerializeField] private NetcodeGameObjectsNetworkData networkData;
        [SerializeField] private NetcodeGameObjectsMessenger networkMessenger;

        private NetworkBootstrapperParams _params;

        private void Awake()
        {
            Assert.IsNotNull(networkData, $"{nameof(networkData)} cannot be null.");
            Assert.IsNotNull(networkMessenger, $"{nameof(networkMessenger)} cannot be null.");

            _params.ovrCameraRig = FindObjectOfType<OVRCameraRig>();
            _params.colocationController = FindObjectOfType<ColocationController>();
            _params.setupColocationReadyEvents = () =>
            {
                _params.colocationLauncher.ColocationReady += OnColocationReady;
            };
        }

        public override void OnNetworkSpawn()
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
            Logger.Log($"{nameof(NGONetworkBootstrapper)}: Colocation is successful and ready", LogLevel.Info);
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (networkMessenger == null)
            {
                networkMessenger = GetComponent<NetcodeGameObjectsMessenger>();
            }

            if (networkData == null)
            {
                networkData = GetComponent<NetcodeGameObjectsNetworkData>();
            }
        }
#endif // UNITY_EDITOR
    }
}
