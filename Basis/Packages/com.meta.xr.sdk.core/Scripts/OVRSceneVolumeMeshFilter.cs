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
using Meta.XR.Util;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Provides access to the mesh of a volume scene entity.
/// </summary>
/// <remarks>
/// When added to a GameObject that represents a volume scene entity, this
/// component provides access to the mesh representing the object.
///
/// If a MeshCollider is present, the component will cook the mesh for use
/// in physics and collisions. Default collider cooking options are used.
///
/// The Unity Job System is used to avoid blocking the main thread.
///
/// <see cref="OVRSceneManager"/> and associated classes are deprecated (v65), please use [MR Utility Kit](https://developer.oculus.com/documentation/unity/unity-mr-utility-kit-overview)" instead.
/// </remarks>
[HelpURL("https://developer.oculus.com/documentation/unity/unity-scene-use-scene-anchors/#further-scene-model-unity-components")]
[RequireComponent(typeof(MeshFilter))]
[Obsolete(OVRSceneManager.DeprecationMessage)]
[Feature(Feature.Scene)]
public class OVRSceneVolumeMeshFilter : MonoBehaviour
{
    /// <summary>
    /// True when all background processing is complete.
    /// </summary>
    public bool IsCompleted { get; private set; } = false;

    private Mesh _mesh;
    private MeshFilter _meshFilter;

    private void Start()
    {
        _mesh = new Mesh
        {
            name = $"{nameof(OVRSceneVolumeMeshFilter)} (anonymous)"
        };
        _meshFilter = GetComponent<MeshFilter>();
        _meshFilter.sharedMesh = _mesh;

        StartCoroutine(CreateVolumeMesh());
    }

    private IEnumerator CreateVolumeMesh()
    {
        if (!TryGetComponent<OVRSceneAnchor>(out var sceneAnchor))
        {
            OVRSceneManager.Development.LogWarning(
                nameof(OVRSceneVolumeMeshFilter),
                $"No Scene Anchor found on {gameObject.name}.", gameObject);
            IsCompleted = true;
            yield break;
        }

        // get mesh data counts
        var vertexCount = -1;
        var triangleCount = -1;
        using (var meshCountResults = new NativeArray<int>(2, Allocator.TempJob))
        {
            var job = new GetTriangleMeshCountsJob
            {
                Space = sceneAnchor.Space,
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
            Space = sceneAnchor.Space,
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

    private static bool IsJobDone(JobHandle job)
    {
        // convenience wrapper to complete job if it's finished
        // use variable to avoid potential race condition
        var completed = job.IsCompleted;
        if (completed) job.Complete();
        return completed;
    }

    #region Jobs

    // IJob wrapper for OVRPlugin.GetSpaceTMCounts
    // Results array - vertexCount:0, triangleCount:1, -1 if failed
    private struct GetTriangleMeshCountsJob : IJob
    {
        /// <summary>This is an internal member.</summary>
        public OVRSpace Space;
        [WriteOnly] public NativeArray<int> Results;

        /// <summary>This is an internal member.</summary>
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
        /// <summary>This is an internal member.</summary>
        public OVRSpace Space;

        /// <summary>This is an internal member.</summary>
        [WriteOnly] public NativeArray<Vector3> Vertices;

        /// <summary>This is an internal member.</summary>
        [WriteOnly] public NativeArray<int> Triangles;

        /// <summary>This is an internal member.</summary>
        public void Execute() =>
            OVRPlugin.GetSpaceTriangleMesh(Space, Vertices, Triangles);
    }

    // IJob to set vertices/triangles on Unity mesh data, converting from OpenXR
    // to Unity. Ensure that you set mesh data on Mesh after completion.
    private struct PopulateMeshDataJob : IJob
    {
        /// <summary>This is an internal member.</summary>
        [ReadOnly] public NativeArray<Vector3> Vertices;

        /// <summary>This is an internal member.</summary>
        [ReadOnly] public NativeArray<int> Triangles;

        /// <summary>This is an internal member.</summary>
        [WriteOnly]
        public Mesh.MeshData MeshData;

        /// <summary>This is an internal member.</summary>
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
        /// <summary>This is an internal member.</summary>
        public int MeshID;

        /// <summary>This is an internal member.</summary>
        public bool Convex;

        /// <summary>This is an internal member.</summary>
        public void Execute() => Physics.BakeMesh(MeshID, Convex);
    }

    #endregion
}
