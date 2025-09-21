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
using Object = UnityEngine.Object;

/// <summary>
/// Represents a scene anchor.
/// </summary>
/// <remarks>
/// A scene anchor is a type of anchor that is provided by the system. It represents an item in the physical
/// environment, such as a plane or volume. Scene anchors are created by the <see cref="OVRSceneManager"/>.
/// </remarks>
/// <seealso cref="OVRScenePlane"/>
/// <seealso cref="OVRSceneVolume"/>
/// <seealso cref="OVRSemanticClassification"/>
[DisallowMultipleComponent]
[HelpURL("https://developer.oculus.com/documentation/unity/unity-scene-use-scene-anchors/#ovrsceneanchor")]
[Obsolete(OVRSceneManager.DeprecationMessage)]
[Feature(Feature.Scene)]
public sealed class OVRSceneAnchor : MonoBehaviour
{
    /// <summary>
    /// The runtime handle of this scene anchor.
    /// </summary>
    public OVRSpace Space { get; private set; }

    /// <summary>
    /// The universally unique identifier for this scene anchor.
    /// </summary>
    public Guid Uuid { get; private set; }

    /// <summary>
    /// Associated OVRAnchor
    /// </summary>
    public OVRAnchor Anchor { get; private set; }

    /// <summary>
    /// Indicates whether this anchor is tracked by the system.
    /// </summary>
    public bool IsTracked { get; internal set; }

    private static readonly Quaternion RotateY180 = Quaternion.Euler(0, 180, 0);
    private OVRPlugin.Posef? _pose = null;
    private bool _isLocatable = false;

    private readonly List<OVRPlugin.SpaceComponentType> _supportedComponents =
        new List<OVRPlugin.SpaceComponentType>();

    private bool IsComponentSupported(OVRPlugin.SpaceComponentType spaceComponentType)
    {
        // late initialization and caching as this doesn't
        // change during the lifetime of a scene anchor
        if (_supportedComponents.Count == 0)
        {
            if (!Anchor.GetSupportedComponents(_supportedComponents))
                return false;
        }
        return _supportedComponents.Contains(spaceComponentType);
    }

    internal bool IsComponentEnabled(OVRPlugin.SpaceComponentType spaceComponentType) =>
        IsComponentSupported(spaceComponentType) &&
        OVRPlugin.GetSpaceComponentStatus(Space, spaceComponentType, out var componentEnabled, out _)
        && componentEnabled;

    private void SyncComponent<T>(OVRPlugin.SpaceComponentType spaceComponentType)
        where T : MonoBehaviour, IOVRSceneComponent
    {
        if (!IsComponentEnabled(spaceComponentType)) return;

        var component = GetComponent<T>();
        if (component)
        {
            // If the component already exists, then it means it was added before this component was valid, so we need
            // to initialize it.
            component.Initialize();
        }
        else
        {
            gameObject.AddComponent<T>();
        }
    }

    internal void ClearPoseCache()
    {
        _pose = null;
    }

    public void Initialize(OVRAnchor anchor)
    {
        var space = (OVRSpace)anchor.Handle;
        var uuid = anchor.Uuid;

        if (Space.Valid)
            throw new InvalidOperationException($"[{uuid}] {nameof(OVRSceneAnchor)} has already been initialized.");

        if (!space.Valid)
            throw new ArgumentException($"[{uuid}] {nameof(space)} must be valid.", nameof(space));

        Space = space;
        Uuid = uuid;
        Anchor = anchor;

        ClearPoseCache();

        SceneAnchors[this.Uuid] = this;
        SceneAnchorsList.Add(this);

        AnchorReferenceCountDictionary.TryGetValue(Space, out var referenceCount);
        AnchorReferenceCountDictionary[Space] = referenceCount + 1;

        // certain components are not locatable, such as room.
        _isLocatable = IsComponentSupported(OVRPlugin.SpaceComponentType.Locatable);

        // Generally, we want to set the transform as soon as possible, but there is a valid use case where we want to
        // disable this component as soon as its added to override the transform.
        if (enabled)
        {
            if (!_isLocatable)
            {
                OVRSceneManager.Development.Log(nameof(OVRSceneAnchor),
                    $"[{uuid}] Skiping transform set, as the entity is not locatable.",
                    gameObject);
            }
            else if (TryUpdateTransform(useCache: false))
            {
                OVRSceneManager.Development.Log(nameof(OVRSceneAnchor),
                    $"[{uuid}] Initial transform set.", gameObject);
            }
            else
            {
                OVRSceneManager.Development.LogWarning(nameof(OVRSceneAnchor),
                    $"[{uuid}] {nameof(OVRPlugin.TryLocateSpace)} failed. The entity may have the wrong initial transform.",
                    gameObject);
            }
        }

        SyncComponent<OVRSemanticClassification>(OVRPlugin.SpaceComponentType.SemanticLabels);
        SyncComponent<OVRSceneVolume>(OVRPlugin.SpaceComponentType.Bounded3D);
        SyncComponent<OVRScenePlane>(OVRPlugin.SpaceComponentType.Bounded2D);
    }

    /// <summary>
    /// Initializes this scene anchor from an existing scene anchor.
    /// </summary>
    /// <param name="other">An existing <see cref="OVRSceneAnchor"/> from which to initialize this scene anchor.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="other"/> is `null`.</exception>
    /// <exception cref="InvalidOperationException">Thrown if this <see cref="OVRSceneAnchor"/> is already associated with a scene anchor.</exception>
    public void InitializeFrom(OVRSceneAnchor other)
    {
        if (other == null)
            throw new ArgumentNullException(nameof(other));

        Initialize(other.Anchor);
    }

