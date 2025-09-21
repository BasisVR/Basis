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
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

partial class OVRSpatialAnchor
{
    /// <summary>
    /// (Obsolete) Initializes this component from an existing space handle and uuid, for example, the result of a call to
    /// <see cref="OVRPlugin.QuerySpaces"/>.
    /// </summary>
    /// <remarks>
    /// \deprecated This method is obsolete. To create a new anchor, use
    /// <code><![CDATA[AddComponent<OVRSpatialAnchor>()]]></code>. To load a previously saved anchor, use
    /// <see cref="LoadUnboundAnchorsAsync"/>.
    ///
    /// This method associates the component with an existing spatial anchor, for example, the one that was saved in
    /// a previous session. Do not call this method to create a new spatial anchor.
    ///
    /// If you call this method, you must do so prior to the component's `Start` method. You cannot change the spatial
    /// anchor associated with this component after that.
    /// </remarks>
    /// <param name="space">The existing <see cref="OVRSpace"/> to associate with this spatial anchor.</param>
    /// <param name="uuid">The universally unique identifier to associate with this spatial anchor.</param>
    /// <exception cref="InvalidOperationException">Thrown if `Start` has already been called on this component.</exception>
    /// <exception cref="ArgumentException">Thrown if <paramref name="space"/> is not <see cref="OVRSpace.Valid"/>.</exception>
    [Obsolete("You should use LoadUnboundAnchorsAsync to load previously saved anchors and" +
              " AddComponent<OVRSpatialAnchor>() to create a new anchor. You should no longer need to use an OVRSpace" +
              " handle directly.")]
    public void InitializeFromExisting(OVRSpace space, Guid uuid)
    {
        if (_startCalled)
            throw new InvalidOperationException(
                $"Cannot call {nameof(InitializeFromExisting)} after {nameof(Start)}. This must be set once upon creation.");

        try
        {
            if (!space.Valid)
                throw new ArgumentException($"Invalid space {space}.", nameof(space));

            ThrowIfBound(uuid);
        }
        catch
        {
            Destroy(this);
            throw;
        }

        InitializeUnchecked(space, uuid);
    }

    /// <summary>
    /// (Obsolete) Saves the <see cref="OVRSpatialAnchor"/> to local persistent storage.
    /// </summary>
    /// <remarks>
    /// \deprecated This method is obsolete. Use <see cref="SaveAsync()"/> instead. To continue using the
    /// <paramref name="onComplete"/> callback, use <see cref="OVRTask{T}.ContinueWith"/> on the returned task.
    ///
    /// This method is asynchronous. Use <paramref name="onComplete"/> to be notified of completion.
    ///
    /// When saved, an <see cref="OVRSpatialAnchor"/> can be loaded by a different session. Use the
    /// <see cref="Uuid"/> to identify the same <see cref="OVRSpatialAnchor"/> at a future time.
    ///
    /// This operation fully succeeds or fails, which means, either all anchors are successfully saved,
    /// or the operation fails.
    /// </remarks>
    /// <param name="onComplete">
    /// Invoked when the save operation completes. May be null. Parameters are
    /// - <see cref="OVRSpatialAnchor"/>: The anchor being saved.
    /// - `bool`: A value indicating whether the save operation succeeded.
    /// </param>
    [Obsolete("Use SaveAsync instead.")]
    public void Save(Action<OVRSpatialAnchor, bool> onComplete = null)
    {
        Save(_defaultSaveOptions, onComplete);
    }

    /// <summary>
    /// (Obsolete) Saves the <see cref="OVRSpatialAnchor"/> with specified <see cref="SaveOptions"/>.
    /// </summary>
    /// <remarks>
    /// \deprecated This method is obsolete. Use <see cref="SaveAsync()"/> instead. To continue using the
    /// <paramref name="onComplete"/> callback, use <see cref="OVRTask{T}.ContinueWith"/> on the returned task.
    ///
    /// This method is asynchronous. Use <paramref name="onComplete"/> to be notified of completion.
    /// When saved, the <see cref="OVRSpatialAnchor"/> can be loaded by a different session. Use the
    /// <see cref="Uuid"/> to identify the same <see cref="OVRSpatialAnchor"/> at a future time.
    ///
    /// This operation fully succeeds or fails; that is, either all anchors are successfully saved,
    /// or the operation fails.
    /// </remarks>
    /// <param name="saveOptions">Save options, e.g., whether local or cloud.</param>
    /// <param name="onComplete">
    /// Invoked when the save operation completes. May be null. Parameters are
    /// - <see cref="OVRSpatialAnchor"/>: The anchor being saved.
    /// - `bool`: A value indicating whether the save operation succeeded.
    /// </param>
    [Obsolete("Use SaveAsync instead.")]
    public void Save(SaveOptions saveOptions, Action<OVRSpatialAnchor, bool> onComplete = null)
    {
        var task = SaveAsync(saveOptions);
        if (onComplete != null)
        {
            InvertedCapture<bool, OVRSpatialAnchor>.ContinueTaskWith(task, onComplete, this);
        }
    }

    /// <summary>
    /// (Obsolete) The space associated with the spatial anchor.
    /// </summary>
    /// <remarks>
    /// \deprecated This property is obsolete. This class provides all spatial anchor functionality and it should not be
    /// necessary to use this low-level handle directly. See <see cref="SaveAsync()"/>,
    /// <see cref="ShareAsync(OVRSpaceUser)"/>, and <see cref="EraseAsync()"/>.
    ///
    /// The <see cref="OVRSpace"/> represents the runtime instance of the spatial anchor and will change across
    /// different sessions.
    /// </remarks>
    [Obsolete("This property exposes an internal handle that should no longer be necessary. You can Save, Erase," +
              " and Share anchors using the methods in this class.")]
    public OVRSpace Space => _anchor.Handle;

