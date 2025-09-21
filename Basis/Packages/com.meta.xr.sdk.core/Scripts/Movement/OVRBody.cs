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
using Meta.XR.Util;
using UnityEngine;

/// <summary>
/// This class implements <see cref="OVRSkeleton.IOVRSkeletonDataProvider"/> in order
/// to provide body tracking data per frame, and is responsible for stopping and starting
/// body tracking. Furthermore, it defines the skeleton type and implements
/// <see cref="OVRSkeletonRenderer.IOVRSkeletonRendererDataProvider"/> in case the
/// body tracking state needs to be provided to renderer-based classes. Use this class
/// to read body tracking pose data, accessible via <see cref="OVRSkeleton.IOVRSkeletonDataProvider.GetSkeletonPoseData"/>,
/// to drive the bone transforms of a character, or to use for pose estimation purposes.
/// For more information, see [Body Tracking for Movement SDK for Unity](https://developer.oculus.com/documentation/unity/move-body-tracking/).
/// </summary>
/// <remarks>
/// Typically, you would use this in conjunction with an <see cref="OVRSkeleton"/> in order
/// to transform bones and/or an <see cref="OVRSkeletonRenderer"/> to render them.
/// </remarks>
[HelpURL("https://developer.oculus.com/documentation/unity/move-body-tracking/")]
[Feature(Feature.BodyTracking)]
public class OVRBody : MonoBehaviour,
    OVRSkeleton.IOVRSkeletonDataProvider,
    OVRSkeletonRenderer.IOVRSkeletonRendererDataProvider
{
    /// <summary>
    /// This field is private. Do not use it in your app logic.
    /// </summary>
    private OVRPlugin.BodyState _bodyState;

    /// <summary>
    /// This field is private. Do not use it in your app logic.
    /// </summary>
    private OVRPlugin.Quatf[] _boneRotations;

    /// <summary>
    /// This field is private. Do not use it in your app logic.
    /// </summary>
    private OVRPlugin.Vector3f[] _boneTranslations;

    /// <summary>
    /// This field is private. Do not use it in your app logic.
    /// </summary>
    private bool _dataChangedSinceLastQuery;

    /// <summary>
    /// This field is private. Do not use it in your app logic.
    /// </summary>
    private bool _hasData;

    /// <summary>
    /// This field is private. Do not use it in your app logic.
    /// </summary>
    private const OVRPermissionsRequester.Permission BodyTrackingPermission =
        OVRPermissionsRequester.Permission.BodyTracking;

    private Action<string> _onPermissionGranted;

    [SerializeField]
    [Tooltip("The skeleton data type to be provided. Should be sync with OVRSkeleton. For selecting the tracking mode on the device, check settings in OVRManager.")]
    private OVRPlugin.BodyJointSet _providedSkeletonType = OVRPlugin.BodyJointSet.UpperBody;

    /// <summary>
    /// The skeleton type joint set that is used when updating the body state.
    /// Change this value based on the joint set that is compatible with your
    /// character.
    /// </summary>
    public OVRPlugin.BodyJointSet ProvidedSkeletonType
    {
        get => _providedSkeletonType;
        set => _providedSkeletonType = value;
    }

    /// <summary>
    /// This field is private. Do not use it in your app logic.
    /// </summary>
    private static int _trackingInstanceCount;

    /// <summary>
    /// The raw <see cref="BodyState"/> data used to populate the <see cref="OVRSkeleton"/>.
    /// Query this value for metadata associated with <see cref="BodyState"/>, such as joint locations
    /// or confidence values.
    /// </summary>
    public OVRPlugin.BodyState? BodyState => _hasData ? _bodyState : default(OVRPlugin.BodyState?);

    private void Awake()
    {
        _onPermissionGranted = OnPermissionGranted;
    }

    private void OnEnable()
    {
        _dataChangedSinceLastQuery = false;
        _hasData = false;
        var manager = FindAnyObjectByType<OVRManager>();
        if (manager != null && manager.SimultaneousHandsAndControllersEnabled)
        {
            Debug.LogWarning("Currently, Body API and simultaneous hands and controllers cannot be enabled at the same time", this);
            enabled = false;
            return;
        }

        if (_providedSkeletonType == OVRPlugin.BodyJointSet.FullBody &&
            OVRRuntimeSettings.GetRuntimeSettings().BodyTrackingJointSet == OVRPlugin.BodyJointSet.UpperBody)
        {
            Debug.LogWarning(
                $"[{nameof(OVRBody)}] Full body skeleton is used, but Full body tracking is disabled. Check settings in OVRManager.");
        }

        _trackingInstanceCount++;
        if (!StartBodyTracking())
        {
            enabled = false;
            return;
        }

        if (OVRPlugin.nativeXrApi == OVRPlugin.XrApi.OpenXR)
        {
            GetBodyState(OVRPlugin.Step.Render);
        }
        else
        {
            enabled = false;
            Debug.LogWarning($"[{nameof(OVRBody)}] Body tracking is only supported by OpenXR and is unavailable.");
        }
    }

    private void OnPermissionGranted(string permissionId)
    {
        if (permissionId == OVRPermissionsRequester.GetPermissionId(BodyTrackingPermission))
        {
            OVRPermissionsRequester.PermissionGranted -= _onPermissionGranted;
            enabled = true;
        }
    }

    private static bool StartBodyTracking()
    {
        OVRPlugin.BodyJointSet jointSet = OVRRuntimeSettings.GetRuntimeSettings().BodyTrackingJointSet;
        if (!OVRPlugin.StartBodyTracking2(jointSet))
        {
            Debug.LogWarning(
                $"[{nameof(OVRBody)}] Failed to start body tracking with joint set {jointSet}.");
            return false;
        }

        OVRPlugin.BodyTrackingFidelity2 fidelity = OVRRuntimeSettings.GetRuntimeSettings().BodyTrackingFidelity;
        bool fidelityChangeSuccessful = OVRPlugin.RequestBodyTrackingFidelity(fidelity);
        if (!fidelityChangeSuccessful)
        {
            // Fidelity suggestion failed but body tracking might still work.
            Debug.LogWarning($"[{nameof(OVRBody)}] Failed to set Body Tracking fidelity to: {fidelity}");
        }

        return true;
    }

    private void OnDisable()
    {

        if (--_trackingInstanceCount == 0)
        {
            OVRPlugin.StopBodyTracking();
        }
    }

    private void OnDestroy()
    {
        OVRPermissionsRequester.PermissionGranted -= _onPermissionGranted;
    }

    private void Update() => GetBodyState(OVRPlugin.Step.Render);

    /// <summary>
    /// Attempts to set the requested joint set. If you call this function and
    /// request a joint set that is not supported, you will get a false value
    /// returned as a result.
    /// </summary>
    /// <param name="jointSet">The requested joint set.</param>
    /// <returns>`true` if joint set requested was set; `false` if not.</returns>
    public static bool SetRequestedJointSet(OVRPlugin.BodyJointSet jointSet)
    {
        var activeJointSet = OVRRuntimeSettings.GetRuntimeSettings().BodyTrackingJointSet;
        if (jointSet != activeJointSet)
        {
            OVRRuntimeSettings.GetRuntimeSettings().BodyTrackingJointSet = jointSet;
            if (_trackingInstanceCount > 0)
            {
                OVRPlugin.StopBodyTracking();
                return StartBodyTracking();
            }
        }

        return true;
    }


    /// <summary>
    /// Runs body tracking calibration with the height specified in meters. Use this function
    /// to enforce the height of the user.
    /// </summary>
    /// <param name="height">The height in meters.</param>
    /// <returns>`true` if the height calibration worked, `false` if not.</returns>
    public static bool SuggestBodyTrackingCalibrationOverride(float height) =>
        OVRPlugin.SuggestBodyTrackingCalibrationOverride(new OVRPlugin.BodyTrackingCalibrationInfo { BodyHeight = height });
    /// <summary>
    /// Resets body tracking calibration.
    /// </summary>
    /// <returns>`true` if body tracking calibration was successful; `false` if not.</returns>
    public static bool ResetBodyTrackingCalibration() => OVRPlugin.ResetBodyTrackingCalibration();

    /// <summary>
    /// Returns the current body tracking calibration status.
    /// </summary>
    /// <returns>Body calibration status in the form of <see cref="OVRPlugin.BodyTrackingCalibrationState"/>.</returns>
    public OVRPlugin.BodyTrackingCalibrationState GetBodyTrackingCalibrationStatus()
    {
        if (!_hasData)
            return OVRPlugin.BodyTrackingCalibrationState.Invalid;

        return _bodyState.CalibrationStatus;
    }

    /// <summary>
    /// This function returns the current body tracking fidelity. Use this function to understand
    /// the visual fidelity of body tracking of your app during runtime.
    /// </summary>
    /// <returns>Body tracking fidelity as <see cref="OVRPlugin.BodyTrackingFidelity2"/>.</returns>
    public OVRPlugin.BodyTrackingFidelity2 GetBodyTrackingFidelityStatus()
    {
        return _bodyState.Fidelity;
    }

    private void GetBodyState(OVRPlugin.Step step)
    {
        if (OVRPlugin.GetBodyState4(step, _providedSkeletonType, ref _bodyState))
        {
            _hasData = true;
            _dataChangedSinceLastQuery = true;
        }
        else
        {
            _hasData = false;
        }

    }

    OVRSkeleton.SkeletonType OVRSkeleton.IOVRSkeletonDataProvider.GetSkeletonType()
    {
        return _providedSkeletonType switch
        {
            OVRPlugin.BodyJointSet.UpperBody => OVRSkeleton.SkeletonType.Body,
            OVRPlugin.BodyJointSet.FullBody => OVRSkeleton.SkeletonType.FullBody,
            _ => OVRSkeleton.SkeletonType.None,
        };
    }

    OVRSkeleton.SkeletonPoseData OVRSkeleton.IOVRSkeletonDataProvider.GetSkeletonPoseData()
    {
        if (!_hasData)
            return default;

        if (_dataChangedSinceLastQuery)
        {
            // Make sure arrays have been allocated
            Array.Resize(ref _boneRotations, _bodyState.JointLocations.Length);
            Array.Resize(ref _boneTranslations, _bodyState.JointLocations.Length);

            // Copy joint poses into bone arrays
            for (var i = 0; i < _bodyState.JointLocations.Length; i++)
            {
                var jointLocation = _bodyState.JointLocations[i];
                if (jointLocation.OrientationValid)
                {
                    _boneRotations[i] = jointLocation.Pose.Orientation;
                }

                if (jointLocation.PositionValid)
                {
                    _boneTranslations[i] = jointLocation.Pose.Position;
                }
            }

            _dataChangedSinceLastQuery = false;
        }

        return new OVRSkeleton.SkeletonPoseData
        {
            IsDataValid = true,
            IsDataHighConfidence = _bodyState.Confidence > .5f,
            RootPose = _bodyState.JointLocations[(int)OVRPlugin.BoneId.Body_Root].Pose,
            RootScale = 1.0f,
            BoneRotations = _boneRotations,
            BoneTranslations = _boneTranslations,
            SkeletonChangedCount = (int)_bodyState.SkeletonChangedCount,
        };
    }

    OVRSkeletonRenderer.SkeletonRendererData
        OVRSkeletonRenderer.IOVRSkeletonRendererDataProvider.GetSkeletonRendererData() => _hasData
        ? new OVRSkeletonRenderer.SkeletonRendererData
        {
            RootScale = 1.0f,
            IsDataValid = true,
            IsDataHighConfidence = true,
            ShouldUseSystemGestureMaterial = false,
        }
        : default;

    /// <summary>
    /// This field manages body tracking fidelity, which is associated with
    /// body tracking quality that is currently being used in an app. Fidelity
    /// is determined by the app's runtime settings as well as the capabilities of
    /// the device that is runs on.
    /// </summary>
    public static OVRPlugin.BodyTrackingFidelity2 Fidelity
    {
        get => OVRRuntimeSettings.GetRuntimeSettings().BodyTrackingFidelity;
        set
        {
            OVRRuntimeSettings.GetRuntimeSettings().BodyTrackingFidelity = value;
            OVRPlugin.RequestBodyTrackingFidelity(value);
        }
    }
}
