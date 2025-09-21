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
using UnityEngine;

namespace Meta.XR.BuildingBlocks
{
    /// <summary>
    /// This is a utility class that is responsible for storing and loading an <see cref="OVRSpatialAnchor"/>
    /// from local storage.
    /// </summary>
    [RequireComponent(typeof(SpatialAnchorSpawnerBuildingBlock))]
    public class SpatialAnchorLoaderBuildingBlock : MonoBehaviour
    {
        private SpatialAnchorCoreBuildingBlock _spatialAnchorCore;
        private SpatialAnchorSpawnerBuildingBlock _spatialAnchorSpawner;

        private void Awake()
        {
            _spatialAnchorSpawner = GetComponent<SpatialAnchorSpawnerBuildingBlock>();
            _spatialAnchorCore = SpatialAnchorCoreBuildingBlock.GetFirstInstance();
        }

        /// <summary>
        /// Loads and instantiates a set of <see cref="OVRSpatialAnchor"/>s from a list of uuids.
        /// </summary>
        public virtual void LoadAndInstantiateAnchors(List<Guid> uuids)
        {
            _spatialAnchorCore.LoadAndInstantiateAnchors(_spatialAnchorSpawner.AnchorPrefab, uuids);
        }

        /// <summary>
        /// Loads an <see cref="OVRSpatialAnchor"/> from local storage.
        /// </summary>
        /// <remarks>A <see cref="SpatialAnchorLocalStorageManagerBuildingBlock"/> component is required to load anchors from
        /// default local storage.</remarks>
        /// <remarks>If </remarks>
        public virtual void LoadAnchorsFromDefaultLocalStorage()
        {
            var spatialAnchorLocalStorageManagerBuildingBlock = FindAnyObjectByType<SpatialAnchorLocalStorageManagerBuildingBlock>();
            if (!spatialAnchorLocalStorageManagerBuildingBlock)
            {
                Debug.Log($"[{nameof(SpatialAnchorLocalStorageManagerBuildingBlock)}] component is missing.");
                return;
            }

            using (new OVRObjectPool.ListScope<Guid>(out var uuids))
            {
                spatialAnchorLocalStorageManagerBuildingBlock.GetAnchorAnchorUuidFromLocalStorage(uuids);
                if (uuids.Count > 0)
                {
                    _spatialAnchorCore.LoadAndInstantiateAnchors(_spatialAnchorSpawner.AnchorPrefab, uuids);
                }
            }
        }
    }
}
