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
using UnityEngine;
using UnityEngine.SceneManagement;

[InitializeOnLoad]
internal static class OVRProjectSetupMovementSDKConfigurationTasks
{
    private const OVRProjectSetup.TaskGroup Group = OVRProjectSetup.TaskGroup.Features;

    static OVRProjectSetupMovementSDKConfigurationTasks()
    {
        AddBodyTrackingTasks();
        AddFaceTrackingTasks();
    }

    private static void AddBodyTrackingTasks()
    {
        OVRProjectSetup.AddTask(
            level: OVRProjectSetup.TaskLevel.Required,
            group: Group,
            isDone: _ => !FindMisconfiguredOVRSkeletonInstances().Any(),
            message: "When using OVRSkeleton components it's required to have OVRBody data provider next to it",
            fix: _ =>
            {
                foreach (var skeleton in FindMisconfiguredOVRSkeletonInstances())
                {
                    OVRSkeletonEditor.FixOVRBodyConfiguration(skeleton, skeleton.GetRequiredBodyJointSet());
                }
            },
            fixMessage: "Create OVRBody components where they are required"
        );


        OVRProjectSetup.AddTask(
            level: OVRProjectSetup.TaskLevel.Required,
            group: Group,
            isDone: _ =>
            {
                var manager = Object.FindFirstObjectByType<OVRManager>();

                return manager == null ||
                       manager.SimultaneousHandsAndControllersEnabled == false ||
                       (manager.wideMotionModeHandPosesEnabled == false &&
                        Object.FindFirstObjectByType<OVRBody>() == null);
            },
            message: "Body API is not compatible with simultaneous hands and controllers",
            fix: _ =>
            {
                var manager = Object.FindFirstObjectByType<OVRManager>();
                if (manager != null && manager.SimultaneousHandsAndControllersEnabled)
                {
                    manager.SimultaneousHandsAndControllersEnabled = false;
                }
            },
            fixMessage: "Turn off simultaneous hands and controllers"
        );
    }

    private static void AddFaceTrackingTasks()
    {
        OVRProjectSetup.AddTask(
            level: OVRProjectSetup.TaskLevel.Required,
            group: Group,
            isDone: _ => !FindMisconfiguredOVRCustomFaceInstances().Any(),
            message:
            "When using OVRCustomFace components it's required to have OVRFaceExpressions data provider next to it",
            fix: _ =>
            {
                foreach (var face in FindMisconfiguredOVRCustomFaceInstances())
                {
                    OVRCustomFaceEditor.FixFaceExpressions(face);
                }
            },
            fixMessage: "Create OVRFaceExpressions components where they are required"
        );
    }

    private static IEnumerable<OVRSkeleton> FindMisconfiguredOVRSkeletonInstances() =>
        FindComponentsInScene<OVRSkeleton>()
            .Where(s => !OVRSkeletonEditor.IsSkeletonProperlyConfigured(s));

    private static IEnumerable<OVRCustomFace> FindMisconfiguredOVRCustomFaceInstances() =>
        FindComponentsInScene<OVRCustomFace>()
            .Where(s => !OVRCustomFaceEditor.IsFaceExpressionsConfigured(s));

    private static IEnumerable<T> FindComponentsInScene<T>() where T : MonoBehaviour
    {
        var results = new List<T>();
        var scene = SceneManager.GetActiveScene();
        var rootGameObjects = scene.GetRootGameObjects();

        foreach (var root in rootGameObjects)
        {
            results.AddRange(root.GetComponentsInChildren<T>());
        }

        return results;
    }
}
