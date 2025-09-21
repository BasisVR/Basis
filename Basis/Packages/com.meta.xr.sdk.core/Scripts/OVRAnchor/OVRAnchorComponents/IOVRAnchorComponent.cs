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
/// Interface shared by all <see cref="OVRAnchor"/> components.
/// </summary>
/// <remarks>
/// For more information about the anchor-component model, see
/// [Spatial Anchor Overview](https://developer.oculus.com/documentation/unity/unity-spatial-anchors-persist-content/#ovrspatialanchor-component).
/// </remarks>
/// <typeparam name="T">The actual implementation Type of the interface.</typeparam>
/// <seealso cref="OVRAnchor.FetchAnchorsAsync(System.Collections.Generic.List{OVRAnchor},OVRAnchor.FetchOptions,System.Action{System.Collections.Generic.List{OVRAnchor},int})"/>
/// <seealso cref="OVRAnchor.TryGetComponent{T}"/>
/// <seealso cref="OVRAnchor.SupportsComponent{T}"/>
public interface IOVRAnchorComponent<T>
{
    /// <summary>
    /// Whether this object represents a valid anchor component.
    /// </summary>
    public bool IsNull { get; }

    /// <summary>
    /// True if this component is enabled and no change to its enabled status is pending.
    /// </summary>
    public bool IsEnabled { get; }

    /// <summary>
    /// Sets the enabled status of this component.
    /// </summary>
    /// <remarks>
    /// A component must be enabled to access its data.
    /// </remarks>
    /// <param name="enable">The desired state of the component.</param>
    /// <param name="timeout">The timeout, in seconds, for the operation. Use zero to indicate an infinite timeout.</param>
    /// <returns>Returns an <see cref="OVRTask"/>&lt;bool&gt; whose result indicates the result of the operation.</returns>
    public OVRTask<bool> SetEnabledAsync(bool enable, double timeout = 0);

    internal OVRPlugin.SpaceComponentType Type { get; }

    internal ulong Handle { get; }

    internal T FromAnchor(OVRAnchor anchor);
}
