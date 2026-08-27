using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;

/// <summary>
/// The per frame half of <see cref="BasisAvatarProxy"/>, off the main thread.
///
/// Every limb needs two bone positions, and a job can only be handed one transform per index, so this is
/// a gather then a compute: one IJobParallelForTransform reads every bone the room's capsules hang on
/// into a flat array, and one Burst job pairs them up and builds the matrices. Splitting it that way is
/// also what lets the second job be Burst compiled at all - it touches no managed object, only two native
/// arrays of numbers.
///
/// The layout is rebuilt only when somebody joins or leaves. A TransformAccessArray rebuild is not cheap
/// and doing it per frame would cost more than the job saves; avatar churn is rare and pose changes are
/// constant, which is exactly the split this arrangement wants.
/// </summary>
public static class BasisAvatarProxyJobs
{
    private static TransformAccessArray bones;
    private static NativeArray<float3> positions;
    private static NativeArray<float2> shape;
    private static NativeArray<float4x4> matrices;
    private static int limbCount;
    private static bool allocated;

    /// <summary>How many limbs the shared arrays currently hold. For tests and diagnostics.</summary>
    public static int LimbCount => limbCount;

    public static bool IsAllocated => allocated;

    /// <summary>
    /// The matrix for a limb, by its global index. Consumers hold an offset into this rather than their
    /// own copy, so nothing is duplicated per tracer and nothing is copied per frame.
    /// </summary>
    public static Matrix4x4 MatrixAt(int index)
    {
        if (!allocated || index < 0 || index >= limbCount) { return Matrix4x4.identity; }
        return matrices[index];
    }

    /// <summary>
    /// Rebuilds the flat arrays from the given limbs. Called when an avatar joins or leaves, never per
    /// frame - the transforms an avatar's capsules read do not change while it is standing there.
    /// </summary>
    public static void Rebuild(System.Collections.Generic.List<BasisAvatarProxy.ResolvedLimb> limbs)
    {
        Release();
        limbCount = limbs != null ? limbs.Count : 0;
        if (limbCount == 0) { return; }

        Transform[] flat = new Transform[limbCount * 2];
        shape = new NativeArray<float2>(limbCount, Allocator.Persistent);
        for (int index = 0; index < limbCount; index++)
        {
            BasisAvatarProxy.ResolvedLimb limb = limbs[index];
            // A null here would desynchronise every index after it, so a dead bone keeps its slot and is
            // caught by the radius being zero instead.
            flat[index * 2] = limb.From;
            flat[index * 2 + 1] = limb.To != null ? limb.To : limb.From;
            shape[index] = new float2(limb.IsValid ? limb.Radius : 0f, limb.Extend);
        }

        bones = new TransformAccessArray(flat);
        positions = new NativeArray<float3>(limbCount * 2, Allocator.Persistent);
        matrices = new NativeArray<float4x4>(limbCount, Allocator.Persistent);
        allocated = true;
    }

    /// <summary>Reads every bone and rebuilds every matrix. One schedule for the whole room.</summary>
    public static void Run()
    {
        if (!allocated || limbCount == 0) { return; }

        JobHandle read = new ReadBonePositions { Positions = positions }.Schedule(bones);
        new BuildLimbMatrices
        {
            Positions = positions,
            Shape = shape,
            Matrices = matrices
        }.Schedule(limbCount, 16, read).Complete();
    }

    public static void Release()
    {
        if (bones.isCreated) { bones.Dispose(); }
        if (positions.IsCreated) { positions.Dispose(); }
        if (shape.IsCreated) { shape.Dispose(); }
        if (matrices.IsCreated) { matrices.Dispose(); }
        limbCount = 0;
        allocated = false;
    }

    [BurstCompile]
    private struct ReadBonePositions : IJobParallelForTransform
    {
        [WriteOnly] public NativeArray<float3> Positions;

        public void Execute(int index, TransformAccess transform)
        {
            Positions[index] = transform.position;
        }
    }

    [BurstCompile]
    private struct BuildLimbMatrices : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> Positions;
        [ReadOnly] public NativeArray<float2> Shape;
        [WriteOnly] public NativeArray<float4x4> Matrices;

        public void Execute(int index)
        {
            float3 start = Positions[index * 2];
            float3 end = Positions[index * 2 + 1];
            float radius = Shape[index].x;
            float extend = Shape[index].y;

            float3 axis = end - start;
            float length = math.length(axis);

            if (length <= 0.0001f)
            {
                // A collapsed joint still has a body part sitting on it, so it stays a ball rather than
                // vanishing - which is what stops a degenerate rig punching holes in the occlusion.
                Matrices[index] = float4x4.TRS(start, quaternion.identity, radius);
                return;
            }

            float3 direction = axis / length;
            if (extend > 0f)
            {
                end += direction * extend;
                length += extend;
            }

            // The capsule is radially symmetric, so any orthonormal basis with +Y down the bone is the
            // right one. Built directly rather than through FromToRotation, which is not Burst compatible
            // and would need its own antiparallel special case anyway.
            float3 reference = math.abs(direction.y) < 0.99f ? new float3(0f, 1f, 0f) : new float3(1f, 0f, 0f);
            float3 x = math.normalize(math.cross(reference, direction));
            float3 z = math.cross(direction, x);

            float halfLength = length * 0.5f;
            float3 centre = (start + end) * 0.5f;

            Matrices[index] = new float4x4(
                new float4(x * radius, 0f),
                new float4(direction * halfLength, 0f),
                new float4(z * radius, 0f),
                new float4(centre, 1f));
        }
    }
}
