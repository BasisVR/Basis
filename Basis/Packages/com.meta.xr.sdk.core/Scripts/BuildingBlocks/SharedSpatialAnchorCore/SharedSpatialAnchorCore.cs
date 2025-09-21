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
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Events;

namespace Meta.XR.BuildingBlocks
{
    /// <summary>
    /// A helper class for creating, loading, erasing, and sharing a <see cref="OVRSpatialAnchor"/> easily.
    /// </summary>
    /// <remarks>
    /// Use <see cref="LoadAndInstantiateAnchors"/> to load an instantiate a list of anchors, and <see cref="OnSharedSpatialAnchorsLoadCompleted"/> to be notified when the loading is completed.
    /// Use <see cref="InstantiateSpatialAnchor"/> to create an instantiate a single anchor based on a prefab.
    /// Use <see cref="ShareSpatialAnchors"/> to share a list of anchors, and <see cref="OnSpatialAnchorsShareCompleted"/> to be notified when the sharing is completed.
    /// See <a href="https://developer.oculus.com/documentation/unity/bb-multiplayer-blocks">Building Blocks</a> for more information.
    /// </remarks>
    public class SharedSpatialAnchorCore : SpatialAnchorCoreBuildingBlock
    {
        /// <summary>
        /// This event will be triggered when the sharing of a list of <see cref="OVRSpatialAnchor"/>(s) is completed.
        /// </summary>
        public UnityEvent<List<OVRSpatialAnchor>, OVRSpatialAnchor.OperationResult> OnSpatialAnchorsShareCompleted
        {
            get => _onSpatialAnchorsShareCompleted;
            set => _onSpatialAnchorsShareCompleted = value;
        }

        public UnityEvent<List<OVRSpatialAnchor>, OVRAnchor.ShareResult> OnSpatialAnchorsShareToGroupCompleted
        {
            get => _onSpatialAnchorsShareToGroupCompleted;
            set => _onSpatialAnchorsShareToGroupCompleted = value;
        }

        /// <summary>
        /// This event will be triggered when the loading of a list of shared <see cref="OVRSpatialAnchor"/>(s) is completed.
        /// </summary>
        public UnityEvent<List<OVRSpatialAnchor>, OVRSpatialAnchor.OperationResult> OnSharedSpatialAnchorsLoadCompleted
        {
            get => _onSharedSpatialAnchorsLoadCompleted;
            set => _onSharedSpatialAnchorsLoadCompleted = value;
        }

        [SerializeField] private UnityEvent<List<OVRSpatialAnchor>, OVRSpatialAnchor.OperationResult> _onSpatialAnchorsShareCompleted;
        [SerializeField] private UnityEvent<List<OVRSpatialAnchor>, OVRAnchor.ShareResult> _onSpatialAnchorsShareToGroupCompleted;
        [SerializeField] private UnityEvent<List<OVRSpatialAnchor>, OVRSpatialAnchor.OperationResult> _onSharedSpatialAnchorsLoadCompleted;


        private Action<OVRSpatialAnchor.OperationResult, IEnumerable<OVRSpatialAnchor>> _onShareCompleted;
        private Action<OVRResult<OVRAnchor.ShareResult>, IEnumerable<OVRSpatialAnchor>> _onShareToGroupCompleted;

        private void Start()
        {
            _onShareCompleted += OnShareCompleted;
            _onShareToGroupCompleted += OnShareToGroupCompleted;
        }

        /// <summary>
        /// Create and instantiate an <see cref="OVRSpatialAnchor"/>.
        /// </summary>
        /// <param name="prefab">A prefab to instantiate as a <see cref="OVRSpatialAnchor"/>.</param>
        /// <param name="position">Initial position of instantiated GameObject.</param>
        /// <param name="rotation">Initial rotation of instantiated GameObject.</param>
        public async new void InstantiateSpatialAnchor(GameObject prefab, Vector3 position, Quaternion rotation)
        {
            if (prefab == null)
            {
                prefab = new GameObject("Shared Spatial Anchor");
            }

            var anchorGameObject = Instantiate(prefab, position, rotation);
            var spatialAnchor = anchorGameObject.AddComponent<OVRSpatialAnchor>();
            await InitSpatialAnchor(spatialAnchor);
        }

        private async Task InitSpatialAnchor(OVRSpatialAnchor anchor)
        {
            await WaitForInit(anchor);
            if (Result == OVRSpatialAnchor.OperationResult.Failure)
            {
                OnAnchorCreateCompleted?.Invoke(anchor, Result);
                return;
            }

            await SaveAsync(anchor);
            if (Result.IsError())
            {
                OnAnchorCreateCompleted?.Invoke(anchor, Result);
                return;
            }

            OnAnchorCreateCompleted?.Invoke(anchor, Result);
        }

