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

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Meta.XR.Samples;
using UnityEngine;

/// <summary>
/// This sample shows you how to implement a scene manager with the following features:
///   * Fetch all room scene anchors
///   * Fetch all child scene anchors of a room
///   * Set the location, and name of the object as label
///   * Spawn primitive geometry to match the scene anchor's plane, volume or mesh data
///
/// There is a fallback for running scene capture if no rooms were found.
/// </summary>
[MetaCodeSample("CoreSDK-MixedReality")]
public class BasicSceneManager : MonoBehaviour
{
    [SerializeField] private Transform _trackingSpace;
    [SerializeField] private Material _material;

    void Start()
    {
        LoadSceneAsync();
    }

    async void LoadSceneAsync()
    {
        // fetch all rooms, with a SceneCapture fallback
        var rooms = new List<OVRAnchor>();
        await OVRAnchor.FetchAnchorsAsync(rooms, new OVRAnchor.FetchOptions
        {
            SingleComponentType = typeof(OVRRoomLayout),
        });
        if (rooms.Count == 0)
        {
            var sceneCaptured = await OVRScene.RequestSpaceSetup();
            if (!sceneCaptured)
                return;

            await OVRAnchor.FetchAnchorsAsync(rooms, new OVRAnchor.FetchOptions
            {
                SingleComponentType = typeof(OVRRoomLayout),
            });
        }

        // fetch room elements, create objects for them
        var tasks = rooms.Select(async room =>
        {
            var roomObject = new GameObject($"Room-{room.Uuid}");
            if (!room.TryGetComponent(out OVRAnchorContainer container))
                return;

            var children = new List<OVRAnchor>();
            await container.FetchAnchorsAsync(children);
            await CreateSceneAnchors(roomObject, children);
        }).ToList();
        await Task.WhenAll(tasks);

    }

    async Task CreateSceneAnchors(GameObject roomGameObject, List<OVRAnchor> anchors)
    {
        // we create tasks to iterate all anchors in parallel
        var tasks = anchors.Select(async anchor =>
        {
            // can we locate it in the world?
            if (!anchor.TryGetComponent(out OVRLocatable locatable))
                return;
            await locatable.SetEnabledAsync(true);

            // get semantic classification for object name
            var classifications = new HashSet<OVRSemanticLabels.Classification>
            {
                OVRSemanticLabels.Classification.Other
            };
            if (anchor.TryGetComponent(out OVRSemanticLabels labels))
                labels.GetClassifications(classifications);


            // create and parent Unity game object
            var gameObject = new GameObject(string.Join(',', classifications));
            gameObject.transform.SetParent(roomGameObject.transform);

            // set location and create objects for 2D, 3D, triangle mesh
            var helper = new SceneManagerHelper(gameObject, _trackingSpace, _material);
            helper.SetLocation(locatable);

            if (anchor.TryGetComponent(out OVRBounded2D b2d) && b2d.IsEnabled)
                helper.CreatePlane(b2d);
            if (anchor.TryGetComponent(out OVRBounded3D b3d) && b3d.IsEnabled)
                helper.CreateVolume(b3d);
            if (anchor.TryGetComponent(out OVRTriangleMesh mesh) && mesh.IsEnabled)
                helper.CreateMesh(mesh);
        }).ToList();

        await Task.WhenAll(tasks);
    }
}
