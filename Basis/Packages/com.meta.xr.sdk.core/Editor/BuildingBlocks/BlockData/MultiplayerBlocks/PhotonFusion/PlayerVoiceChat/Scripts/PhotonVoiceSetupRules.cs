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

#if FUSION_WEAVER && FUSION2 && PHOTON_VOICE_DEFINED
using Fusion.Photon.Realtime;
using System.Linq;
using UnityEditor;
using Meta.XR.BuildingBlocks.Editor;

namespace Meta.XR.MultiplayerBlocks.Fusion.Editor
{
    [InitializeOnLoad]
    public static class PhotonVoiceSetupRules
    {
        static PhotonVoiceSetupRules()
        {
            OVRProjectSetup.AddTask(
                level: OVRProjectSetup.TaskLevel.Required,
                group: OVRProjectSetup.TaskGroup.Features,
                isDone: _ =>
                {
                    if (!IsPhotonVoicePresentInScene)
                    {
                        return true;
                    }
                    if (!PhotonAppSettings.TryGetGlobal(out PhotonAppSettings settings))
                    {
                        return true; // cannot found PhotonAppSettings, fix not actionable
                    }
                    return !string.IsNullOrEmpty(settings.AppSettings.AppIdVoice);
                },
                message:
                "When using Player Voice Chat with Photon Voice2 you must add your Voice AppId via menu of Tools > Fusion > Realtime Settings."
            );
        }

        private static bool IsPhotonVoicePresentInScene =>
            Utils.GetBlocksInScene()
                .Any(b =>
                    b.BlockId == BlockDataIds.PlayerVoiceChat
                );
    }
}
#endif // FUSION_WEAVER && FUSION2 && PHOTON_VOICE_DEFINED