        /// <summary>
        /// Loads and instantiates <see cref="OVRSpatialAnchor"/>(s) from a list of <see cref="Guid"/>s.
        /// </summary>
        /// <remarks>
        /// Use <see cref="OnSharedSpatialAnchorsLoadCompleted"/> to be notified when the loading is completed.
        /// </remarks>
        /// <param name="prefab">A prefab to instantiate as a <see cref="OVRSpatialAnchor"/>.</param>
        /// <param name="uuids">A list of <see cref="Guid"/>(s) to load.</param>
        /// <exception cref="ArgumentNullException">Throws when <paramref name="uuids"/> is null.</exception>
        /// <exception cref="ArgumentException">Throws when <paramref name="uuids"/> list is empty.</exception>
        public override async void LoadAndInstantiateAnchors(GameObject prefab, List<Guid> uuids)
        {
            if (uuids == null)
            {
                throw new ArgumentNullException();
            }

            if (uuids.Count == 0)
            {
                throw new ArgumentException($"[{nameof(SpatialAnchorCoreBuildingBlock)}] Uuid list is empty.");
            }

            using var unboundAnchorsPoolHandle =
                new OVRObjectPool.ListScope<OVRSpatialAnchor.UnboundAnchor>(out var unboundAnchors);
            var loadResult = await OVRSpatialAnchor.LoadUnboundSharedAnchorsAsync(uuids, unboundAnchors);
            LoadSharedSpatialAnchorsRoutine(prefab, loadResult);
        }

        /// <summary>
        /// Loads and instantiates <see cref="OVRSpatialAnchor"/>(s) from
        /// a ColocationSession group represented by <see cref="Guid"/>.
        /// </summary>
        /// <remarks>
        /// Use <see cref="OnSharedSpatialAnchorsLoadCompleted"/> to be notified when the loading is completed.
        /// </remarks>
        /// <param name="prefab">A prefab to instantiate as a <see cref="OVRSpatialAnchor"/>.</param>
        /// <param name="groupUuid">Representing the ColocationSession group used for retrieve the anchors.</param>
        public async void LoadAndInstantiateAnchorsFromGroup(GameObject prefab, Guid groupUuid)
        {
            using var unboundAnchorsPoolHandle =
                new OVRObjectPool.ListScope<OVRSpatialAnchor.UnboundAnchor>(out var unboundAnchors);
            var loadResult = await OVRSpatialAnchor.LoadUnboundSharedAnchorsAsync(groupUuid, unboundAnchors);
            LoadSharedSpatialAnchorsRoutine(prefab, loadResult);
        }

        private async void LoadSharedSpatialAnchorsRoutine(GameObject prefab, OVRResult<List<OVRSpatialAnchor.UnboundAnchor>, OVRSpatialAnchor.OperationResult> result)
        {
            if (!result.Success)
            {
                Debug.LogWarning($"[{nameof(SharedSpatialAnchorCore)}] Failed to load the shared spatial anchors: {result.Status}");
                OnSharedSpatialAnchorsLoadCompleted?.Invoke(null, result.Status);
                return;
            }
            var unboundAnchors = result.Value;
            if (unboundAnchors.Count == 0)
            {
                Debug.LogWarning($"[{nameof(SharedSpatialAnchorCore)}] There's no shared spatial anchors being loaded.");
                OnSharedSpatialAnchorsLoadCompleted?.Invoke(null, result.Status);
                return;
            }

            // Localize the anchors
            using (new OVRObjectPool.ListScope<OVRSpatialAnchor>(out var loadedAnchors))
            {
                for (int i = 0; i < unboundAnchors.Count; i++)
                {
                    var unboundAnchor = unboundAnchors[i];
                    if (!unboundAnchor.Localized)
                    {
                        if (!await unboundAnchor.LocalizeAsync())
                        {
                            Debug.LogWarning($"[{nameof(SharedSpatialAnchorCore)}] Failed to localize the anchor. Uuid: {unboundAnchor.Uuid}");
                            continue;
                        }
                    }

                    var isPoseValid = unboundAnchor.TryGetPose(out var pose);
                    if (!isPoseValid)
                    {
                        Debug.LogWarning("Unable to acquire initial anchor pose. Instantiating prefab at the origin.");
                    }

                    var spatialAnchorGo = isPoseValid
                        ? Instantiate(prefab, pose.position, pose.rotation)
                        : Instantiate(prefab);

                    var anchor = spatialAnchorGo.AddComponent<OVRSpatialAnchor>();
                    unboundAnchor.BindTo(anchor);
                    loadedAnchors.Add(anchor);
                }

                OnSharedSpatialAnchorsLoadCompleted?.Invoke(new List<OVRSpatialAnchor>(loadedAnchors), result.Status);
            }
        }

