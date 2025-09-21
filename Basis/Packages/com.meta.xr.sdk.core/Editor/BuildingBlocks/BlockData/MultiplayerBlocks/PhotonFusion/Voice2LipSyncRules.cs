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

#if META_AVATAR_SDK_DEFINED && PHOTON_VOICE_DEFINED && FUSION_WEAVER && FUSION2
using Meta.XR.BuildingBlocks.Editor;
using UnityEditor;

namespace Meta.XR.MultiplayerBlocks.Fusion.Editor
{
    [InitializeOnLoad]
    internal static class Voice2LipSyncRules
    {
        static Voice2LipSyncRules()
        {
            OVRProjectSetup.AddTask(
                level: OVRProjectSetup.TaskLevel.Required,
                group: OVRProjectSetup.TaskGroup.Features,
                 isDone: _ =>
                 {
                     if (VoiceBlockExists() && AvatarBlockExists())
                     {
                         var avatarBlock = Utils.GetBlocksWithType<AvatarSpawnerFusion>();
                         var lipSyncMicInput = avatarBlock[0].gameObject.GetComponentInChildren<LipSyncMicInput>();
                         if (lipSyncMicInput != null && !lipSyncMicInput.enabled)
                         {
                             return true;
                         }
                         return false;
                     } else if (VoiceBlockExists() != AvatarBlockExists())
                     {
                         return true;
                     }

                     return true;
                 },
                message:
                "When using 'Player Voice Chat' with 'Multiplayer Avatars' you need to disable the LipSyncMicInput script to avoid microphone conflicts.",
                fix: _ =>
                {
                    var avatarBlock = Utils.GetBlocksWithType<AvatarSpawnerFusion>();
                    if (avatarBlock == null && avatarBlock.Count <= 0)
                    {
                        return;
                    }

                    var lipSyncMicInput = avatarBlock[0].gameObject.GetComponentInChildren<LipSyncMicInput>();
                    if (lipSyncMicInput != null)
                    {
                        lipSyncMicInput.enabled = false;
                    }
                },
                fixMessage: $"Disabling LipSyncMicInput script in LipSyncInput prefab to avoid Mic fighting with Photon Voice2"
            );
        }

        private static bool VoiceBlockExists()
        {
            var voice = Utils.GetBlocksWithType<VoiceSetup>();
            return voice?.Count == 1;
        }

        private static bool AvatarBlockExists()
        {
            var avatar = Utils.GetBlocksWithType<AvatarSpawnerFusion>();
            return avatar?.Count == 1;
        }
    }
}
#endif // META_AVATAR_SDK_DEFINED && PHOTON_VOICE_DEFINED && FUSION_WEAVER && FUSION2