    /// <summary>
    /// (Obsolete) Shares the anchor to an <see cref="OVRSpaceUser"/>.
    /// The specified user will be able to download, track, and share specified anchors.
    /// </summary>
    /// <remarks>
    /// \deprecated This method is obsolete. Use <see cref="ShareAsync(OVRSpaceUser)"/> instead. To continue using the
    /// <paramref name="onComplete"/> callback, use <see cref="OVRTask{T}.ContinueWith"/> on the returned task.
    ///
    /// This method is asynchronous. Use <paramref name="onComplete"/> to be notified of completion.
    /// </remarks>
    /// <param name="user">An Oculus user to share the anchor with.</param>
    /// <param name="onComplete">
    /// Invoked when the share operation completes. May be null. Delegate parameter is
    /// - `OperationResult`: An error code that indicates whether the share operation succeeded or not.
    /// </param>
    [Obsolete("Use ShareAsync instead.")]
    public void Share(OVRSpaceUser user, Action<OperationResult> onComplete = null)
    {
        var task = ShareAsync(user);
        if (onComplete != null)
        {
            task.ContinueWith(onComplete);
        }
    }

    /// <summary>
    /// (Obsolete) Shares the anchor with two <see cref="OVRSpaceUser"/>.
    /// Specified users will be able to download, track, and share specified anchors.
    /// </summary>
    /// <remarks>
    /// \deprecated This method is obsolete. Use <see cref="ShareAsync(OVRSpaceUser, OVRSpaceUser)"/> instead. To continue
    /// using the <paramref name="onComplete"/> callback, use <see cref="OVRTask{T}.ContinueWith"/> on the returned task.
    ///
    /// This method is asynchronous. Use <paramref name="onComplete"/> to be notified of completion.
    /// </remarks>
    /// <param name="user1">An Oculus user to share the anchor with.</param>
    /// <param name="user2">An Oculus user to share the anchor with.</param>
    /// <param name="onComplete">
    /// Invoked when the share operation completes. May be null. Delegate parameter is
    /// - `OperationResult`: An error code that indicates whether the share operation succeeded or not.
    /// </param>
    [Obsolete("Use ShareAsync instead.")]
    public void Share(OVRSpaceUser user1, OVRSpaceUser user2, Action<OperationResult> onComplete = null)
    {
        var task = ShareAsync(user1, user2);
        if (onComplete != null)
        {
            task.ContinueWith(onComplete);
        }
    }

    /// <summary>
    /// (Obsolete) Shares the anchor with three <see cref="OVRSpaceUser"/>.
    /// Specified users will be able to download, track, and share specified anchors.
    /// </summary>
    /// <remarks>
    /// \deprecated This method is obsolete. Use <see cref="ShareAsync(OVRSpaceUser, OVRSpaceUser, OVRSpaceUser)"/> instead.
    /// To continue using the <paramref name="onComplete"/> callback, use <see cref="OVRTask{T}.ContinueWith"/> on the
    /// returned task.
    ///
    /// This method is asynchronous. Use <paramref name="onComplete"/> to be notified of completion.
    /// </remarks>
    /// <param name="user1">An Oculus user to share the anchor with.</param>
    /// <param name="user2">An Oculus user to share the anchor with.</param>
    /// <param name="user3">An Oculus user to share the anchor with.</param>
    /// <param name="onComplete">
    /// Invoked when the share operation completes. May be null. Delegate parameter is
    /// - `OperationResult`: An error code that indicates whether the share operation succeeded or not.
    /// </param>
    [Obsolete("Use ShareAsync instead.")]
    public void Share(OVRSpaceUser user1, OVRSpaceUser user2, OVRSpaceUser user3,
        Action<OperationResult> onComplete = null)
    {
        var task = ShareAsync(user1, user2, user3);
        if (onComplete != null)
        {
            task.ContinueWith(onComplete);
        }
    }

    /// <summary>
    /// (Obsolete) Shares the anchor with four <see cref="OVRSpaceUser"/>.
    /// Specified users will be able to download, track, and share specified anchors.
    /// </summary>
    /// <remarks>
    /// \deprecated This method is obsolete. Use
    /// <see cref="ShareAsync(OVRSpaceUser, OVRSpaceUser, OVRSpaceUser, OVRSpaceUser)"/> instead. To continue using the
    /// <paramref name="onComplete"/> callback, use <see cref="OVRTask{T}.ContinueWith"/> on the returned task.
    ///
    /// This method is asynchronous. Use <paramref name="onComplete"/> to be notified of completion.
    /// </remarks>
    /// <param name="user1">An Oculus user to share the anchor with.</param>
    /// <param name="user2">An Oculus user to share the anchor with.</param>
    /// <param name="user3">An Oculus user to share the anchor with.</param>
    /// <param name="user4">An Oculus user to share the anchor with.</param>
    /// <param name="onComplete">
    /// Invoked when the share operation completes. May be null. Delegate parameter is
    /// - `OperationResult`: An error code that indicates whether the share operation succeeded or not.
    /// </param>
    [Obsolete("Use ShareAsync instead.")]
    public void Share(OVRSpaceUser user1, OVRSpaceUser user2, OVRSpaceUser user3, OVRSpaceUser user4,
        Action<OperationResult> onComplete = null)
    {
        var task = ShareAsync(user1, user2, user3, user4);
        if (onComplete != null)
        {
            task.ContinueWith(onComplete);
        }
    }

