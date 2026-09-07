using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

/// <summary>
/// Exterior-visibility culling for the far LOD: drops triangles that cannot be seen from any
/// outside direction (body skin under clothing, jacket linings, inner mouths, inner hair
/// cards) BEFORE decimation, so the whole triangle budget goes to surfaces that exist.
///
/// Test: from each triangle centroid, a fan of rays across its normal hemisphere tries to
/// escape to open space against a collider of the full snapshot. Any escape = visible.
/// Deliberately fail-open — a kept hidden triangle wastes budget, a culled visible one is a
/// hole: grazing directions are included (gap visibility, e.g. armpits), and the one-ring of
/// neighbors around every visible triangle is kept to seal cull boundaries under cloth hems.
///
/// Alpha-cutout occluders (fishnet, mesh fabrics) occlude as solid geometry here, so surfaces
/// only visible through texture holes may be culled — invisible at far LOD ranges.
/// </summary>
public static class BasisFarLodVisibilityCuller
{
    private const int OcclusionLayer = 2; // Ignore Raycast: invisible to default queries, targetable by mask
    private const float SkipBudgetHeadroom = 1.25f;

    /// <summary>
    /// Removes exterior-invisible triangles from <paramref name="indices"/> in place.
    /// Positions are root-space; the collider is instanced at the root's world TRS so the
    /// physics queries run where the (islanded) avatar currently stands.
    /// <paramref name="vertexHidden"/> gets 1 for vertices that belong to no visible triangle —
    /// the fail-open remnants kept to seal hems. The texture pass uses it to skip sampling
    /// those texels and to shrink their charts.
    /// Returns the number of triangles removed.
    /// </summary>
    public static int RemoveHiddenTriangles(List<Vector3> positions, List<int> indices, Transform root, int targetTriangles, out byte[] vertexHidden)
    {
        vertexHidden = null;
        int triangleCount = indices.Count / 3;
        if (triangleCount <= Mathf.RoundToInt(targetTriangles * SkipBudgetHeadroom))
        {
            return 0;
        }

        Bounds bounds = new Bounds(positions[0], Vector3.zero);
        for (int i = 1; i < positions.Count; i++)
        {
            bounds.Encapsulate(positions[i]);
        }
        float radius = Mathf.Max(bounds.extents.magnitude, 0.05f);
        float rayLength = radius * 3f;
        float bias = Mathf.Max(0.002f, radius * 0.0015f);
        int layerMask = 1 << OcclusionLayer;

        // Normal-hemisphere fan (tangent space, z = along the face normal): the straight-out
        // ray, a mid ring, and a near-grazing ring for surfaces peeking through gaps.
        Vector3[] fan = BuildFan();

        GameObject colliderObject = null;
        Mesh colliderMesh = null;
        try
        {
            colliderMesh = new Mesh
            {
                hideFlags = HideFlags.HideAndDontSave,
                indexFormat = UnityEngine.Rendering.IndexFormat.UInt32,
            };
            colliderMesh.SetVertices(positions);
            colliderMesh.SetTriangles(indices, 0);
            colliderObject = new GameObject("FarLodVisibilityCollider") { hideFlags = HideFlags.HideAndDontSave, layer = OcclusionLayer };
            colliderObject.transform.SetPositionAndRotation(root.position, root.rotation);
            colliderObject.transform.localScale = root.lossyScale;
            colliderObject.AddComponent<MeshCollider>().sharedMesh = colliderMesh;
            Physics.SyncTransforms();

            Matrix4x4 rootToWorld = root.localToWorldMatrix;
            Quaternion rootRotation = root.rotation;

            bool[] triangleVisible = new bool[triangleCount];
            bool[] vertexNearVisible = new bool[positions.Count];

            // The pre-decimation soup runs to hundreds of thousands of triangles, and one
            // synchronous Physics.Raycast per (triangle x fan direction) serialised millions of
            // queries on the main thread. RaycastCommand batches fan them across the worker
            // pool instead, chunked to bound the command/hit buffers. Two phases keep most of
            // the serial early-break: the straight-out ray settles the bulk of the visible
            // surface for one ray each, and only the survivors pay for the remaining fan.
            // Escape semantics flip from !Physics.Raycast to colliderEntityId == EntityId.None.
            // A miss writes the zero id, but the implicit int conversion means comparing against
            // the literal 0 is NEVER true, which reads every ray as a hit and culls the whole mesh.
            Vector3[] triOrigin = new Vector3[triangleCount];
            Vector3[] triTangent = new Vector3[triangleCount];
            Vector3[] triBitangent = new Vector3[triangleCount];
            Vector3[] triNormal = new Vector3[triangleCount];
            bool[] triDegenerate = new bool[triangleCount];
            for (int t = 0; t < triangleCount; t++)
            {
                Vector3 p0 = positions[indices[t * 3]];
                Vector3 p1 = positions[indices[t * 3 + 1]];
                Vector3 p2 = positions[indices[t * 3 + 2]];
                Vector3 normal = Vector3.Cross(p1 - p0, p2 - p0);
                float length = normal.magnitude;
                if (length < 1e-12f)
                {
                    triDegenerate[t] = true; // degenerate — the simplifier drops it anyway
                    continue;
                }
                normal /= length;

                Vector3 centroidWorld = rootToWorld.MultiplyPoint3x4((p0 + p1 + p2) * (1f / 3f));
                Vector3 normalWorld = (rootRotation * normal).normalized;
                Vector3 tangent = Vector3.Cross(normalWorld, Mathf.Abs(normalWorld.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
                triOrigin[t] = centroidWorld + normalWorld * bias;
                triTangent[t] = tangent;
                triBitangent[t] = Vector3.Cross(normalWorld, tangent);
                triNormal[t] = normalWorld;
            }

            void MarkVisible(int t)
            {
                triangleVisible[t] = true;
                vertexNearVisible[indices[t * 3]] = true;
                vertexNearVisible[indices[t * 3 + 1]] = true;
                vertexNearVisible[indices[t * 3 + 2]] = true;
            }

            QueryParameters query = new QueryParameters(layerMask, false, QueryTriggerInteraction.UseGlobal, false);

            // Phase 1: the straight-out ray (fan[0] is the +normal direction) for every triangle.
            const int ChunkSize = 65536;
            var commands = new NativeArray<RaycastCommand>(ChunkSize, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var hits = new NativeArray<RaycastHit>(ChunkSize, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            try
            {
                for (int chunkStart = 0; chunkStart < triangleCount; chunkStart += ChunkSize)
                {
                    int count = Mathf.Min(ChunkSize, triangleCount - chunkStart);
                    for (int c = 0; c < count; c++)
                    {
                        int t = chunkStart + c;
                        commands[c] = triDegenerate[t]
                            ? new RaycastCommand(Vector3.zero, Vector3.up, query, 0f)
                            : new RaycastCommand(triOrigin[t], triNormal[t], query, rayLength);
                    }
                    RaycastCommand.ScheduleBatch(commands.GetSubArray(0, count), hits.GetSubArray(0, count), 128).Complete();
                    for (int c = 0; c < count; c++)
                    {
                        int t = chunkStart + c;
                        if (!triDegenerate[t] && hits[c].colliderEntityId == EntityId.None)
                        {
                            MarkVisible(t);
                        }
                    }
                }

                // Phase 2: the remaining fan directions, only for triangles still unresolved.
                List<int> pending = new List<int>();
                for (int t = 0; t < triangleCount; t++)
                {
                    if (!triDegenerate[t] && !triangleVisible[t])
                    {
                        pending.Add(t);
                    }
                }

                int raysPerTriangle = fan.Length - 1;
                if (raysPerTriangle > 0 && pending.Count > 0)
                {
                    int trianglesPerChunk = Mathf.Max(1, ChunkSize / raysPerTriangle);
                    for (int chunkStart = 0; chunkStart < pending.Count; chunkStart += trianglesPerChunk)
                    {
                        int chunkTriangles = Mathf.Min(trianglesPerChunk, pending.Count - chunkStart);
                        int rayCount = chunkTriangles * raysPerTriangle;
                        for (int c = 0; c < chunkTriangles; c++)
                        {
                            int t = pending[chunkStart + c];
                            Vector3 origin = triOrigin[t], tangent = triTangent[t], bitangent = triBitangent[t], normalWorld = triNormal[t];
                            int baseIndex = c * raysPerTriangle;
                            for (int r = 1; r < fan.Length; r++)
                            {
                                Vector3 direction = tangent * fan[r].x + bitangent * fan[r].y + normalWorld * fan[r].z;
                                commands[baseIndex + r - 1] = new RaycastCommand(origin, direction, query, rayLength);
                            }
                        }
                        RaycastCommand.ScheduleBatch(commands.GetSubArray(0, rayCount), hits.GetSubArray(0, rayCount), 128).Complete();
                        for (int c = 0; c < chunkTriangles; c++)
                        {
                            int baseIndex = c * raysPerTriangle;
                            for (int r = 0; r < raysPerTriangle; r++)
                            {
                                if (hits[baseIndex + r].colliderEntityId == EntityId.None)
                                {
                                    MarkVisible(pending[chunkStart + c]);
                                    break;
                                }
                            }
                        }
                    }
                }
            }
            finally
            {
                commands.Dispose();
                hits.Dispose();
            }

            // Keep pass: visible triangles plus anything sharing a vertex with one — seals the
            // boundary so a hem edge never becomes a crack.
            List<int> kept = new List<int>(indices.Count);
            int removed = 0;
            for (int t = 0; t < triangleCount; t++)
            {
                int i0 = indices[t * 3];
                int i1 = indices[t * 3 + 1];
                int i2 = indices[t * 3 + 2];
                if (triangleVisible[t] || vertexNearVisible[i0] || vertexNearVisible[i1] || vertexNearVisible[i2])
                {
                    kept.Add(i0);
                    kept.Add(i1);
                    kept.Add(i2);
                }
                else
                {
                    removed++;
                }
            }

            // Nothing survived: the exterior of a mesh is visible by construction, so this is a
            // broken occlusion probe, not a fully hidden avatar. Fail open on the whole mesh
            // rather than hand the rest of the pipeline an empty soup.
            if (kept.Count == 0)
            {
                Debug.LogWarning($"[FarAvatar] Visibility cull found no exterior surface across {triangleCount} triangles and was discarded. Check that the bake collider is reachable on layer {OcclusionLayer}.");
                return 0;
            }

            if (removed > 0)
            {
                indices.Clear();
                indices.AddRange(kept);
            }
            vertexHidden = new byte[positions.Count];
            for (int i = 0; i < vertexHidden.Length; i++)
            {
                vertexHidden[i] = vertexNearVisible[i] ? (byte)0 : (byte)1;
            }
            return removed;
        }
        finally
        {
            if (colliderObject != null)
            {
                Object.DestroyImmediate(colliderObject);
            }
            if (colliderMesh != null)
            {
                Object.DestroyImmediate(colliderMesh);
            }
        }
    }

    private static Vector3[] BuildFan()
    {
        List<Vector3> fan = new List<Vector3>(13)
        {
            new Vector3(0f, 0f, 1f),
        };
        for (int i = 0; i < 8; i++)
        {
            float yaw = i * Mathf.PI * 2f / 8f;
            const float mid = 40f * Mathf.Deg2Rad;
            fan.Add(new Vector3(Mathf.Cos(yaw) * Mathf.Sin(mid), Mathf.Sin(yaw) * Mathf.Sin(mid), Mathf.Cos(mid)));
        }
        for (int i = 0; i < 4; i++)
        {
            float yaw = (i + 0.5f) * Mathf.PI * 2f / 4f;
            const float grazing = 78f * Mathf.Deg2Rad;
            fan.Add(new Vector3(Mathf.Cos(yaw) * Mathf.Sin(grazing), Mathf.Sin(yaw) * Mathf.Sin(grazing), Mathf.Cos(grazing)));
        }
        return fan.ToArray();
    }
}
