using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;

/// <summary>
/// The per frame half of <see cref="BasisAvatarProxy"/>: every limb's matrix for the room, in one flat
/// array that consumers index into rather than each keeping a copy.
///
/// ⚠️ A JOB VERSION CANNOT BE SCHEDULED FROM INSIDE THE RENDER PIPELINE. An earlier attempt scheduled
/// the gather from RenderPipelineManager.beginFrameRendering and crashed the editor:
///
///     InvalidOperationException: The previously scheduled job ZBinningJob writes to the
///     NativeArray`1[System.UInt32] ZBinningJob.bins. You must call JobHandle.Complete() on the job
///     ZBinningJob, before you can write to it safely.
///
/// ZBinningJob is URP's OWN light binning job — scheduling and completing there is a sync point in the
/// middle of somebody else's job graph, and the safety system is right to refuse it.
///
/// So the split is: <see cref="ScheduleBeforeRender"/> runs on Application.onBeforeRender at
/// BeforeRenderOrder int.MaxValue — after Basis has run its IK on the default-order handler, before URP
/// exists for the frame — and <see cref="Run"/> (beginFrameRendering) only joins and publishes. The poses
/// are still one sample at one instant for every consumer; the sample just happens a hair earlier, at the
/// last onBeforeRender slot instead of the first pipeline callback, with nothing writing bones in between.
/// A destroyed bone is skipped by the transform job and keeps its last matrix until the next rebuild,
/// exactly as the managed loop's null-skip did.
/// </summary>
public static class BasisAvatarProxyJobs
{
    private static Transform[] bones;
    private static float2[] shape;
    private static Matrix4x4[] matrices;
    private static int limbCount;

    private static TransformAccessArray access;
    private static NativeArray<Vector3> positions;
    private static NativeArray<float2> shapeNative;
    private static NativeArray<Matrix4x4> outMatrices;
    private static JobHandle handle;
    private static bool scheduled;
    private static bool hooked;

    [BurstCompile]
    private struct GatherJob : IJobParallelForTransform
    {
        public NativeArray<Vector3> Positions;

        public void Execute(int index, TransformAccess transform)
        {
            if (!transform.isValid) { return; }
            Positions[index] = transform.position;
        }
    }

    [BurstCompile]
    private struct BuildJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<Vector3> Positions;
        [ReadOnly] public NativeArray<float2> Shape;
        public NativeArray<Matrix4x4> Matrices;