    /// <summary>
    /// (Obsolete) Shares the anchor to a collection of <see cref="OVRSpaceUser"/>.
    /// Specified users will be able to download, track, and share specified anchors.
    /// </summary>
    /// <remarks>
    /// \deprecated This method is obsolete. Use <see cref="ShareAsync(IEnumerable{OVRSpaceUser})"/>. To continue using the
    /// <paramref name="onComplete"/> callback, use <see cref="OVRTask{T}.ContinueWith"/> on the returned task.
    ///
    /// This method is asynchronous. Use <paramref name="onComplete"/> to be notified of completion.
    /// </remarks>
    /// <param name="users">A collection of Oculus users to share the anchor with.</param>
    /// <param name="onComplete">
    /// Invoked when the share operation completes. May be null. Delegate parameter is
    /// - `OperationResult`: An error code that indicates whether the share operation succeeded or not.
    /// </param>
    [Obsolete("Use ShareAsync instead.")]
    public void Share(IEnumerable<OVRSpaceUser> users, Action<OperationResult> onComplete = null)
    {
        var task = ShareAsync(users);
        if (onComplete != null)
        {
            task.ContinueWith(onComplete);
        }
    }

    /// <summary>
    /// (Obsolete) Erases the <see cref="OVRSpatialAnchor"/> from persistent storage.
    /// </summary>
    /// <remarks>
    /// \deprecated This method is obsolete. Use <see cref="EraseAsync()"/>. To continue using the
    /// <paramref name="onComplete"/> callback, use <see cref="OVRTask{T}.ContinueWith"/> on the returned task.
    ///
    /// This method is asynchronous. Use <paramref name="onComplete"/> to be notified of completion.
    /// Erasing an <see cref="OVRSpatialAnchor"/> does not destroy the anchor.
    /// </remarks>
    /// <param name="onComplete">
    /// Invoked when the erase operation completes. May be null. Parameters are
    /// - <see cref="OVRSpatialAnchor"/>: The anchor being erased.
    /// - `bool`: A value indicating whether the erase operation succeeded.
    /// </param>
    [Obsolete("Use EraseAsync instead.")]
    public void Erase(Action<OVRSpatialAnchor, bool> onComplete = null)
    {
        Erase(_defaultEraseOptions, onComplete);
    }

    /// <summary>
    /// (Obsolete) Erases the <see cref="OVRSpatialAnchor"/> from specified storage.
    /// </summary>
    /// <remarks>
    /// \deprecated This method is obsolete. Use <see cref="EraseAsync(EraseOptions)"/>. To continue using the
    /// <paramref name="onComplete"/> callback, use <see cref="OVRTask{T}.ContinueWith"/> on the returned task.
    ///
    /// This method is asynchronous. Use <paramref name="onComplete"/> to be notified of completion.
    /// Erasing an <see cref="OVRSpatialAnchor"/> does not destroy the anchor.
    /// </remarks>
    /// <param name="eraseOptions">Options how the anchor should be erased.</param>
    /// <param name="onComplete">
    /// Invoked when the erase operation completes. May be null. Parameters are
    /// - <see cref="OVRSpatialAnchor"/>: The anchor being erased.
    /// - `bool`: A value indicating whether the erase operation succeeded.
    /// </param>
    [Obsolete("Use EraseAsync instead.")]
    public void Erase(EraseOptions eraseOptions, Action<OVRSpatialAnchor, bool> onComplete = null)
    {
        var task = EraseAsync(eraseOptions);

        if (onComplete != null)
        {
            InvertedCapture<bool, OVRSpatialAnchor>.ContinueTaskWith(task, onComplete, this);
        }
    }

    /// <summary>
    /// (Obsolete) Performs a query for anchors with the specified <paramref name="options"/>.
    /// </summary>
    /// <remarks>
    /// \deprecated This method is obsolete. Use <see cref="LoadUnboundAnchorsAsync"/>. To continue using the
    /// <paramref name="onComplete"/> callback, use <see cref="OVRTask{T}.ContinueWith"/> on the returned task.
    ///
    /// Use this method to find anchors that were previously persisted with
    /// <see cref="Save(Action{OVRSpatialAnchor, bool}"/>. The query is asynchronous; when the query completes,
    /// <paramref name="onComplete"/> is invoked with an array of <see cref="UnboundAnchor"/>s for which tracking
    /// may be requested.
    /// </remarks>
    /// <param name="options">Options that affect the query.</param>
    /// <param name="onComplete">A delegate invoked when the query completes. The delegate accepts one argument:
    /// - `UnboundAnchor[]`: An array of unbound anchors.
    ///
    /// If the operation fails, <paramref name="onComplete"/> is invoked with `null`.</param>
    /// <returns>Returns `true` if the operation could be initiated; otherwise `false`.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="onComplete"/> is `null`.</exception>
    /// <exception cref="InvalidOperationException">Thrown if <see cref="LoadOptions.Uuids"/> of <paramref name="options"/> is `null`.</exception>
    [Obsolete("Use LoadUnboundAnchorsAsync instead.")]
    public static bool LoadUnboundAnchors(LoadOptions options, Action<UnboundAnchor[]> onComplete)
    {
        var task = LoadUnboundAnchorsAsync(options);
        task.ContinueWith(onComplete);
        return task.IsPending;
    }

