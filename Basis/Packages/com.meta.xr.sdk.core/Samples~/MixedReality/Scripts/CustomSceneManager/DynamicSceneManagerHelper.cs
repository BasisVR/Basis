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

// This namespace contains helper classes for the DynamicSceneManager.
namespace DynamicSceneManagerHelper
{
    /// <summary>
    /// A class that holds a list of anchors, along with data that
    /// can be used to identify when a change has occured.
    /// </summary>
    [MetaCodeSample("CoreSDK-MixedReality")]
    class SceneSnapshot
    {
        public class Data
        {
            public List<OVRAnchor> Children;
            public Rect? Rect;
            public Bounds? Bounds;
        }
        public Dictionary<OVRAnchor, Data> Anchors { get; } = new Dictionary<OVRAnchor, Data>();
        public bool Contains(OVRAnchor anchor) => Anchors.ContainsKey(anchor);
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
            ChangedId,
            ChangedBounds
        }

        public List<(OVRAnchor, ChangeType)> Compare()
        {
            var changes = new List<(OVRAnchor, ChangeType)>();

            // if in base but not in new, then missing
            // if in new but not in base, then new
            foreach (var anchor1 in BaseSnapshot.Anchors.Keys)
                if (!NewSnapshot.Contains(anchor1))
                    changes.Add((anchor1, ChangeType.Missing));
            foreach (var anchor2 in NewSnapshot.Anchors.Keys)
                if (!BaseSnapshot.Contains(anchor2))
                    changes.Add((anchor2, ChangeType.New));

            // further checks
            CheckRoomChanges(changes);
            CheckBoundsChanges(changes);

            return changes;
        }

        void CheckRoomChanges(List<(OVRAnchor, ChangeType)> changes)
        {
            // a room with new/deleted/edited child anchors is considered
            // a NEW anchor, so we will check if any child elements are
            // the same and mark both old and new room anchors as CHANGED
            for (var i = 0; i < changes.Count; i++)
            {
                var (anchor, change) = changes[i];

                var isRoom = anchor.TryGetComponent(out OVRRoomLayout room) && room.IsEnabled;
                if (!isRoom || change == ChangeType.ChangedId)
                    continue;

                var isNewAnchor = NewSnapshot.Contains(anchor);
                var isOldAnchor = BaseSnapshot.Contains(anchor);
                if (!isNewAnchor && !isOldAnchor)
                    continue;

                var children = isNewAnchor ?
                    NewSnapshot.Anchors[anchor].Children :
                    BaseSnapshot.Anchors[anchor].Children;
                var comparisonSnapshot = change == ChangeType.New ?
                    BaseSnapshot : NewSnapshot;

                foreach (var child in children)
                    if (comparisonSnapshot.Contains(child))
                        changes[i] = (anchor, ChangeType.ChangedId);
            }
        }

        void CheckBoundsChanges(List<(OVRAnchor, ChangeType)> changes)
        {
            // first match pairs of base and new snapshots
            foreach (var baseAnchor in BaseSnapshot.Anchors.Keys)
            {
                var newAnchor = NewSnapshot.Anchors.Keys.FirstOrDefault(
                    newAnchor => newAnchor.Uuid == baseAnchor.Uuid);

                // we have a pair, now compare bounds data
                if (newAnchor.Uuid == baseAnchor.Uuid)
                {
                    var baseData = BaseSnapshot.Anchors[baseAnchor];
                    var newData = NewSnapshot.Anchors[newAnchor];

                    var changed2DBounds = Has2DBounds(baseData, newData) && Are2DBoundsDifferent(baseData, newData);
                    var changed3DBounds = Has3DBounds(baseData, newData) && Are3DBoundsDifferent(baseData, newData);

                    if (changed2DBounds || changed3DBounds)
                        changes.Add((baseAnchor, ChangeType.ChangedBounds));
                }
            }
        }

        bool Has2DBounds(SceneSnapshot.Data data1, SceneSnapshot.Data data2)
            => data1.Rect.HasValue && data2.Rect.HasValue;
        bool Are2DBoundsDifferent(SceneSnapshot.Data data1, SceneSnapshot.Data data2)
            => data1.Rect?.min != data2.Rect?.min || data1.Rect?.max != data2.Rect?.max;
        bool Has3DBounds(SceneSnapshot.Data data1, SceneSnapshot.Data data2)
            => data1.Bounds.HasValue && data2.Bounds.HasValue;
        bool Are3DBoundsDifferent(SceneSnapshot.Data data1, SceneSnapshot.Data data2)
            => data1.Bounds?.min != data2.Bounds?.min || data1.Bounds?.max != data2.Bounds?.max;
    }

    /// <summary>
    /// This class wraps the logic for interacting with Unity
    /// game objects.
    /// </summary>
    class UnityObjectUpdater
    {
        public async Task<GameObject> CreateUnityObject(OVRAnchor anchor, GameObject parent, Transform trackingSpace, Material material)
        {
            // if this is a room, we only need to make a GameObject
            if (anchor.TryGetComponent(out OVRRoomLayout _))
                return new GameObject($"Room-{anchor.Uuid}");

            // only interested in the anchors which are locatable
            if (!anchor.TryGetComponent(out OVRLocatable locatable))
                return null;
            await locatable.SetEnabledAsync(true);

            // get semantic classification for object name
            var classifications = new HashSet<OVRSemanticLabels.Classification>
            {
                OVRSemanticLabels.Classification.Other
            };
            if (anchor.TryGetComponent(out OVRSemanticLabels labels))
                labels.GetClassifications(classifications);

            // create and parent Unity game object if possible
            var gameObject = new GameObject(string.Join(',', classifications));
            if (parent != null)
                gameObject.transform.SetParent(parent.transform);

            // set location and create objects for 2D, 3D, triangle mesh
            var helper = new SceneManagerHelper(gameObject, trackingSpace, material);
            helper.SetLocation(locatable);

            if (anchor.TryGetComponent(out OVRBounded2D b2d) && b2d.IsEnabled)
                helper.CreatePlane(b2d);
            if (anchor.TryGetComponent(out OVRBounded3D b3d) && b3d.IsEnabled)
                helper.CreateVolume(b3d);
            if (anchor.TryGetComponent(out OVRTriangleMesh mesh) && mesh.IsEnabled)
                helper.CreateMesh(mesh);
            return gameObject;
        }

        public void UpdateUnityObject(OVRAnchor anchor, GameObject gameObject, Transform trackingSpace, Material material)
        {
            var helper = new SceneManagerHelper(gameObject, trackingSpace, material);
            if (anchor.TryGetComponent(out OVRLocatable locatable))
                helper.SetLocation(locatable);
            if (anchor.TryGetComponent(out OVRBounded2D b2d) && b2d.IsEnabled)
                helper.UpdatePlane(b2d);
            if (anchor.TryGetComponent(out OVRBounded3D b3d) && b3d.IsEnabled)
                helper.UpdateVolume(b3d);
            if (anchor.TryGetComponent(out OVRTriangleMesh mesh) && mesh.IsEnabled)
                helper.UpdateMesh(mesh);
        }
    }
}
