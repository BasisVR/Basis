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
using System.Collections;
using Meta.XR.Util;
using UnityEngine;
using UnityEngine.Android;

/// <summary>
/// Utility for loading a scene model. Derive from this class to customize the scene loading behavior and respond to
/// events.
/// </summary>
/// <remarks>
/// This class and subclasses typically call <see cref="OVRSceneManager.LoadSceneModel"/> as part of its operation.
///
///  "OVRSceneModelLoader and associated classes are deprecated (v65), please use [MR Utility Kit](https://developer.oculus.com/documentation/unity/unity-mr-utility-kit-overview)" instead.
/// </remarks>
[HelpURL("https://developer.oculus.com/documentation/unity/unity-scene-use-scene-anchors/#what-does-ovrscenemanager-do")]
[RequireComponent(typeof(OVRSceneManager))]
[Obsolete(OVRSceneManager.DeprecationMessage)]
[Feature(Feature.Scene)]
public class OVRSceneModelLoader : MonoBehaviour
{
    private const float RetryingReminderDelay = 10;

    /// <summary>
    /// The <see cref="OVRSceneManager"/> component that this loader will use.
    /// </summary>
    protected OVRSceneManager SceneManager { get; private set; }

    private bool _sceneCaptureRequested;

    /// <summary>
    /// Override of the Unity MonoBehaviour Start() method. Start() is called on the frame when a script is enabled, just before
    /// any of the Update() methods are called for the first time.
    /// </summary>
    protected virtual void Start()
    {
        OVRTelemetry.SendEvent(OVRTelemetryConstants.Scene.MarkerId.UseDefaultSceneModelLoader);

        SceneManager = GetComponent<OVRSceneManager>();

        // Bind the events associated with LoadSceneModel()
        SceneManager.SceneModelLoadedSuccessfully += OnSceneModelLoadedSuccessfully;
        SceneManager.NoSceneModelToLoad += OnNoSceneModelToLoad;
        SceneManager.NewSceneModelAvailable += OnNewSceneModelAvailable;
        SceneManager.LoadSceneModelFailedPermissionNotGranted += OnLoadSceneModelFailedPermissionNotGranted;

        // Bind the events associated with RequestSceneCapture()
        SceneManager.SceneCaptureReturnedWithoutError += OnSceneCaptureReturnedWithoutError;
        SceneManager.UnexpectedErrorWithSceneCapture += OnUnexpectedErrorWithSceneCapture;

        OnStart();
    }

    private IEnumerator AttemptToLoadSceneModel()
    {
        var timeSinceReminder = 0f;
        OVRSceneManager.Development.LogWarning(nameof(OVRSceneModelLoader),
            $"{nameof(OVRSceneManager.LoadSceneModel)} failed. Retrying.");
        do
        {
            timeSinceReminder += Time.deltaTime;
            if (timeSinceReminder >= RetryingReminderDelay)
            {
                timeSinceReminder = 0;
                OVRSceneManager.Development.LogWarning(nameof(OVRSceneModelLoader),
                    $"{nameof(OVRSceneManager.LoadSceneModel)} failed. Still retrying.");
            }
            yield return null;
        } while (!SceneManager.LoadSceneModel());

        OVRSceneManager.Development.Log(nameof(OVRSceneModelLoader),
            $"{nameof(OVRSceneManager.LoadSceneModel)} succeeded.");
    }

    /// <summary>
    /// Invoked from this component's `Start` method. The default behavior is to load the scene model using
    /// <see cref="OVRSceneManager.LoadSceneModel"/>.
    /// </summary>
    protected virtual void OnStart()
    {
        LoadSceneModel();
    }

    /// <summary>
    /// An async version of `Android.Permission.RequestUserPermission`
    /// </summary>
    /// <remarks>
    /// This requests permission for Scene using UnityEngine.Android.Permission.RequestUserPermission. However, it turns
    /// the callback-based API into an async API that can be awaited.
    /// </remarks>
    /// <returns>A task that completes when the user grants or denies permission to use Scene.</returns>
    protected static OVRTask<bool> RequestScenePermissionAsync()
    {
#pragma warning disable CS8321 // declared but not used (on non-Android platforms)
        OVRTask<bool> RequestPermissionOnAndroid()
        {
            // Reserve an ID for the task
            var taskId = Guid.NewGuid();

            // Setup permission callbacks
            var callbacks = new PermissionCallbacks();
            callbacks.PermissionGranted += _ => OVRTask.SetResult(taskId, true);
            callbacks.PermissionDenied += _ => OVRTask.SetResult(taskId, false);

            // Create a task and request permission
            var task = OVRTask.Create<bool>(taskId);
            Permission.RequestUserPermission(OVRPermissionsRequester.ScenePermission, callbacks);
            return task;
        }
#pragma warning restore

#if UNITY_ANDROID && !UNITY_EDITOR
        return RequestPermissionOnAndroid();
#else
        return OVRTask.FromResult(true);
#endif
    }