    partial struct UnboundAnchor
    {
        /// <summary>
        /// (Obsolete) Localizes an anchor.
        /// </summary>
        /// <remarks>
        /// \deprecated This method is obsolete. Use <see cref="LocalizeAsync"/> instead. To continue using the
        /// <paramref name="onComplete"/> callback, use <see cref="OVRTask{T}.ContinueWith"/> on the returned task.
        ///
        /// The delegate supplied to <see cref="OVRSpatialAnchor.LoadUnboundAnchors"/> receives an array of unbound
        /// spatial anchors. You can choose whether to localize each one and be notified when localization completes.
        ///
        /// The <paramref name="onComplete"/> delegate receives two arguments:
        /// - `bool`: Whether localization was successful
        /// - <see cref="UnboundAnchor"/>: The anchor to bind
        ///
        /// Upon successful localization, your delegate should instantiate an <see cref="OVRSpatialAnchor"/>, then bind
        /// the <see cref="UnboundAnchor"/> to the <see cref="OVRSpatialAnchor"/> by calling
        /// <see cref="UnboundAnchor.BindTo"/>. Once an <see cref="UnboundAnchor"/> is bound to an
        /// <see cref="OVRSpatialAnchor"/>, it cannot be used again; that is, it cannot be bound to multiple
        /// <see cref="OVRSpatialAnchor"/> components.
        /// </remarks>
        /// <param name="onComplete">A delegate invoked when localization completes (which may fail). The delegate
        /// receives two arguments:
        /// - <see cref="UnboundAnchor"/>: The anchor to bind
        /// - `bool`: Whether localization was successful
        /// </param>
        /// <param name="timeout">The timeout, in seconds, to attempt localization, or zero to indicate no timeout.</param>
        /// <exception cref="InvalidOperationException">Thrown if
        /// - The anchor does not support localization, e.g., because it is invalid.
        /// - The anchor has already been localized.
        /// - The anchor is being localized, e.g., because <see cref="Localize"/> was previously called.
        /// </exception>
        [Obsolete("Use LocalizeAsync instead.")]
        public void Localize(Action<UnboundAnchor, bool> onComplete = null, double timeout = 0)
        {
            var task = LocalizeAsync(timeout);

            if (onComplete != null)
            {
                InvertedCapture<bool, UnboundAnchor>.ContinueTaskWith(task, onComplete, this);
            }
        }

        /// <summary>
        /// (Obsolete) The world space pose of the spatial anchor.
        /// </summary>
        /// <remarks>
        /// \deprecated This method is obsolete. Acquiring the pose can fail; consider <see cref="TryGetPose"/> instead.
        /// </remarks>
        /// <seealso cref="TryGetPose"/>
        [Obsolete("Use TryGetPose instead.")]
        public Pose Pose
        {
            get
            {
                if (!TryGetPose(out var pose))
                    throw new InvalidOperationException(
                        $"[{Uuid}] Anchor must be localized before obtaining its pose.");

                return pose;
            }
        }
    }

    /// <summary>
    /// (Obsolete) Shares a collection of <see cref="OVRSpatialAnchor"/> to specified users.
    /// Specified users will be able to download, track, and share specified anchors.
    /// </summary>
    /// <remarks>
    /// \deprecated This method is obsolete. Use
    /// <see cref="ShareAsync(IEnumerable{OVRSpatialAnchor},IEnumerable{OVRSpaceUser})"/> instead. To continue using the
    /// <paramref name="onComplete"/> callback, use <see cref="OVRTask{T}.ContinueWith"/> on the returned task.
    ///
    /// This method is asynchronous. Use <paramref name="onComplete"/> to be notified of completion.
    ///
    /// This operation fully succeeds or fails, which means, either all anchors are successfully shared
    /// or the operation fails.
    /// </remarks>
    /// <param name="anchors">The collection of anchors to share.</param>
    /// <param name="users">An array of Oculus users to share these anchors with.</param>
    /// <param name="onComplete">
    /// Invoked when the share operation completes. May be null. Delegate parameter is
    /// - `ICollection&lt;OVRSpatialAnchor&gt;`: The collection of anchors being shared.
    /// - `OperationResult`: An error code that indicates whether the share operation succeeded or not.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="anchors"/> is `null`.</exception>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="users"/> is `null`.</exception>
    [Obsolete("Use ShareAsync instead.")]
    public static void Share(ICollection<OVRSpatialAnchor> anchors, ICollection<OVRSpaceUser> users,
        Action<ICollection<OVRSpatialAnchor>, OperationResult> onComplete = null)
    {
        if (anchors == null)
            throw new ArgumentNullException(nameof(anchors));
        if (users == null)
            throw new ArgumentNullException(nameof(users));

        using var spaces = ToNativeArray(anchors);

        var handles = new NativeArray<ulong>(users.Count, Allocator.Temp);
        using var disposer = handles;
        int i = 0;
        foreach (var user in users)
        {
            handles[i++] = user._handle;
        }

        var shareResult = OVRPlugin.ShareSpaces(spaces, handles, out var requestId);
        if (shareResult.IsSuccess())
        {
            Development.LogRequest(requestId, $"Sharing {(uint)spaces.Length} spatial anchors...");

            MultiAnchorCompletionDelegates[requestId] = new MultiAnchorDelegatePair
            {
                Anchors = CopyAnchorListIntoListFromPool(anchors),
                Delegate = onComplete
            };
        }
        else
        {
            Development.LogError(
                $"{nameof(OVRPlugin)}.{nameof(OVRPlugin.ShareSpaces)}  failed with error {shareResult}.");
            onComplete?.Invoke(anchors, (OperationResult)shareResult);
        }
    }

