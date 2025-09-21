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

using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Meta.XR.BuildingBlocks.Editor
{
    [InitializeOnLoad]
    internal static class CameraRigSetupRules
    {
        static CameraRigSetupRules()
        {
            OVRProjectSetup.AddTask(
                level: OVRProjectSetup.TaskLevel.Required,
                group: OVRProjectSetup.TaskGroup.Compatibility,
                isDone: _ => ValidateDuplicatedComponents(FindComponents<Camera>(true)),
                message: "There can only be one active main camera in this scene",
                fix: _ => FixDuplicatedComponents(FindComponents<Camera>(true)),
                fixMessage: $"Disable the main cameras outside of the camera rig"
            );
            OVRProjectSetup.AddTask(
                level: OVRProjectSetup.TaskLevel.Required,
                group: OVRProjectSetup.TaskGroup.Compatibility,
                isDone: _ => ValidateDuplicatedComponents(FindComponents<AudioListener>(false)),
                message: "There can only be one active audio listener in this scene",
                fix: _ => FixDuplicatedComponents(FindComponents<AudioListener>(false)),
                fixMessage: $"Disable the audio listeners outside of the camera rig"
            );
        }

        private static bool BelongsToCameraRigBuildingBlock(GameObject gameObject) =>
            OVRProjectSetupUtils.HasComponentInParents<OVRCameraRig>(gameObject)
            && OVRProjectSetupUtils.HasComponentInParents<BuildingBlock>(gameObject);

        private static bool CameraRigBuildingBlockExists<T>(IEnumerable<T> components) where T : Behaviour => components.Any(component => BelongsToCameraRigBuildingBlock(component.gameObject));

        private static List<T> FindComponents<T>(bool findInMainCameraOnly) where T : Behaviour =>
            OVRProjectSetupUtils.FindComponentsInScene<T>()
                .Where(component => component.enabled && (!findInMainCameraOnly || component.gameObject.CompareTag("MainCamera"))).ToList();

        private static bool ValidateDuplicatedComponents<T>(List<T> components) where T : Behaviour
        {
            if (!CameraRigBuildingBlockExists(components))
            {
                return true;
            }

            return components.Count <= 1;
        }

        private static void FixDuplicatedComponents<T>(List<T> components) where T : Behaviour
        {
            foreach (var component in components)
            {
                component.enabled = BelongsToCameraRigBuildingBlock(component.gameObject);
                EditorSceneManager.MarkSceneDirty(component.gameObject.scene);
            }
        }
    }
}
