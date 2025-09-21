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
using System.Linq;
using System.Threading.Tasks;
using Meta.XR.BuildingBlocks.Editor;
using Meta.XR.MultiplayerBlocks.Shared.Editor;
using UnityEngine;
#if FUSION_WEAVER && FUSION2
using Fusion;
#if PHOTON_VOICE_DEFINED
using Photon.Voice.Fusion;
#endif // PHOTON_VOICE_DEFINED
#endif // FUSION_WEAVER && FUSION2

namespace Meta.XR.MultiplayerBlocks.Fusion.Editor
{
    public class PlayerVoiceChatInstallationRoutine : NetworkInstallationRoutine
    {
#pragma warning disable CS1998
        public override async Task<List<GameObject>> InstallAsync(BlockData blockData, GameObject selectedGameObject)
#pragma warning restore CS1998
        {
#if FUSION_WEAVER && FUSION2
            var multiplayerVoiceBlockDataGOs = await base.InstallAsync(blockData, selectedGameObject);
            var cameraRigBB = Utils.GetBlocksWithType<OVRCameraRig>().First();
            var networkRunnerBB = Utils.GetBlocksWithType<NetworkRunner>().First();

            if (cameraRigBB == null || networkRunnerBB == null) {
                Debug.LogWarning("Some blocks could not be found, aborting installing Multiplayer Voice block");
                return new List<GameObject>();
            }

#if PHOTON_VOICE_DEFINED
            if (networkRunnerBB.gameObject.TryGetComponent<FusionVoiceClient>(out _) == false)
            {
                networkRunnerBB.gameObject.AddComponent<FusionVoiceClient>();
            }

            var ovrCameraRig = cameraRigBB.GetComponent<OVRCameraRig>();
            multiplayerVoiceBlockDataGOs[0].GetComponent<VoiceSetup>().centerEyeAnchor = ovrCameraRig.centerEyeAnchor;
            return multiplayerVoiceBlockDataGOs;
#else
            throw new Exception("Photon Voice2 package not installed, cannot use this block");
#endif // PHOTON_VOICE_DEFINED
#else
            throw new Exception("Photon Fusion package not installed, cannot use this block");
#endif // FUSION_WEAVER && FUSION2
        }

        internal override IReadOnlyCollection<InstallationStepInfo> GetInstallationSteps(VariantsSelection selection)
        {
            var installationSteps = new List<InstallationStepInfo>();
            installationSteps.AddRange(base.GetInstallationSteps(selection));
            var networkManagerBlockData = Utils.GetBlockData(BlockDataIds.INetworkManager);
            installationSteps.Add(new InstallationStepInfo(networkManagerBlockData, "Add <b>FusionVoiceClient</b> component to the {0} game object"));
            installationSteps.Add(new InstallationStepInfo(null, $"Setup the <b>VoiceSetup.centerEyeAnchor</b> of the installed prefab to <b>OVRCameraRig.centerEyeAnchor</b>"));
            return installationSteps;
        }
    }
}
