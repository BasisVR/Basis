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
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;
using static OVRPlugin;

/// <summary>
/// Represents the Triangle Mesh component of an <see cref="OVRAnchor"/>.
/// </summary>
/// <remarks>
/// This component can be accessed from an <see cref="OVRAnchor"/> that supports it by calling
/// <see cref="OVRAnchor.GetComponent{T}"/> from the anchor.
///
/// The triangle mesh component is part of the Meta Quest Scene Model. Read more at
/// [Scene Overview](https://developer.oculus.com/documentation/unity/unity-scene-overview/).
/// </remarks>
/// <seealso cref="TryGetCounts"/>
/// <seealso cref="TryGetMeshRawUntransformed"/>
/// <seealso cref="TryGetMesh"/>
public readonly partial struct OVRTriangleMesh
{
    /// <summary>
    /// Gets the number of vertices and triangles in the mesh.
    /// </summary>
    /// <remarks>
    /// Use this method to get the required sizes of the vertex and triangle index buffers. The length of the `indices`
    /// array passed to <see cref="TryGetMesh"/> and <seealso cref="TryGetMeshRawUntransformed"/> should be three times
    /// <paramref name="triangleCount"/>.
    ///
    /// This method is thread-safe.
    /// </remarks>
    /// <param name="vertexCount">The number of vertices in the mesh.</param>
    /// <param name="triangleCount">The number of triangles in the mesh. There are three times as many indices.</param>
    /// <returns>Returns true if the counts were retrieved; otherwise, false.</returns>
    public bool TryGetCounts(out int vertexCount, out int triangleCount)
        => GetSpaceTriangleMeshCounts(Handle, out vertexCount, out triangleCount);

    /// <summary>
    /// Gets the raw, untransformed triangle mesh.
    /// </summary>
    /// <remarks>
    /// ## Thread safety
    /// This method is thread-safe.
    ///
    /// ## Memory ownership
    /// The caller owns the memory of the input arrays and is responsible for allocating them to the appropriate size
    /// before passing them to this method. Use <see cref="TryGetCounts"/> to determine the required size of each array.
    /// Note that <paramref name="indices"/> should be three times the number of triangles (`triangleCount`) indicated
    /// by <see cref="TryGetCounts"/>.
    ///
    /// ## Coordinate space
    /// The mesh data provided by this method must be transformed into the appropriate coordinate space before being
    /// used with a `UnityEngine.Mesh`. Use <see cref="TryGetMesh"/> or <see cref="ScheduleGetMeshJob"/> to get mesh
    /// data in the correct coordinate space. This method is typically for advanced use cases where you want to perform
    /// the conversion at a later time, or combine it with your own jobs.
    ///
    /// The <paramref name="positions"/> are provided in the right-handed coordinate
    /// space defined by OpenXR, with X to the right, Y up, and Z backward. <paramref name="indices"/> is an array of
    /// index triplets which define the triangles in counter-clockwise order.
    ///
    /// To convert to the coordinate space used by a Scene anchor in Unity's coordinate system, you must
    /// - Negate each vertex's X coordinate
    /// - Reverse the triangle winding by swapping each index triplet (a, b, c) => (a, c, b)
    /// </remarks>
    /// <param name="positions">The vertex positions of the mesh.</param>
    /// <param name="indices">The triangle indices of the mesh.</param>
    /// <returns>Returns true if the mesh data was retrieved; otherwise, false.</returns>
    public bool TryGetMeshRawUntransformed(NativeArray<Vector3> positions, NativeArray<int> indices)
        => GetSpaceTriangleMesh(Handle, positions, indices);

    /// <summary>
    /// Gets the triangle mesh.
    /// </summary>
    /// <remarks>
    /// The caller owns the memory of the input arrays and is responsible for allocating them to the appropriate size
    /// before passing them to this method. Use <see cref="TryGetCounts"/> to determine the required size of each array.
    /// Note that <paramref name="indices"/> should be three times the number of triangles (`triangleCount`) indicated
    /// by <see cref="TryGetCounts"/>.
    ///
    /// This method is thread-safe.
    /// </remarks>
    /// <param name="positions">The vertex positions of the mesh.</param>
    /// <param name="indices">The triangle indices of the mesh.</param>
    /// <returns>Returns true if the mesh data was retrieved; otherwise, false.</returns>
    public bool TryGetMesh(NativeArray<Vector3> positions, NativeArray<int> indices)
    {
        if (!TryGetMeshRawUntransformed(positions, indices)) return false;

        for (var i = 0; i < positions.Length; i++)
        {
            var p = positions[i];
            // Necessary due to the coordinate space difference between OpenXR (right-handed) and Unity (left-handed)
            positions[i] = new Vector3(-p.x, p.y, p.z);
        }

        var triangles = indices.Reinterpret<Triangle>(
            expectedTypeSize: sizeof(int));
        for (var i = 0; i < triangles.Length; i++)
        {
            var triangle = triangles[i];
            triangles[i] = new Triangle
            {
                A = triangle.A,
                B = triangle.C,
                C = triangle.B
            };
        }

        return true;
    }

    /// <summary>
    /// Schedules a job to get an anchor's triangle mesh.
    /// </summary>
    /// <remarks>
    /// This schedules jobs with the Unity Job system to retrieve the mesh data and then perform the necessary
    /// conversion to Unity's coordinate space (see <see cref="TryGetMeshRawUntransformed"/>).
    ///
    /// The caller owns the memory of the input arrays and is responsible for allocating them to the appropriate size
    /// before passing them to this method. Use <see cref="TryGetCounts"/> to determine the required size of each array.
    /// Note that <paramref name="indices"/> should be three times the number of triangles (`triangleCount`) indicated
    /// by <see cref="TryGetCounts"/>.
    ///
    /// If the triangle mesh cannot be retrieved, all <paramref name="indices"/> will be set to zero. Use this to check
    /// for success after the job completes. For example, if the first three indices are zero, then the mesh is not
    /// valid.
    /// </remarks>
    /// <param name="positions">The vertex positions of the triangle mesh.</param>
    /// <param name="indices">The triangle indices of the triangle mesh.</param>
    /// <param name="dependencies">(Optional) A job on which the new jobs will depend.</param>
    /// <returns>Returns the handle associated with the new job.</returns>
    public JobHandle ScheduleGetMeshJob(NativeArray<Vector3> positions, NativeArray<int> indices,
        JobHandle dependencies = default)
    {
        var getMeshJob = new GetMeshJob
        {
            Positions = positions,
            Indices = indices,
            Space = Handle
        }.Schedule(dependencies);

        var triangles =
            indices.Reinterpret<Triangle>(expectedTypeSize: sizeof(int));

        return JobHandle.CombineDependencies(
            new NegateXJob
            {
                Positions = positions
            }.Schedule(positions.Length, 32, getMeshJob),
            new FlipTriangleWindingJob
            {
                Triangles = triangles
            }.Schedule(triangles.Length, 32, getMeshJob));
    }

    private struct GetMeshJob : IJob
    {
        public ulong Space;

        public NativeArray<Vector3> Positions;

        public NativeArray<int> Indices;

        public unsafe void Execute()
        {
            if (!GetSpaceTriangleMesh(Space, Positions, Indices))
            {
                UnsafeUtility.MemSet(Indices.GetUnsafePtr(), 0, Indices.Length * sizeof(int));
            }
        }
    }

    private struct Triangle
    {
        public int A, B, C;
    }

    private struct FlipTriangleWindingJob : IJobParallelFor
    {
        public NativeArray<Triangle> Triangles;

        public void Execute(int index)
        {
            var triangle = Triangles[index];
            Triangles[index] = new Triangle
            {
                A = triangle.A,
                B = triangle.C,
                C = triangle.B
            };
        }
    }

    // Necessary due to the coordinate space difference between OpenXR (right-handed) and Unity (left-handed)
    private struct NegateXJob : IJobParallelFor
    {
        public NativeArray<Vector3> Positions;

        public void Execute(int index)
        {
            var p = Positions[index];
            Positions[index] = new Vector3(-p.x, p.y, p.z);
        }
    }
}
