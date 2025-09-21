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

/// <summary>
/// Properties for a dynamic object.
/// </summary>
partial struct OVRDynamicObject
{
    /// <summary>
    /// The <see cref="TrackableType"/> this object represents.
    /// </summary>
    /// <remarks>
    /// A dynamic object component is used to represent a "trackable"; that is, something that can be detected in the
    /// physical environment and tracked at runtime.
    /// </remarks>
    public OVRAnchor.TrackableType TrackableType => OVRPlugin.GetSpaceDynamicObjectData(Handle, out var data).IsSuccess()
        ? data.ClassType switch
        {
            OVRPlugin.DynamicObjectClass.Keyboard => OVRAnchor.TrackableType.Keyboard,
            _ => OVRAnchor.TrackableType.None,
        }
        : OVRAnchor.TrackableType.None;
}
