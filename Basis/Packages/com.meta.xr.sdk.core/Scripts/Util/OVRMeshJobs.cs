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
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;

/// <summary>
/// Defines a asynchronous jobs used for converting mesh data into Unity types. Used by <see cref="OVRMesh"/> to convert mesh data obtained from the Meta Quest runtime
/// into Unity data types.
/// </summary>
public class OVRMeshJobs
{
    /// <summary>
    /// Asynchronous job that transforms mesh vertices, normals, UVs, and bone weights into Unity space.
    /// </summary>
    public struct TransformToUnitySpaceJob : IJobParallelFor
    {
        public NativeArray<Vector3> Vertices;
        public NativeArray<Vector3> Normals;
        public NativeArray<Vector2> UV;
        public NativeArray<BoneWeight> BoneWeights;

        public NativeArray<OVRPlugin.Vector3f> MeshVerticesPosition;
        public NativeArray<OVRPlugin.Vector3f> MeshNormals;
        public NativeArray<OVRPlugin.Vector2f> MeshUV;

        public NativeArray<OVRPlugin.Vector4f> MeshBoneWeights;
        public NativeArray<OVRPlugin.Vector4s> MeshBoneIndices;

        public void Execute(int index)
        {
            // Lets try to flip the mesh & skeleton so that both point in such a way that when the hand data comes in it's correct?
            Vertices[index] = MeshVerticesPosition[index].FromFlippedXVector3f();
            Normals[index] = MeshNormals[index].FromFlippedXVector3f();

            UV[index] = new Vector2
            {
                x = MeshUV[index].x,
                y = -MeshUV[index].y
            };

            var currentBlendWeight = MeshBoneWeights[index];
            var currentBlendIndices = MeshBoneIndices[index];

            BoneWeights[index] = new BoneWeight
            {
                boneIndex0 = currentBlendIndices.x,
                weight0 = currentBlendWeight.x,

                boneIndex1 = currentBlendIndices.y,
                weight1 = currentBlendWeight.y,

                boneIndex2 = currentBlendIndices.z,
                weight2 = currentBlendWeight.z,

                boneIndex3 = currentBlendIndices.w,
                weight3 = currentBlendWeight.w,
            };
        }
    }

    /// <summary>
    /// Asynchronous job that rearranges mesh indicies into Unity required order for triangles.
    /// </summary>
    public struct TransformTrianglesJob : IJobParallelFor
    {
        public NativeArray<uint> Triangles;

        [ReadOnly]
        public NativeArray<short> MeshIndices;

        public int NumIndices;

        public void Execute(int index)
        {
            Triangles[index] = (uint)MeshIndices[NumIndices - index - 1];
        }
    }

    /// <summary>
    /// Helper struct that converts arrays to Unity NativeArray based on type.
    /// </summary>
    /// <typeparam name="T">Data type of the array.</typeparam>
    public unsafe struct NativeArrayHelper<T> : IDisposable where T : struct
    {
        public NativeArray<T> UnityNativeArray;
        private GCHandle _handle;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        private readonly AtomicSafetyHandle _atomicSafetyHandle;
#endif

        /// <summary>
        /// Constructor that performs the conversion from array to Unity NativeArray.
        /// </summary>
        /// <param name="ovrArray">Input array to convert.</param>
        /// <param name="length">Length of the input array.</param>
        public NativeArrayHelper(T[] ovrArray, int length)
        {
            _handle = GCHandle.Alloc(ovrArray, GCHandleType.Pinned);
            var ptr = _handle.AddrOfPinnedObject();
            UnityNativeArray = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<T>(
                (void*)ptr, length, Allocator.None);

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            _atomicSafetyHandle = AtomicSafetyHandle.Create();
            NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref UnityNativeArray, _atomicSafetyHandle);
#endif
        }

        /// <summary>
        /// Safely disposes of allocated memory used when converting.
        /// </summary>
        public void Dispose()
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.Release(_atomicSafetyHandle);
#endif
            _handle.Free();
        }
    }
}
