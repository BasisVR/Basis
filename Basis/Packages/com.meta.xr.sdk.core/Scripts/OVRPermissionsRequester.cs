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
using UnityEngine;
using UnityEngine.Android;

/// <summary>
/// This class handles Android permission requests for the capabilities listed in <see cref="Permission"/>.
/// </summary>
/// <remarks>
/// It is recommended to use Unity's Android Permission API directly to request permissions which
/// require callbacks. Subscribing to events here may not be guaranteed to be called when
/// using <see cref="OVRManager"/> startup permissions toggle, due to a potential race
/// condition if the request completes before your callback has been registered.
/// </remarks>
public static class OVRPermissionsRequester
{
    /// <summary>
    /// Occurs when a <see cref="Permission"/> is granted.
    /// </summary>
    public static event Action<string> PermissionGranted;

    /// <summary>
    /// Enum listing the capabilities this class can request permission for.
    /// </summary>
    public enum Permission
    {
        /// <summary>
        /// Represents the Face Tracking capability.
        /// </summary>
        FaceTracking,

        /// <summary>
        /// Represents the Body Tracking capability.
        /// </summary>
        BodyTracking,

        /// <summary>
        /// Represents the Eye Tracking capability.
        /// </summary>
        EyeTracking,

        /// <summary>
        /// Represents the Scene capability.
        /// </summary>
        Scene,

        /// <summary>
        /// Represents the Audio Recording permission (required for audio based Face Tracking capability).
        /// </summary>
        RecordAudio,
    }

    /// <summary>
    /// Android permission string for Face Tracking capability.
    /// </summary>
    public const string FaceTrackingPermission = "com.oculus.permission.FACE_TRACKING";

    /// <summary>
    /// Android permission string for Eye Tracking capability.
    /// </summary>
    public const string EyeTrackingPermission = "com.oculus.permission.EYE_TRACKING";

    /// <summary>
    /// Android permission string for Body Tracking capability.
    /// </summary>
    public const string BodyTrackingPermission = "com.oculus.permission.BODY_TRACKING";

    /// <summary>
    /// Android permission string for Scene (Spatial data) capability.
    /// </summary>
    public const string ScenePermission = "com.oculus.permission.USE_SCENE";

    /// <summary>
    /// Android permission string for Audio Recording.
    /// </summary>
    public const string RecordAudioPermission = "android.permission.RECORD_AUDIO";

    /// <summary>
    /// Returns the permission ID of the given <see cref="Permission"/> to be requested from the user.
    /// </summary>
    /// <param name="permission">The <see cref="Permission"/> to get the ID of.</param>
    /// <returns></returns>
    /// <remarks>
    /// See [Permissions on Android](https://developer.android.com/guide/topics/permissions/overview) for general information about
    /// Android Permissions.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when an invalid <see cref="Permission"/> is used.</exception>
    public static string GetPermissionId(Permission permission)
    {
        return permission switch
        {
            Permission.FaceTracking => FaceTrackingPermission,
            Permission.BodyTracking => BodyTrackingPermission,
            Permission.EyeTracking => EyeTrackingPermission,
            Permission.Scene => ScenePermission,
            Permission.RecordAudio => RecordAudioPermission,
            _ => throw new ArgumentOutOfRangeException(nameof(permission), permission, null)
        };
    }

    private static bool IsPermissionSupportedByPlatform(Permission permission)
    {
        return permission switch
        {
            Permission.FaceTracking => OVRPlugin.faceTrackingSupported || OVRPlugin.faceTracking2Supported,
            Permission.BodyTracking => OVRPlugin.bodyTrackingSupported,
            Permission.EyeTracking => OVRPlugin.eyeTrackingSupported,
            // Scene is a no-op on unsupported platforms, but the request can always be made
            Permission.Scene => true,
            Permission.RecordAudio => true,
            _ => throw new ArgumentOutOfRangeException(nameof(permission), permission, null)
        };
    }


    /// <summary>
    /// Returns whether the <see cref="permission"/> has been granted.
    /// </summary>
    /// <remarks>
    /// These permissions are Android-specific, therefore we always return
    /// true if on any other platform.
    ///
    /// See [Permissions on Android](https://developer.android.com/guide/topics/permissions/overview) for general information about
    /// Android Permissions.
    /// </remarks>
    /// <param name="permission"><see cref="Permission"/> to be checked.</param>
    public static bool IsPermissionGranted(Permission permission)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        return UnityEngine.Android.Permission.HasUserAuthorizedPermission(GetPermissionId(permission));
#else
        return true;
#endif
    }

    /// <summary>
    /// Requests the listed <see cref="permissions"/>.
    /// </summary>
    /// <remarks>
    /// See [Permissions on Android](https://developer.android.com/guide/topics/permissions/overview) for general information about
    /// Android Permissions.
    /// </remarks>
    /// <param name="permissions">Set of <see cref="Permission"/> to be requested.</param>
    public static void Request(IEnumerable<Permission> permissions)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        RequestPermissions(permissions);
#endif // UNITY_ANDROID && !UNITY_EDITOR
    }

    private static void RequestPermissions(IEnumerable<Permission> permissions)
    {
        var permissionIdsToRequest = new List<string>();

        foreach (var permission in permissions)
        {
            if (ShouldRequestPermission(permission))
            {
                permissionIdsToRequest.Add(GetPermissionId(permission));
            }
        }

        if (permissionIdsToRequest.Count > 0)
        {
            UnityEngine.Android.Permission.RequestUserPermissions(permissionIdsToRequest.ToArray(),
                BuildPermissionCallbacks());
        }
    }

    private static bool ShouldRequestPermission(Permission permission)
    {
        if (!IsPermissionSupportedByPlatform(permission))
        {
            Debug.LogWarning(
                $"[[{nameof(OVRPermissionsRequester)}] Permission {permission} is not supported by the platform and can't be requested.");
            return false;
        }

        return !IsPermissionGranted(permission);
    }

    private static PermissionCallbacks BuildPermissionCallbacks()
    {
        var permissionCallbacks = new PermissionCallbacks();
        permissionCallbacks.PermissionDenied += permissionId =>
        {
            Debug.LogWarning($"[{nameof(OVRPermissionsRequester)}] Permission {permissionId} was denied.");
        };
        permissionCallbacks.PermissionGranted += permissionId =>
        {
            Debug.Log($"[{nameof(OVRPermissionsRequester)}] Permission {permissionId} was granted.");
            PermissionGranted?.Invoke(permissionId);
        };
        // as per Unity guidelines, PermissionDeniedAndDontAskAgain is unreliable
        // Denied will be fired instead if this isn't subscribed to
        return permissionCallbacks;
    }
}
