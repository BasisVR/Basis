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

using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Meta.XR.Samples;
using UnityEngine;

/// <summary>
/// This sample expands on the basic scene manager and adds the following features:
///   * Spawn a prefab for the wall, ceiling, floor elements and set 2D dimensions
///   * Spawn a fallback object for all other semantic labels and set 3D dimensions
///   * Update the location of all anchors at some frequency with new tracking info
/// </summary>
/// <remarks>
/// The prefabs in this class are scaled to dimensions 1, however, you will need to
/// consider the dimension within your prefab to scale correctly. Additionally, in
/// order to avoid stretching, you may consider 9-slicing.
///
/// Scene anchors are tracked independently and can change as new tracking information
/// becomes available. It may be better for your use case to consider a single
/// tracking point and only update that. This will keep the relative positions of
/// all objects consistent, while also updating the location with new tracking data.
/// </remarks>
[MetaCodeSample("CoreSDK-MixedReality")]
public class PrefabSceneManager : MonoBehaviour
{
    public GameObject WallPrefab;
    public GameObject CeilingPrefab;
    public GameObject FloorPrefab;
    public GameObject FallbackPrefab;
    public float UpdateFrequencySeconds = 5;
    [SerializeField] private Transform _trackingSpace;

    List<(GameObject, OVRLocatable)> _locatableObjects = new List<(GameObject, OVRLocatable)>();

    void Start()
    {
        LoadSceneAsync();
        StartCoroutine(UpdateAnchorsPeriodically());
    }

    async void LoadSceneAsync()
    {
        // fetch all rooms, with a SceneCapture fallback
        var rooms = new List<OVRAnchor>();
        await OVRAnchor.FetchAnchorsAsync(rooms, new OVRAnchor.FetchOptions { SingleComponentType = typeof(OVRRoomLayout) });
        if (rooms.Count == 0)
        {
            var sceneCaptured = await OVRScene.RequestSpaceSetup();
            if (!sceneCaptured)
                return;

            await OVRAnchor.FetchAnchorsAsync(rooms, new OVRAnchor.FetchOptions { SingleComponentType = typeof(OVRRoomLayout) });
        }

        // fetch room elements, create objects for them
        var tasks = rooms.Select(async room =>
        {
            var roomObject = new GameObject($"Room-{room.Uuid}");
            if (!room.TryGetComponent(out OVRAnchorContainer container))
                return;
            if (!room.TryGetComponent(out OVRRoomLayout roomLayout))
                return;

            var children = new List<OVRAnchor>();
            await container.FetchAnchorsAsync(children);
            await CreateSceneAnchors(roomObject, roomLayout, children);
        }).ToList();
        await Task.WhenAll(tasks);
    }

    async Task CreateSceneAnchors(GameObject roomGameObject,
        OVRRoomLayout roomLayout, List<OVRAnchor> anchors)
    {
        roomLayout.TryGetRoomLayout(out var ceilingUuid,
            out var floorUuid, out var wallUuids);

        // iterate over all anchors as async tasks
        var tasks = anchors.Select(async anchor =>
        {
            // can we locate it in the world?
            if (!anchor.TryGetComponent(out OVRLocatable locatable))
                return;
            await locatable.SetEnabledAsync(true);

            // check room layout information and assign prefab
            // it would also be possible to use the semantic label
            var prefab = FallbackPrefab;
            if (anchor.Uuid == floorUuid)
                prefab = FloorPrefab;
            else if (anchor.Uuid == ceilingUuid)
                prefab = CeilingPrefab;
            else if (wallUuids.Contains(anchor.Uuid))
                prefab = WallPrefab;

            // get semantic classification for object name
            var classifications = new HashSet<OVRSemanticLabels.Classification>
            {
                OVRSemanticLabels.Classification.Other
            };
            if (anchor.TryGetComponent(out OVRSemanticLabels labels))
                labels.GetClassifications(classifications);

            // create container object
            var gameObject = new GameObject(string.Join(',', classifications));
            gameObject.transform.SetParent(roomGameObject.transform);
            var helper = new SceneManagerHelper(gameObject, _trackingSpace);
            helper.SetLocation(locatable);

            // instantiate prefab & set 2D dimensions
            var model = Instantiate(prefab, gameObject.transform);
            if (anchor.TryGetComponent(out OVRBounded2D bounds2D) &&
                bounds2D.IsEnabled)
            {
                model.transform.localScale = new Vector3(
                    bounds2D.BoundingBox.size.x,
                    bounds2D.BoundingBox.size.y,
                    0.01f);
            }

            // we will set volume dimensions for the non-room elements
            if (prefab == FallbackPrefab)
            {
                if (anchor.TryGetComponent(out OVRBounded3D bounds3D) &&
                    bounds3D.IsEnabled)
                {
                    model.transform.localPosition = new Vector3(0, 0,
                        -bounds3D.BoundingBox.size.z / 2);
                    model.transform.localScale = bounds3D.BoundingBox.size;
                }
            }

            // save game object and locatable for updating later
            _locatableObjects.Add((gameObject, locatable));
        }).ToList();

        await Task.WhenAll(tasks);
    }

    IEnumerator UpdateAnchorsPeriodically()
    {
        while (true)
        {
            foreach (var (gameObject, locatable) in _locatableObjects)
            {
                var helper = new SceneManagerHelper(gameObject, _trackingSpace);
                helper.SetLocation(locatable);
            }

            yield return new WaitForSeconds(UpdateFrequencySeconds);
        }
    }
}
