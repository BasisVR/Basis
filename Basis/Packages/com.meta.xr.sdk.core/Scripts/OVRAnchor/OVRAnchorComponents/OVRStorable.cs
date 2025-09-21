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
/// Represents the ability to store (save) an <see cref="OVRAnchor"/>.
/// </summary>
/// <remarks>
/// Savable anchors support this component type. In order to save (that is, persist) an anchor
/// (<see cref="OVRAnchor.SaveAsync()"/> or
/// <see cref="OVRAnchor.SaveAsync(System.Collections.Generic.IEnumerable{OVRAnchor})"/>),
/// you must first enable its storable component.
///
/// Note: The <see cref="OVRSpatialAnchor"/> component provides a higher level abstraction for the
/// <see cref="OVRAnchor"/> type and automatically enables the storable component. You do not need to use this component
/// when using an <see cref="OVRSpatialAnchor"/>.
///
/// <example>
/// This example obtains an anchor's storable component and enables it:
/// <code><![CDATA[
/// async void EnableSaving(OVRAnchor anchor) {
///   if (!anchor.TryGetComponent<OVRStorable>(out var storableComponent)) {
///     Debug.LogError("Anchor does not support persistence.");
///     return;
///   }
///
///   if (await storableComponent.SetEnabledAsync(true)) {
///     Debug.Log("Anchor is now savable.");
///   } else {
///     Debug.LogError("Unable to enable the storable component.");
///   }
/// }
/// ]]></code></example>
/// </remarks>
partial struct OVRStorable { }
