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

using UnityEngine;

/// <summary>
/// Represents an infrared camera that tracks the position of a head-mounted display.
/// </summary>
/// <remarks>
/// The <see cref="OVRTracker"/> object is stateless and provides a convenient accessor to the tracker properties.
/// Instead of new'ing an <see cref="OVRTracker"/>, consider using the global <see cref="OVRTracker"/> accessible
/// via <see cref="OVRManager.tracker"/>.
/// </remarks>
[HelpURL("https://developer.oculus.com/reference/unity/latest/class_o_v_r_tracker")]
public class OVRTracker
{
    /// <summary>
    /// The (symmetric) visible area in front of a sensor.
    /// </summary>
    /// <remarks>
    /// You can obtain the frustum of different trackers using <see cref="OVRTracker.GetFrustum"/>.
    /// <example>
    /// This example uses the global <see cref="OVRTracker"/> instance on the <see cref="OVRManager"/>
    /// to query for the sensor's frustum and then logs the sensor's visible area:
    /// <code><![CDATA[
    /// void LogFrustum()
    /// {
    ///     var frustum = OVRManager.tracker.GetFrustum();
    ///     Debug.Log($"Frustum has near plane={frustum.nearZ} and far plane={frustum.farZ}");
    /// }
    /// ]]></code>
    /// </example>
    /// </remarks>
    public struct Frustum
    {
        /// <summary>
        /// The sensor's minimum supported distance to the HMD.
        /// </summary>
        public float nearZ;

        /// <summary>
        /// The sensor's maximum supported distance to the HMD.
        /// </summary>
        public float farZ;

        /// <summary>
        /// The sensor's horizontal and vertical fields of view in degrees.
        /// </summary>
        public Vector2 fov;
    }

    /// <summary>
    /// If true, a sensor is attached to the system.
    /// </summary>
    public bool isPresent
    {
        get
        {
            if (!OVRManager.isHmdPresent)
                return false;

            return OVRPlugin.positionSupported;
        }
    }

    /// <summary>
    /// If true, the sensor is actively tracking the HMD's position. Otherwise the HMD may be temporarily occluded, the system may not support position tracking, etc.
    /// </summary>
    public bool isPositionTracked
    {
        get { return OVRPlugin.positionTracked; }
    }

    /// <summary>
    /// If this is true and a sensor is available, the system will use position tracking when isPositionTracked is also true.
    /// </summary>
    public bool isEnabled
    {
        get
        {
            if (!OVRManager.isHmdPresent)
                return false;

            return OVRPlugin.position;
        }

        set
        {
            if (!OVRManager.isHmdPresent)
                return;

            OVRPlugin.position = value;
        }
    }

    /// <summary>
    /// Returns the number of sensors currently connected to the system.
    /// </summary>
    public int count
    {
        get
        {
            int count = 0;

            for (int i = 0; i < (int)OVRPlugin.Tracker.Count; ++i)
            {
                if (GetPresent(i))
                    count++;
            }

            return count;
        }
    }

    /// <summary>
    /// Gets the sensor's viewing frustum.
    /// </summary>
    /// <param name="tracker">The index of the tracker.</param>
    /// <returns>Returns the <see cref="Frustum"/> associated with <paramref name="tracker"/>.</returns>
    public Frustum GetFrustum(int tracker = 0)
    {
        if (!OVRManager.isHmdPresent)
            return new Frustum();

        return OVRPlugin.GetTrackerFrustum((OVRPlugin.Tracker)tracker).ToFrustum();
    }

    /// <summary>
    /// Gets the sensor's pose, relative to the head's pose at the time of the last pose recentering.
    /// </summary>
    /// <remarks>
    /// If the HMD is not present (<see cref="OVRManager.isHmdPresent"/>), this method returns the identity pose.
    /// </remarks>
    /// <param name="tracker">The index of the tracker.</param>
    /// <returns>The pose of the <paramref name="tracker"/> in tracking space, or identity if the HMD is not present.</returns>
    public OVRPose GetPose(int tracker = 0)
    {
        if (!OVRManager.isHmdPresent)
            return OVRPose.identity;

        OVRPose p;
        switch (tracker)
        {
            case 0:
                p = OVRPlugin.GetNodePose(OVRPlugin.Node.TrackerZero, OVRPlugin.Step.Render).ToOVRPose();
                break;
            case 1:
                p = OVRPlugin.GetNodePose(OVRPlugin.Node.TrackerOne, OVRPlugin.Step.Render).ToOVRPose();
                break;
            case 2:
                p = OVRPlugin.GetNodePose(OVRPlugin.Node.TrackerTwo, OVRPlugin.Step.Render).ToOVRPose();
                break;
            case 3:
                p = OVRPlugin.GetNodePose(OVRPlugin.Node.TrackerThree, OVRPlugin.Step.Render).ToOVRPose();
                break;
            default:
                return OVRPose.identity;
        }

        return new OVRPose()
        {
            position = p.position,
            orientation = p.orientation * Quaternion.Euler(0, 180, 0)
        };
    }

    /// <summary>
    /// Gets whether the pose of the specified sensor is valid and is ready to be queried.
    /// </summary>
    /// <param name="tracker">The index of the sensor.</param>
    /// <returns>Returns `true` if the pose of the specified sensor is valid; otherwise `false`.</returns>
    public bool GetPoseValid(int tracker = 0)
    {
        if (!OVRManager.isHmdPresent)
            return false;

        switch (tracker)
        {
            case 0:
                return OVRPlugin.GetNodePositionTracked(OVRPlugin.Node.TrackerZero);
            case 1:
                return OVRPlugin.GetNodePositionTracked(OVRPlugin.Node.TrackerOne);
            case 2:
                return OVRPlugin.GetNodePositionTracked(OVRPlugin.Node.TrackerTwo);
            case 3:
                return OVRPlugin.GetNodePositionTracked(OVRPlugin.Node.TrackerThree);
            default:
                return false;
        }
    }

    /// <summary>
    /// Gets whether the specified sensor is currently present.
    /// </summary>
    /// <param name="tracker">The index of the sensor.</param>
    /// <returns>Returns `true` if <paramref name="tracker"/> is present; otherwise, `false`.</returns>
    public bool GetPresent(int tracker = 0)
    {
        if (!OVRManager.isHmdPresent)
            return false;

        switch (tracker)
        {
            case 0:
                return OVRPlugin.GetNodePresent(OVRPlugin.Node.TrackerZero);
            case 1:
                return OVRPlugin.GetNodePresent(OVRPlugin.Node.TrackerOne);
            case 2:
                return OVRPlugin.GetNodePresent(OVRPlugin.Node.TrackerTwo);
            case 3:
                return OVRPlugin.GetNodePresent(OVRPlugin.Node.TrackerThree);
            default:
                return false;
        }
    }
}