    /// <summary>
    /// (Obsolete) Saves a collection of anchors to persistent storage.
    /// </summary>
    /// <remarks>
    /// \deprecated This method is obsolete. Use
    /// <see cref="SaveAsync(IEnumerable{OVRSpatialAnchor}, SaveOptions)"/> instead. To continue using the
    /// <paramref name="onComplete"/> callback, use <see cref="OVRTask{T}.ContinueWith"/> on the returned task.
    ///
    /// This method is asynchronous. Use <paramref name="onComplete"/> to be notified of completion.
    /// When saved, an <see cref="OVRSpatialAnchor"/> can be loaded by a different session. Use the
    /// <see cref="Uuid"/> to identify the same <see cref="OVRSpatialAnchor"/> at a future time.
    /// </remarks>
    /// <param name="anchors">Collection of anchors</param>
    /// <param name="saveOptions">Save options, e.g., whether local or cloud.</param>
    /// <param name="onComplete">
    /// Invoked when the save operation completes. May be null. <paramref name="onComplete"/> receives two parameters:
    /// - `ICollection&lt;OVRSpatialAnchor&gt;`: The same collection as in <paramref name="anchors"/> parameter
    /// - `OperationResult`: An error code indicating whether the save operation succeeded or not.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="anchors"/> is `null`.</exception>
    [Obsolete("Use SaveAsync instead.")]
    public static void Save(ICollection<OVRSpatialAnchor> anchors, SaveOptions saveOptions,
        Action<ICollection<OVRSpatialAnchor>, OperationResult> onComplete = null)
    {
        if (anchors == null)
            throw new ArgumentNullException(nameof(anchors));

        using var spaces = ToNativeArray(anchors);
        OVRPlugin.Result saveResult;
        ulong requestId;
        unsafe
        {
            saveResult = OVRAnchor.SaveSpaceList((ulong*)spaces.GetUnsafeReadOnlyPtr(), (uint)spaces.Length,
                saveOptions.Storage.ToSpaceStorageLocation(), out requestId);
        }

        if (saveResult.IsSuccess())
        {
            Development.LogRequest(requestId, $"Saving spatial anchors...");

            MultiAnchorCompletionDelegates[requestId] = new MultiAnchorDelegatePair
            {
                Anchors = CopyAnchorListIntoListFromPool(anchors),
                Delegate = onComplete
            };
        }
        else
        {
            Development.LogError(
                $"{nameof(OVRPlugin)}.{nameof(OVRPlugin.SaveSpaceList)} failed with error {saveResult}.");
            onComplete?.Invoke(anchors, (OperationResult)saveResult);
        }
    }

    /// <summary>
    /// (Obsolete) Represents options for erasing an <see cref="OVRSpatialAnchor"/>.
    /// </summary>
    /// <remarks>
    /// \deprecated This method is obsolete. You no longer need to provide a storage location when erasing an anchor.
    ///
    /// This struct is used to provide input parameters to the following (now obsolete) methods:
    /// - <see cref="OVRSpatialAnchor.Erase(EraseOptions,Action{OVRSpatialAnchor,bool})"/> and
    /// - <see cref="OVRSpatialAnchor.EraseAsync(EraseOptions)"/> and
    /// </remarks>
    [Obsolete("Use EraseAnchorAsync instead, which does not require you to provide EraseOptions.")]
    public struct EraseOptions
    {
        /// <summary>
        /// (Obsolete) Location from where the <see cref="OVRSpatialAnchor"/> will be erased.
        /// </summary>
        /// <remarks>
        /// \deprecated You no longer need to provide a storage location when erasing anchors.
        ///
        /// In the now obsolete methods, you could specify a storage location (local or cloud)
        /// from which to erase the anchor. This is no longer required.
        /// </remarks>
        public OVRSpace.StorageLocation Storage;
    }

    [Obsolete("See SaveAnchorAsync overload without SaveOptions")]
    private readonly SaveOptions _defaultSaveOptions = new()
    {
        Storage = OVRSpace.StorageLocation.Local,
    };

    [Obsolete("See EraseAnchorAsync overload without EraseOptions")]
    private readonly EraseOptions _defaultEraseOptions = new()
    {
        Storage = OVRSpace.StorageLocation.Local,
    };

    /// <summary>
    /// (Obsolete) Erases the <see cref="OVRSpatialAnchor"/> from specified storage.
    /// </summary>
    /// <remarks>
    /// \deprecated This method is obsolete. Use <see cref="EraseAnchorAsync"/> instead.
    ///
    /// This method is asynchronous; use the returned <see cref="OVRTask"/> to be notified of completion.
    /// Erasing an <see cref="OVRSpatialAnchor"/> does not destroy the anchor.
    /// </remarks>
    /// <returns>
    /// Returns an <see cref="OVRTask"/>&lt;bool&gt; indicating the success of the erase operation.
    /// </returns>
    [Obsolete("Use EraseAnchorAsync instead.")]
    public OVRTask<bool> EraseAsync() => EraseAsync(_defaultEraseOptions);

