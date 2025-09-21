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
/// Represents a user for purposes of sharing scene anchors.
/// </summary>
/// <remarks>
/// In order to share an anchor, you need to specify the user or users with whom you'd like to share those anchor(s).
///
/// A "space user" represents an Oculus user and is used by the following methods:
/// - <see cref="OVRSpatialAnchor.ShareAsync(OVRSpaceUser)"/>
/// - <see cref="OVRSpatialAnchor.ShareAsync(OVRSpaceUser,OVRSpaceUser)"/>
/// - <see cref="OVRSpatialAnchor.ShareAsync(OVRSpaceUser,OVRSpaceUser,OVRSpaceUser)"/>
/// - <see cref="OVRSpatialAnchor.ShareAsync(OVRSpaceUser,OVRSpaceUser,OVRSpaceUser,OVRSpaceUser)"/>
/// - <see cref="OVRSpatialAnchor.ShareAsync(IEnumerable{OVRSpaceUser})"/>
/// - <see cref="OVRSpatialAnchor.ShareAsync(IEnumerable{OVRSpatialAnchor},IEnumerable{OVRSpaceUser})"/>
///
/// An <see cref="OVRSpaceUser"/> is a lightweight struct that wraps a native handle to a space user. You can create
/// an <see cref="OVRSpaceUser"/> from the user id of an Oculus user obtained from the
/// [Platform SDK](https://developer.oculus.com/documentation/unity/ps-platform-intro/).
/// </remarks>
public struct OVRSpaceUser : System.IDisposable
{
    /// <summary>
    /// Tries to create a handle to a specific Oculus user in the current conceptual space.
    /// </summary>
    /// <param name="platformUserId">
    /// A user's unique ID provided by a support platform, i.e. the org-unique Oculus user ID.
    /// You must request these IDs e.g. from the
    /// <a href="https://developer.oculus.com/documentation/unity/ps-platform-intro/">Platform SDK</a>. <br/>
    /// See also: <a href="https://developer.oculus.com/reference/platform-unity/latest/class_oculus_platform_users">Oculus.Platform.Users module</a>.
    /// </param>
    /// <param name="spaceUser">
    /// The result of the space user creation request; will only be <see cref="Valid"/> iff this method returned true.
    /// </param>
    /// <returns>
    /// <c>true</c> iff <paramref name="spaceUser"/> is a <see cref="Valid"/> handle to a user in the current space.
    /// </returns>
    /// <remarks>
    /// <paramref name="platformUserId"/> should not represent the numerical value <c>0</c>.
    /// This is a common value to inadvertently obtain, usually in cases involving insufficient app, user, or device permissions.
    /// </remarks>
    public static bool TryCreate(ulong platformUserId, out OVRSpaceUser spaceUser)
    {
        spaceUser = new OVRSpaceUser();
        return OVRPlugin.CreateSpaceUser(platformUserId, out spaceUser._handle);
    }

    /// <summary>
    /// Tries to create a handle to a specific Oculus user in the current conceptual space.
    /// </summary>
    /// <param name="platformUserId">
    /// A user's unique ID provided by a support platform, i.e. the org-unique Oculus user ID.
    /// You must request these IDs e.g. from the
    /// <a href="https://developer.oculus.com/documentation/unity/ps-platform-intro/">Platform SDK</a>. <br/>
    /// See also: <a href="https://developer.oculus.com/reference/platform-unity/latest/class_oculus_platform_users">Oculus.Platform.Users module</a>.
    /// </param>
    /// <param name="spaceUser">
    /// The result of the space user creation request; will only be <see cref="Valid"/> iff this method returned true.
    /// </param>
    /// <returns>
    /// <c>true</c> if <paramref name="spaceUser"/> is a valid handle to a user in the current space. <br/>
    /// <c>false</c> may be returned if <paramref name="platformUserId"/> is null or could not be parsed as a numerical ID.
    /// </returns>
    /// <remarks>
    /// <paramref name="platformUserId"/> should not represent the numerical value <c>0</c>.
    /// This is a common value to inadvertently obtain, usually in cases involving insufficient app, user, or device permissions.
    /// </remarks>
    public static bool TryCreate(string platformUserId, out OVRSpaceUser spaceUser)
    {
        if (ulong.TryParse(platformUserId, out ulong parsed))
            return TryCreate(parsed, out spaceUser);

        spaceUser = default;
        return false;
    }


    /// <summary>
    /// Checks if this is a valid handle to an authenticated space user.
    /// </summary>
    /// <remarks>
    /// The <see cref="OVRSpaceUser"/> may be invalid if it has been disposed (<see cref="Dispose"/>) or if creation
    /// fails.
    /// <example>
    /// For example:
    /// <code><![CDATA[
    /// void Test(ulong platformUserId) {
    ///   if (OVRspaceUser.TryCreate(platformUserId, out var spaceUser)) {
    ///     Debug.Log(spaceUser.Valid); // True
    ///
    ///     spaceUser.Dispose();
    ///     Debug.Log(spaceUser.Valid); // False; spaceUser disposed
    ///   } else {
    ///     Debug.Log(spaceUser.Valid); // False; creation failed
    ///   }
    /// }
    /// ]]></code></example>
    /// </remarks>
    public bool Valid => _handle != 0 && Id != 0;

    /// <summary>
    /// Creates a handle for a specific platform user in the current conceptual space.
    /// </summary>
    /// <param name="spaceUserId">
    /// A user's unique ID provided by a support platform, i.e. the org-unique Oculus user ID.
    /// You must request these IDs e.g. from the
    /// <a href="https://developer.oculus.com/documentation/unity/ps-platform-intro/">Platform SDK</a>. <br/>
    /// See also: <a href="https://developer.oculus.com/reference/platform-unity/latest/class_oculus_platform_users">Oculus.Platform.Users module</a>.
    /// </param>
    /// <remarks>
    /// This constructor does not perform any kind of validation.
    /// You should consider checking the <see cref="Valid"/> property after construction,
    /// or use the <see cref="TryCreate(ulong,out OVRSpaceUser)"/> static method instead.
    /// </remarks>
    [System.Obsolete("Constructor ignores validation. Use TryCreate(*) methods instead.", error: false)]
    public OVRSpaceUser(ulong spaceUserId)
    {
        _ = OVRPlugin.CreateSpaceUser(spaceUserId, out _handle);
    }

    /// <summary>
    /// The org-unique Oculus user ID associated with this <see cref="OVRSpaceUser"/>.
    /// </summary>
    /// <remarks>
    /// This property is the `spaceUserId` argument provided to the  <see cref="OVRSpaceUser"/> constructor, or zero
    /// if the <see cref="OVRSpaceUser"/> is not valid (<see cref="Valid"/> is false).
    /// </remarks>
    public ulong Id => _handle == 0 ? 0 : OVRPlugin.GetSpaceUserId(_handle, out var userId) ? userId : 0;

    /// <summary>
    /// Disposes of the <see cref="OVRSpaceUser"/>.
    /// </summary>
    /// <remarks>
    /// This method does not destroy the user account. It disposes the handle used to reference it.
    /// </remarks>
    public void Dispose()
    {
        if (_handle == 0)
            return;
        OVRPlugin.DestroySpaceUser(_handle);
        _handle = 0;
    }


    internal ulong _handle;

}
