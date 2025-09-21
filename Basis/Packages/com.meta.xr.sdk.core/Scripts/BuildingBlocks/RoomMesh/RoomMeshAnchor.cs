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
using System.Threading.Tasks;
using Unity.Jobs;
using UnityEngine;
using Unity.Collections;
using UnityEngine.Rendering;

public class RoomMeshAnchor : MonoBehaviour
{
    public bool IsCompleted { get; private set; }

    private OVRAnchor _anchor;
    private bool Valid => _anchor.Handle != 0;
    private bool IsComponentEnabled<T>() where T : struct, IOVRAnchorComponent<T> => _anchor.TryGetComponent(out T component) && component.IsEnabled;
    private static readonly Quaternion RotateY180 = Quaternion.Euler(0, 180, 0);

    private OVRSemanticLabels _labels;
    private OVRTriangleMesh _triangleMeshComponent;

    private Mesh _mesh;
    private MeshFilter _meshFilter;

    private void Awake()
    {
        _mesh = new Mesh
        {
            name = $"{nameof(RoomMeshAnchor)} (anonymous)"
        };

        if (!TryGetComponent(out _meshFilter))
            _meshFilter = gameObject.AddComponent<MeshFilter>();

        _meshFilter.sharedMesh = _mesh;
    }

    internal async void Initialize(OVRAnchor anchor)
    {
        _anchor = anchor;

        if (TryUpdateTransform())
        {
            Debug.Log($"[{nameof(RoomMeshAnchor)}][{_anchor.Uuid}] Initial transform set.", gameObject);
        }
        else
        {
            Debug.LogWarning($"[{nameof(RoomMeshAnchor)}][{_anchor.Uuid}] {nameof(OVRPlugin.TryLocateSpace)} failed. The entity may have the wrong initial transform.", gameObject);
        }

        if (!IsComponentEnabled<OVRSemanticLabels>()) _labels = await EnableComponent<OVRSemanticLabels>();
        if (!IsComponentEnabled<OVRTriangleMesh>()) _triangleMeshComponent = await EnableComponent<OVRTriangleMesh>();

        if (_triangleMeshComponent != null)
        {
            StartCoroutine(GenerateRoomMesh());
        }
    }

    private IEnumerator GenerateRoomMesh()
    {
        // get mesh data counts
        var vertexCount = -1;
        var triangleCount = -1;
        using (var meshCountResults = new NativeArray<int>(2, Allocator.TempJob))
        {
            var job = new GetTriangleMeshCountsJob
            {
                Space = _anchor.Handle,
                Results = meshCountResults
            }.Schedule();
            while (!IsJobDone(job))
            {
                yield return null;
            }

            vertexCount = meshCountResults[0];
            triangleCount = meshCountResults[1];
        }

        if (vertexCount == -1)
        {
            IsCompleted = true;
            yield break;
        }

        // retrieve mesh data, then convert and
        // populate mesh data as dependent job
        var vertices = new NativeArray<Vector3>(vertexCount, Allocator.Persistent);
        var triangles = new NativeArray<int>(triangleCount * 3, Allocator.Persistent);
        var meshDataArray = Mesh.AllocateWritableMeshData(1);
        var getMeshJob = new GetTriangleMeshJob
        {
            Space = _anchor.Handle,
            Vertices = vertices,
            Triangles = triangles
        }.Schedule();
        var populateMeshJob = new PopulateMeshDataJob
        {
            Vertices = vertices,
            Triangles = triangles,
            MeshData = meshDataArray[0]
        }.Schedule(getMeshJob);
        var disposeVerticesJob = JobHandle.CombineDependencies(
            vertices.Dispose(populateMeshJob), triangles.Dispose(populateMeshJob));
        while (!IsJobDone(disposeVerticesJob))
        {
            yield return null;
        }

        // apply data to Unity mesh
        Mesh.ApplyAndDisposeWritableMeshData(meshDataArray, _mesh);
        _mesh.RecalculateNormals();
        _mesh.RecalculateBounds();

        // bake mesh if we have a collider
        if (TryGetComponent<MeshCollider>(out var collider))
        {
            var job = new BakeMeshJob
            {
                MeshID = _mesh.GetInstanceID(),
                Convex = collider.convex
            }.Schedule();
            while (!IsJobDone(job))
            {
                yield return null;
            }

            collider.sharedMesh = _mesh;
        }

        IsCompleted = true;
    }

