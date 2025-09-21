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
/// An enum representing the different formats of hands / skeletons which are supported.
/// </summary>
public enum OVRHandSkeletonVersion
{
    [InspectorName(null)]
    Uninitialized = -1,
    /// <summary>
    /// The hand skeleton that has traditionally been provided by the
    /// Core SDK. Differs from the OpenXR hand skeleton standard in both
    /// joint set and joint orientation.
    /// </summary>
    [InspectorName("OVR Hand Skeleton")]
    OVR = 0,
    /// <summary>
    /// This skeleton type matches the OpenXR hand skeleton specification.
    /// Differs from the <see cref="OVR"/> skeleton in both
    /// joint set and joint orientation.
    /// </summary>
    [InspectorName("OpenXR Hand Skeleton")]
    OpenXR = 1,
}
