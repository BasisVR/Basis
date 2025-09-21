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
using UnityEngine;
using UnityEngine.Rendering;

namespace Meta.XR.BuildingBlocks
{
    public class RoomMeshController : MonoBehaviour
    {
        [SerializeField] private GameObject _meshPrefab;
        private RoomMeshEvent _roomMeshEvent;
        private RoomMeshAnchor _roomMeshAnchor;

        private void Awake()
        {
            _roomMeshAnchor = GetComponent<RoomMeshAnchor>();
#pragma warning disable CS0618 // Type or member is obsolete
            _roomMeshEvent = FindObjectOfType<RoomMeshEvent>();
#pragma warning restore CS0618 // Type or member is obsolete
        }

        private IEnumerator Start()
        {
            var timeout = 10f;
            var startTime = Time.time;
            while (!OVRPermissionsRequester.IsPermissionGranted(OVRPermissionsRequester.Permission.Scene))
            {
                if (Time.time - startTime > timeout)
                {
                    Debug.LogWarning($"[{nameof(RoomMeshController)}] Spatial Data permission is required to load Room Mesh.");
                    yield break;
                }
                yield return null;
            }

            yield return LoadRoomMesh();
            yield return UpdateVolume();
            if (_roomMeshAnchor == null) yield break;

            timeout = 3f;
            startTime = Time.time;
            while (!_roomMeshAnchor.IsCompleted)
            {
                if (Time.time - startTime > timeout)
                {
                    Debug.LogWarning($"[{nameof(RoomMeshController)}] Failed to create Room Mesh.");
                    yield break;
                }
                yield return null;
            }

            _roomMeshEvent.OnRoomMeshLoadCompleted?.Invoke(_roomMeshAnchor.GetComponent<MeshFilter>());
        }

        private IEnumerator UpdateVolume()
        {
            if (_roomMeshAnchor == null)
            {
                yield break;
            }

            while (!_roomMeshAnchor.IsCompleted) yield return null;

            var parentMeshFilter = _roomMeshAnchor.GetComponent<MeshFilter>();

            var parentMesh = parentMeshFilter.sharedMesh;

            var vertices = new List<Vector3>();
            var triangles = new List<int>();

            parentMesh.GetVertices(vertices);
            parentMesh.GetTriangles(triangles, 0);

            var c = new Color[triangles.Count];
            var v = new Vector3[triangles.Count];
            var idx = new int[triangles.Count];
            for (var i = 0; i < triangles.Count; i++)
            {
                c[i] = new Color(
                    i % 3 == 0 ? 1.0f : 0.0f,
                    i % 3 == 1 ? 1.0f : 0.0f,
                    i % 3 == 2 ? 1.0f : 0.0f);
                v[i] = vertices[triangles[i]];
                idx[i] = i;
            }

            var mesh = new Mesh
            {
                indexFormat = IndexFormat.UInt32
            };
            mesh.SetVertices(v);
            mesh.SetColors(c);
            mesh.SetIndices(idx, MeshTopology.Triangles, 0, true, 0);
            mesh.RecalculateBounds();
            mesh.RecalculateNormals();

            parentMeshFilter.sharedMesh = mesh;
        }

        private IEnumerator LoadRoomMesh()
        {
            using (new OVRObjectPool.ListScope<OVRAnchor>(out var anchors))
            {
                var task = OVRAnchor.FetchAnchorsAsync(anchors, new OVRAnchor.FetchOptions
                {
                    SingleComponentType = typeof(OVRTriangleMesh)
                });
                while (task.IsPending) yield return null;

                if (anchors.Count == 0)
                {
                    Debug.LogWarning($"[{nameof(RoomMeshController)}] No RoomMesh available.");
                    yield break;
                }

                foreach (var anchor in anchors)
                {
                    if (!anchor.TryGetComponent(out OVRLocatable locatableComponent))
                    {
                        Debug.LogWarning($"[{nameof(RoomMeshController)}] Failed to localize the room mesh anchor.");
                        continue;
                    }

                    var localizeTask = locatableComponent.SetEnabledAsync(true);
                    while (localizeTask.IsPending) yield return null;

                    InstantiateRoomMesh(anchor, _meshPrefab);
                }
            }
        }

        private void InstantiateRoomMesh(OVRAnchor anchor, GameObject prefab)
        {
            _roomMeshAnchor = Instantiate(prefab, Vector3.zero, Quaternion.identity).GetComponent<RoomMeshAnchor>();
            _roomMeshAnchor.gameObject.name = _meshPrefab.name;
            _roomMeshAnchor.gameObject.SetActive(true);
            _roomMeshAnchor.Initialize(anchor);
        }
    }
}
