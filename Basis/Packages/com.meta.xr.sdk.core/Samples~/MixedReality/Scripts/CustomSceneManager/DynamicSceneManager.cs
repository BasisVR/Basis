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
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using DynamicSceneManagerHelper;
using Meta.XR.Samples;

/// <summary>
/// This sample expands on the snapshot scene manager and adds the ability
/// to update Unity game objects as the data changes through the snapshots.
/// </summary>
/// <remarks>
/// As per the anchor change rules, the UUID of a room changes when a child
/// item is added or removed, so we match pairs of anchors that have similar
/// child anchors between snapshots. We also check the geometric bounds of
/// anchors across snapshots.
///
/// In order to achieve this, we save additional info in scene snapshots,
/// and also provide a way to update existing Unity objects when
/// their geometry changes.
///
/// This sample should serve as a guide only, as it relies on many heap
/// allocations and inefficient search queries to help readability.
/// </remarks>
[MetaCodeSample("CoreSDK-MixedReality")]
public class DynamicSceneManager : MonoBehaviour
{
    public float UpdateFrequencySeconds = 5;
    [SerializeField] private Transform _trackingSpace;
    [SerializeField] private Material _material;

    SceneSnapshot _snapshot = new SceneSnapshot();
    Dictionary<OVRAnchor, GameObject> _sceneGameObjects = new Dictionary<OVRAnchor, GameObject>();

    Task _updateSceneTask;

    void Start()
    {
        StartCoroutine(UpdateScenePeriodically());
    }

    void Update()
    {
        if (OVRInput.GetDown(OVRInput.RawButton.A))
            _ = OVRScene.RequestSpaceSetup();
    }

    IEnumerator UpdateScenePeriodically()
    {
        while (true)
        {
            yield return new WaitForSeconds(UpdateFrequencySeconds);

            _updateSceneTask = UpdateScene();
            yield return new WaitUntil(() => _updateSceneTask.IsCompleted);
        }
    }

    async Task UpdateScene()
    {
        // get current snapshot and compare to previous
        var currentSnapshot = await LoadSceneSnapshotAsync();
        var differences = new SnapshotComparer(
            _snapshot, currentSnapshot).Compare();

        // update unity objects from the differences
        await UpdateUnityObjects(differences, currentSnapshot);

        // update previous snapshot
        _snapshot = currentSnapshot;
    }

    async Task<SceneSnapshot> LoadSceneSnapshotAsync()
    {
        // create snapshot from all rooms and their anchors
        // saving some of the anchor data to detect changes
        var snapshot = new SceneSnapshot();

        var rooms = new List<OVRAnchor>();
        await OVRAnchor.FetchAnchorsAsync(rooms, new OVRAnchor.FetchOptions { SingleComponentType = typeof(OVRRoomLayout) });
        foreach (var room in rooms)
        {
            if (!room.TryGetComponent(out OVRAnchorContainer container))
                continue;

            var children = new List<OVRAnchor>();
            await container.FetchAnchorsAsync(children);
            snapshot.Anchors.Add(room, new SceneSnapshot.Data { Children = children });

            foreach (var child in children)
            {
                var data = new SceneSnapshot.Data();
                if (child.TryGetComponent(out OVRBounded2D b2d) && b2d.IsEnabled)
                    data.Rect = b2d.BoundingBox;
                if (child.TryGetComponent(out OVRBounded3D b3d) && b3d.IsEnabled)
                    data.Bounds = b3d.BoundingBox;
                snapshot.Anchors.Add(child, data);
            }
        }

        return snapshot;
    }

    async Task UpdateUnityObjects(List<(OVRAnchor, SnapshotComparer.ChangeType)> changes,
        SceneSnapshot newSnapshot)
    {
        if (!changes.Any())
            return;

        var updater = new UnityObjectUpdater();

        var changesNew = FilterChanges(changes, SnapshotComparer.ChangeType.New);
        var changesMissing = FilterChanges(changes, SnapshotComparer.ChangeType.Missing);
        var changesId = FilterChanges(changes, SnapshotComparer.ChangeType.ChangedId);
        var changesBounds = FilterChanges(changes, SnapshotComparer.ChangeType.ChangedBounds);

        // create a new game object for all new changes
        foreach (var anchor in changesNew)
        {
            _sceneGameObjects.TryGetValue(GetParentAnchor(anchor, newSnapshot), out var parent);
            _sceneGameObjects.Add(anchor, await updater.CreateUnityObject(anchor, parent, _trackingSpace, _material));
        }

        // destroy game objects for all missing anchors
        foreach (var anchor in changesMissing)
        {
            Destroy(_sceneGameObjects[anchor]);
            _sceneGameObjects.Remove(anchor);
        }

        // ChangedId means we need to find the pairs between the snapshots
        foreach (var (currentAnchor, newAnchor) in FindAnchorPairs(changesId, newSnapshot))
        {
            // we only need to update the reference in our scene game objects
            _sceneGameObjects.Add(newAnchor, _sceneGameObjects[currentAnchor]);
            _sceneGameObjects.Remove(currentAnchor);
        }

        // geometry bounds means just updating an existing game object
        foreach (var currentAnchor in changesBounds)
            updater.UpdateUnityObject(currentAnchor, _sceneGameObjects[currentAnchor], _trackingSpace, _material);
    }

    List<OVRAnchor> FilterChanges(List<(OVRAnchor, SnapshotComparer.ChangeType)> changes,
        SnapshotComparer.ChangeType changeType) =>
            changes.Where(tuple => tuple.Item2 == changeType).Select(tuple => tuple.Item1).ToList();

    List<(OVRAnchor, OVRAnchor)> FindAnchorPairs(List<OVRAnchor> allAnchors, SceneSnapshot newSnapshot)
    {
        var currentAnchors = allAnchors.Where(_snapshot.Contains);
        var newAnchors = allAnchors.Where(newSnapshot.Contains);

        var pairs = new List<(OVRAnchor, OVRAnchor)>();
        foreach (var currentAnchor in currentAnchors)
        {
            foreach (var newAnchor in newAnchors)
            {
                if (AreAnchorsEqual(_snapshot.Anchors[currentAnchor], newSnapshot.Anchors[newAnchor]))
                {
                    pairs.Add((currentAnchor, newAnchor));
                    break;
                }
            }
        }
        return pairs;
    }

    bool AreAnchorsEqual(SceneSnapshot.Data anchor1Data, SceneSnapshot.Data anchor2Data)
    {
        // the only equal anchors with different UUIDs are when they are rooms.
        // so we will check if any of their child elements are the same
        if (anchor1Data.Children == null || anchor2Data.Children == null)
            return false;

        return anchor1Data.Children.Any(anchor2Data.Children.Contains) ||
            anchor2Data.Children.Any(anchor1Data.Children.Contains);
    }

    OVRAnchor GetParentAnchor(OVRAnchor childAnchor, SceneSnapshot snapshot)
    {
        foreach (var kvp in snapshot.Anchors)
            if (kvp.Value.Children?.Contains(childAnchor) == true)
                return kvp.Key;
        return OVRAnchor.Null;
    }
}