    /// <summary>
    /// (Obsolete) Erases the <see cref="OVRSpatialAnchor"/> from specified storage.
    /// </summary>
    /// <remarks>
    /// \deprecated This method is obsolete. Use <see cref="EraseAnchorAsync"/> instead.
    ///
    /// This method is asynchronous; use the returned <see cref="OVRTask"/> to be notified of completion.
    /// Erasing an <see cref="OVRSpatialAnchor"/> does not destroy the anchor.
    /// </remarks>
    /// <param name="eraseOptions">Options for how the anchor should be erased.</param>
    /// <returns>
    /// Returns an <see cref="OVRTask"/>&lt;bool&gt; indicating the success of the erase operation.
    /// </returns>
    [Obsolete("Use EraseAnchorAsync instead.")]
    public OVRTask<bool> EraseAsync(EraseOptions eraseOptions) => OVRTask
        .Build(
            OVRAnchor.EraseSpace(_anchor.Handle, eraseOptions.Storage.ToSpaceStorageLocation(), out var requestId),
            requestId)
        .ToTask(failureValue: false);

    /// <summary>
    /// (Obsolete) Represents options for saving an <see cref="OVRSpatialAnchor"/>.
    /// </summary>
    /// <remarks>
    /// \deprecated This method is obsolete. You no longer need to provide a storage location when saving anchors.
    ///
    /// This struct is used to provide input parameters to the following (now obsolete) methods:
    /// - <see cref="OVRSpatialAnchor.Save(SaveOptions,Action{OVRSpatialAnchor,bool})"/> and
    /// - <see cref="OVRSpatialAnchor.SaveAsync(SaveOptions)"/> and
    /// </remarks>
    [Obsolete("Use SaveAnchorAsync instead, which does not require you to provide SaveOptions.")]
    public struct SaveOptions
    {
        /// <summary>
        /// Location where <see cref="OVRSpatialAnchor"/> will be saved.
        /// </summary>
        /// <remarks>
        /// \deprecated You no longer need to provide a storage location when saving an anchor.
        ///
        /// In the now obsolete save methods, you could specify a storage location (local or cloud)
        /// from which to save the anchor. This is no longer required.
        /// </remarks>
        public OVRSpace.StorageLocation Storage;
    }

    /// <summary>
    /// (Obsolete) Saves the <see cref="OVRSpatialAnchor"/> with specified <see cref="SaveOptions"/>.
    /// </summary>
    /// <remarks>
    /// \deprecated This method is obsolete. Use <see cref="SaveAnchorAsync"/> instead.
    ///
    /// This method is asynchronous; use the returned <see cref="OVRTask"/> to be notified of completion.
    /// When saved, the <see cref="OVRSpatialAnchor"/> can be loaded by a different session. Use the
    /// <see cref="Uuid"/> to identify the same <see cref="OVRSpatialAnchor"/> at a future time.
    ///
    /// This operation fully succeeds or fails; that is, either all anchors are successfully saved,
    /// or the operation fails.
    /// </remarks>
    /// <returns>
    /// Returns an <see cref="OVRTask"/>&lt;bool&gt; indicating the success of the save operation.
    /// </returns>
    [Obsolete("Use SaveAnchorAsync instead.")]
    public OVRTask<bool> SaveAsync() => SaveAsync(_defaultSaveOptions);

    /// <summary>
    /// (Obsolete) Saves the <see cref="OVRSpatialAnchor"/> with specified <see cref="SaveOptions"/>.
    /// </summary>
    /// <remarks>
    /// \deprecated This method is obsolete. Use <see cref="SaveAnchorAsync"/> instead.
    ///
    /// This method is asynchronous; use the returned <see cref="OVRTask"/> to be notified of completion.
    /// When saved, the <see cref="OVRSpatialAnchor"/> can be loaded by a different session. Use the
    /// <see cref="Uuid"/> to identify the same <see cref="OVRSpatialAnchor"/> at a future time.
    ///
    /// This operation fully succeeds or fails; that is, either all anchors are successfully saved,
    /// or the operation fails.
    /// </remarks>
    /// <param name="saveOptions">Options for how the anchor will be saved.</param>
    /// <returns>
    /// Returns an <see cref="OVRTask"/>&lt;bool&gt; indicating the success of the save operation.
    /// </returns>
    [Obsolete("Use SaveAnchorAsync instead.")]
    public OVRTask<bool> SaveAsync(SaveOptions saveOptions)
    {
        var requestId = Guid.NewGuid();
        SaveRequests[saveOptions.Storage].Add(this);
        AsyncRequestTaskIds[this] = requestId;
        return OVRTask.FromGuid<bool>(requestId);
    }

    /// <summary>
    /// (Obsolete) Saves a collection of anchors to persistent storage.
    /// </summary>
    /// <remarks>
    /// \deprecated This method is obsolete. Use <see cref="SaveAnchorsAsync"/> instead.
    ///
    /// This method is asynchronous. Use the returned <see cref="OVRTask"/> to track the progress of the
    /// save operation.
    ///
    /// When saved, an <see cref="OVRSpatialAnchor"/> can be loaded in a different session. Use the
    /// <see cref="Uuid"/> to identify the same <see cref="OVRSpatialAnchor"/> at a future time.
    /// </remarks>
    /// <param name="anchors">The collection of anchors to save.</param>
    /// <param name="saveOptions">Save options, e.g., whether local or cloud.</param>
    /// <returns>Returns a task that represents the asynchronous save operation.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="anchors"/> is `null`.</exception>
    [Obsolete("Use SaveAnchorsAsync instead.")]
    public static OVRTask<OperationResult> SaveAsync(IEnumerable<OVRSpatialAnchor> anchors, SaveOptions saveOptions)
    {
        if (anchors == null)
            throw new ArgumentNullException(nameof(anchors));

        unsafe
        {
            using var spaces = new OVRNativeList<ulong>(anchors.ToNonAlloc().Count, Allocator.Temp);
            foreach (var anchor in anchors.ToNonAlloc())
            {
                spaces.Add(anchor._anchor.Handle);
            }

            var result = OVRAnchor.SaveSpaceList(spaces, (uint)spaces.Count,
                saveOptions.Storage.ToSpaceStorageLocation(), out var requestId);

            Development.LogRequestOrError(requestId, result,
                $"Saving {spaces.Count} spatial anchors.",
                $"xrSaveSpaceListFB failed with error {result}.");

            return OVRTask.Build(result, requestId).ToTask<OperationResult>();
        }
    }

