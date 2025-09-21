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
using UnityEngine;

/// <summary>
/// Represents an XrSpace in the Oculus Runtime.
/// </summary>
/// <remarks>
/// A "space" is an instance of an [XrSpace](https://registry.khronos.org/OpenXR/specs/1.1/html/xrspec.html#XrSpace) in
/// the OpenXR runtime and is used typically as the low-level handle of an anchor.
///
/// \deprecated Most APIs that use <see cref="OVRSpace"/> are deprecated. For low-level access to anchors, use
/// <see cref="OVRAnchor"/> instead.
///
/// Read more about anchors generally at
/// [Spatial Anchor Overview](https://developer.oculus.com/documentation/unity/unity-spatial-anchors-persist-content/#ovrspatialanchor-component)
/// and
/// [Scene Overview](https://developer.oculus.com/documentation/unity/unity-scene-overview/).
/// </remarks>
public readonly struct OVRSpace : IEquatable<OVRSpace>
{
    /// <summary>
    /// \deprecated Represents a storage location for an anchor.
    /// </summary>
    /// <remarks>
    /// \deprecated APIs that accept a storage location are obsolete.
    ///
    /// When you save (<see cref="OVRSpatialAnchor.SaveAsync(OVRSpatialAnchor.SaveOptions)"/>,
    /// erase (<see cref="OVRSpatialAnchor.EraseAsync(OVRSpatialAnchor.EraseOptions)"/>,
    /// or load (<see cref="OVRSpatialAnchor.LoadUnboundAnchorsAsync(OVRSpatialAnchor.LoadOptions)"/> a spatial
    /// anchor using these obsolete APIs, you may specify a storage location to indicate from where you would like to
    /// save, erase, or load the anchor(s), respectively.
    /// </remarks>
    [Obsolete("Anchor APIs no longer require a storage location.")]
    public enum StorageLocation
    {
        /// <summary>
        /// The storage location is local to the device.
        /// </summary>
        Local,

        /// <summary>
        /// The storage location is in the cloud.
        /// </summary>
        Cloud,
    }

    /// <summary>
    /// A runtime handle associated with this <see cref="OVRSpace"/>.
    /// </summary>
    /// <remarks>
    /// The handle can change between subsequent sessions. To refer to a persistent anchor, use <see cref="TryGetUuid"/>
    /// to get a unique identifier for the anchor.
    /// </remarks>
    public ulong Handle { get; }

    /// <summary>
    /// Retrieve the universally unique identifier (UUID) associated with this <see cref="OVRSpace"/>.
    /// </summary>
    /// <remarks>
    /// Every space that can be persisted will have a UUID associated with it. UUIDs are consistent across different
    /// sessions and apps.
    ///
    /// The UUID of a space does not change over time, but not all spaces are guaranteed to have a UUID.
    /// </remarks>
    /// <param name="uuid">If successful, the uuid associated with this <see cref="OVRSpace"/>, otherwise, `Guid.Empty`.
    /// </param>
    /// <returns>Returns `true` if the uuid could be retrieved, otherwise `false`.</returns>
    public bool TryGetUuid(out Guid uuid) => OVRPlugin.GetSpaceUuid(Handle, out uuid);

    /// <summary>
    /// Indicates whether this <see cref="OVRSpace"/> represents a valid space (vs a default constructed
    /// <see cref="OVRSpace"/>).
    /// </summary>
    public bool Valid => Handle != 0;

    /// <summary>
    /// Constructs an <see cref="OVRSpace"/> object from an existing runtime handle and UUID.
    /// </summary>
    /// <remarks>
    /// This constructor does not create a new space. An <see cref="OVRSpace"/> is a wrapper for low-level functionality
    /// in the Oculus Runtime. To create a new spatial anchor, use <see cref="OVRSpatialAnchor"/>.
    /// </remarks>
    /// <param name="handle">The runtime handle associated with the space.</param>
    public OVRSpace(ulong handle) => Handle = handle;

    /// <summary>
    /// Generates a string representation of this <see cref="OVRSpace"/> of the form "0xYYYYYYYY" where "Y" are the
    /// hexadecimal digits of the <see cref="Handle"/>.
    /// </summary>
    /// <returns>Returns a string representation of this <see cref="OVRSpace"/>.</returns>
    public override string ToString() => $"0x{Handle:x16}";

    /// <summary>
    /// Compares for equality with another space.
    /// </summary>
    /// <param name="other">The <see cref="OVRSpace"/> to compare for equality with this space.</param>
    /// <returns>Returns `true` if the two spaces represent the same space instance, otherwise `false`.</returns>
    public bool Equals(OVRSpace other) => Handle == other.Handle;

    /// <summary>
    /// Compares for equality with an `object`.
    /// </summary>
    /// <param name="obj">The `object` to compare for equality with this space.</param>
    /// <returns>Returns `true` if <paramref name="obj"/> is an <see cref="OVRSpace"/> and represents the same
    /// space instance as this one, otherwise `false`.</returns>
    public override bool Equals(object obj) => obj is OVRSpace other && Equals(other);

    /// <summary>
    /// Generates a hash code suitable for use in a `Dictionary` or `HashSet`
    /// </summary>
    /// <returns>Returns a hash code suitable for use in a `Dictionary` or `HashSet`</returns>
    public override int GetHashCode() => Handle.GetHashCode();

    /// <summary>
    /// Compares two spaces for equality.
    /// </summary>
    /// <remarks>
    /// This is the same equality test as <see cref="Equals(OVRSpace)"/>.
    /// </remarks>
    /// <param name="lhs">The space to compare with <paramref name="rhs"/>.</param>
    /// <param name="rhs">The space to compare with <paramref name="lhs"/>.</param>
    /// <returns>Returns `true` if <paramref name="lhs"/> is equal to <paramref name="rhs"/>, otherwise `false`.</returns>
    public static bool operator ==(OVRSpace lhs, OVRSpace rhs) => lhs.Handle == rhs.Handle;

    /// <summary>
    /// Compares two spaces for inequality.
    /// </summary>
    /// <remarks>
    /// This is the logical negation of <see cref="Equals(OVRSpace)"/>.
    /// </remarks>
    /// <param name="lhs">The space to compare with <paramref name="rhs"/>.</param>
    /// <param name="rhs">The space to compare with <paramref name="lhs"/>.</param>
    /// <returns>Returns `true` if <paramref name="lhs"/> is not equal to <paramref name="rhs"/>, otherwise `false`.</returns>
    public static bool operator !=(OVRSpace lhs, OVRSpace rhs) => lhs.Handle != rhs.Handle;

    /// <summary>
    /// Casts a `ulong` handle to an <see cref="OVRSpace"/>.
    /// </summary>
    /// <param name="handle">The handle of the `XrSpace` to convert.</param>
    /// <returns>Returns the <paramref name="handle"/> as an <see cref="OVRSpace"/>.</returns>
    public static implicit operator OVRSpace(ulong handle) => new OVRSpace(handle);

    /// <summary>
    /// Casts an <see cref="OVRSpace"/> to its underlying `XrSpace` handle.
    /// </summary>
    /// <param name="space">The <see cref="OVRSpace"/> to cast.</param>
    /// <returns>Returns the `XrSpace` handle of the <see cref="OVRSpace"/>.</returns>
    public static implicit operator ulong(OVRSpace space) => space.Handle;
}

public static partial class OVRExtensions
{
    [Obsolete("Anchor APIs that specify a storage location are obsolete.")]
    internal static OVRPlugin.SpaceStorageLocation ToSpaceStorageLocation(this OVRSpace.StorageLocation storageLocation)
    {
        switch (storageLocation)
        {
            case OVRSpace.StorageLocation.Local: return OVRPlugin.SpaceStorageLocation.Local;
            case OVRSpace.StorageLocation.Cloud: return OVRPlugin.SpaceStorageLocation.Cloud;
            default:
                throw new NotSupportedException(
                    $"{storageLocation} is not a supported {nameof(OVRPlugin.SpaceStorageLocation)}");
        }
    }
}
