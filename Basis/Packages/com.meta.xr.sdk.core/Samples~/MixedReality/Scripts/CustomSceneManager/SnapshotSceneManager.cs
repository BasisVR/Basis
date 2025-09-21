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
using System.Text;
using System.Threading.Tasks;
using Meta.XR.Samples;
using UnityEngine;

/// <summary>
/// This sample shows you how to listen to changes in the underlying scene.
///
/// At a given update frequency, we query all anchors in the scene and create
/// a snapshot. This is compared to a previous snapshot, and the
/// differences are logged to the console.
/// </summary>
/// <remarks>
/// In order to create new scene items, you need to run Scene Capture, so
/// this sample will only work on-device (as PC Link does not support it).
///
/// The logic involves checking whether anchors exist in the base snapshot
/// and new snapshot. Furthermore, this sample contains the logic for
/// rooms to be compared, as the room anchor is NEW whenever a child element
/// is added or removed.
/// </remarks>
[MetaCodeSample("CoreSDK-MixedReality")]
public class SnapshotSceneManager : MonoBehaviour
{
    public float UpdateFrequencySeconds = 5;

    SceneSnapshot _snapshot = new SceneSnapshot();

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
            UpdateScene();
        }
    }

    async void UpdateScene()
    {
        // get current snapshot and compare to previous
        var currentSnapshot = await LoadSceneSnapshotAsync();
        var differences = await new SnapshotComparer(
            _snapshot, currentSnapshot).Compare();

        // inform user of changes
        var sb = new StringBuilder();
        if (differences.Count > 0)
        {
            sb.AppendLine("---- SCENE SNAPSHOT ----");
            foreach (var (anchor, change) in differences)
                sb.AppendLine($"{change}: {AnchorInfo(anchor)}");
            Debug.Log(sb.ToString());
        }

        // update previous snapshot
        _snapshot = currentSnapshot;
    }

    async Task<SceneSnapshot> LoadSceneSnapshotAsync()
    {
        // create snapshot from all rooms and their anchors
        var snapshot = new SceneSnapshot();

        var rooms = new List<OVRAnchor>();
        await OVRAnchor.FetchAnchorsAsync(rooms, new OVRAnchor.FetchOptions { SingleComponentType = typeof(OVRRoomLayout) });
        foreach (var room in rooms)
        {
            if (!room.TryGetComponent(out OVRAnchorContainer container))
                continue;

            var children = new List<OVRAnchor>();
            await container.FetchAnchorsAsync(children);
            snapshot.Anchors.Add(room);
            snapshot.Anchors.AddRange(children);
        }

        return snapshot;
    }

    string AnchorInfo(OVRAnchor anchor)
    {
        if (anchor.TryGetComponent(out OVRRoomLayout room) && room.IsEnabled)
            return $"{anchor.Uuid} - ROOM";
        if (anchor.TryGetComponent(out OVRSemanticLabels labels) && labels.IsEnabled)
        {
            var classifications = new HashSet<OVRSemanticLabels.Classification>
            {
                OVRSemanticLabels.Classification.Other
            };
            labels.GetClassifications(classifications);
            return $"{anchor.Uuid} - {string.Join(',', classifications)}";
        }

        return $"{anchor.Uuid}";
    }

    /// <summary>
    /// A basic container class that holds a list of anchors.
    /// This class could be extended to optimize the comparison
    /// between snapshots (such as keeping the room-child
    /// relationship, or storing the location and/or bounds)
    /// </summary>
    class SceneSnapshot
    {
        public List<OVRAnchor> Anchors { get; } = new List<OVRAnchor>();
    }

    /// <summary>
    /// This class contains the custom logic for scene snapshot comparison.
    /// </summary>
    class SnapshotComparer
    {
        public SceneSnapshot BaseSnapshot { get; }
        public SceneSnapshot NewSnapshot { get; }

        public SnapshotComparer(SceneSnapshot baseSnapshot, SceneSnapshot newSnapshot)
        {
            BaseSnapshot = baseSnapshot;
            NewSnapshot = newSnapshot;
        }

        public enum ChangeType
        {
            New,
            Missing,
            Changed
        }

        public async Task<List<(OVRAnchor, ChangeType)>> Compare()
        {
            var changes = new List<(OVRAnchor, ChangeType)>();

            // if in base but not in new, then missing
            // if in new but not in base, then new
            foreach (var anchor1 in BaseSnapshot.Anchors)
                if (!NewSnapshot.Anchors.Contains(anchor1))
                    changes.Add((anchor1, ChangeType.Missing));
            foreach (var anchor2 in NewSnapshot.Anchors)
                if (!BaseSnapshot.Anchors.Contains(anchor2))
                    changes.Add((anchor2, ChangeType.New));

            // further custom checks
            await CheckRoomChanges(changes);

            return changes;
        }

        async Task CheckRoomChanges(List<(OVRAnchor, ChangeType)> changes)
        {
            // a room with new/deleted/edited child anchors is considered
            // a NEW anchor, so we will check if any child elements are
            // the same and mark both old and new room anchors as CHANGED
            for (var i = 0; i < changes.Count; i++)
            {
                var (anchor, change) = changes[i];

                var isRoom = anchor.TryGetComponent(out OVRRoomLayout room) &&
                    room.IsEnabled;
                if (!isRoom || change == ChangeType.Changed)
                    continue;

                var childAnchors = new List<OVRAnchor>();
                if (!(await room.FetchAnchorsAsync(childAnchors)).Success)
                    continue;

                var comparisonAnchors = change == ChangeType.New ?
                    BaseSnapshot.Anchors : NewSnapshot.Anchors;
                foreach (var childAnchor in childAnchors)
                    if (comparisonAnchors.Contains(childAnchor))
                        changes[i] = (anchor, ChangeType.Changed);
            }
        }
    }
}
