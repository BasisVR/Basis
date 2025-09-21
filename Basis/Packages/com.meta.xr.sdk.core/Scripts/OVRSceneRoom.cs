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
using System.Diagnostics;
using Meta.XR.Util;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Represents a <see cref="OVRRoomLayout"/> type Scene anchor.
/// </summary>
/// <remarks>
/// This component provides properties for accessing information on the Walls, Floors, and Ceiling.
///
/// <see cref="OVRSceneManager"/> and associated classes are deprecated (v65), please use [MR Utility Kit](https://developer.oculus.com/documentation/unity/unity-mr-utility-kit-overview)" instead.
/// </remarks>
[DisallowMultipleComponent]
[RequireComponent(typeof(OVRSceneAnchor))]
[HelpURL("https://developer.oculus.com/documentation/unity/unity-scene-use-scene-anchors/#further-scene-model-unity-components")]
[Obsolete(OVRSceneManager.DeprecationMessage)]
[Feature(Feature.Scene)]
public class OVRSceneRoom : MonoBehaviour, IOVRSceneComponent
{
    /// <summary>
    /// The <see cref="OVRScenePlane"/> representing the floor of the room.
    /// </summary>
    /// <remarks>
    /// A room contains only a single Floor.
    /// </remarks>
    public OVRScenePlane Floor { get; private set; }

    /// <summary>
    /// The <see cref="OVRScenePlane"/> representing the ceiling of the room.
    /// </summary>
    /// <remarks>
    /// A room contains only a single Ceiling.
    /// </remarks>
    public OVRScenePlane Ceiling { get; private set; }

    /// <summary>
    /// The set of <see cref="OVRScenePlane"/> representing the walls of the room.
    /// </summary>
    /// <remarks>
    /// A room may contain a single Wall, but typically there is more than one.
    /// </remarks>
    public OVRScenePlane[] Walls { get; private set; } = Array.Empty<OVRScenePlane>();


    private OVRSceneAnchor _sceneAnchor;

    private OVRSceneManager _sceneManager;

    private Guid _uuid;

    internal static readonly Dictionary<Guid, OVRSceneRoom> SceneRooms = new();

    internal static readonly List<OVRSceneRoom> SceneRoomsList = new();

    private void Awake()
    {
        _sceneAnchor = GetComponent<OVRSceneAnchor>();
        _sceneManager = FindAnyObjectByType<OVRSceneManager>();
        _uuid = _sceneAnchor.Uuid;
        if (_sceneAnchor.Space.Valid)
        {
            ((IOVRSceneComponent)this).Initialize();
        }
    }

    void IOVRSceneComponent.Initialize()
    {
        SceneRooms[_uuid] = this;
        SceneRoomsList.Add(this);
    }

    internal async OVRTask<bool> LoadRoom(Guid floor, Guid ceiling, Guid[] walls)
    {
        using (new OVRObjectPool.HashSetScope<Guid>(out var uuids))
        using (new OVRObjectPool.ListScope<OVRAnchor>(out var anchors))
        {
            uuids.Add(floor);
            uuids.Add(ceiling);
            foreach (var wall in walls)
            {
                uuids.Add(wall);
            }

            if (_sceneAnchor.Anchor.TryGetComponent<OVRAnchorContainer>(out var container))
            {
                foreach (var uuid in container.Uuids)
                {
                    uuids.Add(uuid);
                }
            }
            else
            {
                LogWarning($"{name} has no anchor container. Some elements may be missing.");
            }

            var result = await OVRSceneManager.FetchAnchorsAsync(uuids, anchors);
            if (!result)
            {
                LogError($"Failed to fetch the {uuids.Count} anchors belonging to {name}");
                return false;
            }

            Log($"{name} has {anchors.Count} anchors.");

            using (new OVRObjectPool.ListScope<bool>(out var results))
            using (new OVRObjectPool.ListScope<OVRTask<bool>>(out var tasks))
            {
                foreach (var anchor in anchors)
                {
                    if (anchor.TryGetComponent<OVRLocatable>(out var locatable))
                    {
                        tasks.Add(locatable.SetEnabledAsync(true));
                    }
                }

                await OVRTask.WhenAll(tasks, results);
            }

            foreach (var anchor in anchors)
            {
                if (!anchor.TryGetComponent<OVRLocatable>(out var locatable) || !locatable.IsEnabled)
                {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                    var labels = anchor.TryGetComponent<OVRSemanticLabels>(out var component)
                                 && component.IsEnabled ? component.Labels : null;
                    LogWarning($"Anchor {anchor}{(string.IsNullOrEmpty(labels) ? "" : $" ({labels})")} could not be localized. Ignoring.");
#endif
                    continue;
                }

                OVRPlugin.GetSpaceComponentStatus(anchor.Handle, OVRPlugin.SpaceComponentType.Bounded2D,
                    out var bounded2dEnabled, out _);
                OVRPlugin.GetSpaceComponentStatus(anchor.Handle, OVRPlugin.SpaceComponentType.Bounded3D,
                    out var bounded3dEnabled, out _);
                OVRPlugin.GetSpaceComponentStatus(anchor.Handle, OVRPlugin.SpaceComponentType.TriangleMesh,
                    out var triangleMeshEnabled, out _);

                var isStrictly2d = bounded2dEnabled && !(bounded3dEnabled || triangleMeshEnabled);

                // The plane prefab is for anchors that are only 2D, i.e. they only have
                // a 2D component. If a volume component exists, we use a volume prefab,
                // else we pass null (prefab overrides may be used)
                var prefab = isStrictly2d
                    ? _sceneManager.PlanePrefab
                    : bounded3dEnabled ? _sceneManager.VolumePrefab : null;

                var sceneAnchor = _sceneManager.InstantiateSceneAnchor(anchor, prefab);
                if (sceneAnchor)
                {
                    sceneAnchor.transform.parent = transform;
                    sceneAnchor.IsTracked = true;
                }
            }

            bool TryGetPlane(Guid uuid, out OVRScenePlane plane)
            {
                plane = null;
                return OVRSceneAnchor.SceneAnchors.TryGetValue(uuid, out var sceneAnchor) &&
                       sceneAnchor.TryGetComponent(out plane);
            }

            OVRScenePlane GetPlane(Guid uuid) => TryGetPlane(uuid, out var plane) ? plane : null;

            Floor = GetPlane(floor);
            Ceiling = GetPlane(ceiling);

            using (new OVRObjectPool.ListScope<OVRScenePlane>(out var planes))
            {
                foreach (var wall in walls)
                {
                    if (TryGetPlane(wall, out var plane))
                    {
                        planes.Add(plane);
                    }
                }

                Walls = planes.ToArray();
            }
        }

        return true;
    }

    private void OnDestroy()
    {
        SceneRooms.Remove(_uuid);
        SceneRoomsList.Remove(this);
    }

    [Conditional("DEVELOPMENT_BUILD"), Conditional("UNITY_EDITOR")]
    private void Log(string message) => Debug.Log($"[{nameof(OVRSceneRoom)}] {message}", gameObject);

    [Conditional("DEVELOPMENT_BUILD"), Conditional("UNITY_EDITOR")]
    private void LogWarning(string message) => Debug.LogWarning($"[{nameof(OVRSceneRoom)}] {message}", gameObject);

    [Conditional("DEVELOPMENT_BUILD"), Conditional("UNITY_EDITOR")]
    private void LogError(string message) => Debug.LogError($"[{nameof(OVRSceneRoom)}] {message}", gameObject);
}