    /// <summary>
    /// Invoked when loading the Scene Model failed because the user has not granted permission to use Scene.
    /// </summary>
    /// <remarks>
    /// See <see cref="OVRSceneManager.LoadSceneModelFailedPermissionNotGranted"/> for details.
    /// </remarks>
    protected virtual async void OnLoadSceneModelFailedPermissionNotGranted()
    {
        SceneManager.Verbose?.Log(nameof(OVRSceneModelLoader),
            $"Requesting permission {OVRPermissionsRequester.ScenePermission}");

        if (await RequestScenePermissionAsync())
        {
            SceneManager.Verbose?.Log(nameof(OVRSceneModelLoader),
                $"Permission {OVRPermissionsRequester.ScenePermission} granted. Attempting to load scene model.");
            LoadSceneModel();
        }
        else
        {
            SceneManager.Verbose?.Log(nameof(OVRSceneModelLoader),
                $"Permission {OVRPermissionsRequester.ScenePermission} denied. Scene model will not be loaded.");
        }
    }

    private void LoadSceneModel()
    {
        SceneManager.Verbose?.Log(nameof(OVRSceneModelLoader),
            $"{nameof(OnStart)}() calling {nameof(OVRSceneManager)}.{nameof(OVRSceneManager.LoadSceneModel)}()");

        if (!SceneManager.LoadSceneModel())
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN || UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX || (UNITY_ANDROID && !UNITY_EDITOR)
            if (!OVRManager.isHmdPresent)
            {
                OVRSceneManager.Development.LogWarning(nameof(OVRSceneModelLoader),
                    $"{nameof(OVRSceneManager.LoadSceneModel)} failed. No link or HMD detected.");
            }
            else
            {
                StartCoroutine(AttemptToLoadSceneModel());
            }
#endif
        }
    }

    /// <summary>
    /// Invoked when the scene model has successfully loaded.
    /// </summary>
    protected virtual void OnSceneModelLoadedSuccessfully()
    {
        // The scene model was captured successfully. At this point all prefabs have been instantiated.
        SceneManager.Verbose?.Log(nameof(OVRSceneModelLoader),
            $"{nameof(OVRSceneManager)}.{nameof(OVRSceneManager.LoadSceneModel)}() completed successfully.");
    }

    /// <summary>
    /// Invoked when there is no scene model available. The default behavior requests scene capture using
    /// <see cref="OVRSceneManager.RequestSceneCapture"/>.
    /// </summary>
    protected virtual void OnNoSceneModelToLoad()
    {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
        UnityEditor.EditorUtility.DisplayDialog("Scene Capture does not work over Link",
            "There is no scene model available, and scene capture cannot be invoked over Link. " +
            "Please capture a scene with the HMD in standalone mode, then access the scene model over Link. " +
            "\n\n" +
            "If a scene model has already been captured, make sure the HMD is connected via Link " +
            "and the spatial data feature has been enabled in Meta Quest Link " +
            "(Settings > Beta > Spatial Data over Meta Quest Link).", "Ok");
#else
        if (_sceneCaptureRequested)
        {
            SceneManager.Verbose?.Log(nameof(OVRSceneModelLoader),
                $"{nameof(OnSceneCaptureReturnedWithoutError)}() There is no scene model, but we have already requested scene capture once. No further action will be taken.");
        }
        else
        {
            // There's no Scene model, we have to ask the user to create one
            SceneManager.Verbose?.Log(nameof(OVRSceneModelLoader),
                $"{nameof(OnNoSceneModelToLoad)}() calling {nameof(OVRSceneManager)}.{nameof(OVRSceneManager.RequestSceneCapture)}()");
            _sceneCaptureRequested = SceneManager.RequestSceneCapture();
        }
#endif
    }

    /// <summary>
    /// Invoked when the scene model has changed. The default behavior loads the scene model using
    /// <see cref="OVRSceneManager.LoadSceneModel"/>.
    /// </summary>
    protected virtual void OnNewSceneModelAvailable()
    {
        // New scene model detected, reloading the scene.
        SceneManager.Verbose?.Log(nameof(OVRSceneModelLoader),
            $"{nameof(OnNewSceneModelAvailable)}() calling {nameof(OVRSceneManager)}.{nameof(OVRSceneManager.LoadSceneModel)}()");
        SceneManager.LoadSceneModel();
    }

    /// <summary>
    /// Invoked when the scene capture succeeds without error.
    /// </summary>
    protected virtual void OnSceneCaptureReturnedWithoutError()
    {
        // The Room Setup successfully returned, we can now load the scene model
        SceneManager.Verbose?.Log(nameof(OVRSceneModelLoader), $"Room setup returned without errors.");
    }

    /// <summary>
    /// Invoked when the scene capture encounters an unexpected error.
    /// </summary>
    protected virtual void OnUnexpectedErrorWithSceneCapture()
    {
        // An unexpected error was returned when invoking the Room Setup. This prevents the user
        // from capturing their room.
        SceneManager.Verbose?.LogError(nameof(OVRSceneModelLoader),
            "Requesting the Room Setup failed. The Scene Model cannot be loaded.");
    }
}
