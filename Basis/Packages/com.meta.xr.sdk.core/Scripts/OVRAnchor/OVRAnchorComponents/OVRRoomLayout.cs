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

/// <summary>
/// Represents a room described by its floor, ceiling and walls.
/// </summary>
/// <remarks>
/// An <see cref="OVRAnchor"/> supports this component when it is a room anchor. Access this component by calling
/// <see cref="OVRAnchor.GetComponent{T}"/> on a room anchor.
///
/// The room layout component is part of the Meta Quest Scene Model. Read more at
/// [Control flow for rooms and child anchors](https://developer.oculus.com/documentation/unity/unity-scene-ovranchor/#control-flow-for-rooms-and-child-anchors).
/// </remarks>
/// <seealso cref="FetchLayoutAnchorsAsync"/>
/// <seealso cref="TryGetRoomLayout"/>
public readonly partial struct OVRRoomLayout : IOVRAnchorComponent<OVRRoomLayout>, IEquatable<OVRRoomLayout>
{
    /// <summary>
    /// (Obsolete) Asynchronous method that fetches anchors contained in the Room Layout.
    /// </summary>
    /// <param name="anchors">List that will get cleared and populated with the requested anchors.</param>
    /// <remarks>
    /// \deprecated This method is obsolete. Use <see cref="FetchAnchorsAsync"/> instead.
    ///
    /// Dispose of the returned task if you don't use the results</remarks>
    /// <returns>Returns a task that will eventually let you test if the fetch was successful or not.
    /// If the result is true, then the <see cref="anchors"/> parameter has been populated with the requested anchors.</returns>
    /// <exception cref="InvalidOperationException">If it fails to retrieve the Room Layout</exception>
    /// <exception cref="ArgumentNullException">If parameter anchors is null</exception>
    [Obsolete("Use FetchAnchorsAsync instead.")]
    public OVRTask<bool> FetchLayoutAnchorsAsync(List<OVRAnchor> anchors)
    {
        if (!OVRPlugin.GetSpaceRoomLayout(Handle, out var roomLayout))
        {
            throw new InvalidOperationException("Could not get Room Layout");
        }

        using (new OVRObjectPool.ListScope<Guid>(out var list))
        {
            list.Add(roomLayout.floorUuid);
            list.Add(roomLayout.ceilingUuid);
            list.AddRange(roomLayout.wallUuids);
            return OVRAnchor.FetchAnchorsAsync(list, anchors);
        }
    }

    /// <summary>
    /// Fetches the anchors contained in the Room Layout.
    /// </summary>
    /// <param name="anchors">List that will get cleared and populated with the requested anchors.</param>
    /// <remarks>
    /// Dispose of the returned task if you don't use the results.
    /// </remarks>
    /// <returns>A task that will eventually let you test if the fetch was successful or not.
    /// If the result is true, then the <see cref="anchors"/> parameter has been populated with the requested anchors.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the method fails to retrieve the Room Layout. This is usually
    /// because the anchor does not have a RoomLayout component.</exception>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="anchors"/> is `null`.</exception>
    public OVRTask<OVRResult<List<OVRAnchor>, OVRAnchor.FetchResult>> FetchAnchorsAsync(List<OVRAnchor> anchors)
    {
        if (anchors == null)
            throw new ArgumentNullException(nameof(anchors));

        if (!OVRPlugin.GetSpaceRoomLayout(Handle, out var roomLayout))
            throw new InvalidOperationException("Could not get Room Layout");

        using (new OVRObjectPool.ListScope<Guid>(out var list))
        {
            list.Add(roomLayout.floorUuid);
            list.Add(roomLayout.ceilingUuid);
            list.AddRange(roomLayout.wallUuids);
            return OVRAnchor.FetchAnchorsAsync(anchors, new OVRAnchor.FetchOptions
            {
                Uuids = list
            });
        }
    }

    /// <summary>
    /// Tries to get the UUIDs of the Ceiling, Floor and Walls in the room layout.
    /// </summary>
    /// <remarks>
    /// The UUIDs can be passed to
    /// <see cref="OVRAnchor.FetchAnchorsAsync(List{OVRAnchor},OVRAnchor.FetchOptions,Action{List{OVRAnchor}, int})"/>.
    ///
    /// You can also use <see cref="FetchAnchorsAsync"/> to combine this method with the fetch operation.
    /// </remarks>
    /// <param name="ceiling">Out <see cref="Guid"/> representing the ceiling of the room.</param>
    /// <param name="floor">Out <see cref="Guid"/> representing the floor of the room.</param>
    /// <param name="walls">Out array of <see cref="Guid"/>s representing the walls of the room.</param>
    /// <returns>Returns `true` if the request succeeds, otherwise `false`.</returns>
    public bool TryGetRoomLayout(out Guid ceiling, out Guid floor, out Guid[] walls)
    {
        ceiling = Guid.Empty;
        floor = Guid.Empty;
        walls = null;
        if (!OVRPlugin.GetSpaceRoomLayout(Handle, out var roomLayout))
        {
            return false;
        }

        ceiling = roomLayout.ceilingUuid;
        floor = roomLayout.floorUuid;
        walls = roomLayout.wallUuids;
        return true;
    }
}
