using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.LowLevelPhysics;

public class ColliderClone
{

    private static Quaternion rotCapsuleX = Quaternion.Euler(new Vector3(0, 0, 90));
    private static Quaternion rotCapsuleZ = Quaternion.Euler(new Vector3(90, 0, 0));

    public static GameObject CloneColliderMesh(Collider collider, Transform parent, string cloneName)
    {
        GameObject primitive = null;
        switch (collider.GeometryHolder.Type)
        {
            case GeometryType.Sphere:
                var sphere = (SphereCollider)collider;
                // TODO: use&cache sphere mesh generated (is lower poly)
                primitive = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                if (primitive.TryGetComponent(out SphereCollider sCol))
                {
                    UnityEngine.Object.Destroy(sCol);
                }
                else
                {
                    BasisDebug.LogError("Primitive Sphere did not have a sphere collider?!");
                }
                primitive.name = cloneName;
                primitive.transform.parent = parent;

                primitive.transform.localPosition = sphere.center;
                primitive.transform.localScale = sphere.radius * 2 * Vector3.one;
                break;
            case GeometryType.Capsule:
                var capsule = (CapsuleCollider)collider;
                primitive = new GameObject(cloneName);
                MeshFilter mFilter = primitive.AddComponent<MeshFilter>();
                primitive.AddComponent<MeshRenderer>();
                primitive.transform.parent = parent;

                // generate mesh since we cant just scale the capsule primitve (sadly)
                Mesh newMesh = CreateCapsuleMesh(capsule.radius, capsule.height, 8);
                mFilter.mesh = newMesh;

                primitive.transform.localPosition = capsule.center;

                switch (capsule.direction)
                {
                    // X, Y (no change), Z
                    case 0:
                        primitive.transform.localRotation = rotCapsuleX;
                        break;
                    case 2:
                        primitive.transform.localRotation = rotCapsuleZ;
                        break;
                    default:
                        break;
                }
                primitive.transform.localScale = Vector3.one;

                break;
            case GeometryType.Box:
                var box = (BoxCollider)collider;
                primitive = GameObject.CreatePrimitive(PrimitiveType.Cube);
                if (primitive.TryGetComponent(out BoxCollider boxCol))
                {
                    UnityEngine.Object.Destroy(boxCol);
                }
                else
                {
                    BasisDebug.LogError("Cube Primitve did not have a box collider?!");
                }
                primitive.name = cloneName;
                primitive.transform.parent = parent;

                primitive.transform.SetLocalPositionAndRotation(box.center, Quaternion.identity);
                primitive.transform.localScale = box.size;
                break;
            case GeometryType.ConvexMesh:
            case GeometryType.TriangleMesh:

                if (!collider.TryGetComponent(out MeshFilter meshFilter))
                {
                    BasisDebug.LogWarning("Mesh collider clone must have MeshFilter on same object to generate Highlight box");
                    return null;
                }
                // mesh filter bounds, not collider bounds- as mesh bounds is in local space and not axis aligned (which is what we want)
                Bounds objectBounds = meshFilter.mesh.bounds;

                primitive = GameObject.CreatePrimitive(PrimitiveType.Cube);
                if (primitive.TryGetComponent(out BoxCollider _col))
                {
                    UnityEngine.Object.Destroy(_col);
                }
                else
                {
                    BasisDebug.LogError("Cube Primitve did not have a box collider?!");
                }
                primitive.name = cloneName;
                primitive.transform.parent = parent;

                primitive.transform.localPosition = objectBounds.center;
                primitive.transform.localRotation = Quaternion.identity;
                primitive.transform.localScale = objectBounds.size;
                break;

            // dont know how to handle remaning types 
            case GeometryType.Terrain:
            case GeometryType.Invalid:
            default:
                Debug.LogWarning("Mesh collider clone could not generate clone for invalid collider type: " + collider.GeometryHolder.Type);
                break;
        }

        primitive.SetActive(false);
        return primitive;
    }

