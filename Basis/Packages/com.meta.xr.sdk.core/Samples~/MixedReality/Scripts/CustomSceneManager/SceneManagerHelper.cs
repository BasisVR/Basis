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

using Meta.XR.Samples;
using Unity.Collections;
using UnityEngine;

/// <summary>
/// A smaller helper class for Custom Scene Manager samples.
/// </summary>
[MetaCodeSample("CoreSDK-MixedReality")]
public class SceneManagerHelper
{
    public GameObject AnchorGameObject { get; }
    private readonly Transform _trackingSpace;
    private Material _material;

    public SceneManagerHelper(GameObject gameObject, Transform trackingSpace, Material material)
    {
        AnchorGameObject = gameObject;
        _trackingSpace = trackingSpace;
        _material = material;
    }

    public SceneManagerHelper(GameObject gameObject, Transform trackingSpace)
    {
        AnchorGameObject = gameObject;
        _trackingSpace = trackingSpace;
        _material = new Material(Shader.Find("Standard"));
    }

    public void SetLocation(OVRLocatable locatable)
    {
        if (!locatable.TryGetSceneAnchorPose(out var pose))
            return;

        var position = pose.ComputeWorldPosition(_trackingSpace);
        var rotation = pose.ComputeWorldRotation(_trackingSpace);

        if (position != null && rotation != null)
            AnchorGameObject.transform.SetPositionAndRotation(
                position.Value, rotation.Value);
    }

    public void CreatePlane(OVRBounded2D bounds)
    {
        var planeGO = GameObject.CreatePrimitive(PrimitiveType.Cube);
        planeGO.name = "Plane";
        planeGO.transform.SetParent(AnchorGameObject.transform, false);
        planeGO.transform.localPosition = bounds.BoundingBox.center;
        planeGO.transform.localScale = new Vector3(
            bounds.BoundingBox.size.x,
            bounds.BoundingBox.size.y,
            0.01f);
        planeGO.GetComponent<MeshRenderer>().material = new Material(_material);
        planeGO.GetComponent<MeshRenderer>().material.SetColor(
            "_BaseColor", UnityEngine.Random.ColorHSV());
    }

    public void UpdatePlane(OVRBounded2D bounds)
    {
        var planeGO = AnchorGameObject.transform.Find("Plane");
        if (planeGO == null)
            CreatePlane(bounds);
        else
        {
            planeGO.transform.localPosition = bounds.BoundingBox.center;
            planeGO.transform.localScale = new Vector3(
                bounds.BoundingBox.size.x,
                bounds.BoundingBox.size.y,
                0.01f);
        }
    }

    public void CreateVolume(OVRBounded3D bounds)
    {
        var volumeGO = GameObject.CreatePrimitive(PrimitiveType.Cube);
        volumeGO.name = "Volume";
        volumeGO.transform.SetParent(AnchorGameObject.transform, false);
        volumeGO.transform.localPosition = bounds.BoundingBox.center;
        volumeGO.transform.localScale = bounds.BoundingBox.size;
        volumeGO.GetComponent<MeshRenderer>().material = new Material(_material);
        volumeGO.GetComponent<MeshRenderer>().material.SetColor(
            "_BaseColor", UnityEngine.Random.ColorHSV());
    }

    public void UpdateVolume(OVRBounded3D bounds)
    {
        var volumeGO = AnchorGameObject.transform.Find("Volume");
        if (volumeGO == null)
            CreateVolume(bounds);
        else
        {
            volumeGO.transform.localPosition = bounds.BoundingBox.center;
            volumeGO.transform.localScale = bounds.BoundingBox.size;
        }
    }

    public void CreateMesh(OVRTriangleMesh mesh)
    {
        if (!mesh.TryGetCounts(out var vcount, out var tcount)) return;
        using var vs = new NativeArray<Vector3>(vcount, Allocator.Temp);
        using var ts = new NativeArray<int>(tcount * 3, Allocator.Temp);
        if (!mesh.TryGetMesh(vs, ts)) return;

        var trimesh = new Mesh();
        trimesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        trimesh.SetVertices(vs);
        trimesh.SetTriangles(ts.ToArray(), 0);

        var meshGO = GameObject.CreatePrimitive(PrimitiveType.Quad);
        meshGO.name = "Mesh";
        meshGO.transform.SetParent(AnchorGameObject.transform, false);
        meshGO.GetComponent<MeshFilter>().sharedMesh = trimesh;
        meshGO.GetComponent<MeshCollider>().sharedMesh = trimesh;
        meshGO.GetComponent<MeshRenderer>().material = new Material(_material);
        meshGO.GetComponent<MeshRenderer>().material.SetColor(
            "_BaseColor", UnityEngine.Random.ColorHSV());
    }

    public void UpdateMesh(OVRTriangleMesh mesh)
    {
        var meshGO = AnchorGameObject.transform.Find("Mesh");
        if (meshGO != null) UnityEngine.Object.Destroy(meshGO);
        CreateMesh(mesh);
    }


}
