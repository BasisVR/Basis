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
using Meta.XR.Util;
using UnityEngine;

/// <summary>
/// A utility <see cref="MonoBehaviour"/> component to handle Shared Spatial Anchor's operational errors.
/// </summary>
/// <remarks>
/// This component will spawn a visual alert box with error message at runtime.
/// The public methods are meant to be used as listeners for Shared Spatial Anchor's operations.
/// See `Editor/BuildingBlocks/BlockData/SampleSpatialAnchorController` in the Meta XR Core SDK for an example of how to use this class.
/// <seealso cref="Meta.XR.BuildingBlocks"/>
/// </remarks>
[Feature(Feature.Anchors)]
public class SharedSpatialAnchorErrorHandler : MonoBehaviour
{
    /// <summary>
    /// Disables message alert box at runtime.
    /// </summary>
    [Tooltip("Disables the message alerts in headset.")]
    public bool DisableRuntimeGUIAlerts = false;

    /// <summary>
    /// Set your own <see cref="AlertViewHUD"/> prefab.
    /// </summary>
    [SerializeField] private GameObject AlertViewHUDPrefab;

    private string cloudPermissionMsg =
        "Your headset uses on-device point cloud data to determine its position within your room. " +
        "To expand your headset’s capabilities and enable features like local multiplayer, you’ll need to share point cloud data with Meta. You can turn off point cloud sharing anytime in Settings." +
        "\n\nSettings > Privacy > Device Permissions > Turn on \"Share Point Cloud Data\"";

    private void Awake()
    {
        if (AlertViewHUDPrefab) Instantiate(AlertViewHUDPrefab);
    }

    /// <summary>
    /// Handles the <see cref="SpatialAnchorCoreBuildingBlock.OnAnchorCreateCompleted">OnAnchorCreateCompleted</see>
    /// event by logging "Failed to share the spatial anchor.".
    /// </summary>
    /// <remarks>
    /// If there is a cloud storage error, it will log that instead.
    /// </remarks>
    /// <param name="result">Contains the <see cref="OVRSpatialAnchor"/> creation result.</param>
    public void OnAnchorCreate(OVRSpatialAnchor _, OVRSpatialAnchor.OperationResult result)
    {
        if (result == OVRSpatialAnchor.OperationResult.Failure_SpaceCloudStorageDisabled)
        {
            LogWarning(cloudPermissionMsg);
            return;
        }

        if (result != OVRSpatialAnchor.OperationResult.Success)
        {
            LogWarning($"Failed to create the spatial anchor.");
        }
    }

    /// <summary>
    /// Propagates <see cref="OVRSpatialAnchor"/> share failure message.
    /// </summary>
    /// <param name="result">Contains the <see cref="OVRSpatialAnchor"/> share result.</param>
    public void OnAnchorShare(List<OVRSpatialAnchor> _, OVRSpatialAnchor.OperationResult result)
    {
        if (result == OVRSpatialAnchor.OperationResult.Failure_SpaceCloudStorageDisabled)
        {
            LogWarning(cloudPermissionMsg);
            return;
        }

        if (result != OVRSpatialAnchor.OperationResult.Success)
        {
            LogWarning($"Failed to share the spatial anchor.");
        }
    }

    /// <summary>
    /// Handles the <see cref="SharedSpatialAnchorCore.OnSharedSpatialAnchorsLoadCompleted">OnSharedSpatialAnchorsLoadCompleted</see>
    /// event by logging "Failed to load the spatial anchor(s).".
    /// </summary>
    /// <remarks>
    /// If there is a cloud storage error, it will log that instead.
    /// </remarks>
    /// <param name="result">Contains the <see cref="OVRSpatialAnchor"/> load result.</param>
    public void OnSharedSpatialAnchorLoad(List<OVRSpatialAnchor> loadedAnchors, OVRSpatialAnchor.OperationResult result)
    {
        if (result == OVRSpatialAnchor.OperationResult.Failure_SpaceCloudStorageDisabled)
        {
            LogWarning(cloudPermissionMsg);
            return;
        }
        if (loadedAnchors == null || loadedAnchors.Count == 0) LogWarning($"Failed to load the spatial anchor(s).");
    }

    /// <summary>
    /// Handles the <see cref="SpatialAnchorCoreBuildingBlock.OnAnchorsEraseAllCompleted">OnAnchorsEraseAllCompleted</see>
    /// event by logging "Failed to erase the spatial anchor(s).".
    /// </summary>
    public void OnAnchorEraseAll(OVRSpatialAnchor.OperationResult result)
    {
        if (result == OVRSpatialAnchor.OperationResult.Failure)
            LogWarning($"Failed to erase the spatial anchor(s).");
    }

    /// <summary>
    /// Handles the <see cref="SpatialAnchorCoreBuildingBlock.OnAnchorEraseCompleted">OnAnchorEraseCompleted</see>
    /// event by logging "Failed to erase the spatial anchor with uuid: " and the uuid of the anchor.
    /// </summary>
    public void OnAnchorErase(OVRSpatialAnchor anchor, OVRSpatialAnchor.OperationResult result)
    {
        if (result == OVRSpatialAnchor.OperationResult.Failure)
            LogWarning($"Failed to erase the spatial anchor with uuid: {anchor}");
    }

    private void LogWarning(string msg)
    {
        if (!DisableRuntimeGUIAlerts)
        {
            AlertViewHUD.PostMessage(msg, AlertViewHUD.MessageType.Error);
        }

        Debug.LogWarning($"[{nameof(SharedSpatialAnchorErrorHandler)}] {msg}");
    }
}