        public void Execute(int index)
        {
            float2 s = Shape[index];
            Matrices[index] = Build(Positions[index * 2], Positions[index * 2 + 1], s.x, s.y);
        }
    }

    public static bool IsAllocated => matrices != null && limbCount > 0;

    /// <summary>
    /// The matrix for a limb, by its global index. Consumers hold an offset into this rather than their
    /// own copy, so nothing is duplicated per tracer and nothing is copied per frame.
    /// </summary>
    public static Matrix4x4 MatrixAt(int index)
    {
        if (matrices == null || index < 0 || index >= limbCount) { return Matrix4x4.identity; }
        return matrices[index];
    }

    /// <summary>
    /// Rebuilds the flat arrays from the given limbs. Called when an avatar joins or leaves, never per
    /// frame - the transforms an avatar's capsules read do not change while it is standing there.
    /// </summary>
    public static void Rebuild(List<BasisAvatarProxy.ResolvedLimb> limbs)
    {
        CompleteScheduled();
        DisposeNative();

        limbCount = limbs != null ? limbs.Count : 0;
        if (limbCount == 0)
        {
            bones = null;
            shape = null;
            matrices = null;
            return;
        }

        bones = new Transform[limbCount * 2];
        shape = new float2[limbCount];
        matrices = new Matrix4x4[limbCount];

        bool everyBoneAlive = true;
        for (int index = 0; index < limbCount; index++)
        {
            BasisAvatarProxy.ResolvedLimb limb = limbs[index];
            // A null here would desynchronise every index after it, so a dead bone keeps its slot and is
            // caught by the radius being zero instead.
            Transform from = limb.From != null ? limb.From : limb.To;
            bones[index * 2] = from;
            bones[index * 2 + 1] = limb.To != null ? limb.To : from;
            shape[index] = new float2(limb.IsValid ? limb.Radius : 0f, limb.Extend);
            matrices[index] = Matrix4x4.identity;
            if (from == null) { everyBoneAlive = false; }
        }

        if (!everyBoneAlive) { return; }

        access = new TransformAccessArray(bones);
        positions = new NativeArray<Vector3>(limbCount * 2, Allocator.Persistent);
        for (int index = 0; index < limbCount * 2; index++) { positions[index] = bones[index].position; }
        shapeNative = new NativeArray<float2>(shape, Allocator.Persistent);
        outMatrices = new NativeArray<Matrix4x4>(limbCount, Allocator.Persistent);
        for (int index = 0; index < limbCount; index++) { outMatrices[index] = Matrix4x4.identity; }
        EnsureHook();
    }

    private static void EnsureHook()
    {
        if (hooked) { return; }
        hooked = true;
        Application.onBeforeRender += ScheduleBeforeRender;
    }

    [BeforeRenderOrder(int.MaxValue)]
    private static void ScheduleBeforeRender()
    {
        if (scheduled || limbCount == 0 || !access.isCreated) { return; }
        JobHandle gather = new GatherJob { Positions = positions }.Schedule(access);
        handle = new BuildJob { Positions = positions, Shape = shapeNative, Matrices = outMatrices }.Schedule(limbCount, 16, gather);
        scheduled = true;
        JobHandle.ScheduleBatchedJobs();
    }

    private static void CompleteScheduled()
    {
        if (!scheduled) { return; }
        handle.Complete();
        scheduled = false;
    }

    /// <summary>
    /// Publishes this frame's matrices. Joins the pre-render job when one is in flight; falls back to the
    /// managed read loop when it is not (the frame a layout rebuild landed on, or when nothing hooked yet).
    /// </summary>
    public static void Run()
    {
        if (matrices == null || limbCount == 0) { return; }

        if (scheduled)
        {
            handle.Complete();
            scheduled = false;
            outMatrices.CopyTo(matrices);
            return;
        }

        for (int index = 0; index < limbCount; index++)
        {
            Transform from = bones[index * 2];
            Transform to = bones[index * 2 + 1];
            if (from == null || to == null) { continue; }
            matrices[index] = Build(from.position, to.position, shape[index].x, shape[index].y);
        }
    }

    /// <summary>
    /// Where the shared unit capsule has to be put to become this limb. The capsule is authored along +Y
    /// with radius 1 and its ends at y = +/-2, so the scale is (radius, half length, radius) and the
    /// second column is the bone direction.
    /// </summary>
    public static Matrix4x4 Build(Vector3 start, Vector3 end, float radius, float extend)
    {
        Vector3 axis = end - start;
        float length = axis.magnitude;

        if (length <= 0.0001f)
        {
            // A collapsed joint still has a body part sitting on it, so it stays a ball rather than
            // vanishing - which is what stops a degenerate rig punching holes in the occlusion.
            Matrix4x4 ball = new Matrix4x4();
            ball.SetColumn(0, new Vector4(radius, 0f, 0f, 0f));
            ball.SetColumn(1, new Vector4(0f, radius, 0f, 0f));
            ball.SetColumn(2, new Vector4(0f, 0f, radius, 0f));
            ball.SetColumn(3, new Vector4(start.x, start.y, start.z, 1f));
            return ball;
        }

        Vector3 direction = axis / length;
        if (extend > 0f)
        {
            end += direction * extend;
            length += extend;
        }

        // The capsule is radially symmetric, so any orthonormal basis with +Y down the bone is the right
        // one. Built directly rather than through FromToRotation, which needs its own antiparallel case.
        Vector3 reference = Mathf.Abs(direction.y) < 0.99f ? Vector3.up : Vector3.right;
        Vector3 x = Vector3.Normalize(Vector3.Cross(reference, direction));
        Vector3 z = Vector3.Cross(direction, x);

        float halfLength = length * 0.5f;
        Vector3 centre = (start + end) * 0.5f;

        Matrix4x4 matrix = new Matrix4x4();
        matrix.SetColumn(0, new Vector4(x.x * radius, x.y * radius, x.z * radius, 0f));
        matrix.SetColumn(1, new Vector4(direction.x * halfLength, direction.y * halfLength, direction.z * halfLength, 0f));
        matrix.SetColumn(2, new Vector4(z.x * radius, z.y * radius, z.z * radius, 0f));
        matrix.SetColumn(3, new Vector4(centre.x, centre.y, centre.z, 1f));
        return matrix;
    }

    private static void DisposeNative()
    {
        if (access.isCreated) { access.Dispose(); }
        if (positions.IsCreated) { positions.Dispose(); }
        if (shapeNative.IsCreated) { shapeNative.Dispose(); }
        if (outMatrices.IsCreated) { outMatrices.Dispose(); }
    }

    public static void Release()
    {
        CompleteScheduled();
        DisposeNative();
        bones = null;
        shape = null;
        matrices = null;
        limbCount = 0;
    }
}
