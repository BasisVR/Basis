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
    /// A local storage manager responsible for storing <see cref="OVRSpatialAnchor"/>'s uuid. This is used as part of the SampleSpatialAnchorController and not designed to be used outside of that context. It is for educational purposes only.
    /// </summary>
    public class SpatialAnchorLocalStorageManagerBuildingBlock : MonoBehaviour
    {
        private SpatialAnchorCoreBuildingBlock _spatialAnchorCore;

        private const string NumUuidsPlayerPref = "numUuids";

        private void Start()
        {
            _spatialAnchorCore = SpatialAnchorCoreBuildingBlock.GetFirstInstance();
            _spatialAnchorCore.OnAnchorCreateCompleted.AddListener(SaveAnchorUuidToLocalStorage);
            _spatialAnchorCore.OnAnchorEraseCompleted.AddListener(RemoveAnchorFromLocalStorage);
        }

        internal void SaveAnchorUuidToLocalStorage(OVRSpatialAnchor anchor, OVRSpatialAnchor.OperationResult result)
        {
            if (result != OVRSpatialAnchor.OperationResult.Success)
            {
                return;
            }

            if (!PlayerPrefs.HasKey(NumUuidsPlayerPref))
            {
                PlayerPrefs.SetInt(NumUuidsPlayerPref, 0);
            }

            int playerNumUuids = PlayerPrefs.GetInt(NumUuidsPlayerPref);
            PlayerPrefs.SetString("uuid" + playerNumUuids, anchor.Uuid.ToString());
            PlayerPrefs.SetInt(NumUuidsPlayerPref, ++playerNumUuids);
        }

        /// <summary>
        /// Remove a <see cref="Guid"/> of specified <see cref="OVRSpatialAnchor"/> from local storage.
        /// </summary>
        internal void RemoveAnchorFromLocalStorage(OVRSpatialAnchor anchor, OVRSpatialAnchor.OperationResult result)
        {
            var uuid = anchor.Uuid;
            if (result == OVRSpatialAnchor.OperationResult.Failure)
                return;

            var playerUuidCount = PlayerPrefs.GetInt(NumUuidsPlayerPref, 0);
            for (int i = 0; i < playerUuidCount; i++)
            {
                var key = "uuid" + i;
                var value = PlayerPrefs.GetString(key, "");
                if (value.Equals(uuid.ToString()))
                {
                    var lastKey = "uuid" + (playerUuidCount - 1);
                    var lastValue = PlayerPrefs.GetString(lastKey);
                    PlayerPrefs.SetString(key, lastValue);
                    PlayerPrefs.DeleteKey(lastKey);

                    playerUuidCount--;
                    if (playerUuidCount < 0) playerUuidCount = 0;
                    PlayerPrefs.SetInt(NumUuidsPlayerPref, playerUuidCount);
                    break;
                }
            }
        }

        /// <summary>
        /// This method will load the <see cref="Guid"/>(s) of <see cref="OVRSpatialAnchor"/> from local storage for each one found.
        /// </summary>
        /// <returns>
        /// List of loaded <see cref="Guid"/>(s) from local storage.
        /// </returns>
        internal void GetAnchorAnchorUuidFromLocalStorage(List<Guid> uuids)
        {
            // Get number of saved anchor uuids
            if (!PlayerPrefs.HasKey(NumUuidsPlayerPref))
            {
                Reset();
                Debug.Log($"[{nameof(SpatialAnchorLocalStorageManagerBuildingBlock)}] Anchor not found.");
                return;
            }

            // Load unbounded anchors' uuid from local storage
            uuids.Clear();
            var playerUuidCount = PlayerPrefs.GetInt(NumUuidsPlayerPref);
            for (int i = 0; i < playerUuidCount; ++i)
            {
                var uuidKey = "uuid" + i;
                if (!PlayerPrefs.HasKey(uuidKey))
                    continue;

                var currentUuid = PlayerPrefs.GetString(uuidKey);
                uuids.Add(new Guid(currentUuid));
            }
        }

        /// <summary>
        /// Clear all <see cref="OVRSpatialAnchor"/> data from local storage.
        /// </summary>
        /// <remarks>
        /// Sets Unity PlayerPrefs to discard all <see cref="OVRSpatialAnchor"/> data from local storage.
        /// </remarks>
        public void Reset()
        {
            PlayerPrefs.SetInt(NumUuidsPlayerPref, 0);
        }

        private void OnDestroy()
        {
            _spatialAnchorCore.OnAnchorCreateCompleted.RemoveAllListeners();
        }
    }
}
