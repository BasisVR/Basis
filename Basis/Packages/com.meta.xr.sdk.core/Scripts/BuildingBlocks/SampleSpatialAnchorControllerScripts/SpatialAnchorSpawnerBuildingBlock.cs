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

using UnityEngine;

namespace Meta.XR.BuildingBlocks
{
    /// <summary>
    /// A utility class to manage instantiation of an <see cref="OVRSpatialAnchor"/> GameObject.
    /// </summary>
    /// <remarks>
    /// By default <see cref="OVRSpatialAnchor"/> will be spawned at user's hand's / controller's position. Disable
    /// <see cref="FollowHand"/> property to spawn the <see cref="OVRSpatialAnchor"/> at the specified position and orientation.
    /// See <see cref="SpatialAnchorCoreBuildingBlock.OnAnchorCreateCompleted"/> event to get notified when the anchor is created.
    /// See also `Editor/BuildingBlocks/BlockData/SampleSpatialAnchorController` in the Meta XR Core SDK for an example of how to use this class.
    /// <seealso cref="BuildingBlock"/>
    /// </remarks>
    public class SpatialAnchorSpawnerBuildingBlock : MonoBehaviour
    {
        /// <summary>
        /// A prefab to instantiate.
        /// </summary>
        public GameObject AnchorPrefab
        {
            get => _anchorPrefab;
            set
            {
                _anchorPrefab = value;
                if (_anchorPrefabTransform) Destroy(_anchorPrefabTransform.gameObject);
                _anchorPrefabTransform = Instantiate(AnchorPrefab).transform;
                FollowHand = _followHand;
            }
        }

        /// <summary>
        /// Indicates whether the <see cref="OVRSpatialAnchor"/> source prefab you instantiate will follow the user's hand.
        /// </summary>
        public bool FollowHand
        {
            get => _followHand;
            set
            {
                _followHand = value;
                if (_followHand)
                {
                    _initialPosition = _anchorPrefabTransform.position;
                    _initialRotation = _anchorPrefabTransform.rotation;
                    _anchorPrefabTransform.parent = _cameraRig.rightControllerAnchor;
                    _anchorPrefabTransform.localPosition = Vector3.zero;
                    _anchorPrefabTransform.localRotation = Quaternion.identity;
                }
                else
                {
                    _anchorPrefabTransform.parent = null;
                    _anchorPrefabTransform.SetPositionAndRotation(_initialPosition, _initialRotation);
                }
            }
        }

        [Tooltip("A placeholder object to place in the anchor's position.")]
        [SerializeField]
        private GameObject _anchorPrefab;

        [Tooltip("Anchor prefab GameObject will follow the user's right hand.")]
        [SerializeField] private bool _followHand = true;

        private SpatialAnchorCoreBuildingBlock _spatialAnchorCore;
        private OVRCameraRig _cameraRig;
        private Transform _anchorPrefabTransform;
        private Vector3 _initialPosition;
        private Quaternion _initialRotation;

        private void Awake()
        {
            _spatialAnchorCore = SpatialAnchorCoreBuildingBlock.GetFirstInstance();
            _cameraRig = FindAnyObjectByType<OVRCameraRig>();
            AnchorPrefab = _anchorPrefab;
            FollowHand = _followHand;
        }

        /// <summary>
        /// Spawns a new <see cref="OVRSpatialAnchor"/> for the <see cref="AnchorPrefab"/> in the provided position and the provided orientation.
        /// </summary>
        /// <param name="position">Position for the new anchor.</param>
        /// <param name="rotation">Orientation of the new anchor</param>
        public void SpawnSpatialAnchor(Vector3 position, Quaternion rotation)
        {
            _spatialAnchorCore.InstantiateSpatialAnchor(AnchorPrefab, position, rotation);
        }

        internal void SpawnSpatialAnchor()
        {
            if (!FollowHand)
                SpawnSpatialAnchor(AnchorPrefab.transform.position, AnchorPrefab.transform.rotation);
            else
                SpawnSpatialAnchor(_anchorPrefabTransform.position, _anchorPrefabTransform.rotation);
        }
    }
}
