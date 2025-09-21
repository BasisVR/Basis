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

using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// This class contains the mesh data for hand tracking that is loaded from the Meta Quest runtime.
/// It is combined with skeleton data from <see cref="OVRSkeleton"/> to create a skinned mesh in <see cref="OVRMeshRenderer"/>.
/// </summary>
public class OVRMesh : MonoBehaviour
{
    /// <summary>
    /// Provides an interface for getting a MeshType. A data provider such as <see cref="OVRHand"/> can expose this interface to allow users to retrieve a MeshType from a private mesh.
    /// Mesh types include None, HandLeft for the left hand and HandRight for the right hand.
    /// </summary>
    public interface IOVRMeshDataProvider
    {
        MeshType GetMeshType();
    }

    public enum MeshType
    {
        None = OVRPlugin.MeshType.None,
        [InspectorName("OVR Hand (Left)")]
        HandLeft = OVRPlugin.MeshType.HandLeft,
        [InspectorName("OVR Hand (Right)")]
        HandRight = OVRPlugin.MeshType.HandRight,
        [InspectorName("OpenXR Hand (Left)")]
        XRHandLeft = OVRPlugin.MeshType.XRHandLeft,
        [InspectorName("OpenXR Hand (Right)")]
        XRHandRight = OVRPlugin.MeshType.XRHandRight,
    }


    [SerializeField]
    private IOVRMeshDataProvider _dataProvider;

    [SerializeField]
    private MeshType _meshType = MeshType.None;

    private MeshType _loadedMeshType = MeshType.None;

    private Mesh _mesh;

    /// <summary>
    /// True if the mesh has been successfully loaded, false otherwise.
    /// </summary>
    public bool IsInitialized { get; private set; }

    /// <summary>
    /// Getter for the Unity mesh object this class contains.
    /// </summary>
    public Mesh Mesh
    {
        get => _mesh;
    }

    internal MeshType GetMeshType()
    {
        return _meshType;
    }

    internal void SetMeshType(MeshType type)
    {
        _meshType = type;
    }

    private void Awake()
    {
        if (_dataProvider == null)
        {
            _dataProvider = GetComponent<IOVRMeshDataProvider>();
        }

        if (_dataProvider != null)
        {
            _meshType = _dataProvider.GetMeshType();
        }

        if (ShouldInitialize())
        {
            Initialize(_meshType);
        }
    }

    private bool ShouldInitialize()
    {
        if (_loadedMeshType != _meshType)
        {
            return true;
        }

        if (IsInitialized)
        {
            return false;
        }

        if (_meshType == MeshType.None)
        {
            return false;
        }
        else if (_meshType.IsHand())
        {
#if UNITY_EDITOR
            return OVRInput.IsControllerConnected(OVRInput.Controller.Hands);
#else
            return true;
#endif
        }
        else
        {
            return true;
        }
    }

    private void Initialize(MeshType meshType)
    {
        _mesh = new Mesh();

        if (OVRPlugin.GetMesh((OVRPlugin.MeshType)meshType, out var ovrpMesh))
        {
            TransformOvrpMesh(ovrpMesh, _mesh);
            IsInitialized = true;
        }
        _loadedMeshType = meshType;
    }

    private void TransformOvrpMesh(OVRPlugin.Mesh ovrpMesh, Mesh mesh)
    {
        int numVertices = (int)ovrpMesh.NumVertices;
        int numIndices = (int)ovrpMesh.NumIndices;

        using (var verticesNativeArray =
               new OVRMeshJobs.NativeArrayHelper<OVRPlugin.Vector3f>(ovrpMesh.VertexPositions, numVertices))
        using (var normalsNativeArray =
               new OVRMeshJobs.NativeArrayHelper<OVRPlugin.Vector3f>(ovrpMesh.VertexNormals, numVertices))
        using (var uvNativeArray =
               new OVRMeshJobs.NativeArrayHelper<OVRPlugin.Vector2f>(ovrpMesh.VertexUV0, numVertices))
        using (var weightsNativeArray =
               new OVRMeshJobs.NativeArrayHelper<OVRPlugin.Vector4f>(ovrpMesh.BlendWeights, numVertices))
        using (var indicesNativeArray =
               new OVRMeshJobs.NativeArrayHelper<OVRPlugin.Vector4s>(ovrpMesh.BlendIndices, numVertices))
        using (var trianglesNativeArray = new OVRMeshJobs.NativeArrayHelper<short>(ovrpMesh.Indices, numIndices))
        using (var vertices = new NativeArray<Vector3>(numVertices, Allocator.TempJob))
        using (var normals = new NativeArray<Vector3>(numVertices, Allocator.TempJob))
        using (var uv = new NativeArray<Vector2>(numVertices, Allocator.TempJob))
        using (var boneWeights = new NativeArray<BoneWeight>(numVertices, Allocator.TempJob))
        using (var triangles = new NativeArray<uint>(numIndices, Allocator.TempJob))
        {
            var job = new OVRMeshJobs.TransformToUnitySpaceJob
            {
                Vertices = vertices,
                Normals = normals,
                UV = uv,
                BoneWeights = boneWeights,
                MeshVerticesPosition = verticesNativeArray.UnityNativeArray,
                MeshNormals = normalsNativeArray.UnityNativeArray,
                MeshUV = uvNativeArray.UnityNativeArray,
                MeshBoneWeights = weightsNativeArray.UnityNativeArray,
                MeshBoneIndices = indicesNativeArray.UnityNativeArray
            };

            var jobTransformTriangle = new OVRMeshJobs.TransformTrianglesJob
            {
                Triangles = triangles,
                MeshIndices = trianglesNativeArray.UnityNativeArray,
                NumIndices = numIndices
            };

            var handle = job.Schedule(numVertices, 20);
            var handleTriangleJob = jobTransformTriangle.Schedule(numIndices, 60);
            JobHandle.CombineDependencies(handle, handleTriangleJob).Complete();

            mesh.SetVertices(job.Vertices);
            mesh.SetNormals(job.Normals);
            mesh.SetUVs(0, job.UV);
            mesh.boneWeights = job.BoneWeights.ToArray();

            mesh.SetIndexBufferParams(numIndices, IndexFormat.UInt32);
            mesh.SetIndexBufferData(jobTransformTriangle.Triangles, 0, 0, numIndices);
            mesh.SetSubMesh(0, new SubMeshDescriptor(0, numIndices));
        }
    }

#if UNITY_EDITOR
    private void Update()
    {
        if (ShouldInitialize())
        {
            Initialize(_meshType);
        }
    }
#endif
}
