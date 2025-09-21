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

#if UNITY_NGO_MODULE_DEFINED
using System.Linq;
using UnityEngine;
using UnityEditor;
using Meta.XR.BuildingBlocks.Editor;

namespace Meta.XR.MultiplayerBlocks.NGO.Editor
{
    [InitializeOnLoad]
    public static class AutoMatchmakingNGOSetupRules
    {
        static AutoMatchmakingNGOSetupRules()
        {
            OVRProjectSetup.AddTask(
                level: OVRProjectSetup.TaskLevel.Required,
                group: OVRProjectSetup.TaskGroup.Features,
                isDone: _ =>
                {
                    if (!IsAutoMatchmakingNGOPresentInScene)
                    {
                        return true;
                    }

                    return !string.IsNullOrEmpty(Application.cloudProjectId);
                },
                message:
                "When using AutoMatchmaking with Unity Game Services you must link your Unity project to a Project ID in Edit > Project Settings > Services"
            );
        }

        private static bool IsAutoMatchmakingNGOPresentInScene =>
            Utils.GetBlocksInScene()
                .Any(b =>
                    b.BlockId == BlockDataIds.IAutoMatchmaking
                    && b.InstallationRoutineCheckpoint.InstallationRoutineId == BlockDataIds.AutoMatchmakingNGOInstallationRoutine
                );
    }
}
#endif // UNITY_NGO_MODULE_DEFINED