    public static Mesh CreateCapsuleMesh(float radius, float height, int segments)
    {
        Mesh mesh = new Mesh();

        segments = segments % 2 != 0 ? segments + 1 : segments;

        float cylinderHeight = Math.Max(height - 2 * radius, 0);

        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        var normals = new List<Vector3>();

        int hemisphereSegments = segments / 2;
        // North
        GenerateHemisphere(vertices, triangles, normals, radius, segments, hemisphereSegments, true, cylinderHeight / 2);
        // South
        GenerateHemisphere(vertices, triangles, normals, radius, segments, hemisphereSegments, false, -(cylinderHeight / 2));
        // Cylinder
        GenerateCylinder(vertices, triangles, normals, radius, cylinderHeight, segments);

        mesh.vertices = vertices.ToArray();
        mesh.triangles = triangles.ToArray();
        mesh.normals = normals.ToArray();

        mesh.RecalculateTangents();
        mesh.Optimize();

        return mesh;
    }

    private static void GenerateHemisphere(
        List<Vector3> vertices,
        List<int> triangles,
        List<Vector3> normals,
        float radius, int segments, int hemisphereSegments, bool isNorth, float yOffset)
    {
        int baseIndex = vertices.Count;

        for (int lat = 0; lat <= hemisphereSegments; lat++)
        {
            float latAngle = Mathf.PI * 0.5f * (lat / (float)hemisphereSegments) * (isNorth ? 1 : -1);
            float y = radius * Mathf.Sin(latAngle);
            float ringRadius = radius * Mathf.Cos(latAngle);

            for (int lon = 0; lon <= segments; lon++)
            {
                float lonAngle = 2 * Mathf.PI * lon / segments;
                float x = ringRadius * Mathf.Cos(lonAngle);
                float z = ringRadius * Mathf.Sin(lonAngle);

                Vector3 vertex = new Vector3(x, y + yOffset, z);
                vertices.Add(vertex);

                // Calculate proper surface normal
                Vector3 normal = new Vector3(x, y, z).normalized;
                normals.Add(normal);

                if (lat < hemisphereSegments && lon < segments)
                {
                    int current = baseIndex + lat * (segments + 1) + lon;
                    int next = baseIndex + lat * (segments + 1) + lon + 1;
                    int below = baseIndex + (lat + 1) * (segments + 1) + lon;
                    int belowNext = baseIndex + (lat + 1) * (segments + 1) + lon + 1;

                    if (!isNorth)
                    {
                        triangles.Add(current);
                        triangles.Add(next);
                        triangles.Add(below);
                        triangles.Add(next);
                        triangles.Add(belowNext);
                        triangles.Add(below);
                    }
                    else
                    {
                        triangles.Add(current);
                        triangles.Add(below);
                        triangles.Add(next);
                        triangles.Add(next);
                        triangles.Add(below);
                        triangles.Add(belowNext);
                    }
                }
            }
        }
    }

    private static void GenerateCylinder(
        List<Vector3> vertices,
        List<int> triangles,
        List<Vector3> normals,
        float radius, float height, int segments)
    {
        if (height <= 0) return;

        int baseIndex = vertices.Count;

        for (int i = 0; i <= 1; i++)
        {
            float y = (i == 0 ? 1 : -1) * height / 2;

            for (int lon = 0; lon <= segments; lon++)
            {
                float lonAngle = 2 * Mathf.PI * lon / segments;
                float x = radius * Mathf.Cos(lonAngle);
                float z = radius * Mathf.Sin(lonAngle);

                vertices.Add(new Vector3(x, y, z));

                // Calculate cylinder surface normal (points outward from central axis)
                Vector3 normal = new Vector3(x, 0, z).normalized;
                normals.Add(normal);
            }
        }

        for (int lon = 0; lon < segments; lon++)
        {
            int topCurrent = baseIndex + lon;
            int topNext = baseIndex + lon + 1;
            int bottomCurrent = baseIndex + segments + 1 + lon;
            int bottomNext = baseIndex + segments + 1 + lon + 1;

            triangles.Add(topCurrent);
            triangles.Add(topNext);
            triangles.Add(bottomCurrent);

            triangles.Add(bottomNext);
            triangles.Add(bottomCurrent);
            triangles.Add(topNext);
        }
    }
}