        /// <summary>
        /// Shares a list of <see cref="OVRSpatialAnchor"/>(s) with a list of <see cref="OVRSpaceUser"/>(s).
        /// </summary>
        /// <remarks>
        /// Use <see cref="OnSpatialAnchorsShareCompleted"/> to be notified when the sharing is completed.
        /// </remarks>
        /// <param name="anchors">A list of <see cref="OVRSpatialAnchor"/>(s) to share.</param>
        /// <param name="users">A list of <see cref="OVRSpaceUser"/> to share <see cref="OVRSpatialAnchor"/>(s) with.</param>
        /// <exception cref="ArgumentNullException">Throws when <paramref name="anchors"/> or <paramref name="users"/> is null.</exception>
        /// <exception cref="ArgumentException">Throws when <paramref name="anchors"/> or <paramref name="users"/> list is empty.</exception>
        public void ShareSpatialAnchors(List<OVRSpatialAnchor> anchors, List<OVRSpaceUser> users)
        {
            if (anchors == null || users == null)
            {
                throw new ArgumentNullException();
            }

            if (anchors.Count == 0 || users.Count == 0)
            {
                throw new ArgumentException($"[{nameof(SharedSpatialAnchorCore)}] Anchors or users cannot be zero.");
            }

            OVRSpatialAnchor.ShareAsync(anchors, users).ContinueWith(_onShareCompleted, anchors);
        }

        /// <summary>
        /// Shares a list of <see cref="OVRSpatialAnchor"/>(s) with
        /// a ColocationSession group represented by a <see cref="Guid"/>.
        /// </summary>
        /// <remarks>
        /// Use <see cref="OnSpatialAnchorsShareToGroupCompleted"/> to be notified when the sharing is completed.
        /// </remarks>
        /// <param name="anchors">A list of <see cref="OVRSpatialAnchor"/>(s) to share.</param>
        /// <param name="groupUuid">A group uuid to share the anchors with.</param>
        /// <exception cref="ArgumentNullException">Throws when <paramref name="anchors"/> is null.</exception>
        /// <exception cref="ArgumentException">Throws when <paramref name="anchors"/> list is empty.</exception>
        public void ShareSpatialAnchors(List<OVRSpatialAnchor> anchors, Guid groupUuid)
        {
            if (anchors == null)
            {
                throw new ArgumentNullException();
            }

            if (anchors.Count == 0)
            {
                throw new ArgumentException($"[{nameof(SharedSpatialAnchorCore)}] Anchors list cannot be zero.");
            }

            OVRSpatialAnchor.ShareAsync(anchors, groupUuid).ContinueWith(_onShareToGroupCompleted, anchors);
        }

        private void OnShareCompleted(OVRSpatialAnchor.OperationResult result, IEnumerable<OVRSpatialAnchor> anchors)
        {
            if (result != OVRSpatialAnchor.OperationResult.Success)
            {
                OnSpatialAnchorsShareCompleted?.Invoke(null, result);
                return;
            }

            using var _ = new OVRObjectPool.ListScope<OVRSpatialAnchor>(out var sharedAnchors);
            sharedAnchors.AddRange(anchors);

            OnSpatialAnchorsShareCompleted?.Invoke(new List<OVRSpatialAnchor>(sharedAnchors), OVRSpatialAnchor.OperationResult.Success);
        }

        private void OnShareToGroupCompleted(OVRResult<OVRAnchor.ShareResult> result, IEnumerable<OVRSpatialAnchor> anchors)
        {
            if (!result.Success)
            {
                OnSpatialAnchorsShareToGroupCompleted?.Invoke(null, result.Status);
                return;
            }

            using var _ = new OVRObjectPool.ListScope<OVRSpatialAnchor>(out var sharedAnchors);
            sharedAnchors.AddRange(anchors);

            OnSpatialAnchorsShareToGroupCompleted?.Invoke(new List<OVRSpatialAnchor>(sharedAnchors), result.Status);
        }

        private void OnDestroy()
        {
            _onShareCompleted -= OnShareCompleted;
            _onShareToGroupCompleted -= OnShareToGroupCompleted;
        }
    }
}