    /// <summary>
    /// (Obsolete) Performs a query for anchors with the specified <paramref name="options"/>.
    /// </summary>
    /// <remarks>
    /// \deprecated This method is obsolete. Use
    /// <see cref="LoadUnboundAnchorsAsync(IEnumerable{Guid},List{UnboundAnchor},Action{List{UnboundAnchor}, int}"/>
    /// instead.
    ///
    /// Use this method to find anchors that were previously persisted with
    /// <see cref="Save(Action{OVRSpatialAnchor, bool}"/>. The query is asynchronous; when the query completes,
    /// the returned <see cref="OVRTask"/> will contain an array of <see cref="UnboundAnchor"/>s for which tracking
    /// may be requested.
    /// </remarks>
    /// <param name="options">Options that affect the query.</param>
    /// <returns>
    /// Returns an <see cref="OVRTask"/> with a <see cref="T:UnboundAnchor[]"/> type parameter containing the loaded unbound anchors.
    /// </returns>
    /// <exception cref="InvalidOperationException">Thrown if <see cref="LoadOptions.Uuids"/> of <paramref name="options"/> is `null`.</exception>
    [Obsolete("Use the overload of LoadUnboundAnchorsAsync that accepts a collection of Guids instead.")]
    public static OVRTask<UnboundAnchor[]> LoadUnboundAnchorsAsync(LoadOptions options)
    {
        if (options.Uuids == null)
        {
            throw new InvalidOperationException($"{nameof(LoadOptions)}.{nameof(LoadOptions.Uuids)} must not be null.");
        }

        var result = options.ToQueryOptions().TryQuerySpaces(out var requestId);
        if (result)
        {
            Development.LogRequest(requestId, $"{nameof(OVRPlugin.QuerySpaces)}: Query created.");
        }
        else
        {
            Development.LogError($"{nameof(OVRPlugin.QuerySpaces)} failed.");
        }

        return OVRTask.Build(result, requestId).ToTask<UnboundAnchor[]>(failureValue: null);
    }

    private static NativeArray<ulong> ToNativeArray(ICollection<OVRSpatialAnchor> anchors)
    {
        var count = anchors.Count;
        var spaces = new NativeArray<ulong>(count, Allocator.Temp);
        var i = 0;
        foreach (var anchor in anchors.ToNonAlloc())
        {
            spaces[i++] = anchor ? anchor._anchor.Handle : 0;
        }

        return spaces;
    }

    private static List<OVRSpatialAnchor> CopyAnchorListIntoListFromPool(
        IEnumerable<OVRSpatialAnchor> anchorList)
    {
        var poolList = OVRObjectPool.List<OVRSpatialAnchor>();
        poolList.AddRange(anchorList);
        return poolList;
    }

    [Obsolete]
    private static void SaveBatchAnchors()
    {
        foreach (var pair in SaveRequests)
        {
            if (pair.Value.Count == 0)
            {
                continue;
            }

            Save(pair.Value, new SaveOptions { Storage = pair.Key });
            pair.Value.Clear();
        }
    }

    private static void OnSpaceSaveComplete(ulong requestId, OVRSpace space, bool result, Guid uuid)
    {
        Development.LogRequestResult(requestId, result,
            $"[{uuid}] Saved.",
            $"[{uuid}] Save failed.");
    }

    private static void OnSpaceEraseComplete(ulong requestId, bool result, Guid uuid,
        OVRPlugin.SpaceStorageLocation location)
    {
        Development.LogRequestResult(requestId, result,
            $"[{uuid}] Erased.",
            $"[{uuid}] Erase failed.");
    }

    /// <summary>
    /// (Obsolete) Options for loading unbound <see cref="OVRSpatialAnchor"/>s used by
    /// <see cref="OVRSpatialAnchor.LoadUnboundAnchorsAsync(LoadOptions)"/>.
    /// </summary>
    /// <example>
    /// \deprecated This struct is obsolete. It is only for use with the obsolete
    /// <see cref="OVRSpatialAnchor.LoadUnboundAnchorsAsync(LoadOptions)"/>. Instead, consider the newer version
    /// <see cref="OVRSpatialAnchor.LoadUnboundAnchorsAsync(IEnumerable{Guid},List{UnboundAnchor},Action{List{UnboundAnchor},int})"/>.
    ///
    /// This example shows how to create <see cref="LoadOptions"/> for loading anchors when given a set of UUIDs.
    /// <example><code><![CDATA[
    /// OVRSpatialAnchor.LoadOptions options = new OVRSpatialAnchor.LoadOptions
    /// {
    ///     Timeout = 0,
    ///     Uuids = savedAnchorUuids
    /// };
    /// ]]></code></example>
    /// </example>
    [Obsolete("Only for use with the obsolete version of LoadUnboundAnchorsAsync. Use the overload of " +
              "LoadUnboundAnchorsAsync that accepts a collection of Guids")]
    public struct LoadOptions
    {
        /// <summary>
        /// The maximum number of uuids that may be present in the <see cref="Uuids"/> collection.
        /// </summary>
        public const int MaxSupported = OVRPlugin.SpaceFilterInfoIdsMaxSize;

