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

using System.Linq;
using UnityEditor;
using UnityEngine;
using System.Collections.Generic;
using UnityEditor.SceneManagement;

[InitializeOnLoad]
internal static class OVRProjectSetupOverlayTasks
{
    private const OVRProjectSetup.TaskGroup XRTaskGroup = OVRProjectSetup.TaskGroup.Rendering;

    private static IEnumerable<T> FindTargetComponents<T>() where T : Component
    {
        if (PrefabStageUtility.GetCurrentPrefabStage() is { } stage)
        {
            return stage.scene.GetRootGameObjects().SelectMany(o => o.GetComponentsInChildren<T>(true));
        }
        return Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None);
    }

#if UNITY_TEXTMESHPRO
    private static IEnumerable<TMPro.TextMeshPro> PotentialTextUpgrades =>
        FindTargetComponents<TMPro.TextMeshPro>().Where(c => c.GetComponent<OVROverlayCanvas>() == null);
#endif

    static OVRProjectSetupOverlayTasks()
    {

#if UNITY_TEXTMESHPRO
        OVRProjectSetup.AddTask(
            level: OVRProjectSetup.TaskLevel.Optional,
            group: XRTaskGroup,
            isDone: _ => PotentialTextUpgrades.FirstOrDefault() == null,
            fixAutomatic: false,
            message: $"Displaying text without using OVROverlayCanvas will result in less readable text.",
            fixMessage: $"Select TextMeshPro objects missing OVROverlayCanvas",
            fix: _ => Selection.objects = PotentialTextUpgrades.Select(c => c.gameObject).ToArray()
        );
#endif
    }
}