    /// <summary>
    /// Get the list of all scene anchors that the <see cref="OVRSceneManager"/> created.
    /// </summary>
    /// <remarks>
    /// Anchor components such as <see cref="OVRSceneRoom"/>, <see cref="OVRScenePlane"/>,
    /// and <see cref="OVRSceneVolume"/> all have a <see cref="OVRSceneAnchor"/>
    /// component, so this method should be used to find those sibling components.
    /// </remarks>
    /// <param name="anchors">A list of <see cref="OVRSceneAnchor"/>s to
    /// populate. Any existing elements in the list will be removed.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="anchors"/> is `null`.</exception>
    public static void GetSceneAnchors(List<OVRSceneAnchor> anchors)
    {
        if (anchors == null)
            throw new ArgumentNullException(nameof(anchors));

        anchors.Clear();
        anchors.AddRange(SceneAnchorsList);
    }

    /// <summary>
    /// Get the list of all scene anchors filtered by type.
    /// </summary>
    /// <remarks>
    /// Anchor components such as <see cref="OVRSceneRoom"/>, <see cref="OVRScenePlane"/>, and
    /// <see cref="OVRSceneVolume"/> all have a <see cref="OVRSceneAnchor"/> component, so this method should be used to
    /// find those sibling components.
    ///
    /// This method traverses all <see cref="OVRSceneAnchor"/> instantiated by the <see cref="OVRSceneManager"/>
    /// and filters them based on whether they have the given component.
    /// </remarks>
    /// <typeparam name="T">The type of component the <see cref="OVRSceneAnchor"/> must have.</typeparam>
    /// <param name="anchors">A list of components of type <typeparamref name="T"/> to populate. Any existing elements
    /// in the list will be removed.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="anchors"/> is `null`.</exception>
    public static void GetSceneAnchorsOfType<T>(List<T> anchors) where T : Object
    {
        if (anchors == null)
            throw new ArgumentNullException(nameof(anchors));

        anchors.Clear();
        foreach (var anchor in SceneAnchorsList)
        {
            if (anchor.TryGetComponent<T>(out var component))
            {
                anchors.Add(component);
            }
        }
    }

    internal bool TryUpdateTransform(bool useCache)
    {
        if (!Space.Valid || !enabled || !_isLocatable)
            return false;

        if (!useCache || _pose == null)
        {
            if (!(IsTracked = OVRPlugin.TryLocateSpace(Space, OVRPlugin.GetTrackingOriginType(), out var pose,
                                  out var locationFlags) &&
                              locationFlags.IsOrientationValid() &&
                              locationFlags.IsPositionValid()))
            {
                return false;
            }

            _pose = pose;
        }

        // NOTE: This transformation performs the following steps:
        // 1. Flip Z to convert from OpenXR's right-handed to Unity's left-handed coordinate system.
        //    OpenXR             Unity
        //       | y          y |  / z
        //       |              | /
        //       +----> x       +----> x
        //      /
        //    z/ (normal)
        //
        // 2. (1) means that Z now points in the opposite direction from OpenXR. However, the design is such that a
        //    plane's normal should coincide with +Z, so we rotate 180 degrees around the +Y axis to make Z now point
        //    in the intended direction.
        //    OpenXR           Unity
        //       | y           y |
        //       |               |
        //       +---->  x  <----+
        //      /               /
        //    z/             z / (normal)
        //
        // 3. Convert from tracking space to world space.
        var worldSpacePose = new OVRPose
        {
            position = _pose.Value.Position.FromFlippedZVector3f(),
            orientation = _pose.Value.Orientation.FromFlippedZQuatf() * RotateY180
        }.ToWorldSpacePose(Camera.main);
        transform.SetPositionAndRotation(worldSpacePose.position, worldSpacePose.orientation);
        return true;
    }

    private void OnDestroy()
    {
        SceneAnchors.Remove(this.Uuid);
        SceneAnchorsList.Remove(this);

        if (!Space.Valid)
        {
            return;
        }

        if (!AnchorReferenceCountDictionary.TryGetValue(Space, out var referenceCount))
        {
            OVRSceneManager.Development.LogError(nameof(OVRSceneAnchor),
                $"[Anchor {Space.Handle}] has not been found, can't find it for deletion",
                gameObject);
            return;
        }

        if (referenceCount == 1)
        {
            // last reference to this anchor, delete it
            if (Space.Valid)
            {
                OVRPlugin.DestroySpace(Space);
            }

            // remove instead of decrement to not waste memory
            AnchorReferenceCountDictionary.Remove(Space);
        }
        else
        {
            AnchorReferenceCountDictionary[Space] = referenceCount - 1;
        }
    }

    private static readonly Dictionary<OVRSpace, int> AnchorReferenceCountDictionary =
        new Dictionary<OVRSpace, int>();

    internal static readonly Dictionary<Guid, OVRSceneAnchor> SceneAnchors = new Dictionary<Guid, OVRSceneAnchor>();
    internal static readonly List<OVRSceneAnchor> SceneAnchorsList = new List<OVRSceneAnchor>();
}

internal interface IOVRSceneComponent
{
    void Initialize();
}