        /// <summary>
        /// The storage location from which to query spatial anchors.
        /// </summary>
        public OVRSpace.StorageLocation StorageLocation { get; set; }

        /// <summary>
        /// (Obsolete) The maximum number of anchors to query.
        /// </summary>
        /// <remarks>
        /// In prior SDK versions, it was mandatory to set this property to receive any
        /// results. However, this property is now obsolete. If <see cref="MaxAnchorCount"/> is zero,
        /// i.e., the default initialized value, it will automatically be set to the count of
        /// <see cref="Uuids"/>.
        ///
        /// If non-zero, the number of anchors in the result will be limited to
        /// <see cref="MaxAnchorCount"/>, preserving the previous behavior.
        /// </remarks>
        [Obsolete(
            "This property is no longer required. MaxAnchorCount will be automatically set to the number of uuids to load.")]
        public int MaxAnchorCount { get; set; }

        /// <summary>
        /// The timeout, in seconds, for the query operation.
        /// </summary>
        /// <remarks>
        /// A value of zero indicates no timeout.
        /// </remarks>
        public double Timeout { get; set; }

        /// <summary>
        /// The set of spatial anchors to query, identified by their UUIDs.
        /// </summary>
        /// <remarks>
        /// The UUIDs are copied by the <see cref="OVRSpatialAnchor.LoadUnboundAnchorsAsync"/> method and no longer
        /// referenced internally afterwards.
        ///
        /// You must supply a list of UUIDs. <see cref="OVRSpatialAnchor.LoadUnboundAnchorsAsync"/> will throw if this
        /// property is null.
        /// </remarks>
        /// <exception cref="System.ArgumentException">Thrown if <see cref="Uuids"/> contains more
        ///     than <see cref="MaxSupported"/> elements.</exception>
        public IReadOnlyList<Guid> Uuids
        {
            get => _uuids;
            set
            {
                if (value?.Count > MaxSupported)
                    throw new ArgumentException(
                        $"There must not be more than {MaxSupported} UUIDs (new value contains {value.Count} UUIDs).",
                        nameof(value));

                _uuids = value;
            }
        }

        private IReadOnlyList<Guid> _uuids;

        internal OVRSpaceQuery.Options ToQueryOptions() => new OVRSpaceQuery.Options
        {
            Location = StorageLocation,
            MaxResults = MaxSupported,
            Timeout = Timeout,
            UuidFilter = Uuids,
            QueryType = OVRPlugin.SpaceQueryType.Action,
            ActionType = OVRPlugin.SpaceQueryActionType.Load,
        };
    }

    private static void OnSpaceQueryComplete(ulong requestId, bool queryResult)
    {
        Development.LogRequestResult(requestId, queryResult,
            $"{nameof(OVRPlugin.QuerySpaces)}: Query succeeded.",
            $"{nameof(OVRPlugin.QuerySpaces)}: Query failed.");

        if (!OVRTask.TryGetPendingTask<UnboundAnchor[]>(requestId, out var task))
        {
            return;
        }

        if (!queryResult)
        {
            task.SetResult(null);
            return;
        }

        if (OVRPlugin.RetrieveSpaceQueryResults(requestId, out var results, Allocator.Temp))
        {
            Development.Log(
                $"{nameof(OVRPlugin.RetrieveSpaceQueryResults)}({requestId}): Retrieved {results.Length} results.");
        }
        else
        {
            Development.LogError(
                $"{nameof(OVRPlugin.RetrieveSpaceQueryResults)}({requestId}): Failed to retrieve results.");
            task.SetResult(null);
            return;
        }

        using var disposer = results;

        using (new OVRObjectPool.ListScope<UnboundAnchor>(out var unboundAnchorList))
        {
            foreach (var result in results)
            {
                if (TryGetUnbound(new OVRAnchor(result.space, result.uuid), out var unboundAnchor))
                {
                    unboundAnchorList.Add(unboundAnchor);
                }
            }

            var unboundAnchors = unboundAnchorList.Count == 0
                ? Array.Empty<UnboundAnchor>()
                : unboundAnchorList.ToArray();

            Development.Log(
                $"Invoking callback with {unboundAnchors.Length} unbound anchor{(unboundAnchors.Length == 1 ? "" : "s")}.");

            task.SetResult(unboundAnchors);
        }
    }

    private static void OnSpaceListSaveComplete(ulong requestId, OperationResult result)
    {
        Development.LogRequestResult(requestId, result >= 0,
            $"Spaces saved.",
            $"Spaces save failed with error {result}.");

        OVRTask.SetResult(requestId, result);
        InvokeMultiAnchorDelegate(requestId, result, MultiAnchorActionType.Save);
    }

    [Obsolete]
    private static readonly Dictionary<OVRSpace.StorageLocation, List<OVRSpatialAnchor>> SaveRequests = new()
    {
        { OVRSpace.StorageLocation.Cloud, new List<OVRSpatialAnchor>() },
        { OVRSpace.StorageLocation.Local, new List<OVRSpatialAnchor>() },
    };
}
