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

using System.Threading.Tasks;
using Meta.XR.Editor.Features;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor;
using UnityEditor.SceneManagement;

[InitializeOnLoad]
public static class OVRSceneOpen
{
    static OVRSceneOpen()
    {
        EditorSceneManager.sceneOpened += OnSceneOpened;
#if UNITY_2022_2_OR_NEWER
        EditorSceneManager.sceneManagerSetupRestored += OnScenesRestored;
#endif
        EditorSceneManager.sceneClosed += OnSceneClosed;
    }

    private static void OnScenesRestored(Scene[] scenes)
    {
        foreach (var scene in scenes)
        {
#pragma warning disable CS4014
            SendEvent(scene, OVRTelemetryConstants.Scene.MarkerId.SceneOpen);
#pragma warning restore CS4014
        }
    }

    private static void OnSceneOpened(Scene scene, OpenSceneMode mode)
    {
#pragma warning disable CS4014
        SendEvent(scene, OVRTelemetryConstants.Scene.MarkerId.SceneOpen);
#pragma warning restore CS4014
    }

    private static void OnSceneClosed(Scene scene)
    {
#pragma warning disable CS4014
        SendEvent(scene, OVRTelemetryConstants.Scene.MarkerId.SceneClose);
#pragma warning restore CS4014
    }

    private static async Task SendEvent(Scene scene, int eventType)
    {
        var guid = AssetDatabase.AssetPathToGUID(scene.path);
        if (string.IsNullOrEmpty(guid)) return;

        var features = await FeatureManager.GetFeaturesInScene(scene);
        OVRTelemetry.Start(eventType)
            .AddAnnotation(OVRTelemetryConstants.Scene.AnnotationType.Guid,
                guid)
            .AddAnnotation(OVRTelemetryConstants.Scene.AnnotationType.BuildTarget,
                EditorUserBuildSettings.selectedBuildTargetGroup.ToString())
            .AddAnnotation(OVRTelemetryConstants.Scene.AnnotationType.RuntimePlatform,
                Application.platform.ToString())
            .AddAnnotation(OVRTelemetryConstants.Scene.AnnotationType.Features,
                            features)
            .AddAnnotation(OVRTelemetryConstants.Scene.AnnotationType.EnabledSettings,
                            FeatureManager.GetFeatureStatusInSettings())
            .Send();
    }
}