    private async Task<T> EnableComponent<T>() where T : struct, IOVRAnchorComponent<T>
    {
        if (_anchor.TryGetComponent(out T component))
        {
            await component.SetEnabledAsync(true);
        }

        return component;
    }

    private bool TryUpdateTransform()
    {
        if (!Valid || !enabled || !IsComponentEnabled<OVRLocatable>())
            return false;

        var tryLocateSpace = OVRPlugin.TryLocateSpace(_anchor.Handle, OVRPlugin.GetTrackingOriginType(), out var pose,
            out var locationFlags);
        if (!tryLocateSpace || !locationFlags.IsOrientationValid() || !locationFlags.IsPositionValid())
        {
            return false;
        }

        var worldSpacePose = new OVRPose
        {
            position = pose.Position.FromFlippedZVector3f(),
            orientation = pose.Orientation.FromFlippedZQuatf() * RotateY180
        }.ToWorldSpacePose(Camera.main);
        transform.SetPositionAndRotation(worldSpacePose.position, worldSpacePose.orientation);
        return true;
    }

    private void OnDestroy()
    {
        Destroy(_mesh);
    }

    #region Jobs

    private static bool IsJobDone(JobHandle job)
    {
        // convenience wrapper to complete job if it's finished
        // use variable to avoid potential race condition
        var completed = job.IsCompleted;
        if (completed) job.Complete();
        return completed;
    }

    // IJob wrapper for OVRPlugin.GetSpaceTMCounts
    // Results array - vertexCount:0, triangleCount:1, -1 if failed
    private struct GetTriangleMeshCountsJob : IJob
    {
        public OVRSpace Space;
        [WriteOnly] public NativeArray<int> Results;

        public void Execute()
        {
            Results[0] = -1;
            Results[1] = -1;
            if (OVRPlugin.GetSpaceTriangleMeshCounts(Space, out int vertexCount, out int triangleCount))
            {
                Results[0] = vertexCount;
                Results[1] = triangleCount;
            }
        }
    }

    // IJob wrapper for OVRPlugin.GetSpaceTM
    private struct GetTriangleMeshJob : IJob
    {
        public OVRSpace Space;

        [WriteOnly] public NativeArray<Vector3> Vertices;
        [WriteOnly] public NativeArray<int> Triangles;

        public void Execute() => OVRPlugin.GetSpaceTriangleMesh(Space, Vertices, Triangles);
    }


    // IJob to set vertices/triangles on Unity mesh data, converting from OpenXR
    // to Unity. Ensure that you set mesh data on Mesh after completion.
    private struct PopulateMeshDataJob : IJob
    {
        [ReadOnly] public NativeArray<Vector3> Vertices;
        [ReadOnly] public NativeArray<int> Triangles;

        [WriteOnly]
        public Mesh.MeshData MeshData;

        public void Execute()
        {
            // assign vertices, converting from OpenXR to Unity
            MeshData.SetVertexBufferParams(Vertices.Length,
                new VertexAttributeDescriptor(VertexAttribute.Position),
                new VertexAttributeDescriptor(VertexAttribute.Normal, stream: 1));
            var vertices = MeshData.GetVertexData<Vector3>();
            for (var i = 0; i < vertices.Length; i++)
            {
                var vertex = Vertices[i];
                vertices[i] = new Vector3(-vertex.x, vertex.y, vertex.z);
            }

            // assign triangles, changing the winding order
            MeshData.SetIndexBufferParams(Triangles.Length, IndexFormat.UInt32);
            var indices = MeshData.GetIndexData<int>();
            for (var i = 0; i < indices.Length; i += 3)
            {
                indices[i + 0] = Triangles[i + 0];
                indices[i + 1] = Triangles[i + 2];
                indices[i + 2] = Triangles[i + 1];
            }

            // lastly, set the sub mesh
            MeshData.subMeshCount = 1;
            MeshData.SetSubMesh(0, new SubMeshDescriptor(0, Triangles.Length));
        }
    }

    // BakeMesh with Physics - this only bakes with default collider options
    // and works on a mesh id. After mesh is baked, it may need assigning
    // to the collider.
    private struct BakeMeshJob : IJob
    {
        public int MeshID;
        public bool Convex;

        public void Execute() => Physics.BakeMesh(MeshID, Convex);
    }

    #endregion
}
